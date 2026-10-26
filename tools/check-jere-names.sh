#!/usr/bin/env bash
# Added by Wubarrk on 2026-10-26 for Upgrade World compatibility (0.10.7).
#
# Lists every type and member of Expand World Data, Expand World Size and Upgrade World that Better Continents looks up by name, and
# resolves each against the DLLs in libs-Tools (EWD 1.73 and 1.79, EWS 1.45, UW 1.87), by reading their metadata (nothing runs). Exit
# code 1 when a name is missing from one of the DLLs, or when Better Continents' source holds a name the list does not know.
# Usage: tools/check-jere-names.sh        (memory-capped and niced like the suites; run it through heavy.sh when other jobs may be going)
set -u
cd "$(dirname "$0")/jere-names"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_GCHeapHardLimit="${DOTNET_GCHeapHardLimit:-0x40000000}"
exec nice -n 10 dotnet run -c Release -- "$@"
