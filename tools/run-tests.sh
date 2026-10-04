#!/usr/bin/env bash
# Added by Wubarrk on 2026-10-04 for the unifying refactor and the vegetation twin guard (0.10.0).
#
# Builds the plugin (Release) and runs every offline suite against it, one after another, memory-capped and niced:
#   golden-tests      the reference recordings (tools/golden-tests/golden): any difference fails
#   altbiome-harness  alt-biome control, planting, export round trip
#   export-tests      world export math, passes and the end-to-end coroutine
#   import-tests      world import, presets, export.cfg, the Directory way
#   livecfg-tests     live config, transfer rate, world cache, minimap
#   ewd-tests         Expand World Data biomes (vanilla and EWD processes)
#   twin-tests        the vegetation twin guard, bc_twins' scan, the PlaceVegetation tracking on the game's IL
#   size-tests        the world's size: WorldGeometry, SetSize, the edge-of-world patches on the game's IL
#   tile-tests        the maps' tiles: the codec, every map kind sampling as before, saves in tiles, the cache
# Usage: tools/run-tests.sh [suite ...]   (default: all). Exit code 1 when the build or any suite fails.
set -u
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
# ulimit -v breaks CoreCLR's heap reservation; a hard GC heap limit caps it instead.
export DOTNET_GCHeapHardLimit="${DOTNET_GCHeapHardLimit:-0xC0000000}"
logs="$(mktemp -d "${TMPDIR:-/tmp}/bc-tests-XXXXXX")"
suites=("$@")
[ ${#suites[@]} -eq 0 ] && suites=(golden-tests altbiome-harness export-tests import-tests livecfg-tests ewd-tests twin-tests size-tests tile-tests)

echo "== building the plugin"
if ! nice -n 10 dotnet build -c Release BetterContinents.csproj > "$logs/build.log" 2>&1; then
  grep -E "error|Error" "$logs/build.log" | sort -u | head -20
  echo "BUILD FAILED (log: $logs/build.log)"
  exit 1
fi
grep -E "Warning\(s\)|Error\(s\)" "$logs/build.log" | tail -2
md5sum obj/Release/net4.8/BetterContinents.dll dist/plugins/BetterContinents.dll 2>/dev/null

failed=()
for s in "${suites[@]}"; do
  echo "== $s"
  if (cd "tools/$s" && nice -n 10 dotnet run -c Release > "$logs/$s.log" 2>&1); then
    tail -n 2 "$logs/$s.log"
  else
    grep -E "FAIL|CRASH|error" "$logs/$s.log" | head -20
    tail -n 2 "$logs/$s.log"
    failed+=("$s")
  fi
done
echo "== logs in $logs"
if [ ${#failed[@]} -gt 0 ]; then
  echo "FAILED: ${failed[*]}"
  exit 1
fi
echo "ALL PASSED: ${suites[*]}"
