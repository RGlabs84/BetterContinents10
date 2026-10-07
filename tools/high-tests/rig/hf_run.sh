#!/bin/bash
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
# The high-fix rig (2026-10-07, stream feat/0.10.3-highfix; port 2503): one server run, run as one heavy.sh job (cap 6G).
#   hf_run.sh <label> <world> <seed> <dll> <amount> <mode> <heightmap file | none> [VAR=value ...]
# Builds the probe (a few seconds), puts <dll> in the profile, writes the config it needs (World Size 8000 + Edge Size 500, Heightmap Amount, Override All on,
# High Terrain <mode>, the heightmap or none) and runs ./run.sh <label> with the VAR=value pairs in the probe's environment (BCPROBE_HIGH, BCPROBE_DUNGEONS, ...).
# A world that does not exist is made from that config with seed <seed>; one that does is loaded as it is (the config's amount and mode then mean nothing to it).
set -u
cd "$(dirname "$0")"
LABEL="${1:?label}"; WORLD="${2:?world}"; SEED="${3:?seed}"; DLL="${4:?dll}"; AMOUNT="${5:?amount}"; MODE="${6:?mode}"; MAP="${7:?map}"; shift 7
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet DOTNET_GCHeapHardLimit=0x80000000
( cd probe/BCServerProbe && dotnet build -c Release -v q -p:L=$HOME/WubarrkCODING/libs-Tools/ 2>&1 | tail -3 ) || exit 1
cp probe/BCServerProbe/bin/Release/net4.8/BCServerProbe.dll profile/BepInEx/plugins/BCServerProbe/BCServerProbe.dll
cp "$DLL" profile/BepInEx/plugins/BetterContinents/BetterContinents.dll
md5sum profile/BepInEx/plugins/*/*.dll
CFG=profile/BepInEx/config/BetterContinents.cfg
set_cfg() { if grep -q "^$1 = " "$CFG"; then sed -i "s#^$1 = .*#$1 = $2#" "$CFG"; else sed -i "/^\[02 BetterContinents.Heightmap\]/a $1 = $2" "$CFG"; fi; }
set_cfg "World Size" 8000; set_cfg "Edge Size" 500; set_cfg "Rivers" true; set_cfg "Skip Default Locations" false; set_cfg "Mode" Random
[ "$MAP" = none ] && set_cfg "Heightmap File" "" || set_cfg "Heightmap File" "$MAP"
set_cfg "Heightmap Amount" "$AMOUNT"; set_cfg "Heightmap Override All" true; set_cfg "Heightmap Alpha" false
set_cfg "High Terrain" "$MODE"
grep -E "^(World Size|Edge Size|Heightmap File|Heightmap Amount|Heightmap Override All|High Terrain|SelectedPreset) =" "$CFG"
export BCPROBE_SEED="$SEED" BCPROBE_GRID=0 TIMEOUT="${TIMEOUT:-420}"
for pair in "$@"; do export "$pair"; done
WORLD="$WORLD" ./run.sh "$LABEL"
echo "== $LABEL done: $(tail -n 3 out/$LABEL/run.txt | head -1)"
