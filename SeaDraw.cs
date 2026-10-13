// Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).

using BepInEx.Configuration;

namespace BetterContinents;

/// <summary>Where the client side of BC's own water reads its settings (the plan's 8.2): bound by SettingsSchema, null where the config is not (the offline suites).</summary>
internal static class SeaSettings
{
  internal static ConfigEntry<bool>? OwnWater, SoftFarSea;
}
