# Test plan — Loadouts module

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

That last pair matters: the runner's own `"passed"` is `phases.All(p => !p.failed && !p.invalid)`, and a
phase that was never reached is not marked failed — so a run that stopped early reports
`passed: true`. Until 2026-08-24 the script `cat`ed the file and exited 0 regardless, so
every green result it ever produced meant only that a file had been written.

`supply1` covers, currently in 46 phases (one DLC-gated — the outfit-stand phase self-skips without its JobDef): initial reconcile and physical fetch (memory contents,
roles, gladius stuff fix-up), reorder → role flip, a hand-set role on a DECLARED weapon
yielding to the loadout, a FORCED weapon surviving reconcile untouched, template forget,
manual-memory protection through template churn, pre-existing memory claimed by the loadout,
an undeclared equipped weapon keeping the role while carried and the loadout taking over when
it is stowed, and the gizmo-forget suppression sticking and then resuming when the weapon is
put back in the list.

Phase groups, by origin (indexes shift as phases are added — go by label): the 2026-08-23
review's regressions, the exclusion design (#37), the 2026-08-26 review's findings
(haul-back, machine-equip asymmetry, inventory-tab path, Release() scope,
duplicate-memory handling, eligibility gate), and the later rounds' additions
(exclusion-integrity, release-lifecycle, player-surface, patch-inventory,
selection-ban and per-assignment-exclusion phases).
Persistence claims are `N()` checks over windows — some drive the window with a `poll`
action re-running the reconcile, others hold across natural cadence only; setup facts
are `P()` preconditions; one-shot outcomes are `C()`, sampled at the act when the state
is transient.


**Module isolation (2026-08-31):** the Tactics module ships its behaviors ON by
default, so with it in the shared mod list every SUPPLY scenario ran with its
findBest re-ranks and reload-abort component active. The runner now forces all
Tactics toggles off in-memory (`DisableTacticsModule`) and fails LOUD if the
module is present but the reflection misses. The core compat patch stays
active on purpose — it is this module's declared dependency.

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

Measured (CE 16.7.3.0, SS v1.6):

| | |
|---|---|
| reconcile overhead | **18.41 us/call** (195.46 patched vs 177.05 stock, whole TryGiveJob path) |
| observed rate | **0.79 calls per colonist per 1000 ticks** (~one every 1300 ticks) |
| cost at 20 colonists | **0.0017% of a 60fps frame** |

Re-measured 2026-08-25 against the set-difference reconcile. 3.2x the per-call cost of the
three-phase design it replaced — Target/Apply allocate where the old phases mutated in place —
and still ~1/600,000th of the frame budget, so no optimization pass is warranted. The prior
figures (5.67 us, 0.67 calls, 0.0005%) are kept here for the comparison.

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
  gladius. (The module never touches SS's combat mode — primaryWeaponMode is the
  player's, full stop.)
- Reorder shotgun to top → default ranged flips to shotgun on next reconcile.
- Manually set pistol as default ranged via gizmo → reorder loadout again → the loadout's
  first ranged weapon takes the role back (a hand-set role on a DECLARED weapon yields to
  the loadout; only roles on undeclared carried weapons, vetoes, and forced weapons stick).
- Remove shotgun from loadout → shotgun forgotten from gizmo. Manually remember shotgun,
  remove and re-add another weapon → manual shotgun memory untouched.
- Melee stuff fix-up: loadout lists gladius, pawn picks up a *plasteel* gladius → memory pair
  retargets to plasteel variant on next reconcile (gizmo tooltip shows the carried one).

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

Five rules, each first paid for as a real failure in this suite:

1. **Arrange the whole world — possession included.** The staged save spawns the kit on the
   map because phase one tests the fetch; every other phase must put the weapons in the
   pawn's hands itself, or in isolation it claims an empty set and SS's own equip-memorise
   fires mid-window while CE fetches.
2. **Context in arrange, acts in mutate.** mutate waits for the preconditions; a
   precondition that only becomes true inside mutate deadlocks the phase into VOID.
3. **Arrange reconciles the world to its spec in both directions** — it adds what is missing and removes what is extra (Baseline strips undeclared weapons and refreshes CE's inventory caches). Reuse a
   leftover before manufacturing a duplicate, or the mutate removes yours while the
   leftover keeps the behavior alive.
4. **Assert what this module controls.** "SS never remembers it again" and "CE drops it"
   are other mods' timing; the module's own writes and records are assertable.
5. **Assert what persists, not what passes through.** A job in flight and a mid-window
   role are transients that fall between 30-tick polls; the durable consequence is the
   evidence.

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

- The diagnostics gate cannot see load-time errors: `BaselineDiagnostics()` runs after the
  save loads, so anything logged during load — including a total patch failure from
  Bootstrap — is swallowed. The `every-declared-patch-is-applied` phase covers the patch
  half (all 15 Harmony targets verified to carry this mod's owner); a load-clean
  assertion for the rest is still open. Further gate blind spots, known and accepted:
  diagnostics are deduplicated by exact text (a repeated error is attributed to its
  first phase only); an error whose text matches any startup message is baselined away;
  `Log.ErrorOnce` that fires during load is pre-baselined, so the "any ErrorOnce is a
  failure" rule below is enforceable only for first occurrences after load; errors
  after the last phase are never read.
- `LoadoutsSessionComponent`'s deferred release runs at load, before any phase, so the
  releasePending's arming is covered (the toggle-off phase asserts the flag is set when
  the feature goes off in-game); the once-per-load consumer, LoadoutsSessionComponent
  .FinalizeInit, still has no in-suite coverage — it runs before the runner does.
- SS's own UI branch dispatch is not exercised: phases enter the gizmo scope directly
  (`InGizmo` raises it around the SS memory call) rather than driving
  `Gizmo_SidearmsList.handleInteraction`, so which branch a real click reaches is
  untested — the scope, not the click, is what the phases prove.
- Self-calibration rule: a phase whose assertion depends on an upstream scorer's
  choice ASKS the scorer at arrange time (exclude findBest's own favourite) rather
  than predicting it — predicting CE-modified scores cost three phase designs, and
  predicting is mirroring in test clothing.
- The ranged flavour of the selection ban is entangled with the sibling compat
  patch's P03 ammo-aware re-run and has no direct phase (the ordered-job phase pins
  the melee flavour; both ride the same canUseSidearmInstance gate). Safe by
  construction — the re-run calls the same filtered picker — but the interaction
  belongs to the compat repo's suite when that repo is next active.
- The TakeFromOther downgrade is driven at the postfix contract (the exact job shape
  CE builds); the end-to-end driver run is not — CE only takes from pack animals and
  hosted prisoners, and the issued job did not complete against a wandering animal in
  the window during development. GetPrioritySlot's carrier nomination (LowStock,
  carriedBy set) was verified live.
- The outfit-stand equip path is driven at the recorder's contract level (a
  playerForced UseOutfitStand job around a real AddEquipment), not through a real
  stand + driver; equipping an excluded weapon from an actual stand is a manual test — NOT YET RUN anywhere (the dev environment lacks the DLC, so the phase and the recorder clause both ship with zero executions).
