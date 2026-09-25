// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0) and alt-biome planting (0.8.1), and on 2026-09-24 for world export and import (0.9.0).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace BetterContinents;

public partial class BetterContinents
{
    private static string? LastConnectionError = null;

    // Dealing with settings, synchronization of them in multiplayer
    [HarmonyPatch]
    private class ZRpcPatch
    {
        // When the world is set on the server (applies to single player as well), we should select the correct loaded settings
        private static void Prefix(string name, ref Action<ZRpc, ZPackage> f)
        {
            if (ZNet.instance.IsServer() && name == "PeerInfo")
            {
                var RPC_PeerInfo = f;
                f = new Action<ZRpc, ZPackage>((rpc, pkg) => ZNetPatch.RPC_PeerInfoRedirect(rpc, pkg, () => RPC_PeerInfo(rpc, pkg)));
                Log($"Redirecting RPC_PeerInfo to allow BC config download");
            }
        }

        private static MethodBase TargetMethod()
        {
            return typeof(ZRpc)
                .GetMethods()
                .Where(m => m.Name == nameof(ZRpc.Register))
                .First(m => m.GetParameters().Length == 2
                                     && m.GetGenericArguments().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(string)
                                     && m.GetParameters()[1].ParameterType ==
                                     typeof(Action<,>).MakeGenericType(typeof(ZRpc), m.GetGenericArguments()[0]))
                .MakeGenericMethod(typeof(ZPackage));
        }
    }

    // Dealing with settings, synchronization of them in multiplayer
    [HarmonyPatch(typeof(ZNet))]
    public class ZNetPatch
    {
        // When the world is set on the server (applies to single player as well), we should select the correct loaded settings
        [HarmonyPrefix, HarmonyPatch(nameof(ZNet.SetServer))]
        private static void SetServerPrefix(bool server, World world)
        {
            // A new session: no connection of the last one is current any more, and no server has sent its export
            // settings yet (LiveConfig).
            ClientInfo.Clear();
            LiveConfig.ClearServerValues();
            if (server)
            {
                var settings = ResolveWorldSettingsPath(world);
                Settings = BetterContinentsSettings.LoadFromSource(settings.path, settings.source);
                Settings.Dump();
            }
            else
            {
                Settings = BetterContinentsSettings.Disabled();
            }
            DynamicPatch();
        }

        // Settings live inside the world folder for 1.0 worlds (GetWorldBCFile). Every other location we
        // look at is a one-way import from a layout or a storage root Better Continents used to use, never
        // a second supported home: whatever we find here gets written back into the world folder by the
        // next save (WorldPatch.SaveWorldFWLDataPostfix).
        //
        // The search has to span storage roots, not just the world's current one, because Valheim moves a
        // world between roots without taking anything of ours with it:
        //
        //  - SaveSystem.CheckMove (SaveSystem.cs:623-646) is the very first thing World.SaveWorldFWLData
        //    does (World.cs:220). For a Legacy world it rewrites m_fileSource to Local or Cloud in place
        //    and renames the old flat .fwl/.db aside, so from that instant the world reports a root that
        //    has never held a settings file. It can fire before we have read anything at all:
        //    FejdStartup.OnServerOptionsDone calls SaveWorldFWLData on the selected world straight from
        //    the main menu (FejdStartup.cs:1582) and then rebuilds m_world from a fresh scan
        //    (FejdStartup.cs:1583 -> UpdateWorldList:1309 -> SetSelectedWorld:1447), so by the time the
        //    world is started it claims to be a Local chunked world and the settings are still sitting in
        //    the Legacy root.
        //
        //  - SaveSystem.MoveSource (SaveSystem.cs:465-524), behind the "Manage Saves" Move button, copies
        //    only SaveFile.AllPaths, and our file is never in there. FileHelpers.GetFiles returns only
        //    "_main.*" and "*.chunk" out of a world folder (assembly_utils.decompiled.cs:4236 for cloud,
        //    :4269 for local), and a flat sidecar beside the world is dropped by the IsWorldSaveExtension
        //    filter in SaveCollection.Reload (SaveCollection.cs:125).
        //
        // Both leave the settings in the root the world came from while the world itself has moved on, so
        // probing only world.m_fileSource loses them for good.
        private static (string path, FileHelpers.FileSource source) ResolveWorldSettingsPath(World world)
        {
            var current = world.m_fileSource;
            var inFolder = GetWorldBCFile(world.m_worldName, current);
            if (FileHelpers.Exists(inFolder, current))
                return (inFolder, current);

            var tried = new HashSet<string> { inFolder };
            foreach (var source in SettingsSearchRoots(current))
            {
                // The flat pre-1.0 layout: World.GetMetaPath(FileSource) is "<root>/<world name>.fwl"
                // (World.cs:121-124), and the settings sat beside it under our own extension.
                var flat = GetBCFile(SaveSystem.GetWorldsSaveRootPath(source) + "/" + world.m_worldName + ".fwl");
                if (tried.Add(flat) && FileHelpers.Exists(flat, source))
                {
                    Log($"Importing settings from the older location {flat}; they will move into the world folder on the next save.");
                    return (flat, source);
                }

                // The current layout, but under a root the world has since been moved out of.
                var stranded = GetWorldBCFile(world.m_worldName, source);
                if (tried.Add(stranded) && FileHelpers.Exists(stranded, source))
                {
                    Log($"Importing settings left behind at {stranded}; they will move into the world folder on the next save.");
                    return (stranded, source);
                }
            }

            return (inFolder, current);
        }

