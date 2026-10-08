// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// BakedTransfer (spec 5, 14.1 "Transfer"): stage 1 of the join and the pushes during play, over real ZRpc objects on made-up sockets. The
// server's connection to a client is a pair of XferEnd sockets (what one sends is in flight until the wire delivers it; the number of bytes in
// flight is what GetSendQueueSize says), and the two ZRpc objects are the game's own, so the RPCs are serialized and read as they are on a real
// connection. The clock and the coroutines are BakedTransfer's seams: a scheduler steps every coroutine once a tick, delivers the wires, updates the
// ZRpc objects, and moves a clock of its own, so a wait of 120 s takes no time. The client is either the real one (BakedTransfer.RegisterClient, with
// the cache in a folder of its own) or a scripted one that records what it is sent and answers as the test says. Everything here is named Xfer.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using WorldCache = BetterContinents.BetterContinents.ZNetPatch.WorldCache;

namespace PlacementTests;

internal static partial class Tests
{
  // ------------------------------------------------------------------------------------------------ the wire

  // One end of a made-up connection: what it sends waits in Flight until the wire delivers it to the other end's Inbox.
  private sealed class XferEnd : ISocket
  {
    public XferEnd Peer = null;
    public bool Open = true;
    public readonly string Name;
    public readonly Queue<byte[]> Inbox = new();
    public readonly List<byte[]> Flight = new();
    public int FlightBytes;
    // The method hash of every package handed to Send, in order, and how many bytes were in flight at the most.
    public readonly List<int> SentMethods = new();
    public int MaxFlightBytes;
    // Called as a package leaves the wire: its bytes and its number among the packages sent from this end. Null drops it.
    public Func<byte[], int, byte[]> Filter;
    public bool Stalled;
    private int left;

    public XferEnd(string name) => Name = name;

    public bool IsConnected() => Open && Peer.Open;

    public void Send(ZPackage pkg)
    {
      if (!IsConnected())
        return;
      var bytes = pkg.GetArray();
      Flight.Add(bytes);
      FlightBytes += bytes.Length;
      MaxFlightBytes = Math.Max(MaxFlightBytes, FlightBytes);
      SentMethods.Add(bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : 0);
    }

    public ZPackage Recv() => Inbox.Count > 0 ? new ZPackage(Inbox.Dequeue()) : null;
    public int GetSendQueueSize() => FlightBytes;
    public int GetCurrentSendRate() => 0;
    public bool IsHost() => false;
    public void Dispose() => Close();
    public bool GotNewData() => Inbox.Count > 0;

    public void Close()
    {
      // Both ends are down; what was in flight, and what was delivered and not read, is lost.
      foreach (var end in new[] { this, Peer })
      {
        end.Open = false;
        end.Flight.Clear();
        end.FlightBytes = 0;
        end.Inbox.Clear();
      }
    }

    public string GetEndPointString() => Name;
    public void GetAndResetStats(out int totalSent, out int totalRecv) { totalSent = 0; totalRecv = 0; }
    public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec) { localQuality = remoteQuality = 1f; ping = 0; outByteSec = inByteSec = 0f; }
    public ISocket Accept() => null;
    public int GetHostPort() => 0;
    public bool Flush() => true;
    public string GetHostName() => Name;
    public void VersionMatch() { }

    // The wire: up to `budget` bytes go to the other end.
    public void Deliver(int budget)
    {
      if (Stalled || !IsConnected())
        return;
      while (Flight.Count > 0 && budget > 0)
      {
        var bytes = Flight[0];
        Flight.RemoveAt(0);
        FlightBytes -= bytes.Length;
        budget -= bytes.Length;
        var arrives = Filter == null ? bytes : Filter(bytes, left);
        left++;
        if (arrives != null)
          Peer.Inbox.Enqueue(arrives);
      }
    }

