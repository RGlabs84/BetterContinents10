#!/usr/bin/env bash
# Added by Wubarrk on 2026-10-04 for the unifying refactor and the vegetation twin guard (0.10.0), and modified on 2026-10-06 for the Forest Scale default (0.10.2) and for 16k worlds (0.10.3).
#
# Builds the plugin (Release) and runs every offline suite against it, one after another, memory-capped and niced:
#   golden-tests      the reference recordings (tools/golden-tests/golden): any difference fails
#   altbiome-harness  alt-biome control, planting, export round trip
#   export-tests      world export math, passes and the end-to-end coroutine
#   import-tests      world import, presets, export.cfg, the Directory way
#   livecfg-tests     live config, transfer rate, world cache, minimap
#   ewd-tests         Expand World Data biomes (vanilla and EWD processes)
#   twin-tests        the vegetation twin guard, bc_twins' scan, the PlaceVegetation tracking on the game's IL
#   size-tests        the world's size: WorldGeometry, SetSize, the edge-of-world patches on the game's IL; the water depth patch
#   sector-tests      the sectors of a world past the game's 16 km (WorldSectors): the sector, index and chunk map for all 2048 x 2048 zones, the
#                     transpilers on the game's IL (client and dedicated server), the game's save planning run patched, and when the map is on
#   tile-tests        the maps' tiles: the codec, every map kind sampling as before, saves in tiles, the cache
#   zone-tests        zone regeneration: what protects a zone (pieces, tombstones, players, worked ground), locations kept whole,
#                     the plan and the work, saves and stops in the middle, requests and errors (Unity's coroutines stood in for),
#                     a world that closes in every phase, time-slicing and bandwidth, the ground at a border, the Debug Reset
#                     Command and Forest Scale migrations, "bc regen", the game code it relies on, a whole made-up world
#   high-tests        terrain up to 16 km: when a world is a high world, heights at Heightmap Amount 81, what is inside a dungeon, the
#                     patches of the game's height rules on the client's and the dedicated server's IL, an inventory of the game's numbers
# Usage: tools/run-tests.sh [suite ...]   (default: all). Exit code 1 when the build or any suite fails.
set -u
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
# ulimit -v breaks CoreCLR's heap reservation; a hard GC heap limit caps it instead.
export DOTNET_GCHeapHardLimit="${DOTNET_GCHeapHardLimit:-0xC0000000}"
logs="$(mktemp -d "${TMPDIR:-/tmp}/bc-tests-XXXXXX")"
suites=("$@")
[ ${#suites[@]} -eq 0 ] && suites=(golden-tests altbiome-harness export-tests import-tests livecfg-tests ewd-tests twin-tests size-tests sector-tests tile-tests zone-tests high-tests)

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
