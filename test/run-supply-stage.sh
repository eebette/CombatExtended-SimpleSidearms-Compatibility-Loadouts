#!/usr/bin/env bash
# Build both mods and regenerate the SUPPLY-* staged saves in the shared test
# profile (lives in the compat patch repo). After the in-game letter appears,
# quit and relaunch normally via the compat repo's test/run-test.sh, then load
# a SUPPLY save and UNPAUSE to watch reconcile/fetch happen.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
COMPAT="$HOME/Projects/CombatExtended-SimpleSidearms Compatibility Patch"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
# GS_WRAP: launch inside gamescope's nested compositor — immune to the desktop's
# display state (owner gaming via Proton, mode-list churn, XF86VidMode crashes).
GS=(gamescope -W 1600 -H 900 --)
SAVEDATA="$COMPAT/test/SaveData"

if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/CESidearmsSupply/CESidearmsSupply.csproj" -c Release
    dotnet build "$REPO/test/StagingMod/Source/SupplyTestStaging.csproj" -c Release
fi

rm -f "$SAVEDATA/Saves"/SUPPLY-*.rws
# Bounded: the staging mod shuts itself down when the saves are written, and the
# timeout is the backstop if it wedges before that.
timeout --signal=TERM 20m "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" -quicktest -cesupplystage || true

for save in SUPPLY-1-loadout-sidearms; do
    if [[ ! -f "$SAVEDATA/Saves/$save.rws" ]]; then
        echo "== STAGING FAILED: $save.rws was not written ==" >&2
        exit 1
    fi
done
echo "== staged: $(ls "$SAVEDATA/Saves"/SUPPLY-*.rws | wc -l) saves =="
