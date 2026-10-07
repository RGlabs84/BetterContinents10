// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using ZNetPatch = BetterContinents.BetterContinents.ZNetPatch;

namespace BetterContinents;

// How the baked layer reaches a player (build spec 5). The layer is not in the settings package a joining client downloads (the package
// says "this world has a layer, sent apart"), so a bake leaves that package, and every client's cached copy of it, as they were.
//
//  * At join it is stage 1, after the settings (stage 0) and before the alt-biome placement and PeerInfo: LoadFromCache when the client's
//    handshake lists the layer's id, otherwise Start and the packets, through the loop the settings use (SendPackets), and the server
//    waits for Ready(1). The client reads the cache entry or the download on a worker, checks the layer, stores a download in its cache
//    (BakedCache), makes it current, and answers.
//  * During play a change of the layer is pushed (Push): to every client whose join is over, as a patch of the changed zones when it holds
//    the revision before (checked here first: the patch applied to that revision must give the new layer's id), else as the whole layer,
//    four whole layers at a time. A client answers Applied(revision) when the zones inside its collider ring have their new colliders
//    (BakedClient reports it through ReportApplied); a patch that does not give the server's bytes is answered with NeedFull and gets the
//    whole layer. The operation that pushed waits for every client to answer, to leave, or to time out 30 s after its last packet, and
//    for this machine's own client side when it has one.
//  * A client still joining gets no push. When its join is over and it does not hold the current revision, it gets the layer then.
//
// The offline tests (tools/placement-tests) drive all of it over real ZRpc objects on made-up sockets: the connection is an IBakedLink,
// the clock, the coroutines and the machine's role are the seams below.
internal static class BakedTransfer
{
  // ---- the RPCs (build spec 5.1) ----------------------------------------------------------------------------------------------------

  internal const string RpcLoadFromCache = "BetterContinentsLayerLoadFromCache";   // server to client: (string id, int revision)
  internal const string RpcStart = "BetterContinentsLayerStart";                   // server to client: (ZPackage header, see LayerHeader)
  internal const string RpcPacket = "BetterContinentsLayerPacket";                 // server to client: (int offset, int packetHash, ZPackage bytes)
  internal const string RpcReady = "BetterContinentsReady";                        // client to server: (int stage): 0 settings, 1 layer
  internal const string RpcApplied = "BetterContinentsLayerApplied";               // client to server: (int revision)
  internal const string RpcNeedFull = "BetterContinentsLayerNeedFull";             // client to server: (int revision)
  internal const string RpcSettingsUpdate = "BetterContinentsSettingsUpdate";      // server to client: (ZPackage settings), at a GameTerrain conversion

  /// <summary>The packets of a transfer: 128 KiB, up to two in flight, the settings' own numbers.</summary>
  internal const int SendChunkSize = 128 * 1024;

  /// <summary>A transfer that moves no byte for this long is given up, and a push waits this long after its last packet for the answer.</summary>
  internal const float TimeoutSeconds = 30f;

  /// <summary>Whole-layer pushes that run at once (build spec 5.3).</summary>
  internal const int MaxWholePushes = 4;

  /// <summary>A patch of more than this share of the layer is not worth it: the whole layer is sent.</summary>
  internal const double PatchShareLimit = 0.5;

  // ---- the seams (the offline tests stand in for them) -------------------------------------------------------------------------------

  /// <summary>The clock the waits are made by, in seconds: the game's time.</summary>
  internal static Func<float> Clock = () => Time.time;

  /// <summary>Starts a coroutine that outlives a connection's own (a follow-up push, a client's receive).</summary>
  internal static Func<IEnumerator, object?> StartRoutine = routine => BetterContinents.instance.StartCoroutine(routine);

  /// <summary>Whether this machine runs the world (single player, a host, a dedicated server): it has the authority over the layer.</summary>
  internal static Func<bool> RunsWorld = () => ZNet.instance != null && ZNet.instance.IsServer();

  /// <summary>Set by BakedClient while its manager runs (build spec 7.6: where the machine is not a dedicated server and its graphics device
  /// is not Null, once a world is loaded): it reports Applied through ReportApplied after every change of the layer, so a push waits for
  /// it on the machine that runs the world, and a client leaves the answer to it. When it is not set nothing is there to rebuild (the
  /// loading screen, a dedicated server, a machine with no graphics): a client answers by itself and a push does not wait.</summary>
  internal static volatile bool ClientReports;

  // ---- one connection ----------------------------------------------------------------------------------------------------------------

  /// <summary>What the transfer code needs of a connection: ZRpc and its socket, or a stand-in.</summary>
  internal interface IBakedLink
  {
    bool Connected { get; }
    void Invoke(string method, params object[] parameters);
    /// <summary>The bytes the socket still has to send.</summary>
    int SendQueueSize { get; }
    void Flush();
    float Now { get; }
    /// <summary>The Steam send rate raised for this connection while a layer goes over it (the Settings Transfer Rate). False: not changed.</summary>
    bool RaiseSendRate(string what, int length);
    void RestoreSendRate(string why);
  }

