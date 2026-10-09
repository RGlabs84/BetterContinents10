#!/bin/bash
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# The server runs behind the report of the High Terrain work, one after the other with the same DLL; one heavy job (cap 6G; the steps together take about ten minutes):
#   ~/valheim-testbed/heavy.sh 6G hfz-final tools/high-tests/rig/final-runs.sh <DLL of the code under test> <DLL of the 0.10.2 build> [prefix [step ...]]
# The rig (RIG, by default ~/WubarrkCODING/BetterContinents10/.work/bc-16k/highfix-rig) is a copy of ~/WubarrkCODING/BetterContinents10/.work/bc-16k/template with run.sh, hf_run.sh and probe/ of this folder
# added, and maps/ holding high.png (make_heightmap.py) and the point and zone lists of maps/ of this folder. Each run is a world made new, in the rig's saves/, under
# the prefix (default hfz): out/<prefix>-<run>/ holds what the probe wrote, out/<prefix>-compare.txt what compare_runs.py made of it.
# Steps (all of them by default):
#   high     Heightmap Amount 81, High Terrain Auto: 14 points on a ziggurat of 60 m to 16,100 m, the zones of the towns list, 'bc h ht' typed through the game's own
#            console table (off, on, Auto), and the same world again with 6 dungeons made as a dedicated server makes them; then High Terrain Off in a world made so
#   vanilla  land of vanilla heights (Heightmap Amount 2): the 0.10.2 build and this one in Auto, then this one On
#   none     no heightmap at all: this build On and Auto
#   noise    the world of 'none' made again, twice in Auto and once in On: how much two runs of one mode differ from each other
set -u
HERE="$(dirname "$(realpath "$0")")"
DLL="$(realpath "${1:?the DLL under test}")"; BASE="$(realpath "${2:?the 0.10.2 DLL}")"; P="${3:-hfz}"; shift 3 2>/dev/null || shift $#
STEPS=("$@"); [ ${#STEPS[@]} -gt 0 ] || STEPS=(high vanilla none noise)
RIG="${RIG:-$HOME/WubarrkCODING/BetterContinents10/.work/bc-16k/highfix-rig}"
cd "$RIG" || exit 1
E="BCPROBE_HIGH=1 BCPROBE_HIGHPTS=$PWD/maps/high-pts.tsv BCPROBE_POINTS=$PWD/maps/high-points.tsv BCPROBE_TOWNS=$PWD/maps/high-zones.tsv"
W="${P^}"   # the worlds' names: hfz -> Hfz
mkdir -p out
say() { echo "$@" | tee -a "out/$P-final-runs.txt"; }
run() { say "== $1 ($(date +%T))"; ./hf_run.sh "$@" > "out/$P-last-run.console.txt" 2>&1 || say "!! $1 failed: see out/$P-last-run.console.txt"; sed -n 1,2p "out/$1/run.txt" 2>/dev/null | tee -a "out/$P-final-runs.txt"; }
for step in "${STEPS[@]}"; do
  case $step in
    high)
      run $P-high06 ${W}High06 high06 "$DLL" 81 Auto $PWD/maps/high.png $E BCPROBE_HIGH_TOGGLE=1
      run $P-dungeons ${W}High06 high06 "$DLL" 81 Auto $PWD/maps/high.png BCPROBE_DUNGEONS=6
      run $P-off81 ${W}Off81 high06 "$DLL" 81 Off $PWD/maps/high.png $E;;
    vanilla)
      run $P-van06o ${W}Van06o van06 "$BASE" 2 Auto $PWD/maps/high.png $E
      run $P-van06n ${W}Van06n van06 "$DLL" 2 Auto $PWD/maps/high.png $E
      run $P-on2 ${W}On2 van06 "$DLL" 2 On $PWD/maps/high.png $E;;
    none)
      run $P-onvanilla ${W}OnVan van06 "$DLL" 1 On none $E
      run $P-autovanilla ${W}AutoVan van06 "$DLL" 1 Auto none $E;;
    noise)
      run $P-autovanilla2 ${W}AutoVan2 van06 "$DLL" 1 Auto none $E
      run $P-autovanilla3 ${W}AutoVan3 van06 "$DLL" 1 Auto none $E
      run $P-onvanilla2 ${W}OnVan2 van06 "$DLL" 1 On none $E;;
    *) say "unknown step $step";;
  esac
done
# EARLIER: the prefix of runs made before with a build that differs only a little (a noise floor to compare with), if there are any.
python3 "$HERE/compare_runs.py" out "$P" ${EARLIER:-} | tee -a "out/$P-final-runs.txt"
