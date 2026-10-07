// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
//
// The High Terrain setting (SettingsSchema.HighTerrain, HighTerrainMode: Auto, On, Off): which worlds want the patches of the game's height rules
// (HighTerrain.Wanted), the top the patches stand on where there is no heightmap, what a world saves and sends of it (DataKey.HighTerrain: disk, network, the
// older formats, a preset file, a world that never had the key, a number this version does not know), how a config and a new world read it, and how the
// bc console parses it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BetterContinents;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

internal static partial class Program
{
  private static readonly HighTerrainMode[] AllModes = [HighTerrainMode.Auto, HighTerrainMode.On, HighTerrainMode.Off];

  // A Better Continents world in a mode: with a heightmap (white) read at an amount, or without one.
  private static BC.BetterContinentsSettings ModeWorld(HighTerrainMode mode, float amount, bool heightmap, bool overrideAll = true, bool enabled = true)
  {
    var s = heightmap
      ? HighWorld(amount, overrideAll: overrideAll, enabled: enabled)
      : new BC.BetterContinentsSettings { EnabledForThisWorld = enabled, Version = 12, HeightmapAmount = amount, HeightmapOverrideAll = overrideAll };
    s.HighTerrainMode = mode;
    return s;
  }

  // A world whose white heightmap is transparent on its left half, read to blend with the game's own terrain (Heightmap Alpha).
  private static BC.BetterContinentsSettings AlphaWorld(float amount, bool overrideAll = true)
  {
    using var image = new Image<La32>(4, 4);
    for (int y = 0; y < 4; y++)
      for (int x = 0; x < 4; x++)
        image[x, y] = new La32(65535, (ushort)(x < 2 ? 0 : 65535));
    using var stream = new MemoryStream();
    image.Save(stream, new PngEncoder { ColorType = PngColorType.GrayscaleWithAlpha, BitDepth = PngBitDepth.Bit16 });
    var s = new BC.BetterContinentsSettings { EnabledForThisWorld = true, Version = 12, HeightmapAmount = amount, HeightmapOverrideAll = overrideAll, HeightMapAlpha = true };
    HeightMapField.SetValue(s, ImageMapFloat.Create(stream.ToArray(), s.HeightmapAlphaMode)!);
    return s;
  }

  private static List<string> DumpLines(BC.BetterContinentsSettings s)
  {
    var lines = new List<string>();
    s.Dump(lines.Add);
    return lines;
  }

  private static string OnOff(bool on) => on ? "on" : "off";

