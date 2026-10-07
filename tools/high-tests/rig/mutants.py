#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# The mutation check of the high-terrain tests: each mutant changes ONE line of the production code (a wiring line, a guard, a rule, a number) in a scratch copy of
# the repository, builds the plugin, runs the suites that should notice, and says whether one failed. A mutant that every suite lets through is a line no test
# pins. The repository itself is never touched.
#
#   mutants.py [--keep] [--no-baseline] <scratch folder> [mutant ...]       (every mutant when none is named; --list names them)
#
# Run it as heavy jobs (about 25 minutes in all: split it into jobs of ten minutes or so by naming the mutants, the first without --keep and the later ones with it,
# so that the copy is made once and its build is reused): ~/valheim-testbed/heavy.sh 4G highfix-mutants-1 python3 tools/high-tests/rig/mutants.py <folder> <names>
# The scratch folder gets bc/ (the copy) and libs-Tools (a link to the game's libraries, next to the copy as in the repository's own layout), and results.txt.
#   --keep         use the copy a job before made (it must still equal the repository's sources); the report is added to
#   --no-baseline  do not run the suites on the unmutated copy first (the first job does)
import os, re, shutil, subprocess, sys, time

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
LIBS = os.environ.get("BC_LIBS_TOOLS", os.path.join(os.path.dirname(ROOT), "libs-Tools"))

