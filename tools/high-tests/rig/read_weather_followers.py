#!/home/rohan/upy/bin/python
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# For every environment of EnvMan (the game's weather), lists its particle systems (EnvSetup.m_psystems) and the FollowPlayer ancestor each one
# hangs under, with that ancestor's m_lockYPos and m_maxYPos (FollowPlayer.cs): the follower decides where the effect is when the player is high.
# Usage (run under ~/valheim-testbed/heavy.sh, about 1 GB): read_weather_followers.py [bundle]    (default: the client's d59cfac)
import sys
sys.path.insert(0, "/home/rohan/WubarrkCODING/libs-Tools/UNITY-ASSET-TOOLS")
import UnityPy
from valheim_paths import pid

path = sys.argv[1] if len(sys.argv) > 1 else "/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/StreamingAssets/SoftRef/Bundles/d59cfac"
env = UnityPy.load(path)
objs = {o.path_id: o for o in env.objects}

def ppid(p):
    """path id of a PPtr, whether UnityPy gave an object or the typetree's {'m_FileID', 'm_PathID'} dict."""
    return p["m_PathID"] if isinstance(p, dict) else pid(p)


def comp_of(go, kind):
    for c in go.m_Component:
        o = objs.get(pid(c.component if hasattr(c, "component") else c))
        if o is not None and o.type.name == kind:
            return o
    return None

def follow_ancestor(go):
    chain = []
    seen = 0
    while go is not None and seen < 40:
        seen += 1
        chain.append(go.m_Name)
        for c in go.m_Component:
            o = objs.get(pid(c.component if hasattr(c, "component") else c))
            if o is not None and o.type.name == "MonoBehaviour":
                try:
                    tt = o.read_typetree()
                except Exception:
                    continue
                if "m_lockYPos" in tt and "m_maxYPos" in tt:
                    return chain, tt
        tr = comp_of(go, "Transform")
        if tr is None:
            break
        parent = tr.read().m_Father
        if pid(parent) == 0 or pid(parent) not in objs:
            break
        go = objs[pid(objs[pid(parent)].read().m_GameObject)].read()
    return chain, None

for o in env.objects:
    if o.type.name != "MonoBehaviour":
        continue
    try:
        tt = o.read_typetree()
    except Exception:
        continue
    if "m_environments" not in tt or "m_biomes" not in tt:
        continue
    for e in tt["m_environments"]:
        names = []
        for p in e.get("m_psystems", []):
            try:
                if ppid(p) == 0 or ppid(p) not in objs:
                    names.append("(not in this bundle)")
                    continue
                go = objs[ppid(p)].read()
                chain, f = follow_ancestor(go)
                names.append(f"{'/'.join(reversed(chain[:3]))} -> " + (f"FollowPlayer({'Player' if f['m_follow']==0 else 'Camera' if f['m_follow']==1 else 'Average'}, lockY {bool(f['m_lockYPos'])}, maxY {f['m_maxYPos']:g})" if f else "no follower"))
            except Exception as ex:
                names.append(f"? {ex}")
        print(f"{e['m_name']:22} {len(names)} particle system(s)")
        for n in names:
            print("      ", n)
