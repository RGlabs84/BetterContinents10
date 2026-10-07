// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;

namespace BetterContinents;

// The layer of the world this machine is in. It is the active settings' layer (BetterContinents.Settings.Layer), so the settings that
// are saved, sent and swapped on a world change always carry the layer that is current; Set swaps it whole and says what changed.
// A reader on any thread takes Current once and uses that reference: a layer is a snapshot and never changes.
internal static class BakedLayerStore
{
  // The current layer; null in a world without one (and in the main menu).
  public static BakedLayer? Current => BetterContinents.Settings.Layer;

  public static bool HasLayer => Current != null;

  // After a new layer is current: (the old one, the new one, the zones whose look changed). Raised on the thread that called Set, which
  // is the main thread; a handler that works on another thread hands it the snapshot. The zones are null when everything changed (a
  // first layer, a world change, a reset). Not raised when the settings are swapped for another world's: see Current.
  public static event Action<BakedLayer?, BakedLayer?, ZoneKey[]?>? Changed;

  // Makes `next` the current layer; null clears it (a client session starts or ends). `changed`: the zones that differ from the old layer
  // (what LayerEdit.Build returned), or null for all of them.
  public static void Set(BakedLayer? next, ZoneKey[]? changed)
  {
    var settings = BetterContinents.Settings;
    var old = settings.Layer;
    settings.Layer = next;
    if (ReferenceEquals(old, next))
      return;
    Changed?.Invoke(old, next, changed);
    BakedApi.RaiseLayerChanged();
  }
}
