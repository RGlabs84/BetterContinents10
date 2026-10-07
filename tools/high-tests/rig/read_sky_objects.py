#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# Where the sky objects that keep their own height stand (FollowPlayer.m_lockYPos: Clouds, CloudCylinder, Distant_fog_planes, WaterPlane, OceanMist,
# the lightning objects), by their world position, scale and mesh bounds as the game's main bundle has them: the world height range each covers
# at the start (the scene's own numbers, before any script moves it).
# Usage (run under ~/valheim-testbed/heavy.sh, about 1 GB): read_sky_objects.py [bundle]    (default: the client's d59cfac)
import os
import sys
sys.path.insert(0, os.path.expanduser("~/WubarrkCODING/libs-Tools/UNITY-ASSET-TOOLS"))
import UnityPy
from valheim_paths import pid

path = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/share/Steam/steamapps/common/Valheim/valheim_Data/StreamingAssets/SoftRef/Bundles/d59cfac")
NAMES = {"Clouds", "CloudCylinder", "Distant_fog_planes", "WaterPlane", "OceanMist", "Thunder", "Rain"}
env = UnityPy.load(path)
objs = {o.path_id: o for o in env.objects}

def comp(go, kind):
    for c in go.m_Component:
        o = objs.get(pid(c.component if hasattr(c, "component") else c))
        if o is not None and o.type.name == kind:
            return o
    return None

def world(go):
    """world position (sum of local positions up the chain; rotations ignored) and the product of the local scales."""
    pos = [0.0, 0.0, 0.0]; scale = [1.0, 1.0, 1.0]; chain = []
    while go is not None and len(chain) < 40:
        tr = comp(go, "Transform")
        if tr is None:
            break
        t = tr.read_typetree()
        chain.append(go.m_Name)
        lp, ls = t["m_LocalPosition"], t["m_LocalScale"]
        pos = [pos[0] * 1 + lp["x"] * 1, pos[1] + lp["y"], pos[2] + lp["z"]]
        scale = [scale[0] * ls["x"], scale[1] * ls["y"], scale[2] * ls["z"]]
        f = t["m_Father"]["m_PathID"]
        if f == 0 or f not in objs:
            break
        go = objs[pid(objs[f].read().m_GameObject)].read()
    return pos, scale, chain

for o in env.objects:
    if o.type.name != "GameObject":
        continue
    go = o.read()
    if go.m_Name not in NAMES:
        continue
    pos, scale, chain = world(go)
    line = f"{go.m_Name:20} world y {pos[1]:9.1f}  scale y {scale[1]:9.2f}  under {'/'.join(reversed(chain[1:4]))}"
    mf = comp(go, "MeshFilter")
    if mf is not None:
        try:
            mesh = objs[mf.read_typetree()["m_Mesh"]["m_PathID"]].read()
            aabb = mesh.m_LocalAABB
            cy, ey = aabb.m_Center.y, aabb.m_Extent.y
            line += f"  mesh local y {cy - ey:.1f} .. {cy + ey:.1f}  -> world y {pos[1] + (cy - ey) * scale[1]:.0f} .. {pos[1] + (cy + ey) * scale[1]:.0f}"
        except Exception as e:
            line += f"  (mesh: {e})"
    print(line)
