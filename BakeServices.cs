// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// What the bake needs from the other parts of baked placements, reached through small interfaces: the transport and the reconciliation of Live
// pieces (the network and server work), the drawing, the counts, hide and show (the client work), and the conversion of a world to one that keeps
// the game's terrain. Until those are part of the build, the stand-ins below answer that the part is not available in this build; the real ones
// replace them in BakeServices.
internal static class BakeServices
{
  public static IBakeTransport Transport { get; set; } = new UnavailableTransport();
  public static IBakeReconcile? Reconcile { get; set; }
  public static IBakeConvert Convert { get; set; } = new UnavailableConvert();
  public static IBakeClientTools Client { get; set; } = new UnavailableClientTools();
  public static IBakeOrphans Orphans { get; set; } = new UnavailableOrphans();

  public const string NotHere = "not available in this build";
}

// The tools that act on this machine's drawing of the baked pieces.
internal interface IBakeClientTools
{
  // Why this build has none, or null.
  string? Unavailable { get; }
  // In view now, per kind (bc_bake stats), one line at a time.
  void Stats(Action<string> say);
  // This client's drawing off and on; colliders stay.
  void Hide();
  void Show();
  // The prefabs the layer's palette names that this game does not have; null when not known.
  IReadOnlyList<string>? MissingKinds();
}

// A piece the layer once held and no longer does, which stays where it is (a container that holds items): the reconciliation lists them.
internal sealed class BakeOrphan
{
  public ZDOID Id { get; init; }
  public string Prefab { get; init; } = "";
  public Vector3 Position { get; init; }
  public string Reason { get; init; } = "";
  public bool HoldsItems { get; init; }
}

internal interface IBakeOrphans
{
  string? Unavailable { get; }
  IReadOnlyList<BakeOrphan> List();
  void Destroy(IEnumerable<ZDOID> ids, Action<string> say);
}

internal sealed class UnavailableTransport : IBakeTransport
{
  public string? Unavailable => "sending a changed layer to the players is " + BakeServices.NotHere;

  public int Players => ZNet.instance != null ? ZNet.instance.GetPeers().Count : 0;

  // Which version each player runs is the transport's to know; this stand-in knows none.
  public IReadOnlyList<(string Name, string? Version)> Peers =>
    ZNet.instance == null ? [] : ZNet.instance.GetPeers().Select(p => (p.m_playerName ?? "", (string?)null)).ToList();

  public System.Collections.IEnumerator Push(LayerChange change, Action<BakePush> done) =>
    throw new NotSupportedException("sending a changed layer is " + BakeServices.NotHere);
}

internal sealed class UnavailableConvert : IBakeConvert
{
  public string? Unavailable => "making a world a Better Continents world that keeps the game's terrain is " + BakeServices.NotHere;

  public BakeWorldKind Kind => BakeRuntime.KindOfWorld();

  public void Convert() => throw new NotSupportedException(Unavailable);
}

internal sealed class UnavailableClientTools : IBakeClientTools
{
  public string? Unavailable => "the drawing of baked pieces is " + BakeServices.NotHere;

  public void Stats(Action<string> say) => say("bc_bake stats: " + Unavailable + ".");
  public void Hide() => throw new NotSupportedException(Unavailable);
  public void Show() => throw new NotSupportedException(Unavailable);
  public IReadOnlyList<string>? MissingKinds() => null;
}

internal sealed class UnavailableOrphans : IBakeOrphans
{
  public string? Unavailable => "the list of orphans is " + BakeServices.NotHere;

  public IReadOnlyList<BakeOrphan> List() => [];

  public void Destroy(IEnumerable<ZDOID> ids, Action<string> say) => say("bc_bake orphans: " + Unavailable + ".");
}

// The game's side of a context: the world's folder of undo files, and the pieces of a BakeContext as they are in the running game.
internal static class BakeRuntime
{
  // <save data>/BetterContinents/bakes/<world name>-<world uid>/, on this machine, or null with no world loaded.
  internal static BakeJournal? JournalOfWorld()
  {
    var world = ZNet.World;
    if (world == null)
      return null;
    var root = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents");
    return new BakeJournal(BakeJournal.FolderFor(root, world.m_worldName, world.m_uid));
  }

  // The context of a command in the running game.
  internal static BakeContext? Context(Action<string> say, BakeWho who, Action<int, BakeState>? ended = null)
  {
    var journal = JournalOfWorld();
    if (journal == null)
      return null;
    return new BakeContext
    {
      World = ZdoBakeWorld.Instance,
      Layer = new LayerPort(),
      Transport = BakeServices.Transport,
      Reconcile = BakeServices.Reconcile,
      Convert = BakeServices.Convert,
      Journal = journal,
      Say = say,
      Who = who,
      // An operation ends in a state a load settles; a complete save that began after it settles it without one (BakeSettle).
      Ended = ended ?? ((number, final) =>
      {
        BakeSettle.Register(GameSaves.Instance, journal, number, final);
        BakeSettle.EnsureLoop();
      }),
      Began = number => BakeSettle.Cancel(journal, number),
    };
  }

  // The context of the check at world load: no player, no push, and the log for a voice.
  internal static BakeContext? ContextForCheck()
  {
    if (!ZdoBakeWorld.Instance.Ready)
      return null;
    return Context(BetterContinents.Log, new BakeWho { Name = "world load" });
  }

  // What the world's settings say about it (4.1, 4.3).
  internal static BakeWorldKind KindOfWorld()
  {
    if (BetterContinents.Settings.EnabledForThisWorld)
      return BakeWorldKind.BetterContinents;
    var world = ZNet.World;
    // A settings file that is there and was not read: a newer Better Continents wrote it, or it is damaged. (A world on the cloud is not looked
    // for; it counts as the game's own.)
    if (world != null && world.m_fileSource != FileHelpers.FileSource.Cloud)
    {
      try
      {
        if (File.Exists(BetterContinents.GetWorldBCFile(world.m_worldName, world.m_fileSource)))
          return BakeWorldKind.Unreadable;
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException)
      {
      }
    }
    return BakeWorldKind.Vanilla;
  }
}
