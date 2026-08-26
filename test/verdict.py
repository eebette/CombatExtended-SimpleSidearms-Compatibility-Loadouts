#!/usr/bin/env python3
"""Decide pass/fail for a SupplyTestRunner result file and say why.

The runner's own top-level "passed" is necessary but not sufficient: it is
computed as "no phase is marked failed", and a phase that was never reached is
not marked failed. So a run that stopped early reports true. This checks the
whole shape and prints the failing checks rather than the raw JSON, because a
dump of sixteen passing phases buries the one that matters.
"""
import json
import re
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

    invalid = [p for p in phases if p.get("invalid", False)]
    if invalid:
        reasons.append(
            f"{len(invalid)} phase(s) INVALID — preconditions never held, so they tested "
            "nothing: " + ", ".join(p.get("label", "?") for p in invalid)
        )

    failed = [p for p in phases if not p.get("passed", False) and not p.get("invalid", False)]
    if failed:
        reasons.append(f"{len(failed)} phase(s) failed")

    ok = not reasons
    print(f"== {scenario}: {'PASS' if ok else 'FAIL'} "
          f"({len(phases) - len(failed) - len(invalid) - len(unreached)}/{len(phases)} phases) ==")

    for phase in phases:
        if not phase.get("reached", False):
            mark = "SKIP"
        elif phase.get("invalid", False):
            mark = "VOID"
        elif phase.get("passed", False):
            mark = "ok"
        else:
            mark = "FAIL"
        print(f"  [{mark:>4}] {phase.get('label', '?')}")
        # Only expand a phase that went wrong. Informational checks are the
        # forensics for exactly that case, so show them here and nowhere else.
        if phase.get("diagnostic"):
            print(f"         [diag] {phase['diagnostic']}")
        if mark in ("FAIL", "VOID"):
            for check in phase.get("checks") or []:
                if check.get("passed") and not check.get("informational"):
                    continue
                if check.get("informational"):
                    kind = "info"
                elif check.get("precondition"):
                    kind = "PRE"
                else:
                    kind = "FAIL"
                print(f"         [{kind}] {check.get('name', '?')}: {check.get('detail', '')}")

    if reasons:
        # stderr is unbuffered and stdout is not, so without this the reasons
        # print above the detail they refer to.
        sys.stdout.flush()
        print("\n== why ==", file=sys.stderr)
        for reason in reasons:
            print(f"  - {reason}", file=sys.stderr)
    return 0 if ok else 1


def merge(paths):
    """One result file per phase, from an isolated sweep. A phase missing entirely is a
    failure — it means that process never wrote results."""
    merged = {"scenario": "", "passed": True, "phases": []}
    for path in sorted(paths):
        try:
            with open(path) as fh:
                data = json.load(fh)
        except (OSError, ValueError) as exc:
            print(f"== UNREADABLE: {path}: {exc}", file=sys.stderr)
            return 1
        merged["scenario"] = data.get("scenario", "?") + " (isolated)"
        if data.get("crashed"):
            merged["crashed"] = data["crashed"]
        # Identity, not just count: 26 copies of phase 0 must not report 26/26.
        m = re.search(r"-iso-(\d+)\.json$", path)
        if m is not None and data.get("isolatedPhase") is not None \
                and int(m.group(1)) != data["isolatedPhase"]:
            print(f"== MISMATCH: {path} holds isolatedPhase {data['isolatedPhase']} ==",
                  file=sys.stderr)
            merged["passed"] = False
        merged["phases"].extend(data.get("phases") or [])

    expected = None
    for path in sorted(paths):
        with open(path) as fh:
            expected = json.load(fh).get("phaseCount")
        break
    labels = [p.get("label") for p in merged["phases"]]
    if len(set(labels)) != len(labels):
        dupes = sorted({l for l in labels if labels.count(l) > 1})
        print(f"== DUPLICATE PHASES IN SWEEP: {', '.join(dupes)} ==", file=sys.stderr)
        merged["passed"] = False
    if expected is not None and len(merged["phases"]) != expected:
        print(f"== ISOLATED SWEEP INCOMPLETE: {len(merged['phases'])} of {expected} phases "
              "produced results ==", file=sys.stderr)
        merged["passed"] = False

    merged["passed"] = merged["passed"] and all(
        p.get("passed") and not p.get("invalid") for p in merged["phases"]
    )
    tmp = "/tmp/verdict-merged.json"
    with open(tmp, "w") as fh:
        json.dump(merged, fh)
    return main(tmp)


if __name__ == "__main__":
    if len(sys.argv) == 2:
        sys.exit(main(sys.argv[1]))
    if len(sys.argv) > 2 and sys.argv[1] == "--merge":
        sys.exit(merge(sys.argv[2:]))
    print("usage: verdict.py <results.json> | verdict.py --merge <results-iso-*.json>", file=sys.stderr)
    sys.exit(2)
