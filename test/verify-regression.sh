#!/usr/bin/env bash
# A regression test that has never been seen to fail is an assertion, not a test.
#
# This makes the A/B mechanical: remove the fix, prove the named phase FAILS
# (not VOID — a setup problem proves nothing about the fix), restore it, prove
# the suite passes. The exclusion-prune test passed with and without its fix
# twice before an A/B exposed why; run this instead of feeling suspicious.
#
# Run it BEFORE committing a fix+test pair (default mode: the uncommitted fix is
# stashed for run A). For an already-committed fix, name the pre-fix revision:
#
#   ./test/verify-regression.sh <phase-label> <file...>              # fix is uncommitted
#   ./test/verify-regression.sh --ref HEAD~1 <phase-label> <file...> # fix is committed
set -euo pipefail

REF=""
if [[ "${1:-}" == "--ref" ]]; then REF="$2"; shift 2; fi
PHASE="${1:?phase label}"; shift
FILES=("${@:?files containing the fix}")

REPO="$(cd "$(dirname "$0")/.." && pwd)"
COMPAT="$HOME/Projects/CombatExtended-SimpleSidearms Compatibility Patch"
RESULT="$COMPAT/test/SaveData/test-results-supply1.json"

build() { dotnet build "$REPO/Source/CESidearmsSupply/CESidearmsSupply.csproj" -c Release -v q --nologo >/dev/null; }
run()   { SKIP_BUILD=1 "$REPO/test/run-supply-assert.sh" supply1 SUPPLY-1-loadout-sidearms >/dev/null 2>&1 || true; }

phase_state() {
    python3 - "$RESULT" "$PHASE" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
for ph in d["phases"]:
    if ph["label"] == sys.argv[2]:
        print("invalid" if ph.get("invalid") else ("passed" if ph.get("passed") else "failed"))
        sys.exit(0)
print("absent")
PY
}

if [[ -z "$REF" ]]; then
    if git -C "$REPO" diff --quiet -- "${FILES[@]}"; then
        echo "!! ${FILES[*]} have no uncommitted changes. If the fix is already" >&2
        echo "!! committed, name the pre-fix revision: --ref HEAD~1" >&2
        exit 2
    fi
    echo "== A: stashing the uncommitted fix, expecting '$PHASE' to FAIL =="
    git -C "$REPO" stash push -q -- "${FILES[@]}"
    restore() { git -C "$REPO" stash pop -q; }
else
    if ! git -C "$REPO" diff --quiet -- "${FILES[@]}"; then
        echo "!! --ref mode needs a clean tree for ${FILES[*]}" >&2; exit 2
    fi
    echo "== A: taking ${FILES[*]} from $REF, expecting '$PHASE' to FAIL =="
    git -C "$REPO" checkout -q "$REF" -- "${FILES[@]}"
    restore() { git -C "$REPO" checkout -q HEAD -- "${FILES[@]}"; }
fi
trap restore EXIT

build; run
A=$(phase_state)
restore; trap - EXIT
# The A leg built the mod from the reverted source; put the real build back even on
# the rejection paths, or the tree is left with a stale DLL that poisons the next build.
build

case "$A" in
    failed)  echo "   A: failed — the test detects the regression" ;;
    invalid) echo "!! A: VOID — the phase's setup broke without the fix; it proves nothing about it" >&2; exit 1 ;;
    passed)  echo "!! A: PASSED without the fix — the test does not pin it" >&2; exit 1 ;;
    *)       echo "!! A: phase '$PHASE' not found in results" >&2; exit 1 ;;
esac

echo "== B: fix restored, expecting the suite to pass =="
run
if [[ "$(phase_state)" != "passed" ]]; then
    echo "!! B: '$PHASE' is $(phase_state) with the fix in place" >&2
    exit 1
fi
echo "   B: passed"
echo "== verified: '$PHASE' fails without the fix and passes with it =="
