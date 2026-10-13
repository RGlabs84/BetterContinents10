#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
"""The contents check of the water bundle (run by tools/build-water.sh after the Unity build; also usable on any bundle with --describe).

    bundle_check.py assets/BCWater/bcwater           check; exit 1 when something is missing
    bundle_check.py --describe BUNDLE                print every shader's sub-shaders and passes with the program types per stage and graphics API

What it requires of bcwater:
  - the shaders BetterContinents/Water and BetterContinents/WaterProbe;
  - every property of the game's Custom/Water (names and types, from shader_state.txt) on BetterContinents/Water;
  - the two sub-shaders of Water, LOD 250 and LOD 200, each with a GrabPass `_BackgroundTex`;
  - in every non-grab pass of every shader: Direct3D 11 programs (vertex 15/16 and pixel 17/18), OpenGL Core programs (6/7/8) and Vulkan programs (25);
  - in every pass of the LOD 250 sub-shader: Direct3D 11 hull (21) and domain (22) programs (the OpenGL Core hull and domain are inside its program text).
The stencil, offset, blend and ZWrite states of the water passes are printed too (compare WATER-SHADER-FACTS.md, section 1).

Run as (ulimit -v 4000000; PYTHONWARNINGS=ignore nice -n 10 ~/upy/bin/python bundle_check.py ...). Needs UnityPy (~/upy).
"""
import os, re, sys
import UnityPy

HERE = os.path.dirname(os.path.abspath(__file__))
STATE = os.path.join(HERE, "shader_state.txt")
D3D_VERTEX, D3D_PIXEL, D3D_HULL, D3D_DOMAIN = {15, 16}, {17, 18}, {21}, {22}
GL, VULKAN = {6, 7, 8}, {25}
STAGES = ("progVertex", "progFragment", "progHull", "progDomain", "progGeometry")
CMP = {0: "Disabled", 1: "Never", 2: "Less", 3: "Equal", 4: "LEqual", 5: "Greater", 6: "NotEqual", 7: "GEqual", 8: "Always"}
PROPTYPE = {0: "Color", 1: "Vector", 2: "Float", 3: "Range", 4: "Texture"}


def game_properties():
    """name -> type of the game's Custom/Water properties, from shader_state.txt."""
    out, inside = {}, False
    for line in open(STATE):
        if line.startswith("--- properties"):
            inside = True
        elif line.startswith("--- keywords"):
            break
        elif inside:
            m = re.match(r"\s+(_\w+)\s+(\w+)\s+flags", line)
            if m:
                out[m.group(1)] = m.group(2)
    return out


def pass_programs(ps):
    """stage -> set of GPU program types over all platforms/tiers; also the number of programs per type in the vertex list."""
    types, counts = {}, {}
    for stage in STAGES:
        prog = getattr(ps, stage, None)
        found = set()
        if prog is not None and getattr(prog, "m_PlayerSubPrograms", None) is not None:
            for plist in prog.m_PlayerSubPrograms:
                for sp in plist:
                    found.add(sp.m_GpuProgramType)
                    if stage == "progVertex":
                        counts[sp.m_GpuProgramType] = counts.get(sp.m_GpuProgramType, 0) + 1
        types[stage] = found
    return types, counts


def shaders(bundle):
    env = UnityPy.load(bundle)
    for obj in env.objects:
        if obj.type.name == "Shader":
            d = obj.read()
            yield d.m_ParsedForm


