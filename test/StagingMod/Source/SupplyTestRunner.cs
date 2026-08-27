using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CombatExtended;
using PeteTimesSix.SimpleSidearms;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;

namespace CESupplyTestStaging
{
    /// <summary>
    /// Headless-ish acceptance harness. Launch with:
    ///   -celoadsave=SUPPLY-1-loadout-sidearms -ceassert=supply1
    /// Loads the save from the main menu, runs the scenario's phases (each phase =
    /// optional mutation + polled checks with a tick deadline), writes
    /// test-results-&lt;scenario&gt;.json into the save-data folder, then shuts the
    /// game down. Assertions poll every 30 ticks so slow fetch jobs simply take
    /// longer instead of failing; a phase fails only on deadline.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class TestBoot
    {
        static TestBoot()
        {
            // Scenario prefix routing: this runner owns "supply"; the compat patch's
            // CETestRunner owns "cetest". Both staging mods share the test profile.
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out string scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("supply") || scenario.StartsWith("supplybench"))
            {
                return;
            }
            if (GenCommandLine.TryGetCommandLineArg("celoadsave", out string save) && !save.NullOrEmpty())
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    Log.Message($"[SupplyTest] Auto-loading save '{save}'.");
                    GameDataSaveLoader.LoadGame(save);
                });
            }
        }
    }

    public class TestRunnerComponent : GameComponent
    {
        private class Check
        {
            public string name;
            public Func<(bool pass, string detail)> eval;
            public bool informational; // recorded, never fails the run
            // Must-not-happen. Re-evaluated on every poll instead of latching on first pass,
            // and a failure fails the phase immediately rather than waiting for the deadline.
            // Without this a negative check passes at tick 0 — before the thing it forbids
            // could have happened — and is never looked at again.
            public bool negative;
            // Something the phase needs to be TRUE before its real checks mean anything —
            // the pawn is carrying the weapon, the role is set, the row exists. A phase whose
            // precondition never holds is reported INVALID rather than passed or failed:
            // it did not test what it claims to, and that is a different problem from the
            // code being wrong. Unasserted preconditions are how every vacuous phase in this
            // suite got that way.
            public bool precondition;
            public bool passed;
            public string lastDetail = "not evaluated";
        }

        private class Phase
        {
            public string label;
            // Establishes everything this phase depends on, so it inherits nothing from the
            // phases before it. Runs once, before mutate. Paired with precondition checks:
            // arrange makes it so, the preconditions prove it.
            public Action arrange;
            public Action mutate;
            public List<Check> checks = new List<Check>();
            public int deadlineTicks;
            // Phase cannot complete before this. The observation window a negative check has
            // to hold across, and the settle time for informational checks.
            public int minTicks;
            // Runs on every poll after the act. Without it a negative check's window is
            // passive — at the measured 0.79 reconciles per colonist per 1000 ticks, a
            // 600-tick window sees under one natural reconcile, so "holds for the window"
            // was mostly "nothing ran during the window".
            public Action poll;
            public bool failed;
            public bool invalid;   // a precondition never held; the phase proved nothing
            // mutate is deferred until every precondition holds. Firing it immediately after
            // arrange means firing it into a world that has not caught up — CE has not
            // hauled, the reconcile has not run, the memory is not seeded. In a sequenced run
            // that is invisible because earlier phases already settled things; alone it is
            // the difference between testing something and testing nothing.
            public bool mutated;
            public string diagnostic;  // an unexpected error or warning seen during it
        }

        /// <summary>
        /// Diagnostics we have accounted for and decided are not ours. Everything else — any
        /// Error from any mod, any Warning not listed here — fails the phase it appeared in.
        ///
        /// Errors from CE or Simple Sidearms count against us on purpose: this suite exists
        /// to prove the two work together, and breaking one of them is the most consequential
        /// thing this module can do. Each entry below has to be justified, not just observed;
        /// an allowlist built from "whatever showed up once" is how a real defect gets
        /// permanently excused.
        /// </summary>
        private static readonly string[] ExpectedDiagnostics =
        {
            // Simple Sidearms sweeps its own memory on load and says so. Not provoked by us:
            // it fires on a save this module has never touched.
            "had a null weapon memory, removing",
            "had a missing def or malformed data, removing",
            // The harness's own informational lines — NOT a blanket prefix: the
            // runner's "threw:" error reports must stay visible to this gate, or a dead
            // poll/mutate is excused by the very instrument meant to catch it.
            "[SupplyTest] Phase ",
            "[SupplyTest] poll for ",
            "[SupplyTest] Mutation for phase ",
            "[SupplyTest] Setup for phase ",
            "[SupplyTest] Isolated run",
            "[SupplyTest] Results written",
            "[SupplyTest] Scenario complete",
            "[SupplyTest] UseOutfitStand def absent",
            "[SupplyStaging]",
            // RimBridge logs startup telemetry at Warning level, and its startup straddles
            // the point where this scenario baselines the log. It is a development tool in
            // this profile, is not shipped with the mod, and nothing here can provoke it.
            // A pre-release profile trimmed to CE + SS + this mod would not need the entry.
            "[RimBridge] STARTUP_TIMING",
        };

        private readonly HashSet<string> seenDiagnostics = new HashSet<string>();

        /// <summary>
        /// Returns the first unaccounted-for error or warning since the last call.
        ///
        /// Log.Messages is a capped queue, so this reads the whole of it every poll and
        /// remembers what it has already reported rather than tracking an index that the
        /// queue can invalidate underneath it.
        /// </summary>
        /// <summary>
        /// Everything already in the log when the scenario starts is somebody else's: mod
        /// metadata complaints, startup telemetry, whatever the profile's other mods say on
        /// their way up. Only what the run provokes can be attributed to it.
        /// </summary>
        private void BaselineDiagnostics()
        {
            foreach (LogMessage msg in Log.Messages)
            {
                seenDiagnostics.Add(msg.text ?? "");
            }
            Log.Message($"[SupplyTest] Diagnostics baselined at {seenDiagnostics.Count} pre-existing message(s).");
        }

        private string NewDiagnostic()
        {
            foreach (LogMessage msg in Log.Messages)
            {
                if (msg.type != LogMessageType.Error && msg.type != LogMessageType.Warning)
                {
                    continue;
                }
                string text = msg.text ?? "";
                if (!seenDiagnostics.Add(text))
                {
                    continue;
                }
                if (ExpectedDiagnostics.Any(e => text.Contains(e)))
                {
                    continue;
                }
                return $"{msg.type}: {text.Split('\n')[0]}";
            }
            return null;
        }

        private List<Phase> phases;
        private int isolatedPhase = -1;
        private int totalPhaseCount;
        private int phaseIndex = -1;
        private int phaseStartTick;
        private string scenario;
        private bool active;
        private bool done;

        public TestRunnerComponent(Game game)
        {
        }

        public override void LoadedGame()
        {
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("supply") || scenario.StartsWith("supplybench"))
            {
                return;
            }
            // "supply1:7" runs phase 7 and nothing else, in its own process against a freshly
            // loaded save. The sequenced run proves the phases work against accumulated state;
            // this proves each one stands on its own, which arrange alone cannot demonstrate —
            // a phase can arrange everything it remembered to and still lean on something it
            // did not.
            int colon = scenario.IndexOf(':');
            if (colon > 0 && int.TryParse(scenario.Substring(colon + 1), out int only))
            {
                isolatedPhase = only;
                scenario = scenario.Substring(0, colon);
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    phases = BuildScenario(scenario);
                    // Every phase carries the state dump; forgetting to add it per-phase is
                    // exactly the kind of omission it exists to catch.
                    foreach (Phase ph in phases)
                    {
                        if (!ph.checks.Any(c => c.name == "state"))
                        {
                            ph.checks.Add(State(Colonist("Dockie"), () => Colonist("Dockie").GetLoadout()));
                        }
                    }
                    totalPhaseCount = phases.Count;
                    if (isolatedPhase >= 0)
                    {
                        phases = isolatedPhase < totalPhaseCount
                            ? new List<Phase> { phases[isolatedPhase] }
                            : new List<Phase>();
                        Log.Message($"[SupplyTest] Isolated run: phase {isolatedPhase} of {totalPhaseCount}"
                                    + (phases.Count == 0 ? " — out of range." : $" ('{phases[0].label}')."));
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[SupplyTest] Scenario build failed: " + e);
                    WriteResults(crashed: e.ToString());
                    Root.Shutdown();
                    return;
                }
                BaselineDiagnostics();
                active = true;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                Log.Message($"[SupplyTest] Scenario '{scenario}' started, {phases.Count} phases.");
                AdvancePhase();
            });
        }

        public override void GameComponentTick()
        {
            if (!active || done)
            {
                return;
            }
            if (Find.TickManager.Paused || Find.TickManager.CurTimeSpeed != TimeSpeed.Superfast)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            }
            int tick = Find.TickManager.TicksGame;
            if (tick % 30 != 0)
            {
                return;
            }

            if (phases.Count == 0)
            {
                Finish();
                return;
            }
            Phase phase = phases[phaseIndex];

            // Any unaccounted-for error or warning, from this mod or from CE or SS, fails the
            // phase it appeared in. Checked first: a phase that provoked a red error has not
            // passed, whatever its assertions say.
            string diagnostic = NewDiagnostic();
            if (diagnostic != null)
            {
                phase.failed = true;
                phase.diagnostic = diagnostic;
                Log.Warning($"[SupplyTest] Phase '{phase.label}' FAILED on an unexpected diagnostic: {diagnostic}");
                AdvancePhase();
                return;
            }

            // Poll BEFORE the checks, so the state the checks evaluate includes the last
            // action the phase drove. Polling after meant a phase could advance on state
            // observed before its own final act.
            if (phase.mutated)
            {
                try
                {
                    phase.poll?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error($"[SupplyTest] poll for '{phase.label}' threw: " + e);
                    // A phase whose driver is dead is not observing anything — failing it
                    // beats silently degrading its driven window to a passive one.
                    phase.failed = true;
                    AdvancePhase();
                    return;
                }
            }

            bool allPass = true;
            bool preconditionsHold = true;
            Check tripped = null;
            foreach (Check check in phase.checks)
            {
                // Nothing but a precondition may be evaluated before the act. The loop
                // evaluates checks and then fires mutate on the same poll, so without this an
                // outcome check runs against the freshly-arranged world — where it is often
                // trivially true — and latches there, recording the state before the thing it
                // exists to observe ever happened.
                if (!phase.mutated && !check.precondition)
                {
                    continue;
                }
                // Informational checks re-evaluate until the phase ends (their last
                // observation is what gets reported) and never gate advancement. Negative
                // checks re-evaluate because a must-not-happen that passes now can still
                // fail later — latching them is what makes them vacuous.
                if (check.passed && !check.informational && !check.negative)
                {
                    continue;
                }
                try
                {
                    (bool pass, string detail) = check.eval();
                    check.lastDetail = detail;
                    check.passed = pass || check.informational;
                    if (!pass && !check.informational)
                    {
                        allPass = false;
                        if (check.precondition)
                        {
                            preconditionsHold = false;
                        }
                        else if (check.negative)
                        {
                            tripped = check;
                        }
                    }
                }
                catch (Exception e)
                {
                    check.lastDetail = "EXCEPTION: " + e.Message;
                    if (!check.informational)
                    {
                        allPass = false;
                        if (check.precondition)
                        {
                            // A throwing precondition means the world was never ready —
                            // the phase must report VOID (tested nothing), not FAIL
                            // (blaming the product for a broken setup).
                            preconditionsHold = false;
                        }
                    }
                }
            }

            // Preconditions hold and the act has not happened yet: this is the moment the
            // world is ready for it.
            if (preconditionsHold && !phase.mutated)
            {
                phase.mutated = true;
                phaseStartTick = tick;   // the observation window starts from the act
                try
                {
                    phase.mutate?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error($"[SupplyTest] Mutation for phase '{phase.label}' threw: " + e);
                    phase.failed = true;
                    AdvancePhase();
                }
                return;
            }
            // Nothing the phase asserts means anything until its act has happened.
            if (!phase.mutated)
            {
                if (tick - phaseStartTick > phase.deadlineTicks)
                {
                    phase.invalid = true;
                    Log.Warning($"[SupplyTest] Phase '{phase.label}' INVALID — preconditions never held: "
                                + string.Join(", ", phase.checks.Where(c => c.precondition && !c.passed)
                                                         .Select(c => $"{c.name} ({c.lastDetail})")));
                    AdvancePhase();
                }
                return;
            }

            if (tripped != null && preconditionsHold)
            {
                phase.failed = true;
                Log.Warning($"[SupplyTest] Phase '{phase.label}' FAILED: '{tripped.name}' must not happen "
                            + $"but did at tick {tick} — {tripped.lastDetail}");
                AdvancePhase();
                return;
            }
            if (tick - phaseStartTick < phase.minTicks)
            {
                return;
            }
            if (allPass)
            {
                Log.Message($"[SupplyTest] Phase '{phase.label}' PASSED at tick {tick}.");
                AdvancePhase();
            }
            else if (tick - phaseStartTick > phase.deadlineTicks)
            {
                // A phase whose preconditions never held did not test what it claims to.
                // That is a broken test, not broken code, and conflating the two is how a
                // suite quietly stops meaning anything.
                phase.invalid = !preconditionsHold;
                phase.failed = !phase.invalid;
                string why = phase.invalid
                    ? "INVALID — preconditions never held: "
                      + string.Join(", ", phase.checks.Where(c => c.precondition && !c.passed)
                                               .Select(c => $"{c.name} ({c.lastDetail})"))
                    : $"FAILED (deadline {phase.deadlineTicks} ticks).";
                Log.Warning($"[SupplyTest] Phase '{phase.label}' {why}");
                AdvancePhase();
            }
        }

        private void AdvancePhase()
        {
            phaseIndex++;
            if (phaseIndex >= phases.Count)
            {
                Finish();
                return;
            }
            Phase phase = phases[phaseIndex];
            phaseStartTick = Find.TickManager.TicksGame;
            try
            {
                // Arrange only. mutate waits for the preconditions to hold — see Phase.mutated.
                phase.arrange?.Invoke();
                if (!phase.checks.Any(c => c.precondition))
                {
                    phase.mutate?.Invoke();
                    phase.mutated = true;
                }
            }
            catch (Exception e)
            {
                Log.Error($"[SupplyTest] Setup for phase '{phase.label}' threw: " + e);
                phase.failed = true;
                foreach (Check c in phase.checks)
                {
                    c.lastDetail = "mutation threw: " + e.Message;
                }
                AdvancePhase();
            }
        }

        private void Finish()
        {
            done = true;
            WriteResults();
            Log.Message("[SupplyTest] Scenario complete; shutting down.");
            Root.Shutdown();
        }

        private void WriteResults(string crashed = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"scenario\": \"{scenario}\",\n");
            sb.Append($"  \"phaseCount\": {totalPhaseCount},\n");
            if (isolatedPhase >= 0)
            {
                sb.Append($"  \"isolatedPhase\": {isolatedPhase},\n");
            }
            bool overall = crashed == null && phases != null && phases.All(p => !p.failed && !p.invalid);
            sb.Append($"  \"passed\": {(overall ? "true" : "false")},\n");
            if (crashed != null)
            {
                sb.Append($"  \"crashed\": \"{Escape(crashed)}\",\n");
            }
            sb.Append($"  \"ticks\": {(Find.TickManager?.TicksGame ?? 0)},\n");
            sb.Append("  \"phases\": [\n");
            if (phases != null)
            {
                for (int i = 0; i < phases.Count; i++)
                {
                    Phase p = phases[i];
                    sb.Append("    {\n");
                    sb.Append($"      \"label\": \"{Escape(p.label)}\",\n");
                    sb.Append($"      \"passed\": {((!p.failed && !p.invalid) ? "true" : "false")},\n");
                    sb.Append($"      \"invalid\": {(p.invalid ? "true" : "false")},\n");
                    if (p.diagnostic != null)
                    {
                        sb.Append($"      \"diagnostic\": \"{Escape(p.diagnostic)}\",\n");
                    }
                    sb.Append($"      \"reached\": {(i <= phaseIndex ? "true" : "false")},\n");
                    sb.Append("      \"checks\": [\n");
                    for (int j = 0; j < p.checks.Count; j++)
                    {
                        Check c = p.checks[j];
                        sb.Append("        {");
                        sb.Append($"\"name\": \"{Escape(c.name)}\", ");
                        sb.Append($"\"passed\": {(c.passed ? "true" : "false")}, ");
                        sb.Append($"\"informational\": {(c.informational ? "true" : "false")}, ");
                        sb.Append($"\"precondition\": {(c.precondition ? "true" : "false")}, ");
                        sb.Append($"\"detail\": \"{Escape(c.lastDetail)}\"");
                        sb.Append("}");
                        sb.Append(j < p.checks.Count - 1 ? ",\n" : "\n");
                    }
                    sb.Append("      ]\n");
                    sb.Append(i < phases.Count - 1 ? "    },\n" : "    }\n");
                }
            }
            sb.Append("  ]\n}\n");
            string suffix = isolatedPhase >= 0 ? $"-iso-{isolatedPhase:D2}" : "";
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, $"test-results-{scenario}{suffix}.json");
            File.WriteAllText(path, sb.ToString());
            Log.Message($"[SupplyTest] Results written to {path}");
        }

        private static string Escape(string s)
        {
            return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }

        // ---- scenario definitions -----------------------------------------

        private List<Phase> BuildScenario(string name)
        {
            switch (name)
            {
                case "supply1": return BuildSupply1();
                default: throw new InvalidOperationException("Unknown scenario: " + name);
            }
        }

        // -- shared helpers --

        private static Pawn Colonist(string nick)
        {
            Pawn pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned
                .FirstOrDefault(p => p.Name is NameTriple nt && nt.Nick == nick);
            if (pawn == null)
            {
                throw new InvalidOperationException("Colonist not found: " + nick);
            }
            return pawn;
        }

        private static ThingDef D(string defName) => DefDatabase<ThingDef>.GetNamed(defName);

        private static List<ThingDef> AmmoSetOf(ThingDef weapon)
        {
            return weapon.GetCompProperties<CompProperties_AmmoUser>()?.ammoSet?.ammoTypes?
                       .Select(l => l.ammo).Cast<ThingDef>().ToList()
                   ?? new List<ThingDef>();
        }

        
        
        private static List<ThingDef> CarriedWeaponDefs(Pawn pawn)
        {
            return pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true).Select(w => w.def).ToList();
        }

        private static CompSidearmMemory Mem(Pawn pawn) => CompSidearmMemory.GetMemoryCompForPawn(pawn);

        private static void ForceReconcile(Pawn pawn)
        {
            // The reconcile itself, nothing else. Going through CE's TryGiveJob would also
            // run CE's own loadout enforcement — which physically drops equipment and
            // inventory and rewrites CE's throttle — so a phase calling this twice was doing
            // two rounds of CE enforcement rather than two reconciles.
            CESidearmsSupply.Patches.JobGiver_UpdateLoadout_TryGiveJob_Patch.Reconcile(pawn);
        }

        private static List<LoadoutSlot> Stream(Pawn pawn)
        {
            return pawn.GetLoadout().GetSlotsFor(pawn).ToList();
        }

        
        private static Check C(string name, Func<(bool, string)> eval, bool informational = false)
        {
            return new Check { name = name, eval = eval, informational = informational };
        }

        /// <summary>
        /// The standing state dump every phase carries. Informational checks re-evaluate on
        /// every poll while positive ones latch — so this reports live state where the checks
        /// beside it report history, and a disagreement between the two is how a check that
        /// latched on the wrong world gets caught. That exact divergence is what exposed the
        /// pre-act latching defect; this makes the tripwire standing rather than a hunch.
        /// </summary>
        private static Check State(Pawn pawn, Func<Loadout> loadoutOf)
        {
            return new Check
            {
                name = "state",
                informational = true,
                eval = () =>
                {
                    CompSidearmMemory m = Mem(pawn);
                    var rec = CESidearmsSupply.CompLoadoutSidearms.For(pawn);
                    string mem = string.Join(",", m.RememberedWeapons.Select(pr => pr.thing?.defName));
                    string clm = rec == null ? "-" : string.Join(",", rec.claimed.Select(pr => pr.thing?.defName));
                    string exc = rec == null ? "-" : string.Join(",", rec.dontEquip.Select(pr => pr.thing?.defName));
                    string carried = string.Join(",", pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .Select(w => w.def.defName));
                    Loadout lo = loadoutOf();
                    string rows = lo == null ? "-" : string.Join(",", lo.Slots.Where(sl => sl.thingDef != null)
                        .Select(sl => sl.thingDef.defName));
                    return (true,
                        $"mem=[{mem}] claimed=[{clm}] dontEquip=[{exc}] carried=[{carried}] rows=[{rows}] "
                        + $"ranged={m.DefaultRangedWeapon?.thing?.defName ?? "-"} "
                        + $"melee={m.PreferredMeleeWeapon?.thing?.defName ?? "-"} "
                        + $"forced={m.ForcedWeapon?.thing?.defName ?? "-"} "
                        + $"primary={pawn.equipment?.Primary?.def?.defName ?? "-"} "
                        + $"vetoR={rec?.rangedRoleVetoed} vetoM={rec?.meleeRoleVetoed} "
                        + $"job={pawn.CurJobDef?.defName ?? "-"}");
                },
            };
        }

        /// <summary>A must-not-happen check. Held across the whole phase, not just sampled once.</summary>
        private static Check N(string name, Func<(bool, string)> eval)
        {
            return new Check { name = name, eval = eval, negative = true };
        }

        /// <summary>
        /// Puts the pawn into a known state so a phase inherits nothing from the ones before
        /// it: the loadout holds exactly these rows in this order, Simple Sidearms remembers
        /// exactly the carried weapons among them, and every piece of player intent — forced
        /// weapon, unarmed flags, role vetoes, exclusions — is cleared.
        ///
        /// This is what lets the same phase definition run inside the sequence and on its own
        /// in a fresh process. A phase that arranges its own world does not care which of
        /// those it is in.
        /// </summary>
        private static void Baseline(Pawn pawn, Loadout loadout, params ThingDef[] rows)
        {
            CompSidearmMemory memory = Mem(pawn);
            var rec = CESidearmsSupply.CompLoadoutSidearms.For(pawn);

            // Loadout-level flags are shared state a phase can mutate (one did, and every
            // later sequenced phase then ran adHoc-off while the isolated runs ran
            // adHoc-on — two measurably different worlds). Staged values, restored.
            loadout.adHoc = true;
            loadout.adHocMags = 2;
            // A drafted pawn runs no think tree: CE never hauls, SS never re-arms, and a
            // phase that failed mid-draft (one can) leaks a dead world to everything after.
            if (pawn.drafter != null && pawn.drafter.Drafted)
            {
                pawn.drafter.Drafted = false;
            }
            // Ground litter from earlier phases' drops: a spawned weapon on this test map
            // is never scenery, and CE will otherwise haul it mid-phase.
            foreach (Thing stray in pawn.Map.listerThings
                         .ThingsInGroup(ThingRequestGroup.Weapon).ToList())
            {
                if (stray.Spawned && !stray.Destroyed)
                {
                    stray.Destroy();
                }
            }

            // Player intent first: SS's role setters clear forced state as a side effect, so
            // clearing intent after seeding memory would undo part of the seeding.
            memory.UnsetForcedWeapon(drafted: false);
            memory.UnsetForcedWeapon(drafted: true);
            memory.ForcedUnarmed = false;
            memory.PreferredUnarmed = false;
            memory.UnsetRangedWeaponDefault();
            memory.UnsetMeleeWeaponPreference();
            if (rec != null)
            {
                rec.dontEquip.Clear();
                rec.claimed.Clear();
                rec.rangedRoleVetoed = false;
                rec.meleeRoleVetoed = false;
            }

            // Memory: drop everything, then re-seed from what the pawn actually carries among
            // the declared rows. Going through ForgetSidearmMemory rather than clearing the
            // list keeps SS's own bookkeeping consistent.
            foreach (var pair in memory.RememberedWeapons.ToList())
            {
                memory.ForgetSidearmMemory(pair);
            }

            // Assignment before contents. A phase ordered after one that deletes the loadout
            // finds the pawn on the default one and this object detached from the manager, so
            // rebuilding its rows would arrange a loadout nobody is assigned to and every
            // reconcile would early-return. Establishing a world means establishing that too.
            if (!LoadoutManager.Loadouts.Contains(loadout))
            {
                LoadoutManager.AddLoadout(loadout);
            }
            if (pawn.GetLoadout() != loadout)
            {
                pawn.SetLoadout(loadout);
            }

            loadout._slots.Clear();
            foreach (ThingDef def in rows)
            {
                loadout.AddSlot(new LoadoutSlot(def, 1));
            }

            // Possession. The staged save spawns the kit on the MAP — phase one exists to
            // watch CE fetch it, and every sequenced phase after inherits the result. A phase
            // running alone inherits nothing, and without the weapons in hand the reconcile
            // claims an empty set, a forget has nothing to forget, and SS's own
            // equip-memorise clears a forced weapon mid-window while CE fetches. Establishing
            // a world includes establishing what the pawn is holding.
            foreach (ThingDef def in rows)
            {
                if (!pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true).Any(w => w.def == def))
                {
                    var made = (ThingWithComps)ThingMaker.MakeThing(def,
                        def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    pawn.inventory.innerContainer.TryAdd(made, canMergeWithExistingStacks: true);
                }
            }

            // Reconcile the inventory to the spec in BOTH directions: the sequenced run
            // accumulates weapons earlier phases created (a revolver, an SMG), and leaving
            // them made the two suites run measurably different worlds. Undeclared primary
            // included.
            foreach (ThingWithComps extra in pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                         .Where(w => !rows.Contains(w.def)).ToList())
            {
                if (pawn.equipment?.Primary == extra)
                {
                    pawn.equipment.Remove(extra);
                }
                else
                {
                    pawn.inventory.innerContainer.Remove(extra);
                }
                extra.Destroy();
            }
            // The direct container writes above and in the possession block bypass CE's
            // bookkeeping; its cached bulk/weight and weapon lists feed everything the
            // phases observe.
            pawn.TryGetComp<CombatExtended.CompInventory>()?.UpdateInventory();

            // The vetoes above were written through the same methods the intent hooks watch,
            // so clear the record once more after the fact.
            if (rec != null)
            {
                rec.dontEquip.Clear();
                rec.rangedRoleVetoed = false;
                rec.meleeRoleVetoed = false;
            }
            ForceReconcile(pawn);
        }

        /// <summary>
        /// Something that must be true for the phase to mean anything. If it never holds the
        /// phase reports INVALID — it did not test what it claims to, which is a different
        /// failure from the code being wrong and should not be reported as either a pass or
        /// a bug.
        /// </summary>
        private static Check P(string name, Func<(bool, string)> eval)
        {
            return new Check { name = name, eval = eval, precondition = true };
        }

        // The player's levers. The module's intent hooks only fire inside a gizmo
        // interaction, so these enter that scope the way Gizmo_SidearmsList does and then
        // make the call SS's own branch would make. The hooks under test are the real ones.
        private static void InGizmo(Action act)
        {
            CESidearmsSupply.Patches.PlayerIntent.Enter();
            try { act(); } finally { CESidearmsSupply.Patches.PlayerIntent.Exit(); }
        }

        private static void PlayerForgets(Pawn pawn, ThingDef def)
        {
            InGizmo(() =>
            {
                foreach (var pair in Mem(pawn).RememberedWeapons.Where(p => p.thing == def).ToList())
                {
                    Mem(pawn).ForgetSidearmMemory(pair);
                }
            });
            ForceReconcile(pawn);
            ForceReconcile(pawn); // a second pass is where the old code re-claimed it
        }

        private static void PlayerRemembers(Pawn pawn, ThingWithComps weapon)
        {
            InGizmo(() => Mem(pawn).InformOfAddedSidearm(weapon));
        }

        private static bool InChoice(Func<bool> probe)
        {
            CESidearmsSupply.Patches.PlayerIntent.EnterChoice();
            try { return probe(); } finally { CESidearmsSupply.Patches.PlayerIntent.ExitChoice(); }
        }

        private static void PlayerClearsRangedRole(Pawn pawn)
        {
            InGizmo(() => Mem(pawn).UnsetRangedWeaponDefault());
        }

        // -- SUPPLY-1: loadout weapons as sidearms + ammo sustainment --

        private List<Phase> BuildSupply1()
        {
            Pawn dockie = Colonist("Dockie");
            ThingDef sniper = D("Gun_SniperRifle");
            ThingDef shotgun = D("Gun_PumpShotgun");
            ThingDef pistol = D("Gun_Autopistol");
            ThingDef gladius = D("MeleeWeapon_Gladius");
            ThingDef revolver = D("Gun_Revolver");
            Loadout loadout = dockie.GetLoadout();

            LoadoutSlot SlotOf(ThingDef def) => loadout._slots.FirstOrDefault(s => s.thingDef == def);
            void MoveTop(ThingDef def)
            {
                LoadoutSlot slot = SlotOf(def);
                loadout._slots.Remove(slot);
                loadout._slots.Insert(0, slot);
            }

            var beforeDelete = new HashSet<string>();
            bool pistolWasReleased = false;
            bool pistolWasExcluded = false;
            ThingWithComps droppedPistol = null;
            int illegalClaimCount = -1;
            bool tabSwitchEquipped = false;
            bool tabSwitchRole = false;
            bool sniperWasPrimaryAtEquip = false;
            bool shotgunWasDropped = false;
            bool gizmoForgetShotgunDropped = false;
            bool machineEquipLanded = false;
            bool sniperWasExcludedAtDrop = false;
            ThingDefStuffDefPair? meleeRoleAtSettle = null;
            bool featureOffHadClaims = false;
            bool forceWithdrewExclusion = false;
            bool tabSwitchVetoLifted = false;
            bool failedSwitchKeptExclusion = false;
            bool failedSwitchNotRemembered = false;
            bool releaseLeftNoClaims = false;
            bool claimsReturnedAfterRestore = false;
            bool orderedSwapSkippedExcluded = false;
            bool orderedSwapPickedRunnerUp = false;
            bool orderedSwapJobWasForced = false;
            ThingDef orderedSwapFavourite = null;
            string orderedSwapPickerSaw = "unsampled";
            bool loadoutSwitchClearedAll = false;
            bool loadoutSwitchStayedClear = false;
            bool standEquipWithdrew = false;
            bool forcedPairSurvivedRelease = false;
            bool releaseTookTheRest = false;
            bool gestureAfterAssignSurvived = false;
            bool reusedIdKeptRules = true;
            bool featureOffSweptThisColony = false;
            bool featureOffArmedTheFlag = false;

            var phases = new List<Phase>();

            phases.Add(new Phase
            {
                label = "initial-reconcile-and-fetch",
                deadlineTicks = 40000,
                mutate = () =>
                {
                    // Top up needs: a tired/hungry colonist sleeps or eats instead of
                    // hauling, and CE's loadout fetch is low-priority enough that an
                    // unlucky spawn can burn the whole deadline resting (seen as an
                    // ammo-never-fetched flake). Not part of what's under test.
                    if (dockie.needs?.rest != null)
                    {
                        dockie.needs.rest.CurLevel = dockie.needs.rest.MaxLevel;
                    }
                    if (dockie.needs?.food != null)
                    {
                        dockie.needs.food.CurLevel = dockie.needs.food.MaxLevel;
                    }
                    if (dockie.needs?.joy != null)
                    {
                        dockie.needs.joy.CurLevel = dockie.needs.joy.MaxLevel;
                    }
                },
                checks =
                {
                    C("remembered-all-four", () =>
                    {
                        var defs = Mem(dockie).RememberedWeapons.Select(p => p.thing).ToList();
                        bool ok = defs.Contains(sniper) && defs.Contains(shotgun) && defs.Contains(pistol) && defs.Contains(gladius);
                        return (ok, "remembered: " + string.Join(",", defs.Select(d => d.defName)));
                    }),
                    C("default-ranged-sniper", () =>
                    {
                        ThingDef def = Mem(dockie).DefaultRangedWeapon?.thing;
                        return (def == sniper, "defaultRanged=" + (def?.defName ?? "null"));
                    }),
                    C("preferred-melee-gladius", () =>
                    {
                        ThingDef def = Mem(dockie).PreferredMeleeWeapon?.thing;
                        return (def == gladius, "preferredMelee=" + (def?.defName ?? "null"));
                    }),
                    C("weapons-acquired", () =>
                    {
                        var carried = CarriedWeaponDefs(dockie);
                        bool ok = carried.Contains(sniper) && carried.Contains(shotgun) && carried.Contains(pistol) && carried.Contains(gladius);
                        return (ok, "carried: " + string.Join(",", carried.Select(d => d.defName)));
                    }),
                    C("gladius-pair-stuff-matches-carried", () =>
                    {
                        var carriedGladius = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .FirstOrDefault(w => w.def == gladius);
                        if (carriedGladius == null) return (false, "gladius not carried yet");
                        var pair = Mem(dockie).RememberedWeapons.Where(p => p.thing == gladius).Cast<ThingDefStuffDefPair?>().FirstOrDefault();
                        if (pair == null) return (false, "gladius not remembered");
                        return (pair.Value.stuff == carriedGladius.Stuff,
                            $"pair.stuff={pair.Value.stuff?.defName ?? "null"} carried.stuff={carriedGladius.Stuff?.defName ?? "null"}");
                    }),
                    C("fetch-forensics", () =>
                    {
                        CompInventory inv = dockie.TryGetComp<CompInventory>();
                        var stream = Stream(dockie);
                        string slots = string.Join(",", stream.Select(s =>
                            (s.thingDef?.defName ?? s.genericDef?.defName ?? "?") + "x" + s.count));
                        var ammoOnMap = dockie.Map.listerThings.AllThings
                            .Where(t => t.def is AmmoDef && t.Spawned).Take(3)
                            .Select(t => $"{t.def.defName}x{t.stackCount}@{t.Position}");
                        return (true,
                            $"bulk={inv?.currentBulk:F1}/{inv?.capacityBulk:F1} weight={inv?.currentWeight:F1}/{inv?.capacityWeight:F1} " +
                            $"job={dockie.CurJobDef?.defName} slots=[{slots}] mapAmmo=[{string.Join(" ", ammoOnMap)}]");
                    }, informational: true),
                }
            });

            phases.Add(new Phase
            {
                label = "reorder-shotgun-top",
                deadlineTicks = 6000,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    // restore the kit the fetch phase stripped, then reorder
                    foreach (ThingDef def in new[] { sniper, shotgun, gladius })
                    {
                        if (SlotOf(def) == null)
                        {
                            loadout.AddSlot(new LoadoutSlot(def, 1));
                        }
                    }
                    ForceReconcile(dockie);
                    MoveTop(shotgun);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("default-ranged-flips-to-shotgun", () =>
                    {
                        ThingDef def = Mem(dockie).DefaultRangedWeapon?.thing;
                        return (def == shotgun, "defaultRanged=" + (def?.defName ?? "null"));
                    }),
                }
            });

            phases.Add(new Phase
            {
                // The loadout decides among the weapons it declares. Setting the role by hand
                // to another DECLARED weapon does not stick — reorder the loadout instead.
                label = "declared-role-override-yields-to-loadout",
                deadlineTicks = 6000,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    MoveTop(sniper);
                    Mem(dockie).SetRangedWeaponTypeAsDefault(new ThingDefStuffDefPair(pistol, null));
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("loadout-first-wins", () =>
                    {
                        ThingDef def = Mem(dockie).DefaultRangedWeapon?.thing;
                        return (def == sniper, "defaultRanged=" + (def?.defName ?? "null"));
                    }),
                }
            });

            phases.Add(new Phase
            {
                // Forcing IS the lever, and SS checks it before any default. The projection
                // must not touch a forced weapon: SetRangedWeaponTypeAsDefault would clear it.
                label = "forced-weapon-is-never-touched",
                deadlineTicks = 6000,
                // "Never" is a claim about a window, not a sample. A positive check latches on
                // its first pass, so C + minTicks would sample once and idle; only a negative
                // re-evaluates every poll for the whole window.
                minTicks = 600,
                poll = () => ForceReconcile(dockie),
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    Mem(dockie).SetWeaponAsForced(new ThingDefStuffDefPair(pistol, null), false);
                    MoveTop(shotgun);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    N("force-holds-for-the-whole-window", () =>
                    {
                        ThingDef forced = Mem(dockie).ForcedWeapon?.thing;
                        return (forced == pistol, "forced=" + (forced?.defName ?? "null"));
                    }),
                }
            });


            phases.Add(new Phase
            {
                label = "template-forget",
                deadlineTicks = 6000,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () => { loadout.RemoveSlot(SlotOf(shotgun)); ForceReconcile(dockie); },
                checks =
                {
                    P("shotgun-was-remembered-first", () =>
                    {
                        // Without this, "forgotten" passes just as well when Target() claims
                        // nothing at all and there was never a memory to forget.
                        bool present = Mem(dockie).RememberedWeapons.Any(p => p.thing == shotgun);
                        return (present, "shotgun remembered=" + present);
                    }),
                    C("shotgun-forgotten", () =>
                    {
                        bool present = Mem(dockie).RememberedWeapons.Any(p => p.thing == shotgun);
                        return (!present, "shotgun remembered=" + present);
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "manual-memory-protected",
                deadlineTicks = 6000,
                minTicks = 600,
                poll = () => ForceReconcile(dockie),
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    // Re-remember shotgun MANUALLY (not template-tracked), then churn the
                    // template (remove + re-add pistol) — manual memory must survive.
                    Mem(dockie).RememberedWeapons.Add(new ThingDefStuffDefPair(shotgun, null));
                    LoadoutSlot pistolSlot = SlotOf(pistol);
                    loadout.RemoveSlot(pistolSlot);
                    ForceReconcile(dockie);
                    // Sampled here, between the removal and the re-add: the end state alone
                    // cannot tell a correct round trip from the projection doing nothing.
                    pistolWasReleased = !Mem(dockie).RememberedWeapons.Any(pr => pr.thing == pistol);
                    loadout.AddSlot(new LoadoutSlot(pistol, 1));
                    ForceReconcile(dockie);
                },
                checks =
                {
                    N("manual-shotgun-survives-template-churn", () =>
                    {
                        // Held across the window, and by COUNT: the claimed copy plus the
                        // manual one. An .Any() here passed just as well if the projection
                        // drained duplicates to exhaustion and took the player's copy with
                        // its own.
                        int n = Mem(dockie).RememberedWeapons.Count(p => p.thing == shotgun);
                        return (n == 2, $"shotgun memories={n} (want 2: claimed + manual)");
                    }),
                    C("pistol-was-actually-released-first", () =>
                    {
                        // Reading the end state cannot tell "correctly re-claimed" from
                        // "never released". The mutate records whether the memory was gone
                        // between the removal and the re-add; without that this check passes
                        // even if ForgetUndeclared does nothing at all.
                        return (pistolWasReleased, $"released between remove and re-add={pistolWasReleased}");
                    }),
                    C("pistol-re-remembered", () =>
                    {
                        bool present = Mem(dockie).RememberedWeapons.Any(p => p.thing == pistol);
                        return (present, "pistol remembered=" + present);
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "preexisting-memory-claimed-by-loadout",
                deadlineTicks = 8000,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    // The common real-world ordering: the pawn ALREADY carries and
                    // remembers a gun (SS auto-remembers anything equipped as primary),
                    // and THEN the player builds the loadout around it. The projection
                    // must still claim it, so removing it from the loadout forgets it
                    // and CE is free to drop it.
                    var revolverThing = (ThingWithComps)ThingMaker.MakeThing(revolver);
                    dockie.inventory.innerContainer.TryAdd(revolverThing, true);
                    Mem(dockie).InformOfAddedSidearm(revolverThing);
                    loadout.AddSlot(new LoadoutSlot(revolver, 1));
                    ForceReconcile(dockie);
                    loadout.RemoveSlot(SlotOf(revolver));
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("preexisting-memory-forgotten-on-removal", () =>
                    {
                        bool present = Mem(dockie).RememberedWeapons.Any(p => p.thing == revolver);
                        return (!present, $"revolver remembered={present} (loadout owns what it lists)");
                    }),
                    C("drop-exemption-lifted", () =>
                    {
                        // The compat patch exempts a weapon from CE's drop for exactly as
                        // long as SS remembers it, so releasing the claim is the whole of
                        // this module's contribution. Whether CE then drops it depends on
                        // hold records, dropUndefined and what else is excess — CE's call,
                        // not ours to assert.
                        //
                        // This used to also accept "the pawn no longer carries it", which
                        // passed because ForceReconcile ran CE's entire TryGiveJob and that
                        // physically drops inventory. Calling the reconcile directly removed
                        // the side effect and left the check resting on an escape hatch.
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool remembered = Mem(dockie).RememberedWeapons.Any(p => p.thing == revolver);
                        bool claimed = rec != null && rec.claimed.Any(p => p.thing == revolver);
                        return (!remembered && !claimed,
                                $"remembered={remembered} claimed={claimed}");
                    }),
                    C("ce-view-of-excess", () =>
                    {
                        bool excess = Utility_HoldTracker.GetExcessThing(dockie, out Thing dropThing, out int _);
                        bool stillCarried = CarriedWeaponDefs(dockie).Contains(revolver);
                        return (true, $"excess={excess} dropThing={dropThing?.def?.defName ?? "none"} "
                                      + $"revolverStillCarried={stillCarried}");
                    }, informational: true),
                }
            });

            // The role is the head of [player's own choice] ++ [loadout order], filtered to
            // what the pawn actually carries. A weapon the player equips outranks the loadout
            // while they hold it; put it away and the loadout's first takes over; pick it back
            // up and it returns. Nothing is surrendered permanently.
            ThingWithComps playerPick = null;
            phases.Add(new Phase
            {
                label = "player-pick-heads-the-list",
                deadlineTicks = 6000,
                minTicks = 600,
                poll = () => ForceReconcile(dockie),
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    Mem(dockie).RememberedWeapons.RemoveAll(p => p.thing == revolver);
                    // An undeclared weapon, equipped: exactly what SS does on a battlefield pickup.
                    playerPick = (ThingWithComps)ThingMaker.MakeThing(D("Gun_HeavySMG"));
                    dockie.inventory.innerContainer.TryAdd(playerPick, true);
                    Mem(dockie).InformOfAddedPrimary(playerPick);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    N("carried-player-pick-outranks-loadout", () =>
                    {
                        // Negative over a driven window, not a first-pass latch: the old
                        // form latched after one reconcile, so a clobber on any LATER pass
                        // was invisible — and InformOfAddedPrimary had already set the role,
                        // so the reconcile only had to not break it once.
                        ThingDef def = Mem(dockie).DefaultRangedWeapon?.thing;
                        return (def == playerPick.def, $"default={def?.defName ?? "none"} want={playerPick.def.defName}");
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "loadout-takes-over-when-pick-is-gone",
                deadlineTicks = 6000,
                // Owns its context: the undeclared pick used to be a local left behind by the
                // previous phase, which is a NullReferenceException the moment this phase runs
                // alone. Same battlefield-pickup modelling as that phase, re-established here.
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    // Reuse a carried SMG before manufacturing one: in sequence the previous
                    // phase's pick is still in the inventory, and a second copy means the
                    // mutate removes ours while theirs keeps the role — correctly. Arrange
                    // reconciles the world to its spec; it does not add to whatever is there.
                    playerPick = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .FirstOrDefault(w => w.def == D("Gun_HeavySMG"));
                    if (playerPick == null)
                    {
                        playerPick = (ThingWithComps)ThingMaker.MakeThing(D("Gun_HeavySMG"));
                        dockie.inventory.innerContainer.TryAdd(playerPick, true);
                    }
                    Mem(dockie).InformOfAddedPrimary(playerPick);
                    ForceReconcile(dockie);
                },
                mutate = () =>
                {
                    dockie.inventory.innerContainer.Remove(playerPick);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("head-falls-through-to-loadout-first", () =>
                    {
                        ThingDef def = Mem(dockie).DefaultRangedWeapon?.thing;
                        return (def == sniper, $"default={def?.defName ?? "none"} want={sniper.defName}");
                    }),
                    C("claimed-is-exactly-declared-and-carried", () =>
                    {
                        // The old form asserted rec.claimed held no HeavySMG — which no code
                        // path can produce, so it passed with the feature deleted. The real
                        // invariant is that claimed matches what the loadout declares and the
                        // pawn holds, nothing more and nothing less.
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        var carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Select(w => w.def).ToHashSet();
                        var declaredNow = loadout.Slots.Where(sl => sl.thingDef != null && sl.thingDef.IsWeapon)
                            .Select(sl => sl.thingDef).ToHashSet();
                        var want = declaredNow.Where(d => carried.Contains(d)).ToHashSet();
                        var got = rec == null ? new HashSet<ThingDef>() : rec.claimed.Select(p => p.thing).ToHashSet();
                        bool ok = want.SetEquals(got);
                        return (ok, ok ? $"claimed == declared-and-carried ({got.Count})"
                                       : $"want=[{string.Join(",", want.Select(d => d.defName))}] "
                                         + $"got=[{string.Join(",", got.Select(d => d.defName))}]");
                    }),
                    C("undeclared-pick-never-claimed", () =>
                    {
                        // Deliberately not remembered: the player expressed the choice by
                        // EQUIPPING. Simple Sidearms' retrieval brings a weapon back to the
                        // inventory, not to their hands, so restoring it as the role would be
                        // inferring intent from an automatic action. Equip it again to lead.
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool claimed = playerPick != null && rec != null
                                       && rec.claimed.Any(p => p.thing == playerPick.def);
                        return (playerPick != null && !claimed,
                                playerPick == null ? "no pick arranged" : $"undeclared pick claimed={claimed}");
                    }),
                }
            });

            // "Carry it, but do not wield it": forgetting a DECLARED weapon in SS's gizmo is
            // the only way to say that, and the projection used to re-claim it every pass.
            //
            // Driven through the gizmo observer, not by calling ForgetSidearmMemory on its
            // own. That distinction is the whole fix: SS's equip interception calls the very
            // same method on every weapon swap, so a test that fakes the player that way
            // proves nothing about whether the player was understood.
            phases.Add(new Phase
            {
                label = "gizmo-forget-of-declared-weapon-sticks",
                deadlineTicks = 6000,
                minTicks = 600,
                poll = () => ForceReconcile(dockie),
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () => PlayerForgets(dockie, pistol),
                checks =
                {
                    P("pistol-is-remembered-first", () =>
                    {
                        // Forgetting something SS does not remember calls nothing, records
                        // nothing, and the phase then blames the code for it.
                        bool remembered = Mem(dockie).RememberedWeapons.Any(pr => pr.thing == pistol);
                        return (remembered, $"pistol remembered={remembered}");
                    }),

                    N("never-re-claimed", () =>
                    {
                        // The windowed negative is over what this module controls. "Never
                        // remembered again by anyone" is not assertable — the row is still
                        // declared, so CE may equip the pistol as primary and SS re-remembers
                        // it through InformOfAddedPrimary with nothing of ours involved; that
                        // exact over-assertion made the sister phase flake on CE's timing.
                        var recNow = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool claimed = recNow != null && recNow.claimed.Any(p => p.thing == pistol);
                        return (!claimed, $"pistol re-claimed by the projection={claimed}");
                    }),
                    C("recorded-as-player-intent", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(p => p.thing == pistol);
                        return (excluded, $"recorded as do-not-equip={excluded}");
                    }),
                    N("still-carried-because-the-row-stands", () =>
                    {
                        // The consequence the player sees, held across the window: the
                        // exclusion must not get the pistol dropped — its row is declared,
                        // so CE keeps it in the inventory with or without an SS memory.
                        // (The old form also asserted the row itself existed, which this
                        // module has no code to affect — that half was unfailable.)
                        bool carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Any(w => w.def == pistol);
                        return (carried, $"carried={carried}");
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "re-remembering-resumes-management",
                deadlineTicks = 6000,
                // The withdrawal under test needs an exclusion to withdraw. The old arrange
                // wiped it (Baseline clears the record), so the hook being tested could be
                // deleted with the phase staying green.
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    PlayerForgets(dockie, pistol);
                },
                mutate = () =>
                {
                    ThingWithComps carriedPistol = dockie.inventory.innerContainer.OfType<ThingWithComps>()
                        .FirstOrDefault(t => t.def == pistol);
                    if (carriedPistol == null)
                    {
                        carriedPistol = (ThingWithComps)ThingMaker.MakeThing(pistol);
                        dockie.inventory.innerContainer.TryAdd(carriedPistol, true);
                    }
                    PlayerRemembers(dockie, carriedPistol);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    P("pistol-starts-excluded", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(p => p.thing == pistol);
                        return (excluded, $"excluded={excluded}");
                    }),
                    C("suppression-cleared", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(p => p.thing == pistol);
                        bool claimed = rec != null && rec.claimed.Any(p => p.thing == pistol);
                        return (!excluded && claimed, $"dontEquip={excluded} claimed={claimed}");
                    }),
                }
            });


            // #5. The defect that made the old design unshippable: Simple Sidearms forgets the
            // outgoing primary on EVERY equip (JustBeforeEquip, on vanilla JobDriver_Equip),
            // and CE catches the displaced weapon into the inventory rather than dropping it.
            // Inferring "the player forgot this" from the resulting gap suppressed a declared
            // weapon permanently, on an ordinary right-click, with no way back.
            phases.Add(new Phase
            {
                label = "equipping-something-else-does-not-suppress-a-declared-weapon",
                deadlineTicks = 6000,
                minTicks = 300,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    // Pinning the primary is part of the ACT, done and sampled here: done in
                    // arrange it raced SS's idle switching, which legitimately re-arms the
                    // pawn within a few ticks and VOIDed the phase in isolation.
                    ThingWithComps sn = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .FirstOrDefault(w => w.def == sniper);
                    if (sn != null && dockie.equipment?.Primary != sn)
                    {
                        if (dockie.equipment.Primary != null)
                        {
                            ThingWithComps prev = dockie.equipment.Primary;
                            dockie.equipment.Remove(prev);
                            dockie.inventory.innerContainer.TryAdd(prev, true);
                        }
                        dockie.inventory.innerContainer.Remove(sn);
                        dockie.equipment.AddEquipment(sn);
                    }
                    sniperWasPrimaryAtEquip = dockie.equipment?.Primary?.def == sniper;
                    ThingWithComps outgoing = dockie.equipment?.Primary;
                    var other = (ThingWithComps)ThingMaker.MakeThing(revolver);
                    GenPlace.TryPlaceThing(other, dockie.Position, dockie.Map, ThingPlaceMode.Near);
                    // What SS's transpiler runs just before the equip toil completes.
                    PeteTimesSix.SimpleSidearms.Intercepts.JobDriver_Equip_MakeNewToils_Patches
                        .JustBeforeEquip(dockie, other);
                    if (outgoing != null && dockie.equipment.Primary == outgoing)
                    {
                        dockie.equipment.Remove(outgoing);
                        dockie.inventory.innerContainer.TryAdd(outgoing, true);
                    }
                    ForceReconcile(dockie);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("sniper-was-primary-at-the-equip", () =>
                    {
                        return (sniperWasPrimaryAtEquip, $"pinned={sniperWasPrimaryAtEquip}");
                    }),
                    N("nothing-recorded-as-player-intent", () =>
                    {
                        // Empty, not merely sniper-free: the defect fires on whatever the
                        // outgoing primary was, and asserting one def let it hide.
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        int n = rec?.dontEquip.Count ?? 0;
                        return (n == 0, $"dontEquip has {n} entr(y/ies)");
                    }),
                    C("sniper-reclaimed", () =>
                    {
                        bool remembered = Mem(dockie).RememberedWeapons.Any(p => p.thing == sniper);
                        return (remembered, $"sniper remembered again={remembered}");
                    }),
                }
            });

            // #7. SS clears ForcedWeapon as a side effect of forgetting its last copy, so the
            // guard has to hold across the forget phase, not just in role assertion.
            phases.Add(new Phase
            {
                label = "forced-weapon-survives-its-row-leaving-the-loadout",
                deadlineTicks = 6000,
                minTicks = 300,
                poll = () => ForceReconcile(dockie),
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    var pair = Mem(dockie).RememberedWeapons.FirstOrDefault(p => p.thing == gladius);
                    if (pair.thing != null)
                    {
                        Mem(dockie).SetWeaponAsForced(pair, drafted: false);
                    }
                    LoadoutSlot slot = SlotOf(gladius);
                    if (slot != null)
                    {
                        loadout.RemoveSlot(slot);
                    }
                    ForceReconcile(dockie);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    P("gladius-is-remembered-first", () =>
                    {
                        // The mutate forces the gladius only if it can find a pair. No pair,
                        // no force, and force-never-cleared then reports forced=none as
                        // though this module had cleared it.
                        bool remembered = Mem(dockie).RememberedWeapons.Any(pr => pr.thing == gladius);
                        return (remembered, $"gladius remembered={remembered}");
                    }),

                    N("force-never-cleared", () =>
                    {
                        var forced = Mem(dockie).ForcedWeapon;
                        return (forced.HasValue && forced.Value.thing == gladius,
                                $"forced={forced?.thing?.defName ?? "none"}");
                    }),
                }
            });

            // #12. Guessing a material for a weapon the pawn has not got sends SS chasing a
            // stuff the loadout never named — it matches pairs exactly, CE matches defs.
            phases.Add(new Phase
            {
                label = "declared-but-uncarried-weapon-is-not-remembered-with-a-guessed-stuff",
                deadlineTicks = 6000,
                minTicks = 600,
                poll = () => ForceReconcile(dockie),
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    ThingDef knife = D("MeleeWeapon_Knife");
                    foreach (var t in dockie.inventory.innerContainer.OfType<ThingWithComps>()
                                          .Where(t => t.def == knife).ToList())
                    {
                        dockie.inventory.innerContainer.Remove(t);
                    }
                    loadout.AddSlot(new LoadoutSlot(knife, 1));
                    ForceReconcile(dockie);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    N("no-invented-knife-memory", () =>
                    {
                        ThingDef knife = D("MeleeWeapon_Knife");
                        bool carried = CarriedWeaponDefs(dockie).Contains(knife);
                        bool remembered = Mem(dockie).RememberedWeapons.Any(p => p.thing == knife);
                        return (carried || !remembered,
                                $"knife carried={carried} remembered={remembered}");
                    }),
                }
            });

            // #8. SS's gizmo cascade unsets a role on the first click and only forgets on the
            // second, so restoring a cleared role made the first click look broken.
            phases.Add(new Phase
            {
                label = "a-hand-cleared-ranged-role-is-not-restored",
                deadlineTicks = 6000,
                minTicks = 600,
                poll = () => ForceReconcile(dockie),
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    PlayerClearsRangedRole(dockie);
                    ForceReconcile(dockie);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    P("sniper-is-the-default-first", () =>
                    {
                        // Clearing a role that was never set proves nothing about whether the
                        // projection restores a cleared one.
                        var def = Mem(dockie).DefaultRangedWeapon;
                        return (def.HasValue && def.Value.thing == sniper,
                                $"defaultRanged={def?.thing?.defName ?? "none"}");
                    }),

                    N("ranged-default-stays-cleared", () =>
                    {
                        var def = Mem(dockie).DefaultRangedWeapon;
                        return (!def.HasValue, $"default={def?.thing?.defName ?? "none"}");
                    }),
                }
            });

            // The regression for the defect that made the previous design inert: right-clicking
            // a CARRIED weapon in the sidearms gizmo does not reach ForgetSidearmMemory
            // directly — SS routes it through DropSidearm -> InformOfDroppedSidearm, the same
            // call it uses for machine-driven drops. The old design classified by that call
            // path and threw the player's decision away. This drives the real branch.
            phases.Add(new Phase
            {
                label = "forgetting-a-carried-weapon-in-the-gizmo-sticks",
                deadlineTicks = 12000,
                minTicks = 900,
                poll = () =>
                {
                    // Retry a quietly-failed drop, confirming on the retry's OWN result —
                    // a poll-boundary re-check races the re-haul (a drop can land and come
                    // back inside one gap). The mutate already sampled the gesture's drop
                    // at the act.
                    if (!gizmoForgetShotgunDropped)
                    {
                        ThingWithComps t2 = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .FirstOrDefault(w => w.def == shotgun);
                        if (t2 == null)
                        {
                            gizmoForgetShotgunDropped = true;
                        }
                        else if (dockie.equipment?.Primary == t2)
                        {
                            if (dockie.equipment.TryDropEquipment(t2, out _, dockie.Position, forbid: false))
                            {
                                gizmoForgetShotgunDropped = true;
                            }
                        }
                        else if (dockie.inventory?.innerContainer?.TryDrop(t2, dockie.Position, dockie.Map,
                                     ThingPlaceMode.Near, out _) ?? false)
                        {
                            gizmoForgetShotgunDropped = true;
                        }
                    }
                    ForceReconcile(dockie);
                },
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    ThingWithComps carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                                                   .FirstOrDefault(w => w.def == shotgun);
                    if (carried == null)
                    {
                        carried = (ThingWithComps)ThingMaker.MakeThing(shotgun);
                        dockie.inventory.innerContainer.TryAdd(carried, true);
                        Mem(dockie).InformOfAddedSidearm(carried);
                        ForceReconcile(dockie);
                    }
                    if (!loadout.Slots.Any(sl => sl.thingDef == shotgun))
                    {
                        loadout.AddSlot(new LoadoutSlot(shotgun, 1));
                    }
                    ForceReconcile(dockie);
                    // Sampled AT the act: "on the ground" is momentary by design — the
                    // re-haul can land inside one poll gap (it did, isolated), so a
                    // poll-boundary confirm races the exact behaviour this phase proves.
                    bool wasCarriedBefore = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .Any(w => w.def == shotgun);
                    InGizmo(() => WeaponAssingment.DropSidearm(dockie, carried,
                                                              intentionalDrop: true, unmemorise: true));
                    if (wasCarriedBefore
                        && !dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                               .Any(w => w.def == shotgun))
                    {
                        gizmoForgetShotgunDropped = true;
                    }
                    ForceReconcile(dockie);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("recorded-as-do-not-equip", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(p => p.thing == shotgun);
                        return (excluded, $"shotgun in dontEquip={excluded}");
                    }),
                    C("dropped-and-carried-again", () =>
                    {
                        // The old form was a precondition — which latched on Baseline's
                        // own possession BEFORE the drop, so "CE hauled it back" was
                        // never actually gated on it having left. Confirmed-drop first,
                        // then carried again.
                        var carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Any(w => w.def == shotgun);
                        return (gizmoForgetShotgunDropped && carried,
                                $"dropped={gizmoForgetShotgunDropped} carried again={carried}");
                    }),
                    N("never-re-claimed", () =>
                    {
                        // What this module controls. NOT "SS never remembers it again": the
                        // row is declared, so CE may equip the shotgun as primary, and SS's
                        // JustBeforeEquip then remembers it through InformOfAddedPrimary with
                        // nothing of ours involved. Asserting against that made this phase
                        // fail or pass on CE's equip timing.
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool claimed = rec != null && rec.claimed.Any(p => p.thing == shotgun);
                        return (!claimed, $"shotgun re-claimed by the projection={claimed}");
                    }),
                    C("forget-forensics", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        string excl = rec == null ? "no-record"
                            : string.Join(",", rec.dontEquip.Select(p => $"{p.thing?.defName}/{p.stuff?.defName ?? "null"}"));
                        string mem = string.Join(",", Mem(dockie).RememberedWeapons
                            .Select(p => $"{p.thing?.defName}/{p.stuff?.defName ?? "null"}"));
                        var carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .FirstOrDefault(w => w.def == shotgun);
                        return (true, $"dontEquip=[{excl}] remembered=[{mem}] "
                                      + $"carriedShotgun={(carried == null ? "none" : carried.Stuff?.defName ?? "null-stuff")} "
                                      + $"curJob={dockie.CurJobDef?.defName ?? "none"} "
                                      + $"playerForced={dockie.CurJob?.playerForced}");
                    }, informational: true),
                    C("row-still-declared-so-ce-keeps-hauling-it", () =>
                    {
                        bool declared = loadout.Slots.Any(sl => sl.thingDef == shotgun);
                        return (declared, $"shotgun row present={declared}");
                    }),
                }
            });

            // #4. CE reassigns every pawn of a deleted loadout to the default one by writing
            // its dictionary directly, and deleting a loadout is an unconfirmed float-menu
            // click. Reading that as "declares nothing" wiped every claimed sidearm at once.
            // Last, because it takes the loadout away.
            phases.Add(new Phase
            {
                label = "deleting-the-loadout-releases-claims-and-keeps-the-players-own",
                deadlineTicks = 6000,
                minTicks = 600,
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    // A memory that is the PLAYER's and not a claim: an undeclared,
                    // uncarried pair. The deletion must release the projection's claims
                    // (or the compat patch's drop exemption pins those weapons forever)
                    // while leaving this one alone.
                    var own = new ThingDefStuffDefPair(revolver, null);
                    if (!Mem(dockie).RememberedWeapons.Contains(own))
                    {
                        Mem(dockie).RememberedWeapons.Add(own);
                    }
                },
                mutate = () =>
                {
                    var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                    beforeDelete = (rec?.claimed ?? new List<ThingDefStuffDefPair>())
                        .Select(pr => pr.thing.defName).ToHashSet();
                    LoadoutManager.RemoveLoadout(loadout);
                    ForceReconcile(dockie);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("precondition-pawn-is-on-the-default-loadout", () =>
                    {
                        Loadout now = dockie.GetLoadout();
                        return (now == null || now.defaultLoadout, $"loadout={now?.label ?? "null"}");
                    }),
                    P("there-were-claims-to-release", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        int n = rec?.claimed.Count ?? 0;
                        return (n > 0, $"claims={n}");
                    }),
                    C("the-claims-are-handed-back", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        int n = rec?.claimed.Count ?? 0;
                        bool stillRemembered = Mem(dockie).RememberedWeapons
                            .Any(pr => beforeDelete.Contains(pr.thing.defName));
                        return (n == 0 && !stillRemembered,
                                $"claims={n} formerly-claimed still remembered={stillRemembered}");
                    }),
                    N("the-players-own-memory-survives", () =>
                    {
                        bool kept = Mem(dockie).RememberedWeapons.Any(pr => pr.thing == revolver);
                        return (kept, $"revolver remembered={kept}");
                    }),
                }
            });




            // The lifecycle nobody had a test for, and which regressed unnoticed through three
            // reviews: an exclusion follows its loadout row. Take the weapon out and put it
            // back, and the pawn manages it again. Without the prune this passes for the
            // wrong reason only if the exclusion was never set, which the precondition rules
            // out.
            phases.Add(new Phase
            {
                label = "an-exclusion-is-cleared-by-removing-and-re-adding-the-row",
                deadlineTicks = 8000,
                minTicks = 300,
                // Arrange only baselines. Excluding the pistol is an ACT, and acts belong in
                // mutate — it needs the pistol to be remembered first, which Baseline sets up
                // asynchronously. Doing it here is how this phase VOIDed on its first run.
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                checks =
                {
                    P("pistol-is-remembered-before-we-exclude-it", () =>
                    {
                        bool remembered = Mem(dockie).RememberedWeapons.Any(p => p.thing == pistol);
                        return (remembered, $"remembered={remembered}");
                    }),
                    C("the-exclusion-was-actually-set", () =>
                    {
                        // Sampled inside mutate. Without it, "no longer excluded" at the end
                        // passes just as well when the exclusion was never set in the first
                        // place — which is exactly how this phase first went wrong.
                        return (pistolWasExcluded, $"exclusion took={pistolWasExcluded}");
                    }),
                    C("row-round-trip-clears-it", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(p => p.thing == pistol);
                        return (!excluded, $"still excluded={excluded}");
                    }),
                    C("exclusion-forensics", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        string excl = rec == null ? "no-record"
                            : string.Join(",", rec.dontEquip.Select(p => p.thing?.defName));
                        string clm = rec == null ? "-"
                            : string.Join(",", rec.claimed.Select(p => p.thing?.defName));
                        bool carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Any(w => w.def == pistol);
                        return (true, $"setAt={pistolWasExcluded} dontEquip=[{excl}] claimed=[{clm}] "
                                      + $"pistolCarried={carried} row={SlotOf(pistol) != null} "
                                      + $"curJob={dockie.CurJobDef?.defName ?? "none"} "
                                      + $"forced={dockie.CurJob?.playerForced}");
                    }, informational: true),
                    C("and-the-pistol-is-managed-again", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool claimed = rec != null && rec.claimed.Any(p => p.thing == pistol);
                        bool remembered = Mem(dockie).RememberedWeapons.Any(p => p.thing == pistol);
                        return (claimed && remembered, $"claimed={claimed} remembered={remembered}");
                    }),
                },
                mutate = () =>
                {
                    PlayerForgets(dockie, pistol);
                    var recNow = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                    pistolWasExcluded = recNow != null && recNow.dontEquip.Any(p => p.thing == pistol);

                    // No drop in between: the pawn keeps carrying it throughout, so this also
                    // pins that re-claiming does not depend on CE re-fetching anything.
                    LoadoutSlot slot = SlotOf(pistol);
                    if (slot != null)
                    {
                        loadout.RemoveSlot(slot);
                    }
                    ForceReconcile(dockie);
                    loadout.AddSlot(new LoadoutSlot(pistol, 1));
                    ForceReconcile(dockie);
                },
            });


            // The exclusion versus CE's automatic re-arm (#37). The old version of this
            // phase could not reach the mechanism: the staged loadout is ad-hoc, and CE's
            // primary-swap branch never fires on an ad-hoc loadout because GetSlotsFor
            // synthesises a slot for whatever the current primary is. adHoc is switched off
            // here so CE's re-arm logic genuinely runs — and the phase requires the pawn to
            // actually re-arm, so "nothing happened" cannot pass as "correctly refused".
            phases.Add(new Phase
            {
                label = "a-suppressed-weapon-is-refused-to-the-machine-and-offered-to-the-player",
                deadlineTicks = 20000,
                minTicks = 600,
                poll = () =>
                {
                    ForceReconcile(dockie);
                    // The re-arm is DRIVEN through SS's real funnel rather than raced
                    // against think-tree cadence: with the ground pool swept, SS's
                    // retrieval wins the fetch and parks the sniper in the inventory,
                    // and the idle re-arm giver does not reliably fire mid-wander. This
                    // is the same driven style as ForceReconcile — and it is exactly the
                    // selection-plus-funnel path the exclusion must survive: the sniper
                    // must win it, the excluded pistol must not.
                    if (dockie.equipment?.Primary == null
                        && dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                               .Any(w => w.def.IsRangedWeapon))
                    {
                        WeaponAssingment.equipBestWeaponFromInventoryByPreference(
                            dockie, PeteTimesSix.SimpleSidearms.Utilities.Enums.DroppingModeEnum.Calm,
                            PeteTimesSix.SimpleSidearms.Utilities.Enums.PrimaryWeaponMode.Ranged);
                    }
                },
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    loadout.adHoc = false;
                    PlayerForgets(dockie, pistol);
                },
                mutate = () =>
                {
                    dockie.equipment.DestroyAllEquipment();
                    // The re-arm this phase expects is CE fetching a fresh sniper for the
                    // now-unsatisfied row and equipping it (primary is empty). It used to
                    // lean on the staging map's ground pool — which Baseline's litter
                    // sweep now clears — so the phase brings its own.
                    GenSpawn.Spawn(ThingMaker.MakeThing(sniper),
                        CellFinder.RandomClosewalkCellNear(dockie.Position, dockie.Map, 4),
                        dockie.Map);
                },
                checks =
                {
                    P("pistol-is-excluded", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        return (excluded, $"excluded={excluded}");
                    }),
                    P("pistol-still-carried", () =>
                    {
                        bool carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Any(w => w.def == pistol);
                        return (carried, $"carried={carried}");
                    }),
                    C("machine-context-is-refused", () =>
                    {
                        var t = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .FirstOrDefault(w => w.def == pistol);
                        if (t == null) { return (false, "pistol not found"); }
                        bool ok = EquipmentUtility.CanEquip(t, dockie, out string why);
                        return (!ok, $"CanEquip={ok} reason='{why}'");
                    }),
                    C("player-context-is-offered", () =>
                    {
                        var t = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .FirstOrDefault(w => w.def == pistol);
                        if (t == null) { return (false, "pistol not found"); }
                        bool ok = InChoice(() => EquipmentUtility.CanEquip(t, dockie, out string _));
                        return (ok, $"CanEquip(inside the tab's menu build)={ok}");
                    }),
                    C("the-pawn-did-re-arm", () =>
                    {
                        // Without this, "the pistol never became primary" passes just as
                        // well when nothing re-armed at all — which is exactly how the old
                        // phase passed in the isolated run (primary=none for the window).
                        bool armed = dockie.equipment?.Primary != null;
                        return (armed, $"primary={dockie.equipment?.Primary?.def?.defName ?? "none"}");
                    }),
                    N("pistol-never-becomes-primary", () =>
                    {
                        bool isPrimary = dockie.equipment?.Primary?.def == pistol;
                        return (!isPrimary, $"primary={dockie.equipment?.Primary?.def?.defName ?? "none"}");
                    }),
                }
            });

            // The other half of the same design: the player's own Equip order goes through
            // and IS the un-suppress gesture — one gesture in (gizmo forget), one gesture out
            // (right-click, Equip).
            phases.Add(new Phase
            {
                label = "a-player-equip-order-withdraws-the-exclusion",
                deadlineTicks = 12000,
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    PlayerForgets(dockie, pistol);
                },
                mutate = () =>
                {
                    ThingWithComps t = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .FirstOrDefault(w => w.def == pistol);
                    if (t != null)
                    {
                        // The dropdown path: the weapon on the ground, an ordered Equip job.
                        dockie.inventory.innerContainer.TryDrop(t, dockie.Position, dockie.Map,
                            ThingPlaceMode.Near, out Thing dropped);
                        droppedPistol = dropped as ThingWithComps;
                        if (droppedPistol != null)
                        {
                            dockie.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Equip, droppedPistol));
                        }
                    }
                },
                checks =
                {
                    P("pistol-starts-excluded", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        return (excluded, $"excluded={excluded}");
                    }),
                    C("pistol-was-dropped-for-the-order", () =>
                    {
                        // Only the setup half is asserted directly. "The equip ran" was first
                        // written as job-in-flight-or-pistol-is-primary, which races SS's own
                        // idle switching — it legitimately swaps the pawn back toward the
                        // sniper role between polls, so the transient window can fall between
                        // samples. The durable completion proof is the two outcome checks
                        // below: only this phase's playerForced equip can produce them.
                        return (droppedPistol != null, $"dropped={droppedPistol != null}");
                    }),
                    C("exclusion-withdrawn", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        return (!excluded, $"still excluded={excluded}");
                    }),
                    C("pistol-remembered-again", () =>
                    {
                        bool remembered = Mem(dockie).RememberedWeapons.Any(pr => pr.thing == pistol);
                        return (remembered, $"remembered={remembered}");
                    }),
                }
            });


            // The proof for the main fix of this round: the exclusion gesture drops the
            // weapon (SS's cascade), and CE must still haul it back for the still-declared
            // row. On the pre-fix code the CanEquip patch refused CE's pickup search and the
            // weapon rotted on the ground with the row permanently unsatisfiable.
            phases.Add(new Phase
            {
                label = "an-excluded-weapon-is-still-hauled-back",
                deadlineTicks = 25000,
                minTicks = 600,
                poll = () =>
                {
                    // The gesture's drop half can fail quietly (both drop APIs return a
                    // bool nothing reads) and one run failed exactly there, with the
                    // haul-back check then latching on "still carried" before any drop.
                    // So the drop is CONFIRMED here, retried until it lands, and the
                    // haul-back check below is gated on it having happened.
                    if (!shotgunWasDropped)
                    {
                        ThingWithComps t2 = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .FirstOrDefault(w => w.def == shotgun);
                        if (t2 == null)
                        {
                            shotgunWasDropped = true;
                        }
                        else if (dockie.equipment?.Primary == t2)
                        {
                            dockie.equipment.TryDropEquipment(t2, out _, dockie.Position, forbid: false);
                        }
                        else
                        {
                            dockie.inventory?.innerContainer?.TryDrop(t2, dockie.Position, dockie.Map,
                                ThingPlaceMode.Near, out _);
                        }
                    }
                    else
                    {
                        // Post-return, the never-re-claimed negative needs reconciles to
                        // actually run against the returned weapon — without this the
                        // window was passive and the phase usually ended on the very poll
                        // the shotgun came back.
                        ForceReconcile(dockie);
                    }
                },
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    ThingWithComps t = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .FirstOrDefault(w => w.def == shotgun);
                    if (t != null)
                    {
                        // The REAL removal gesture: SS's carried-weapon branch, which drops
                        // the weapon before forgetting it.
                        InGizmo(() => WeaponAssingment.DropSidearm(dockie, t,
                            intentionalDrop: true, unmemorise: true));
                        ForceReconcile(dockie);
                    }
                },
                checks =
                {
                    C("shotgun-excluded", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(pr => pr.thing == shotgun);
                        return (excluded, $"excluded={excluded}");
                    }),
                    C("shotgun-was-dropped", () =>
                    {
                        return (shotgunWasDropped, $"dropped={shotgunWasDropped}");
                    }),
                    C("ce-hauls-it-back", () =>
                    {
                        bool carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Any(w => w.def == shotgun);
                        // Gated on the drop: "still carried because it never left" must not
                        // satisfy the check that exists to prove it comes BACK.
                        return (shotgunWasDropped && carried,
                                $"dropped={shotgunWasDropped} carried again={carried}");
                    }),
                    N("and-it-is-never-re-claimed", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool claimed = rec != null && rec.claimed.Any(pr => pr.thing == shotgun);
                        return (!claimed, $"re-claimed={claimed}");
                    }),
                    N("and-it-is-never-wielded", () =>
                    {
                        bool wielded = dockie.equipment?.Primary?.def == shotgun;
                        return (!wielded, $"primary={dockie.equipment?.Primary?.def?.defName ?? "none"}");
                    }),
                }
            });

            // The other half of the design's asymmetry, previously untested: a MACHINE
            // equip must not clear an exclusion. The player half (a playerForced job
            // clearing it) is phase 20.
            phases.Add(new Phase
            {
                label = "a-machine-equip-does-not-clear-the-exclusion",
                deadlineTicks = 15000,
                minTicks = 600,
                poll = () =>
                {
                    // Sampled before the reconcile: the equip landing is the event this
                    // phase exists around, and without the capture the negative below holds
                    // just as well when the equip never happened at all.
                    if (dockie.equipment?.Primary?.def == pistol)
                    {
                        machineEquipLanded = true;
                    }
                    // Drafting held CE's think tree off the ground pistol so the forced
                    // equip could not be out-raced; done with it once the equip landed.
                    if (machineEquipLanded && (dockie.drafter?.Drafted ?? false))
                    {
                        dockie.drafter.Drafted = false;
                    }
                    ForceReconcile(dockie);
                },
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    PlayerForgets(dockie, pistol);
                },
                mutate = () =>
                {
                    ThingWithComps t = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .FirstOrDefault(w => w.def == pistol);
                    if (t != null)
                    {
                        // Drafted, so CE's job giver cannot re-haul the dropped pistol and
                        // out-race the forced equip — the phase failed its own landed-check
                        // on runs where CE won that race.
                        if (dockie.drafter != null)
                        {
                            dockie.drafter.Drafted = true;
                        }
                        dockie.inventory.innerContainer.TryDrop(t, dockie.Position, dockie.Map,
                            ThingPlaceMode.Near, out Thing dropped);
                        if (dropped is ThingWithComps ground)
                        {
                            // A non-player equip: StartJob, not TryTakeOrderedJob, so
                            // playerForced stays false — the shape of any mod- or
                            // game-issued equip.
                            dockie.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Equip, ground),
                                Verse.AI.JobCondition.InterruptForced);
                        }
                    }
                },
                checks =
                {
                    P("pistol-was-excluded-first", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        return (excluded, $"excluded={excluded}");
                    }),
                    C("the-machine-equip-landed", () =>
                    {
                        return (machineEquipLanded, $"landed={machineEquipLanded}");
                    }),
                    N("exclusion-survives-the-machine-equip", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        return (excluded, $"still excluded={excluded}");
                    }),
                }
            });

            // The tab path (#37 follow-up): equipping an excluded weapon from CE's
            // inventory tab clears the exclusion and lands in the identical end state as
            // the map menu — equipped, remembered, role set — immediately, no reconcile.
            phases.Add(new Phase
            {
                label = "equipping-from-the-inventory-tab-clears-the-exclusion",
                deadlineTicks = 8000,
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    PlayerForgets(dockie, pistol);
                    // A hand-cleared role, so the phase also proves the recorder lifts the
                    // matching veto: a tab equip is the player's word on both fronts.
                    PlayerClearsRangedRole(dockie);
                },
                mutate = () =>
                {
                    ThingWithComps t = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .FirstOrDefault(w => w.def == pistol);
                    var inv = dockie.TryGetComp<CombatExtended.CompInventory>();
                    if (t != null && inv != null)
                    {
                        // The tab menu's click action, via its (player-only) synced wrapper.
                        AccessTools.Method(typeof(CombatExtended.ITab_Inventory), "SyncedTrySwitchToWeapon")
                            .Invoke(null, new object[] { inv, t });
                        // Sampled HERE: primary and role are transients — the next reconcile
                        // correctly hands the ranged role back to the loadout's first, and
                        // SS's idle switching then re-arms accordingly. The first version of
                        // this phase asserted primary at poll time and failed on that
                        // correct behaviour.
                        tabSwitchEquipped = dockie.equipment?.Primary?.def == pistol;
                        tabSwitchRole = Mem(dockie).DefaultRangedWeapon?.thing == pistol;
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        tabSwitchVetoLifted = rec != null && !rec.rangedRoleVetoed;
                    }
                },
                checks =
                {
                    P("pistol-starts-excluded-and-carried", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        bool carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Any(w => w.def == pistol);
                        return (excluded && carried, $"excluded={excluded} carried={carried}");
                    }),
                    C("the-switch-happened", () =>
                    {
                        return (tabSwitchEquipped && tabSwitchRole,
                                $"equipped-at-click={tabSwitchEquipped} role-at-click={tabSwitchRole}");
                    }),
                    C("exclusion-cleared", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        return (!excluded, $"still excluded={excluded}");
                    }),
                    C("remembered-durably", () =>
                    {
                        bool remembered = Mem(dockie).RememberedWeapons.Any(pr => pr.thing == pistol);
                        return (remembered, $"remembered={remembered}");
                    }),
                    C("the-role-veto-was-lifted-at-the-click", () =>
                    {
                        return (tabSwitchVetoLifted, $"veto lifted={tabSwitchVetoLifted}");
                    }),
                }
            });

            // The half the old hook got wrong: the click action runs a frame after the
            // menu was built, and the weapon can be gone by then — TrySwitchToWeapon
            // returns void and exits silently. The old click-time hook still cleared the
            // exclusion and wrote a memory for a weapon never equipped. The AddEquipment
            // recorder only fires on an equip that actually happened, so a failed click
            // changes nothing.
            phases.Add(new Phase
            {
                label = "a-failed-tab-switch-changes-nothing",
                deadlineTicks = 4000,
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    PlayerForgets(dockie, pistol);
                },
                mutate = () =>
                {
                    ThingWithComps t = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .FirstOrDefault(w => w.def == pistol);
                    var inv = dockie.TryGetComp<CombatExtended.CompInventory>();
                    if (t != null && inv != null)
                    {
                        // The weapon leaves the container between the menu frame and the
                        // click frame — the race CE's loadout enforcement or a caravan
                        // pack job produces in play.
                        dockie.inventory.innerContainer.Remove(t);
                        AccessTools.Method(typeof(CombatExtended.ITab_Inventory), "SyncedTrySwitchToWeapon")
                            .Invoke(null, new object[] { inv, t });
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        failedSwitchKeptExclusion = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        failedSwitchNotRemembered = !Mem(dockie).RememberedWeapons.Any(pr => pr.thing == pistol);
                        // Restore the world for the phases after this one.
                        dockie.inventory.innerContainer.TryAdd(t);
                        inv.UpdateInventory();
                    }
                    ForceReconcile(dockie);
                },
                checks =
                {
                    P("the-tab-wrapper-still-resolves", () =>
                    {
                        bool ok = AccessTools.Method(typeof(CombatExtended.ITab_Inventory),
                            "SyncedTrySwitchToWeapon") != null;
                        return (ok, $"resolves={ok}");
                    }),
                    P("pistol-starts-excluded-and-carried", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        bool carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Any(w => w.def == pistol);
                        return (excluded && carried, $"excluded={excluded} carried={carried}");
                    }),
                    C("the-exclusion-survives-the-failed-click", () =>
                    {
                        return (failedSwitchKeptExclusion, $"kept={failedSwitchKeptExclusion}");
                    }),
                    C("nothing-was-remembered-from-the-failed-click", () =>
                    {
                        return (failedSwitchNotRemembered, $"not remembered={failedSwitchNotRemembered}");
                    }),
                }
            });

            // Release() hands back claims and nothing else.
            phases.Add(new Phase
            {
                label = "release-returns-claims-and-keeps-exclusions",
                deadlineTicks = 6000,
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    PlayerForgets(dockie, pistol);
                },
                mutate = () =>
                {
                    CESidearmsSupply.SupplyMod.Release();
                    // Sampled AT the act: with the feature on, the next natural reconcile
                    // correctly re-claims, so "claims == 0 at the first poll" was a race
                    // with CE's cadence — a few runs in a hundred lost it.
                    var recNow = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                    releaseLeftNoClaims = (recNow?.claimed.Count ?? -1) == 0;
                },
                checks =
                {
                    P("claims-and-an-exclusion-exist", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool claims = rec != null && rec.claimed.Count > 0;
                        bool excl = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        return (claims && excl, $"claims={rec?.claimed.Count ?? 0} excluded={excl}");
                    }),
                    C("claims-released", () =>
                    {
                        return (releaseLeftNoClaims, $"claims were zero at the release={releaseLeftNoClaims}");
                    }),
                    C("exclusion-kept", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excl = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        return (excl, $"excluded={excl}");
                    }),
                }
            });

            // Removing a loadout row releases exactly one memory when a duplicate exists —
            // the one this module added — and never drains the player's copies.
            phases.Add(new Phase
            {
                label = "removing-a-row-releases-one-memory-not-all-copies",
                deadlineTicks = 6000,
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    // A second, player-style memory of the same pair. SS keeps duplicates.
                    var pair = Mem(dockie).RememberedWeapons.FirstOrDefault(pr => pr.thing == shotgun);
                    if (pair.thing != null)
                    {
                        Mem(dockie).RememberedWeapons.Add(pair);
                    }
                },
                mutate = () =>
                {
                    LoadoutSlot slot = SlotOf(shotgun);
                    if (slot != null)
                    {
                        loadout.RemoveSlot(slot);
                    }
                    ForceReconcile(dockie);
                },
                checks =
                {
                    P("two-copies-remembered", () =>
                    {
                        int n = Mem(dockie).RememberedWeapons.Count(pr => pr.thing == shotgun);
                        return (n == 2, $"copies={n}");
                    }),
                    C("exactly-one-copy-remains", () =>
                    {
                        int n = Mem(dockie).RememberedWeapons.Count(pr => pr.thing == shotgun);
                        return (n == 1, $"copies={n}");
                    }),
                }
            });

            // The eligibility test in Target() must be able to say no. SS's per-weapon
            // whitelist (Selection mode, emptied) makes every weapon illegal; nothing may be
            // claimed under it. Settings restored inside the mutate.
            phases.Add(new Phase
            {
                label = "an-illegal-sidearm-is-never-claimed",
                deadlineTicks = 6000,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    var settings = PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings;
                    var oldMode = settings.LimitModeSingle;
                    var oldSel = settings.LimitModeSingle_Selection;
                    // isValidSidearm reads LimitModeSingle only when SeparateModes is false;
                    // SS's default preset sets it true, so without forcing it the knob below
                    // is inert — which is exactly how this phase first failed.
                    var oldSeparate = settings.SeparateModes;
                    try
                    {
                        settings.SeparateModes = false;
                        settings.LimitModeSingle = PeteTimesSix.SimpleSidearms.Utilities.Enums.LimitModeSingleSidearm.Selection;
                        settings.LimitModeSingle_Selection = new HashSet<ThingDef>();
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        rec.claimed.Clear();
                        foreach (var pr in Mem(dockie).RememberedWeapons.ToList())
                        {
                            Mem(dockie).ForgetSidearmMemory(pr);
                        }
                        ForceReconcile(dockie);
                        illegalClaimCount = rec.claimed.Count
                                            + Mem(dockie).RememberedWeapons.Count;
                    }
                    finally
                    {
                        settings.SeparateModes = oldSeparate;
                        settings.LimitModeSingle = oldMode;
                        settings.LimitModeSingle_Selection = oldSel;
                    }
                    ForceReconcile(dockie);
                    // The positive control: claims must RETURN once the whitelist does,
                    // or "nothing claimed under an empty whitelist" is indistinguishable
                    // from Target() claiming nothing under any settings at all.
                    claimsReturnedAfterRestore =
                        (CESidearmsSupply.CompLoadoutSidearms.For(dockie)?.claimed.Count ?? 0) > 0;
                },
                checks =
                {
                    C("nothing-claimed-while-everything-was-illegal", () =>
                    {
                        return (illegalClaimCount == 0, $"claimed+remembered under empty whitelist={illegalClaimCount}");
                    }),
                    C("claims-return-with-the-whitelist", () =>
                    {
                        return (claimsReturnedAfterRestore, $"claims after restore>0={claimsReturnedAfterRestore}");
                    }),
                }
            });

            // The other, worse half of the ground-item story: CE does not only haul.
            // When the pawn's primary is empty (which the removal gesture on a wielded
            // weapon guarantees) or not covered by a loadout row, CE issues a real Equip
            // job on the priority ground item — wielding the exact weapon the player just
            // excluded, after which SS's own equip hook re-remembers it with no player
            // anywhere in the chain. The fix downgrades the job to a plain take; and even
            // once the weapon is back in the inventory, SS's own switching (which never
            // consults CanEquip) must not draw it either.
            phases.Add(new Phase
            {
                label = "an-excluded-weapon-on-the-ground-is-not-wielded-by-the-machine",
                deadlineTicks = 25000,
                minTicks = 900,
                // Single-row loadout, deliberately: with any other declared weapon carried,
                // SS re-arms it within ticks of the drop, the primary is loadout-covered
                // again, and CE takes its haul branch even WITHOUT the fix — the wield
                // branch this phase pins fires only while the primary is empty or not
                // covered by a row.
                arrange = () => Baseline(dockie, loadout, sniper),
                mutate = () =>
                {
                    ThingWithComps sn = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .FirstOrDefault(w => w.def == sniper);
                    if (sn == null)
                    {
                        return;
                    }
                    // Wield it first, so the removal gesture leaves the primary empty and
                    // CE's equip branch — not its haul branch — is the live one.
                    if (dockie.equipment?.Primary != sn)
                    {
                        dockie.TryGetComp<CombatExtended.CompInventory>()?.TrySwitchToWeapon(sn);
                    }
                    InGizmo(() => WeaponAssingment.DropSidearm(dockie, sn,
                        intentionalDrop: true, unmemorise: true));
                    var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                    sniperWasExcludedAtDrop = rec != null && rec.dontEquip.Any(pr => pr.thing == sniper);
                },
                checks =
                {
                    C("sniper-was-excluded-at-the-drop", () =>
                    {
                        return (sniperWasExcludedAtDrop, $"excluded at drop={sniperWasExcludedAtDrop}");
                    }),
                    C("ce-takes-it-back-to-inventory", () =>
                    {
                        bool inInventory = dockie.inventory?.innerContainer?
                            .Any(t => t.def == sniper) ?? false;
                        return (inInventory, $"in inventory={inInventory}");
                    }),
                    N("the-sniper-is-never-wielded-again", () =>
                    {
                        bool wielded = dockie.equipment?.Primary?.def == sniper;
                        return (!wielded, $"primary={dockie.equipment?.Primary?.def?.defName ?? "none"}");
                    }),
                }
            });

            // Two materials of one declared def: the role must settle on one of them and
            // stay there while the inventory reorders underneath — which every equip,
            // unequip and CE ammo shuffle does. The old preference read the previous claim
            // list, which contained BOTH candidates, so the role followed enumeration
            // order; the fix pins the pair currently holding the role.
            phases.Add(new Phase
            {
                label = "a-role-settles-between-two-materials-of-one-def",
                deadlineTicks = 8000,
                minTicks = 900,
                poll = () =>
                {
                    // The inventory order is flipped on EVERY poll — the churn equips,
                    // unequips and ammo shuffles produce in play. Under the old logic the
                    // preference followed the claim list, whose order lags the enumeration
                    // by one pass, so continuous churn makes the role oscillate; the pinned
                    // role holder cannot.
                    ThingWithComps g = dockie.inventory.innerContainer.OfType<ThingWithComps>()
                        .FirstOrDefault(t => t.def == gladius);
                    if (g != null)
                    {
                        dockie.inventory.innerContainer.Remove(g);
                        dockie.inventory.innerContainer.TryAdd(g);
                        dockie.TryGetComp<CombatExtended.CompInventory>()?.UpdateInventory();
                    }
                    ForceReconcile(dockie);
                },
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    // A second copy in a DIFFERENT material than whatever the staged one
                    // is — hardcoding plasteel made a duplicate pair the moment the staged
                    // gladius happened to be plasteel itself.
                    ThingWithComps staged = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                        .FirstOrDefault(w => w.def == gladius);
                    ThingDef otherStuff = staged?.Stuff == ThingDefOf.Plasteel
                        ? ThingDefOf.Steel : ThingDefOf.Plasteel;
                    ThingWithComps second = (ThingWithComps)ThingMaker.MakeThing(gladius, otherStuff);
                    dockie.inventory.innerContainer.TryAdd(second);
                    dockie.TryGetComp<CombatExtended.CompInventory>()?.UpdateInventory();
                    ForceReconcile(dockie);
                },
                mutate = () =>
                {
                    // The settled role, sampled at the act. Which material won is not the
                    // claim (market value decides that fresh pick); the claim is that it
                    // never changes afterwards, while the poll churns the inventory order.
                    meleeRoleAtSettle = Mem(dockie).PreferredMeleeWeapon;
                },
                checks =
                {
                    P("two-materials-are-claimed", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        var pairs = rec == null ? new List<ThingDefStuffDefPair>()
                            : rec.claimed.Where(pr => pr.thing == gladius).ToList();
                        var carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Where(w => w.def == gladius)
                            .Select(w => (w.Stuff?.defName ?? "null")
                                + ":valid=" + StatCalculator.isValidSidearm(w.toThingDefStuffDefPair(), out string why) + why)
                            .ToList();
                        return (pairs.Count == 2,
                                $"claimed=[{string.Join(",", pairs.Select(pr => pr.stuff?.defName ?? "null"))}] "
                                + $"carried=[{string.Join(",", carried)}] "
                                + $"dontEquip=[{string.Join(",", (rec?.dontEquip ?? new List<ThingDefStuffDefPair>()).Select(pr => (pr.thing?.defName ?? "null") + "/" + (pr.stuff?.defName ?? "null")))}]");
                    }),
                    C("a-role-was-settled-at-the-act", () =>
                    {
                        return (meleeRoleAtSettle.HasValue,
                                $"role at act={meleeRoleAtSettle?.thing?.defName ?? "none"}");
                    }),
                    N("the-role-never-flips", () =>
                    {
                        var now = Mem(dockie).PreferredMeleeWeapon;
                        bool stable = now == meleeRoleAtSettle;
                        return (stable, $"was={meleeRoleAtSettle?.stuff?.defName ?? "null"} "
                                        + $"now={now?.stuff?.defName ?? "null"}");
                    }),
                }
            });

            // The settings toggle is global; the sweep it triggers is per-colony. Turning
            // the feature off must sweep the loaded colony AND arm releasePending so every
            // other save is swept on its next load — the flag was previously armed only in
            // the no-save-loaded branch, so a second colony kept its claims forever (and
            // the compat patch's drop exemption pinned those weapons in inventories).
            //
            // A/B note: on a pre-fix tree this phase fails in mutate with a
            // MissingMethodException (Release(bool) does not exist there), not on the
            // armed-flag check itself. The verdict direction is still right — the old tree
            // cannot arm the flag — but the A leg pins the signature, not the semantics.
            phases.Add(new Phase
            {
                label = "turning-the-feature-off-sweeps-this-colony-and-arms-the-rest",
                deadlineTicks = 4000,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                    var settings = CESidearmsSupply.SupplyMod.Settings;
                    bool wasPending = settings.releasePending;
                    featureOffHadClaims = rec != null && rec.claimed.Count > 0;
                    try
                    {
                        settings.loadoutWeaponsAsSidearms = false;
                        CESidearmsSupply.SupplyMod.Release(interactive: true);
                        featureOffSweptThisColony = rec != null && rec.claimed.Count == 0;
                        featureOffArmedTheFlag = settings.releasePending;
                    }
                    finally
                    {
                        // Mirror the settings window's re-enable path: turning the feature
                        // back on clears the pending flag so the deferred sweep does not
                        // fire on an enabled feature. Written to DISK, not just memory:
                        // Release() persisted the flipped values via Settings.Write(), and
                        // leaving them on disk poisoned every later game launch — the
                        // feature booted off, phase 0 burned its whole deadline fetching
                        // nothing, and the A/B legs judged a broken world.
                        settings.loadoutWeaponsAsSidearms = true;
                        // The staged default, NOT wasPending: a poisoned boot value would
                        // self-perpetuate through the restore.
                        settings.releasePending = false;
                        settings.Write();
                    }
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("there-were-claims-to-sweep", () =>
                    {
                        return (featureOffHadClaims, $"had claims={featureOffHadClaims}");
                    }),
                    C("this-colony-was-swept", () =>
                    {
                        return (featureOffSweptThisColony, $"swept={featureOffSweptThisColony}");
                    }),
                    C("the-flag-was-armed-for-every-other-save", () =>
                    {
                        return (featureOffArmedTheFlag, $"releasePending={featureOffArmedTheFlag}");
                    }),
                }
            });

            // The drafted branch of clicking an unremembered weapon in the gizmo calls
            // SetWeaponAsForced, not InformOfAddedSidearm — so before the fix, a drafted
            // player clicking an excluded weapon back into use got the force and KEPT the
            // exclusion, which then outlived the force. Forcing is the strongest player
            // statement there is; it withdraws the exclusion.
            phases.Add(new Phase
            {
                label = "forcing-an-excluded-weapon-while-drafted-withdraws-the-exclusion",
                deadlineTicks = 4000,
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    PlayerForgets(dockie, pistol);
                },
                mutate = () =>
                {
                    var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                    ThingDefStuffDefPair? pr = rec?.dontEquip.FirstOrDefault(x => x.thing == pistol);
                    if (pr.HasValue && pr.Value.thing != null)
                    {
                        InGizmo(() => Mem(dockie).SetWeaponAsForced(pr.Value, drafted: true));
                        forceWithdrewExclusion = rec != null && !rec.dontEquip.Any(x => x.thing == pistol);
                    }
                    // The force itself is scaffolding for this phase, not its claim; a
                    // lingering drafted-force would leak into every later phase's Apply.
                    Mem(dockie).ForcedWeaponWhileDrafted = null;
                    ForceReconcile(dockie);
                },
                checks =
                {
                    P("pistol-was-excluded-first", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(x => x.thing == pistol);
                        return (excluded, $"excluded={excluded}");
                    }),
                    C("the-force-withdrew-the-exclusion", () =>
                    {
                        return (forceWithdrewExclusion, $"withdrawn at force={forceWithdrewExclusion}");
                    }),
                }
            });

            // A role is a stronger statement than a claim: SS equips the default ranged
            // weapon unconditionally, skipping every filter its own picker applies. A
            // loadout listing an EMP weapon first must still hand the role to the first
            // REAL gun — while the EMP weapon stays claimed (carried per the loadout).
            phases.Add(new Phase
            {
                label = "an-emp-weapon-is-never-handed-a-role",
                deadlineTicks = 6000,
                arrange = () => Baseline(dockie, loadout, D("Weapon_GrenadeEMP"), pistol),
                mutate = () =>
                {
                    ForceReconcile(dockie);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("the-role-skips-the-emp-weapon", () =>
                    {
                        var role = Mem(dockie).DefaultRangedWeapon;
                        return (role?.thing == pistol,
                                $"default ranged={role?.thing?.defName ?? "none"}");
                    }),
                    C("the-emp-weapon-is-still-claimed", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool claimed = rec != null && rec.claimed.Any(x => x.thing == D("Weapon_GrenadeEMP"));
                        return (claimed, $"claimed={claimed}");
                    }),
                }
            });

            // Every Harmony patch this mod declares, verified applied. TESTPLAN promised
            // this phase for two rounds before it existed. It is the tripwire for the
            // attribute-pin class of failure: an ambiguous overload upstream aborts
            // PatchAll for the WHOLE assembly, and every phase here would then measure a
            // world where this mod is silently absent.
            phases.Add(new Phase
            {
                label = "every-declared-patch-is-applied",
                deadlineTicks = 2000,
                mutate = () => { },
                checks =
                {
                    C("all-patch-targets-carry-our-owner", () =>
                    {
                        // Derived from the assembly's own [HarmonyPatch] classes — a
                        // hardcoded list lagged the 16th patch within one round of being
                        // written, exactly as predicted when it was reviewed.
                        var missing = new List<string>();
                        int total = 0;
                        foreach (Type t in typeof(CESidearmsSupply.SupplyMod).Assembly.GetTypes())
                        {
                            var attrs = t.GetCustomAttributes(typeof(HarmonyPatch), inherit: true);
                            if (attrs.Length == 0)
                            {
                                continue;
                            }
                            var info = HarmonyMethod.Merge(attrs.Cast<HarmonyPatch>()
                                .Select(a => a.info).ToList());
                            if (info.declaringType == null || info.methodName == null)
                            {
                                continue;
                            }
                            total++;
                            System.Reflection.MethodBase m = null;
                            try
                            {
                                m = AccessTools.Method(info.declaringType, info.methodName,
                                                       info.argumentTypes);
                            }
                            catch { }
                            if (m == null)
                            {
                                missing.Add(t.Name + " (target unresolved)");
                                continue;
                            }
                            var patches = Harmony.GetPatchInfo(m);
                            if (patches == null || !patches.Owners.Contains("eebette.CESidearmsSupply"))
                            {
                                missing.Add(t.Name);
                            }
                        }
                        return (missing.Count == 0 && total >= 16,
                                missing.Count == 0
                                    ? $"all {total} declared targets patched"
                                    : "UNPATCHED: " + string.Join(", ", missing));
                    }),
                }
            });

            // Round-5 High: the SS-funnel ban had a CurJob.playerForced exemption — and
            // vanilla stamps that flag on EVERY right-click order, attacks included, while
            // no player equip gesture ever reaches the funnel at all. So the ban switched
            // itself off during player-directed combat. Paired fix: the exclusion is now
            // registered at SELECTION (canUseSidearmInstance, where SS registers
            // bladelink), so the pickers skip the excluded weapon and the runner-up wins
            // instead of the refusal falling through to melee/unarmed.
            phases.Add(new Phase
            {
                label = "an-ordered-job-does-not-unpocket-an-excluded-weapon",
                deadlineTicks = 6000,
                arrange = () =>
                {
                    // MELEE, deliberately: the ranged picker is rewired by the sibling
                    // compat patch's ammo-aware re-run (P03) and scored by CE's DPS table
                    // — three phase designs in a row lost to that machinery. The melee
                    // picker is upstream-pure: no ammo, no sibling patches, same
                    // canUseSidearmInstance gate under test.
                    Baseline(dockie, loadout, gladius, D("MeleeWeapon_Knife"));
                    // SELF-CALIBRATING: ask the picker for its favourite and exclude
                    // exactly that — whichever blade the scorer prefers is the only one
                    // that tempts the old bug.
                    GettersFilters.findBestMeleeWeapon(dockie, out ThingWithComps favNow,
                        includeEquipped: true, includeRangedWithBash: false);
                    orderedSwapFavourite = favNow?.def;
                    if (orderedSwapFavourite != null)
                    {
                        PlayerForgets(dockie, orderedSwapFavourite);
                    }
                    // No melee role, so the picker path is the live one.
                    InGizmo(() => Mem(dockie).UnsetMeleeWeaponPreference());
                },
                mutate = () =>
                {
                    // The shape of every player order: TryTakeOrderedJob stamps playerForced.
                    // A REAL destination — ordering the pawn to its own tile ends the job
                    // in the same tick and no forced job is standing for the swap.
                    IntVec3 dest = CellFinder.RandomClosewalkCellNear(dockie.Position, dockie.Map, 4);
                    Verse.AI.Job order = JobMaker.MakeJob(JobDefOf.Goto, dest);
                    dockie.jobs.TryTakeOrderedJob(order);
                    orderedSwapJobWasForced = dockie.CurJob?.playerForced ?? false;
                    // Empty the hands first: SS can arm the runner-up in the arrange→mutate
                    // gap, and a swap to a blade ALREADY held legitimately does not move
                    // (SS early-returns on Primary == pick) — the movement requirement
                    // then false-reds the fixed tree. From empty hands, fixed = the
                    // runner-up is equipped (movement), broken = nothing is (the funnel
                    // refused the nominated favourite and the tree fell through).
                    ThingWithComps held = dockie.equipment?.Primary;
                    if (held != null)
                    {
                        dockie.equipment.TryTransferEquipmentToContainer(held, dockie.inventory.innerContainer);
                        dockie.TryGetComp<CombatExtended.CompInventory>()?.UpdateInventory();
                    }
                    ThingDef primaryBefore = dockie.equipment?.Primary?.def;
                    // SS's re-arm entry point, driven in melee mode: with the old
                    // playerForced exemption this equipped the excluded blade; with
                    // selection-level registration the other blade wins.
                    WeaponAssingment.equipBestWeaponFromInventoryByPreference(
                        dockie, PeteTimesSix.SimpleSidearms.Utilities.Enums.DroppingModeEnum.Calm,
                        PeteTimesSix.SimpleSidearms.Utilities.Enums.PrimaryWeaponMode.Melee);
                    GettersFilters.findBestMeleeWeapon(dockie, out ThingWithComps sawNow,
                        includeEquipped: true, includeRangedWithBash: false);
                    orderedSwapPickerSaw = sawNow?.def?.defName ?? "null";
                    ThingDef primaryNow = dockie.equipment?.Primary?.def;
                    orderedSwapSkippedExcluded = primaryNow != orderedSwapFavourite;
                    // The runner-up must be the SWAP's doing: without the pre-act sample,
                    // an already-wielded runner-up satisfied this with the selection
                    // patch deleted (the funnel blocked the nomination and nothing moved).
                    orderedSwapPickedRunnerUp = primaryNow != null && primaryNow.IsMeleeWeapon
                        && primaryNow != orderedSwapFavourite && primaryNow != primaryBefore;
                },
                checks =
                {
                    P("the-pickers-favourite-is-excluded-and-carried", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = orderedSwapFavourite != null && rec != null
                            && rec.dontEquip.Any(pr => pr.thing == orderedSwapFavourite);
                        bool carried = orderedSwapFavourite != null
                            && dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                                .Any(w => w.def == orderedSwapFavourite);
                        return (excluded && carried,
                                $"favourite={orderedSwapFavourite?.defName ?? "none"} excluded={excluded} carried={carried}");
                    }),
                    C("the-order-was-player-forced", () =>
                    {
                        return (orderedSwapJobWasForced, $"playerForced={orderedSwapJobWasForced}");
                    }),
                    C("the-excluded-weapon-was-not-wielded", () =>
                    {
                        return (orderedSwapSkippedExcluded, $"skipped={orderedSwapSkippedExcluded}");
                    }),
                    C("the-picker-no-longer-sees-the-favourite", () =>
                    {
                        bool skipped = orderedSwapPickerSaw != (orderedSwapFavourite?.defName ?? "?");
                        return (skipped, $"picker-saw={orderedSwapPickerSaw}");
                    }),
                    C("the-runner-up-blade-won", () =>
                    {
                        // The refusal must not fall through to unarmed — the second-best
                        // BLADE takes the slot, and the swap itself must have moved it.
                        return (orderedSwapPickedRunnerUp,
                                $"runner-up equipped={orderedSwapPickedRunnerUp} "
                                + $"picker-saw={orderedSwapPickerSaw} "
                                + $"primary={dockie.equipment?.Primary?.def?.defName ?? "none"}");
                    }),
                }
            });

            // Round-5 ruling: exclusions and role vetoes belong to the loadout
            // ASSIGNMENT. Any change of assignment clears them all — they are fabricated
            // per-pawn rules with no review UI, ephemeral by design, and they do NOT come
            // back when the old loadout does.
            phases.Add(new Phase
            {
                label = "switching-loadouts-clears-exclusions-and-vetoes",
                deadlineTicks = 6000,
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    PlayerForgets(dockie, pistol);
                    PlayerClearsRangedRole(dockie);
                },
                mutate = () =>
                {
                    var other = new Loadout("supply-test-other");
                    LoadoutManager.AddLoadout(other);
                    try
                    {
                        dockie.SetLoadout(other);
                        ForceReconcile(dockie);
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        loadoutSwitchClearedAll = rec != null && rec.dontEquip.Count == 0
                            && !rec.rangedRoleVetoed && !rec.meleeRoleVetoed;
                        dockie.SetLoadout(loadout);
                        ForceReconcile(dockie);
                        loadoutSwitchStayedClear = rec != null && rec.dontEquip.Count == 0;
                    }
                    finally
                    {
                        LoadoutManager.RemoveLoadout(other);
                        dockie.SetLoadout(loadout);
                        ForceReconcile(dockie);
                    }
                },
                checks =
                {
                    P("an-exclusion-and-a-veto-exist", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excl = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        bool veto = rec != null && rec.rangedRoleVetoed;
                        return (excl && veto, $"excluded={excl} vetoed={veto}");
                    }),
                    C("the-assignment-change-cleared-everything", () =>
                    {
                        return (loadoutSwitchClearedAll, $"cleared={loadoutSwitchClearedAll}");
                    }),
                    C("returning-does-not-revive-them", () =>
                    {
                        return (loadoutSwitchStayedClear, $"still clear={loadoutSwitchStayedClear}");
                    }),
                    C("the-pistol-is-claimed-again", () =>
                    {
                        // The positive control: with the exclusion gone, the row claims it.
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool claimed = rec != null && rec.claimed.Any(pr => pr.thing == pistol);
                        return (claimed, $"claimed={claimed}");
                    }),
                }
            });

            // Outfit stands: the click only queues a job — the equip happens minutes
            // later inside JobDriver_UseOutfitStand, outside every scope, and SS's
            // remember-on-equip hook does not cover that driver either. The recorder
            // accepts that one job def's playerForced flag as player context (the think
            // tree never issues it). Driven at the recorder's contract level: the real
            // stand flow is a manual test (TESTPLAN).
            if (JobDefOf.UseOutfitStand != null)
            {
            phases.Add(new Phase
            {
                label = "equipping-from-an-outfit-stand-withdraws-the-exclusion",
                deadlineTicks = 4000,
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    PlayerForgets(dockie, pistol);
                },
                mutate = () =>
                {
                    var jobField = AccessTools.Field(typeof(Verse.AI.Pawn_JobTracker), "curJob");
                    Verse.AI.Job old = dockie.CurJob;
                    ThingWithComps pist = dockie.inventory.innerContainer.OfType<ThingWithComps>()
                        .FirstOrDefault(t => t.def == pistol);
                    ThingWithComps oldPrimary = dockie.equipment?.Primary;
                    if (pist == null || jobField == null)
                    {
                        return;
                    }
                    Verse.AI.Job standJob = JobMaker.MakeJob(JobDefOf.UseOutfitStand);
                    standJob.playerForced = true;
                    jobField.SetValue(dockie.jobs, standJob);
                    try
                    {
                        if (oldPrimary != null)
                        {
                            dockie.equipment.TryTransferEquipmentToContainer(oldPrimary,
                                dockie.inventory.innerContainer);
                        }
                        dockie.inventory.innerContainer.Remove(pist);
                        dockie.equipment.AddEquipment(pist);
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        standEquipWithdrew = rec != null && !rec.dontEquip.Any(pr => pr.thing == pistol);
                    }
                    finally
                    {
                        jobField.SetValue(dockie.jobs, old);
                        dockie.TryGetComp<CombatExtended.CompInventory>()?.UpdateInventory();
                    }
                    ForceReconcile(dockie);
                },
                checks =
                {
                    P("the-curjob-field-still-resolves", () =>
                    {
                        bool ok = AccessTools.Field(typeof(Verse.AI.Pawn_JobTracker), "curJob") != null;
                        return (ok, $"resolves={ok}");
                    }),
                    P("pistol-starts-excluded", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(pr => pr.thing == pistol);
                        return (excluded, $"excluded={excluded}");
                    }),
                    C("the-stand-equip-withdrew-the-exclusion", () =>
                    {
                        return (standEquipWithdrew, $"withdrawn={standEquipWithdrew}");
                    }),
                }
            });
            }
            else
            {
                // Outfit stands are DLC content; without the def the recorder clause is
                // inert by construction and there is nothing to drive.
                Log.Message("[SupplyTest] UseOutfitStand def absent — stand phase skipped.");
            }

            // Release() hands claims back but must NOT touch a pair the player forced:
            // forgetting a pair's last copy clears the force as an SS side effect, with
            // nothing to tell the player. The skip has existed since the sweep was
            // written; until this phase, deleting it changed no verdict anywhere.
            phases.Add(new Phase
            {
                label = "a-forced-pair-survives-the-release",
                deadlineTicks = 4000,
                arrange = () =>
                {
                    Baseline(dockie, loadout, sniper, shotgun, pistol, gladius);
                    InGizmo(() => Mem(dockie).SetWeaponAsForced(
                        new ThingDefStuffDefPair(shotgun, null), drafted: false));
                },
                mutate = () =>
                {
                    try
                    {
                        CESidearmsSupply.SupplyMod.Release(interactive: true);
                        var mem = Mem(dockie);
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        forcedPairSurvivedRelease =
                            mem.ForcedWeapon?.thing == shotgun
                            && mem.RememberedWeapons.Any(pr => pr.thing == shotgun);
                        releaseTookTheRest = rec != null
                            && rec.claimed.Any(pr => pr.thing == shotgun)
                            && rec.claimed.All(pr => pr.thing == shotgun)
                            && !mem.RememberedWeapons.Any(pr => pr.thing == sniper);
                    }
                    finally
                    {
                        InGizmo(() => Mem(dockie).UnsetForcedWeapon(drafted: false));
                    }
                    ForceReconcile(dockie);
                },
                checks =
                {
                    P("the-shotgun-is-forced-and-claimed", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool forced = Mem(dockie).ForcedWeapon?.thing == shotgun;
                        bool claimed = rec != null && rec.claimed.Any(pr => pr.thing == shotgun);
                        return (forced && claimed, $"forced={forced} claimed={claimed}");
                    }),
                    C("the-force-and-its-memory-survive-the-sweep", () =>
                    {
                        return (forcedPairSurvivedRelease, $"survived={forcedPairSurvivedRelease}");
                    }),
                    C("everything-unforced-was-released", () =>
                    {
                        return (releaseTookTheRest, $"rest released={releaseTookTheRest}");
                    }),
                }
            });

            // Round-6 P2: a gesture made right after reassigning a loadout must be
            // recorded under the NEW assignment, not destroyed by the pending
            // per-assignment clear ~20 seconds later. The recorders sync the assignment
            // stamp before writing.
            phases.Add(new Phase
            {
                label = "a-gesture-right-after-a-reassignment-survives",
                deadlineTicks = 6000,
                minTicks = 600,
                poll = () => ForceReconcile(dockie),
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    var fresh = new Loadout("supply-test-fresh");
                    LoadoutManager.AddLoadout(fresh);
                    try
                    {
                        foreach (ThingDef d in new[] { sniper, shotgun, pistol, gladius })
                        {
                            fresh.AddSlot(new LoadoutSlot(d, 1));
                        }
                        // Assign, then IMMEDIATELY gesture — no reconcile in between: the
                        // record still carries the old assignment's stamp when the forget
                        // hook runs, which is exactly the destroyed-gesture window.
                        dockie.SetLoadout(fresh);
                        PlayerForgets(dockie, pistol);
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        gestureAfterAssignSurvived = rec != null
                            && rec.dontEquip.Any(pr => pr.thing == pistol);
                    }
                    finally
                    {
                        dockie.SetLoadout(loadout);
                        LoadoutManager.RemoveLoadout(fresh);
                        ForceReconcile(dockie);
                    }
                },
                checks =
                {
                    C("the-gesture-was-recorded-under-the-new-assignment", () =>
                    {
                        return (gestureAfterAssignSurvived, $"survived={gestureAfterAssignSurvived}");
                    }),
                }
            });

            // Round-6 P3: CE reuses loadout ids (max-plus-one over survivors) and loadout
            // surgery happens paused, so deleting a loadout and recreating one that
            // inherits the dead id must not resurrect the dead loadout's rules. The
            // deletion itself is observed (RemoveLoadout postfix).
            phases.Add(new Phase
            {
                label = "a-recreated-loadout-does-not-inherit-a-dead-ones-exclusions",
                deadlineTicks = 6000,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    var doomed = new Loadout("supply-test-doomed");
                    LoadoutManager.AddLoadout(doomed);
                    Loadout reborn = null;
                    try
                    {
                        foreach (ThingDef d in new[] { sniper, pistol })
                        {
                            doomed.AddSlot(new LoadoutSlot(d, 1));
                        }
                        dockie.SetLoadout(doomed);
                        // Stamp the doomed assignment BEFORE recording the gesture — the
                        // A leg otherwise loses the exclusion to the destroyed-gesture bug
                        // (a different finding) before the id-reuse scenario even starts.
                        ForceReconcile(dockie);
                        PlayerForgets(dockie, pistol);
                        int deadId = doomed.UniqueID;
                        // The paused-surgery window: delete, recreate, reassign, with NO
                        // reconcile anywhere in between.
                        LoadoutManager.RemoveLoadout(doomed);
                        reborn = new Loadout("supply-test-reborn");
                        LoadoutManager.AddLoadout(reborn);
                        foreach (ThingDef d in new[] { sniper, pistol })
                        {
                            reborn.AddSlot(new LoadoutSlot(d, 1));
                        }
                        if (reborn.UniqueID == deadId)
                        {
                            dockie.SetLoadout(reborn);
                            ForceReconcile(dockie);
                            var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                            reusedIdKeptRules = rec != null
                                && rec.dontEquip.Any(pr => pr.thing == pistol);
                        }
                        else
                        {
                            // CE stopped reusing ids — the hazard is gone by upstream
                            // change; record that rather than failing.
                            reusedIdKeptRules = false;
                        }
                    }
                    finally
                    {
                        dockie.SetLoadout(loadout);
                        if (reborn != null)
                        {
                            LoadoutManager.RemoveLoadout(reborn);
                        }
                        ForceReconcile(dockie);
                    }
                },
                checks =
                {
                    C("the-dead-loadouts-rules-do-not-govern-the-reborn-one", () =>
                    {
                        return (!reusedIdKeptRules, $"stale rules survived={reusedIdKeptRules}");
                    }),
                }
            });

            // The positive control for the job downgrade: a NON-excluded fetch with an
            // empty primary must end WIELDED via CE's own Equip branch. Without this, an
            // over-broad downgrade (every fetch becomes a haul, pawns never auto-wield)
            // survived the whole suite — the refused-to-machine phase self-arms from
            // inventory and masked it.
            phases.Add(new Phase
            {
                label = "a-non-excluded-fetch-is-wielded-by-the-machine",
                deadlineTicks = 25000,
                minTicks = 300,
                poll = () => ForceReconcile(dockie),
                arrange = () => Baseline(dockie, loadout, sniper),
                mutate = () =>
                {
                    dockie.equipment.DestroyAllEquipment();
                    foreach (ThingWithComps w in dockie.inventory.innerContainer
                                 .OfType<ThingWithComps>().Where(t => t.def == sniper).ToList())
                    {
                        dockie.inventory.innerContainer.Remove(w);
                        w.Destroy();
                    }
                    dockie.TryGetComp<CombatExtended.CompInventory>()?.UpdateInventory();
                    GenSpawn.Spawn(ThingMaker.MakeThing(sniper),
                        CellFinder.RandomClosewalkCellNear(dockie.Position, dockie.Map, 4),
                        dockie.Map);
                },
                checks =
                {
                    C("ce-wields-the-fetched-sniper", () =>
                    {
                        bool wielded = dockie.equipment?.Primary?.def == sniper;
                        return (wielded, $"primary={dockie.equipment?.Primary?.def?.defName ?? "none"}");
                    }),
                }
            });

            // Standing invariant, appended to every phase: nothing is ever both excluded
            // and remembered. A pair on both lists means a machine path wrote SS memory
            // back behind the recorder — the permanent-leak state the self-heal exists to
            // clear. HONEST SCOPE: this holds at poll boundaries only because any phase
            // that lands a machine equip also drives a reconcile in its poll (the
            // machine-equip phase does) — the self-heal runs before the checks see the
            // transient. A future phase that lands a machine equip WITHOUT a reconciling
            // poll will trip this on a correct product; give it one, or expect ~1300
            // ticks of legitimate transient. It is also one-eyed by design: it cannot
            // see the symmetric leak (an exclusion lost while the memory stays).
            foreach (Phase phase in phases)
            {
                phase.checks.Add(N("no-pair-is-both-excluded-and-remembered", () =>
                {
                    var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                    if (rec == null || rec.dontEquip.Count == 0)
                    {
                        return (true, "no exclusions");
                    }
                    var mem = Mem(dockie);
                    if (mem == null)
                    {
                        return (true, "no memory comp");
                    }
                    var both = mem.RememberedWeapons.Where(pr => rec.dontEquip.Contains(pr)).ToList();
                    return (both.Count == 0, both.Count == 0 ? "clean"
                            : "on both lists: " + string.Join(",", both.Select(pr => pr.thing?.defName)));
                }));
            }

            return phases;
        }
    }
}