        // Roots to sweep after the world's own, most likely first. Cloud is only worth mounting when there
        // is cloud storage to mount: with it disabled, Utils.GetSaveDataPath maps the cloud root onto the
        // same "<save data>/worlds" the Legacy probe already covered
        // (assembly_utils.decompiled.cs:11685-11695 with SaveSystem.cs:907-910), and the duplicate path is
        // skipped anyway.
        private static IEnumerable<FileHelpers.FileSource> SettingsSearchRoots(FileHelpers.FileSource current)
        {
            yield return current;
            if (current != FileHelpers.FileSource.Legacy)
                yield return FileHelpers.FileSource.Legacy;
            if (current != FileHelpers.FileSource.Local)
                yield return FileHelpers.FileSource.Local;
            if (current != FileHelpers.FileSource.Cloud && FileHelpers.CloudStorageSupportedAndEnabled)
                yield return FileHelpers.FileSource.Cloud;
        }

        private static byte[] SettingsReceiveBuffer = [];
        private static int SettingsReceiveBufferBytesReceived;
        private static int SettingsReceiveHash;

        private static int GetHashCode<T>(T[] array) where T : struct
        {
            unchecked
            {
                if (array == null)
                {
                    return 0;
                }
                int hash = 17;
                foreach (T element in array)
                {
                    hash = hash * 31 + element.GetHashCode();
                }
                return hash;
            }
        }

        private static string ServerVersion = "";

        // 0.9.0: internal (not private) and WorldCachePath overridable, so a shareable ".bcworld" file
        // (WorldCacheShare) can read and write this same on-disk cache, and so the offline tests can point it at a
        // temp directory. The default is computed lazily (not in a field initializer): Utils.GetSaveDataPath needs
        // a running game, and a field initializer would run - and throw - the instant anything touches this class,
        // including a test that only wants to set the override before ever reading the default.
        internal static class WorldCache
        {
            private static string? worldCachePathOverride;

            internal static string WorldCachePath
            {
                get => worldCachePathOverride ??= Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents", "cache");
                set => worldCachePathOverride = value;
            }

            internal static string GetCachePath(string id) => Path.Combine(WorldCachePath, id + ".bc");

            // 0.9.0: the id of the settings package behind the world this client is currently in - set by the
            // client-side receive/LoadFromCache handlers below, so "bc_cache export" knows which cache entry is
            // "the current world". Null on the host/dedicated server (which always re-serializes fresh instead) and
            // until a client has one (still downloading, a fresh connection, or a world that doesn't use the mod).
            internal static string? CurrentId;

            public static string Add(ZPackage package)
            {
                var id = PackageID(package);
                var filePath = GetCachePath(id);
                if (File.Exists(filePath))
                {
                    LogError($"{filePath} already exists in cache, this shouldn't happen! Deleting the file...");
                    File.Delete(filePath);
                }
                Log($"Adding cache entry {filePath}");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllBytes(filePath + ".tmp", package.GetArray());
                File.Move(filePath + ".tmp", filePath);
                return id;
            }

            private static List<string> GetCacheList() =>
                Directory.Exists(WorldCachePath)
                    ? Directory.GetFiles(WorldCachePath, "*.bc").Select(f => Path.GetFileNameWithoutExtension(f).ToLower()).ToList()
                    : Enumerable.Empty<string>().ToList();

