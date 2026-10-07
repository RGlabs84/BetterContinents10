#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# Reads ClutterSystem's grass entries (biome, altitude limits over the water) out of the game's own asset bundle d59cfac: the grass is
# placed by ClutterSystem.GenerateVegPatch at points whose y is 0, found by a ray from 500 m up (ClutterSystem.GetGroundInfo), and kept
# only between each entry's m_minAlt and m_maxAlt over the water (m_waterLevel, 30).
# Usage (run under ~/valheim-testbed/heavy.sh, about 1 GB): read_clutter.py [bundle]    (default: the client's d59cfac)
import os
import sys
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
    if "m_clutter" not in tt:
        continue
    print("ClutterSystem: water level", tt.get("m_waterLevel"), "distance", tt.get("m_distance"), "patch size", tt.get("m_grassPatchSize"))
    for c in tt["m_clutter"]:
        print(f"  {c.get('m_name', '?'):28} enabled {c.get('m_enabled')} biome {c.get('m_biome')} altitude over the water {c.get('m_minAlt')} .. {c.get('m_maxAlt')} instanced {c.get('m_instanced')} amount {c.get('m_amount')}")