# (name, what the line does, [(file, old, new), ...], suites that should fail)
MUTANTS = [
  ("wiring-patcher-call-removed", "DynamicPatch's call of PatchHighTerrain",
   [("Patcher.cs", "    PatchHighTerrain();\n    UpdateGeometry();", "    UpdateGeometry();")], ["high-tests"]),
  ("wiring-patcher-call-last", "PatchHighTerrain as DynamicPatch's last step, where an earlier step that throws skips it",
   [("Patcher.cs", "    PatchHighTerrain();\n    UpdateGeometry();", "    UpdateGeometry();"),
    ("Patcher.cs", "    WorldSectors.Update(HarmonyInstance, Settings, ExpandWorldSizeGeometry);", "    WorldSectors.Update(HarmonyInstance, Settings, ExpandWorldSizeGeometry);\n    PatchHighTerrain();")], ["high-tests"]),
  ("wiring-update-removed", "PatchHighTerrain that does not tell HighTerrain the world's settings first",
   [("BetterContinents.HighTerrainPatch.cs", "    HighTerrain.Update(Settings);\n    foreach (var toggle in HighTerrainToggles)", "    foreach (var toggle in HighTerrainToggles)")], ["high-tests"]),
  ("wiring-usez-store", "DynamicPatch's store of the Deep North weather's x-and-z rule",
   [("Patcher.cs", "    DeepNorthWeather.UseZ = deepNorthUsesZ;\n", "")], ["high-tests"]),
  ("wiring-export-limit", "a world export's heightmap amount limit (the schema's 81)",
   [("WorldExport.cs", "HeightmapAmount <= MaxHeightmapAmount))", "HeightmapAmount <= 5f))")], ["export-tests"]),
  ("wiring-altbiome-limit", "the alt biomes' 10000 m limit on a sector's mean height, lifted with the patches",
   [("BetterContinents.AltBiomeControl.cs", "?? HighTerrain.MaxAverageHeight(orig.MaxH);", "?? orig.MaxH;")], ["altbiome-harness"]),
  ("wiring-zoneregen-interior", "a player's 'inside a dungeon' in zone regeneration, by the patched rule",
   [("ZoneRegen.cs", "if (HighTerrain.Interior(zdo.GetPosition()))", "if (zdo.GetPosition().y > 3000f)")], ["zone-tests"]),
  ("ray-guard-partial", "the ground ray transpiler's all-or-nothing guard (both numbers found, not either)",
   [("BetterContinents.HighTerrainPatch.cs", "return starts == 1 && lengths == 1 ? code : NotFound(source, original, \"the start (6000 m)",
     "return starts >= 1 || lengths >= 1 ? code : NotFound(source, original, \"the start (6000 m)")], ["high-tests"]),
  ("upray-guard-partial", "the same guard of the ground data and grass ray transpilers",
   [("BetterContinents.HighTerrainPatch.cs", "return starts == 1 && lengths == 1 ? code : NotFound(source, original, $\"the origin",
     "return starts >= 1 || lengths >= 1 ? code : NotFound(source, original, $\"the origin")], ["high-tests"]),
  ("blocker-guard-two", "the blocker transpiler's count (exactly one 2000 m found)",
   [("BetterContinents.HighTerrainPatch.cs", "return changed == 1 ? code : NotFound(source, original, \"the 2000 m a blocker ray",
     "return changed >= 1 ? code : NotFound(source, original, \"the 2000 m a blocker ray")], ["high-tests"]),
  ("blocker-shape-guard", "the blocker transpiler's check of the method's shape (an instance method with one Vector3)",
   [("BetterContinents.HighTerrainPatch.cs", "bool shape = original == null || (!original.IsStatic && original.GetParameters().Length == 1 && original.GetParameters()[0].ParameterType.Name == \"Vector3\");",
     "bool shape = true;")], ["high-tests"]),
  ("hook-swapped", "the AI's FindGround hooked to the transpiler of another method's rays",
   [("BetterContinents.HighTerrainPatch.cs", "OnGame(typeof(Pathfinding), \"FindGround\", null, nameof(HighTerrainPatches.RaiseGroundRay), HookKind.Transpiler)",
     "OnGame(typeof(Pathfinding), \"FindGround\", null, nameof(HighTerrainPatches.RaiseGroundDataRay), HookKind.Transpiler)")], ["high-tests"]),
  ("hook-missing-teleport", "a caller of Character.InInterior left out of the hooks",
   [("BetterContinents.HighTerrainPatch.cs", ",\n      OnGame(typeof(Teleport), nameof(Teleport.Interact), null, nameof(HighTerrainPatches.RedirectInterior), HookKind.Transpiler))", ")")], ["high-tests"]),
  ("wanted-off-is-auto", "High Terrain Off that still patches a high world",
   [("HighTerrain.cs", "HighTerrainMode.Off => false,", "HighTerrainMode.Off => HighByAmount(s),")], ["high-tests"]),
  ("wanted-on-needs-heightmap", "High Terrain On that skips a world without a heightmap",
   [("HighTerrain.cs", "HighTerrainMode.On => true,", "HighTerrainMode.On => s.HasHeightMap,")], ["high-tests"]),
  ("bound-no-heightmap", "the top of a world without a heightmap, taken as 0 m",
   [("HighTerrain.cs", "if (!s.HasHeightMap || s.BlendsHeightmapAlpha)", "if (s.BlendsHeightmapAlpha)")], ["high-tests"]),
  ("mode-change-no-repatch", "a live change of the mode that does not switch the patches",
   [("HighTerrain.cs", "    BetterContinents.DynamicPatch();\n    if (MaxAverageHeight(NoLimitAverageHeight) != limit)", "    if (MaxAverageHeight(NoLimitAverageHeight) != limit)")], ["high-tests"]),
  ("mode-change-no-regen", "a live change of the mode that leaves the zones as they were",
   [("HighTerrain.cs", "      GameUtils.RegenerateZones();", "      { }")], ["high-tests"]),
  ("serialize-writes-auto", "Auto written to every world's settings",
   [("Serialize.cs", "if (HighTerrainMode != HighTerrainMode.Auto)\n      {\n        pkg.Write((int)DataKey.HighTerrain);", "if (true)\n      {\n        pkg.Write((int)DataKey.HighTerrain);")], ["golden-tests", "high-tests"]),
  ("serialize-read-ignored", "the saved mode read and thrown away",
   [("Serialize.cs", "HighTerrainMode = HighTerrainModes.Of(mode);", "_ = HighTerrainModes.Of(mode);")], ["high-tests"]),
  ("serialize-unknown-mode", "a mode number no version knows, kept as it is",
   [("Serialize.cs", "HighTerrainMode = HighTerrainModes.Of(mode);", "HighTerrainMode = (HighTerrainMode)mode;")], ["high-tests"]),
  ("serialize-key-68", "the key 68, which is the wide sectors'",
   [("Serialize.cs", "HighTerrain = 69,", "HighTerrain = 68,")], ["high-tests", "tile-tests"]),
  ("export-cfg-no-mode", "the mode left out of an export's export.cfg",
   [("WorldExport.cs", "if (Settings.EnabledForThisWorld && Settings.HighTerrainMode != HighTerrainMode.Auto)", "if (false)")], ["export-tests", "import-tests", "high-tests"]),
  ("dump-no-mode", "bc info that does not say High Terrain On",
   [("BetterContinents.BetterContinentsSettings.cs", "if (HighTerrainMode == HighTerrainMode.On)", "if (false)")], ["high-tests"]),
  ("schema-order", "High Terrain moved from the end of its section",
   [("SettingsSchema.cs", "new(\"BetterContinents.Heightmap\", HeightmapFile, HeightmapAmount, HeightmapBlend, HeightmapAdd, HeightmapMask, HeightmapOverrideAll, HeightmapAlpha, RoughmapFile, RoughmapBlend, HighTerrain)",
     "new(\"BetterContinents.Heightmap\", HeightmapFile, HeightmapAmount, HeightmapBlend, HeightmapAdd, HeightmapMask, HeightmapOverrideAll, HeightmapAlpha, HighTerrain, RoughmapFile, RoughmapBlend)")], ["golden-tests", "high-tests"]),
  ("console-name", "the console name of the setting",
   [("SettingsSchema.cs", "ConsoleGroup = \"h\", ConsoleName = \"ht\"", "ConsoleGroup = \"h\", ConsoleName = \"hx\"")], ["high-tests"]),
  ("console-parse-old", "the console reading a number or a list of names as a mode (Enum.Parse alone)",
   [("DebugUtils.Command.cs", "            var names = Enum.GetNames(type);\n            var name = names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));\n            if (name == null)\n                throw new ArgumentException($\"{text} is not one of {string.Join(\", \", names)}\");\n            return Enum.Parse(type, name);",
     "            return Enum.Parse(type, text, true);")], ["high-tests"]),
  ("description-no-cost", "the setting's description without what Off costs",
   [("SettingsSchema.cs", "Off costs a world with high ground: no grass above 500 m, no plants, creatures or locations above about 1,000 m, no terrain, weather or building above 3,000 m (the game takes it for the inside of a dungeon), and no ground found above 6,000 m. ", "")], ["golden-tests", "high-tests"]),
  ("deepnorth-log-every-time", "the Deep North transpiler that says so at every re-patch",
   [("BetterContinents.EnvManPatch.cs", "if (n > 0 && n != DeepNorthWeather.UpdateEnvironmentCalls)", "if (n > 0)")], ["high-tests"]),
  ("update-note-wording", "the log line when a high world is left, with the old reason",
   [("HighTerrain.cs", "High terrain: off (no high world is loaded): the game's own height rules apply", "High terrain: this world's heightmap is not read above Heightmap Amount 5: the game's own height rules are left alone")], ["high-tests"]),
]