            public static ZPackage SerializeCacheList()
            {
                var items = GetCacheList();
                var pkg = new ZPackage();
                pkg.Write(items.Count);
                foreach (var item in items)
                {
                    pkg.Write(item);
                }

                return pkg;
            }

            public static bool CacheItemExists(ZPackage item, ZPackage cacheList) =>
                CacheItemExists(PackageID(item), cacheList);

            public static bool CacheItemExists(string id, ZPackage cacheList)
            {
                int itemCount = cacheList.ReadInt();
                for (int i = 0; i < itemCount; i++)
                {
                    if (id == cacheList.ReadString())
                    {
                        return true;
                    }
                }

                return false;
            }

            // 0.9.0: is id already in THIS machine's cache on disk (no handshake list involved) - what
            // "bc_cache import" and the plugin-tree seed scan skip duplicates with.
            internal static bool CacheItemExists(string id) => File.Exists(GetCachePath(id));

            public static ZPackage LoadCacheItem(string id) => new(File.ReadAllBytes(GetCachePath(id)));

            public static void DeleteCacheItem(string id) => File.Delete(GetCachePath(id));

            public static BetterContinentsSettings LoadCacheSettings(string id) =>
                BetterContinentsSettings.Load(LoadCacheItem(id));

            private static string ByteArrayToString(byte[] ba)
            {
                var hex = new StringBuilder(ba.Length * 2);
                foreach (byte b in ba)
                    hex.AppendFormat("{0:x2}", b);
                return hex.ToString();
            }

            public static string PackageID(ZPackage package) => ByteArrayToString(package.GenerateHash()).Substring(0, 32).ToLower();
        }

        private class BCClientInfo
        {
#nullable disable
            public long id;
            public string player;
            public ZNetPeer peer;
            public string version;
            public ZPackage worldCache;
#nullable enable
            public bool readyForPeerInfo;

            public override string ToString() => $"{id} ({player})";
        }

        private static readonly List<BCClientInfo> ClientInfo = [];

        // Register our RPC for receiving settings on clients
        [HarmonyPrefix, HarmonyPatch(nameof(ZNet.OnNewConnection))]
        private static void OnNewConnectionPrefix(ZNet __instance, ZNetPeer peer)
        {
            Log($"Registering settings RPC");

            ServerVersion = "(old)";

            if (ZNet.instance.IsServer())
            {
                var bcClientInfo = new BCClientInfo { peer = peer, readyForPeerInfo = false };
                ClientInfo.Add(bcClientInfo);
                peer.m_rpc.Register("BetterContinentsServerHandshake", (ZRpc rpc, string clientVersion, ZPackage worldCache) =>
                {
                    Log($"Receiving new client version {clientVersion}");
                    // We check this when sending settings (if we have a BC world loaded, otherwise it doesn't matter)
                    bcClientInfo.version = clientVersion;
                    bcClientInfo.worldCache = worldCache;
                });

                peer.m_rpc.Register("BetterContinentsReady", (ZRpc rpc, int stage) =>
                {
                    Log($"Client is ready for PeerInfo");
                    // We wait for this flag before continuing after sending the world settings, allowing the client to behave asynchronously on its end
                    bcClientInfo.readyForPeerInfo = true;
                });

                // 0.8.1: after applying the server's alt-biome placement, the client reports its hashes, so a
                // disagreement shows in the server's log. A warning, never a kick.
                peer.m_rpc.Register("BetterContinentsAltBiomesResult", (ZRpc rpc, int gridHash, int assignmentHash) =>
                    AltBiomeControl.OnClientResult(peer, gridHash, assignmentHash));
            }
            else
            {
                // 0.9.0: a fresh connection means a (possibly different) world; forget which cache entry was
                // "current" until this connection's own handshake or download says otherwise (LoadFromCache /
                // ReceivedSettings below) - otherwise a stale id from the last server could get exported under
                // this world's name.
                WorldCache.CurrentId = null;
                peer.m_rpc.Invoke("BetterContinentsServerHandshake", ModInfo.Version, WorldCache.SerializeCacheList());

                // The server's alt-biome placement, applied over the client's own once its grid is built
                // (AltBiomeControl.OnBiomeDataReady). Sent before PeerInfo, so it is here in time.
                AltBiomeControl.ResetServerAssignment();
                AltBiomeControl.ServerPeer = peer;
                peer.m_rpc.Register("BetterContinentsAltBiomes", (ZRpc rpc, ZPackage assignment) =>
                    AltBiomeControl.ReceiveServerAssignment(assignment));

                // 0.9.0: the server's export HUD and Allow Export values (LiveConfig), sent when our PeerInfo arrives
                // there and again whenever they change on the server. They apply until this connection ends.
                LiveConfig.ClearServerValues();
                peer.m_rpc.Register(LiveConfig.Rpc, (ZRpc rpc, ZPackage values) => LiveConfig.ReceiveServerValues(values));

                peer.m_rpc.Register("BetterContinentsVersion", (ZRpc rpc, string serverVersion) =>
                {
                    ServerVersion = serverVersion;
                    Log($"Receiving server version {serverVersion}");
                });

                peer.m_rpc.Register("BetterContinentsConfigLoadFromCache", (ZRpc rpc, string id) =>
                {
                    Log($"Loading server world settings from local cache, id {id}");

                    __instance.StartCoroutine(LoadFromCache(peer, id));
                });

                peer.m_rpc.Register("BetterContinentsConfigStart", (ZRpc rpc, int totalBytes, int hash) =>
                {
                    SettingsReceiveBuffer = new byte[totalBytes];
                    SettingsReceiveHash = hash;
                    SettingsReceiveBufferBytesReceived = 0;
                    Log($"Receiving settings from server ({SettingsReceiveBuffer.Length} bytes)");

                    UI.Add("ConfigDownload", () => UI.ProgressBar(SettingsReceiveBufferBytesReceived * 100 / SettingsReceiveBuffer.Length, $"Better Continents: downloading world settings from server ..."));
                });

                peer.m_rpc.Register("BetterContinentsConfigPacket", (ZRpc rpc, int offset, int packetHash, ZPackage packet) =>
                {
                    var packetData = packet.GetArray();
                    int hash = GetHashCode(packetData);
                    if (hash != packetHash)
                    {
                        LastConnectionError = $"Better Continents: settings from server were corrupted during transfer, please reconnect!";
                        LogError($"{LastConnectionError}: packet hash mismatch, got {hash}, expected {packetHash}");

                        ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
                        ZNet.instance.Disconnect(peer);
                        return;
                    }

                    Buffer.BlockCopy(packetData, 0, SettingsReceiveBuffer, offset, packetData.Length);

                    SettingsReceiveBufferBytesReceived += packetData.Length;

                    Log($"Received settings packet {packetData.Length} bytes at {offset}, {SettingsReceiveBufferBytesReceived} / {SettingsReceiveBuffer.Length} received");
                    if (SettingsReceiveBufferBytesReceived == SettingsReceiveBuffer.Length)
                    {
                        UI.Remove("ConfigDownload");
                        __instance.StartCoroutine(ReceivedSettings(peer));
                    }
                });
            }
        }

