#!/home/rohan/upy/bin/python
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# Lists every FollowPlayer component (FollowPlayer.cs: an object that follows the camera or the player, optionally keeping its own height with
# m_lockYPos, or capping the height it follows to with m_maxYPos) in the game's asset bundle(s), with the object's name: the sky, clouds,
# rain and the ocean are such objects, and what they do at 16 km depends on these numbers.
# Usage (run under ~/valheim-testbed/heavy.sh, about 1 GB for d59cfac): read_followplayer.py [bundle ...]   (default: the client's d59cfac)
import sys
sys.path.insert(0, "/home/rohan/WubarrkCODING/libs-Tools/UNITY-ASSET-TOOLS")
import UnityPy
from valheim_paths import pid

paths = sys.argv[1:] or ["/home/rohan/.local/share/Steam/steamapps/common/Valheim/valheim_Data/StreamingAssets/SoftRef/Bundles/d59cfac"]
for path in paths:
    print("####", path)
    env = UnityPy.load(path)
    objs = {o.path_id: o for o in env.objects}
    n = 0
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        try:
            tt = o.read_typetree()
        except Exception:
            continue
        if "m_lockYPos" not in tt or "m_maxYPos" not in tt:
            continue
        n += 1
        try:
            go = objs[pid(o.read().m_GameObject)].read()
            name = go.m_Name
            tr = [objs[pid(c.component if hasattr(c, 'component') else c)] for c in go.m_Component]
            pos = None
            for t in tr:
                if t.type.name == "Transform":
                    tt2 = t.read_typetree()
                    p = tt2.get("m_LocalPosition")
                    pos = (round(p["x"], 1), round(p["y"], 1), round(p["z"], 1)) if p else None
        except Exception as e:
            name, pos = "?", None
        follow = {0: "Player", 1: "Camera", 2: "Average"}.get(tt.get("m_follow"), tt.get("m_follow"))
        print(f"  {name:32} follows {follow:8} lock y {bool(tt.get('m_lockYPos'))!s:5} max y {tt.get('m_maxYPos')}  local position {pos}")
    print("FollowPlayer components:", n)
