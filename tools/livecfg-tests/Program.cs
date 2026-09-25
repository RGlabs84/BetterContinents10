// Added by Wubarrk on 2026-09-24 for world export and import (0.9.0).

// Offline checks of Better Continents 0.9.0's live config (LiveConfig), its RPC package, the export gate and the
// configured export defaults. Loads the real pre-ILRepack BetterContinents.dll, the game's assemblies and BepInEx.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using BepInEx.Configuration;
using BetterContinents;
using UnityEngine;
using BC = BetterContinents.BetterContinents;

namespace LiveCfgTest;

internal sealed class LogHandler : ILogHandler
{
  public static readonly List<string> Lines = [];
  public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
  {
    lock (Lines) Lines.Add($"[{logType}] " + (args == null || args.Length == 0 ? format : string.Format(format, args)));
  }
  public void LogException(Exception exception, UnityEngine.Object context)
  {
    lock (Lines) Lines.Add("[Exception] " + exception);
  }
  public static bool Has(string part) { lock (Lines) return Lines.Any(l => l.Contains(part)); }
}

internal static class Program
{
  static int checks, failures;
  static void C(bool ok, string what)
  {
    checks++;
    if (!ok) failures++;
    System.Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
  }
  static void Section(string s) => System.Console.WriteLine("== " + s);