        private static IEnumerator LoadFromCache(ZNetPeer peer, string id)
        {
            var loadTask = Task.Run(() =>
            {
                var package = WorldCache.LoadCacheItem(id);
                // Recalculate the id again to confirm it really matches
                string localId = WorldCache.PackageID(package);
                if (id != localId)
                {
                    return null;
                }
                return BetterContinentsSettings.Load(package);
            });

            try
            {
                UI.Add("LoadingFromCache", () => UI.DisplayMessage($"Better Continents: initializing from cached config"));
                yield return new WaitUntil(() => loadTask.IsCompleted);
            }
            finally
            {
                UI.Remove("LoadingFromCache");
            }

            if (loadTask.IsFaulted || loadTask.Result == null)
            {
                LastConnectionError = loadTask.Exception != null
                    ? $"Better Continents: cached world settings failed to load ({loadTask.Exception.Message}), please reconnect to download them again!"
                    : $"Better Continents: cached world settings are corrupted, please reconnect to download them again!";

                LogError(LastConnectionError);
                ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
                ZNet.instance.Disconnect(peer);
                WorldCache.DeleteCacheItem(id);
                yield break;
            }

            Settings = loadTask.Result;
            WorldCache.CurrentId = id;
            DynamicPatch();
            Settings.Dump();

            // We only care about server/client version match when the server sends a world that actually uses the mod
            if (Settings.EnabledForThisWorld && ServerVersion != ModInfo.Version)
            {
                LastConnectionError = $"Better Continents: world has the mod enabled, but server {ServerVersion} and client {ModInfo.Version} versions don't match";
                LogError(LastConnectionError);
                ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
                ZNet.instance.Disconnect(peer);
            }
            else if (!Settings.EnabledForThisWorld)
            {
                Log($"Server world does not have Better Continents enabled, skipping version check");
            }

            peer.m_rpc.Invoke("BetterContinentsReady", 0);
        }

