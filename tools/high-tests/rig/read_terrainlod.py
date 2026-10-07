#!/home/rohan/upy/bin/python
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# Reads TerrainLod's serialized values (how far and how finely the game draws distant terrain) out of the game's own asset bundle,
# for the Steam client and the Steam dedicated server, without starting either: the bundle d59cfac holds Assets/Systems/_GameMain.prefab
# (see libs-Tools/UNITY-ASSET-TOOLS/README.md). TerrainLod.cs has defaults (m_terrainSize 2400, m_vertexDistance 10, m_regionsPerAxis 3,
# m_updateStepDistance 256); the prefab may override them, so this prints what the prefab holds.
# Usage (run under ~/valheim-testbed/heavy.sh, about 1 GB): read_terrainlod.py [bundle ...]   (default: client's and server's d59cfac)
import os, sys
sys.path.insert(0, "/home/rohan/WubarrkCODING/libs-Tools/UNITY-ASSET-TOOLS")
import UnityPy
from valheim_paths import pid

STEAM = "/home/rohan/.local/share/Steam/steamapps/common/"
BUNDLES = sys.argv[1:] or [
    STEAM + "Valheim/valheim_Data/StreamingAssets/SoftRef/Bundles/d59cfac",
    STEAM + "Valheim dedicated server/valheim_server_Data/StreamingAssets/SoftRef/Bundles/d59cfac",
]
FIELDS = ["m_updateStepDistance", "m_terrainSize", "m_regionsPerAxis", "m_vertexDistance", "m_material"]

for path in BUNDLES:
    print("####", path)
    env = UnityPy.load(path)
    objs = {o.path_id: o for o in env.objects}
    found = 0
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        try:
            tt = o.read_typetree()
        except Exception:
            continue
        if "m_terrainSize" not in tt or "m_vertexDistance" not in tt:
            continue
        found += 1
        try:
            go = objs[pid(o.read().m_GameObject)].read()
            name = go.m_Name
        except Exception as e:
            name = "?"
        print(f"TerrainLod on '{name}' (path id {o.path_id}), enabled {tt.get('m_Enabled')}:")
        for k in FIELDS:
            print(f"   {k} = {tt.get(k)}")
        size, region, vertex = tt["m_terrainSize"], tt["m_regionsPerAxis"], tt["m_vertexDistance"]
        width = round(size / region / round(vertex))
        print(f"   -> {region} x {region} meshes of {size / region:.0f} m, {width} cells of {round(vertex)} m ({width + 1} x {width + 1} vertices each), {region * region * (width + 1) ** 2:,} vertices in all "
              f"({(width * region + 1) ** 2:,} without the doubled seams), reaching {size / 2:.0f} m from the player; rebuilt every {tt['m_updateStepDistance']} m")
    print("TerrainLod components found:", found)