  internal sealed class ZRpcLink(ZRpc rpc) : IBakedLink
  {
    public bool Connected => rpc.IsConnected();
    public void Invoke(string method, params object[] parameters) => rpc.Invoke(method, parameters);
    public int SendQueueSize => rpc.GetSocket().GetSendQueueSize();

    public void Flush()
    {
      try
      {
        rpc.GetSocket().Flush();
      }
      catch (NotImplementedException)
      {
        // ZPlayFabSocket doesn't implement it and throws instead
      }
    }

    public float Now => Clock();
    public bool RaiseSendRate(string what, int length) => ZNetPatch.RaiseSendRate(rpc, what, length);
    public void RestoreSendRate(string why) => ZNetPatch.RestoreSteamSendRate(rpc, why);
  }

  /// <summary>How a send ended.</summary>
  internal sealed class SendResult
  {
    public bool Done;
    public bool Left;
    public bool TimedOut;
    public int Sent;
    /// <summary>When the last packet went into the socket, by the link's clock.</summary>
    public float LastPacketAt;
  }

  /// <summary>The packets of one transfer, one frame each while the socket has two chunks queued: the loop the settings go through
  /// (ZNetPatch.SendSettings) and the layer goes through. Ends with Done, with Left (the connection closed), or with TimedOut (a chunk
  /// has waited 30 s for room).</summary>
  internal static IEnumerator SendPackets(IBakedLink link, byte[] data, int length, string packetRpc, string what, SendResult result)
  {
    int nextProgressLogAt = Mathf.Max(length / 10, 1);
    for (int sentBytes = 0; sentBytes < length;)
    {
      if (!link.Connected)
      {
        result.Left = true;
        yield break;
      }
      int packetSize = Mathf.Min(length - sentBytes, SendChunkSize);
      var packet = new byte[packetSize];
      Buffer.BlockCopy(data, sentBytes, packet, 0, packetSize);
      link.Invoke(packetRpc, sentBytes, ZNetPatch.GetHashCode(packet), new ZPackage(packet));
      // Make sure to flush or we will saturate the queue...
      link.Flush();
      sentBytes += packetSize;
      result.Sent = sentBytes;
      result.LastPacketAt = link.Now;
      if (sentBytes >= nextProgressLogAt || sentBytes == length)
      {
        BetterContinents.Log($"Sent {sentBytes} of {length} bytes{what}");
        nextProgressLogAt += Mathf.Max(length / 10, 1);
      }
      float timeout = link.Now + TimeoutSeconds;
      // One chunk a frame at most (about 4 MB/s, the settings' own pace), and up to two chunks in flight (Steam's own send buffer is
      // 512 KiB) instead of draining to one.
      yield return null;
      while (link.SendQueueSize >= 2 * SendChunkSize && link.Now <= timeout && link.Connected)
        yield return null;
      if (!link.Connected)
      {
        result.Left = true;
        yield break;
      }
      if (link.Now > timeout)
      {
        result.TimedOut = true;
        yield break;
      }
    }
    result.Done = true;
  }

  // ---- what the server keeps of each client --------------------------------------------------------------------------------------------

  internal sealed class LayerClient(string name, IBakedLink link, Action<string> disconnect)
  {
    /// <summary>How the client is named in the logs and the operations' output: "id (player)" once the join has read them.</summary>
    public string Name { get; set; } = name;
    /// <summary>The Better Continents version the client sent in its handshake; null when it sent none.</summary>
    public string? Version { get; set; }
    public readonly IBakedLink Link = link;
    /// <summary>Ends the connection with a reason the client's screen shows (an Error and ZNet.Disconnect in the game).</summary>
    public readonly Action<string> Disconnect = disconnect;
    /// <summary>The ids in the client's cache, from its handshake (settings packages and layers).</summary>
    public HashSet<string> CachedIds = [];
    /// <summary>Stage 1 answered: the client has the layer the join sent.</summary>
    public bool LayerReady;
    /// <summary>The join is over (SendSettings let PeerInfo through): pushes reach the client from now.</summary>
    public bool JoinDone;
    /// <summary>The layer the client is known to hold; null until it has answered one.</summary>
    public BakedLayer? Held;
    /// <summary>The layer being sent to it, which it holds once it answers Applied (or Ready for the join's).</summary>
    public BakedLayer? Pending;
    /// <summary>The revision of what it was last sent, 0 before any.</summary>
    public uint SentRevision;
    /// <summary>The highest revision it has answered Applied for, -1 before any.</summary>
    public long AppliedRevision = -1;
    /// <summary>It asked for the whole layer (a patch did not give the server's bytes).</summary>
    public bool NeedsFull;
    /// <summary>A send to it is running (a follow-up push); another waits.</summary>
    public bool Sending;

    public bool Has(string id) => CachedIds.Contains(id);

    public override string ToString() => Name;
  }