        private static IEnumerator ReceivedSettings(ZNetPeer peer)
        {
            int finalHash = GetHashCode(SettingsReceiveBuffer);
            if (finalHash == SettingsReceiveHash)
            {
                Log($"Settings transfer complete, unpacking them now");

                var loadingTask = Task.Run(() =>
                {
                    var settingsPkg = new ZPackage(SettingsReceiveBuffer);
                    var settings = BetterContinentsSettings.Load(settingsPkg);
                    // 0.9.0: Add's return is this package's id - the same one a join check computes - remembered
                    // as "the current world" below, once we're back on the main thread.
                    var id = WorldCache.Add(settingsPkg);
                    return (settings, id);
                });

                try
                {
                    UI.Add("ReceivedSettings", () => UI.DisplayMessage($"Better Continents: initializing from server config"));
                    yield return new WaitUntil(() => loadingTask.IsCompleted);
                }
                finally
                {
                    UI.Remove("ReceivedSettings");
                }

                if (loadingTask.IsFaulted)
                {
                    LastConnectionError = $"Better Continents: cached world settings failed to load ({loadingTask.Exception.Message}), please reconnect to download them again!";
                    LogError(LastConnectionError);
                    ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
                    ZNet.instance.Disconnect(peer);
                    yield break;
                }

                Settings = loadingTask.Result.settings;
                WorldCache.CurrentId = loadingTask.Result.id;
                DynamicPatch();
                Settings.Dump();

                // We only care about server/client version match when the server sends a world that actually uses the mod
                if (Settings.EnabledForThisWorld && ServerVersion != ModInfo.Version)
                {
                    LastConnectionError = $"Better Continents: world has Better Continents enabled, but server {ServerVersion} and client {ModInfo.Version} mod versions don't match";
                    LogError(LastConnectionError);
                    ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
                    ZNet.instance.Disconnect(peer);
                }
                else if (!Settings.EnabledForThisWorld)
                {
                    Log($"Server world does not have Better Continents enabled, skipping version check");
                }

                peer.m_rpc.Invoke("BetterContinentsReady", 0);
            }
            else
            {
                LogError($"{LastConnectionError}: hash mismatch, got {finalHash}, expected {SettingsReceiveHash}");
                ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
                ZNet.instance.Disconnect(peer);
            }
        }


        // A client's entry leaves ClientInfo when its connection ends. Upstream 0.7.31 hooked ZNet.ClearPlayerData for
        // this, but vanilla also calls that from RPC_ServerHandshake (ZNet.cs:889-895), for the game's own handshake that
        // every client sends straight after "BetterContinentsServerHandshake" (ZNet.OnNewConnection). The entry was gone
        // before the client's PeerInfo, so SendSettings never found its version and a world that uses Better Continents
        // turned the client away as having "an old version of Better Continents, or none". ZNet.Disconnect
        // (ZNet.cs:1239-1248) is the end of a connection, and it still calls ClearPlayerData first.
        [HarmonyPostfix, HarmonyPatch(nameof(ZNet.Disconnect))]
        private static void DisconnectPostfix(ZNet __instance, ZNetPeer peer)
        {
            var bcClientInfo = ClientInfo.FirstOrDefault(c => c.peer == peer);
            if (bcClientInfo != null)
                ClientInfo.Remove(bcClientInfo);
            // A client leaving its server stops following the server's export settings.
            if (!__instance.IsServer() && peer != null && peer.m_server)
                LiveConfig.ClearServerValues();
        }

        [HarmonyPrefix, HarmonyPatch(nameof(ZNet.RPC_Error))]
        private static void RPC_ErrorPrefix(ref int error)
        {
            if (error == 69)
            {
                LastConnectionError = $"Better Continents: local mod version doesn't match the servers (local one is {ModInfo.Version}, server one is unknown)";
                error = (int)ZNet.ConnectionStatus.ErrorConnectFailed;
            }
        }

        // Settings Transfer Rate (07 BetterContinents.Misc, ConfigSettingsTransferRate): Valheim pins every Steam
        // connection to a 150 KB/s send rate (ZSteamSocket.RegisterGlobalCallbacks sets SendRateMin and SendRateMax
        // to 153600 bytes/s), and the transfer below drains at whatever rate the connection allows, so an 11.8 MB
        // world took 93 s to join over Steam sockets. This raises SendRateMax for one connection while its transfer
        // runs (below, in SendSettings), then restores it. Read only on the sending side (this machine is the host
        // or the dedicated server): a client's own value has no effect, and a PlayFab (crossplay) connection cannot
        // be changed at all (ZPlayFabSocket, not ZSteamSocket).
        private const int VanillaSteamSendRate = 153600;
        private static bool LoggedPlayFabTransferRateNotice;

