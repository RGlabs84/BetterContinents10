// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
//
// What slice B's tests (Tests.Server*.cs) share: VALtima's layer, read once, and a layer made current for the code that reads the store.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace PlacementTests;

internal static partial class Tests
{
  private static BakedLayer serverValtima;

  // VALtima's real file as a layer (spec 2.7), read once for all of slice B's tests; null (and the test says so) when it is not here.
  internal static BakedLayer ServerValtima(string test)
  {
    if (serverValtima != null)
      return serverValtima;
    if (!NeedValtima(test))
      return null;
    var bytes = Valtima();
    return serverValtima = BakedLayer.Read(bytes, bytes.Length);
  }

  // The same file with another revision (the header's u32 at byte 8, and the CRC): a layer that differs from the first in its bytes and its id
  // and nowhere else.
  internal static BakedLayer ServerWithRevision(BakedLayer layer, uint revision)
  {
    var bytes = (byte[])layer.Bytes.Clone();
    BitConverter.GetBytes(revision).CopyTo(bytes, 8);
    BitConverter.GetBytes(BakedFormat.Crc32(bytes, 0, layer.Length - 4)).CopyTo(bytes, layer.Length - 4);
    return BakedLayer.Read(bytes, layer.Length);
  }

  // The world's settings as the code reads the store: a fresh settings object holding the layer (or none). Returns what to restore.
  internal static IDisposable ServerStore(BakedLayer layer)
  {
    var before = BC.Settings;
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = true };
    BC.Settings.Layer = layer;
    return new Restore(() => BC.Settings = before);
  }

  internal sealed class Restore(Action undo) : IDisposable
  {
    private bool done;

    public void Dispose()
    {
      if (done)
        return;
      done = true;
      undo();
    }
  }

  internal static FieldInfo ServerPrivate(Type type, string name) =>
    type.GetField(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingFieldException(type.Name, name);
}
