#!/usr/bin/env bash
# Automated acceptance run: load a staged SUPPLY save and execute a scenario's
# assertions in-game (SupplyTestRunner.cs), writing
#   test/SaveData/test-results-<scenario>.json
# in the compat repo's shared profile, then the game shuts itself down.
#
# Usage:
#   ./test/run-supply-assert.sh supply1 SUPPLY-1-loadout-sidearms
#   SKIP_BUILD=1 ./test/run-supply-assert.sh ...
#
# Steam must be running. The game window opens but needs no interaction; the
# whole run is bounded by `timeout` in case the runner wedges.
set -euo pipefail

SCENARIO="${1:?scenario (supply1)}"
SAVE="${2:?save name}"

REPO="$(cd "$(dirname "$0")/.." && pwd)"
COMPAT="$HOME/Projects/CombatExtended-SimpleSidearms Compatibility Patch"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
# GS_WRAP: launch inside gamescope's nested compositor — immune to the desktop's
# display state (owner gaming via Proton, mode-list churn, XF86VidMode crashes).
GS=(gamescope -W 1600 -H 900 --)
SAVEDATA="$COMPAT/test/SaveData"
RESULT="$SAVEDATA/test-results-$SCENARIO.json"

if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/CESidearmsSupply/CESidearmsSupply.csproj" -c Release
    dotnet build "$REPO/test/StagingMod/Source/SupplyTestStaging.csproj" -c Release
fi

CFG="$SAVEDATA/Config/Mod_CESidearmsSupply_SupplyMod.xml"
if [[ -f "$CFG" ]] && grep -qE "<loadoutWeaponsAsSidearms>False</|<releasePending>True</" "$CFG"; then
    echo "== MOD CONFIG POISONED before launch — a previous run left non-default settings ==" >&2
    grep -E "loadoutWeaponsAsSidearms|releasePending" "$CFG" >&2
    exit 1
fi
rm -f "$RESULT"
timeout --signal=TERM 20m "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" \
    "-celoadsave=$SAVE" "-ceassert=$SCENARIO" || true

if [[ ! -f "$RESULT" ]]; then
    echo "== NO RESULTS FILE — runner never finished; check Player.log ==" >&2
    exit 1
fi

# The verdict decides the exit code. Printing the file and stopping there is how
# this script spent its life reporting success for runs in which every phase failed.

# A test that flips persisted mod settings must not poison the next boot: a leaked
# loadoutWeaponsAsSidearms=False turned every later launch into a feature-off world
# (phase 0 burned its whole deadline; caught by a human watching pawns starve).
CFG="$SAVEDATA/Config/Mod_CESidearmsSupply_SupplyMod.xml"
if [[ -f "$CFG" ]] && grep -qE "<loadoutWeaponsAsSidearms>False</|<releasePending>True</" "$CFG"; then
    echo "== MOD CONFIG POISONED by this run — a test left non-default settings on disk ==" >&2
    grep -E "loadoutWeaponsAsSidearms|releasePending" "$CFG" >&2
    exit 1
fi

exec "$(dirname "$0")/verdict.py" "$RESULT"
