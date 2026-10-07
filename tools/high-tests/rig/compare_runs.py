#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# What final-runs.sh compares, as a script of its own so that it can be run again over the runs' output, which is small (no server, no game: run it where you like):
#   compare_runs.py <out folder of the rig> <prefix of the runs> [<prefix of an earlier set of the same runs>]  (a run that is not there is left out)
# Each pair is two runs of the same world (seed, size, heightmap) that differ in one thing: the build, or the High Terrain mode.
#   - the terrain and chunks of the towns' zones (towns-terrain.bin, towns-chunks.*), the locations, the NPCs and stations, the point table: byte for byte
#   - every object a zone places (towns-objects.tsv): as a list, then as a set (the order a zone controller is created in varies from run to run), then zone by zone.
#     A procedural location (a Mistlands town, a dungeon) lays itself out from the game's random state, which other code of the running server also draws on, so
#     its zone can differ between two runs of the very same build and mode: the optional third argument is that noise floor (the same run made earlier).
import os, sys
from collections import Counter

DETERMINISTIC = ["towns-terrain.bin", "towns-chunks.bin", "towns-chunks.tsv", "towns-npcs.tsv", "towns-stations.tsv", "towns-zones.tsv", "locations.tsv", "location-entries.tsv", "points-out.tsv"]


def load(folder, name):
    with open(os.path.join(folder, name), "rb") as f:
        return f.read()


def zone_of(line):
    parts = line.split("\t")
    return f"{parts[0]},{parts[1]}"


def objects(folder):
    return load(folder, "towns-objects.tsv").decode("utf-8").splitlines()


def compare(out, a, b, say):
    fa, fb = os.path.join(out, a), os.path.join(out, b)
    if not (os.path.isdir(fa) and os.path.isdir(fb)):
        say(f"{a} vs {b}: not compared, a run is missing")
        return
    same = [n for n in DETERMINISTIC if load(fa, n) == load(fb, n)]
    diff = [n for n in DETERMINISTIC if n not in same]
    oa, ob = objects(fa), objects(fb)
    if oa == ob:
        objs = f"every object identical, in the same order ({len(oa)})"
    elif Counter(oa) == Counter(ob):
        first = next(i for i, (x, y) in enumerate(zip(oa, ob)) if x != y) + 1
        objs = f"the same {len(oa)} objects, listed in another order (the first line that differs is {first})"
    else:
        only_a, only_b = Counter(oa) - Counter(ob), Counter(ob) - Counter(oa)
        zones = Counter(zone_of(l) for l in list(only_a.elements()) + list(only_b.elements()))
        objs = (f"{len(oa)} and {len(ob)} objects, {sum(only_a.values())} only in the first and {sum(only_b.values())} only in the second, in zones "
                + ", ".join(f"({z}): {n} lines" for z, n in sorted(zones.items(), key=lambda kv: -kv[1])) + f"; the other {len(set(map(zone_of, oa)) - set(zones))} zones identical")
    say(f"{a} vs {b}: {len(same)} of {len(DETERMINISTIC)} files identical" + (f" (DIFFERENT: {', '.join(diff)})" if diff else "") + f"; {objs}")
    ta, tb = os.path.join(fa, "high-rays.tsv"), os.path.join(fb, "high-rays.tsv")
    if os.path.exists(ta) and os.path.exists(tb):
        say(f"    point table: " + ("identical" if open(ta, "rb").read() == open(tb, "rb").read() else "DIFFERENT"))


def variants(out, labels, say):
    """The zones whose objects are not the same in every run, and which of the runs agree on them: a letter for each distinct content."""
    runs = {l: objects(os.path.join(out, l)) for l in labels if os.path.isdir(os.path.join(out, l))}
    by_zone = {}
    for label, lines in runs.items():
        zones = {}
        for line in lines:
            zones.setdefault(zone_of(line), []).append(line)
        for z, ls in zones.items():
            by_zone.setdefault(z, {})[label] = tuple(sorted(ls))
    for z, per in sorted(by_zone.items()):
        distinct = sorted(set(per.values()))
        if len(distinct) < 2:
            continue
        letters = {c: chr(ord("A") + i) for i, c in enumerate(distinct)}
        say(f"    zone ({z}): " + ", ".join(f"{l.split('-', 1)[1]} {letters[c]}" for l, c in per.items()))
    same = [z for z, per in by_zone.items() if len(set(per.values())) < 2]
    say(f"    the other {len(same)} zones hold the same objects in all {len(runs)} runs")


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    out, p = sys.argv[1], sys.argv[2]
    earlier = sys.argv[3] if len(sys.argv) > 3 else None
    lines = []

    def say(text):
        print(text)
        lines.append(text)

    say("-- two builds, one world (Heightmap Amount 2, High Terrain Auto): the 0.10.2 build and this one")
    compare(out, f"{p}-van06o", f"{p}-van06n", say)
    say("-- High Terrain On against Auto, the same world (Heightmap Amount 2)")
    compare(out, f"{p}-van06n", f"{p}-on2", say)
    say("-- High Terrain On against Auto, a world with no heightmap at all (Heightmap Amount 1)")
    compare(out, f"{p}-autovanilla", f"{p}-onvanilla", say)
    if earlier and os.path.isdir(os.path.join(out, f"{earlier}-onvanilla")):
        say(f"-- the noise floor: the same On world made twice, by builds that differ only in the console's parse ({earlier} and {p})")
        compare(out, f"{earlier}-onvanilla", f"{p}-onvanilla", say)
        say(f"-- and {p}'s Auto world against the earlier On run")
        compare(out, f"{p}-autovanilla", f"{earlier}-onvanilla", say)
    labels = [f"{p}-{n}" for n in ("autovanilla", "autovanilla2", "autovanilla3", "onvanilla", "onvanilla2")]
    if sum(os.path.isdir(os.path.join(out, l)) for l in labels) > 2:
        say("-- the same no-heightmap world, every run of it: the zones that are not the same in all of them, and the runs that agree (a letter for each distinct content)")
        variants(out, labels, say)
    say("-- the toggles of the Amount 81 world, typed through the game's console table (bc h ht)")
    base = os.path.join(out, f"{p}-high06")
    for t in ("off", "on", "Auto"):
        path = os.path.join(base, f"high-rays-{t}.tsv")
        if os.path.exists(path):
            first = open(os.path.join(base, "high-rays.tsv"), "rb").read()
            now = open(path, "rb").read()
            say(f"    after 'bc h ht {t}': " + ("the table of the world as it was made (patched)" if now == first else "a different table (the game's own rules)" if t == "off" else "a different table"))
    off = os.path.join(out, f"{p}-off81", "high-rays.tsv")
    if os.path.exists(off) and os.path.exists(os.path.join(base, "high-rays-off.tsv")):
        same = open(off, "rb").read() == open(os.path.join(base, "high-rays-off.tsv"), "rb").read()
        say("    a world made with High Terrain Off: its table is " + ("identical to the Auto world after 'bc h ht off'" if same else "DIFFERENT from the Auto world after 'bc h ht off'"))
    with open(os.path.join(out, f"{p}-compare.txt"), "w") as f:
        f.write("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