  /// <summary>The clients of this server, in the order they connected. ZNetPatch keeps it beside its own list.</summary>
  internal static readonly List<LayerClient> Clients = [];

  /// <summary>A new session: nothing of the last one is current (the layer, what a client held, the download under way).</summary>
  internal static void SessionStarts(bool server)
  {
    Clients.Clear();
    wholePushes = 0;
    session++;
    download = null;
    ClientLink = null;
    localApplied = 0;
    downloads = installed = 0;
    BakedCache.SessionStarts();
    UI.Remove(DownloadUiKey);
    // A client has no layer until the server's arrives; a machine that runs the world has the one its settings load.
    if (!server)
      BakedLayerStore.Set(null, null);
  }

  // ---- the server: join stage 1 ----------------------------------------------------------------------------------------------------

  /// <summary>How the stage ended.</summary>
  internal sealed class StageResult
  {
    public bool Ready;
    public bool Left;
    public bool Failed;
  }

  /// <summary>Join stage 1 (build spec 5.2): sends the layer and waits for the answer. Run it after the settings are in place and before
  /// the alt-biome placement.</summary>
  internal static IEnumerator JoinStage(LayerClient client, BakedLayer layer, StageResult result)
  {
    client.LayerReady = false;
    client.Pending = layer;
    client.SentRevision = layer.Revision;
    string id = layer.Id;
    if (client.Has(id))
    {
      BetterContinents.Log($"Client {client} already has the baked pieces cached (id {id}, revision {layer.Revision}), instructing it to load those");
      client.Link.Invoke(RpcLoadFromCache, id, (int)layer.Revision);
    }
    else
    {
      BetterContinents.Log($"Client {client} doesn't have the baked pieces cached, sending them now (revision {layer.Revision})");
      var sent = new SendResult();
      var sending = SendLayer(client, layer, kind: 0, layer.Bytes, layer.Length, fromId: "", changed: null, joining: true, sent);
      while (sending.MoveNext())
        yield return null;
      if (sent.Left)
      {
        result.Left = true;
        yield break;
      }
      if (!sent.Done)
      {
        BetterContinents.Log($"Timed out sending the baked pieces to client {client} after {TimeoutSeconds:F0} seconds, disconnecting them");
        client.Disconnect("Better Continents: the baked pieces of this world could not be sent in time");
        result.Failed = true;
        yield break;
      }
    }
    // The client reads and checks the layer on a worker: a layer of tens of megabytes takes seconds on a slow machine. A client that has
    // not answered in this long is stuck.
    float waitFrom = client.Link.Now;
    float patience = Math.Max(120f, layer.Length / 250_000f);
    while (!client.LayerReady && client.Link.Connected && client.Link.Now - waitFrom < patience)
      yield return null;
    if (!client.LayerReady)
    {
      if (!client.Link.Connected)
      {
        result.Left = true;
        yield break;
      }
      BetterContinents.Log($"Client {client} did not answer for the baked pieces in {patience:F0} seconds, disconnecting them");
      client.Disconnect("Better Continents: this client did not answer for the baked pieces of the world");
      result.Failed = true;
      yield break;
    }
    client.Held = layer;
    client.Pending = null;
    result.Ready = true;
  }

  /// <summary>The join is over: PeerInfo goes through next. Pushes reach the client from now, and a layer that changed while it joined, or
  /// that came into being then, is sent to it now (a client still joining gets no push).</summary>
  internal static void JoinFinished(LayerClient client)
  {
    client.JoinDone = true;
    var current = BakedLayerStore.Current;
    if (current == null || !client.Link.Connected || client.Sending)
      return;
    if (client.Held != null && client.Held.Revision == current.Revision && client.Held.Id == current.Id)
      return;
    BetterContinents.Log($"Client {client} joined while the baked pieces changed (revision {current.Revision}), sending it the current layer");
    client.Sending = true;
    StartRoutine(FollowUp(client, current));
  }

  // The whole layer to one client, outside any operation: nobody waits for the answer (Applied sets what the client holds).
  private static IEnumerator FollowUp(LayerClient client, BakedLayer layer)
  {
    try
    {
      client.Pending = layer;
      client.SentRevision = layer.Revision;
      var sent = new SendResult();
      var sending = SendLayer(client, layer, kind: 0, layer.Bytes, layer.Length, fromId: "", changed: null, joining: false, sent);
      while (sending.MoveNext())
        yield return null;
      if (sent.TimedOut)
        BetterContinents.Log($"Timed out sending the baked pieces to client {client}; it keeps the layer it has");
    }
    finally
    {
      client.Sending = false;
    }
  }

  // ---- the server: the messages from a client ---------------------------------------------------------------------------------------

  internal static void OnReady(LayerClient client, int stage)
  {
    if (stage == 1)
    {
      BetterContinents.Log($"Client {client} has the baked pieces");
      client.LayerReady = true;
    }
  }

