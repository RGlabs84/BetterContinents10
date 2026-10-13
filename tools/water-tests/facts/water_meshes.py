#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
"""W0: the meshes, scale and settings of the game's water objects: every GameObject whose name contains "water" (zone water, the far sea
"watersurface", interior and decorative water) in the client bundle c4210710, with its components, its Transform scale and, for a mesh
renderer, the mesh (vertex count, bounds, vertex spacing in x and z, uv range) and the material names.
Also the FollowPlayer settings of the WaterPlane in the _GameMain scene bundle d59cfac.

    water_meshes.py [NAME_REGEX]        (default: water)

Read only. Run as (ulimit -v 4000000; PYTHONWARNINGS=ignore nice -n 10 ~/upy/bin/python water_meshes.py) through heavy.sh.
Needs UnityPy (~/upy, libs-Tools UNITY-ASSET-TOOLS/README.md); opens c4210710 with its dependencies 86c3d76e and 2c2cce25.
"""
import os, re, sys
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

BUNDLES = os.path.expanduser("~/.local/share/Steam/steamapps/common/Valheim/valheim_Data/StreamingAssets/SoftRef/Bundles/")
pattern = re.compile(sys.argv[1] if len(sys.argv) > 1 else "water", re.I)


def comps(go):
    out = []
    for c in go.m_Component:
        try:
            ref = c.component if hasattr(c, "component") else c
            out.append((ref.type.name, ref.read()))
        except Exception as e:
            out.append(("ERR " + str(e)[:50], None))
    return out


def mesh_facts(mesh):
    h = MeshHandler(mesh)
    h.process()
    v = h.m_Vertices
    n = len(v) // 3 if len(v) and not hasattr(v[0], "__len__") else len(v)
    if n == 0:
        return "no vertices"
    pts = [(v[3 * i], v[3 * i + 1], v[3 * i + 2]) for i in range(n)] if not hasattr(v[0], "__len__") else [tuple(p) for p in v]
    xs = sorted({round(p[0], 4) for p in pts})
    zs = sorted({round(p[2], 4) for p in pts})
    ys = sorted({round(p[1], 4) for p in pts})
    def spacing(a):
        d = sorted({round(a[i + 1] - a[i], 4) for i in range(len(a) - 1)})
        return d[:4] if d else []
    text = f"{mesh.m_Name!r}: {n} vertices, x [{xs[0]:g}, {xs[-1]:g}] z [{zs[0]:g}, {zs[-1]:g}] y [{ys[0]:g}, {ys[-1]:g}], distinct x {len(xs)} z {len(zs)}, spacing x {spacing(xs)} z {spacing(zs)}"
    uv = h.m_UV0
    if uv is not None and len(uv):
        us = [p[0] for p in uv] if hasattr(uv[0], "__len__") else uv[0::2]
        vs = [p[1] for p in uv] if hasattr(uv[0], "__len__") else uv[1::2]
        text += f"; uv0 u [{min(us):g}, {max(us):g}] v [{min(vs):g}, {max(vs):g}]"
    else:
        text += "; no uv0"
    idx = h.get_triangles() if hasattr(h, "get_triangles") else None
    if idx:
        text += f"; submeshes {len(idx)} triangles {[len(t) for t in idx]}"
    return text


