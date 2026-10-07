#!/home/rohan/upy/bin/python
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# The world height range each sky object that keeps its own height covers (FollowPlayer.m_lockYPos: Clouds, CloudCylinder, Distant_fog_planes,
# WaterPlane, OceanMist, the lightning objects): its position and scale in the scene, times the bounds of the meshes it draws. read_sky_objects.py
# cannot give that: the objects are in the prefab bundle (d59cfac) but the meshes they draw are assets of the main bundle (c4210710), reached through
# the prefab bundle's external file table, and they hang on child objects. This resolves each MeshFilter through that table (and stops if the bundle it
# is given does not hold the file the mesh is in) and lists the particle emitters' shape boxes too. Rotations are not applied (it says when a node has one).
# Usage (run under ~/valheim-testbed/heavy.sh, about 3 GB because of the main bundle): read_sky_ranges.py [prefab bundle [mesh bundle]]
#   defaults: the client's d59cfac and c4210710.
import sys
sys.path.insert(0, "/home/rohan/WubarrkCODING/libs-Tools/UNITY-ASSET-TOOLS")
import UnityPy
from valheim_paths import pid, sibling_bundle

prefab_path = sys.argv[1] if len(sys.argv) > 1 else sibling_bundle("d59cfac")
mesh_path = sys.argv[2] if len(sys.argv) > 2 else sibling_bundle("c4210710")
FOLLOW = {0: "Player", 1: "Camera", 2: "Average"}

env = UnityPy.load(prefab_path)
objs = {o.path_id: o for o in env.objects}


def comps(go):
    out = []
    for c in go.m_Component:
        o = objs.get(pid(c.component if hasattr(c, "component") else c))
        if o is not None:
            out.append(o)
    return out


def transform(go):
    for o in comps(go):
        if o.type.name == "Transform":
            return o
    return None


def children(go):
    kids = []
    for ch in transform(go).read_typetree()["m_Children"]:
        p = ch["m_PathID"]
        if p in objs:
            kids.append(objs[pid(objs[p].read().m_GameObject)].read())
    return kids


def follower(go):
    """the FollowPlayer typetree of the object when it keeps its own height, else None"""
    for c in comps(go):
        if c.type.name == "MonoBehaviour":
            try:
                tt = c.read_typetree()
            except Exception:
                continue
            if "m_lockYPos" in tt and "m_maxYPos" in tt and tt["m_lockYPos"]:
                return tt
    return None


# every node under an object that keeps its own height: its path, world y, accumulated scale, the MeshFilter's mesh reference, the particle shape
nodes = []


def walk(go, trail, y, scale, root):
    t = transform(go).read_typetree()
    lp, ls, rot = t["m_LocalPosition"], t["m_LocalScale"], t["m_LocalRotation"]
    y2 = y + lp["y"]
    scale2 = (scale[0] * ls["x"], scale[1] * ls["y"], scale[2] * ls["z"])
    rotated = max(abs(rot["x"]), abs(rot["y"]), abs(rot["z"])) > 1e-4
    entry = {"root": root, "path": "/".join(trail + [go.m_Name]), "y": y2, "scale": scale2, "rotated": rotated, "mesh": None, "shape": None}
    for o in comps(go):
        if o.type.name == "MeshFilter":
            m = o.read_typetree()["m_Mesh"]
            entry["mesh"] = (m["m_FileID"], m["m_PathID"])
        elif o.type.name == "ParticleSystem":
            sm = o.read_typetree().get("ShapeModule", {})
            entry["shape"] = (sm.get("type"), sm.get("m_Scale"), sm.get("m_Position"))
    nodes.append(entry)
    for k in children(go):
        walk(k, trail + [go.m_Name], y2, scale2, root)


roots = {}
for o in env.objects:
    if o.type.name != "GameObject":
        continue
    go = o.read()
    tt = follower(go)
    if tt is not None and go.m_Name not in roots:       # the bundle holds some of them twice (variants): the first is read
        roots[go.m_Name] = tt
        walk(go, [], 0.0, (1.0, 1.0, 1.0), go.m_Name)

# the external file table of the prefab bundle: file id -> CAB of the file the PPtr points into
externals = {}
for name, f in env.files.items():
    for k, sf in getattr(f, "files", {}).items():
        for i, e in enumerate(getattr(sf, "externals", []) or []):
            externals[i + 1] = e.path.split("/")[-1]
meshes_env = UnityPy.load(mesh_path)
cabs = {k for f in meshes_env.files.values() for k in getattr(f, "files", {})}
meshes = {o.path_id: o for o in meshes_env.objects if o.type.name == "Mesh"}

print(f"{len(roots)} sky objects keep their own height (FollowPlayer.m_lockYPos); meshes read from {mesh_path.split('/')[-1]}")
for name, tt in roots.items():
    print(f"== {name}: follows {FOLLOW.get(tt['m_follow'], tt['m_follow'])}, locked at its own y, m_maxYPos {tt['m_maxYPos']:g}")
    for n in (n for n in nodes if n["root"] == name):
        s = n["scale"]
        line = f"   {n['path']:48} y {n['y']:7.1f}  scale {s[0]:g} x {s[1]:g} x {s[2]:g}" + ("  (ROTATED: not applied)" if n["rotated"] else "")
        if n["mesh"] is not None:
            fid, p = n["mesh"]
            cab = externals.get(fid)
            if fid != 0 and cab not in cabs:
                line += f"  mesh in file {cab}, which is not in {mesh_path.split('/')[-1]}"
            elif p not in meshes:
                line += f"  mesh {p} not found"
            else:
                m = meshes[p].read()
                a = m.m_LocalAABB
                lo, hi = a.m_Center.y - a.m_Extent.y, a.m_Center.y + a.m_Extent.y
                line += (f"  mesh '{m.m_Name}' local y {lo:.2f}..{hi:.2f}  -> world y {n['y'] + lo * s[1]:.0f} .. {n['y'] + hi * s[1]:.0f} m,"
                         f" half-width {a.m_Extent.x * s[0]:.0f} x {a.m_Extent.z * s[2]:.0f} m")
        if n["shape"] is not None and n["shape"][0] in (5, 15, 16):     # Box, BoxShell, BoxEdge: Scale is the box's size
            _, size, pos = n["shape"]
            cy = n["y"] + (pos["y"] if pos else 0.0) * s[1]
            half = size["y"] * s[1] / 2.0
            line += f"  particle box {size['x'] * s[0]:g} x {size['y'] * s[1]:g} x {size['z'] * s[2]:g} m -> world y {cy - half:.1f} .. {cy + half:.1f} m (drift not included)"
        elif n["shape"] is not None:
            line += f"  particle emitter at y {n['y']:.1f} (shape type {n['shape'][0]})"
        print(line)
