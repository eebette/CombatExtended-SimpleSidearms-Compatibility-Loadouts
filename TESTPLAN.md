# Manual test plan — Sidearms & Supply

Uses the compat patch's harness (`../CombatExtended-SimpleSidearms Compatibility Patch/test/run-test.sh`);
this mod is included in that profile's modlist. Iterate: edit → build → relaunch → load save.

## Doctrine projection

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

Scope rule (the curated-loadout contract): ammo derives ONLY for weapons declared in the
loadout (doctrine), completing that declaration. Incidental/manual memories derive nothing
unless the "ammo for ALL remembered" opt-in is on. Explicit caliber rows always win per-def.

- Same colonist: pawn fetches spare mags for the four LOADOUT-DECLARED guns (default 2 mags
  each; Gear tab counts ≈ magSize × 2 per caliber) — except the sniper caliber, whose
  explicit row of 10 must suppress derivation: exactly 10 carried.
- Manually remember an extra gun via gizmo (not in loadout) → NO derived ammo for it. Flip
  "Ammo for ALL remembered weapons" on → its demand appears next Rearm.
- Excess-drop check: pawn carrying derived ammo does NOT drop it during loadout enforcement
  (virtual slots feed GetExcessThing too).
- CE ammo system disabled in CE settings → no derived ammo demand, no errors.

## Weapon refetch (opt-in; staging enables "Refetch ALL remembered" in this profile)

- SUPPLY-2: both pawns remember an uncarried pistol; pistols in a pile. Fetchy-Loadout
  (assigned empty loadout) must fetch one. Fetchy-Default (default loadout) reveals whether
  CE evaluates default-loadout pawns at all — record the outcome either way.
- Loadout-declared weapons refetch natively via their real slots (covered by doctrine test).
- Toggle "Refetch ALL remembered" off → no fetch jobs for manual memories.
- Verify no fetch loop when no pistol exists on the map (job simply not generated).

## Regression

- Pawn with default loadout: zero behavior change, no records created.
- Save/load mid-state: template records persist; removing this mod mid-save leaves only
  remembered sidearms (inert SS data) behind.
- Dev log: no red errors from [Sidearms&Supply]; look for the reconcile WarningOnce.
