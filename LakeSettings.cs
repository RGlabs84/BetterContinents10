// Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).

using BepInEx.Configuration;

namespace BetterContinents;

// The bound entries of the settings of section 12 (SettingsSchema.Lakes...), set when the config is bound (Awake). The engine (LakeStreamer, LakeBuild, LakeSound,
// LakeMap) reads the live ones with their defaults when an entry is not bound (the offline tests and a dedicated server's early frames).
internal static class LakeSettings
{
  // The config's Lake Depth Scale: what a new world copies. The world's own value is kept with its lakes (LakeSet.DepthScale).
  internal static ConfigEntry<float>? DepthScale;

  // Live: read while the game runs, on this machine only, never part of a world.
  internal static ConfigEntry<float>? BuildRange, BuildBudgetMs, SoundVolume;
  internal static ConfigEntry<bool>? OnMap;

  // Lakes within this many metres of the player are built, and dropped again DropMargin metres further out.
  internal const float DefaultBuildRange = 400f, DropMargin = 150f;
  internal const float DefaultBuildBudgetMs = 2f;
  internal const float DefaultSoundVolume = 0.35f;

  internal static float BuildRangeValue => BuildRange?.Value ?? DefaultBuildRange;
  internal static float BuildBudgetValue => BuildBudgetMs?.Value ?? DefaultBuildBudgetMs;
  internal static float SoundVolumeValue => SoundVolume?.Value ?? DefaultSoundVolume;
  internal static bool OnMapValue => OnMap?.Value ?? true;
}
