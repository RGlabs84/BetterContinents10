// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterContinents;

// What the world's saves are doing, as the settling of operations needs to know it.
internal interface IBakeSaves
{
  // The number of complete, successful saves of this session's world (SaveSystem's own count: a failed save does not move it).
  uint Completed { get; }
  // A save is under way: its objects were copied before this moment, and its settings will be written after it.
  bool Saving { get; }
  // The world is still loaded.
  bool WorldReady { get; }
}

internal sealed class GameSaves : IBakeSaves
{
  public static readonly GameSaves Instance = new();

  public uint Completed => SaveSystem.s_saveNumber;
  public bool Saving => SaveSystem.s_saving || (ZNet.instance != null && ZNet.instance.m_saveThread != null && ZNet.instance.m_saveThread.IsAlive);
  public bool WorldReady => ZNet.instance != null && ZNet.World != null && ZDOMan.instance != null;
}

// Settles the operations that ended in a state the next world load would settle (Removed, Unbaking, Undoing, Applied), as soon as that is true
// without a load: after a complete world save that began after the operation ended.
//
// Why it matters: an operation's last change reaches the objects and the layer at one moment, but a save writes the objects first and the layer
// last, and a cut save can leave the two from different moments. The operation's state stays unsettled until a save that held all of it is
// complete, so that the check at world load can still finish it. But the check finishes an unbake by making the pieces that are missing: if players
// demolish some of them meanwhile, and the state is still unsettled when the world is loaded, they would be made again. A save that began after the
// operation ended wrote its objects and its layer whole, and then there is nothing left for a load to finish.
internal static class BakeSettle
{
  private sealed class Waiting
  {
    public BakeJournal Journal = null!;
    public int Number;
    // The state to write, and the one the file must still hold for it to be written (another operation may have taken the file since).
    public BakeState Final;
    public BakeState Transient;
    public uint CompletedThen;
    // Complete saves needed: 1, or 2 when a save was under way at the end (its objects were copied before the operation's last change).
    public int Needed;
  }

  private static readonly List<Waiting> waiting = [];
  private static bool looping;

  internal static int Count => waiting.Count;

  // The operation has ended in its transient state: write `final` after the saves have gone by.
  internal static void Register(IBakeSaves saves, BakeJournal journal, int number, BakeState final)
  {
    Cancel(journal, number);
    waiting.Add(new Waiting
    {
      Journal = journal,
      Number = number,
      Final = final,
      Transient = journal.GetState(number) ?? BakeState.Prepared,
      CompletedThen = saves.Completed,
      Needed = saves.Saving ? 2 : 1,
    });
  }

  // The operation is being changed again (an undo takes its file): it is not waiting for a save any more.
  internal static void Cancel(BakeJournal journal, int number) =>
    waiting.RemoveAll(w => w.Number == number && w.Journal.Folder == journal.Folder);

  internal static void Clear() => waiting.Clear();

  // Writes the final state of every operation whose saves have gone by. A world that is not loaded any more settles nothing: the next load does.
  internal static void Poll(IBakeSaves saves)
  {
    if (waiting.Count == 0)
      return;
    if (!saves.WorldReady)
    {
      waiting.Clear();
      return;
    }
    uint completed = saves.Completed;
    foreach (var w in waiting.ToList())
    {
      if (completed - w.CompletedThen < (uint)w.Needed)
        continue;
      waiting.Remove(w);
      try
      {
        // Only the state this operation ended in: a file another operation has taken since is not touched.
        if ((w.Journal.GetState(w.Number) ?? BakeState.Prepared) == w.Transient)
          w.Journal.SetState(w.Number, w.Final);
      }
      catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
      {
        // The next world load settles it.
        BetterContinents.LogWarning($"bc_bake: operation {w.Number} could not be marked {w.Final} ({e.Message}); the next world load settles it.");
      }
    }
  }

  // Polls once a second while an operation waits.
  internal static void EnsureLoop()
  {
    if (looping || BetterContinents.instance == null)
      return;
    looping = true;
    BetterContinents.instance.StartCoroutine(Loop());
  }

  private static IEnumerator Loop()
  {
    try
    {
      while (waiting.Count > 0)
      {
        yield return new WaitForSecondsRealtime(1f);
        Poll(GameSaves.Instance);
      }
    }
    finally
    {
      looping = false;
    }
  }
}
