// Added by Wubarrk on 2026-09-22 for alt-biome planting (0.8.1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static BetterContinents.BetterContinents;

namespace BetterContinents;

// "bc ab ..." - the alt-biome report, map pins and export, the planted alt-biome map, and live edits of the
// world's alt-biome settings. Like every other "bc" value, edits change the loaded world's settings and are saved
// with the world; each redoes only as much of the pipeline as it affects and then resets the zones.
public partial class DebugUtils
{
    private static void PrintLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            Console.instance.Print(line);
    }

    private static Action<T> SetAltBiomeValue<T>(Action<AltBiomeSettings, T> set, AltBiomeControl.RebuildLevel level) =>
        value =>
        {
            set(BetterContinents.Settings.EditAltBiomes(), value);
            AltBiomeControl.Configure();
            AltBiomeControl.RequestRebuild(level, "alt-biome setting changed", GameUtils.ResetZones);
        };

    private static void RerunPlacement(string reason)
    {
        AltBiomeControl.Configure();
        AltBiomeControl.RequestRebuild(AltBiomeControl.RebuildLevel.Assignment, reason, GameUtils.ResetZones);
    }

    // Planting changes sectors but never the point grid.
    internal static void ReplantAltBiomes(string reason)
    {
        AltBiomeControl.Configure();
        AltBiomeControl.RequestRebuild(AltBiomeControl.RebuildLevel.Sectors, reason, GameUtils.ResetZones);
    }

    private static void AddAltBiomeCommands(Command.SubcommandBuilder bc)
    {
        bc.AddGroup("ab", "Alt biomes", "Alt-biome report, planting and per-world controls, get more info with 'bc ab help'", ab =>
        {
            ab.AddCommand("info", "Report", "What the alt-biome pipeline produced for this world, and why each alt biome did or did not place",
                _ =>
                {
                    if (!AltBiomeControl.WorldEnabled)
                    {
                        Console.instance.Print("Better Continents is not enabled for this world.");
                        return;
                    }
                    PrintLines(AltBiomeReport.Summary(detailed: false));
                    PrintLines(AltBiomeReport.PlantedLines(40));
                });
            ab.AddCommand("list", "List sectors", "Sectors with alt biomes; or 'planted', 'random', 'all', a biome name, or part of an alt-biome name",
                args => PrintLines(AltBiomeReport.ListSectors(args ?? "", 60)));
            ab.AddCommand("here", "Sector here", "The sector you are standing in, what was planted on it, and how each alt biome for its biome fared",
                _ =>
                {
                    if (Player.m_localPlayer != null)
                        PrintLines(AltBiomeReport.Here(Player.m_localPlayer.transform.position));
                });
            ab.AddCommand("show", "Show on map", "Pins every sector that has an alt biome (optional alt-biome name filter)",
                args => Console.instance.Print($"Pinned {AltBiomeReport.ShowPins(args ?? "")} alt-biome sectors"));
            ab.AddCommand("hide", "Hide pins", "Removes the pins added by 'bc ab show'", _ => AltBiomeReport.HidePins());
            ab.AddCommand("export", "Export", "Writes altbiomes-<time>.png (the sector grid, north up, alt-biome sectors tinted, planted ones striped) and a .txt report",
                _ =>
                {
                    try
                    {
                        Console.instance.Print($"Alt-biome map exported to {AltBiomeReport.Export()}");
                    }
                    catch (Exception e)
                    {
                        Console.instance.Print($"<color=#ff0000>Export failed: {e.Message}</color>");
                    }
                });
            ab.AddCommand("hash", "Agreement hashes", "This machine's sector-grid and placement hashes (compare with 'bc_altbiomes hash' on a client)",
                _ => PrintLines(AltBiomeReport.ClientSummary()));
            ab.AddCommand("names", "Alt-biome names", "The game's alt biomes with their default legend colours and random-placement rules (optional filter)",
                args => PrintLines(AltBiomeReport.Names(args ?? "")));
            ab.AddCommand("rebuild", "Rebuild", "Regenerates the point grid, sectors and alt biomes from the current settings, then resets the zones",
                _ =>
                {
                    AltBiomeControl.Configure();
                    AltBiomeControl.RequestRebuild(AltBiomeControl.RebuildLevel.Points, "rebuild", GameUtils.ResetZones);
                    Console.instance.Print("Rebuilding the alt-biome grid, sectors and placement. Connected clients keep theirs until they reconnect.");
                });

            ab.AddValue("fn", "Altbiomemap Filename",
                "Sets the alt-biome map (full path, directory or file name); its legend is the .txt beside it, written with the default palette when missing. Empty removes the map",
                defaultValue: string.Empty,
                setter: path =>
                {
                    var fullPath = BetterContinents.Settings.ResolveAltBiomePath(path);
                    BetterContinents.Settings.SetAltBiomePath(fullPath);
                    if (BetterContinents.Settings.HasAltBiomeMap)
                        Console.instance.Print("<color=#ffa500>Altbiomemap enabled!</color>");
                    else if (string.IsNullOrEmpty(path))
                        Console.instance.Print("<color=#ff0000>Altbiomemap disabled!</color>");
                    else
                        Console.instance.Print($"<color=#ff0000>ERROR: Path {path} not found!</color>");
                    ReplantAltBiomes("alt-biome map changed");
                },
                getter: () => BetterContinents.Settings.GetAltBiomePath());
            ab.AddValue("mode", "Mode", "Random = the game's random placement on unplanted land plus planted regions; PlantedOnly = only planted regions; Off = no alt biomes at all",
                defaultValue: "Random", list: ["Random", "PlantedOnly", "Off"],
                setter: SetAltBiomeValue<string>((s, v) => s.Mode = AltBiomeSettings.ParseEnum(v, s.Mode), AltBiomeControl.RebuildLevel.Sectors),
                getter: () => BetterContinents.Settings.EffectiveAltBiomes.Mode.ToString());
            ab.AddValue("grid", "Grid", "WorldEdge = stop sampling at the edge of the world when it is inside vanilla's 10500 m; Vanilla = always 10500 m",
                defaultValue: "WorldEdge", list: ["WorldEdge", "Vanilla"],
                setter: SetAltBiomeValue<string>((s, v) => s.Grid = AltBiomeSettings.ParseEnum(v, s.Grid), AltBiomeControl.RebuildLevel.Points),
                getter: () => BetterContinents.Settings.EffectiveAltBiomes.Grid.ToString());
            ab.AddValue("seed", "Placement seed", "Seed for random alt-biome placement: a number or any text; 'world' uses the world seed",
                defaultValue: "world",
                setter: SetAltBiomeValue<string>((s, v) =>
                {
                    s.UseFixedSeed = AltBiomeSettings.TryParseSeed(v, out var seed);
                    s.Seed = s.UseFixedSeed ? seed : 0;
                }, AltBiomeControl.RebuildLevel.Assignment),
                getter: () => BetterContinents.Settings.EffectiveAltBiomes.SeedText);
            ab.AddValue("chance", "Chance multiplier", "Multiplies every alt biome's random placement chance",
                defaultValue: 1f, minValue: 0f, maxValue: 10f,
                setter: SetAltBiomeValue<float>((s, v) => s.ChanceMultiplier = Math.Max(0f, v), AltBiomeControl.RebuildLevel.Assignment),
                getter: () => BetterContinents.Settings.EffectiveAltBiomes.ChanceMultiplier);
            ab.AddValue("amount", "Amount multiplier", "Multiplies every alt biome's minimum and maximum number of regions",
                defaultValue: 1f, minValue: 0f, maxValue: 10f,
                setter: SetAltBiomeValue<float>((s, v) => s.AmountMultiplier = Math.Max(0f, v), AltBiomeControl.RebuildLevel.Assignment),
                getter: () => BetterContinents.Settings.EffectiveAltBiomes.AmountMultiplier);
            ab.AddValue("regionscale", "Region size scale", "Multiplies every alt biome's region size (edge length) window",
                defaultValue: 1f, minValue: 0.1f, maxValue: 50f,
                setter: SetAltBiomeValue<float>((s, v) => s.EdgeScale = Math.Max(0.01f, v), AltBiomeControl.RebuildLevel.Assignment),
                getter: () => BetterContinents.Settings.EffectiveAltBiomes.EdgeScale);
            ab.AddValue("distancescale", "Distance scale", "Multiplies every alt biome's minimum distance from the centre and its world bounds",
                defaultValue: 1f, minValue: 0f, maxValue: 10f,
                setter: SetAltBiomeValue<float>((s, v) => s.DistanceScale = Math.Max(0f, v), AltBiomeControl.RebuildLevel.Assignment),
                getter: () => BetterContinents.Settings.EffectiveAltBiomes.DistanceScale);
            ab.AddValue("thickness", "Min sector thickness", "Regions thinner than this (area / edge, in grid cells) never get a random alt biome; 0 = vanilla",
                defaultValue: 0f, minValue: 0f, maxValue: 20f,
                setter: SetAltBiomeValue<float>((s, v) => s.MinSectorThickness = Math.Max(0f, v), AltBiomeControl.RebuildLevel.Assignment),
                getter: () => BetterContinents.Settings.EffectiveAltBiomes.MinSectorThickness);
            ab.AddValue("meanheight", "Mean sector height", "Measure a region's height as its mean instead of vanilla's (lowest + highest) / 2 of its border",
                defaultValue: false,
                setter: SetAltBiomeValue<bool>((s, v) => s.MeanSectorHeight = v, AltBiomeControl.RebuildLevel.Assignment),
                getter: () => BetterContinents.Settings.EffectiveAltBiomes.MeanSectorHeight);
            ab.AddValue("neighbourfix", "Fix neighbour check", "Use a corrected require/not-neighbour test (vanilla's is broken in 1.0.15)",
                defaultValue: false,
                setter: SetAltBiomeValue<bool>((s, v) => s.FixNeighbourCheck = v, AltBiomeControl.RebuildLevel.Assignment),
                getter: () => BetterContinents.Settings.EffectiveAltBiomes.FixNeighbourCheck);

            ab.AddCommand("set", "Set override",
                "bc ab set <alt name|pattern|*> <field> <value>; fields: " + string.Join(" ", AltBiomeOverride.FieldNames) + "; value 'default' clears the field",
                args =>
                {
                    var parts = (args ?? "").Split([' '], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3)
                    {
                        Console.instance.Print("Usage: bc ab set <alt name|pattern|*> <field> <value>");
                        return;
                    }
                    var name = string.Join(" ", parts.Take(parts.Length - 2));
                    var settings = BetterContinents.Settings.EditAltBiomes();
                    if (!settings.Overrides.TryGetValue(name, out var entry))
                        entry = new AltBiomeOverride();
                    if (!entry.TrySet(parts[parts.Length - 2], parts[parts.Length - 1], out var error))
                    {
                        Console.instance.Print($"<color=#ff0000>{error}</color>");
                        return;
                    }
                    if (entry.IsEmpty) settings.Overrides.Remove(name);
                    else settings.Overrides[name] = entry;
                    bool known = AltBiomeSettings.IsPattern(name)
                        ? AltBiomeList.m_altBiomes.Any(a => AltBiomeSettings.GlobMatches(name, a.m_name ?? ""))
                        : AltBiomeControl.FindAltBiome(name) != null;
                    if (!known)
                        Console.instance.Print($"<color=#ffa500>Note: no loaded alt biome matches '{name}'.</color>");
                    Console.instance.Print($"{name}: {(entry.IsEmpty ? "(no overrides)" : entry.ToString())}");
                    RerunPlacement("override changed");
                });
            ab.AddCommand("clear", "Clear overrides", "bc ab clear [alt name|pattern|*]; with no name clears every override",
                args =>
                {
                    var name = (args ?? "").Trim();
                    var settings = BetterContinents.Settings.EditAltBiomes();
                    if (name == "") settings.Overrides.Clear();
                    else settings.Overrides.Remove(name);
                    Console.instance.Print(name == "" ? "Cleared all alt-biome overrides." : $"Cleared the overrides for {name}.");
                    RerunPlacement("overrides cleared");
                });
            ab.AddCommand("overrides", "Show overrides", "Prints this world's alt-biome overrides",
                _ =>
                {
                    var text = BetterContinents.Settings.EffectiveAltBiomes.FormatOverrides();
                    Console.instance.Print(text == "" ? "No alt-biome overrides." : text);
                });
            ab.AddCommand("reroll", "Reroll placement", "Sets a new fixed placement seed (or the one given) and re-runs placement",
                args =>
                {
                    if (!AltBiomeSettings.TryParseSeed(args ?? "", out var seed))
                        seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
                    var settings = BetterContinents.Settings.EditAltBiomes();
                    settings.UseFixedSeed = true;
                    settings.Seed = seed;
                    Console.instance.Print($"Alt-biome placement seed is now {seed.ToString(CultureInfo.InvariantCulture)}.");
                    RerunPlacement("reroll");
                });
        });
    }
}
