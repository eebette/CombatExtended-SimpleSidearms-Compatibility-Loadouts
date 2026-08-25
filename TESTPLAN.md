# Test plan — Sidearms & Supply

**Most of this plan is automated.** `test/run-supply-assert.sh <scenario> <save>` loads a
staged save and runs in-game assertions (`test/StagingMod/Source/SupplyTestRunner.cs`),
writing `test-results-<scenario>.json` into the shared profile, then self-exits:

```
./test/run-supply-stage.sh                                  # regenerate SUPPLY saves (quit after letter)
./test/run-supply-assert.sh supply1 SUPPLY-1-loadout-sidearms
```

`run-supply-assert.sh` exits non-zero when the run did not pass — via `test/verdict.py`,
which also prints the failing checks (and that phase's informational checks, which exist to
diagnose exactly that) rather than dumping sixteen phases of JSON. It fails on a crash, on a
failed phase, on a phase that was never reached, and on an empty suite.

That last pair matters: the runner's own `"passed"` is `phases.All(p => !p.failed)`, and a
phase that was never reached is not marked failed — so a run that stopped early reports
`passed: true`. Until 2026-08-24 the script `cat`ed the file and exited 0 regardless, so
every green result it ever produced meant only that a file had been written.

`supply1` covers, in 17 phases: initial reconcile and physical fetch (memory contents,
roles, gladius stuff fix-up), reorder → role flip, a hand-set role on a DECLARED weapon
yielding to the loadout, a FORCED weapon surviving reconcile untouched, template forget,
manual-memory protection through template churn, pre-existing memory claimed by the loadout,
an undeclared equipped weapon keeping the role while carried and the loadout taking over when
it is stowed, and the gizmo-forget suppression sticking and then resuming when the weapon is
put back in the list.

The last five phases are the regressions from the 2026-08-23 review: an ordinary equip must
not be read as a deliberate forget, a forced weapon must survive its row leaving the loadout,
a declared-but-uncarried weapon must not be remembered with a guessed material, a hand-cleared
role must stay cleared, and deleting the loadout must not wipe anything. Each is a `negative`
check — re-evaluated on every poll and failing the phase the moment it trips, rather than
latching on the first sample.

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

## How a phase is built

Each phase is `arrange -> wait for preconditions -> mutate -> assert`:

- **arrange** establishes the world (Baseline: loadout assignment and rows, memory reseeded,
  all player intent cleared) rather than inheriting it from earlier phases.
- **P() preconditions** prove the arrangement took. If they never hold, the phase reports
  INVALID/VOID — a broken test, not broken code — and mutate never fires.
- **mutate** performs the act, only once the world is ready. Outcome checks are not evaluated
  before it: a positive check latches, and evaluated pre-act it latches on the arranged world.
- **N() negatives** re-evaluate every poll and must hold across `minTicks`. Any phase whose
  name is a persistence claim ("never", "survives", "sticks") asserts it this way — a positive
  check plus a window samples once and idles.
- Every phase carries a **state dump** (informational, re-evaluates every poll). When it
  disagrees with the checks beside it, a check has latched on the wrong world.

Two suites, one set of phases: `run-supply-assert.sh` runs them in sequence against
accumulated state (~3 min); `run-supply-isolated.sh` runs each in its own process against a
freshly loaded save (~25 min, pre-release). A phase that passes in sequence and fails alone
leans on something its arrange did not establish — that shape found three real harness bugs
on its first run.

New regression tests are verified with `test/verify-regression.sh <phase> <files>`: the fix is
removed, the phase must FAIL (VOID is rejected — a setup break proves nothing about the fix),
the fix restored, the suite must pass. A regression test never seen failing is an assertion,
not a test.

## Known gaps

- **SS's UI branch dispatch is not exercised.** The intent hooks fire inside a gizmo
  interaction scope, and the tests enter that scope the way `handleInteraction` does before
  making the call SS's own branch would make — so the hooks under test are real. Which branch
  a given click reaches is SS's business and is untested. Confirm by hand: right-click a
  carried sidearm in the gizmo and check the pawn does not re-acquire it within the minute.
- **The eligibility predicate has no negative test.** Nothing stages a pawn SS would refuse
  (slot limit reached, over the per-weapon mass cap, pacifist). Deleting `IsLegalSidearm`
  would leave the suite green.
- **No save/load round trip.** `CompLoadoutSidearms.PostExposeData` is untested; `claimed`,
  `dontEquip` and both role vetoes should survive a reload.
- **No settings-toggle coverage**, including the deferred release when the feature is
  switched off with no save loaded.

## Regression

- Pawn with default loadout: zero behavior change, no records created.
- Save/load mid-state: template records persist.
- Uninstalling mid-save is not silent. The orphaned GameComponent node produces two red
  errors at load — "could not find class ... trying to use Verse.GameComponent" and the
  SaveableFromNode exception behind it — each dumping the component's serialized XML, which
  on a mature colony is several kilobytes. RimWorld then drops the null and moves on, so it
  is a one-time cost, but it is not nothing. The save-time prune is what bounds its size.
  Sidearm memories the mod wrote are left behind as inert SS data; "Release all claimed
  sidearms" in the settings clears them first if the player uses it before uninstalling.
- Dev log: no red errors from [Sidearms&Supply]; look for the reconcile WarningOnce.
