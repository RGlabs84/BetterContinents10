#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-07 for BC's own water (0.10.4).
"""W0: print the render state of the game's Custom/Water shader (bundle eb7a4308): sub-shaders, LODs, tags, passes, the GrabPass,
blend, ZWrite, ZTest, cull, stencil, offsets, keywords per program and the property list with its defaults.

    shader_state.py [BUNDLE]        (default: the Steam client's SoftRef/Bundles/eb7a4308)

Read only. Run as (ulimit -v 4000000; PYTHONWARNINGS=ignore nice -n 10 ~/upy/bin/python shader_state.py).
Needs UnityPy (~/upy, libs-Tools UNITY-ASSET-TOOLS/README.md).
"""
import os, sys
DEFAULT = "/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/StreamingAssets/SoftRef/Bundles/eb7a4308"
bundle = sys.argv[1] if len(sys.argv) > 1 else DEFAULT
import UnityPy

BLEND = {0: "Zero", 1: "One", 2: "DstColor", 3: "SrcColor", 4: "OneMinusDstColor", 5: "SrcAlpha", 6: "OneMinusSrcColor",
         7: "DstAlpha", 8: "OneMinusDstAlpha", 9: "SrcAlphaSaturate", 10: "OneMinusSrcAlpha"}
BLENDOP = {0: "Add", 1: "Sub", 2: "RevSub", 3: "Min", 4: "Max"}
CMP = {0: "Disabled", 1: "Never", 2: "Less", 3: "Equal", 4: "LEqual", 5: "Greater", 6: "NotEqual", 7: "GEqual", 8: "Always"}
CULL = {0: "Off", 1: "Front", 2: "Back"}
STENCILOP = {0: "Keep", 1: "Zero", 2: "Replace", 3: "IncrSat", 4: "DecrSat", 5: "Invert", 6: "IncrWrap", 7: "DecrWrap"}
PTYPE = {0: "Normal", 1: "Use", 2: "Grab"}
PROPTYPE = {0: "Color", 1: "Vector", 2: "Float", 3: "Range", 4: "Texture"}


def fv(x):
    """A SerializedShaderFloatValue: its value, and the material property that drives it when it has a name."""
    name = getattr(x, "name", "") or ""
    if name == "<noninit>":
        name = ""
    return f"{x.val:g}" + (f" (property {name})" if name else "")


def dump():
    env = UnityPy.load(bundle)
    for obj in env.objects:
        if obj.type.name != "Shader":
            continue
        d = obj.read()
        pf = d.m_ParsedForm
        print(f"=== {pf.m_Name} (path_id {obj.path_id}), fallback {pf.m_FallbackName!r}, custom editor {pf.m_CustomEditorName!r}")
        print("--- properties")
        for p in pf.m_PropInfo.m_Props:
            default = [round(getattr(p, f"m_DefValue_{i}_"), 6) for i in range(4)] if hasattr(p, "m_DefValue_0_") else None
            tex = getattr(p.m_DefTexture, "m_DefaultName", None) if getattr(p, "m_DefTexture", None) is not None else None
            print(f"  {p.m_Name:28s} {PROPTYPE.get(p.m_Type, p.m_Type):8s} flags {p.m_Flags} default {default} tex {tex!r} attrs {list(p.m_Attributes) if p.m_Attributes else []} desc {p.m_Description!r}")
        keywords = list(pf.m_KeywordNames)
        print("--- keywords", keywords)
        for si, ss in enumerate(pf.m_SubShaders):
            print(f"--- SubShader {si}: LOD {ss.m_LOD}, tags {dict(ss.m_Tags.tags)}")
            for pi, ps in enumerate(ss.m_Passes):
                st = ps.m_State
                print(f"  Pass {pi}: type {PTYPE.get(ps.m_Type, ps.m_Type)}, name {st.m_Name!r}, useName {ps.m_UseName!r}, grab texture {ps.m_TextureName!r}, tags {dict(st.m_Tags.tags)}, LOD {st.m_LOD}")
                if ps.m_Type == 2:
                    continue
                b = st.rtBlend0
                print(f"     ZTest {CMP.get(int(st.zTest.val), st.zTest.val)} {('(property ' + st.zTest.name + ')') if st.zTest.name not in ('', '<noninit>') else ''}; ZWrite {fv(st.zWrite)}; Cull {CULL.get(int(st.culling.val), st.culling.val)} {('(property ' + st.culling.name + ')') if st.culling.name not in ('', '<noninit>') else ''}; ZClip {fv(st.zClip)}")
                print(f"     Blend src {BLEND.get(int(b.srcBlend.val), b.srcBlend.val)} dst {BLEND.get(int(b.destBlend.val), b.destBlend.val)}; alpha src {BLEND.get(int(b.srcBlendAlpha.val), b.srcBlendAlpha.val)} dst {BLEND.get(int(b.destBlendAlpha.val), b.destBlendAlpha.val)}; op {BLENDOP.get(int(b.blendOp.val), b.blendOp.val)} / {BLENDOP.get(int(b.blendOpAlpha.val), b.blendOpAlpha.val)}; ColorMask {int(b.colMask.val)}; separate {st.rtSeparateBlend}; AlphaToMask {fv(st.alphaToMask)}")
                print(f"     Offset {fv(st.offsetFactor)}, {fv(st.offsetUnits)}; Stencil ref {fv(st.stencilRef)} read {fv(st.stencilReadMask)} write {fv(st.stencilWriteMask)}")
                for nm in ("stencilOp", "stencilOpFront", "stencilOpBack"):
                    so = getattr(st, nm)
                    print(f"       {nm}: pass {STENCILOP.get(int(so.pass_.val), so.pass_.val)} fail {STENCILOP.get(int(so.fail.val), so.fail.val)} zfail {STENCILOP.get(int(so.zFail.val), so.zFail.val)} comp {CMP.get(int(so.comp.val), so.comp.val)}")
                print(f"     Fog mode {st.fogMode}, lighting {st.lighting}")
                print(f"     platforms {list(ps.m_Platforms) if ps.m_Platforms is not None else None}")
                for stage in ("progVertex", "progFragment", "progHull", "progDomain", "progGeometry"):
                    prog = getattr(ps, stage, None)
                    if prog is None:
                        continue
                    subs = list(prog.m_SubPrograms) if prog.m_SubPrograms is not None else []
                    players = prog.m_PlayerSubPrograms if getattr(prog, "m_PlayerSubPrograms", None) is not None else []
                    nplayer = sum(len(x) for x in players)
                    print(f"       {stage}: {len(subs)} sub-programs, player sub-programs {nplayer}")
                    # per platform
                    for pidx, plist in enumerate(players):
                        kws = []
                        for sp in plist:
                            k = tuple(sorted({keywords[i] for i in (list(getattr(sp, 'm_KeywordIndices', []) or []) + list(getattr(sp, 'm_GlobalKeywordIndices', []) or []) + list(getattr(sp, 'm_LocalKeywordIndices', []) or []))}))
                            kws.append((sp.m_GpuProgramType, k))
                        if kws:
                            print(f"         platform block {pidx}: {len(plist)} programs; types {sorted({t for t, _ in kws})}; keyword sets {sorted({k for _, k in kws})}")
                print(f"     name indices: {sorted(ps.m_NameIndices.keys()) if isinstance(ps.m_NameIndices, dict) else ps.m_NameIndices}")


if __name__ == "__main__":
    dump()
