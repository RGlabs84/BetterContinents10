// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

using System;
using System.IO;
using System.Threading;
using BepInEx.Configuration;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// The part of the config that applies while the game runs: the [09 BetterContinents.Export] group.
//
// Every other group is a default for NEW worlds, baked into a world's settings when the world is created (see Awake).
// The Export group is read live instead:
//  - LIVE: a change applies at once, whether it comes from Configuration Manager (SettingChanged) or from an edit of
//    BetterContinents.cfg on disk. The watcher below re-reads the whole file; the other groups take the new values
//    too, but they only matter when the next world is created.
//  - SERVER-AUTHORITATIVE: on a client, Hud and Allow Export come from the server while it is connected to one that
//    runs Better Continents 0.9.0 or later. The server sends them when the client's PeerInfo arrives and again whenever
//    they change ("BetterContinentsLiveConfig", BetterContinents.ZNetPatch.cs). Connected to an older server, or none,
//    a client uses its own values.
//
// Allow Export governs players connected to a server. The machine that runs the world (single player, the host, a
// dedicated server's own console, or an admin's "bc_export server" that the game routes to it) may always export.
public static class LiveConfig
{
  internal const string Rpc = "BetterContinentsLiveConfig";

  // The RPC's package: the format, then the values in this order. A later format appends values, and a client reads
  // the ones it knows.
  private const int PackageFormat = 1;

  // What the server sent this client this session; null = nothing yet (or this machine runs the world).
  private static bool? serverHud;
  private static bool? serverAllowExport;

  internal static (bool? Hud, bool? AllowExport) ServerValues => (serverHud, serverAllowExport);

  /// <summary>This machine is a client of a server: not single player, not the host, not a dedicated server.</summary>
  public static bool IsClient => ZNet.instance != null && !ZNet.instance.IsServer();

  /// <summary>Whether the export HUD is on: the server's Hud on its clients once it has sent one, else this game's.</summary>
  public static bool HudEnabled => EffectiveHud(IsClient, serverHud, ConfigExportHud?.Value ?? false);

  /// <summary>Why this machine may not run a world export now, or null when it may. Only a client can be refused.</summary>
  public static string? ExportBlockedReason() => BlockedReason(IsClient, serverAllowExport, ConfigExportAllowed?.Value ?? true);

  // The rules, free of the game so the offline harness can check them. serverValue is what the server sent this
  // session (null: nothing), localValue this game's own config.
  internal static bool EffectiveHud(bool isClient, bool? serverValue, bool localValue) =>
    (isClient && serverValue.HasValue) ? serverValue.Value : localValue;

  internal static string? BlockedReason(bool isClient, bool? serverValue, bool localValue)
  {
    // The machine that runs the world may always export.
    if (!isClient)
      return null;
    if (serverValue.HasValue)
      return serverValue.Value ? null : "the server does not allow world export (its Allow Export is off)";
    return localValue ? null : "Allow Export is off in this game's Better Continents config";
  }

  /// <summary>Awake, after the config is bound: wires the export gate and the Export group's change handlers, and starts
  /// watching the config file.</summary>
  public static void Init(ConfigFile config)
  {
    WorldExport.Allowed = () => ExportBlockedReason() == null;
    WorldExport.NotAllowedReason = ExportBlockedReason;
    ConfigExportHud.SettingChanged += (_, _) => OwnValueChanged();
    ConfigExportAllowed.SettingChanged += (_, _) => OwnValueChanged();
    ConfigExportSize.SettingChanged += (_, _) => ExportHud.DefaultChanged(ConfigExportSize);
    ConfigExportHeightmapAmount.SettingChanged += (_, _) => ExportHud.DefaultChanged(ConfigExportHeightmapAmount);
    ConfigExportSeaLevel.SettingChanged += (_, _) => ExportHud.DefaultChanged(ConfigExportSeaLevel);
    StartWatcher(config);
  }

