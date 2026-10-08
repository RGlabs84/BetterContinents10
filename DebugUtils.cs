// Modified by Wubarrk on 2026-09-22 for Valheim 1.0.15 support (0.8.0) and alt-biome planting (0.8.1), and on 2026-09-24 for world export and import (0.9.0), and on 2026-09-29 for Expand World Data biomes (0.9.3), and on 2026-10-04 for the unifying refactor and the vegetation twin guard (0.10.0), and on 2026-10-06 for 16k worlds (0.10.3), and on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using static BetterContinents.BetterContinents;
using MapKind = BetterContinents.BetterContinents.BetterContinentsSettings.MapKind;
#nullable disable
namespace BetterContinents;

[HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
public class PlayerPatch
{
    private static void Postfix()
    {
        if (BetterContinents.AllowDebugActions)
        {
            if (!Terminal.m_cheat)
                Console.instance.TryRunCommand("devcommands");
        }
    }
}
[HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
public partial class DebugUtils
{
    static void Postfix()
    {
        new DebugUtils();
    }
    private static readonly string[] Bosses =
    [
            "StartTemple", "Eikthyrnir", "GDKing", "GoblinKing", "Bonemass", "Dragonqueen",
            "Vendor_BlackForest", "Mistlands_DvergrBossEntrance1"
        ];

    static DebugUtils()
    {
        // Named args: 1.0.15 inserted `bool hideBehindDevCommands` between `allowInDevBuild` and `optionsFetcher`
        // (Terminal.cs:152), so positional bools past the action delegate are no longer safe to rely on here.
        new Terminal.ConsoleCommand("bc", "Root Better Continents command", args => RunConsoleCommand(args.FullLine.Trim()),
            isCheat: true, isNetwork: false, onlyServer: true);
        // Read-only, and without positions unless devcommands is on, so any player can run it on a client.
        AltBiomeCommands.Register();
        // Any player too, gated by WorldExport.Allowed; "bc_export server" reaches a dedicated server's own world.
        WorldExportCommands.Register();
        // Any player, in the main menu too (InitTerminal runs when the menu's console wakes): it reads local folders and
        // writes the player's own presets and config only.
        WorldImportCommands.Register();
        // Any player counts the twins their machine holds; removal runs only where the world is (bc_twins server ...).
        VegetationTwinCommands.Register();
        // bc_bake: the in-game bake. Read-only words run anywhere; the rest runs where the world is (bc_bake server ... for admins).
        BakeCommands.Register();
        rootCommand = new Command("bc", "Better Continents", "Better Continents command").Subcommands(bc =>
        {
            bc.AddCommand("info", "Dump Info", "Prints current settings to console", _ =>
            {
                BetterContinents.Settings.Dump(str =>
                    Console.instance.Print($"<size=15><color=#c0c0c0>{str}</color></size>"));
                Console.instance.Print(
                    $"<color=#ffa500>NOTE: these settings don't map exactly to console param function or the config file, as some of them are derived.</color>");
            });

            if (BetterContinents.Settings.AnyImageMap)
            {
                bc.AddGroup("reload", "Reload", "Reloads and reapplies one or more of the image maps", reload =>
                {
                    foreach (var kind in MapKind.All.Where(k => k.Has(BetterContinents.Settings)))
                    {
                        if (kind == MapKind.AltBiome)
                            reload.AddCommand(kind.ReloadCommand, kind.Name, "Reloads the alt-biome map and its legend, replants the alt biomes and resets the zones",
                                _ =>
                                {
                                    kind.Reload(BetterContinents.Settings);
                                    ReplantAltBiomes("alt-biome map reloaded");
                                });
                        else
                            reload.AddCommand(kind.ReloadCommand, kind.Name, $"Reloads the {kind.Name.ToLowerInvariant()}",
                                HeightmapCommand(_ => kind.Reload(BetterContinents.Settings)));
                    }
                    // Planted regions follow on the grid rebuild that the reset runs.
                    reload.AddCommand("all", "All", "Reloads all image maps", HeightmapCommand(_ =>
                    {
                        foreach (var kind in MapKind.All)
                            if (kind.Has(BetterContinents.Settings))
                                kind.Reload(BetterContinents.Settings);
                    }));
                });
            }

            bc.AddCommand("locs", "Dump locations", "Prints all location spawn instance counts to the console", _ =>
            {
                var locationInstances = ZoneSystem.instance.m_locationInstances;

                var locationTypes = locationInstances.Values
                    .GroupBy(l => l.m_location.m_prefabName)
                    .ToDictionary(g => g.Key, g => g.ToList());
                foreach (var lg in locationTypes)
                {
                    Console.instance.Print($"Placed {lg.Value.Count} {lg.Key} locations");
                }

                foreach (var boss in Bosses)
                {
                    if (!locationTypes.ContainsKey(boss))
                    {
                        Console.instance.Print($"<color=#ffa500>WARNING: No {boss} generated</color>");
                    }
                }
            });
            bc.AddCommand("bosses", "Show bosses", "Shows pins for bosses, start temple and trader",
                _ => GameUtils.ShowOnMap(Bosses));
            bc.AddCommand("show", "Show locations", "Pins locations matching optional filter on the map", args =>
            {
                GameUtils.ShowOnMap((args ?? "")
                    .Split([' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .ToArray());
            });
            bc.AddCommand("hide", "Hide locations", "Removes the pins 'bc show' and 'bc bosses' put on the map, those matching the optional filter or, with none, all of them; your own pins stay", args =>
            {
                GameUtils.HideOnMap((args ?? "")
                    .Split([' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .ToArray());
            });
            bc.AddCommand("clouds", "Clouds", "Toggles minimap clouds", args =>
            {
                if (GameUtils.MinimapCloudsEnabled)
                    GameUtils.DisableMinimapClouds();
                else
                    GameUtils.EnableMinimapClouds();
            });
            bc.AddValue("mapds", "Minimap downscaling", "Sets minimap downscaling factor (for faster updates)",
                defaultValue: 2,
                list: [0, 1, 2, 3],
                getter: () => GameUtils.MinimapDownscalingPower,
                setter: val =>
                {
                    GameUtils.MinimapDownscalingPower = Mathf.Clamp(val, 0, 3);
                    GameUtils.FastMinimapRegen();
                });
            bc.AddCommand("reset", "reset",
                "Redraws the minimap, rebuilds the alt-biome grid and the loaded terrain, and regenerates the zones (as every change does)",
                _ => GameUtils.Reset());
            bc.AddCommand("regen", "Regenerate zones",
                "Regenerates the zones now, the way every change here does: every generated zone is reset and generates again with the current settings when somebody comes near, "
                + "except zones within one zone of something a player built or worked on (a field, a path, levelled ground), a tombstone, a player connected from another machine "
                + "(as far as their game keeps zones loaded around them), or you inside a dungeon. A location that reaches into one of those zones is kept whole. "
                + "Players, what players built, tombstones and tamed animals are never removed, and the loaded terrain is left as it is. A console command in Debug Reset Command runs instead. "
                + "'bc reset' does this after redrawing the minimap and rebuilding the alt-biome grid and the loaded terrain",
                args => ZoneRegen.RunCommand(args, line => Console.instance.Print(line), GameUtils.RegenerateZones));
            bc.AddCommand("scr", "Save map screenshot",
                "Saves the minimap to a png, optionally pass resolution, default is 2048", arg =>
                {
                    var filename = DateTime.Now.ToString("yyyy-dd-M-HH-mm-ss") + ".png";
                    var screenshotDir = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "BetterContinents",
                        WorldGenerator.instance.m_world.m_name);
                    var path = Path.Combine(screenshotDir, filename);
                    int size = string.IsNullOrEmpty(arg) ? 2048 : int.Parse(arg);
                    GameUtils.SaveMinimap(path, size);
                    Console.instance.Print($"Map screenshot saved to {path}, size {size} x {size}");
                });
            bc.AddCommand("savepreset", "Save preset",
                "Saves current world settings as a preset, including a thumbnail, pass preset name as argument (the world's name if none)",
                arg =>
                {
                    // The console passes "" when no name is given, not null.
                    arg = string.IsNullOrWhiteSpace(arg) ? WorldGenerator.instance.m_world.m_name : arg.Trim();
                    Presets.Save(BetterContinents.Settings, arg);
                    Console.instance.Print($"Preset {arg} saved");
                });

            AddAltBiomeCommands(bc);
            AddExportCommands(bc);
            AddBakeCommands(bc);

            bc.AddGroup("g", "Global", "Global settings, get more info with 'bc g help'",
                group =>
                {
                    AddSettings(group, "g");
                });

            bc.AddGroup("h", "Heightmap", "Heightmap settings, get more info with 'bc h help'",
                group =>
                {
                    AddMapFile(group, MapKind.Height);
                    AddSettings(group, "h");
                });

            bc.AddGroup("r", "Roughmap", "Roughmap settings, get more info with 'bc r help'", group =>
            {
                AddMapFile(group, MapKind.Rough);
                AddSettings(group, "r");
            });
            bc.AddGroup("b", "Biomemap", "Biomemap settings, get more info with 'bc b help'", group =>
            {
                AddMapFile(group, MapKind.Biome);
                AddSettings(group, "b");
            });
            bc.AddGroup("terrain", "Terrainmap", "Terrainmap settings, get more info with 'bc terrain help'",
                group =>
                {
                    AddMapFile(group, MapKind.Terrain);
                });
            bc.AddGroup("l", "Locationmap", "Locationmap settings, get more info with 'bc l help'", group =>
            {
                AddMapFile(group, MapKind.Location);
            });

            bc.AddGroup("paint", "Paintmap", "Paintmap settings, get more info with 'bc paint help'", group =>
            {
                AddMapFile(group, MapKind.Paint);
            });
            bc.AddGroup("lava", "Lavamap", "Lavamap settings, get more info with 'bc lava help'", group =>
            {
                AddMapFile(group, MapKind.Lava);
            });
            bc.AddGroup("moss", "Mossmap", "Mossmap settings, get more info with 'bc moss help'", group =>
            {
                AddMapFile(group, MapKind.Moss);
            });
            bc.AddGroup("vegetation", "Vegetationmap", "Vegetationmap settings, get more info with 'bc vegetation help'", group =>
            {
                AddMapFile(group, MapKind.Vegetation);
            });
            bc.AddGroup("spawn", "Spawnmap", "Spawnmap settings, get more info with 'bc spawn help'", group =>
            {
                AddMapFile(group, MapKind.Spawn);
            });
            bc.AddGroup("heat", "Heatmap", "Heatmap settings, get more info with 'bc heat help'", group =>
            {
                AddMapFile(group, MapKind.Heat);
                AddSettings(group, "heat");
            });
            bc.AddGroup("fo", "Forest", "Forest settings, get more info with 'bc fo help'", group =>
            {
                AddMapFile(group, MapKind.Forest);
                AddSettings(group, "fo");
            });
            // bc.AddGroup("ri", "ridge settings, get more info with 'bc param ri help'", 
            // subcmd =>
            // {
            //     AddHeightmapSubcommand(subcmd, "mh", "ridges max height", "(between 0 and 1)", args =>
            //     {
            //         BetterContinents.Settings.MaxRidgeHeight = float.Parse(args);
            //     });
            //     AddHeightmapSubcommand(subcmd, "si", "ridge size", "(between 0 and 1)", args => BetterContinents.Settings.RidgeSize = float.Parse(args));
            //     AddHeightmapSubcommand(subcmd, "bl", "ridge blend", "(between 0 and 1)", args => BetterContinents.Settings.RidgeBlend = float.Parse(args));
            //     AddHeightmapSubcommand(subcmd, "am", "ridge amount", "(between 0 and 1)", args => BetterContinents.Settings.RidgeAmount = float.Parse(args));
            // });
            bc.AddGroup("st", "Start Position", "Start position settings, get more info with 'bc st help'",
                group =>
                {
                    AddSettings(group, "st");
                });

            void AddNoiseCommands(Command.SubcommandBuilder group, NoiseStackSettings.NoiseSettings settings,
                bool isWarp = false, bool isMask = false)
            {
                // Basic
                group.AddValue("nt", "Noise Type", "Noise type",
                    defaultValue: FastNoiseLite.NoiseType.OpenSimplex2,
                    setter: SetHeightmapValue<FastNoiseLite.NoiseType>(value => settings.NoiseType = value),
                    getter: () => settings.NoiseType);
                group.AddValue("fq", "Frequency X", "Frequency x",
                    defaultValue: 0.0005f,
                    setter: SetHeightmapValue<float>(value => settings.Frequency = value),
                    getter: () => settings.Frequency);
                group.AddValue("asp", "Aspect Ratio", "Scales y dimension relative to x",
                    defaultValue: 1,
                    setter: SetHeightmapValue<float>(value => settings.Aspect = value),
                    getter: () => settings.Aspect);

                // Fractal
                group.AddValue("ft", "Fractal Type", "Fractal type",
                    defaultValue: isWarp ? FastNoiseLite.FractalType.None : FastNoiseLite.FractalType.FBm,
                    list: isWarp
                        ? NoiseStackSettings.NoiseSettings.WarpFractalTypes
                        : NoiseStackSettings.NoiseSettings.NonFractalTypes,
                    setter: SetHeightmapValue<FastNoiseLite.FractalType>(value => settings.FractalType = value),
                    getter: () => settings.FractalType);

                if (settings.FractalType != FastNoiseLite.FractalType.None)
                {
                    group.AddValue<int>("fo", "Fractal Octaves", "Fractal octaves",
                        defaultValue: 4, minValue: 1, maxValue: 10,
                        setter: SetHeightmapValue<int>(value => settings.FractalOctaves = value),
                        getter: () => settings.FractalOctaves);
                    group.AddValue("fl", "Fractal Lacunarity", "Fractal lacunarity",
                        defaultValue: 2, minValue: 0, maxValue: 10,
                        setter: SetHeightmapValue<float>(value => settings.FractalLacunarity = value),
                        getter: () => settings.FractalLacunarity);
                    group.AddValue("fg", "Fractal Gain", "Fractal gain",
                        defaultValue: 0.5f, minValue: 0, maxValue: 2,
                        setter: SetHeightmapValue<float>(value => settings.FractalGain = value),
                        getter: () => settings.FractalGain);
                    group.AddValue("ws", "Weighted Strength", "Weighted strength",
                        defaultValue: 0, minValue: -2, maxValue: 2,
                        setter: SetHeightmapValue<float>(value => settings.FractalWeightedStrength = value),
                        getter: () => settings.FractalWeightedStrength);
                    if (settings.FractalType == FastNoiseLite.FractalType.PingPong)
                    {
                        group.AddValue("ps", "Ping-Pong Strength", "Ping-pong strength",
                            defaultValue: 2, minValue: 0, maxValue: 10,
                            setter: SetHeightmapValue<float>(value => settings.FractalPingPongStrength = value),
                            getter: () => settings.FractalPingPongStrength);
                    }
                }

                if (settings.NoiseType == FastNoiseLite.NoiseType.Cellular)
                {
                    // Cellular
                    group.AddValue("cf", "Cellular Distance Function", "Cellular distance function",
                        defaultValue: FastNoiseLite.CellularDistanceFunction.Euclidean,
                        setter: SetHeightmapValue<FastNoiseLite.CellularDistanceFunction>(value =>
                            settings.CellularDistanceFunction = value),
                        getter: () => settings.CellularDistanceFunction);
                    group.AddValue("ct", "Cellular Return Type", "Cellular return type",
                        defaultValue: FastNoiseLite.CellularReturnType.Distance2Div,
                        setter: SetHeightmapValue<FastNoiseLite.CellularReturnType>(value =>
                            settings.CellularReturnType = value),
                        getter: () => settings.CellularReturnType);
                    group.AddValue("cj", "Cellular Jitter", "Cellular jitter",
                        defaultValue: 1, minValue: 0, maxValue: 2,
                        setter: SetHeightmapValue<float>(value => settings.CellularJitter = value),
                        getter: () => settings.CellularJitter);
                }

                if (isWarp)
                {
                    // Warp
                    group.AddValue("dt", "Domain Warp Type", "Domain warp type",
                        defaultValue: FastNoiseLite.DomainWarpType.OpenSimplex2,
                        setter: SetHeightmapValue<FastNoiseLite.DomainWarpType>(value =>
                            settings.DomainWarpType = value),
                        getter: () => settings.DomainWarpType);
                    group.AddValue("da", "Domain Warp Amp", "Domain warp amp",
                        defaultValue: 50, minValue: 0, maxValue: 20000,
                        setter: SetHeightmapValue<float>(value => settings.DomainWarpAmp = value),
                        getter: () => settings.DomainWarpAmp);
                }

                // Filters
                group.AddValue("in", "Invert", "Invert",
                    setter: SetHeightmapValue<bool>(value => settings.Invert = value),
                    getter: () => settings.Invert);

                group.AddValue("ust", "Use Smooth Threshold", "Use smooth threshold",
                    setter: SetHeightmapValue<bool>(value => settings.UseSmoothThreshold = value),
                    getter: () => settings.UseSmoothThreshold);
                group.AddValue("sts", "Smooth Threshold Start", "Smooth threshold start",
                    defaultValue: 0, minValue: -1, maxValue: 1,
                    getter: () => settings.SmoothThresholdStart,
                    setter: SetHeightmapValue<float>(value => settings.SmoothThresholdStart = value));
                group.AddValue("ste", "Smooth Threshold End", "Smooth threshold end",
                    defaultValue: 1, minValue: -1, maxValue: 1,
                    getter: () => settings.SmoothThresholdEnd,
                    setter: SetHeightmapValue<float>(value => settings.SmoothThresholdEnd = value));

                group.AddValue("uth", "Use Threshold", "Use threshold",
                    setter: SetHeightmapValue<bool>(value => settings.UseThreshold = value),
                    getter: () => settings.UseThreshold);
                group.AddValue("th", "Threshold", "Threshold",
                    defaultValue: 0, minValue: 0, maxValue: 1,
                    getter: () => settings.Threshold,
                    setter: SetHeightmapValue<float>(value => settings.Threshold = value));

                group.AddValue("ura", "Use Range", "Use range",
                    setter: SetHeightmapValue<bool>(value => settings.UseRange = value),
                    getter: () => settings.UseRange);
                group.AddValue("ras", "Range Start", "Range start",
                    defaultValue: 0, minValue: -1, maxValue: 1,
                    getter: () => settings.RangeStart,
                    setter: SetHeightmapValue<float>(value => settings.RangeStart = value));
                group.AddValue("rae", "Range End", "Range end",
                    defaultValue: 1, minValue: -1, maxValue: 1,
                    getter: () => settings.RangeEnd,
                    setter: SetHeightmapValue<float>(value => settings.RangeEnd = value));

                group.AddValue("uop", "Use Opacity", "Use opacity",
                    setter: SetHeightmapValue<bool>(value => settings.UseOpacity = value),
                    getter: () => settings.UseOpacity);
                group.AddValue("op", "Opacity", "Opacity",
                    defaultValue: 1, minValue: 0, maxValue: 1,
                    getter: () => settings.Opacity,
                    setter: SetHeightmapValue<float>(value => settings.Opacity = value));

                group.AddValue("blm", "Blend Mode", "How to apply this layer to the previous one",
                    defaultValue: BlendOperations.BlendModeType.Overlay,
                    setter: SetHeightmapValue<BlendOperations.BlendModeType>(value =>
                        settings.BlendMode = value),
                    getter: () => settings.BlendMode);
            }

            bc.AddGroup("hl", "Height Layer Settings", "Height layer settings",
                hl =>
                {
                    var baseNoise = BetterContinents.Settings.BaseHeightNoise;
                    // hl.AddValue<int>("n", "Number of Layers", "set number of layers",
                    //         defaultValue: 1, minValue: 1, maxValue: 5,
                    //         getter: () => baseNoise.NoiseLayers.Count,
                    //         setter: SetHeightmapValue<int>(val => baseNoise.SetNoiseLayerCount(val)))
                    //     .CustomDrawer(cmd =>
                    //     {
                    //         GUILayout.BeginHorizontal();
                    //         if(baseNoise.NoiseLayers.Count > )
                    //         if (GUILayout.Button("-"))
                    //         {
                    //             
                    //         }
                    //         GUILayout.EndHorizontal();
                    //     });

                    hl.AddCommand("add", "Add Layer", "", HeightmapCommand(_ => baseNoise.AddNoiseLayer()));

                    for (int i = 0; i < baseNoise.NoiseLayers.Count; i++)
                    {
                        int index = i;
                        var noiseLayer = baseNoise.NoiseLayers[index];
                        hl.AddGroup(index.ToString(), $"layer {index}", $"layer {index} settings", l =>
                        {
                            l.AddGroup("npreset", "Apply Noise Preset", "", preset =>
                            {
                                preset.AddCommand("def", "Default", "General noise layer",
                                    HeightmapCommand(_ =>
                                        noiseLayer.noiseSettings = NoiseStackSettings.NoiseSettings.Default()));
                                preset.AddCommand("ri", "Ridges", "Ridged noise",
                                    HeightmapCommand(_ =>
                                        noiseLayer.noiseSettings = NoiseStackSettings.NoiseSettings.Ridged()));
                            });
                            l.AddGroup("n", "Noise", $"layer {index} noise settings", nm
                                    => AddNoiseCommands(nm, noiseLayer.noiseSettings))
                                .UIBackgroundColor(new Color32(0xCE, 0xB3, 0xAB, 0x7f));
                            l.AddGroup("nw", "Noise Warp", $"layer {index} noise warp settings", nm =>
                            {
                                nm.AddValue<bool>("on", "Enabled", $"layer {index} noise warp enabled",
                                    defaultValue: false,
                                    setter: SetHeightmapValue<bool>(value =>
                                    {
                                        if (value && noiseLayer.noiseWarpSettings == null)
                                            noiseLayer.noiseWarpSettings =
                                                NoiseStackSettings.NoiseSettings.DefaultWarp();
                                        else if (!value)
                                            noiseLayer.noiseWarpSettings = null;
                                    }),
                                    getter: () => noiseLayer.noiseWarpSettings != null);
                                if (noiseLayer.noiseWarpSettings != null)
                                {
                                    AddNoiseCommands(nm, noiseLayer.noiseWarpSettings, isWarp: true);
                                }
                            }).UIBackgroundColor(new Color32(0xCA, 0xAE, 0xA5, 0x7f));
                            l.AddValue<int>(null, $"Noise layer {index} preview", $"Noise layer {index} preview")
                                .CustomDrawer(_ => DrawNoisePreview(index));

                            if (index > 0)
                            {
                                l.AddGroup("mpreset", "Apply Mask Preset", "", preset =>
                                {
                                    preset.AddCommand("def", "Default", "General mask layer", HeightmapCommand(_ =>
                                    {
                                        noiseLayer.maskSettings = NoiseStackSettings.NoiseSettings.Default();
                                        noiseLayer.maskSettings.SmoothThresholdStart = 0.6f;
                                        noiseLayer.maskSettings.SmoothThresholdEnd = 0.75f;
                                    }));
                                    preset.AddCommand("25%", "25%", "About 25% coverage with smooth threshold",
                                        HeightmapCommand(_ =>
                                        {
                                            noiseLayer.maskSettings = NoiseStackSettings.NoiseSettings.Default();
                                            noiseLayer.maskSettings.SmoothThresholdStart = 0.6f;
                                            noiseLayer.maskSettings.SmoothThresholdEnd = 0.75f;
                                        }));
                                    preset.AddCommand("ri", "Ridges", "Warped with smooth threshold",
                                        HeightmapCommand(_ =>
                                        {
                                            noiseLayer.maskSettings = NoiseStackSettings.NoiseSettings.Default();
                                            noiseLayer.maskSettings.SmoothThresholdStart = 0.6f;
                                            noiseLayer.maskSettings.SmoothThresholdEnd = 0.75f;
                                            noiseLayer.maskWarpSettings =
                                                NoiseStackSettings.NoiseSettings.DefaultWarp();
                                        }));
                                });

                                l.AddGroup("m", "Mask", $"layer {index} mask settings", nm =>
                                {
                                    nm.AddValue<bool>("on", "Enabled", $"layer {index} mask enabled",
                                        defaultValue: false,
                                        setter: SetHeightmapValue<bool>(value =>
                                        {
                                            if (value && noiseLayer.maskSettings == null)
                                                noiseLayer.maskSettings =
                                                    NoiseStackSettings.NoiseSettings.Default();
                                            else if (!value)
                                                noiseLayer.maskSettings = noiseLayer.maskWarpSettings = null;
                                        }),
                                        getter: () => noiseLayer.maskSettings != null);
                                    if (noiseLayer.maskSettings != null)
                                    {
                                        AddNoiseCommands(nm, noiseLayer.maskSettings, isMask: true);
                                    }
                                }).UIBackgroundColor(new Color32(0xBA, 0xA5, 0xFF, 0x7f));
                                if (noiseLayer.maskSettings != null)
                                {
                                    l.AddGroup("mw", "Mask Warp", $"layer {index} mask warp settings", nm =>
                                    {
                                        nm.AddValue<bool>("on", "Enabled", $"layer {index} mask warp enabled",
                                            defaultValue: false,
                                            setter: SetHeightmapValue<bool>(value =>
                                            {
                                                if (value && noiseLayer.maskWarpSettings == null)
                                                    noiseLayer.maskWarpSettings =
                                                        NoiseStackSettings.NoiseSettings.DefaultWarp();
                                                else if (!value)
                                                    noiseLayer.maskWarpSettings = null;
                                            }),
                                            getter: () => noiseLayer.maskWarpSettings != null);
                                        if (noiseLayer.maskWarpSettings != null)
                                        {
                                            AddNoiseCommands(nm, noiseLayer.maskWarpSettings, isWarp: true);
                                        }
                                    }).UIBackgroundColor(new Color32(0xB1, 0x99, 0xFF, 0x7f));
                                    l.AddValue<int>(null, $"Mask layer {index} preview",
                                            $"Mask layer {index} preview")
                                        .CustomDrawer(_ => DrawMaskPreview(index));
                                }
                            }

                            l.AddCommand("delete", "Delete Layer", "",
                                    HeightmapCommand(_ => baseNoise.NoiseLayers.Remove(noiseLayer)))
                                .UIBackgroundColor(new Color(0.5f, 0.1f, 0.1f));
                            if (index > 0 && index < baseNoise.NoiseLayers.Count - 1)
                            {
                                l.AddCommand("down", "Move Down", "Swap this layer with the one below",
                                    HeightmapCommand(_ =>
                                    {
                                        var other = baseNoise.NoiseLayers[index + 1];
                                        baseNoise.NoiseLayers[index + 1] = noiseLayer;
                                        baseNoise.NoiseLayers[index] = other;
                                    }));
                            }
                        });
                    }

                    hl.AddValue<int>(null, $"Final preview", "preview of final heightmap")
                        .CustomDrawer(_ => DrawNoisePreview(baseNoise.NoiseLayers.Count));
                }).UIBackgroundColor(new Color32(0xB4, 0x9A, 0x67, 0x7f));

            // AddHeightmapSubcommand(command, "num", "height noise layer count", "(count from 0 to 4)", args => BetterContinents.Settings.BaseHeightNoise.SetNoiseLayerCount(int.Parse(args)));
            // for (int i = 0; i < 4; i++)
            // {
            //     int index = i;
            //     command.AddSubcommand(index.ToString(), $"height layer {index}", subcmdConfig: subcmdLayer =>
            //     {
            //         subcmdLayer.AddSubcommand("n", $"height layer {index} noise", 
            //             subcmdConfig: subcmdLayerPart => AddNoiseCommands(
            //                 (cmd, desc, args, action, getValue) => AddHeightmapSubcommand(subcmdLayerPart, cmd, desc, args, action, getValue),
            //                 () => BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index].noiseSettings));
            //         subcmdLayer.AddSubcommand("nw", $"height layer {index} noise domain warp", 
            //             subcmdConfig: subcmdLayerPart =>
            //             {
            //                 AddHeightmapSubcommand(subcmdLayerPart, "on", $"enable height layer {index} noise domain warp", "", 
            //                     _ => BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index].noiseWarpSettings ??= NoiseStackSettings.NoiseSettings.Default());
            //                 AddHeightmapSubcommand(subcmdLayerPart, "off", $"disable height layer {index} noise domain warp", "", 
            //                     _ => BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index].noiseWarpSettings = null);
            //                 AddNoiseCommands(
            //                     (cmd, desc, args, action, getValue) =>
            //                         AddHeightmapSubcommand(subcmdLayerPart, cmd, desc, args, action,
            //                             getValue),
            //                     () => BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index]
            //                         .noiseWarpSettings);
            //             });
            //         subcmdLayer.AddSubcommand("m", $"height layer {index} mask", 
            //             subcmdConfig: subcmdLayerPart =>
            //             {
            //                 AddHeightmapSubcommand(subcmdLayerPart, "on", $"enable height layer {index} mask", "", 
            //                     _ => BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index].maskSettings ??= NoiseStackSettings.NoiseSettings.Default());
            //                 AddHeightmapSubcommand(subcmdLayerPart, "off", $"disable height layer {index} mask", "", 
            //                     _ =>
            //                     {
            //                         BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index].maskSettings = null;
            //                         BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index].maskWarpSettings = null;
            //                     });
            //                 AddNoiseCommands(
            //                     (cmd, desc, args, action, getValue) =>
            //                         AddHeightmapSubcommand(subcmdLayerPart, cmd, desc, args, action,
            //                             getValue),
            //                     () => BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index].maskSettings);
            //             });
            //         subcmdLayer.AddSubcommand("mw", $"height layer {index} mask domain warp", 
            //             subcmdConfig: subcmdLayerPart =>
            //             {
            //                 AddHeightmapSubcommand(subcmdLayerPart, "on", $"enable height layer {index} mask domain warp", "", 
            //                     _ => BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index].maskWarpSettings ??= NoiseStackSettings.NoiseSettings.Default());
            //                 AddHeightmapSubcommand(subcmdLayerPart, "off", $"disable height layer {index} mask domain warp", "", 
            //                     _ => BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index].maskWarpSettings = null);
            //                 AddNoiseCommands(
            //                     (cmd, desc, args, action, getValue) =>
            //                         AddHeightmapSubcommand(subcmdLayerPart, cmd, desc, args, action,
            //                             getValue),
            //                     () => BetterContinents.Settings.BaseHeightNoise.NoiseLayers[index].maskWarpSettings);
            //             });
            //     });
            // }
        });
        CommandWrapper.Register("bc", args => GetAutoComplete());
    }

    // The plain values of a console group, from their definitions in SettingsSchema: the console's default, range and help
    // are the config's own, so the two can never disagree.
    private static void AddSettings(Command.SubcommandBuilder group, string name)
    {
        foreach (var setting in SettingsSchema.Console(name))
        {
            switch (setting)
            {
                case SettingDef<float> f: AddSetting(group, f); break;
                case SettingDef<int> i: AddSetting(group, i); break;
                case SettingDef<bool> b: AddSetting(group, b); break;
                case SettingDef<string> s: AddSetting(group, s); break;
                case SettingDef<HighTerrainMode> m: AddSetting(group, m); break;
                default: throw new NotSupportedException($"bc {name} {setting.ConsoleName}: no console value for {setting.ValueType.Name}");
            }
        }
    }

    private static void AddSetting<T>(Command.SubcommandBuilder group, SettingDef<T> setting) where T : IComparable
    {
        Action<T> set = setting.OnLiveChange switch
        {
            // Precision changes how a zone samples the biomes, not the biomes, so no minimap, noise or alt-biome rebuild:
            // DynamicPatch rebuilds the loaded terrain and grass, and the zone reset places the vegetation again.
            LiveChange.Precision => value =>
            {
                setting.Set(BetterContinents.Settings, value);
                DynamicPatch();
                GameUtils.ResetZones();
            },
            // Heightmap Alpha changes how the heightmap is read: it is decoded again before DynamicPatch, which picks the
            // blend by the map it now holds.
            LiveChange.HeightmapDecode => SetHeightmapValue<T>(value =>
            {
                setting.Set(BetterContinents.Settings, value);
                MapKind.Height.Redecode(BetterContinents.Settings);
            }),
            // High Terrain changes which of the game's rules are patched, not the world: no noise, minimap or terrain rebuild, but the patches
            // (DynamicPatch), the alt-biome placement that reads the height limit they lift, and the zones, whose plants, creatures and locations follow the rules.
            LiveChange.Rules => value =>
            {
                setting.Set(BetterContinents.Settings, value);
                HighTerrain.ModeChanged();
            },
            _ => SetHeightmapValue<T>(value => setting.Set(BetterContinents.Settings, value)),
        };
        Func<T> get = () => setting.Get(BetterContinents.Settings);
        if (setting.Limits is { } limits)
            group.AddValue(setting.ConsoleName, setting.ConsoleLabel, setting.Description, setting.Default, limits.Min, limits.Max, set, get);
        else if (typeof(T).IsEnum)
            // An enum is picked from its values: the console's help lists them, and the settings window offers them.
            group.AddValue(setting.ConsoleName, setting.ConsoleLabel, setting.Description, setting.Default, (T[])Enum.GetValues(typeof(T)), set, get);
        else
            group.AddValue(setting.ConsoleName, setting.ConsoleLabel, setting.Description, setting.Default, set, get);
    }

    private static List<string> GetAutoComplete()
    {
        var text = Console.instance.m_input.text;
        // Empty part kept on purpose to detect when going to the next part.
        var parts = text.Split(' ');
        var cmd = rootCommand;
        for (int i = 1; i < parts.Length - 1; i++)
        {
            var subcmd = cmd.GetSubcommands().FirstOrDefault(s => s.cmd == parts[i]);
            if (subcmd == null)
                break;
            cmd = subcmd;
        }
        if (cmd.GetSubcommands().Count == 0)
            return CommandWrapper.Info(cmd.desc);
        return cmd.GetSubcommands().Select(s => s.cmd).ToList();
    }

    public static void RunConsoleCommand(string text)
    {
        rootCommand.Run(text);
    }
    // bc <group> fn: sets a map's file from a full path, a directory (its standard file name) or a file name in the
    // Directory; empty switches the map off. Its value is the file the map came from.
    private static void AddMapFile(Command.SubcommandBuilder group, MapKind kind) =>
        AddMapFile(group, kind, $"Sets {kind.Name.ToLowerInvariant()} filename (full path, directory or file name)", SetHeightmapValue<string>);

    private static void AddMapFile(Command.SubcommandBuilder group, MapKind kind, string description, Func<Action<string>, Action<string>> apply) =>
        group.AddValue("fn", $"{kind.Name} Filename", description,
            defaultValue: string.Empty,
            setter: apply(path =>
            {
                if (kind.Set(BetterContinents.Settings, kind.Resolve(path)))
                    Console.instance.Print($"<color=#ffa500>{kind.Name} enabled!</color>");
                else if (string.IsNullOrEmpty(path))
                    Console.instance.Print($"<color=#ff0000>{kind.Name} disabled!</color>");
                else
                    Console.instance.Print($"<color=#ff0000>{kind.NotLoaded(path)}</color>");
            }),
            getter: () => kind.Get(BetterContinents.Settings));

    // DynamicPatch, as for a value: the first layer of a world with no heightmap, or a heightmap reloaded, changes which
    // GetBaseHeight patch the world needs (Patcher.PatchGetBaseHeight).
    private static Action<string> HeightmapCommand(Action<string> command) =>
        value =>
        {
            command(value);
            WorldGeneratorPatch.ApplyNoiseSettings();
            DynamicPatch();
            noisePreviewTextures = null;
            maskPreviewTextures = null;
            GameUtils.Reset();
        };

    private static Action<T> SetHeightmapValue<T>(Action<T> setValue) =>
        value =>
        {
            setValue(value);
            WorldGeneratorPatch.ApplyNoiseSettings();
            DynamicPatch();
            noisePreviewTextures = null;
            maskPreviewTextures = null;
            GameUtils.Reset();
        };

    private static readonly Command rootCommand;
    private static List<Texture> noisePreviewTextures = null;
    private static List<Texture> maskPreviewTextures = null;
    private static readonly List<bool> noisePreviewExpanded = [];

    private const int NoisePreviewSize = 512;
    private static (Texture noise, Texture mask) GetPreviewTextures(int layerIndex)
    {
        if (noisePreviewTextures == null)
        {
            var noise = BetterContinents.WorldGeneratorPatch.BaseHeightNoise;
            noisePreviewTextures = [];
            maskPreviewTextures = [];
            for (int i = 0; i < noise.layers.Count; i++)
            {
                noisePreviewTextures.Add(CreateNoisePreview((x, y) => noise.layers[i].noise.GetNoise(x, y),
                    NoisePreviewSize));
                maskPreviewTextures.Add(noise.layers[i].mask != null
                    ? CreateNoisePreview((x, y) => noise.layers[i].mask.Value.GetNoise(x, y), NoisePreviewSize)
                    : null);
            }

            noisePreviewTextures.Add(CreateNoisePreview((x, y) => noise.Apply(x, y), NoisePreviewSize));
            maskPreviewTextures.Add(null);
        }

        return (noisePreviewTextures[layerIndex], maskPreviewTextures[layerIndex]);
    }

    private static Texture CreateNoisePreview(Func<float, float, float> noiseFn, int size = 128)
    {
        var tex = new Texture2D(size, size);
        var pixels = new Color32[size * size];
        float totalRadius = BetterContinents.Geometry.TotalRadius;
        GameUtils.SimpleParallelFor(4, 0, size, y =>
        {
            float yp = 2f * (y / (float)size - 0.5f) * totalRadius;
            for (int x = 0; x < size; ++x)
            {
                float xp = 2f * (x / (float)size - 0.5f) * totalRadius;
                byte val = (byte)Mathf.Clamp((int)(noiseFn(xp, yp) * 255f), 0, 255);
                pixels[y * size + x] = new Color32(val, val, val, byte.MaxValue);
            }
        });

        tex.SetPixels32(pixels);
        tex.Apply(false);
        return tex;
    }

    private static void DrawNoisePreview(int i)
    {
        var (noiseTexture, _) = GetPreviewTextures(i);

        noisePreviewExpanded.Resize(Mathf.Max(noisePreviewExpanded.Count, noisePreviewTextures.Count));
        noisePreviewExpanded[i] = GUILayout.Toggle(noisePreviewExpanded[i], i == noisePreviewTextures.Count - 1 ? "Preview Final" : $"Preview Layer {i} Noise");
        if (noisePreviewExpanded[i])
        {
            GUILayout.Box(noiseTexture);
        }
    }

    private static void DrawMaskPreview(int i)
    {
        var (_, maskTexture) = GetPreviewTextures(i);
        noisePreviewExpanded.Resize(Mathf.Max(noisePreviewExpanded.Count, maskPreviewTextures.Count));
        noisePreviewExpanded[i] = GUILayout.Toggle(noisePreviewExpanded[i], i == maskPreviewTextures.Count - 1 ? "Preview Final Mask" : $"Preview Layer {i} Mask");
        if (noisePreviewExpanded[i])
        {
            GUILayout.Box(maskTexture);
        }
    }
}

#nullable enable