- Accepted edge (round-7 F4): a pawn who records an exclusion, dies, is purged from
  CE's assignment dictionary by a save, whose loadout is then deleted and its id reused
  by a new loadout, and who is then resurrected and assigned that new loadout, keeps the
  dead loadout's exclusions (the RemoveLoadout sweep sees only living colonists). Five
  rare events in strict order; escape is one gizmo click or any reassignment.
- Drafted-side state is partially covered: the drafted gizmo's force branch has a phase
  (forcing an excluded weapon withdraws the exclusion); `ForcedWeaponWhileDrafted`
  surviving a release and the drafted reconcile-cadence gap still have none.
- No full save/load round trip of the comp (out-of-process).

## Regression

- Pawn with default loadout: zero behavior change, no records created.
- Save/load mid-state: template records persist.
- Uninstalling mid-save is NOT fully quiet. The per-pawn state (two `cessLoadouts_*` nodes on
  each humanlike pawn) is dropped silently when the comp class is absent — but
  `LoadoutsSessionComponent` is serialized into the save's game-component list by class
  name, so the first load without the mod logs one red "Could not find class
  CESimpleSidearmsCompat.Loadouts.LoadoutsSessionComponent" error (the node is a single self-closing
  element; RimWorld discards it and continues, and the save is clean after re-saving).
  Sidearm memories the mod wrote remain as ordinary SS data; "Release all claimed
  sidearms" before uninstalling clears them.
- Dev log: no red errors from [CE+SS Loadouts]; any [CE+SS Loadouts] Log.ErrorOnce is a failure.

## Combined-config phases (compat #42 import, 2026-08-30)

Three phases pin behaviors that exist only with BOTH mods enabled — the normal
player configuration, which the compat suite deliberately never runs (it tests
the compat patch alone):

- `a-role-judges-the-def-not-the-magazine` — role eligibility reads the def's
  nature (default projectile), not the loaded round; a magazine swap can no
  longer evict a role. Pinned by A/B (the old instance-classification code
  keeps the role on an EMP-natured def as long as it happens to be loaded with
  FMJ). The def's nature is flipped in place and restored because no base-CE
  primary-EMP round loads into a role-eligible weapon — the live trigger is
  modded content, the mechanism identical.
- `an-excluded-swap-cannot-eat-a-reload` — the three-hook collision (compat
  reload guard ends, our funnel veto refuses, compat repair restarts): end
  state pinned as reload-running + excluded weapon never in hand. Rides on
  cross-mod prefix registration order nothing else pins.
- `claims-follow-materials-and-the-shield-honors-them` — a declared def's
  claims span carried materials, and the compat patch's def-level drop shield
  protects the resulting multiset: a "knife x1" row with two looted materials
  keeps both. Pinned as intended combined semantics (claims mean "these
  carried weapons are loadout-managed"; counts live CE-side).