  // ---- what each mode wants ---------------------------------------------------------------------------------------------
  private static void ModeTests()
  {
    Section("High Terrain: what Auto, On and Off want");
    try
    {
      (string name, float amount, bool heightmap)[] kinds =
        [("no heightmap", 81f, false), ("Heightmap Amount 1", 1f, true), ("Amount 5", 5f, true), ("Amount 5.01", 5.01f, true), ("Amount 81", 81f, true)];
      foreach (var mode in AllModes)
        foreach (var (name, amount, heightmap) in kinds)
          foreach (var overrideAll in new[] { true, false })
          {
            var s = ModeWorld(mode, amount, heightmap, overrideAll);
            bool byAmount = heightmap && amount > 5f;
            bool want = mode == HighTerrainMode.On || (mode == HighTerrainMode.Auto && byAmount);
            var label = $"{mode}, {name}, Override All {OnOff(overrideAll)}";
            Check(HighTerrain.Wanted(s) == want, $"{label}: the patches are {(want ? "wanted" : "not wanted")} (got {HighTerrain.Wanted(s)})");
            int wanted = BC.WantedToggles(s).Count(HighToggleNames.Contains);
            Check(wanted == (want ? 6 : 0), $"{label}: {wanted} of the six toggles are wanted ({(want ? "all" : "none")} expected)");
            Check(BC.DeepNorthWeatherUsesZ(s) == want, $"{label}: the Deep North weather asks at x and z exactly when the patches are wanted (no biome map)");
            Check(!HighTerrain.Wanted(ModeWorld(mode, amount, heightmap, overrideAll, enabled: false)) && BC.WantedToggles(ModeWorld(mode, amount, heightmap, overrideAll, enabled: false)).Count(HighToggleNames.Contains) == 0,
              $"{label}, Better Continents off for the world: not wanted, whatever the mode");
          }
      Check(HighTerrain.Wanted(ModeWorld(HighTerrainMode.On, 1f, false)) && !HighTerrain.Wanted(new BC.BetterContinentsSettings { HighTerrainMode = HighTerrainMode.On }),
        "On wants a Better Continents world, not the main menu's settings (Better Continents off)");

      // The top the patches stand on does not depend on the mode; where there is no heightmap it is the game's own terrain at its largest.
      foreach (var mode in AllModes)
        Check(Math.Abs(HighTerrain.MaxMetres(ModeWorld(mode, 81f, true)) - 16170f) < 0.5f && Math.Abs(HighTerrain.MaxMetres(ModeWorld(mode, 2f, true)) - 370f) < 0.5f,
          $"{mode}: the top of a heightmap world is the same whatever the mode (Amount 81: 16170 m, Amount 2: 370 m)");
      float mostBase = NoiseCombinations(6).Max(GameBase);
      float field = FieldNoise(1.15f, 1.15f, 1.15f, 1.15f);
      float own = HighTerrain.MaxMetres(ModeWorld(HighTerrainMode.On, 81f, false));
      float model = 200f * (2f * mostBase - 0.4f + 0.2f * field + 0.013f * 1.15f);
      Check(Math.Abs(own - model) < 0.5f && own > 1900f && own < 1920f,
        $"no heightmap: the land is the game's own, the Mountain biome over its largest base height, {own:0.#} m (the game's formulas over every noise extreme: {model:0.#} m)");
      foreach (var mode in AllModes)
        foreach (var amount in new[] { 1f, 81f })
          Check(HighTerrain.MaxMetres(ModeWorld(mode, amount, false)) == own && HighTerrain.MaxMetres(ModeWorld(mode, amount, false, overrideAll: false)) == own,
            $"{mode}, no heightmap, Amount {amount}: the same top ({own:0.#} m): the amount multiplies nothing, and Override All only means something with a heightmap");
      var shifted = ModeWorld(HighTerrainMode.On, 1f, false);
      shifted.SeaLevelAdjustment = 1f;
      Check(Math.Abs(HighTerrain.MaxMetres(shifted) - (own + 400f)) < 0.5f, "a Sea Level shift of +1 lifts the game's own ground by a unit, which the Mountain biome doubles: +400 m (a world made by the older formulas has it)");
      Check(HighTerrain.MaxMetres(ModeWorld(HighTerrainMode.On, 81f, false, enabled: false)) == 0f, "Better Continents off: 0 m");

      // A heightmap with an alpha channel shows the game's own terrain where it is transparent: the top is the larger of the two.
      float alphaOn = HighTerrain.MaxMetres(AlphaWorld(1f)), alphaOff = HighTerrain.MaxMetres(AlphaWorld(1f, overrideAll: false));
      Check(Math.Abs(alphaOn - 200f * HighTerrain.GameBaseMax) < 0.5f && Math.Abs(alphaOff - own) < 0.5f,
        $"a heightmap with alpha at Amount 1: {alphaOn:0.#} m with Override All on (the game's own base height, 200 m a unit), {alphaOff:0.#} m with it off (the Mountain biome over it)");
      Check(Math.Abs(HighTerrain.MaxMetres(AlphaWorld(81f)) - 16170f) < 0.5f && Math.Abs(HighTerrain.MaxMetres(HighWorld(1f)) - 170f) < 0.5f,
        "with alpha at Amount 81 the heightmap's 16170 m is the top; a heightmap without alpha at Amount 1 keeps its 170 m");

      // What Update keeps and says, for the three modes and the changes between them (the log is the player's only word of it).
      HighTerrain.GroundSource = null;
      HighTerrain.Update(new BC.BetterContinentsSettings());
      int mark = CapturingLogHandler.Lines.Count;
      List<string> Said() => CapturingLogHandler.Lines.Skip(mark).Where(l => l.Contains("High terrain")).ToList();
      HighTerrain.Update(ModeWorld(HighTerrainMode.On, 1f, false));
      Check(HighTerrain.Active && Math.Abs(HighTerrain.Top - own) < 0.5f && Said().Count == 1 && Said()[0].Contains("High Terrain is On for this world") && Said()[0].Contains($"{own:0} m"),
        $"On with no heightmap: active, the top {HighTerrain.Top:0.#} m, and says so once ({string.Join(" | ", Said())})");
      HighTerrain.Update(ModeWorld(HighTerrainMode.On, 1f, false));
      Check(Said().Count == 1, "the same world again: nothing said again");
      HighTerrain.Update(ModeWorld(HighTerrainMode.Off, 1f, true));
      Check(!HighTerrain.Active && HighTerrain.Top == 0f && Said().Count == 2 && Said()[1].Contains("off (no high world is loaded)"),
        "On to Off (as a console change): inactive, and says the game's rules apply again");
      HighTerrain.Update(ModeWorld(HighTerrainMode.Off, 81f, true));
      Check(!HighTerrain.Active && HighTerrain.Top == 0f && Said().Count == 3 && Said()[2].Contains("High Terrain is Off for this world") && Said()[2].Contains("Heightmap Amount 81")
            && new[] { "no grass above 500 m", "above about 1,000 m", "above 3,000 m counts as inside a dungeon", "no ground is found above 6,000 m" }.All(Said()[2].Contains),
        $"Off over a heightmap of Amount 81: inactive, and says what that costs ({(Said().Count > 2 ? Said()[2] : "nothing said")})");
      HighTerrain.Update(ModeWorld(HighTerrainMode.Off, 81f, true));
      Check(Said().Count == 3, "Off again: nothing said again");
      HighTerrain.Update(ModeWorld(HighTerrainMode.Auto, 81f, true));
      Check(HighTerrain.Active && Math.Abs(HighTerrain.Top - 16170f) < 0.5f && Said().Count == 4 && Said()[3].Contains("this world's heightmap is read at Heightmap Amount 81, above the 5 that older versions allowed"),
        "Off to Auto at Amount 81: active, and says why");
      HighTerrain.Update(ModeWorld(HighTerrainMode.Off, 81f, true));
      Check(!HighTerrain.Active && Said().Count == 5 && Said()[4].Contains("High Terrain is Off for this world") && !Said().Any(l => l.Contains("off (no high world is loaded)") && Said().IndexOf(l) > 1),
        "Auto to Off at Amount 81: inactive, and says what Off costs, not that no high world is loaded");
      HighTerrain.Update(new BC.BetterContinentsSettings());
      Check(Said().Count == 5, "the main menu after an Off world: nothing to say");
      HighTerrain.Update(ModeWorld(HighTerrainMode.Off, 2f, true));
      Check(Said().Count == 5, "Off at an amount Auto would leave alone says nothing: there is no cost");

      // bc info and the log's dump say the mode where it is not Auto.
      Check(DumpLines(ModeWorld(HighTerrainMode.On, 1f, false)).Any(l => l.StartsWith("High Terrain On:") && l.Contains("whatever its heights")),
        "the dump says High Terrain On, with no heightmap too");
      Check(DumpLines(ModeWorld(HighTerrainMode.On, 81f, true)).Count(l => l.StartsWith("High Terrain On:")) == 1 && !DumpLines(ModeWorld(HighTerrainMode.On, 81f, true)).Any(l => l.StartsWith("High terrain:")),
        "On over a heightmap: said once, as On");
      Check(DumpLines(ModeWorld(HighTerrainMode.Off, 81f, true)).Any(l => l.StartsWith("High Terrain Off:") && l.Contains("whatever Heightmap Amount is")) && !DumpLines(ModeWorld(HighTerrainMode.Off, 81f, true)).Any(l => l.StartsWith("High terrain:")),
        "the dump says High Terrain Off over a heightmap of Amount 81, and not that the land can reach 16170 m");
      Check(DumpLines(ModeWorld(HighTerrainMode.Auto, 81f, true)).Any(l => l.StartsWith("High terrain: the land can reach 16170 m"))
            && !DumpLines(ModeWorld(HighTerrainMode.Auto, 2f, true)).Any(l => l.Contains("High terrain", StringComparison.OrdinalIgnoreCase))
            && !DumpLines(ModeWorld(HighTerrainMode.Auto, 1f, false)).Any(l => l.Contains("High terrain", StringComparison.OrdinalIgnoreCase)),
        "Auto says nothing but the line it always said, at Amount 81 only");
    }
    finally
    {
      HighTerrain.GroundSource = null;
      HighTerrain.Update(new BC.BetterContinentsSettings());
    }
  }