  /// <summary>The built-in export options with Default Size, Default Heightmap Amount and Default Sea Level applied. Left
  /// as they are when the config is not bound (the offline harness).</summary>
  internal static WorldExport.Options ApplyDefaults(WorldExport.Options options)
  {
    if (ConfigExportSize != null)
      options.Size = ConfigExportSize.Value;
    if (ConfigExportHeightmapAmount != null)
      options.HeightmapAmount = ConfigExportHeightmapAmount.Value;
    if (ConfigExportSeaLevel != null)
      options.SeaLevel = ConfigExportSeaLevel.Value;
    return options;
  }

  // Hud or Allow Export changed in this game's config (Configuration Manager, a file edit, another mod).
  private static void OwnValueChanged()
  {
    var hud = ConfigExportHud.Value;
    var allow = ConfigExportAllowed.Value;
    var net = ZNet.instance;
    if (net != null && net.IsServer())
    {
      // This machine runs the world, so its values are the server's: every connected Better Continents client follows.
      WorldExport.Say($"Better Continents export settings: HUD {OnOff(hud)}, players {(allow ? "may" : "may not")} export the world. Sending them to the connected clients.");
      ZNetPatch.SendLiveConfigToAll();
    }
    else if (IsClient && (serverHud.HasValue || serverAllowExport.HasValue))
      WorldExport.Say("Better Continents export settings changed in this game's config; the server's Hud and Allow Export apply while connected to it.");
    else
      WorldExport.Say($"Better Continents export settings: HUD {OnOff(hud)}, world export {(allow ? "allowed" : "not allowed")} when connected to a server.");
    EnforceAllowed();
  }

  // The server's values, for the RPC.
  internal static ZPackage ServerPackage()
  {
    var pkg = new ZPackage();
    pkg.Write(PackageFormat);
    pkg.Write(ConfigExportHud.Value);
    pkg.Write(ConfigExportAllowed.Value);
    return pkg;
  }

  // The package's values (format 1 and later).
  internal static (bool Hud, bool AllowExport) ReadPackage(ZPackage pkg)
  {
    if (pkg.ReadInt() < 1)
      throw new InvalidDataException("unknown format");
    return (pkg.ReadBool(), pkg.ReadBool());
  }

  // A client receives the server's values: they stand in for its own Hud and Allow Export until the connection ends.
  internal static void ReceiveServerValues(ZPackage pkg)
  {
    bool hud, allow;
    try
    {
      (hud, allow) = ReadPackage(pkg);
    }
    catch (Exception e)
    {
      LogWarning($"Could not read the server's export settings ({e.Message}); this game's own apply.");
      return;
    }
    bool first = !serverHud.HasValue;
    bool changed = serverHud != hud || serverAllowExport != allow;
    serverHud = hud;
    serverAllowExport = allow;
    var text = $"export HUD {OnOff(hud)}, world export {(allow ? "allowed" : "not allowed")}";
    if (first)
      Log($"The server's export settings apply while connected: {text}.");
    else if (changed)
      WorldExport.Say($"Better Continents: the server changed its export settings: {text}.");
    EnforceAllowed();
  }

  /// <summary>A new session starts, or the connection to the server ends: this game's own values apply again.</summary>
  internal static void ClearServerValues()
  {
    if (serverHud.HasValue || serverAllowExport.HasValue)
      Log("Export settings: this game's own config applies again.");
    serverHud = null;
    serverAllowExport = null;
  }

  // Allow Export turned off for this machine while its export runs: stop it, as the server (or the player) asked.
  private static void EnforceAllowed()
  {
    if (!WorldExport.IsRunning)
      return;
    var reason = ExportBlockedReason();
    if (reason == null)
      return;
    WorldExport.Say($"World export stopped: {reason}.", warning: true);
    WorldExport.Cancel();
  }

  private static string OnOff(bool on) => on ? "on" : "off";

  // ---- live reload of BetterContinents.cfg from disk -----------------------------------------------------------------