  static int Main()
  {
    var dirs = new[]
    {
      AppContext.BaseDirectory,
      "/home/rohan/WubarrkCODING/libs-Tools/1.0/client",
      "/home/rohan/WubarrkCODING/libs-Tools",
      "/home/rohan/WubarrkCODING/libs-Tools/BepInEx/core",
      "/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed",
    };
    AssemblyLoadContext.Default.Resolving += (ctx, name) =>
    {
      foreach (var d in dirs)
      {
        var p = Path.Combine(d, name.Name + ".dll");
        if (File.Exists(p))
          return ctx.LoadFromAssemblyPath(p);
      }
      return null;
    };
    return Run();
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static int Run()
  {
    UnityEngine.Debug.unityLogger.logHandler = new LogHandler();
    var work = Path.Combine(Path.GetTempPath(), "bc-livecfg-test-" + Environment.ProcessId);
    Directory.CreateDirectory(work);
    try
    {
      Rules();
      Gate();
      var cfg = Bind(work);
      Package();
      Reload(cfg);
      SectionNumbers();
      TransferRateTests();
      failures += CacheTests.Run();
    }
    catch (Exception e)
    {
      System.Console.WriteLine("CRASH " + e);
      failures++;
    }
    finally
    {
      try { Directory.Delete(work, true); } catch { }
    }
    System.Console.WriteLine($"{checks} checks, {failures} failures");
    return failures == 0 ? 0 : 1;
  }

  static void Rules()
  {
    Section("who decides (LiveConfig.EffectiveHud / BlockedReason)");
    foreach (bool local in new[] { false, true })
      foreach (bool? server in new bool?[] { null, false, true })
      {
        C(LiveConfig.EffectiveHud(false, server, local) == local, $"host/single player: Hud = own value {local} (server sent {server?.ToString() ?? "nothing"})");
        C(LiveConfig.BlockedReason(false, server, local) == null, $"host/single player: always allowed (own {local}, server {server?.ToString() ?? "nothing"})");
        bool expectHud = server ?? local;
        C(LiveConfig.EffectiveHud(true, server, local) == expectHud, $"client: Hud = {expectHud} (own {local}, server {server?.ToString() ?? "nothing"})");
        bool expectAllowed = server ?? local;
        var reason = LiveConfig.BlockedReason(true, server, local);
        C((reason == null) == expectAllowed, $"client: allowed = {expectAllowed} (own {local}, server {server?.ToString() ?? "nothing"}), reason '{reason}'");
        if (server == false)
          C(reason != null && reason.Contains("server"), "client refused by the server: the reason names the server");
        if (server == null && !local)
          C(reason != null && reason.Contains("this game's"), "client refused by its own config: the reason says so");
      }
  }

  static void Gate()
  {
    Section("WorldExport.CanExport reason");
    var allowed = WorldExport.Allowed;
    var why = WorldExport.NotAllowedReason;
    try
    {
      WorldExport.Allowed = () => false;
      WorldExport.NotAllowedReason = () => "test reason";
      C(!WorldExport.CanExport(out var r1) && r1 == "test reason", $"a refusal carries NotAllowedReason's text (got '{r1}')");
      WorldExport.NotAllowedReason = null;
      C(!WorldExport.CanExport(out var r2) && r2 == "world export is not allowed here", $"no reason hook: the generic text (got '{r2}')");
      WorldExport.NotAllowedReason = () => throw new InvalidOperationException("boom");
      C(!WorldExport.CanExport(out var r3) && r3 == "world export is not allowed here" && LogHandler.Has("NotAllowedReason check threw"), $"a throwing reason hook: generic text and a warning (got '{r3}')");
      WorldExport.Allowed = () => true;
      C(!WorldExport.CanExport(out var r4) && r4 == "no world is loaded", $"allowed, but no world: 'no world is loaded' (got '{r4}')");
    }
    finally
    {
      WorldExport.Allowed = allowed;
      WorldExport.NotAllowedReason = why;
    }
  }

  static ConfigFile Bind(string work)
  {
    Section("binding the Export group");
    // Before binding: the built-in defaults.
    var builtIn = WorldExport.Options.Default();
    C(builtIn.Size == 4096 && builtIn.HeightmapAmount == 2f && builtIn.SeaLevel == 0.5f, $"unbound config: Options.Default() is the built-in 4096 / 2 / 0.5 ({builtIn.Size} / {builtIn.HeightmapAmount} / {builtIn.SeaLevel})");
    var path = Path.Combine(work, "BetterContinents.cfg");
    var cfg = new ConfigFile(path, true);
    const string S = "09 BetterContinents.Export";
    BC.ConfigExportHud = cfg.Bind(S, "Hud", false, "hud");
    BC.ConfigExportAllowed = cfg.Bind(S, "Allow Export", true, "allow");
    BC.ConfigExportHudKey = cfg.Bind(S, "Hud Hotkey", KeyCode.F9, "hud key");
    BC.ConfigExportWindowKey = cfg.Bind(S, "Window Hotkey", KeyCode.F7, "window key");
    BC.ConfigExportSize = cfg.Bind(S, "Default Size", 4096, new ConfigDescription("size", new AcceptableValueRange<int>(WorldExport.MinSize, WorldExport.MaxSize)));
    BC.ConfigExportHeightmapAmount = cfg.Bind(S, "Default Heightmap Amount", 2f, new ConfigDescription("amount", new AcceptableValueRange<float>(0.01f, 5f)));
    BC.ConfigExportSeaLevel = cfg.Bind(S, "Default Sea Level", 0.5f, new ConfigDescription("sea", new AcceptableValueRange<float>(0f, 1f)));
    var text = File.ReadAllText(path);
    C(text.Contains("[09 BetterContinents.Export]") && text.Contains("Window Hotkey = F7") && text.Contains("Hud Hotkey = F9"), "the file carries the section and the KeyCode values by name");
    LiveConfig.Init(cfg);
    C(LogHandler.Has("Watching " + path), "Init starts the file watcher and says so");
    var d = WorldExport.Options.Default();
    C(d.Size == 4096 && d.HeightmapAmount == 2f && d.SeaLevel == 0.5f, "bound config at its defaults: Options.Default() unchanged");
    C(WorldExport.Allowed() && LiveConfig.ExportBlockedReason() == null, "not connected anywhere: export allowed (the machine runs its own world)");
    return cfg;
  }

  static void Package()
  {
    Section("the BetterContinentsLiveConfig package");
    C(LiveConfig.Rpc == "BetterContinentsLiveConfig", "RPC name");
    foreach (bool hud in new[] { false, true })
      foreach (bool allow in new[] { false, true })
      {
        BC.ConfigExportHud.Value = hud;
        BC.ConfigExportAllowed.Value = allow;
        var sent = LiveConfig.ServerPackage();
        var got = LiveConfig.ReadPackage(new ZPackage(sent.GetArray()));
        C(got.Hud == hud && got.AllowExport == allow, $"round trip hud={hud} allow={allow}");
      }
    // A later format that appends a value is still read.
    var later = new ZPackage();
    later.Write(2); later.Write(true); later.Write(false); later.Write(12345); later.Write("more");
    var l = LiveConfig.ReadPackage(new ZPackage(later.GetArray()));
    C(l.Hud && !l.AllowExport, "format 2 with appended values: the known ones are read");
    var bad = new ZPackage();
    bad.Write(0); bad.Write(true); bad.Write(true);
    bool threw = false;
    try { LiveConfig.ReadPackage(new ZPackage(bad.GetArray())); } catch (InvalidDataException) { threw = true; }
    C(threw, "format 0 is refused");

    // Receiving: the values are stored; a bad package changes nothing and warns.
    LiveConfig.ClearServerValues();
    var p = new ZPackage(); p.Write(1); p.Write(true); p.Write(false);
    LiveConfig.ReceiveServerValues(new ZPackage(p.GetArray()));
    C(LiveConfig.ServerValues == (true, false), $"received values stored ({LiveConfig.ServerValues})");
    C(LogHandler.Has("The server's export settings apply while connected: export HUD on, world export not allowed"), "first receipt is logged");
    LiveConfig.ReceiveServerValues(new ZPackage(bad.GetArray()));
    C(LiveConfig.ServerValues == (true, false) && LogHandler.Has("Could not read the server's export settings"), "a bad package keeps the last values and warns");
    var p2 = new ZPackage(); p2.Write(1); p2.Write(false); p2.Write(true);
    LiveConfig.ReceiveServerValues(new ZPackage(p2.GetArray()));
    C(LiveConfig.ServerValues == (false, true) && LogHandler.Has("the server changed its export settings: export HUD off, world export allowed"), "a later change is applied and announced");
    // Not a client here (no ZNet): the stored server values do not apply.
    C(LiveConfig.HudEnabled == BC.ConfigExportHud.Value, "no connection: HudEnabled is this game's own value");
    LiveConfig.ClearServerValues();
    C(LiveConfig.ServerValues == (null, null), "ClearServerValues forgets them");
    BC.ConfigExportHud.Value = false;
    BC.ConfigExportAllowed.Value = true;
  }

  static void Reload(ConfigFile cfg)
  {
    Section("live reload from disk");
    var path = cfg.ConfigFilePath;
    // Let the watcher's events from the binding saves settle and be consumed first.
    Pump(TimeSpan.FromSeconds(1.2));
    LogHandler.Lines.Clear();
    var text = File.ReadAllText(path);
    var edited = text.Replace("Hud = false", "Hud = true")
                     .Replace("Window Hotkey = F7", "Window Hotkey = F10")
                     .Replace("Default Size = 4096", "Default Size = 2048")
                     .Replace("Default Heightmap Amount = 2", "Default Heightmap Amount = 3");
    C(edited != text && edited.Contains("Hud = true") && edited.Contains("Default Size = 2048") && edited.Contains("Default Heightmap Amount = 3"), "test edit prepared");
    File.WriteAllText(path, edited);
    // Right after the write: nothing yet (debounced).
    LiveConfig.Update();
    C(BC.ConfigExportHud.Value == false, "no reload inside the quiet period");
    var watch = System.Diagnostics.Stopwatch.StartNew();
    while (watch.Elapsed < TimeSpan.FromSeconds(8) && !BC.ConfigExportHud.Value)
    {
      Thread.Sleep(50);
      LiveConfig.Update();
    }
    C(BC.ConfigExportHud.Value, $"Hud reloaded from the file after {watch.ElapsedMilliseconds} ms");
    C(watch.ElapsedMilliseconds >= 450, "not before the 500 ms quiet period");
    C(BC.ConfigExportWindowKey.Value == KeyCode.F10, $"Window Hotkey reloaded as a KeyCode ({BC.ConfigExportWindowKey.Value})");
    C(BC.ConfigExportSize.Value == 2048 && BC.ConfigExportHeightmapAmount.Value == 3f, "defaults reloaded");
    var d = WorldExport.Options.Default();
    C(d.Size == 2048 && d.HeightmapAmount == 3f && d.SeaLevel == 0.5f, $"Options.Default() follows the reloaded config ({d.Size} / {d.HeightmapAmount} / {d.SeaLevel})");
    C(WorldExportCommands.TryParse("", out var o1, out _) && o1.Size == 2048 && o1.HeightmapAmount == 3f, "bc_export with no size uses Default Size and Default Heightmap Amount");
    C(WorldExportCommands.TryParse("1024 amount=2", out var o2, out _) && o2.Size == 1024 && o2.HeightmapAmount == 2f, "bc_export's own arguments still win");
    C(File.ReadAllText(path) == edited, "the reload did not rewrite the player's file");
    C(cfg.SaveOnConfigSet, "SaveOnConfigSet is restored after the reload");
    C(LogHandler.Has("changed on disk: 4 setting(s) reloaded"), "the reload says how many settings changed");
    C(LogHandler.Has("Better Continents export settings: HUD on"), "the Hud change went through the SettingChanged handler");
    // A second, identical write: no settings change, no log line.
    LogHandler.Lines.Clear();
    File.WriteAllText(path, edited);
    Pump(TimeSpan.FromSeconds(1.5));
    C(!LogHandler.Has("changed on disk"), "rewriting the same content reloads silently");
    // An editor's save through a temporary file renamed over the config.
    var tmp = path + ".tmp";
    File.WriteAllText(tmp, edited.Replace("Allow Export = true", "Allow Export = false"));
    File.Move(tmp, path, overwrite: true);
    watch.Restart();
    while (watch.Elapsed < TimeSpan.FromSeconds(8) && BC.ConfigExportAllowed.Value)
    {
      Thread.Sleep(50);
      LiveConfig.Update();
    }
    C(!BC.ConfigExportAllowed.Value, $"a save by rename is picked up too ({watch.ElapsedMilliseconds} ms)");
    C(LiveConfig.ExportBlockedReason() == null, "Allow Export off does not stop the machine that runs the world");
    // A value set in memory (as Configuration Manager does) still saves the file, as before.
    BC.ConfigExportHud.Value = false;
    C(File.ReadAllText(path).Contains("Hud = false"), "a Configuration Manager style change is still saved to the file");
    Pump(TimeSpan.FromSeconds(1.2));
    C(BC.ConfigExportHud.Value == false, "reading back that save changes nothing");
  }

  static void Pump(TimeSpan time)
  {
    var watch = System.Diagnostics.Stopwatch.StartNew();
    while (watch.Elapsed < time)
    {
      Thread.Sleep(50);
      LiveConfig.Update();
    }
  }

  static void SectionNumbers()
  {
    Section("config section numbering");
    // ConfigHelpers numbers groups in declaration order; the Export group must come tenth (09), after AltBiomes (08).
    var src = File.ReadAllText("/home/rohan/WubarrkCODING/BetterContinents10/BetterContinents.cs");
    var groups = System.Text.RegularExpressions.Regex.Matches(src, "\\.AddGroup\\(\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
    C(groups.Count == 10 && groups[8] == "BetterContinents.AltBiomes" && groups[9] == "BetterContinents.Export", $"Awake declares 10 groups, Export last: {string.Join(", ", groups.Select((g, i) => $"{i:00} {g}"))}");
    var path = Path.Combine(Path.GetTempPath(), "bc-sections-" + Environment.ProcessId + ".cfg");
    try
    {
      var cfg = new ConfigFile(path, true);
      var b = cfg.Declare();
      for (int i = 0; i < 9; i++)
        b.AddGroup("G" + i, g => { });
      ConfigEntry<bool> e = null;
      b.AddGroup("BetterContinents.Export", g => g.AddValue("Hud").Default(false).Bind(out e));
      C(e.Definition.Section == "09 BetterContinents.Export", $"the tenth group is section '{e.Definition.Section}'");
    }
    finally
    {
      try { File.Delete(path); } catch { }
    }
  }

  static void TransferRateTests()
  {
    Section("Settings Transfer Rate (TransferRate.BytesPerSecond mapping)");
    C(TransferRate.BytesPerSecond(TransferRatePreset.Vanilla) == null, "Vanilla: null (SendSettings must not touch the connection)");
    C(TransferRate.BytesPerSecond(TransferRatePreset.KB256) == 256 * 1024, $"KB256: 262144 bytes/s (got {TransferRate.BytesPerSecond(TransferRatePreset.KB256)})");
    C(TransferRate.BytesPerSecond(TransferRatePreset.KB384) == 384 * 1024, $"KB384: 393216 bytes/s (got {TransferRate.BytesPerSecond(TransferRatePreset.KB384)})");
    C(TransferRate.BytesPerSecond(TransferRatePreset.KB512) == 512 * 1024, $"KB512: 524288 bytes/s (got {TransferRate.BytesPerSecond(TransferRatePreset.KB512)})");
    C(TransferRate.BytesPerSecond(TransferRatePreset.KB768) == 768 * 1024, $"KB768: 786432 bytes/s (got {TransferRate.BytesPerSecond(TransferRatePreset.KB768)})");
    C(TransferRate.BytesPerSecond(TransferRatePreset.MB1) == 1024 * 1024, $"MB1: 1048576 bytes/s (got {TransferRate.BytesPerSecond(TransferRatePreset.MB1)})");
    C(TransferRate.BytesPerSecond(TransferRatePreset.MB1_5) == 1536 * 1024, $"MB1_5: 1572864 bytes/s (got {TransferRate.BytesPerSecond(TransferRatePreset.MB1_5)})");
    C(TransferRate.BytesPerSecond(TransferRatePreset.MB3) == 3072 * 1024, $"MB3: 3145728 bytes/s (got {TransferRate.BytesPerSecond(TransferRatePreset.MB3)})");
    C(TransferRate.BytesPerSecond(TransferRatePreset.Unlimited) == 100 * 1024 * 1024, $"Unlimited: a fixed 100 MB/s (the user's 2026-09-25 choice), not Steam's undocumented literal 0 (got {TransferRate.BytesPerSecond(TransferRatePreset.Unlimited)})");
    C(TransferRate.Describe(TransferRatePreset.MB1_5) == "1.5 MB/s" && TransferRate.Describe(TransferRatePreset.Unlimited) == "unlimited (100 MB/s)", "Describe names the new steps the way the log should read them");
    C(Enum.GetValues(typeof(TransferRatePreset)).Length == 9, $"nine presets: Vanilla, KB256, KB384, KB512, KB768, MB1, MB1_5, MB3, Unlimited (got {Enum.GetValues(typeof(TransferRatePreset)).Length})");
    C(TransferRate.BytesPerSecond(TransferRatePreset.Unlimited) != 0, "Unlimited must never be the literal 0 - GameNetworkingSockets does not document 0 as \"no limit\" for SendRateMax, so 0 would pin the connection at zero bytes/second instead");
    C(TransferRate.Default == TransferRatePreset.KB512, $"TransferRate.Default is KB512 (got {TransferRate.Default})");
    var unknownPreset = (TransferRatePreset)999;
    C(TransferRate.BytesPerSecond(unknownPreset) == TransferRate.BytesPerSecond(TransferRate.Default), "an unrecognised preset value's bytes/second falls back to Default's, instead of throwing");
    C(TransferRate.Describe(unknownPreset) == TransferRate.Describe(TransferRate.Default), "...and so does its log text (Describe)");

    Section("Settings Transfer Rate (cfg round trip, through the real BepInEx ConfigFile)");
    const string S = "07 BetterContinents.Misc";
    var path = Path.Combine(Path.GetTempPath(), "bc-transferrate-" + Environment.ProcessId + ".cfg");
    try
    {
      // A fresh file, nothing set yet: binding gives the compiled-in default.
      var cfg = new ConfigFile(path, true);
      var entry = cfg.Bind(S, "Settings Transfer Rate", TransferRate.Default, new ConfigDescription("transfer rate"));
      C(entry.Value == TransferRatePreset.KB512, $"a fresh bind with nothing in the file defaults to KB512 (got {entry.Value})");

      // Round trip: write "Settings Transfer Rate = KB384" on disk, then Reload through the real ConfigFile.
      File.WriteAllText(path, File.ReadAllText(path).Replace("Settings Transfer Rate = KB512", "Settings Transfer Rate = KB384"));
      cfg.Reload();
      C(entry.Value == TransferRatePreset.KB384, $"cfg.Reload() picks up 'Settings Transfer Rate = KB384' (got {entry.Value})");
      C(TomlTypeConverter.ConvertToString(entry.Value, typeof(TransferRatePreset)) == "KB384", "the enum serialises back to its name, not a number");

      // An unrecognised value in the file: BepInEx's enum TomlTypeConverter throws inside SetSerializedValue, which
      // catches it, logs a warning (through BepInEx's own Logger, a separate pipe from the UnityEngine.Debug hook
      // LogHandler watches, so not asserted here), and leaves the entry's current value alone -- Reload itself
      // never throws.
      File.WriteAllText(path, File.ReadAllText(path).Replace("Settings Transfer Rate = KB384", "Settings Transfer Rate = Bogus"));
      Exception reloadThrew = null;
      try { cfg.Reload(); } catch (Exception e) { reloadThrew = e; }
      C(reloadThrew == null, $"an unrecognised value in the file does not throw out of Reload() (got {reloadThrew})");
      C(entry.Value == TransferRatePreset.KB384, $"...and the entry keeps its last known value, KB384, instead of changing (got {entry.Value})");

      // The default specifically: a brand-new bind whose file already holds an unrecognised value (nothing valid
      // was ever read for this key) falls back to Default, KB512 -- not to whatever the bad text half-parses to.
      var path2 = path + "-2";
      File.WriteAllText(path2, $"[{S}]\nSettings Transfer Rate = TotallyUnknown\n");
      var cfg2 = new ConfigFile(path2, true);
      Exception bindThrew = null;
      ConfigEntry<TransferRatePreset> entry2 = null;
      try { entry2 = cfg2.Bind(S, "Settings Transfer Rate", TransferRate.Default, new ConfigDescription("transfer rate")); }
      catch (Exception e) { bindThrew = e; }
      C(bindThrew == null && entry2 != null, $"binding against a file that already holds an unrecognised value does not throw (got {bindThrew})");
      C(entry2?.Value == TransferRate.Default, $"...and falls back to Default, KB512, without throwing (got {entry2?.Value})");
      try { File.Delete(path2); } catch { }
    }
    finally
    {
      try { File.Delete(path); } catch { }
    }
  }
}