  // ---- the setting, as the config and a new world have it ---------------------------------------------------------------
  private static ConfigFile boundConfig;

  // BetterContinents.cfg bound the way the game binds it (SettingsSchema.Bind), once, in a file of its own.
  private static ConfigFile BoundConfig()
  {
    if (boundConfig != null)
      return boundConfig;
    var path = Path.Combine(Path.GetTempPath(), $"high-tests-{Environment.ProcessId}", "BetterContinents.cfg");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.Delete(path);
    boundConfig = new ConfigFile(path, true);
    BC.DeclareConfig(boundConfig);
    return boundConfig;
  }

  private static void ModeSettingTests()
  {
    Section("High Terrain: the setting");
    var cfg = BoundConfig();
    var setting = SettingsSchema.HighTerrain;
    Check(setting.Key == "High Terrain" && setting.Section == "02 BetterContinents.Heightmap" && setting.Scope == SettingScope.World && setting.Default == HighTerrainMode.Auto
          && setting.ValueType == typeof(HighTerrainMode) && setting.ReadsIntoSettings && setting.Range == null,
      "High Terrain is a world setting of [02 BetterContinents.Heightmap], an enum, Auto by default");
    Check(SettingsSchema.Groups.Single(g => g.Name == "BetterContinents.Heightmap").Settings.Last() == setting && SettingsSchema.All.Count(d => d == setting) == 1,
      "it is the last of its section (a setting in the middle would move the order of the others in the file)");
    Check(setting.ConsoleGroup == "h" && setting.ConsoleName == "ht" && setting.ConsoleLabel == "High Terrain" && setting.OnLiveChange == LiveChange.Rules && SettingsSchema.Console("h").Contains(setting),
      "the console has it as bc h ht, and a live change redoes the rules (LiveChange.Rules), not the world");
    var entry = BC.ConfigHighTerrain;
    Check(entry != null && entry.Value == HighTerrainMode.Auto && entry.Description.Tags.OfType<ConfigurationManagerAttributes>().Single().Order == -10,
      "the config entry is bound, Auto, and ordered last in the section");
    // What the description says: the three modes, what Off costs, when a new world takes it, how to change it live; and no version number.
    var text = setting.Description;
    Check(new[] { "Auto (the default)", "On: every Better Continents world", "Off: none does", "no grass above 500 m", "above about 1,000 m", "above 3,000 m", "no ground found above 6,000 m", "bc h ht" }.All(text.Contains)
          && !System.Text.RegularExpressions.Regex.IsMatch(text, @"\d+\.\d+\.\d+") && !text.Contains("0.10"),
      "its description says what each mode does and what Off costs, and names no version");
    // The config file as BepInEx writes it: the type, the values it takes, the default.
    cfg.Save();
    var file = File.ReadAllText(cfg.ConfigFilePath);
    Check(file.Contains("# Setting type: HighTerrainMode") && file.Contains("# Acceptable values: Auto, On, Off") && file.Contains("High Terrain = Auto") && file.Contains("# Default value: Auto"),
      "BetterContinents.cfg holds it as High Terrain = Auto, with the values it takes");

    // Text in a config or an export.cfg: any case, and what is no mode is refused (the line is ignored, with a reason) or, as a number, read as Auto.
    object Convert(string t) => WorldImport.TryConvert(entry, t, out var value, out _) ? value : "refused";
    Check(Convert("Off").Equals(HighTerrainMode.Off) && Convert("off").Equals(HighTerrainMode.Off) && Convert(" ON ").Equals(HighTerrainMode.On) && Convert("auto").Equals(HighTerrainMode.Auto)
          && Convert("banana").Equals("refused") && Convert("").Equals("refused"),
      "export.cfg's text is read in any case, and a word that is no mode is refused");
    var seven = new BC.BetterContinentsSettings();
    setting.Set(seven, (HighTerrainMode)7);
    Check(seven.HighTerrainMode == HighTerrainMode.Auto && HighTerrainModes.Of(7) == HighTerrainMode.Auto && HighTerrainModes.Of(-1) == HighTerrainMode.Auto && HighTerrainModes.Of(2) == HighTerrainMode.Off,
      "a number that is no mode (a 7 typed in a config) is Auto in a new world");

    // A new world takes it from the config when it is made (From Config, an import, a Directory's export.cfg all build the settings this way).
    HighTerrainMode Made(HighTerrainMode? value, bool heightmap = false)
    {
      var overrides = new Dictionary<ConfigEntryBase, object> { [BC.ConfigEnabled] = true };
      if (value != null)
        overrides[BC.ConfigHighTerrain] = value.Value;
      return BC.BetterContinentsSettings.CreateForImport(ConfigValues.Snapshot(cfg, overrides!), lean: false).HighTerrainMode;
    }
    Check(Made(null) == HighTerrainMode.Auto && Made(HighTerrainMode.On) == HighTerrainMode.On && Made(HighTerrainMode.Off) == HighTerrainMode.Off && Made(HighTerrainMode.Auto) == HighTerrainMode.Auto,
      "a new world made from the config is in the mode the config says, Auto when it says nothing");
    var madeOn = BC.BetterContinentsSettings.CreateForImport(ConfigValues.Snapshot(cfg, new Dictionary<ConfigEntryBase, object?> { [BC.ConfigEnabled] = true, [BC.ConfigHighTerrain] = HighTerrainMode.On }), lean: false);
    Check(madeOn.EnabledForThisWorld && madeOn.Version == 12 && HighTerrain.Wanted(madeOn) && !HighTerrain.Wanted(BC.BetterContinentsSettings.CreateForImport(ConfigValues.Snapshot(cfg, new Dictionary<ConfigEntryBase, object?> { [BC.ConfigEnabled] = true }), lean: false)),
      "and it is baked in: On wants the patches for a world with no heightmap, Auto does not");
  }

