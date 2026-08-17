#!/usr/bin/env bash
# Build both mods and regenerate the SUPPLY-* staged saves in the shared test
# profile (lives in the compat patch repo). After the in-game letter appears,
# quit and relaunch normally via the compat repo's test/run-test.sh, then load
# a SUPPLY save and UNPAUSE to watch reconcile/fetch happen.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
COMPAT="$HOME/Projects/CombatExtended-SimpleSidearms Compatibility Patch"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
SAVEDATA="$COMPAT/test/SaveData"

if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/CESidearmsSupply/CESidearmsSupply.csproj" -c Release
    dotnet build "$REPO/test/StagingMod/Source/SupplyTestStaging.csproj" -c Release
fi

rm -f "$SAVEDATA/Saves"/SUPPLY-*.rws
exec "$RIMWORLD" -savedatafolder="$SAVEDATA" -quicktest -cesupplystage
