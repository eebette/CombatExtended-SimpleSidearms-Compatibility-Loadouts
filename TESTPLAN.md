# Test plan — Sidearms & Supply

**Most of this plan is automated.** `test/run-supply-assert.sh <scenario> <save>` loads a
staged save and runs in-game assertions (`test/StagingMod/Source/SupplyTestRunner.cs`),
writing `test-results-<scenario>.json` into the shared profile, then self-exits:

```
./test/run-supply-stage.sh                                  # regenerate SUPPLY saves (quit after letter)
./test/run-supply-assert.sh supply1 SUPPLY-1-loadout-sidearms
./test/run-supply-assert.sh supply2 SUPPLY-2-refetch
```

`supply1` covers, in 15 phases: initial reconcile + physical fetch (memory contents, roles,
mode, gladius stuff fix-up, ammo counts incl. explicit-row suppression), reorder→role
flip, manual role override sticking, template forget, manual-memory protection through
template churn, Ad hoc untick parity (stream + physical drop), Ad hoc re-tick, and the
ammo-for-all-remembered opt-in. `supply2` covers the CE-capacity gate on Simple Sidearms'
own weapon retrieval. The last five `supply1` phases cover the role model and the
suppression rule: a player-equipped weapon heads the list while carried, the loadout's
first takes over when it is stowed (with the displaced choice shelved), it returns when
carried again, and forgetting a declared weapon in SS's gizmo sticks instead of being
re-claimed on the next pass.

## Benchmark

`./test/run-supply-bench.sh [label]` loads SUPPLY-1 and times `Loadout.GetSlotsFor` in the
three shapes CE actually uses — full enumeration, `FirstOrDefault`, `Any(predicate)` — with
the module's patches active and again with them removed, in one process. 200k iterations x 5
rounds, best-of; anything shorter measures scheduler noise at these sub-microsecond costs.

Measured 2026-08-22 (CE 16.7.3.0, SS v1.6), before and after making the postfix lazy:

| | full | firstOnly | anyMatch |
|---|---|---|---|
| eager postfix | 0.583 us | 0.661 us | 0.660 us |
| lazy postfix | 0.704 us | 0.405 us | 0.410 us |
| stock CE | 0.289 us | 0.198 us | 0.194 us |

Eagerly materialising the stream made the short-circuit shapes cost as much as full
enumeration, which is what CE's callers are written to avoid. Overhead on those paths fell
from +0.463 us to +0.214 us. Full enumeration costs ~0.12 us more than before (one extra
iterator layer) — the right side of the trade, since `GetExcessEquipment` runs from
`GetPriority` on every think-tree evaluation while full enumeration only happens when CE is
hunting for work.

**Correction (2026-08-22).** An earlier run recorded "CE DOES evaluate default-loadout
pawns — Fetchy-Default fetched the pistol too" and credited this module's virtual slots.
CE cannot have done that: `JobGiver_UpdateLoadout` wraps its whole body in
`loadout != null && !loadout.defaultLoadout` at both call sites, so a default-loadout pawn
never gets a weapon job from it. The fetch almost certainly came from Simple Sidearms'
own `JobGiver_RetrieveWeapon`, which is in the vanilla think tree, reads only SS memory,
and is on by default. That discovery is what replaced the refetch feature with the
capacity gate; the old check was informational and never gated pass/fail.

Two things the harness pinned down that are worth keeping in mind:

- **The loadout owns what it lists.** Simple Sidearms auto-remembers any weapon a
  pawn equips as primary, so a loadout built around a gun the pawn already
  carries used to leave that gun unclaimed by the projection — and removing it
  from the loadout would leave it remembered, hence exempt from CE's drop,
  forever. Reconcile now claims every listed def regardless of who remembered it
  first; `preexisting-memory-claimed-by-loadout` is the regression test (memory
  forgotten on removal AND CE free to drop).
- **Four CE weapons put a colonist ~60% over bulk capacity**, so physical ammo
  cannot fit while the whole staged kit is carried. Phase 1 therefore asserts
  ammo DEMAND (the slot stream), and a dedicated phase sheds the kit down to the
  pistol before asserting that derived rounds physically arrive. The old
  carriage-based assertions in phase 1 could only ever pass by luck of fetch
  ordering.

Remaining MANUAL checks (visual/config-level, not covered by the runner): SS gizmo
rendering of remembered weapons; "CE ammo system disabled" settings run; save/load
mid-state persistence; removing-the-mod-mid-save leaves only inert SS data. Manual
loop: `../CombatExtended-SimpleSidearms Compatibility Patch/test/run-test.sh`, load a
SUPPLY save, unpause.

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
