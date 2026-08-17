# Manual test plan — Sidearms & Supply

Uses the compat patch's harness (`../CombatExtended-SimpleSidearms Compatibility Patch/test/run-test.sh`);
this mod is included in that profile's modlist. Iterate: edit → build → relaunch → load save.

## Doctrine projection

Stage: colonist, fresh CE loadout containing (in list order): sniper, shotgun, pistol, gladius,
plus some FMJ rifle ammo rows. Assign loadout.

- Within one loadout evaluation (~few in-game minutes; force with draft-toggle), the SS gizmo
  shows all four weapons remembered; default ranged = sniper; preferred melee = gladius;
  combat mode = ranged (only if it was BySkill before — a hand-set mode must survive).
- Reorder shotgun to top → default ranged flips to shotgun on next reconcile.
- Manually set pistol as default ranged via gizmo → reorder loadout again → pistol stays
  default (player override sticks).
- Remove shotgun from loadout → shotgun forgotten from gizmo. Manually remember shotgun,
  remove and re-add another weapon → manual shotgun memory untouched.
- Melee stuff fix-up: loadout lists gladius, pawn picks up a *plasteel* gladius → memory pair
  retargets to plasteel variant on next reconcile (gizmo tooltip shows the carried one).

## Ammo sustainment

- Same colonist: without any hand-added caliber rows, pawn fetches spare mags for every
  remembered gun (default 2 mags each; check Gear tab counts ≈ magSize × 2 per caliber).
- Add an explicit 7.62 row with a custom count → derived demand for that caliber disappears;
  pawn carries exactly the explicit count. Other calibers still auto-derived.
- Excess-drop check: pawn carrying auto-derived ammo does NOT drop it during loadout
  enforcement (virtual slots feed GetExcessThing too).
- CE ammo system disabled in CE settings → no derived ammo demand, no errors.

## Weapon refetch

- Drop the pistol via gizmo-forced drop or destroy it → pawn (or hauler-free pawn itself)
  fetches a replacement pistol from stockpile via normal loadout job. Verify no fetch loop
  when no pistol exists on the map (job simply not generated).

## Regression

- Pawn with default loadout: zero behavior change, no records created.
- Save/load mid-state: template records persist; removing this mod mid-save leaves only
  remembered sidearms (inert SS data) behind.
- Dev log: no red errors from [Sidearms&Supply]; look for the reconcile WarningOnce.
