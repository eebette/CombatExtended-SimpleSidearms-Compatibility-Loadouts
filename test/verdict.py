#!/usr/bin/env python3
"""Decide pass/fail for a SupplyTestRunner result file and say why.

The runner's own top-level "passed" is necessary but not sufficient: it is
computed as "no phase is marked failed", and a phase that was never reached is
not marked failed. So a run that stopped early reports true. This checks the
whole shape and prints the failing checks rather than the raw JSON, because a
dump of sixteen passing phases buries the one that matters.
"""
import json
import sys


def main(path):
    try:
        with open(path) as fh:
            data = json.load(fh)
    except (OSError, ValueError) as exc:
        print(f"== UNREADABLE RESULTS: {path}: {exc}", file=sys.stderr)
        return 1

    scenario = data.get("scenario", "?")
    phases = data.get("phases") or []
    reasons = []

    if data.get("crashed"):
        reasons.append(f"runner crashed: {data['crashed']}")
    if not data.get("passed", False):
        reasons.append("runner reported passed=false")
    if not phases:
        reasons.append("no phases ran — an empty suite is not a pass")

    unreached = [p for p in phases if not p.get("reached", False)]
    if unreached:
        reasons.append(
            f"{len(unreached)} phase(s) never reached: "
            + ", ".join(p.get("label", "?") for p in unreached)
        )

    failed = [p for p in phases if not p.get("passed", False)]
    if failed:
        reasons.append(f"{len(failed)} phase(s) failed")

    ok = not reasons
    print(f"== {scenario}: {'PASS' if ok else 'FAIL'} "
          f"({len(phases) - len(failed) - len(unreached)}/{len(phases)} phases) ==")

    for phase in phases:
        if not phase.get("reached", False):
            mark = "SKIP"
        elif phase.get("passed", False):
            mark = "ok"
        else:
            mark = "FAIL"
        print(f"  [{mark:>4}] {phase.get('label', '?')}")
        # Only expand a phase that went wrong. Informational checks are the
        # forensics for exactly that case, so show them here and nowhere else.
        if mark == "FAIL":
            for check in phase.get("checks") or []:
                if check.get("passed") and not check.get("informational"):
                    continue
                kind = "info" if check.get("informational") else "FAIL"
                print(f"         [{kind}] {check.get('name', '?')}: {check.get('detail', '')}")

    if reasons:
        # stderr is unbuffered and stdout is not, so without this the reasons
        # print above the detail they refer to.
        sys.stdout.flush()
        print("\n== why ==", file=sys.stderr)
        for reason in reasons:
            print(f"  - {reason}", file=sys.stderr)
    return 0 if ok else 1


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("usage: verdict.py <test-results-*.json>", file=sys.stderr)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
