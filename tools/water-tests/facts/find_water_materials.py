#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-07 for BC's own water (0.10.4).
"""W0: find every material of the game's SoftRef bundles whose shader is Custom/Water, and print its stored values.

    find_water_materials.py [--bundles DIR] [--shader-path-id N]

A material points at its shader by (file id, path id); Custom/Water's path id in bundle eb7a4308 is -1220688799747046602 (shader_state.py).
Each Material's raw bytes are searched for that 8-byte id (cheap: nothing is parsed until a material matches), then the match is read in
full: name, bundle, queue, keywords, floats, colours, textures. Read only; run as (ulimit -v 4000000; PYTHONWARNINGS=ignore nice -n 10 python).
"""
import os, struct, sys
import UnityPy

DIR = "/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/StreamingAssets/SoftRef/Bundles"
PATH_ID = -1220688799747046602
args = sys.argv[1:]
while args:
    if args[0] == "--bundles": DIR = args[1]; args = args[2:]
    elif args[0] == "--shader-path-id": PATH_ID = int(args[1]); args = args[2:]
    else: args = args[1:]
pattern = struct.pack("<q", PATH_ID)


def show(bundle, obj):
    d = obj.read()
    sp = d.m_SavedProperties
    print(f"== material {d.m_Name!r} in bundle {bundle} (path_id {obj.path_id}); shader {d.m_Shader.m_FileID}:{d.m_Shader.m_PathID}")
    print(f"   queue {d.m_CustomRenderQueue}, keywords {list(d.m_ValidKeywords) if getattr(d, 'm_ValidKeywords', None) else []}, disabled passes {list(d.stringTagMap.keys()) if getattr(d, 'stringTagMap', None) else []}, lightmapFlags {getattr(d, 'm_LightmapFlags', None)}, instancing {getattr(d, 'm_EnableInstancingVariants', None)}")
    for k, v in sp.m_Floats:
        print(f"   float {k} = {v:g}")
    for k, v in sp.m_Colors:
        print(f"   color {k} = ({v.r:g}, {v.g:g}, {v.b:g}, {v.a:g})")
    for k, v in sp.m_TexEnvs:
        tex = v.m_Texture
        name = None
        try:
            if tex.m_PathID != 0:
                name = tex.read().m_Name if tex.m_FileID == 0 else f"external {tex.m_FileID}:{tex.m_PathID}"
        except Exception as e:
            name = f"unreadable {tex.m_FileID}:{tex.m_PathID} ({type(e).__name__})"
        print(f"   tex {k} = {name!r} scale ({v.m_Scale.x:g}, {v.m_Scale.y:g}) offset ({v.m_Offset.x:g}, {v.m_Offset.y:g})")


found = 0
files = sorted(os.listdir(DIR))
for i, fn in enumerate(files):
    path = os.path.join(DIR, fn)
    try:
        env = UnityPy.load(path)
    except Exception as e:
        print(f"-- {fn}: unreadable ({type(e).__name__}: {str(e)[:80]})", file=sys.stderr)
        continue
    for obj in env.objects:
        if obj.type.name != "Material":
            continue
        raw = obj.get_raw_data()
        if pattern in raw[:512]:
            try:
                show(fn, obj)
            except Exception as e:
                print(f"== material in {fn} path_id {obj.path_id}: read failed {type(e).__name__}: {e}")
            found += 1
    if i % 100 == 99:
        print(f"-- scanned {i + 1}/{len(files)} bundles, {found} water materials so far", file=sys.stderr)
print(f"-- done: {found} materials use the shader")
