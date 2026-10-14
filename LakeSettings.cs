// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).

using System;
using BepInEx.Configuration;

namespace BetterContinents;

/// <summary>The bound entries of the settings of section 12 (SettingsSchema.Lakes...), set when the config is bound (Awake), and where the lake engine (LakeStreamer, LakeBuild,
/// LakeSound, LakeMap) reads the live ones: null where the config is not bound (the offline suites, a dedicated server's early frames), and then the defaults of the schema
/// give a value all the same. The member names and meanings are the lake engine's own (agent B wrote the same holder in LakeWater.cs: this one has all of its members and
/// DepthScale besides, so one of the two copies goes when the branches meet).</summary>
internal static class LakeSettings
{
  /// <summary>The config's Lake Depth Scale: what a new world copies. The world's own value is kept with its lakes (LakeSet.DepthScale).</summary>
  internal static ConfigEntry<float>? DepthScale;

  internal static ConfigEntry<float>? BuildRange, BuildBudget, Sound;
  internal static ConfigEntry<bool>? OnMap;

  /// <summary>Tiles within this of the player are made, and dropped 150 m further out (Lake Build Range, 150 to 2000).</summary>
  internal static float Range => Math.Max(150f, Math.Min(2000f, BuildRange?.Value ?? 400f));
  /// <summary>The main thread's milliseconds a frame for making tile objects (Lake Build Budget, 0.5 to 20).</summary>
  internal static float Budget => Math.Max(0.5f, Math.Min(20f, BuildBudget?.Value ?? 2f));
  /// <summary>The volume of the lake sound (Lake Sound, 0 to 1).</summary>
  internal static float SoundVolume => Math.Max(0f, Math.Min(1f, Sound?.Value ?? 0.35f));
  /// <summary>Whether lakes are painted on the map (Lakes On Map).</summary>
  internal static bool PaintOnMap => OnMap?.Value ?? true;
}
