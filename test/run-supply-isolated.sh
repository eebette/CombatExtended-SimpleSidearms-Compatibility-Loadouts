#!/usr/bin/env bash
# Every phase in its own process, against a freshly loaded save.
#
# The sequenced run (run-supply-assert.sh) proves the phases work against
# accumulated state. This proves each one stands alone — which arranging cannot
# demonstrate on its own, because a phase can arrange everything its author
# remembered and still lean on something they did not.
#
# Slow by construction: one game launch per phase, ~90s each. A pre-release
# sweep, not something to run on every edit.
#
# Usage:
#   ./test/run-supply-isolated.sh [supply1] [SUPPLY-1-loadout-sidearms]
set -euo pipefail

SCENARIO="${1:-supply1}"
SAVE="${2:-SUPPLY-1-loadout-sidearms}"

REPO="$(cd "$(dirname "$0")/.." && pwd)"
COMPAT="$HOME/Projects/CombatExtended-SimpleSidearms Compatibility Patch"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
GS=(gamescope -W 1600 -H 900 --)
SAVEDATA="$COMPAT/test/SaveData"

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
rm -f "$SAVEDATA/test-results-$SCENARIO-iso-"*.json

run_one() {
    timeout --signal=TERM 20m "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" \
        "-celoadsave=$SAVE" "-ceassert=$SCENARIO:$1" >/dev/null 2>&1 || true
}

# Phase 0 also reports how many phases the scenario has, so the sweep does not
# need to know the count in advance.
echo "== isolated sweep: $SCENARIO =="
run_one 0
FIRST="$SAVEDATA/test-results-$SCENARIO-iso-00.json"
if [[ ! -f "$FIRST" ]]; then
    echo "== phase 0 produced no results; check Player.log ==" >&2
    exit 1
fi
COUNT=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['phaseCount'])" "$FIRST")
echo "   $COUNT phases"

for ((i = 1; i < COUNT; i++)); do
    printf '   phase %d/%d\n' "$i" "$((COUNT - 1))"
    run_one "$i"
done


# A test that flips persisted mod settings must not poison the next boot: a leaked
# loadoutWeaponsAsSidearms=False turned every later launch into a feature-off world
# (phase 0 burned its whole deadline; caught by a human watching pawns starve).
CFG="$SAVEDATA/Config/Mod_CESidearmsSupply_SupplyMod.xml"
if [[ -f "$CFG" ]] && grep -qE "<loadoutWeaponsAsSidearms>False</|<releasePending>True</" "$CFG"; then
    echo "== MOD CONFIG POISONED by this run — a test left non-default settings on disk ==" >&2
    grep -E "loadoutWeaponsAsSidearms|releasePending" "$CFG" >&2
    exit 1
fi

exec "$(dirname "$0")/verdict.py" --merge "$SAVEDATA/test-results-$SCENARIO-iso-"*.json