  internal static void OnApplied(LayerClient client, int revision)
  {
    if (revision > client.AppliedRevision)
      client.AppliedRevision = revision;
    if (client.Pending != null && client.Pending.Revision == (uint)revision)
    {
      client.Held = client.Pending;
      client.Pending = null;
    }
  }

  internal static void OnNeedFull(LayerClient client, int revision)
  {
    BetterContinents.Log($"Client {client} asks for the whole baked layer (its copy of the patch to revision {revision} did not give the server's bytes)");
    client.NeedsFull = true;
    client.Held = null;
  }

  // ---- the server: pushes ----------------------------------------------------------------------------------------------------------

  /// <summary>What a push did, for the operation's output (build spec 10.11).</summary>
  internal sealed class PushReport
  {
    public uint Revision;
    /// <summary>The clients it was pushed to, and how it went with each.</summary>
    public readonly List<PushTarget> Targets = [];
    /// <summary>This machine has a client side that had to rebuild too, and whether it did in time.</summary>
    public bool LocalSide;
    public bool LocalApplied;
    public double Seconds;
    public int Patches => Targets.Count(t => t.Kind == 1);
    public int Whole => Targets.Count(t => t.Kind == 0);
    public int Applied => Targets.Count(t => t.Outcome == PushOutcome.Applied);
    public int Left => Targets.Count(t => t.Outcome == PushOutcome.Left);
    public IEnumerable<PushTarget> TimedOut => Targets.Where(t => t.Outcome == PushOutcome.TimedOut);
    public long Bytes => Targets.Sum(t => (long)t.Bytes);

    /// <summary>"layer revision 13 sent to 3 players; 3 of 3 ready in 2.4 s", with what did not go as hoped.</summary>
    public string Summary()
    {
      var line = Targets.Count == 0 && !LocalSide
        ? $"layer revision {Revision}: no player to send it to"
        : $"layer revision {Revision} sent to {Targets.Count} player{(Targets.Count == 1 ? "" : "s")}; {Applied} of {Targets.Count} ready in {Seconds:F1} s";
      if (Left > 0)
        line += $", {Left} left meanwhile";
      var late = TimedOut.ToList();
      if (late.Count > 0)
        line += $"; no answer in {TimeoutSeconds:F0} s from {string.Join(", ", late.Select(t => t.Name))} (the operation went on without them)";
      if (LocalSide && !LocalApplied)
        line += $"; this machine's own view did not report in {TimeoutSeconds:F0} s";
      return line;
    }
  }

  internal enum PushOutcome { Waiting, Applied, Left, TimedOut }

  internal sealed class PushTarget(LayerClient client)
  {
    public readonly LayerClient Client = client;
    public string Name => Client.Name;
    /// <summary>0 the whole layer, 1 a patch.</summary>
    public int Kind;
    public int Bytes;
    /// <summary>The patch, for a kind 1.</summary>
    public byte[]? Patch;
    public PushOutcome Outcome = PushOutcome.Waiting;
    public IEnumerator? Sender;
    public SendResult? Sent;
    /// <summary>When the answer started to be waited for: the last packet.</summary>
    public float WaitingFrom;
    /// <summary>Not started: the connection is busy with a follow-up, or four whole layers are going out already.</summary>
    public bool Queued;
    /// <summary>It holds the connection's send (LayerClient.Sending) and, for a whole layer, one of the four places.</summary>
    public bool HoldsSend, HoldsWhole;
  }

  private static int wholePushes;

  /// <summary>Pushes the new layer to every connected client whose join is over, and waits until each has answered Applied, has left, or
  /// has been silent for 30 s after its last packet; and for this machine's own client side, which reports the same way without the
  /// network. Calls <paramref name="done"/> with the report. The layer is made current on this machine here if it is not yet.</summary>
  internal static IEnumerator Push(BakedLayer next, ZoneKey[] changed, Action<PushReport> done)
  {
    var report = new PushReport { Revision = next.Revision };
    float began = Clock();
    string nextId = next.Id;
    if (!ReferenceEquals(BakedLayerStore.Current, next))
      BakedLayerStore.Set(next, changed);

    var targets = new List<PushTarget>();
    foreach (var client in Clients.ToList())
      if (client.JoinDone && client.Link.Connected)
        targets.Add(new PushTarget(client));

    // The patches, one per layer the clients hold: made and checked on a worker (the check is the one the client makes: the patch applied
    // to that layer gives the new layer's id).
    var patches = new Dictionary<BakedLayer, byte[]?>(ReferenceComparer<BakedLayer>.Instance);
    var building = new List<(BakedLayer From, Task<byte[]?> Task)>();
    foreach (var held in targets.Select(t => t.Client.Held).OfType<BakedLayer>().Where(h => h.Revision + 1 == next.Revision).Distinct(ReferenceComparer<BakedLayer>.Instance))
      building.Add((held, Task.Run(() => MakePatch(held, next, changed, nextId))));
    while (building.Any(b => !b.Task.IsCompleted))
      yield return null;
    foreach (var (from, task) in building)
      patches[from] = task.IsFaulted ? null : task.Result;

    foreach (var target in targets)
    {
      var client = target.Client;
      client.Pending = next;
      client.SentRevision = next.Revision;
      client.NeedsFull = false;
      StartSend(target, next, patches);
    }
    report.Targets.AddRange(targets);

    // This machine's own client side, when it has one running: it reports the same way, without the network.
    bool local = ClientReports;
    bool localPending = local && localApplied < next.Revision;
    float localSince = Clock();
    while (true)
    {
      bool waiting = false;
      foreach (var target in targets)
      {
        if (target.Outcome != PushOutcome.Waiting)
          continue;
        Step(target, next, changed);
        waiting |= target.Outcome == PushOutcome.Waiting;
      }
      if (localPending)
      {
        if (localApplied >= next.Revision || Clock() - localSince > TimeoutSeconds)
          localPending = false;
        else
          waiting = true;
      }
      if (!waiting)
        break;
      yield return null;
    }
    report.LocalSide = local;
    report.LocalApplied = local && localApplied >= next.Revision;
    foreach (var target in targets)
      ReleaseSend(target);
    report.Seconds = Clock() - began;
    BetterContinents.Log($"Baked pieces: {report.Summary()}");
    done(report);
  }