def show(env, label):
    seen = 0
    for obj in env.objects:
        if obj.type.name != "GameObject":
            continue
        try:
            go = obj.read()
        except Exception:
            continue
        if not pattern.search(go.m_Name or ""):
            continue
        seen += 1
        cl = comps(go)
        print(f"== {label} GameObject {go.m_Name!r} path_id {obj.path_id} layer {go.m_Layer} active {go.m_IsActive} comps {[n for n, _ in cl]}")
        for n, c in cl:
            if n == "Transform":
                print(f"   Transform pos ({c.m_LocalPosition.x:g}, {c.m_LocalPosition.y:g}, {c.m_LocalPosition.z:g}) scale ({c.m_LocalScale.x:g}, {c.m_LocalScale.y:g}, {c.m_LocalScale.z:g}) rot ({c.m_LocalRotation.x:g}, {c.m_LocalRotation.y:g}, {c.m_LocalRotation.z:g}, {c.m_LocalRotation.w:g})")
            elif n == "MeshFilter":
                try:
                    print("   MeshFilter", mesh_facts(c.m_Mesh.read()))
                except Exception as e:
                    print("   MeshFilter ERR", str(e)[:120])
            elif n == "MeshRenderer":
                mats = []
                for m in c.m_Materials:
                    try:
                        mats.append(m.read().m_Name)
                    except Exception as e:
                        mats.append("ERR " + str(e)[:40])
                print(f"   MeshRenderer materials {mats} castShadows {c.m_CastShadows} receiveShadows {c.m_ReceiveShadows} lightProbes {c.m_LightProbeUsage} reflectionProbes {getattr(c, 'm_ReflectionProbeUsage', None)} staticBatch {getattr(c.m_StaticBatchInfo, 'subMeshCount', None)}")
            elif n in ("MeshCollider", "BoxCollider"):
                extra = ""
                if n == "BoxCollider":
                    extra = f" size ({c.m_Size.x:g}, {c.m_Size.y:g}, {c.m_Size.z:g}) center ({c.m_Center.x:g}, {c.m_Center.y:g}, {c.m_Center.z:g}) trigger {c.m_IsTrigger}"
                print(f"   {n}{extra}")
            elif n == "MonoBehaviour":
                try:
                    script = c.m_Script.read().m_ClassName
                except Exception as e:
                    script = "ERR " + str(e)[:50]
                tree = None
                try:
                    tree = c.object_reader.read_typetree() if hasattr(c, "object_reader") else None
                except Exception:
                    tree = None
                fields = {k: v for k, v in (tree or {}).items() if not k.startswith("m_Script") and k not in ("m_GameObject", "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_Name", "m_EditorHideFlags", "m_EditorClassIdentifier")}
                print(f"   MonoBehaviour {script} {fields}")
    print(f"-- {label}: {seen} matching GameObjects")


def layout_facts(env, path_ids):
    """Vertex layout of the water meshes (distinct y values, extra vertices, normals, tangents, colours, uv corners)."""
    import collections
    for obj in env.objects:
        if obj.type.name != "GameObject" or obj.path_id not in path_ids:
            continue
        go = obj.read()
        for c in go.m_Component:
            ref = c.component
            if ref.type.name != "MeshFilter":
                continue
            m = ref.read().m_Mesh.read()
            h = MeshHandler(m)
            h.process()
            v = h.m_Vertices
            pts = [tuple(p) for p in v] if hasattr(v[0], "__len__") else [(v[3 * i], v[3 * i + 1], v[3 * i + 2]) for i in range(len(v) // 3)]
            ys = collections.Counter(round(p[1], 3) for p in pts)
            print(f"== layout {go.m_Name!r} path_id {obj.path_id}: {len(pts)} vertices, distinct y {sorted(ys.items())}")
            xs = sorted({round(p[0], 4) for p in pts if p[1] == 0.0 or abs(p[1] + 82.317) > 1})
            print(f"   x of the grid vertices: {len(xs)} distinct, first {xs[:4]}, last {xs[-2:]}")
            odd = [p for p in pts if abs(p[1]) > 1]
            print(f"   vertices off the y=0 grid: {[(round(a, 3), round(b, 3), round(c_, 3)) for a, b, c_ in odd[:6]]}")
            nrm, tan = h.m_Normals, h.m_Tangents
            print(f"   normals {len(nrm) if nrm is not None else None}, first {tuple(nrm[0]) if nrm is not None and len(nrm) else None}; tangents {len(tan) if tan is not None else None}, first {tuple(tan[0]) if tan is not None and len(tan) else None}; colours {None if h.m_Colors is None else len(h.m_Colors)}")
            uv = h.m_UV0
            print(f"   uv0 corners {[tuple(uv[i]) for i in (0, 1, len(uv) - 1)]}")


if __name__ == "__main__":
    env = UnityPy.load(BUNDLES + "c4210710", BUNDLES + "86c3d76e", BUNDLES + "2c2cce25")
    show(env, "c4210710")
    del env
    env2 = UnityPy.load(BUNDLES + "d59cfac", BUNDLES + "c4210710", BUNDLES + "86c3d76e")
    # only the WaterPlane objects of the scene bundle
    pattern = re.compile(r"^(waterplane|watersurface)$", re.I)
    show(env2, "d59cfac")
    layout_facts(env2, {8382862449791470714, 9022755149327211762})
