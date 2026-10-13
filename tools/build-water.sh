#!/usr/bin/env bash
# Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
# build-water.sh - builds assets/BCWater/bcwater (BC's water shaders) in the Unity project:
#   0. creates the project if it is not there, refuses to run while the Valheim client is up (Unity needs the GPU), and checks the HLSL's wave constants
#      against the game's code (offline);
#   1. copies unity/Sea/* (shaders, includes), unity/Editor/SeaWavesCheck.compute and unity/Editor/*.cs into the project;
#   2. runs the editor in batch mode WITH a graphics device (`unity run` adds -batchmode -quit; never -nographics, the bake lessons): it compiles
#      the shaders, checks Custom/Water's 33 properties, runs the GPU wave check (SeaWavesCheck) and builds the bundle for StandaloneWindows64 with
#      Direct3D 11, Vulkan and OpenGL Core programs (BuildBcWater.Build);
#   3. checks the bundle's contents with tools/water-tests/facts/bundle_check.py (both shaders; Direct3D 11, OpenGL Core and Vulkan programs in
#      every pass; the properties), and only then replaces assets/BCWater/bcwater. A 0-byte bundle or any failure leaves the old bundle in place.
# The Unity project is $BC_UNITY_PROJECT (default ~/WubarrkCODING/BetterContinents10/.work/BetterContinents-Unity; never VALtima's). Heavy: run it
# through the machine-wide lock, with the game closed:
#   HEAVY_MARGIN_GB=4 ~/valheim-testbed/heavy.sh 8G water-bundle tools/build-water.sh
# BC_SEA_COPY_CS=1 also copies SeaWaves.cs from the repository root into the project (Assets/Editor/Reference/) so that SeaWavesCheck calls
# SeaWaves.One by name; leave it off until that file compiles on its own in Unity (it must not use BepInEx, Harmony or the game's types).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="${BC_UNITY_PROJECT:-$HOME/WubarrkCODING/BetterContinents10/.work/BetterContinents-Unity}"
PY="${BC_PYTHON:-$HOME/upy/bin/python}"
FACTS="$ROOT/tools/water-tests/facts"

if [ ! -f "$PROJ/ProjectSettings/ProjectVersion.txt" ]; then
  # a hand-made project like VALtima's: the built-in modules only (no packages from a registry, no collections package), editor 6000.0.61f1.
  # The editor writes the rest of ProjectSettings and Library on its first run. The project stays out of git (it lives in .work/).
  case "$PROJ" in *ValtimaOnline*|*valtima*) echo "refusing to create a project in VALtima's folders: $PROJ"; exit 2 ;; esac
  mkdir -p "$PROJ/Assets" "$PROJ/Packages" "$PROJ/ProjectSettings"
  cat > "$PROJ/Packages/manifest.json" <<'JSON'
{
  "dependencies": {
    "com.unity.modules.accessibility": "1.0.0",
    "com.unity.modules.ai": "1.0.0",
    "com.unity.modules.androidjni": "1.0.0",
    "com.unity.modules.animation": "1.0.0",
    "com.unity.modules.assetbundle": "1.0.0",
    "com.unity.modules.audio": "1.0.0",
    "com.unity.modules.cloth": "1.0.0",
    "com.unity.modules.director": "1.0.0",
    "com.unity.modules.imageconversion": "1.0.0",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.jsonserialize": "1.0.0",
    "com.unity.modules.particlesystem": "1.0.0",
    "com.unity.modules.physics": "1.0.0",
    "com.unity.modules.physics2d": "1.0.0",
    "com.unity.modules.screencapture": "1.0.0",
    "com.unity.modules.terrain": "1.0.0",
    "com.unity.modules.terrainphysics": "1.0.0",
    "com.unity.modules.tilemap": "1.0.0",
    "com.unity.modules.ui": "1.0.0",
    "com.unity.modules.uielements": "1.0.0",
    "com.unity.modules.umbra": "1.0.0",
    "com.unity.modules.unityanalytics": "1.0.0",
    "com.unity.modules.unitywebrequest": "1.0.0",
    "com.unity.modules.unitywebrequestassetbundle": "1.0.0",
    "com.unity.modules.unitywebrequestaudio": "1.0.0",
    "com.unity.modules.unitywebrequesttexture": "1.0.0",
    "com.unity.modules.unitywebrequestwww": "1.0.0",
    "com.unity.modules.vehicles": "1.0.0",
    "com.unity.modules.video": "1.0.0",
    "com.unity.modules.vr": "1.0.0",
    "com.unity.modules.wind": "1.0.0",
    "com.unity.modules.xr": "1.0.0"
  }
}
JSON
  printf 'm_EditorVersion: 6000.0.61f1\nm_EditorVersionWithRevision: 6000.0.61f1 (74a0adb02c31)\n' > "$PROJ/ProjectSettings/ProjectVersion.txt"
  echo "created the Unity project $PROJ"