  private static byte[]? MakePatch(BakedLayer from, BakedLayer to, ZoneKey[] changed, string toId)
  {
    try
    {
      var patch = BakedPatch.Make(from, to, changed);
      if (patch.Length > to.Length * PatchShareLimit)
        return null;
      // What the client does with it, and the id it must come to.
      var back = BakedPatch.Apply(from, patch);
      if (back.Id != toId)
      {
        BetterContinents.LogWarning($"The patch from baked revision {from.Revision} to {to.Revision} did not give the new layer; the whole layer is sent instead.");
        return null;
      }
      return patch;
    }
    catch (Exception e)
    {
      BetterContinents.LogWarning($"No patch could be made from baked revision {from.Revision} to {to.Revision} ({e.Message}); the whole layer is sent instead.");
      return null;
    }
  }

  // What the client gets: a patch when there is one for the layer it holds, the whole layer otherwise. Nothing starts until Step finds
  // room for it.
  private static void StartSend(PushTarget target, BakedLayer next, Dictionary<BakedLayer, byte[]?> patches)
  {
    var client = target.Client;
    target.Sent = new SendResult();
    target.Queued = true;
    if (client.Held != null && patches.TryGetValue(client.Held, out var patch) && patch != null && !client.NeedsFull)
    {
      target.Kind = 1;
      target.Patch = patch;
      target.Bytes = patch.Length;
      return;
    }
    target.Kind = 0;
    target.Bytes = next.Length;
  }

  private static void Step(PushTarget target, BakedLayer next, ZoneKey[] changed)
  {
    var client = target.Client;
    if (!client.Link.Connected)
    {
      ReleaseSend(target);
      target.Outcome = PushOutcome.Left;
      return;
    }
    if (client.NeedsFull)
    {
      // A patch that did not give the server's bytes: the whole layer, from the start (the client drops what it had of the patch at its
      // Start), and the wait starts again after its last packet.
      client.NeedsFull = false;
      ReleaseSend(target);
      target.Kind = 0;
      target.Patch = null;
      target.Bytes = next.Length;
      target.Sender = null;
      target.Queued = true;
    }
    if (target.Queued)
    {
      // One send at a time on a connection (a follow-up may be going out), and four whole layers at a time on the server.
      if (client.Sending || (target.Kind == 0 && wholePushes >= MaxWholePushes))
        return;
      target.Queued = false;
      client.Sending = true;
      target.HoldsSend = true;
      if (target.Kind == 0)
      {
        wholePushes++;
        target.HoldsWhole = true;
      }
      target.Sent = new SendResult();
      target.Sender = target.Kind == 1
        ? SendLayer(client, next, kind: 1, target.Patch!, target.Patch!.Length, fromId: client.Held!.Id, changed, joining: false, target.Sent)
        : SendLayer(client, next, kind: 0, next.Bytes, next.Length, fromId: "", changed: null, joining: false, target.Sent);
    }
    if (target.Sender != null)
    {
      if (target.Sender.MoveNext())
        return;
      target.Sender = null;
      ReleaseSend(target);
      if (target.Sent!.Left)
      {
        target.Outcome = PushOutcome.Left;
        return;
      }
      if (target.Sent.TimedOut)
      {
        target.Outcome = PushOutcome.TimedOut;
        return;
      }
      target.WaitingFrom = target.Sent.LastPacketAt;
    }
    // Sent: the answer, or silence.
    if (client.AppliedRevision >= next.Revision)
    {
      target.Outcome = PushOutcome.Applied;
      return;
    }
    if (client.Link.Now - target.WaitingFrom > TimeoutSeconds)
      target.Outcome = PushOutcome.TimedOut;
  }