        // The low-level change. False means nothing was touched (not a Steam connection, the connection is already
        // gone, or Steam refused the change) and never throws for those cases; callers still wrap it, since the
        // Steam API can throw for other reasons (a torn-down connection handle reused elsewhere, for instance).
        private static bool TrySetSteamSendRate(ISocket socket, int bytesPerSecond)
        {
            if (socket is not ZSteamSocket steam || steam.m_con == HSteamNetConnection.Invalid)
                return false;
            var handle = GCHandle.Alloc(bytesPerSecond, GCHandleType.Pinned);
            try
            {
                // Per-connection scope is k_ESteamNetworkingConfig_Connection, verified against
                // libs-Tools/steamworks.net.dll's own metadata (Connection = 4, NOT k_ESteamNetworkingConfig_ListenSocket
                // = 3, which an earlier draft of this fix used and which would have applied the override to the wrong
                // scope object and done nothing). Scope object is this one connection's handle.
                var scopeObj = new IntPtr((long)steam.m_con.m_HSteamNetConnection);
                // A dedicated server talks to Steam through the game-server interfaces (its own assembly_valheim makes
                // its sockets with SteamGameServerNetworkingSockets), and there the client-side SteamNetworkingUtils
                // wrapper throws "Steamworks is not initialized." - seen 2026-09-25 on the local 1.0.15 server, where
                // the transfer then fell back to Valheim's own rate. A player hosting from the game uses the client
                // interfaces. Underneath it is the same ISteamNetworkingUtils and the same connection handle, so the
                // override is the same call either way; the other interface is tried if the expected one refuses.
                bool dedicated = ZNet.instance != null && ZNet.instance.IsDedicated();
                try
                {
                    return SetSendRate(dedicated, scopeObj, handle.AddrOfPinnedObject(), bytesPerSecond);
                }
                catch (InvalidOperationException)
                {
                    return SetSendRate(!dedicated, scopeObj, handle.AddrOfPinnedObject(), bytesPerSecond);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        // GameNetworkingSockets has no adaptive bandwidth (its header: SendRateMin and SendRateMax "should always be
        // set to the same value, to have deterministic behavior"): a connection sends at its minimum, and vanilla pins
        // both to 153600 (ZSteamSocket.RegisterGlobalCallbacks). Raising only the maximum changed nothing - seen
        // 2026-09-25, 11.8 MB still took 78 s after the override was accepted - so both are set, the maximum first
        // when raising and the minimum first when lowering, so the minimum never sits above the maximum.
        private static bool SetSendRate(bool gameServer, IntPtr connection, IntPtr value, int bytesPerSecond)
        {
            bool raising = bytesPerSecond > VanillaSteamSendRate;
            var first = raising ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin;
            var second = raising ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax;
            return SetConfig(gameServer, first, connection, value) & SetConfig(gameServer, second, connection, value);
        }

        private static bool SetConfig(bool gameServer, ESteamNetworkingConfigValue which, IntPtr connection, IntPtr value) => gameServer
            ? SteamGameServerNetworkingUtils.SetConfigValue(which, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, connection,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, value)
            : SteamNetworkingUtils.SetConfigValue(which, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, connection,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, value);

        // Puts this connection's send rate back to vanilla once the transfer ends, times out, or is abandoned. Safe
        // to call even if the peer disconnected mid-transfer: its connection is simply gone, which is not an error,
        // so any exception here is only logged (at Unity's Log level, not Warning/Error).
        private static void RestoreSteamSendRate(ZRpc rpc, string why)
        {
            try
            {
                if (!TrySetSteamSendRate(rpc.GetSocket(), VanillaSteamSendRate))
                    Log($"Steam send rate restore ({why}): nothing to restore (the connection is already closed, or the rate was never raised).");
            }
            catch (Exception e)
            {
                Log($"Steam send rate restore skipped ({why}): {e.Message}");
            }
        }

        private static IEnumerator SendSettings(ZRpc rpc, ZPackage pkg, Action call_RPC_PeerInfo)
        {
            static byte[] ArraySlice(byte[] source, int offset, int length)
            {
                byte[] target = new byte[length];
                Buffer.BlockCopy(source, offset, target, 0, length);
                return target;
            }

            var peer = ZNet.instance.GetPeer(rpc);
            if (peer == null)
            {
                Log($"Couldn't find peer for rpc");
                rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorConnectFailed);
                yield break;
            }

            var bcClientInfo = ClientInfo.FirstOrDefault(c => c.peer == peer);
            if (bcClientInfo != null)
            {
                // Peek some info (the main impl does this also)
                int startPos = pkg.GetPos();
                bcClientInfo.id = pkg.ReadLong();
                string version = pkg.ReadString();
                var refPos = pkg.ReadVector3();
                bcClientInfo.player = pkg.ReadString();
                pkg.SetPos(startPos);
                Log($"Registered client {bcClientInfo} is connecting");
            }
            else
            {
                Log($"Unregistered client is connecting");
            }

            if (!Settings.EnabledForThisWorld)
            {
                Log($"Skipping sending settings, as Better Continents is not enabled in this world");
            }
            else
            {
                Log($"World is using Better Continents, so client version must match server version {ModInfo.Name}");

                if (bcClientInfo?.version == null)
                {
                    Log($"Client info not found, client has an old version of Better Continents, or none!");
                    rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorConnectFailed);
                    ZNet.instance.Disconnect(peer);
                    yield break;
                }
                else if (bcClientInfo.version != ModInfo.Version)
                {
                    Log($"Client {bcClientInfo} version {bcClientInfo.version} doesn't match server version {ModInfo.Version}");
                    peer.m_rpc.Invoke("Error", 69);
                    ZNet.instance.Disconnect(peer);
                    yield break;
                }
                else
                {
                    Log($"Client {bcClientInfo} version {bcClientInfo.version} matches server version {ModInfo.Version}");
                }

                // This was the initial way that versioning was implemented, before the client->server way, so may
                // as well leave it in
                Log($"Sending server version {ModInfo.Version} to client for bi-lateral version agreement");
                rpc.Invoke("BetterContinentsVersion", ModInfo.Version);

                var settingsPackage = new ZPackage();
                Settings.Serialize(settingsPackage, true);

                if (WorldCache.CacheItemExists(settingsPackage, bcClientInfo.worldCache))
                {
                    // We send hash and id
                    string cacheId = WorldCache.PackageID(settingsPackage);
                    Log($"Client {bcClientInfo} already has cached settings for world, instructing it to load those (id {cacheId})");
                    rpc.Invoke("BetterContinentsConfigLoadFromCache", cacheId);
                }
                else
                {
                    Log($"Client {bcClientInfo} doesn't have cached settings, sending them now");
                    Settings.Dump();

                    var settingsData = settingsPackage.GetArray();
                    Log($"Sending settings package header for {settingsData.Length} byte stream");
                    rpc.Invoke("BetterContinentsConfigStart", settingsData.Length, GetHashCode(settingsData));

                    const int SendChunkSize = 128 * 1024;

                    // Settings Transfer Rate: read only here, on the sending side. See TrySetSteamSendRate above.
                    var transferRate = ConfigSettingsTransferRate?.Value ?? TransferRate.Default;
                    int? rateBytesPerSecond = TransferRate.BytesPerSecond(transferRate);
                    Log($"Settings transfer rate: {TransferRate.Describe(transferRate)} (server setting)");
                    bool rateRaised = false;
                    if (rateBytesPerSecond.HasValue)
                    {
                        if (rpc.GetSocket() is ZSteamSocket)
                        {
                            try
                            {
                                rateRaised = TrySetSteamSendRate(rpc.GetSocket(), rateBytesPerSecond.Value);
                                if (!rateRaised)
                                    Log("Could not raise the Steam send rate for this connection; sending at Valheim's own rate.");
                            }
                            catch (Exception e)
                            {
                                LogWarning($"Could not raise the Steam send rate for this connection ({e.Message}); sending at Valheim's own rate.");
                            }
                        }
                        else if (!LoggedPlayFabTransferRateNotice)
                        {
                            // Not spammed per connection: a crossplay server would otherwise log this on every join.
                            LoggedPlayFabTransferRateNotice = true;
                            Log("Settings Transfer Rate cannot be raised on this connection (not Steam, e.g. PlayFab/crossplay); sending at Valheim's own rate.");
                        }
                    }
                    float transferStartedAt = Time.realtimeSinceStartup;
                    int nextProgressLogAt = Mathf.Max(settingsData.Length / 10, 1);

                    for (int sentBytes = 0; sentBytes < settingsData.Length;)
                    {
                        int packetSize = Mathf.Min(settingsData.Length - sentBytes, SendChunkSize);
                        var packet = ArraySlice(settingsData, sentBytes, packetSize);
                        rpc.Invoke("BetterContinentsConfigPacket", sentBytes, GetHashCode(packet),
                            new ZPackage(packet));
                        // Make sure to flush or we will saturate the queue...
                        try
                        {
                            rpc.GetSocket().Flush();
                        }
                        catch (NotImplementedException)
                        {
                            // ZPlayFabSocket doesn't implement it and throws instead
                        }

                        sentBytes += packetSize;
                        if (sentBytes >= nextProgressLogAt || sentBytes == settingsData.Length)
                        {
                            Log($"Sent {sentBytes} of {settingsData.Length} bytes");
                            nextProgressLogAt += Mathf.Max(settingsData.Length / 10, 1);
                        }
                        float timeout = Time.time + 30;
                        // Keep up to two chunks in flight (Steam's own send buffer is 512 KiB) instead of draining to one.
                        yield return new WaitUntil(() => rpc.GetSocket().GetSendQueueSize() < 2 * SendChunkSize || Time.time > timeout);
                        if (Time.time > timeout)
                        {
                            Log($"Timed out sending config to client {bcClientInfo} after 30 seconds, disconnecting them");
                            if (rateRaised)
                                RestoreSteamSendRate(rpc, "transfer timed out");
                            peer.m_rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorConnectFailed);
                            ZNet.instance.Disconnect(peer);
                            yield break;
                        }
                    }
                    if (rateRaised)
                        RestoreSteamSendRate(rpc, "transfer done");
                    float transferSeconds = Mathf.Max(Time.realtimeSinceStartup - transferStartedAt, 0.001f);
                    Log($"Settings sent: {settingsData.Length} bytes in {transferSeconds:F1} s ({settingsData.Length / transferSeconds / 1024f:F0} KB/s)");
                }
                yield return new WaitUntil(() => bcClientInfo.readyForPeerInfo || !peer.m_socket.IsConnected());

                // Nothing about sectors or alt biomes is sent by vanilla: each peer computes its own. Send ours so
                // the client uses the server's placement and can report whether its sector grid agrees.
                var altBiomes = AltBiomeControl.BuildServerAssignmentPackage();
                if (altBiomes != null && peer.m_socket.IsConnected())
                {
                    Log($"Sending alt-biome placement to client {bcClientInfo} (grid {AltBiomeControl.LastGridHash:x8}, assignment {AltBiomeControl.LastAssignmentHash:x8})");
                    rpc.Invoke("BetterContinentsAltBiomes", altBiomes);
                }
            }

            call_RPC_PeerInfo();
        }

