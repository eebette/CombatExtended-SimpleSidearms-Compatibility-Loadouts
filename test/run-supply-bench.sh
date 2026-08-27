#!/usr/bin/env bash
# In-game benchmark of the Loadout.GetSlotsFor postfix (CE's convention: benchmark
# inside RimWorld, not a desktop harness). Loads SUPPLY-1-loadout-sidearms and times
# full enumeration, FirstOrDefault and Any(predicate) — the last two being how CE's
# own callers use it — with the module's patches active and again with them removed.
# Writes test/SaveData/bench-results-<label>.json in the shared profile.
#
# Usage:
#   ./test/run-supply-bench.sh                    # measure the current build
#   ./test/run-supply-bench.sh supplybench-before # label the run for an A/B
set -euo pipefail

LABEL="${1:-supplybench}"

REPO="$(cd "$(dirname "$0")/.." && pwd)"
COMPAT="$HOME/Projects/CombatExtended-SimpleSidearms Compatibility Patch"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
GS=(gamescope -W 1600 -H 900 --)
SAVEDATA="$COMPAT/test/SaveData"
RESULT="$SAVEDATA/bench-results-$LABEL.json"

if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/CESimpleSidearmsCompat.Loadouts/CESimpleSidearmsCompat.Loadouts.csproj" -c Release
    dotnet build "$REPO/test/StagingMod/Source/SupplyTestStaging.csproj" -c Release
fi

rm -f "$RESULT"
timeout --signal=TERM 20m "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" \
    "-celoadsave=SUPPLY-1-loadout-sidearms" "-ceassert=$LABEL" || true

if [[ ! -f "$RESULT" ]]; then
    echo "== NO RESULTS FILE — bench never finished; check Player.log ==" >&2
    exit 1
fi

echo "== results: $RESULT =="
cat "$RESULT"
# A benchmark has no pass/fail, but it can still have measured nothing.
if grep -q '"crashed"' "$RESULT"; then
    echo "== BENCH CRASHED — the numbers above are not measurements ==" >&2
    exit 1
fi