def describe(bundle):
    for pf in shaders(bundle):
        print(f"=== {pf.m_Name}: properties {len(pf.m_PropInfo.m_Props)}, sub-shaders {len(pf.m_SubShaders)}")
        for si, ss in enumerate(pf.m_SubShaders):
            print(f"--- SubShader {si}: LOD {ss.m_LOD}, tags {dict(ss.m_Tags.tags)}")
            for pi, ps in enumerate(ss.m_Passes):
                st = ps.m_State
                if ps.m_Type == 2:
                    print(f"  Pass {pi}: GrabPass {ps.m_TextureName!r}")
                    continue
                types, counts = pass_programs(ps)
                so = st.stencilOp
                print(f"  Pass {pi}: {st.m_Name!r} LightMode {dict(st.m_Tags.tags).get('LIGHTMODE')} ZWrite {st.zWrite.val:g} ColorMask {int(st.rtBlend0.colMask.val)} "
                      f"Blend {int(st.rtBlend0.srcBlend.val)}/{int(st.rtBlend0.destBlend.val)} Offset {st.offsetFactor.val:g},{st.offsetUnits.val:g} "
                      f"Stencil ref {st.stencilRef.val:g} comp {CMP.get(int(so.comp.val), so.comp.val)}")
                print("     program types: " + "; ".join(f"{s[4:]} {sorted(t)}" for s, t in types.items() if t) + f"   (vertex programs per type {dict(sorted(counts.items()))})")


def check(bundle):
    bad = []
    seen = {}
    for pf in shaders(bundle):
        seen[pf.m_Name] = pf
    for name in ("BetterContinents/Water", "BetterContinents/WaterProbe"):
        if name not in seen:
            bad.append(f"the shader {name} is not in the bundle (it has {sorted(seen)})")
    water = seen.get("BetterContinents/Water")
    if water is not None:
        props = {p.m_Name: PROPTYPE.get(p.m_Type, p.m_Type) for p in water.m_PropInfo.m_Props}
        for name, typ in game_properties().items():
            if props.get(name) != typ:
                bad.append(f"BetterContinents/Water: property {name} is {props.get(name)}, Custom/Water has {typ}")
        lods = sorted(ss.m_LOD for ss in water.m_SubShaders)
        if lods != [200, 250]:
            bad.append(f"BetterContinents/Water: sub-shader LODs are {lods}, expected [200, 250]")
        for ss in water.m_SubShaders:
            grabs = [ps.m_TextureName for ps in ss.m_Passes if ps.m_Type == 2]
            if grabs != ["_BackgroundTex"]:
                bad.append(f"BetterContinents/Water LOD {ss.m_LOD}: GrabPass textures {grabs}, expected ['_BackgroundTex']")
    for name, pf in seen.items():
        for si, ss in enumerate(pf.m_SubShaders):
            n = 0
            for pi, ps in enumerate(ss.m_Passes):
                if ps.m_Type == 2:
                    continue
                n += 1
                types, _ = pass_programs(ps)
                where = f"{name} sub-shader {si} (LOD {ss.m_LOD}) pass {pi}"
                if not (types["progVertex"] & D3D_VERTEX) or not (types["progFragment"] & D3D_PIXEL):
                    bad.append(f"{where}: no Direct3D 11 vertex/pixel programs (vertex {sorted(types['progVertex'])}, fragment {sorted(types['progFragment'])})")
                if not ((types["progVertex"] | types["progFragment"]) & GL):
                    bad.append(f"{where}: no OpenGL Core programs")
                if not ((types["progVertex"] | types["progFragment"]) & VULKAN):
                    bad.append(f"{where}: no Vulkan programs")
                if name == "BetterContinents/Water" and ss.m_LOD == 250:
                    if not (types["progHull"] & D3D_HULL) or not (types["progDomain"] & D3D_DOMAIN):
                        bad.append(f"{where}: no Direct3D 11 hull/domain programs (hull {sorted(types['progHull'])}, domain {sorted(types['progDomain'])})")
            if n == 0:
                bad.append(f"{name} sub-shader {si} has no passes")
    return bad


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--describe"]
    if not args:
        print(__doc__)
        sys.exit(2)
    if "--describe" in sys.argv:
        describe(args[0])
        sys.exit(0)
    describe(args[0])
    problems = check(args[0])
    if problems:
        print("bundle_check FAILED:\n  " + "\n  ".join(problems))
        sys.exit(1)
    print(f"bundle_check: {args[0]} holds both shaders, all 33 properties of Custom/Water, Direct3D 11 + OpenGL Core + Vulkan programs in every pass")