  // ---- what a world saves and sends of it -------------------------------------------------------------------------------

  private static byte[] SavedBytes(BC.BetterContinentsSettings s, bool network, int version = 12)
  {
    var pkg = new ZPackage();
    s.Serialize(pkg, network, true, version);
    return pkg.GetArray();
  }

  private static BC.BetterContinentsSettings ReadBack(byte[] bytes) => BC.BetterContinentsSettings.Load(new ZPackage(bytes));

  // The key and its value as the package holds them (two ints).
  private static byte[] KeyBlock(int key, int value) => BitConverter.GetBytes(key).Concat(BitConverter.GetBytes(value)).ToArray();

  // Where a block was put into plain to make with (every position tried), or -1: the settings with a key are the settings without it, plus the block.
  private static int BlockAt(byte[] plain, byte[] with, byte[] block)
  {
    if (with.Length != plain.Length + block.Length)
      return -1;
    for (int p = 0; p <= plain.Length; p++)
      if (with.AsSpan(0, p).SequenceEqual(plain.AsSpan(0, p)) && with.AsSpan(p, block.Length).SequenceEqual(block) && with.AsSpan(p + block.Length).SequenceEqual(plain.AsSpan(p)))
        return p;
    return -1;
  }

  private static void ModeSaveTests()
  {
    Section("High Terrain: what a world saves and sends");
    // The numbers of the keys: nothing renumbered, the new one 69 (68 is left for another).
    var keys = Enum.GetValues(typeof(BC.DataKey)).Cast<BC.DataKey>().ToList();
    Check((int)BC.DataKey.HighTerrain == 69 && (int)BC.DataKey.TiledMap == 67 && (int)BC.DataKey.GlobalScale == 0 && keys.Where(k => (int)k < 68).Select(k => (int)k).SequenceEqual(Enumerable.Range(0, 68)),
      "DataKey.HighTerrain is 69; the keys before it are numbered 0 to 67 as ever");

    foreach (var (name, amount, heightmap) in new[] { ("no heightmap", 1f, false), ("Heightmap Amount 81", 81f, true), ("Amount 2", 2f, true) })
      foreach (var network in new[] { false, true })
      {
        var form = network ? "network" : "disk";
        var auto = SavedBytes(ModeWorld(HighTerrainMode.Auto, amount, heightmap), network);
        // Auto writes no key at all (the golden recordings say its bytes are what they were for every existing world); On and Off add the key to them.
        foreach (var mode in new[] { HighTerrainMode.On, HighTerrainMode.Off })
        {
          var s = ModeWorld(mode, amount, heightmap);
          var bytes = SavedBytes(s, network);
          int at = BlockAt(auto, bytes, KeyBlock(69, (int)mode));
          Check(at >= 0, $"{name}, {form}, {mode}: the bytes are the Auto world's with the key 69 and the value {(int)mode} put in (at byte {at})");
          var back = ReadBack(bytes);
          Check(back.EnabledForThisWorld && back.Version == 12 && back.HighTerrainMode == mode, $"{name}, {form}, {mode}: read back, the mode is {back.HighTerrainMode}");
          Check(HighTerrain.Wanted(back) == HighTerrain.Wanted(s) && HighTerrain.MaxMetres(back) == HighTerrain.MaxMetres(s) && DumpLines(back).SequenceEqual(DumpLines(s)),
            $"{name}, {form}, {mode}: the world that reads it wants the patches as the one that wrote it did, to the same top, and says the same of itself");
          Check(SavedBytes(back, network).SequenceEqual(bytes), $"{name}, {form}, {mode}: saved again, the same bytes");
        }
        // A world without the key reads as Auto, and a client that gets the server's package computes what the server does.
        var plain = ReadBack(auto);
        Check(plain.HighTerrainMode == HighTerrainMode.Auto && HighTerrain.Wanted(plain) == (heightmap && amount > 5f), $"{name}, {form}: a world with no key reads as Auto");
      }

    // A number this version does not know (a newer version wrote it): Auto, with a line in the log; the rest of the settings still load.
    var plainBytes = SavedBytes(ModeWorld(HighTerrainMode.Auto, 81f, true), false);
    int place = BlockAt(plainBytes, SavedBytes(ModeWorld(HighTerrainMode.Off, 81f, true), false), KeyBlock(69, 2));
    int mark = CapturingLogHandler.Lines.Count;
    var odd = ReadBack(plainBytes.Take(place).Concat(KeyBlock(69, 9)).Concat(plainBytes.Skip(place)).ToArray());
    Check(odd.EnabledForThisWorld && odd.HighTerrainMode == HighTerrainMode.Auto && odd.HeightmapAmount == 81f && odd.HasHeightMap
          && CapturingLogHandler.Lines.Skip(mark).Any(l => l.Contains("High Terrain 9") && l.Contains("read as Auto")),
      "High Terrain 9, which no mode is: read as Auto, said in the log, and the rest of the settings load");

    // The older settings formats have no keys: a world saved in one is Auto when it is read, and its bytes are an Auto world's. (Without a heightmap: those formats
    // name a map by its file path, which the in-memory one of these worlds has not.)
    foreach (var version in new[] { 1, 3, 6, 7, 10 })
    {
      var on = ModeWorld(HighTerrainMode.On, 81f, false);
      var legacy = SavedBytes(on, false, version);
      Check(legacy.SequenceEqual(SavedBytes(ModeWorld(HighTerrainMode.Auto, 81f, false), false, version)) && ReadBack(legacy).HighTerrainMode == HighTerrainMode.Auto,
        $"saved in settings version {version}: no key, and read back as Auto");
    }
    // The keyed version 11 holds it as version 12 does.
    var on11 = ModeWorld(HighTerrainMode.Off, 81f, true);
    on11.Version = 11;
    Check(ReadBack(SavedBytes(on11, false, 11)).HighTerrainMode == HighTerrainMode.Off && ReadBack(SavedBytes(on11, true, 11)).HighTerrainMode == HighTerrainMode.Off, "settings version 11 holds it too");

    // A preset is a file of the same bytes (bc savepreset, the export's preset, bc_import).
    var path = Path.Combine(Path.GetTempPath(), $"high-tests-{Environment.ProcessId}", "preset.BetterContinents");
    try
    {
      foreach (var mode in AllModes)
      {
        File.Delete(path);
        ModeWorld(mode, 81f, true).Save(path);
        Check(BC.BetterContinentsSettings.Load(path).HighTerrainMode == mode, $"a preset file saved in {mode} loads in {mode}");
      }
    }
    finally
    {
      File.Delete(path);
    }
  }

