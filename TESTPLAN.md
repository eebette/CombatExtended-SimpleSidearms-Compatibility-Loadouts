# Test plan — Sidearms & Supply

**Most of this plan is automated.** `test/run-supply-assert.sh <scenario> <save>` loads a
staged save and runs in-game assertions (`test/StagingMod/Source/SupplyTestRunner.cs`),
writing `test-results-<scenario>.json` into the shared profile, then self-exits:

```
./test/run-supply-stage.sh                                  # regenerate SUPPLY saves (quit after letter)
./test/run-supply-assert.sh supply1 SUPPLY-1-loadout-sidearms
./test/run-supply-assert.sh supply2 SUPPLY-2-refetch
```

`supply1` covers, in 11 phases: initial reconcile and physical fetch (memory contents,
roles, gladius stuff fix-up), reorder → role flip, a hand-set role on a DECLARED weapon
yielding to the loadout, a FORCED weapon surviving reconcile untouched, template forget,
manual-memory protection through template churn, pre-existing memory claimed by the loadout,
an undeclared equipped weapon keeping the role while carried and the loadout taking over when
it is stowed, and the gizmo-forget suppression sticking and then resuming when the weapon is
put back in the list. `supply2` covers the CE-capacity gate on Simple Sidearms' own weapon
retrieval.

## Benchmark

`./test/run-supply-bench.sh [label]` loads SUPPLY-1 and answers the two questions that decide
whether one reconciling hook is the right trigger:

- **cost** — microseconds per reconcile, with the module's patches active and again with them
  removed, in one process so the save and JIT state match
- **rate** — how often CE actually reaches the reconcile per colonist, counted live over 6000
  ticks rather than derived from CE's 1800-tick cooldown

The second matters because cost only counts multiplied by rate, and the rate is CE's to
decide: it registers `JobGiver_UpdateLoadout` in the colonist behaviour tree's priority
sorter, whose `GetPriority` bids 30f once the cooldown lapses.

Measured 2026-08-23 (CE 16.7.3.0, SS v1.6):

| | |
|---|---|
| reconcile overhead | **5.67 us/call** (183.45 patched vs 177.79 stock, whole TryGiveJob path) |
| observed rate | **0.67 calls per colonist per 1000 ticks** (~one every 1500 ticks) |
| cost at 20 colonists | **0.0005% of a 60fps frame** |

That settles the trigger question: a single reconciling hook costs three orders of magnitude
less than the 1%-of-frame bar set before measuring, so it stays, and no dirty-check or
event-driven fan-out is warranted. The observed rate also confirms the reconcile is not
player-triggered — nobody touched the game during the sample.

A note on the harness, since it produced a wrong answer first: the staging assembly has no
Harmony bootstrap of its own, so the call counter was never applied and the first run reported
a rate of 0.00. A rate of zero should have been read as "the instrument is broken", not as
"CE never calls it" — the same mistake shape as the 5k-iteration run that reported
FirstOrDefault costing more than full enumeration.


## Loadout weapons as sidearms

Stage: colonist, fresh CE loadout containing (in list order): sniper, shotgun, pistol, gladius,
plus some FMJ rifle ammo rows. Assign loadout.

- Within one loadout evaluation — CE throttles these to one per 1800 ticks (30s real time
  at 1x) per pawn, run at the pawn's next job selection; the Assign tab's loadout-column
  button ("update now"/Rearm) triggers it instantly and is the fastest test lever — the SS
  gizmo shows all four weapons remembered; default ranged = sniper; preferred melee =
  gladius; combat mode = ranged (only if it was BySkill before — a hand-set mode must
  survive).
- Reorder shotgun to top → default ranged flips to shotgun on next reconcile.
- Manually set pistol as default ranged via gizmo → reorder loadout again → pistol stays
  default (player override sticks).
- Remove shotgun from loadout → shotgun forgotten from gizmo. Manually remember shotgun,
  remove and re-add another weapon → manual shotgun memory untouched.
- Melee stuff fix-up: loadout lists gladius, pawn picks up a *plasteel* gladius → memory pair
  retargets to plasteel variant on next reconcile (gizmo tooltip shows the carried one).

## Ammo sustainment

Scope rule: ammo derivation rides CE's own per-loadout "Ad hoc" checkbox (vanilla CE uses
it to auto-supply the equipped primary; mod 2 extends it to every weapon DECLARED in that
loadout, at the loadout's mags count). Ad hoc unticked = pure CE curated contract: weapons
carried, no ammo, no demand. Incidental/manual memories derive nothing unless the "ammo for
ALL remembered" opt-in is on. Explicit caliber rows always win per-def.

- The staged loadout has Ad hoc ticked, mags = 2: pawn fetches mags for the LOADOUT-DECLARED
  guns (Gear tab counts ≈ magSize × 2 per caliber) — except the sniper caliber, whose
  explicit row of 10 must suppress derivation: exactly 10 carried.
- UNTICK Ad hoc in the loadout dialog → Rearm → derived demand gone; pawn drops the derived
  ammo (pure-CE parity check). Re-tick → demand returns.
- Manually remember an extra gun via gizmo (not in loadout) → NO derived ammo for it even
  with Ad hoc on. Flip "Ammo for ALL remembered weapons" on → its demand appears next Rearm
  (at the global spare-magazines count, not the loadout's).
- Excess-drop check: pawn carrying derived ammo does NOT drop it during loadout enforcement
  (virtual slots feed GetExcessThing too).
- CE ammo system disabled in CE settings → no derived ammo demand, no errors.

## Capacity-aware retrieval (SUPPLY-2)

This module does not fetch weapons — Simple Sidearms does, on its own, by default. What it
adds is the capacity limit SS never checks (neither its job giver nor its pickup toil, which
ends in a bare `innerContainer.TryAdd`).

- SUPPLY-2: both colonists remember an uncarried pistol, with pistols in a pile. "Roomy" has
  space and must end up carrying one — that also proves the gate is not cancelling retrievals
  it should allow. "Stuffed" is loaded to CE's bulk limit and must NOT, because CE reports no
  room. The phase is held open so "did not fetch" is only judged after the other pawn has had
  as long to act.
- Toggle the setting off → SS's retrieval runs unmodified, including for the over-capacity
  pawn (manual check).
- No fetch loop when no pistol exists on the map (SS simply generates no job).

## Regression

- Pawn with default loadout: zero behavior change, no records created.
- Save/load mid-state: template records persist; removing this mod mid-save leaves only
  remembered sidearms (inert SS data) behind.
- Dev log: no red errors from [Sidearms&Supply]; look for the reconcile WarningOnce.
