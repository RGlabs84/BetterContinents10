#!/bin/bash
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
# Better Continents 0.10.3 (16k worlds) headless test rig, 2026-10-06. One copy per work stream under ~/WubarrkCODING/BetterContinents10/.work/bc-16k/
# (sectors, high, maps, export), each with its own port (file "port") and its own saves. Runs the Steam-installed Linux
# dedicated server IN PLACE (read only) with BepInEx injected by doorstop from profile/ (BepInEx core, Better Continents, and
# BCServerProbe: see probe/BCServerProbe/Plugin.cs). Saves go to saves/, HOME is a throwaway (fake-home.sh).
#   ./run.sh <label>          boots the world $WORLD (default BC16k). A world that does not exist yet is CREATED with Better
#                             Continents' settings from profile/BepInEx/config/BetterContinents.cfg (the probe raises BC's
#                             "world being created" flag, as the client's New World button does), seed $BCPROBE_SEED.
#   Any BCPROBE_* variable set by the caller reaches the probe (BCPROBE_TOWNS = zones to generate, BCPROBE_POINTS, ...).
# Output in out/<label>/: server-console.log (timestamped), bepinex.log, the probe's files, mem.csv (RSS each second), run.txt.
# Stops only the PID it started (SIGTERM, which saves) if TIMEOUT seconds pass.
set -u
TB="$(cd "$(dirname "$0")" && pwd)"
LABEL="${1:?usage: run.sh <label>}"; TIMEOUT="${TIMEOUT:-2400}"
PORT="$(cat "$TB/port")"
OUT="$TB/out/$LABEL"; mkdir -p "$OUT"
PROFILE="$TB/profile"; SAVES="$TB/saves"; DOORSTOP="$HOME/valheim-testbed/doorstop_libs"
GAME="$HOME/.local/share/Steam/steamapps/common/Valheim dedicated server"
NAME="BC 16k test $(basename "$TB")"; WORLD="${WORLD:-BC16k}"; PASSWORD="bctest"
for p in /proc/[0-9]*; do
  if tr '\0' ' ' < "$p/cmdline" 2>/dev/null | grep -q -- "-savedir $SAVES "; then echo "refusing: a server with -savedir $SAVES is running (${p#/proc/})"; exit 1; fi
done
if ss -uln 2>/dev/null | grep -q ":$PORT "; then echo "refusing: UDP port $PORT is in use"; exit 1; fi
source "$HOME/valheim-testbed/fake-home.sh"; fake_home "$PROFILE/fake-home"
cd "$GAME" || exit 1
START=$(date +%s%3N)
(
  fake_home_env
  export DOORSTOP_ENABLED=1 DOORSTOP_TARGET_ASSEMBLY="$PROFILE/BepInEx/core/BepInEx.Preloader.dll"
  export LD_LIBRARY_PATH="$DOORSTOP:./linux64:${LD_LIBRARY_PATH:-}" LD_PRELOAD="libdoorstop_x64.so:${LD_PRELOAD:-}"
  export SteamAppId=892970
  export BCPROBE_OUT="$OUT" BCPROBE_SEED="${BCPROBE_SEED:-BC16k}" BCPROBE_QUIT="${BCPROBE_QUIT:-1}" BCPROBE_GRID="${BCPROBE_GRID:-0}"
  exec nice -n 10 ./valheim_server.x86_64 -nographics -batchmode -name "$NAME" -port "$PORT" -world "$WORLD" \
    -password "$PASSWORD" -savedir "$SAVES" -public 0 -saveinterval 1800
) > >(perl -MTime::HiRes=time -ne 'BEGIN{$|=1} printf "%.2f %s", time, $_' > "$OUT/server-console.log") 2>&1 &
PID=$!
echo "pid $PID, started $(date '+%F %T'), world $WORLD, label $LABEL, port $PORT" > "$OUT/run.txt"
echo "t_s,rss_kb,hwm_kb" > "$OUT/mem.csv"
while kill -0 "$PID" 2>/dev/null; do
  T=$(( $(date +%s%3N) - START ))
  RSS=$(awk '/^VmRSS:/{print $2}' "/proc/$PID/status" 2>/dev/null); HWM=$(awk '/^VmHWM:/{print $2}' "/proc/$PID/status" 2>/dev/null)
  echo "$((T / 1000)).$(printf %03d $((T % 1000))),${RSS:-},${HWM:-}" >> "$OUT/mem.csv"
  if [ $((T / 1000)) -gt "$TIMEOUT" ] && [ ! -e "$OUT/timeout" ]; then
    touch "$OUT/timeout"; echo "timeout after ${TIMEOUT}s: SIGTERM $PID" >> "$OUT/run.txt"; kill -TERM "$PID"
  fi
  sleep 1
done
wait "$PID" 2>/dev/null; CODE=$?
T=$(( $(date +%s%3N) - START ))
echo "exit $CODE after $((T / 1000)).$(printf %03d $((T % 1000))) s, $(date '+%F %T')" >> "$OUT/run.txt"
cp "$PROFILE/BepInEx/LogOutput.log" "$OUT/bepinex.log" 2>/dev/null
find "$SAVES/worlds_local" -type f -printf '%s\t%p\n' >> "$OUT/run.txt" 2>/dev/null
fake_home_verdict >> "$OUT/run.txt"