        // 0.9.0: the server's Hud and Allow Export (LiveConfig) to one client that runs Better Continents. Clients
        // older than 0.9.0 have no handler, and ZRpc drops an unknown RPC without a word.
        private static bool SendLiveConfig(BCClientInfo? client)
        {
            if (client?.version == null || client.peer?.m_rpc == null)
                return false;
            client.peer.m_rpc.Invoke(LiveConfig.Rpc, LiveConfig.ServerPackage());
            return true;
        }

        // ... and to every connected one, when they change on this server.
        internal static void SendLiveConfigToAll()
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer())
                return;
            var peers = net.GetPeers();
            int sent = 0;
            foreach (var client in ClientInfo)
            {
                if (client.peer != null && peers.Contains(client.peer) && SendLiveConfig(client))
                    sent++;
            }
            Log($"Sent the export settings to {sent} connected Better Continents client(s).");
        }

        public static void RPC_PeerInfoRedirect(ZRpc rpc, ZPackage pkg, Action call_RPC_PeerInfo)
        {
            // Every client that runs Better Continents learns the server's export settings, whether or not this world
            // uses Better Continents: exporting a vanilla world is as useful as exporting one of ours.
            var peer = ZNet.instance.GetPeer(rpc);
            if (peer != null)
                SendLiveConfig(ClientInfo.FirstOrDefault(c => c.peer == peer));

            if (Settings.EnabledForThisWorld)
            {
                Log($"Sending settings now");
                ZNet.instance.StartCoroutine(SendSettings(rpc, pkg, call_RPC_PeerInfo));
            }
            else
            {
                Log($"World doesn't use Better Continents, skipping version check and sync");
                call_RPC_PeerInfo();
            }
        }
    }

    public static string CleanPath(string? path) => path?.Replace("\\\"", "").Replace("\"", "").Trim() ?? "";
}