  private static void ReleaseSend(PushTarget target)
  {
    if (target.HoldsSend)
    {
      target.HoldsSend = false;
      target.Client.Sending = false;
    }
    if (target.HoldsWhole)
    {
      target.HoldsWhole = false;
      wholePushes--;
    }
  }

  // ---- the server: one layer to one client ------------------------------------------------------------------------------------------

  /// <summary>What the Start message holds (build spec 5.1, as one package: ZRpc takes four arguments at most).</summary>
  internal sealed class LayerHeader
  {
    /// <summary>0 the whole layer, 1 a patch.</summary>
    public int Kind;
    public int Length;
    public int Hash;
    public uint Revision;
    /// <summary>The id of the layer this makes (for a patch, the one it comes to).</summary>
    public string Id = "";
    /// <summary>For a patch, the id of the layer it applies to.</summary>
    public string FromId = "";
    /// <summary>The join's own transfer: the client answers Ready(1) instead of Applied.</summary>
    public bool Joining;
    /// <summary>The zones that differ from the layer before: null is every zone.</summary>
    public ZoneKey[]? Changed;

    public ZPackage ToPackage()
    {
      var pkg = new ZPackage();
      pkg.Write(Kind);
      pkg.Write(Length);
      pkg.Write(Hash);
      pkg.Write((int)Revision);
      pkg.Write(Id);
      pkg.Write(FromId);
      pkg.Write(Joining);
      var zones = new ZPackage();
      zones.Write(Changed == null ? -1 : Changed.Length);
      if (Changed != null)
        foreach (var zone in Changed)
        {
          zones.Write((short)zone.X);
          zones.Write((short)zone.Z);
        }
      pkg.Write(zones);
      return pkg;
    }

    public static LayerHeader Read(ZPackage pkg)
    {
      var header = new LayerHeader
      {
        Kind = pkg.ReadInt(),
        Length = pkg.ReadInt(),
        Hash = pkg.ReadInt(),
        Revision = (uint)pkg.ReadInt(),
        Id = pkg.ReadString(),
        FromId = pkg.ReadString(),
        Joining = pkg.ReadBool(),
      };
      var zones = pkg.ReadPackage();
      int count = zones.ReadInt();
      if (count >= 0)
      {
        header.Changed = new ZoneKey[count];
        for (int i = 0; i < count; i++)
          header.Changed[i] = new ZoneKey(zones.ReadShort(), zones.ReadShort());
      }
      return header;
    }
  }

  /// <summary>Start, and the packets after it, to one client. <paramref name="data"/> is the layer or the patch.</summary>
  internal static IEnumerator SendLayer(LayerClient client, BakedLayer layer, int kind, byte[] data, int length, string fromId, ZoneKey[]? changed, bool joining, SendResult result)
  {
    var header = new LayerHeader
    {
      Kind = kind,
      Length = length,
      Hash = ZNetPatch.GetHashCode(data, length),
      Revision = layer.Revision,
      Id = layer.Id,
      FromId = fromId,
      Joining = joining,
      Changed = changed,
    };
    BetterContinents.Log($"Sending the baked pieces to client {client}: {(kind == 1 ? "a patch" : "the whole layer")} of {length} bytes (revision {layer.Revision})");
    client.Link.Invoke(RpcStart, header.ToPackage());
    bool rateRaised = client.Link.RaiseSendRate("baked pieces", length);
    float startedAt = client.Link.Now;
    var sending = SendPackets(client.Link, data, length, RpcPacket, " (baked pieces)", result);
    while (sending.MoveNext())
      yield return null;
    if (rateRaised)
      client.Link.RestoreSendRate(result.Done ? "baked pieces sent" : "baked pieces not sent");
    if (result.Done)
    {
      float seconds = Mathf.Max(client.Link.Now - startedAt, 0.001f);
      BetterContinents.Log($"Baked pieces sent: {length} bytes in {seconds:F1} s ({length / seconds / 1024f:F0} KB/s)");
    }
  }

  // ---- helpers -------------------------------------------------------------------------------------------------------------------------