    // How many packages sent from this end have this method.
    public int Count(string rpc) => SentMethods.Count(m => m == rpc.GetStableHashCode());
  }

  // What a scripted client does: it records the transfers it is sent and answers each one as Answer says ("ready" and "applied" are the answers
  // the real client gives, "needfull" asks for the whole layer, "silent" says nothing).
  private sealed class XferScript
  {
    public readonly List<BakedTransfer.LayerHeader> Starts = new();
    public readonly List<byte[]> Completed = new();
    public readonly List<(string Id, int Revision)> CacheLoads = new();
    public Func<BakedTransfer.LayerHeader, string> Answer = header => header.Joining ? "ready" : "applied";
    public BakedTransfer.LayerHeader Current;
    public byte[] Buffer;
    public int Received;
    public bool Active => Current != null;
  }

  // The server's connection to one client, and the client's end of it.
  private sealed class XferPair
  {
    public readonly string Name;
    public readonly XferEnd ServerEnd, ClientEnd;
    public readonly ZRpc ServerRpc, ClientRpc;
    public readonly BakedTransfer.LayerClient Client;
    public readonly List<string> Disconnects = new();
    public readonly List<string> Failures = new();
    public readonly XferScript Script;

    // `scripted` false: the real client (BakedTransfer.RegisterClient); there can be one at a time, it is the process's own.
    public XferPair(string name, bool scripted)
    {
      Name = name;
      ServerEnd = new XferEnd(name + " (server end)");
      ClientEnd = new XferEnd(name + " (client end)");
      ServerEnd.Peer = ClientEnd;
      ClientEnd.Peer = ServerEnd;
      ServerRpc = new ZRpc(ServerEnd);
      ClientRpc = new ZRpc(ClientEnd);
      Client = new BakedTransfer.LayerClient(name, new BakedTransfer.ZRpcLink(ServerRpc), reason =>
      {
        Disconnects.Add(reason);
        ServerEnd.Close();
      });
      BakedTransfer.Clients.Add(Client);
      // The server's side, as ZNetPatch registers it for a connection.
      BakedTransfer.RegisterServer(ServerRpc, Client);
      ServerRpc.Register(BakedTransfer.RpcReady, (ZRpc _, int stage) =>
      {
        if (stage != 0)
          BakedTransfer.OnReady(Client, stage);
      });
      if (!scripted)
      {
        BakedTransfer.RegisterClient(ClientRpc, new BakedTransfer.ZRpcLink(ClientRpc), reason =>
        {
          Failures.Add(reason);
          ClientEnd.Close();
        });
        return;
      }
      Script = new XferScript();
      void Finish()
      {
        var header = Script.Current;
        Script.Completed.Add(Script.Buffer);
        Script.Current = null;
        Answer(header);
      }
      ClientRpc.Register(BakedTransfer.RpcStart, (ZRpc _, ZPackage package) =>
      {
        var header = BakedTransfer.LayerHeader.Read(package);
        Script.Starts.Add(header);
        Script.Current = header;
        Script.Buffer = new byte[header.Length];
        Script.Received = 0;
        if (header.Length == 0)
          Finish();
      });
      ClientRpc.Register(BakedTransfer.RpcPacket, (ZRpc _, int offset, int packetHash, ZPackage packet) =>
      {
        var bytes = packet.GetArray();
        System.Buffer.BlockCopy(bytes, 0, Script.Buffer, offset, bytes.Length);
        Script.Received += bytes.Length;
        if (Script.Received == Script.Buffer.Length)
          Finish();
      });
      ClientRpc.Register(BakedTransfer.RpcLoadFromCache, (ZRpc _, string id, int revision) =>
      {
        Script.CacheLoads.Add((id, revision));
        ClientRpc.Invoke(BakedTransfer.RpcReady, 1);
      });
    }

    private void Answer(BakedTransfer.LayerHeader header)
    {
      switch (Script.Answer(header))
      {
        case "ready": ClientRpc.Invoke(BakedTransfer.RpcReady, 1); break;
        case "applied": ClientRpc.Invoke(BakedTransfer.RpcApplied, (int)header.Revision); break;
        case "needfull": ClientRpc.Invoke(BakedTransfer.RpcNeedFull, (int)header.Revision); break;
      }
    }

    // The connection is over (the client leaves, or the server hangs up).
    public void Close() => ServerEnd.Close();
  }

  // The scheduler and the clock, and everything BakedTransfer keeps that a test must put back.
  private sealed class XferWorld : IDisposable
  {
    public float Time;
    public int Ticks;
    public int Budget = 1 << 20;
    public readonly List<IEnumerator> Routines = new();
    public readonly List<XferPair> Pairs = new();
    public readonly string Folder;
    private readonly Func<float> clock = BakedTransfer.Clock;
    private readonly Func<IEnumerator, object> start = BakedTransfer.StartRoutine;
    private readonly Func<bool> runsWorld = BakedTransfer.RunsWorld;
    private readonly bool clientReports = BakedTransfer.ClientReports;
    private readonly (Func<IEnumerable<(ZoneKey, Heightmap)>>, Action<Heightmap>, Action, Action, Action<Vector3, float>, Action, Func<bool>) ground =
      (BakedGround.LoadedTerrain, BakedGround.PokeTerrain, BakedGround.PokeDistant, BakedGround.ClearBuilds, BakedGround.ResetGrass, BakedGround.DeleteMinimapCache, BakedGround.RunsWorld);
    private static int serial;

    public XferWorld()
    {
      Folder = Path.Combine(Work, "transfer-" + ++serial);
      Directory.CreateDirectory(Folder);
      WorldCache.WorldCachePath = Folder;
      BakedCache.ForgetPruned();
      // The game's terrain is not here: a new layer rebuilds nothing.
      BakedGround.LoadedTerrain = () => new List<(ZoneKey, Heightmap)>();
      BakedGround.PokeTerrain = _ => { };
      BakedGround.PokeDistant = () => { };
      BakedGround.ClearBuilds = () => { };
      BakedGround.ResetGrass = (_, _) => { };
      BakedGround.DeleteMinimapCache = () => { };
      BakedGround.RunsWorld = () => false;
      BakedTransfer.Clock = () => Time;
      BakedTransfer.StartRoutine = routine =>
      {
        Routines.Add(routine);
        return routine;
      };
      BakedTransfer.RunsWorld = () => false;
      BakedTransfer.ClientReports = false;
      BakedTransfer.SessionStarts(false);
      BakedTransfer.Clients.Clear();
    }

    public XferPair Pair(string name, bool scripted = true)
    {
      var pair = new XferPair(name, scripted);
      Pairs.Add(pair);
      return pair;
    }

    // A client that has joined: the server holds the layer it has, and a push reaches it.
    public XferPair Joined(string name, BakedLayer held, bool scripted = true)
    {
      var pair = Pair(name, scripted);
      pair.Client.Held = held;
      pair.Client.JoinDone = true;
      return pair;
    }

    public T Run<T>(T routine) where T : IEnumerator
    {
      Routines.Add(routine);
      return routine;
    }

    public void Tick(float dt = 0.02f)
    {
      Time += dt;
      Ticks++;
      foreach (var pair in Pairs)
      {
        pair.ServerEnd.Deliver(Budget);
        pair.ClientEnd.Deliver(Budget);
      }
      foreach (var pair in Pairs)
      {
        pair.ServerRpc.Update(0f);
        pair.ClientRpc.Update(0f);
      }
      for (int i = 0; i < Routines.Count; i++)
        if (!Routines[i].MoveNext())
          Routines.RemoveAt(i--);
      // The workers (a layer read, a hash, a file written) run in real time.
      Thread.Sleep(1);
    }

    // Ticks until `done`, at most `seconds` of the world's time and one real minute. False when it did not come.
    public bool Until(Func<bool> done, float seconds = 300f, float dt = 0.02f)
    {
      float from = Time;
      var watch = Stopwatch.StartNew();
      while (!done())
      {
        if (Time - from > seconds || watch.Elapsed.TotalSeconds > 60)
          return false;
        Tick(dt);
      }
      return true;
    }

    public void Dispose()
    {
      foreach (var pair in Pairs)
        pair.Close();
      BakedTransfer.Clients.Clear();
      BakedTransfer.SessionStarts(false);
      BakedLayerStore.Changed -= BakedGround.OnLayerChanged;
      BakedTransfer.Clock = clock;
      BakedTransfer.StartRoutine = start;
      BakedTransfer.RunsWorld = runsWorld;
      BakedTransfer.ClientReports = clientReports;
      (BakedGround.LoadedTerrain, BakedGround.PokeTerrain, BakedGround.PokeDistant, BakedGround.ClearBuilds, BakedGround.ResetGrass, BakedGround.DeleteMinimapCache, BakedGround.RunsWorld) = ground;
    }
  }

  private static readonly Dictionary<uint, BakedLayer> xferLayers = new();

  // VALtima's layer with another revision: another id, the same content.
  private static BakedLayer XferRevision(BakedLayer layer, uint revision)
  {
    if (revision == layer.Revision)
      return layer;
    if (!xferLayers.TryGetValue(revision, out var made))
      xferLayers[revision] = made = ServerWithRevision(layer, revision);
    return made;
  }

  private static int XferPacketsOf(int length) => (length + BakedTransfer.SendChunkSize - 1) / BakedTransfer.SendChunkSize;

  private static bool XferSame(byte[] a, int aLength, byte[] b, int bLength) => aLength == bLength && a.AsSpan(0, aLength).SequenceEqual(b.AsSpan(0, bLength));

  // The wire's trouble, on the server's end: the nth packet of the layer (counting the packets, not Start) is changed or lost.
  private static Func<byte[], int, byte[]> XferOnPacket(int number, Func<byte[], byte[]> what)
  {
    int seen = 0;
    return (bytes, _) => bytes.Length >= 4 && BitConverter.ToInt32(bytes, 0) == BakedTransfer.RpcPacket.GetStableHashCode() && seen++ == number ? what(bytes) : bytes;
  }

  // ------------------------------------------------------------------------------------------------ the wire itself

  public static void ServerTransferWireTest()
  {
    Section("transfer: the made-up wire carries ZRpc calls, holds what is in flight, drops, changes and closes");
    using var world = new XferWorld();
    var a = world.Pair("wire", scripted: true);
    var seen = new List<(int, int, int)>();
    a.ClientRpc.Register("XferProbe", (ZRpc _, int x, int y, ZPackage z) => seen.Add((x, y, z.GetArray().Length)));
    for (int i = 0; i < 3; i++)
      a.ServerRpc.Invoke("XferProbe", i, i * 10, new ZPackage(new byte[100 + i]));
    C(a.ServerEnd.GetSendQueueSize() > 300 && a.ServerEnd.Count("XferProbe") == 3 && seen.Count == 0, $"three calls wait in flight ({a.ServerEnd.GetSendQueueSize()} bytes) until the wire delivers");
    a.ServerEnd.Stalled = true;
    world.Tick();
    C(a.ServerEnd.GetSendQueueSize() > 300 && seen.Count == 0, "a stalled wire delivers nothing, and the queue stays");
    a.ServerEnd.Stalled = false;
    world.Tick();
    C(a.ServerEnd.GetSendQueueSize() == 0 && seen.SequenceEqual(new[] { (0, 0, 100), (1, 10, 101), (2, 20, 102) }), "an open wire delivers them whole and in order");
    // (the first three packages left the wire as numbers 0 to 2)
    a.ServerEnd.Filter = (bytes, n) => n == 4 ? null : n == 5 ? bytes.Take(bytes.Length - 1).Append((byte)(bytes[^1] ^ 1)).ToArray() : bytes;
    seen.Clear();
    for (int i = 0; i < 3; i++)
      a.ServerRpc.Invoke("XferProbe", i, 0, new ZPackage(new byte[4]));
    world.Tick();
    C(seen.Count == 2 && seen[0].Item1 == 0 && seen[1].Item1 == 2, "the filter drops the package it says (the second) and passes the others");
    a.ServerEnd.Filter = null;
    a.ServerRpc.Invoke("XferProbe", 7, 0, new ZPackage(new byte[4]));
    a.ClientEnd.Close();
    C(!a.ServerRpc.IsConnected() && !a.ClientRpc.IsConnected() && a.ServerEnd.GetSendQueueSize() == 0, "a close ends both ends and throws away what was in flight");
    a.ServerRpc.Invoke("XferProbe", 8, 0, new ZPackage(new byte[4]));
    world.Tick();
    C(seen.Count == 2, "and nothing is sent to a closed connection");
  }

  // ------------------------------------------------------------------------------------------------ the join: stage 1

  public static void ServerTransferJoinMissTest()
  {
    Section("transfer, join stage 1: a client without the layer is sent it, and has it, cached, checked and current");
    var layer = ServerValtima(nameof(ServerTransferJoinMissTest));
    if (layer == null)
      return;
    using var world = new XferWorld();
    var pair = world.Pair("miss", scripted: false);
    var stage = new BakedTransfer.StageResult();
    var lines = LogHandler.During(() =>
    {
      world.Run(BakedTransfer.JoinStage(pair.Client, layer, stage));
      C(world.Until(() => stage.Ready || stage.Left || stage.Failed), "the stage ended");
    });
    int packets = XferPacketsOf(layer.Length);
    C(stage.Ready && !stage.Left && !stage.Failed && pair.Client.LayerReady && ReferenceEquals(pair.Client.Held, layer) && pair.Client.Pending == null, "the stage is Ready and the server holds the layer the client has");
    C(pair.ServerEnd.Count(BakedTransfer.RpcStart) == 1 && pair.ServerEnd.Count(BakedTransfer.RpcPacket) == packets && pair.ServerEnd.Count(BakedTransfer.RpcLoadFromCache) == 0,
      $"one Start and {packets} packets of at most 128 KiB, and no LoadFromCache");
    C(pair.ClientEnd.Count(BakedTransfer.RpcReady) == 1 && pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 0, "the client answered Ready(1), once, and nothing else");
    C(pair.ServerEnd.MaxFlightBytes <= 2 * BakedTransfer.SendChunkSize + 1024, $"never more than two chunks in flight ({pair.ServerEnd.MaxFlightBytes:N0} bytes)");
    C(world.Ticks >= packets, $"one chunk a frame at most: {packets} packets took {world.Ticks} ticks");
    var mine = BakedLayerStore.Current;
    C(mine != null && mine.Id == layer.Id && mine.Revision == layer.Revision && XferSame(mine.Bytes, mine.Length, layer.Bytes, layer.Length), "the client's layer is the server's, byte for byte");
    var cached = BakedCache.PathOf(layer.Id);
    C(File.Exists(cached) && XferSame(File.ReadAllBytes(cached), (int)new FileInfo(cached).Length, layer.Bytes, layer.Length) && !File.Exists(cached + ".tmp"), "and it is in the client's cache under its id, with no .tmp left");
    C(BakedCache.Ids().SequenceEqual(new[] { layer.Id }), "the handshake's list now has its id");
    C(pair.Failures.Count == 0 && pair.Disconnects.Count == 0, "nobody failed or was disconnected");
    C(lines.Any(l => l.Contains("doesn't have the baked pieces cached")) && lines.Any(l => l.Contains("Baked pieces sent")) && lines.Any(l => l.Contains("Client miss has the baked pieces")), "the log says what happened on both sides");
  }

  public static void ServerTransferJoinHitTest()
  {
    Section("transfer, join stage 1: a client that has the layer cached is told to load it, and nothing is sent");
    var layer = ServerValtima(nameof(ServerTransferJoinHitTest));
    if (layer == null)
      return;
    using (var world = new XferWorld())
    {
      BakedCache.Add(layer.Id, layer.Bytes, layer.Length);
      // The handshake: the client's list of ids, read by the server as ZNetPatch reads it.
      var ids = (HashSet<string>)typeof(BC.ZNetPatch).GetMethod("ReadCachedIds", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, [WorldCache.SerializeCacheList()]);
      C(ids.Contains(layer.Id), "the client's cache list, as the server reads it, has the layer's id");
      var pair = world.Pair("hit", scripted: false);
      pair.Client.CachedIds = ids;
      File.SetLastWriteTimeUtc(BakedCache.PathOf(layer.Id), DateTime.UtcNow.AddDays(-3));
      var stage = new BakedTransfer.StageResult();
      world.Run(BakedTransfer.JoinStage(pair.Client, layer, stage));
      C(world.Until(() => stage.Ready || stage.Left || stage.Failed), "the stage ended");
      C(stage.Ready && pair.ServerEnd.Count(BakedTransfer.RpcLoadFromCache) == 1 && pair.ServerEnd.Count(BakedTransfer.RpcStart) == 0 && pair.ServerEnd.Count(BakedTransfer.RpcPacket) == 0,
        "Ready after one LoadFromCache: no Start, no packet");
      C(BakedLayerStore.Current != null && BakedLayerStore.Current.Id == layer.Id && pair.ClientEnd.Count(BakedTransfer.RpcReady) == 1, "the client read the layer from its cache, made it current, and answered Ready(1)");
      C(DateTime.UtcNow - File.GetLastWriteTimeUtc(BakedCache.PathOf(layer.Id)) < TimeSpan.FromHours(1), "and noted the use of the file");
    }

    // A cache entry that is not what its name says: the client deletes it, leaves, and the server sees it go.
    using (var world = new XferWorld())
    {
      var bytes = (byte[])layer.Bytes.Clone();
      bytes[bytes.Length / 2] ^= 0x5A;
      File.WriteAllBytes(BakedCache.PathOf(layer.Id), bytes);
      var damaged = world.Pair("damaged", scripted: false);
      damaged.Client.CachedIds = [layer.Id];
      var stage = new BakedTransfer.StageResult();
      var lines = LogHandler.During(() =>
      {
        world.Run(BakedTransfer.JoinStage(damaged.Client, layer, stage));
        world.Until(() => stage.Ready || stage.Left || stage.Failed);
      });
      C(stage.Left && !stage.Ready && damaged.Failures.Count == 1 && damaged.Failures[0].Contains("cached baked pieces are corrupted"), "a cache file that is not its id is refused: " + string.Join(" | ", damaged.Failures));
      C(!File.Exists(BakedCache.PathOf(layer.Id)) && BakedLayerStore.Current == null, "its file is deleted and no layer became current");
      C(lines.Any(l => l.Contains("cached baked pieces are corrupted")), "and the log says so");
    }

    // A cache list that names a file the client no longer has.
    using (var world = new XferWorld())
    {
      var gone = world.Pair("gone", scripted: false);
      gone.Client.CachedIds = [layer.Id];
      var stage = new BakedTransfer.StageResult();
      world.Run(BakedTransfer.JoinStage(gone.Client, layer, stage));
      world.Until(() => stage.Ready || stage.Left || stage.Failed);
      C(stage.Left && gone.Failures.Count == 1 && gone.Failures[0].Contains("failed to load"), "a file that is not there fails the client with its reason: " + string.Join(" | ", gone.Failures));
    }
  }

  public static void ServerTransferTroubleTest()
  {
    Section("transfer, join stage 1: a damaged packet, a client that leaves, a lost packet, a wire that stops");
    var layer = ServerValtima(nameof(ServerTransferTroubleTest));
    if (layer == null)
      return;

    // A packet changed on the wire: the client sees its own hash differ, fails with the reason and leaves; the server sees the connection go.
    using (var world = new XferWorld())
    {
      var pair = world.Pair("damaged packet", scripted: false);
      pair.ServerEnd.Filter = XferOnPacket(2, bytes => bytes.Take(bytes.Length - 1).Append((byte)(bytes[^1] ^ 1)).ToArray());
      var stage = new BakedTransfer.StageResult();
      world.Run(BakedTransfer.JoinStage(pair.Client, layer, stage));
      world.Until(() => stage.Ready || stage.Left || stage.Failed);
      C(stage.Left && !stage.Ready && pair.Failures.Count == 1 && pair.Failures[0] == "Better Continents: the baked pieces of this world were corrupted during transfer, please reconnect!",
        "the client fails with the reason on its screen: " + string.Join(" | ", pair.Failures));
      C(BakedLayerStore.Current == null && !File.Exists(BakedCache.PathOf(layer.Id)) && pair.Client.Held == null && pair.Disconnects.Count == 0,
        "no layer became current or was cached, and the server did not have to disconnect anyone");
    }

    // The client leaves in the middle: the sender stops at once, with Left.
    using (var world = new XferWorld())
    {
      var pair = world.Pair("leaves", scripted: true);
      var stage = new BakedTransfer.StageResult();
      world.Run(BakedTransfer.JoinStage(pair.Client, layer, stage));
      C(world.Until(() => pair.Script.Received > 3 * BakedTransfer.SendChunkSize), "three chunks have arrived");
      pair.ClientEnd.Close();
      C(world.Until(() => stage.Ready || stage.Left || stage.Failed, 5f) && stage.Left && !stage.Failed && pair.Disconnects.Count == 0, "the stage ends as Left, and the server hangs up on nobody");
      C(pair.ServerEnd.Count(BakedTransfer.RpcPacket) < XferPacketsOf(layer.Length), $"and no more packets were sent ({pair.ServerEnd.Count(BakedTransfer.RpcPacket)} of {XferPacketsOf(layer.Length)})");
      C(world.Routines.Count == 0, "no coroutine is left running");
    }

    // A packet that never arrives: the server has sent everything and waits for the answer for 120 s, then disconnects the client.
    using (var world = new XferWorld())
    {
      var pair = world.Pair("lost packet", scripted: true);
      pair.ServerEnd.Filter = XferOnPacket(4, _ => null);
      var stage = new BakedTransfer.StageResult();
      world.Run(BakedTransfer.JoinStage(pair.Client, layer, stage));
      C(world.Until(() => pair.ServerEnd.Count(BakedTransfer.RpcPacket) == XferPacketsOf(layer.Length), 30f) && world.Time < 5f, "the server sent all the packets in a few seconds");
      for (int n = 0; n < 5; n++)
        world.Tick();
      C(!stage.Ready && !stage.Left && !stage.Failed && pair.Script.Received < layer.Length, "and waits: the client has not got it all");
      world.Tick(110f);
      C(!stage.Failed && pair.Disconnects.Count == 0, "110 seconds on: it is still waiting (the patience is 120 s)");
      world.Tick(15f);
      C(stage.Failed && !stage.Ready && !stage.Left && pair.Disconnects.SequenceEqual(new[] { "Better Continents: this client did not answer for the baked pieces of the world" }),
        "past 120 s it gives the client up, with a reason: " + string.Join(" | ", pair.Disconnects));
    }

    // A wire that stops taking bytes: two chunks are queued and the sender waits, 30 s for room, then gives up.
    using (var world = new XferWorld())
    {
      var pair = world.Pair("stalled", scripted: true);
      pair.ServerEnd.Stalled = true;
      var stage = new BakedTransfer.StageResult();
      world.Run(BakedTransfer.JoinStage(pair.Client, layer, stage));
      for (int n = 0; n < 20; n++)
        world.Tick();
      C(pair.ServerEnd.Count(BakedTransfer.RpcPacket) == 2 && !stage.Failed, $"with the wire stalled two chunks go in and the sender waits ({pair.ServerEnd.Count(BakedTransfer.RpcPacket)})");
      world.Tick(29f);
      C(!stage.Failed && !stage.Left, "29 seconds on: still waiting for room");
      world.Tick(2f);
      C(stage.Failed && pair.Disconnects.SequenceEqual(new[] { "Better Continents: the baked pieces of this world could not be sent in time" }), "past 30 s the client is disconnected for it: " + string.Join(" | ", pair.Disconnects));
    }
    using (var world = new XferWorld())
    {
      var pair = world.Pair("slow", scripted: true);
      pair.ServerEnd.Stalled = true;
      var stage = new BakedTransfer.StageResult();
      world.Run(BakedTransfer.JoinStage(pair.Client, layer, stage));
      for (int n = 0; n < 5; n++)
        world.Tick();
      world.Tick(20f);
      pair.ServerEnd.Stalled = false;
      C(world.Until(() => stage.Ready || stage.Failed || stage.Left) && stage.Ready && XferSame(pair.Script.Completed[0], pair.Script.Completed[0].Length, layer.Bytes, layer.Length),
        "a wire that wakes in time carries all of it, and the stage is Ready");
    }
  }

  // ------------------------------------------------------------------------------------------------ pushes during play

  // The number of whole layers being sent that the transfer counts (private).
  private static int XferWholePushes() => (int)typeof(BakedTransfer).GetField("wholePushes", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

  public static void ServerTransferPushTest()
  {
    Section("transfer, pushes: a changed layer goes to every client whose join is over, and the operation waits for each answer");
    var layer = ServerValtima(nameof(ServerTransferPushTest));
    if (layer == null)
      return;
    var next = XferRevision(layer, 3);
    using var world = new XferWorld();
    using var store = ServerStore(layer);
    var first = world.Joined("first", layer);
    var second = world.Joined("second", layer);
    var silent = world.Joined("silent", layer);
    silent.Script.Answer = _ => "silent";
    var leaves = world.Joined("leaves", layer);
    leaves.Script.Answer = _ => "silent";
    var joining = world.Pair("still joining");
    joining.Client.Held = layer;
    var gone = world.Joined("gone", layer);
    gone.Close();
    BakedTransfer.PushReport report = null;
    world.Run(BakedTransfer.Push(next, [new ZoneKey(1, 1)], r => report = r));
    // `leaves` hangs up once its first chunk has come.
    C(world.Until(() => leaves.Script.Received > 0), "the first chunk has reached the last client");
    leaves.ClientEnd.Close();
    C(world.Until(() => report != null, 300f, 0.05f), "the push ended");
    var sent = new[] { first, second, silent }.Select(p => p.Script.Starts.Count).ToArray();
    C(BakedLayerStore.Current == next, "the new layer is current on the server");
    C(report.Revision == 3 && report.Targets.Count == 4 && report.Whole == 4 && report.Patches == 0, $"it went to the four connected clients that had joined, whole ({report.Targets.Count})");
    C(sent.All(n => n == 1) && joining.Script.Starts.Count == 0 && joining.ServerEnd.Count(BakedTransfer.RpcStart) == 0 && gone.ServerEnd.Count(BakedTransfer.RpcStart) == 0,
      "one Start each, and none to a client still joining or one that is gone");
    var header = first.Script.Starts[0];
    C(header.Kind == 0 && header.Length == next.Length && header.Revision == 3 && header.Id == next.Id && header.FromId == "" && !header.Joining && header.Changed == null,
      "a whole layer: its length, revision and id, no patch source, not the join's, no changed zones");
    C(first.Script.Completed.Count == 1 && XferSame(first.Script.Completed[0], first.Script.Completed[0].Length, next.Bytes, next.Length)
      && second.Script.Completed.Count == 1 && XferSame(second.Script.Completed[0], second.Script.Completed[0].Length, next.Bytes, next.Length), "the bytes that arrive are the new layer's");
    C(first.Client.AppliedRevision == 3 && ReferenceEquals(first.Client.Held, next) && first.Client.Pending == null && !first.Client.Sending, "a client that answered Applied(3) holds the new layer, and its connection is free");
    var outcomes = report.Targets.ToDictionary(t => t.Name, t => t.Outcome);
    C(outcomes["first"] == BakedTransfer.PushOutcome.Applied && outcomes["second"] == BakedTransfer.PushOutcome.Applied && outcomes["silent"] == BakedTransfer.PushOutcome.TimedOut
      && outcomes["leaves"] == BakedTransfer.PushOutcome.Left, "answered, answered, silent for 30 s, and gone: " + string.Join(", ", outcomes.Select(kv => kv.Key + " " + kv.Value)));
    C(report.Applied == 2 && report.Left == 1 && report.TimedOut.Count() == 1 && report.Bytes == 4L * next.Length, "the report counts them");
    var summary = report.Summary();
    C(summary.Contains("layer revision 3 sent to 4 players; 2 of 4 ready") && summary.Contains("1 left meanwhile") && summary.Contains("no answer in 30 s from silent"), "and says it: " + summary);
    // Silent for 30 s after the last packet, not from the start.
    C(report.Seconds >= 30.0 && report.Seconds < 45.0, $"the operation waited 30 s after the last packet for the silent one ({report.Seconds:F1} s)");
    C(XferWholePushes() == 0 && world.Pairs.All(p => !p.Client.Sending) && world.Routines.Count == 0, "no place for a whole layer or connection is left held, and no coroutine runs");

    // Nobody to send it to.
    BakedTransfer.Clients.Clear();
    BakedTransfer.PushReport none = null;
    world.Run(BakedTransfer.Push(layer, [], r => none = r));
    world.Until(() => none != null, 5f);
    C(none != null && none.Targets.Count == 0 && none.Summary() == "layer revision 1: no player to send it to" && BakedLayerStore.Current == layer, "a push with nobody to send it to says so, and the layer is current all the same");
  }

  public static void ServerTransferLateJoinTest()
  {
    Section("transfer, pushes: a client still joining gets no push, and the layer when its join is over");
    var layer = ServerValtima(nameof(ServerTransferLateJoinTest));
    if (layer == null)
      return;
    var next = XferRevision(layer, 3);
    using var world = new XferWorld();
    using var store = ServerStore(layer);
    var joined = world.Joined("joined", layer);
    var late = world.Pair("late");
    late.Client.Held = layer;
    var report = (BakedTransfer.PushReport)null;
    world.Run(BakedTransfer.Push(next, [], r => report = r));
    C(world.Until(() => report != null) && report.Targets.Count == 1 && late.ServerEnd.Count(BakedTransfer.RpcStart) == 0, "the push goes to the client that has joined only");
    // The client's join ends now: it does not hold the current layer, so the server sends it, outside any operation.
    BakedTransfer.JoinFinished(late.Client);
    C(late.Client.JoinDone && late.Client.Sending && world.Routines.Count == 1, "the end of its join starts a follow-up send, and holds the connection");
    BakedTransfer.JoinFinished(late.Client);
    C(world.Routines.Count == 1, "a second end of join does not start a second send");
    C(world.Until(() => late.Client.AppliedRevision == 3 && !late.Client.Sending) && late.Script.Completed.Count == 1 && late.Script.Starts[0].Kind == 0 && late.Script.Starts[0].Revision == 3 && !late.Script.Starts[0].Joining,
      "the whole layer comes, as a push (not the join's)");
    C(ReferenceEquals(late.Client.Held, next) && late.Client.AppliedRevision == 3 && XferSame(late.Script.Completed[0], late.Script.Completed[0].Length, next.Bytes, next.Length), "and when the client answers Applied it holds the layer");
    C(world.Routines.Count == 0 && joined.Script.Starts.Count == 1, "no coroutine is left, and the other client was sent nothing again");

    // The cases where nothing is sent.
    var upToDate = world.Pair("up to date");
    upToDate.Client.Held = next;
    BakedTransfer.JoinFinished(upToDate.Client);
    var holdsSame = world.Pair("same id");
    holdsSame.Client.Held = BakedLayer.Read((byte[])next.Bytes.Clone(), next.Length);
    BakedTransfer.JoinFinished(holdsSame.Client);
    var away = world.Pair("away");
    away.Client.Held = layer;
    away.Close();
    BakedTransfer.JoinFinished(away.Client);
    var busy = world.Pair("busy");
    busy.Client.Held = layer;
    busy.Client.Sending = true;
    BakedTransfer.JoinFinished(busy.Client);
    C(world.Routines.Count == 0 && upToDate.Client.JoinDone && holdsSame.Client.JoinDone && away.Client.JoinDone && busy.Client.JoinDone, "a client that holds the current layer, one that has left, and one whose connection is busy are sent nothing (and their join is over)");
    // No layer at all.
    BakedLayerStore.Set(null, null);
    var noLayer = world.Pair("no layer");
    BakedTransfer.JoinFinished(noLayer.Client);
    C(world.Routines.Count == 0 && noLayer.Client.JoinDone, "with no layer there is nothing to send");
    BakedLayerStore.Set(layer, null);
  }

  public static void ServerTransferNeedFullTest()
  {
    Section("transfer, pushes: a client that cannot use what it was sent asks for the whole layer, and gets it");
    var layer = ServerValtima(nameof(ServerTransferNeedFullTest));
    if (layer == null)
      return;
    var next = XferRevision(layer, 4);
    using var world = new XferWorld();
    using var store = ServerStore(layer);
    var picky = world.Joined("picky", layer);
    int asked = 0;
    picky.Script.Answer = header => asked++ == 0 ? "needfull" : "applied";
    var easy = world.Joined("easy", layer);
    BakedTransfer.PushReport report = null;
    var lines = LogHandler.During(() =>
    {
      world.Run(BakedTransfer.Push(next, [], r => report = r));
      world.Until(() => report != null);
    });
    C(report != null && report.Applied == 2 && picky.Script.Starts.Count == 2 && easy.Script.Starts.Count == 1, $"it asked once, was sent the layer again from the start, and answered ({picky.Script.Starts.Count} Starts)");
    C(picky.Script.Starts.All(h => h.Kind == 0) && picky.Script.Completed.Count == 2 && picky.Script.Completed.All(c => XferSame(c, c.Length, next.Bytes, next.Length)), "each time the whole layer, and complete");
    C(!picky.Client.NeedsFull && ReferenceEquals(picky.Client.Held, next) && picky.Client.AppliedRevision == 4 && report.Targets.Single(t => t.Name == "picky").Outcome == BakedTransfer.PushOutcome.Applied, "afterwards it holds the layer and the flag is down");
    C(lines.Any(l => l.Contains("asks for the whole baked layer")), "and the log says it asked");
    C(XferWholePushes() == 0 && world.Routines.Count == 0 && !picky.Client.Sending, "no place is left held");
  }

  public static void ServerTransferFourAtATimeTest()
  {
    Section("transfer, pushes: four whole layers at a time, one send at a time on a connection");
    var layer = ServerValtima(nameof(ServerTransferFourAtATimeTest));
    if (layer == null)
      return;
    var next = XferRevision(layer, 5);
    using var world = new XferWorld();
    using var store = ServerStore(layer);
    var pairs = Enumerable.Range(0, 7).Select(n => world.Joined("client " + n, layer)).ToList();
    // The last one is busy with a follow-up when the push starts: its send waits for that to end.
    pairs[6].Client.Sending = true;
    BakedTransfer.PushReport report = null;
    world.Run(BakedTransfer.Push(next, [], r => report = r));
    int most = 0, mostHeld = 0;
    int[] startedAt = new int[pairs.Count];
    bool released = false;
    while (report == null && world.Time < 300f)
    {
      world.Tick();
      int active = pairs.Count(p => p.Script.Active);
      most = Math.Max(most, active);
      mostHeld = Math.Max(mostHeld, XferWholePushes());
      for (int i = 0; i < pairs.Count; i++)
        if (startedAt[i] == 0 && pairs[i].Script.Starts.Count > 0)
          startedAt[i] = world.Ticks;
      if (!released && world.Ticks == 60)
      {
        released = true;
        C(pairs[6].Script.Starts.Count == 0 && pairs[6].Client.Sending, "the busy connection has been sent nothing yet");
        pairs[6].Client.Sending = false;
      }
    }
    C(report != null && report.Whole == 7 && report.Applied == 7, "all seven were sent the layer and answered");
    C(most == 4 && mostHeld == 4, $"never more than four downloads at once, and four ran at once ({most} downloads, {mostHeld} places held)");
    C(startedAt[4] > startedAt[0] && startedAt[5] > startedAt[1] && startedAt[0] <= 5 && startedAt[3] <= 5 && startedAt[4] > 20, $"the fifth and sixth wait for a place: the first four began by tick {startedAt[3]}, the fifth at {startedAt[4]}");
    C(startedAt[6] >= 60, "and the busy connection's push began only after its follow-up ended");
    C(XferWholePushes() == 0 && pairs.All(p => !p.Client.Sending) && pairs.All(p => ReferenceEquals(p.Client.Held, next)), "no place is left held; every client holds the new layer");
  }

  public static void ServerTransferLocalSideTest()
  {
    Section("transfer, pushes: the machine that runs the world waits for its own client side, and a client answers by itself when none is there");
    var layer = ServerValtima(nameof(ServerTransferLocalSideTest));
    if (layer == null)
      return;
    var next = XferRevision(layer, 6);
    using var world = new XferWorld();
    using var store = ServerStore(layer);
    BakedTransfer.RunsWorld = () => true;
    BakedTransfer.ClientReports = true;
    BakedTransfer.PushReport report = null;
    world.Run(BakedTransfer.Push(next, [], r => report = r));
    for (int n = 0; n < 200; n++)
      world.Tick();
    C(report == null, "with a client side that has not reported, the operation waits");
    BakedTransfer.ReportApplied(6);
    world.Until(() => report != null, 5f);
    C(report != null && report.LocalSide && report.LocalApplied && BakedTransfer.LocalApplied == 6 && report.Summary().Contains("sent to 0 players"), "and ends when it reports, without a RPC (it is the machine that runs the world)");

    // Never reports: 30 s.
    var again = XferRevision(layer, 7);
    BakedTransfer.PushReport late = null;
    world.Run(BakedTransfer.Push(again, [], r => late = r));
    world.Until(() => late != null, 60f, 0.5f);
    C(late != null && late.LocalSide && !late.LocalApplied && late.Seconds >= 30.0 && late.Seconds < 32.0 && late.Summary().Contains("this machine's own view did not report in 30 s"), $"a client side that never reports is given 30 s ({late?.Seconds:F1} s) and the output says so");

    // No client side (a dedicated server, a machine with no graphics): the operation does not wait.
    BakedTransfer.ClientReports = false;
    var third = XferRevision(layer, 8);
    BakedTransfer.PushReport quick = null;
    world.Run(BakedTransfer.Push(third, [], r => quick = r));
    world.Until(() => quick != null, 5f);
    C(quick != null && !quick.LocalSide && quick.Seconds < 1.0, "where no client side runs it does not wait at all");
  }

  public static void ServerTransferClientTest()
  {
    Section("transfer, client side: Applied goes to the server by itself where nothing rebuilds, and what the client does with a bad transfer");
    var layer = ServerValtima(nameof(ServerTransferClientTest));
    if (layer == null)
      return;
    var next = XferRevision(layer, 3);

    // A push to the real client with no client side to wait for: it answers Applied(3) at once, and the old layer's file goes.
    using (var world = new XferWorld())
    {
      var pair = world.Joined("pushed", layer, scripted: false);
      BakedCache.Add(layer.Id, layer.Bytes, layer.Length);
      BakedCache.Replaced(layer.Id);
      BakedPush(world, pair, next);
      C(world.Until(() => pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 1 && pair.Client.AppliedRevision == 3, 60f), "the client answered Applied(3)");
      C(pair.ClientEnd.Count(BakedTransfer.RpcReady) == 0 && BakedLayerStore.Current != null && BakedLayerStore.Current.Id == next.Id, "(not Ready: that is the join's), and the layer is current");
      C(File.Exists(BakedCache.PathOf(next.Id)) && !File.Exists(BakedCache.PathOf(layer.Id)), "the new layer is cached and the file of the revision before it is deleted");
      C(world.Until(() => ReferenceEquals(pair.Client.Held, next) || pair.Client.Held != null && pair.Client.Held.Id == next.Id, 5f), "and the server holds it for the client");
    }

    // With a client side that reports (BakedClient), the answer waits for it.
    using (var world = new XferWorld())
    {
      BakedTransfer.ClientReports = true;
      var pair = world.Joined("rebuilding", layer, scripted: false);
      BakedPush(world, pair, next);
      C(world.Until(() => BakedLayerStore.Current != null && BakedLayerStore.Current.Id == next.Id, 60f), "the layer is current");
      for (int n = 0; n < 20; n++)
        world.Tick();
      C(pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 0, "but the client has not said Applied: its zones are not rebuilt");
      BakedTransfer.ReportApplied(3);
      world.Tick();
      world.Tick();
      C(pair.ClientEnd.Count(BakedTransfer.RpcApplied) == 1 && pair.Client.AppliedRevision == 3, "BakedClient's report sends it");
    }

    // A transfer in a form this client does not know, a packet outside the transfer, a packet with nothing before it.
    using (var world = new XferWorld())
    {
      var pair = world.Pair("odd", scripted: false);
      var header = new BakedTransfer.LayerHeader { Kind = 0, Length = -5, Hash = 0, Revision = 1, Id = "x" };
      pair.ServerRpc.Invoke(BakedTransfer.RpcStart, header.ToPackage());
      world.Tick();
      world.Tick();
      C(pair.Failures.Count == 1 && pair.Failures[0].Contains("in a form this version does not know"), "a header with a negative length fails the client: " + string.Join(" | ", pair.Failures));
    }
    using (var world = new XferWorld())
    {
      var pair = world.Pair("outside", scripted: false);
      var packet = new byte[10];
      var header = new BakedTransfer.LayerHeader { Kind = 0, Length = 100, Hash = 1, Revision = 1, Id = "x" };
      pair.ServerRpc.Invoke(BakedTransfer.RpcStart, header.ToPackage());
      pair.ServerRpc.Invoke(BakedTransfer.RpcPacket, 95, BC.ZNetPatch.GetHashCode(packet), new ZPackage(packet));
      world.Tick();
      world.Tick();
      C(pair.Failures.Count == 1 && pair.Failures[0].Contains("corrupted during transfer"), "a packet that runs past the end of the transfer fails the client: " + string.Join(" | ", pair.Failures));
    }
    using (var world = new XferWorld())
    {
      var pair = world.Pair("early", scripted: false);
      var packet = new byte[10];
      pair.ServerRpc.Invoke(BakedTransfer.RpcPacket, 0, BC.ZNetPatch.GetHashCode(packet), new ZPackage(packet));
      world.Tick();
      world.Tick();
      C(pair.Failures.Count == 0 && pair.Client.Held == null, "a packet before any Start is ignored");
    }
    // A layer whose bytes are right in every packet, but are not a layer: the client fails with the reason and leaves.
    using (var world = new XferWorld())
    {
      var pair = world.Pair("nonsense", scripted: false);
      var junk = new byte[3000];
      new System.Random(3).NextBytes(junk);
      var header = new BakedTransfer.LayerHeader { Kind = 0, Length = junk.Length, Hash = BC.ZNetPatch.GetHashCode(junk, junk.Length), Revision = 1, Id = BakedCache.IdOf(junk, junk.Length) };
      pair.ServerRpc.Invoke(BakedTransfer.RpcStart, header.ToPackage());
      pair.ServerRpc.Invoke(BakedTransfer.RpcPacket, 0, BC.ZNetPatch.GetHashCode(junk), new ZPackage(junk));
      world.Until(() => pair.Failures.Count > 0, 30f);
      C(pair.Failures.Count == 1 && pair.Failures[0].Contains("failed to load") && BakedLayerStore.Current == null && !File.Exists(BakedCache.PathOf(header.Id)),
        "bytes that are not a layer fail the client with a reason, and nothing is cached: " + string.Join(" | ", pair.Failures));
    }
    // A layer whose id is not the header's.
    using (var world = new XferWorld())
    {
      var pair = world.Pair("other id", scripted: false);
      var header = new BakedTransfer.LayerHeader { Kind = 0, Length = layer.Length, Hash = BC.ZNetPatch.GetHashCode(layer.Bytes, layer.Length), Revision = 1, Id = "00000000000000000000000000000000", Joining = true };
      pair.ServerRpc.Invoke(BakedTransfer.RpcStart, header.ToPackage());
      for (int at = 0; at < layer.Length; at += BakedTransfer.SendChunkSize)
      {
        var chunk = layer.Bytes.Skip(at).Take(Math.Min(BakedTransfer.SendChunkSize, layer.Length - at)).ToArray();
        pair.ServerRpc.Invoke(BakedTransfer.RpcPacket, at, BC.ZNetPatch.GetHashCode(chunk), new ZPackage(chunk));
      }
      world.Until(() => pair.Failures.Count > 0, 30f);
      C(pair.Failures.Count == 1 && pair.Failures[0].Contains("their id differs") && BakedLayerStore.Current == null, "a layer that is not the one the header names fails the client: " + string.Join(" | ", pair.Failures));
    }
    // A session that ends while the worker reads the layer: nothing is installed in the next one.
    using (var world = new XferWorld())
    {
      var pair = world.Pair("old session", scripted: false);
      var stage = new BakedTransfer.StageResult();
      world.Run(BakedTransfer.JoinStage(pair.Client, layer, stage));
      // The join's coroutine, and then the client's own one that reads the layer on a worker.
      C(world.Until(() => world.Routines.Count == 2, 30f), "the client has all of it and is reading it");
      BakedTransfer.SessionStarts(false);
      for (int n = 0; n < 300; n++)
        world.Tick();
      C(BakedLayerStore.Current == null && pair.Failures.Count == 0 && !stage.Ready, "a layer that arrived for the session before is not made current in this one, and does not fail it");
    }
  }

  // A push of `next` to one client, as Push does it, without the operation around it: the whole layer goes out straight away.
  private static void BakedPush(XferWorld world, XferPair pair, BakedLayer next)
  {
    pair.Client.Pending = next;
    pair.Client.SentRevision = next.Revision;
    var sent = new BakedTransfer.SendResult();
    world.Run(BakedTransfer.SendLayer(pair.Client, next, kind: 0, next.Bytes, next.Length, fromId: "", changed: null, joining: false, sent));
  }

  public static void ServerTransferBroadcastTest()
  {
    Section("transfer: the settings sent again at a GameTerrain conversion go to the clients whose join is over");
    var layer = ServerValtima(nameof(ServerTransferBroadcastTest));
    if (layer == null)
      return;
    using var world = new XferWorld();
    var a = world.Joined("a", layer);
    var b = world.Pair("b");
    var closed = world.Joined("closed", layer);
    closed.Close();
    var d = world.Joined("d", layer);
    int sent = BakedTransfer.BroadcastSettings(new ZPackage(new byte[] { 1, 2, 3 }));
    C(sent == 2 && a.ServerEnd.Count(BakedTransfer.RpcSettingsUpdate) == 1 && d.ServerEnd.Count(BakedTransfer.RpcSettingsUpdate) == 1 && b.ServerEnd.Count(BakedTransfer.RpcSettingsUpdate) == 0 && closed.ServerEnd.Count(BakedTransfer.RpcSettingsUpdate) == 0,
      $"two clients heard it: the two that had joined and are connected ({sent})");
  }

  // ------------------------------------------------------------------------------------------------ patches (need slice A's BakedPatch and LayerEdit)

  // The layer one edit makes from VALtima's, and the zones that changed; null (and a line saying so) when slice A's code is not here yet.
  private static (BakedLayer Next, ZoneKey[] Changed)? XferEdited(BakedLayer layer, Action<LayerEdit> change, string test)
  {
    try
    {
      var edit = layer.Edit();
      change(edit);
      var built = edit.Build();
      // The patch itself: slice A's BakedPatch.
      BakedPatch.Make(layer, built.Layer, built.Changed);
      return built;
    }
    catch (NotImplementedException)
    {
      System.Console.WriteLine($"skipped: BakedPatch not merged ({test}: LayerEdit and BakedPatch throw NotImplementedException on this branch)");
      return null;
    }
  }

  public static void ServerTransferPatchTest()
  {
    Section("transfer, pushes: a client that holds the revision before gets a patch of the changed zones, and the whole layer when the patch is no good");
    var layer = ServerValtima(nameof(ServerTransferPatchTest));
    if (layer == null)
      return;
    // One door out of VALtima's thousand: a small change in one zone.
    var door = layer.Decode(layer.Zones.First(z => z.Live > 0)).Records().First(r => layer.Palette[r.Palette].Role == BakedRole.Live);
    var small = XferEdited(layer, edit => edit.RemoveRecords([door]), nameof(ServerTransferPatchTest));
    if (small == null)
      return;
    var (next, changed) = small.Value;
    C(next.Revision == layer.Revision + 1 && changed.Length == 1, $"(the edit made revision {next.Revision} and changed {changed.Length} zone)");
    using var store = ServerStore(layer);
    using var world = new XferWorld();
    var holder = world.Joined("holder", layer);
    var stranger = world.Joined("stranger", null);
    BakedTransfer.PushReport report = null;
    world.Run(BakedTransfer.Push(next, changed, r => report = r));
    C(world.Until(() => report != null), "the push ended");
    C(report.Patches == 1 && report.Whole == 1 && report.Applied == 2, $"one client got a patch and the other (which holds nothing) the whole layer ({report.Patches} patches, {report.Whole} whole)");
    var header = holder.Script.Starts.Single();
    C(header.Kind == 1 && header.FromId == layer.Id && header.Id == next.Id && header.Revision == next.Revision && !header.Joining && header.Changed != null && header.Changed.SequenceEqual(changed), "the patch's header: kind 1, from the old id to the new, with the changed zones");
    C(header.Length < next.Length / 10 && stranger.Script.Starts.Single().Kind == 0 && stranger.Script.Starts.Single().Length == next.Length, $"the patch is small ({header.Length:N0} bytes of {next.Length:N0})");
    // What the client does with it is what the server checked: the patch applied to the old layer gives the new one.
    var applied = BakedPatch.Apply(layer, holder.Script.Completed.Single());
    C(applied.Id == next.Id && XferSame(applied.Bytes, applied.Length, next.Bytes, next.Length), "the patch applied to the old layer is the new layer, byte for byte");
    C(holder.Client.AppliedRevision == next.Revision && ReferenceEquals(holder.Client.Held, next) && XferWholePushes() == 0, "and the holder holds the new layer");

    // The real client with the old layer current: the patch is applied on a worker and the layer is current.
    using (var real = new XferWorld())
    {
      var pair = real.Joined("real", layer, scripted: false);
      var patch = BakedPatch.Make(layer, next, changed);
      pair.Client.Pending = next;
      var sent = new BakedTransfer.SendResult();
      real.Run(BakedTransfer.SendLayer(pair.Client, next, kind: 1, patch, patch.Length, fromId: layer.Id, changed, joining: false, sent));
      C(real.Until(() => pair.Client.AppliedRevision == next.Revision, 60f) && BakedLayerStore.Current != null && BakedLayerStore.Current.Id == next.Id, "the real client applied the patch to its layer, made the result current, and answered Applied");
      C(File.Exists(BakedCache.PathOf(next.Id)) && pair.ClientEnd.Count(BakedTransfer.RpcNeedFull) == 0, "and cached it, without asking for more");
    }

    // A patch from a layer the client does not hold: NeedFull.
    using (var real = new XferWorld())
    {
      var pair = real.Joined("wrong base", layer, scripted: false);
      var patch = BakedPatch.Make(layer, next, changed);
      pair.Client.Pending = next;
      var sent = new BakedTransfer.SendResult();
      real.Run(BakedTransfer.SendLayer(pair.Client, next, kind: 1, patch, patch.Length, fromId: "00000000000000000000000000000000", changed, joining: false, sent));
      C(real.Until(() => pair.Client.NeedsFull, 60f) && pair.ClientEnd.Count(BakedTransfer.RpcNeedFull) == 1 && pair.Failures.Count == 0 && pair.Client.Held == null, "a patch from another layer than the client's: it asks for the whole layer, and does not leave");
    }

    // A scripted client that asks for the whole layer when it is sent a patch.
    using (var again = new XferWorld())
    {
      var picky = again.Joined("picky", layer);
      picky.Script.Answer = h => h.Kind == 1 ? "needfull" : "applied";
      BakedTransfer.PushReport second = null;
      BakedLayerStore.Set(layer, null);
      again.Run(BakedTransfer.Push(next, changed, r => second = r));
      C(again.Until(() => second != null) && picky.Script.Starts.Select(h => h.Kind).SequenceEqual(new[] { 1, 0 }) && second.Applied == 1, "NeedFull for a patch: the whole layer follows it, and that is answered");
      C(picky.Script.Completed.Count == 2 && XferSame(picky.Script.Completed[1], picky.Script.Completed[1].Length, next.Bytes, next.Length), "complete");
    }

    // A change over half of the layer is not worth a patch.
    var big = XferEdited(layer, edit => edit.RemoveWhere(null, r => r.Palette % 2 == 0), nameof(ServerTransferPatchTest));
    var (bigNext, bigChanged) = big.Value;
    using (var heavy = new XferWorld())
    {
      var holds = heavy.Joined("holds", layer);
      BakedLayerStore.Set(layer, null);
      BakedTransfer.PushReport third = null;
      heavy.Run(BakedTransfer.Push(bigNext, bigChanged, r => third = r));
      heavy.Until(() => third != null);
      C(third.Patches == 0 && holds.Script.Starts.Single().Kind == 0, $"a change of {bigChanged.Length} zones is sent as the whole layer");
    }
  }

  // ------------------------------------------------------------------------------------------------ the order of the stages in SendSettings

  public static void ServerTransferStageOrderTest()
  {
    Section("transfer: the join's stages run in the order of the spec: settings, the baked layer, the alt biomes, PeerInfo");
    var machine = typeof(BC.ZNetPatch).GetNestedTypes(BindingFlags.NonPublic).First(t => t.Name.StartsWith("<SendSettings>"));
    var moveNext = machine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    var instructions = PatchProcessor.GetOriginalInstructions(moveNext);
    int Call(string type, string name, bool last = false)
    {
      int found = -1;
      for (int i = 0; i < instructions.Count; i++)
        if ((instructions[i].opcode == OpCodes.Call || instructions[i].opcode == OpCodes.Callvirt) && instructions[i].operand is MethodBase m && m.Name == name && m.DeclaringType?.Name == type)
        {
          found = i;
          if (!last)
            break;
        }
      return found;
    }
    int settings = Call("BakedTransfer", "SendPackets"), layerStage = Call("BakedTransfer", "JoinStage"), altBiomes = Call("AltBiomeControl", "BuildServerAssignmentPackage"),
      finished = Call("BakedTransfer", "JoinFinished"), peerInfo = Call("Action", "Invoke", last: true);
    C(settings >= 0 && layerStage >= 0 && altBiomes >= 0 && finished >= 0, $"SendSettings calls SendPackets ({settings}), JoinStage ({layerStage}), BuildServerAssignmentPackage ({altBiomes}) and JoinFinished ({finished})");
    C(settings < layerStage && layerStage < altBiomes && altBiomes < finished, "in that order: the settings, stage 1, the alt-biome placement, the end of the join");
    C(peerInfo > finished, $"and PeerInfo goes after JoinFinished ({peerInfo} > {finished})");
    // The layer stage is a loop over the coroutine (it yields to the game's frames), and a failed stage ends the join.
    C(instructions.Count(i => i.operand is MethodBase m && m.Name == "JoinStage") == 1 && instructions.Count(i => i.operand is MethodBase m && m.Name == "JoinFinished") == 1, "each once");
  }

  // ------------------------------------------------------------------------------------------------ the client's cache

  public static void ServerTransferCacheTest()
  {
    Section("transfer: the client's cache of layers (.bcl): written whole, listed, used, replaced and pruned");
    using var world = new XferWorld();
    var one = new byte[5000];
    var two = new byte[7000];
    new System.Random(1).NextBytes(one);
    new System.Random(2).NextBytes(two);
    string idOne = BakedCache.IdOf(one, one.Length), idTwo = BakedCache.IdOf(two, two.Length);
    C(idOne.Length == 32 && idOne == idOne.ToLowerInvariant() && idOne != idTwo && idOne == WorldCache.PackageID(one, one.Length), "a layer's id is a settings package's: 32 lower-case hex digits of its SHA-512");
    C(BakedCache.Ids().Count == 0 && !BakedCache.Exists(idOne), "an empty cache lists nothing");
    BakedCache.Add(idOne, one, one.Length);
    BakedCache.Add(idTwo, two, 6000);
    C(BakedCache.Exists(idOne) && File.ReadAllBytes(BakedCache.PathOf(idOne)).SequenceEqual(one) && new FileInfo(BakedCache.PathOf(idTwo)).Length == 6000, "a file is written under its id, as long as it is told (the first length bytes)");
    C(!Directory.GetFiles(world.Folder, "*.tmp").Any() && Path.GetExtension(BakedCache.PathOf(idOne)) == ".bcl", "no .tmp is left, and the extension is .bcl");
    BakedCache.Add(idOne, one, one.Length);
    C(BakedCache.Ids().OrderBy(x => x).SequenceEqual(new[] { idOne, idTwo }.OrderBy(x => x)), "a layer added again is replaced, not doubled; the list has both ids");
    File.WriteAllBytes(Path.Combine(world.Folder, idOne + ".bcl.tmp"), [1]);
    File.WriteAllBytes(Path.Combine(world.Folder, "aabbcc.bc"), [1]);
    C(BakedCache.Ids().Count == 2, "a .tmp file and a settings package (.bc) are not layers");
    var listed = new ZPackage(WorldCache.SerializeCacheList().GetArray());
    int count = listed.ReadInt();
    var names = Enumerable.Range(0, count).Select(_ => listed.ReadString()).ToList();
    C(names.Contains(idOne) && names.Contains(idTwo) && names.Contains("aabbcc") && count == 3, "the handshake's list has the settings packages and the layers");

    // Use and age.
    File.SetLastWriteTimeUtc(BakedCache.PathOf(idOne), DateTime.UtcNow.AddDays(-40));
    File.SetLastWriteTimeUtc(BakedCache.PathOf(idTwo), DateTime.UtcNow.AddDays(-40));
    BakedCache.Load(idTwo);
    C(DateTime.UtcNow - File.GetLastWriteTimeUtc(BakedCache.PathOf(idTwo)) < TimeSpan.FromMinutes(1), "a file that is read is touched");
    C(BakedCache.Prune(DateTime.UtcNow) == 1 && !BakedCache.Exists(idOne) && BakedCache.Exists(idTwo), "a file nobody used for 30 days is deleted, and one used lately is not");
    File.SetLastWriteTimeUtc(BakedCache.PathOf(idTwo), DateTime.UtcNow.AddDays(-29));
    C(BakedCache.Prune(DateTime.UtcNow) == 0 && BakedCache.Exists(idTwo), "29 days old stays");
    BakedCache.ForgetPruned();
    File.SetLastWriteTimeUtc(BakedCache.PathOf(idTwo), DateTime.UtcNow.AddDays(-45));
    C(BakedCache.PruneOnce() == 1 && BakedCache.PruneOnce() == 0, "the first handshake of a session prunes, the later ones do not");

    // The file of the revision before goes when a newer one is stored.
    BakedCache.Add(idOne, one, one.Length);
    BakedCache.Add(idTwo, two, two.Length);
    BakedCache.SessionStarts();
    BakedCache.Replaced(idOne);
    C(BakedCache.Exists(idOne) && BakedCache.Exists(idTwo), "the first layer of a session replaces nothing");
    BakedCache.Replaced(idOne);
    C(BakedCache.Exists(idOne), "the same layer again does not delete itself");
    BakedCache.Replaced(idTwo);
    C(!BakedCache.Exists(idOne) && BakedCache.Exists(idTwo), "the next layer deletes the one before it");
    BakedCache.Delete(idTwo);
    BakedCache.Delete(idTwo);
    C(!BakedCache.Exists(idTwo), "a file can be deleted, also when it is gone");
  }
}