fi
[ -d "$PROJ/Assets" ] || { echo "no Assets folder in $PROJ"; exit 2; }
if pgrep -x valheim.x86_64 > /dev/null || pgrep -x valheim.exe > /dev/null; then
  echo "the Valheim client is running (pid $(pgrep -x valheim.x86_64 | head -n 1)): Unity needs the GPU, so not now"; exit 3
fi
if pgrep -if -- "-projectpath[= ].*$(basename "$PROJ")" > /dev/null; then
  echo "the Unity editor has $PROJ open: close it first"; exit 2
fi

# offline: the HLSL's wave constants against the game's code
"$PY" "$FACTS/check_wave_constants.py"

mkdir -p "$PROJ/Assets/BCWater" "$PROJ/Assets/Editor"
for f in "$ROOT"/unity/Sea/*.shader "$ROOT"/unity/Sea/*.hlsl "$ROOT"/unity/Editor/SeaWavesCheck.compute; do
  cp "$f" "$PROJ/Assets/BCWater/$(basename "$f")"
done
for f in "$ROOT"/unity/Editor/*.cs; do
  cp "$f" "$PROJ/Assets/Editor/$(basename "$f")"
done
# files that left the repository leave the project too
for f in "$PROJ"/Assets/BCWater/*.shader "$PROJ"/Assets/BCWater/*.hlsl "$PROJ"/Assets/BCWater/*.compute; do
  [ -e "$f" ] || continue
  case "$(basename "$f")" in
    *.compute) [ -e "$ROOT/unity/Editor/$(basename "$f")" ] || { rm -f "$f" "$f.meta"; } ;;
    *) [ -e "$ROOT/unity/Sea/$(basename "$f")" ] || { rm -f "$f" "$f.meta"; } ;;
  esac
done
for f in "$PROJ"/Assets/Editor/*.cs; do
  [ -e "$f" ] || continue
  [ -e "$ROOT/unity/Editor/$(basename "$f")" ] || rm -f "$f" "$f.meta"
done
if [ "${BC_SEA_COPY_CS:-0}" = "1" ] && [ -f "$ROOT/SeaWaves.cs" ]; then
  mkdir -p "$PROJ/Assets/Editor/Reference"
  cp "$ROOT/SeaWaves.cs" "$PROJ/Assets/Editor/Reference/SeaWaves.cs"
else
  rm -rf "$PROJ/Assets/Editor/Reference" "$PROJ/Assets/Editor/Reference.meta"
fi

LOG="$PROJ/Logs/build-water.log"
mkdir -p "$PROJ/Logs" "$PROJ/Build/BCWater"
rm -f "$PROJ/Build/BCWater/bcwater"
STATUS=0
unity run "$PROJ" --timeout 3000 -- -executeMethod BuildBcWater.Build -logFile "$LOG" || STATUS=$?
echo "---- [BCWater] lines of $LOG"
grep -a '\[BCWater\]' "$LOG" | grep -av '^UnityEngine' || true
BAD="$(grep -a -n -i 'shader error\|error CS[0-9]\|compilation failed\|Compute shader.*error\|BCWater.*failed' "$LOG" | head -30 || true)"
if [ "$STATUS" -ne 0 ] || [ -n "$BAD" ]; then
  echo "Unity failed (exit $STATUS); the log is $LOG:"
  [ -n "$BAD" ] && echo "$BAD"
  grep -a -n -i 'error\|exception' "$LOG" | tail -30 || true
  exit 1
fi

BUNDLE="$PROJ/Build/BCWater/bcwater"
[ -s "$BUNDLE" ] || { echo "the bundle $BUNDLE is missing or empty"; exit 1; }
"$PY" "$FACTS/bundle_check.py" "$BUNDLE"

mkdir -p "$ROOT/assets/BCWater"
cp "$BUNDLE" "$ROOT/assets/BCWater/bcwater"
echo "bundle: $ROOT/assets/BCWater/bcwater ($(stat -c%s "$ROOT/assets/BCWater/bcwater") bytes)"