  private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
  {
    public static readonly ReferenceComparer<T> Instance = new();
    public bool Equals(T? a, T? b) => ReferenceEquals(a, b);
    public int GetHashCode(T value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
  }

  // ---- the client's side ---------------------------------------------------------------------------------------------------------------

  private const string DownloadUiKey = "LayerDownload";

  /// <summary>A layer or a patch coming in.</summary>
  internal sealed class Download(LayerHeader header)
  {
    public readonly LayerHeader Header = header;
    /// <summary>The order downloads began in: a later one is newer, whatever order the workers finish in.</summary>
    public readonly int Serial = ++downloads;
    public readonly byte[] Buffer = new byte[Math.Max(header.Length, 0)];
    public int Received;
    public int Percent => ZNetPatch.Percent(Received, Buffer.Length);
    public bool Complete => Received == Buffer.Length;

    private int nextLogAt = Math.Max(header.Length / 10, 1);

    /// <summary>Whether another tenth of the transfer, or the end of it, has come in since the last line.</summary>
    public bool TimeToLog()
    {
      if (Received < nextLogAt && !Complete)
        return false;
      nextLogAt += Math.Max(Buffer.Length / 10, 1);
      return true;
    }

    /// <summary>Takes a packet. Null when it is in order, else why it is not.</summary>
    public string? Add(int offset, int packetHash, byte[] bytes)
    {
      if (ZNetPatch.GetHashCode(bytes) != packetHash)
        return "a packet arrived damaged";
      if (offset < 0 || offset + bytes.Length > Buffer.Length)
        return "a packet is outside the transfer";
      System.Buffer.BlockCopy(bytes, 0, Buffer, offset, bytes.Length);
      Received += bytes.Length;
      return null;
    }
  }

  private static Download? download;
  private static int downloads, installed;
  private static int session;
  private static long localApplied;

  /// <summary>The connection to the server, on a client: where Applied goes.</summary>
  internal static IBakedLink? ClientLink;

  /// <summary>What the client does when a transfer fails past recovery: show the reason and leave (ZNetPatch's, for the game).</summary>
  private static Action<string> failClient = _ => { };

  /// <summary>The handlers of a client's connection (the server's messages).</summary>
  internal static void RegisterClient(ZRpc rpc, IBakedLink link, Action<string> fail)
  {
    ClientLink = link;
    failClient = fail;
    int mine = session;
    rpc.Register(RpcLoadFromCache, (ZRpc _, string id, int revision) => StartRoutine(LoadFromCache(id, revision, mine)));
    rpc.Register(RpcStart, (ZRpc _, ZPackage header) => OnStart(header));
    rpc.Register(RpcPacket, (ZRpc _, int offset, int packetHash, ZPackage packet) => OnPacket(offset, packetHash, packet, mine));
  }

  /// <summary>The handlers of a server's connection (a client's messages).</summary>
  internal static void RegisterServer(ZRpc rpc, LayerClient client)
  {
    rpc.Register(RpcApplied, (ZRpc _, int revision) => OnApplied(client, revision));
    rpc.Register(RpcNeedFull, (ZRpc _, int revision) => OnNeedFull(client, revision));
  }

  private static void OnStart(ZPackage package)
  {
    var header = LayerHeader.Read(package);
    if (header.Length < 0)
    {
      failClient("Better Continents: the server sent the baked pieces of this world in a form this version does not know, please reconnect!");
      return;
    }
    var now = new Download(header);
    download = now;
    BetterContinents.Log($"Receiving the baked pieces from the server: {(header.Kind == 1 ? "a patch" : "the whole layer")}, {header.Length} bytes (revision {header.Revision})");
    UI.Add(DownloadUiKey, () => UI.ProgressBar(now.Percent, "Better Continents: downloading the baked pieces of this world from the server ..."));
    if (header.Length == 0)
      Complete(now);
  }

  private static void OnPacket(int offset, int packetHash, ZPackage packet, int mine)
  {
    var current = download;
    if (current == null || mine != session)
      return;
    var problem = current.Add(offset, packetHash, packet.GetArray());
    if (problem != null)
    {
      UI.Remove(DownloadUiKey);
      download = null;
      BetterContinents.LogError($"Better Continents: the baked pieces from the server: {problem} (offset {offset})");
      Problem(current.Header, "the baked pieces of this world were corrupted during transfer, please reconnect!");
      return;
    }
    // A line for each tenth of the transfer (the settings log every packet).
    if (current.TimeToLog())
      BetterContinents.Log($"Received {current.Received} of {current.Buffer.Length} bytes of the baked pieces");
    if (current.Complete)
      Complete(current);
  }

  // A transfer that went wrong. A patch asks for the whole layer instead (the join's own transfer and a whole layer cannot ask again
  // without end): anything else ends the connection with the reason on the screen.
  private static void Problem(LayerHeader header, string reason)
  {
    if (header.Kind == 1 && !header.Joining)
    {
      BetterContinents.Log($"The patch to baked revision {header.Revision} is no good ({reason}); asking for the whole layer");
      ClientLink?.Invoke(RpcNeedFull, (int)header.Revision);
      return;
    }
    failClient($"Better Continents: {reason}");
  }

  private static void Complete(Download finished)
  {
    UI.Remove(DownloadUiKey);
    download = null;
    StartRoutine(Receive(finished, session));
  }

  /// <summary>What a worker made of what came in: the layer, or why not.</summary>
  private sealed class Received
  {
    public BakedLayer? Layer;
    public string? Error;
    /// <summary>The patch did not fit the layer this machine has: ask for the whole one.</summary>
    public bool NeedFull;
  }

  // A download is whole: on a worker, the checks (the transfer's hash, then the layer's id) and the layer, and its file in the cache; on
  // the main thread, the layer becomes current and the server hears.
  private static IEnumerator Receive(Download finished, int mine)
  {
    var header = finished.Header;
    var held = BakedLayerStore.Current;
    var task = Task.Run(() =>
    {
      var result = new Received();
      if (ZNetPatch.GetHashCode(finished.Buffer, finished.Header.Length) != header.Hash)
      {
        result.Error = "the baked pieces arrived damaged";
        return result;
      }
      try
      {
        if (header.Kind == 1)
        {
          if (held == null || held.Id != header.FromId)
          {
            result.NeedFull = true;
            return result;
          }
          var made = BakedPatch.Apply(held, finished.Buffer);
          if (made.Id != header.Id)
          {
            result.NeedFull = true;
            return result;
          }
          result.Layer = made;
        }
        else
        {
          if (BakedCache.IdOf(finished.Buffer, header.Length) != header.Id)
          {
            result.Error = "the baked pieces arrived damaged (their id differs)";
            return result;
          }
          result.Layer = BakedLayer.Read(finished.Buffer, header.Length);
        }
        BakedCache.Add(header.Id, result.Layer.Bytes, result.Layer.Length);
      }
      catch (Exception e)
      {
        result.Error = e.Message;
      }
      return result;
    });
    while (!task.IsCompleted)
      yield return null;
    // A session that ended meanwhile, or a download that began after this one and has been installed already: not this.
    if (mine != session || finished.Serial < installed)
      yield break;
    var outcome = task.IsFaulted ? new Received { Error = task.Exception?.GetBaseException().Message ?? "failed" } : task.Result;
    if (outcome.NeedFull)
    {
      Problem(header, "the patch does not fit this machine's layer");
      yield break;
    }
    if (outcome.Layer == null)
    {
      BetterContinents.LogError($"Better Continents: the baked pieces from the server failed to load: {outcome.Error}");
      Problem(header, $"the baked pieces of this world failed to load ({outcome.Error}), please reconnect to download them again!");
      yield break;
    }
    installed = finished.Serial;
    Install(outcome.Layer, header.Changed, header.Joining, header.Id);
  }

  /// <summary>The layer is this machine's now: current, with its zones that changed (null: every one), and the server told.</summary>
  private static void Install(BakedLayer layer, ZoneKey[]? changed, bool joining, string id)
  {
    BakedLayerStore.Set(layer, changed);
    BakedCache.Replaced(id);
    if (joining)
    {
      ClientLink?.Invoke(RpcReady, 1);
      return;
    }
    // Pushed during play: BakedClient reports when the zones inside its collider ring have their new colliders. Where nothing is there
    // to rebuild (the loading screen, a machine with no graphics) the answer is now.
    if (!ClientReports)
      ReportApplied((int)layer.Revision);
  }

  // The join's cache hit: the file read and checked on a worker, then the layer is current and the server hears.
  private static IEnumerator LoadFromCache(string id, int revision, int mine)
  {
    BetterContinents.Log($"Loading the baked pieces from the local cache, id {id}");
    UI.Add("LoadingLayerFromCache", () => UI.DisplayMessage("Better Continents: initializing from the cached baked pieces"));
    var task = Task.Run(() =>
    {
      var bytes = BakedCache.Load(id);
      if (BakedCache.IdOf(bytes, bytes.Length) != id)
        return null;
      return BakedLayer.Read(bytes, bytes.Length);
    });
    while (!task.IsCompleted)
      yield return null;
    UI.Remove("LoadingLayerFromCache");
    if (mine != session)
      yield break;
    if (task.IsFaulted || task.Result == null)
    {
      var why = task.Exception != null
        ? $"Better Continents: the cached baked pieces failed to load ({task.Exception.GetBaseException().Message}), please reconnect to download them again!"
        : "Better Continents: the cached baked pieces are corrupted, please reconnect to download them again!";
      BetterContinents.LogError(why);
      BakedCache.Delete(id);
      failClient(why);
      yield break;
    }
    Install(task.Result, null, joining: true, id);
  }

  /// <summary>This machine's own view has the layer's revision in place (BakedClient calls it when the zones inside its collider ring have
  /// their new colliders, or after 10 s): on a client the server hears, on the machine that runs the world its operation's wait counts it.</summary>
  internal static void ReportApplied(int revision)
  {
    if (revision > localApplied)
      localApplied = revision;
    if (!RunsWorld())
      ClientLink?.Invoke(RpcApplied, revision);
  }

  /// <summary>For the tests: what this machine's own view has reported.</summary>
  internal static long LocalApplied => localApplied;

  // ---- a GameTerrain conversion (build spec 4.3) ---------------------------------------------------------------------------------------

  /// <summary>The settings as they are now, to every client whose join is over: a vanilla world became a Better Continents world while they
  /// were in it. Returns how many were sent to.</summary>
  internal static int BroadcastSettings(ZPackage settings)
  {
    int sent = 0;
    foreach (var client in Clients.ToList())
      if (client.JoinDone && client.Link.Connected)
      {
        client.Link.Invoke(RpcSettingsUpdate, settings);
        sent++;
      }
    return sent;
  }
}
