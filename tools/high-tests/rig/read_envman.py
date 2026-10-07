#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# Reads EnvMan's environments (fog density by time of day, the sky and cloud numbers) out of the game's own asset bundle d59cfac, so
# that "how far can a player see" is a number from the game and not a guess. The fog is plain exponential, as the game's shaders have it
# (libs-Tools/1.0/ASSET-DATA/shaders/veg_frag.glsl, line 708: exp2(-(distance * unity_FogParams.y)), with Unity's unity_FogParams.y = density / ln 2): at
# density d a surface keeps a fraction exp(-d * distance) of its colour (the game sets RenderSettings.fogDensity from these, EnvMan.cs:871-875).
# Not read: the scene's own fog mode (RenderSettings.fogMode); the shader's term is what the distances below stand on.
# Usage (run under ~/valheim-testbed/heavy.sh, about 1 GB): read_envman.py [bundle]    (default: the client's d59cfac)
import os
import math, sys
sys.path.insert(0, os.path.expanduser("~/WubarrkCODING/libs-Tools/UNITY-ASSET-TOOLS"))
import UnityPy

path = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/share/Steam/steamapps/common/Valheim/valheim_Data/StreamingAssets/SoftRef/Bundles/d59cfac")
env = UnityPy.load(path)
for o in env.objects:
    if o.type.name != "MonoBehaviour":
        continue
    try:
        tt = o.read_typetree()
    except Exception:
        continue
    if "m_environments" not in tt or "m_biomes" not in tt:
        continue
    print("EnvMan: keys", [k for k in tt if "fog" in k.lower() or "cloud" in k.lower() or "sky" in k.lower() or "ocean" in k.lower()][:20])
    print(f"{'environment':26} {'fog night':>10} {'morning':>9} {'day':>8} {'evening':>9}   distance (m) where exp(-d*x) = 0.5 / 0.05 (half / 95% fogged), by the day density")
    for e in tt["m_environments"]:
        day = e["m_fogDensityDay"]
        half = -math.log(0.5) / day if day > 0 else float("inf")
        low = -math.log(0.05) / day if day > 0 else float("inf")
        print(f"{e['m_name']:26} {e['m_fogDensityNight']:10.4f} {e['m_fogDensityMorning']:9.4f} {day:8.4f} {e['m_fogDensityEvening']:9.4f}   {half:8.0f} / {low:8.0f}")