  // ---- the console ------------------------------------------------------------------------------------------------------

  private static void ConsoleTests()
  {
    Section("High Terrain: the bc console's parse");
    var parse = typeof(DebugUtils.Command).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;
    object Parsed(Type type, string text)
    {
      try
      {
        return parse.Invoke(null, [type, text])!;
      }
      catch (TargetInvocationException e)
      {
        return e.InnerException!.GetType().Name;
      }
    }
    Check(Parsed(typeof(HighTerrainMode), "off").Equals(HighTerrainMode.Off) && Parsed(typeof(HighTerrainMode), "ON").Equals(HighTerrainMode.On) && Parsed(typeof(HighTerrainMode), "Auto").Equals(HighTerrainMode.Auto),
      "the console reads a mode by its name, in any case");
    Check(new[] { "7", "1", "-1", "banana", "", "Auto, On", "Auto,On", "1, 2", "Offf" }.All(text => Parsed(typeof(HighTerrainMode), text).Equals("ArgumentException")),
      "and refuses a number (one that is a mode too), a list of names, a word that is no mode, and nothing: only a name");
    Check(Parsed(typeof(float), "1.5").Equals(1.5f) && Parsed(typeof(int), "7").Equals(7) && Parsed(typeof(bool), "true").Equals(true) && Parsed(typeof(string), "x y").Equals("x y")
          && Parsed(typeof(Guid), "x").Equals("KeyNotFoundException"),
      "every other type is read as it was (a type it has no reader for fails as it did)");

    // The command as the console's table builds it: a list of the modes, which the help prints and the settings window offers.
    HighTerrainMode set = HighTerrainMode.Auto;
    var root = new DebugUtils.Command("bc", "Better Continents", "test").Subcommands(bc => bc.AddGroup("h", "Heightmap", "test", group =>
      group.AddValue("ht", "High Terrain", "test", HighTerrainMode.Auto, (HighTerrainMode[])Enum.GetValues(typeof(HighTerrainMode)), v => set = v, () => set)));
    root.Run("bc h ht off");
    Check(set == HighTerrainMode.Off, "bc h ht off sets the mode");
    root.Run("bc h ht On");
    Check(set == HighTerrainMode.On, "bc h ht On sets it, in any case");
    var ht = root.GetSubcommands().Single().GetSubcommands().Single();
    Check(ht.cmd == "ht" && ht.desc == "test", "the value is a command of its group");
  }
}