def run(cmd, cwd, env=None, timeout=1500):
  p = subprocess.run(cmd, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=timeout)
  return p.returncode, p.stdout


def main():
  args = sys.argv[1:]
  keep = "--keep" in args
  baseline = "--no-baseline" not in args
  args = [a for a in args if a not in ("--keep", "--no-baseline")]
  if args and args[0] == "--list":
    for name, what, _, suites in MUTANTS:
      print(f"{name:32} {what}  [{', '.join(suites)}]")
    return 0
  if not args:
    print(__doc__)
    return 2
  scratch = os.path.abspath(args[0])
  if scratch == ROOT or ROOT.startswith(scratch + os.sep) or os.path.exists(os.path.join(scratch, ".git")):
    print("refusing: the scratch folder is, holds or is inside a repository")
    return 2
  wanted = args[1:] or [m[0] for m in MUTANTS]
  unknown = [w for w in wanted if w not in {m[0] for m in MUTANTS}]
  if unknown:
    print("unknown mutants:", unknown)
    return 2
  work = os.path.join(scratch, "bc")
  skip = (".git", "bin", "obj", "dist", "__pycache__")
  if keep:
    # The copy of a job before: refuse it when the repository's sources have moved on since.
    same = subprocess.run(["diff", "-rq"] + [f"--exclude={x}" for x in skip] + [ROOT, work], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if same.returncode != 0:
      print("the copy is not the repository's sources any more:\n" + same.stdout[-2000:])
      return 2
  else:
    shutil.rmtree(scratch, ignore_errors=True)
    os.makedirs(scratch)
    os.symlink(LIBS, os.path.join(scratch, "libs-Tools"))
    shutil.copytree(ROOT, work, ignore=shutil.ignore_patterns(*skip))
  # The test projects are built where they stand: bin and obj of tools/* are not copied, and tools/*/bin is made again by each run.
  env = dict(os.environ, TMPDIR=os.path.join(scratch, "tmp"), DOTNET_CLI_TELEMETRY_OPTOUT="1")
  os.makedirs(env["TMPDIR"], exist_ok=True)
  results = os.path.join(scratch, "results.txt")
  report = []
  # The copy itself first: every suite that is named below passes on it, or no mutant means anything.
  if baseline:
    suites_used = sorted({s for m in MUTANTS if m[0] in wanted for s in m[3]})
    t0 = time.time()
    code, out = run(["tools/run-tests.sh"] + suites_used, work, env)
    base = f"unmutated copy, {' '.join(suites_used)}: {'ALL PASSED' if code == 0 else 'FAILED'} ({time.time() - t0:.0f} s)"
    print(base, flush=True)
    report.append(base)
    if code != 0:
      print(out[-3000:])
      return 1
  killed = 0
  for name, what, edits, suites in MUTANTS:
    if name not in wanted:
      continue
    originals = {}
    secs = 0.0
    try:
      for f, old, new in edits:
        path = os.path.join(work, f)
        if path not in originals:
          originals[path] = open(path, encoding="utf-8", newline="").read()
        text = open(path, encoding="utf-8", newline="").read()
        if text.count(old) != 1:
          raise RuntimeError(f"{f} has {text.count(old)} copies of the text to change")
        open(path, "w", encoding="utf-8", newline="").write(text.replace(old, new, 1))
      t0 = time.time()
      code, out = run(["tools/run-tests.sh"] + suites, work, env)
      secs = time.time() - t0
      if "BUILD FAILED" in out or re.search(r"error CS\d+", out):
        verdict = "INVALID (the mutant does not build, or a test project that refers to it does not)"
      elif code == 0:
        verdict = "SURVIVED: every suite passed"
      else:
        failed = re.search(r"^FAILED: (.*)$", out, re.M)
        # The failing checks first (a suite prints them as FAIL), else whatever else says what went wrong.
        fails = [l.strip() for l in out.splitlines() if re.match(r"\s*FAIL\b", l) and not l.startswith("FAILED:")]
        others = [l.strip() for l in out.splitlines() if re.search(r"CRASH|error|Exception", l) and not re.match(r"\s*PASS\b", l) and "BUILD" not in l]
        lines = (fails or others)[:3]
        verdict = "killed by " + (failed.group(1) if failed else "?") + ": " + " | ".join(l[:230] for l in lines)
        killed += 1
    except (RuntimeError, subprocess.TimeoutExpired) as e:
      verdict = f"INVALID ({e})"
    finally:
      for path, text in originals.items():
        open(path, "w", encoding="utf-8", newline="").write(text)
    row = f"{name:30} {verdict}   ({secs:.0f} s)  # {what}"
    print(row, flush=True)
    report.append(row)
  report.append(f"{killed} of {len(wanted)} mutants killed")
  print(report[-1])
  with open(results, "a") as f:
    f.write("\n".join(report) + "\n")
  return 0 if killed == len(wanted) else 1


if __name__ == "__main__":
  sys.exit(main())
