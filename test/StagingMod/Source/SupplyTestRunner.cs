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
            // The harness itself.
            "[SupplyTest]",
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

            bool allPass = true;
            bool preconditionsHold = true;
            Check tripped = null;
            foreach (Check check in phase.checks)
            {
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

            loadout._slots.Clear();
            foreach (ThingDef def in rows)
            {
                loadout.AddSlot(new LoadoutSlot(def, 1));
            }

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
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    Mem(dockie).SetWeaponAsForced(new ThingDefStuffDefPair(pistol, null), false);
                    MoveTop(shotgun);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("force-survives-reconcile", () =>
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
                    C("manual-shotgun-survives-template-churn", () =>
                    {
                        bool present = Mem(dockie).RememberedWeapons.Any(p => p.thing == shotgun);
                        return (present, "shotgun remembered=" + present);
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
                    C("carried-player-pick-outranks-loadout", () =>
                    {
                        ThingDef def = Mem(dockie).DefaultRangedWeapon?.thing;
                        return (def == playerPick.def, $"default={def?.defName ?? "none"} want={playerPick.def.defName}");
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "loadout-takes-over-when-pick-is-gone",
                deadlineTicks = 6000,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
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
                        bool claimed = rec != null && rec.claimed.Any(p => p.thing == playerPick.def);
                        return (!claimed, $"undeclared pick claimed={claimed}");
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

                    C("stays-forgotten", () =>
                    {
                        bool remembered = Mem(dockie).RememberedWeapons.Any(p => p.thing == pistol);
                        return (!remembered, $"pistol remembered={remembered} (player took it out of the list)");
                    }),
                    C("recorded-as-player-intent", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(p => p.thing == pistol);
                        return (excluded, $"recorded as do-not-equip={excluded}");
                    }),
                    C("ce-still-hauls-it", () =>
                    {
                        // The old form asserted the row still existed — but this module
                        // contains no code that removes loadout slots, so it passed with the
                        // feature deleted. What matters is the consequence: excluded from the
                        // sidearm list, still carried because CE's row stands.
                        bool declared = loadout.Slots.Any(sl => sl.thingDef == pistol);
                        bool carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Any(w => w.def == pistol);
                        return (declared && carried, $"row={declared} carried={carried}");
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "re-remembering-resumes-management",
                deadlineTicks = 6000,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
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
                    N("sniper-never-recorded-as-player-forgotten", () =>
                    {
                        var rec = CESidearmsSupply.CompLoadoutSidearms.For(dockie);
                        bool excluded = rec != null && rec.dontEquip.Any(p => p.thing == sniper);
                        return (!excluded, $"sniper in dontEquip={excluded}");
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
                    InGizmo(() => WeaponAssingment.DropSidearm(dockie, carried,
                                                              intentionalDrop: true, unmemorise: true));
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
                    P("shotgun-is-carried-again", () =>
                    {
                        // CE hauls it back for the still-declared row. Until it has, this
                        // phase is not yet testing the thing it claims to.
                        var carried = dockie.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                            .Any(w => w.def == shotgun);
                        return (carried, $"carried={carried}");
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
                label = "deleting-the-loadout-does-not-wipe-remembered-sidearms",
                deadlineTicks = 6000,
                minTicks = 600,
                arrange = () => Baseline(dockie, loadout, sniper, shotgun, pistol, gladius),
                mutate = () =>
                {
                    beforeDelete = Mem(dockie).RememberedWeapons.Select(p => p.thing.defName).ToHashSet();
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
                    N("every-remembered-weapon-survives", () =>
                    {
                        // A count cannot fail here: a forced pair and a hand-added memory
                        // both survive the wipe this phase is named for. Assert identity.
                        var now = Mem(dockie).RememberedWeapons.Select(p => p.thing.defName).ToHashSet();
                        var lost = beforeDelete.Where(d => !now.Contains(d)).ToList();
                        return (lost.Count == 0,
                                lost.Count == 0
                                    ? $"all {beforeDelete.Count} still remembered"
                                    : "LOST: " + string.Join(",", lost));
                    }),
                }
            });



            return phases;
        }
    }
}