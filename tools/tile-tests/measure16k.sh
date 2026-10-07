#!/bin/bash
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
# measure16k.sh <results.tsv> <folder of maps> <kind> [<kind> ...]
# Each kind (height forest rough heat lava moss biome location spawn vegetation paint paintnoisy terrain altbiome), decoded and
# compact, in a process of its own (Program.Measure16k.cs): one TSV line each, appended to the results file and printed. Build
# first ("dotnet build -c Release" here and in ../..), make the maps with gen16k.py, and run it under heavy.sh 6G.
set -u
cd "$(dirname "$0")"
OUT="${1:?usage: measure16k.sh <results.tsv> <folder of maps> <kind>...}"; FOLDER="${2:?folder}"; shift 2
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" DOTNET_GCHeapHardLimit=0x140000000 DOTNET_gcConcurrent=0
for kind in "$@"; do
  for compact in 0 1; do
    dotnet bin/Release/net8.0/AltBiomeHarness.dll measure16k "$FOLDER" "$kind" "$compact" 2>&1 | grep -v "^    log" | tee -a "$OUT"
  done
done