  // BepInEx reads its config file once, at startup. The watcher notes each change on its own worker thread (only a
  // timestamp and a flag: no Unity or BepInEx call is safe there), and Update, on the main thread, reloads once the
  // file has been quiet for DebounceTicks, so an editor's burst of writes is read once, after it is complete.
  private static readonly long DebounceTicks = TimeSpan.FromMilliseconds(500).Ticks;
  private static FileSystemWatcher? watcher;
  private static ConfigFile? watchedConfig;
  private static string configPath = "";
  private static long lastChangeTicks;
  private static volatile bool changePending;
  // A reload that could not read the file (an editor holding it locked, a save half done) is tried again this often.
  private const int MaxReloadRetries = 5;
  private static int reloadRetries;

  private static void StartWatcher(ConfigFile config)
  {
    try
    {
      watchedConfig = config;
      configPath = config.ConfigFilePath;
      var dir = Path.GetDirectoryName(configPath);
      var name = Path.GetFileName(configPath);
      if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
        throw new IOException("no config file path");
      watcher = new FileSystemWatcher(dir, name)
      {
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        IncludeSubdirectories = false,
      };
      // Editors save in place (Changed) or through a temporary file renamed over it (Created, Renamed).
      watcher.Changed += OnFileEvent;
      watcher.Created += OnFileEvent;
      watcher.Renamed += OnFileEvent;
      // An overflowed event buffer loses events, not the fact that the file changed: re-read it.
      watcher.Error += (_, _) => MarkChanged();
      watcher.EnableRaisingEvents = true;
      Log($"Watching {configPath}: an edit applies while the game runs (the Export settings at once, world-generation settings to worlds created afterwards).");
    }
    catch (Exception e)
    {
      watcher?.Dispose();
      watcher = null;
      LogWarning($"Cannot watch {configPath} for changes ({e.Message}). Edits to the file apply after a restart; changes made in Configuration Manager still apply at once.");
    }
  }

  // FileSystemWatcher thread.
  private static void OnFileEvent(object sender, FileSystemEventArgs e) => MarkChanged();

  private static void MarkChanged()
  {
    Interlocked.Exchange(ref lastChangeTicks, DateTime.UtcNow.Ticks);
    changePending = true;
  }

  /// <summary>Main thread, every frame (BetterContinents.Update): reloads the config once a change on disk has settled.</summary>
  public static void Update()
  {
    if (!changePending)
      return;
    if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastChangeTicks) < DebounceTicks)
      return;
    // Cleared before reading, so a write that lands during the reload is read again after its own quiet period.
    changePending = false;
    ReloadFromDisk();
  }

  private static void ReloadFromDisk()
  {
    var config = watchedConfig;
    if (config == null || !File.Exists(configPath))
      return;
    int changed = 0;
    void Count(object sender, SettingChangedEventArgs args) => changed++;
    bool saveOnSet = config.SaveOnConfigSet;
    config.SettingChanged += Count;
    // The values come from the file: saving them back would rewrite the player's file and wake the watcher again.
    config.SaveOnConfigSet = false;
    try
    {
      // Raises SettingChanged for every value that differs, on this (the main) thread: the handlers in Init run here.
      config.Reload();
    }
    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
    {
      if (++reloadRetries <= MaxReloadRetries)
        MarkChanged();
      else
      {
        reloadRetries = 0;
        LogWarning($"Could not read {configPath} after {MaxReloadRetries} tries ({e.Message}); the next change to it is read again.");
      }
      return;
    }
    catch (Exception e)
    {
      reloadRetries = 0;
      LogWarning($"Could not reload {configPath}: {e.Message}");
      return;
    }
    finally
    {
      config.SaveOnConfigSet = saveOnSet;
      config.SettingChanged -= Count;
    }
    reloadRetries = 0;
    if (changed > 0)
      Log($"{configPath} changed on disk: {changed} setting(s) reloaded. The Export settings apply at once; world-generation settings apply to worlds created from now on.");
  }
}
