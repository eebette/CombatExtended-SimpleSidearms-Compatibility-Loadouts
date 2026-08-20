# Test plan — Sidearms & Supply

**Most of this plan is automated.** `test/run-supply-assert.sh <scenario> <save>` loads a
staged save and runs in-game assertions (`test/StagingMod/Source/SupplyTestRunner.cs`),
writing `test-results-<scenario>.json` into the shared profile, then self-exits:

```
./test/run-supply-stage.sh                                  # regenerate SUPPLY saves (quit after letter)
./test/run-supply-assert.sh supply1 SUPPLY-1-loadout-sidearms
./test/run-supply-assert.sh supply2 SUPPLY-2-refetch
```

`supply1` covers, in phases (10 phases, full green 2026-08-20): initial reconcile + physical fetch (memory contents, roles,
mode, gladius stuff fix-up, ammo counts incl. explicit-row suppression), reorder→role
flip, manual role override sticking, template forget, manual-memory protection through
template churn, Ad hoc untick parity (stream + physical drop), Ad hoc re-tick, and the
ammo-for-all-remembered opt-in. `supply2` covers memory-only refetch. Full pass recorded
2026-08-17 (supply1 8/8 phases, supply2 pass, no log errors).

**SUPPLY-2 open question answered (2026-08-17 run): CE DOES evaluate default-loadout
pawns — Fetchy-Default fetched the pistol too.** Refetch reaches pawns with no assigned
loadout.

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

## Weapon refetch (opt-in; staging enables "Refetch ALL remembered" in this profile)

- SUPPLY-2: both pawns remember an uncarried pistol; pistols in a pile. Fetchy-Loadout
  (assigned empty loadout) must fetch one. Fetchy-Default (default loadout) reveals whether
  CE evaluates default-loadout pawns at all — record the outcome either way.
- Loadout-declared weapons refetch natively via their real slots (covered by loadout-sidearms test).
- Toggle "Refetch ALL remembered" off → no fetch jobs for manual memories.
- Verify no fetch loop when no pistol exists on the map (job simply not generated).

## Regression

- Pawn with default loadout: zero behavior change, no records created.
- Save/load mid-state: template records persist; removing this mod mid-save leaves only
  remembered sidearms (inert SS data) behind.
- Dev log: no red errors from [Sidearms&Supply]; look for the reconcile WarningOnce.
