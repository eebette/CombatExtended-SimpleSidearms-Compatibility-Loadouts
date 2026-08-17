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
    ///   -celoadsave=SUPPLY-2-refetch          -ceassert=supply2
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
                || scenario.NullOrEmpty() || !scenario.StartsWith("supply"))
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
            public bool passed;
            public string lastDetail = "not evaluated";
        }

        private class Phase
        {
            public string label;
            public Action mutate;
            public List<Check> checks = new List<Check>();
            public int deadlineTicks;
            public int minTicks; // phase cannot complete before this — observation window for informational checks
            public bool failed;
        }

        private List<Phase> phases;
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
                || scenario.NullOrEmpty() || !scenario.StartsWith("supply"))
            {
                return;
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    phases = BuildScenario(scenario);
                }
                catch (Exception e)
                {
                    Log.Error("[SupplyTest] Scenario build failed: " + e);
                    WriteResults(crashed: e.ToString());
                    Root.Shutdown();
                    return;
                }
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

            Phase phase = phases[phaseIndex];
            bool allPass = true;
            foreach (Check check in phase.checks)
            {
                // Informational checks re-evaluate until the phase ends (their last
                // observation is what gets reported) and never gate advancement.
                if (check.passed && !check.informational)
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
                phase.failed = true;
                Log.Warning($"[SupplyTest] Phase '{phase.label}' FAILED (deadline {phase.deadlineTicks} ticks).");
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
                phase.mutate?.Invoke();
            }
            catch (Exception e)
            {
                Log.Error($"[SupplyTest] Mutation for phase '{phase.label}' threw: " + e);
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
            bool overall = crashed == null && phases != null && phases.All(p => !p.failed);
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
                    sb.Append($"      \"passed\": {((!p.failed) ? "true" : "false")},\n");
                    sb.Append($"      \"reached\": {(i <= phaseIndex ? "true" : "false")},\n");
                    sb.Append("      \"checks\": [\n");
                    for (int j = 0; j < p.checks.Count; j++)
                    {
                        Check c = p.checks[j];
                        sb.Append("        {");
                        sb.Append($"\"name\": \"{Escape(c.name)}\", ");
                        sb.Append($"\"passed\": {(c.passed ? "true" : "false")}, ");
                        sb.Append($"\"informational\": {(c.informational ? "true" : "false")}, ");
                        sb.Append($"\"detail\": \"{Escape(c.lastDetail)}\"");
                        sb.Append("}");
                        sb.Append(j < p.checks.Count - 1 ? ",\n" : "\n");
                    }
                    sb.Append("      ]\n");
                    sb.Append(i < phases.Count - 1 ? "    },\n" : "    }\n");
                }
            }
            sb.Append("  ]\n}\n");
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, $"test-results-{scenario}.json");
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
                case "supply2": return BuildSupply2();
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

        private static int MagOf(ThingDef weapon)
        {
            var props = weapon.GetCompProperties<CompProperties_AmmoUser>();
            if (props == null) return 0;
            return props.AmmoGenPerMagOverride > 0 ? props.AmmoGenPerMagOverride
                 : props.magazineSize > 0 ? props.magazineSize : 25;
        }

        private static int CarriedAmmoCount(Pawn pawn, ThingDef weapon)
        {
            List<ThingDef> set = AmmoSetOf(weapon);
            return pawn.inventory.innerContainer.Where(t => set.Contains(t.def)).Sum(t => t.stackCount);
        }

        private static List<ThingDef> CarriedWeaponDefs(Pawn pawn)
        {
            return pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true).Select(w => w.def).ToList();
        }

        private static CompSidearmMemory Mem(Pawn pawn) => CompSidearmMemory.GetMemoryCompForPawn(pawn);

        private static void ForceReconcile(Pawn pawn)
        {
            // Any invocation of TryGiveJob runs the Loadouts module's reconcile prefix;
            // the returned job (if any) is discarded — physical work stays with the
            // pawn's natural think tree.
            new JobGiver_UpdateLoadout().TryGiveJob(pawn);
        }

        private static List<LoadoutSlot> Stream(Pawn pawn)
        {
            return pawn.GetLoadout().GetSlotsFor(pawn).ToList();
        }

        private static int StreamAmmoCount(Pawn pawn, ThingDef weapon)
        {
            List<ThingDef> set = AmmoSetOf(weapon);
            return Stream(pawn).Where(s => s.thingDef != null && set.Contains(s.thingDef)).Sum(s => s.count);
        }

        private static Check C(string name, Func<(bool, string)> eval, bool informational = false)
        {
            return new Check { name = name, eval = eval, informational = informational };
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

            var phases = new List<Phase>();

            phases.Add(new Phase
            {
                label = "initial-reconcile-and-fetch",
                deadlineTicks = 40000,
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
                    C("mode-ranged", () =>
                    {
                        var mode = Mem(dockie).primaryWeaponMode;
                        return (mode == PrimaryWeaponMode.Ranged, "mode=" + mode);
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
                    C("sniper-ammo-exactly-10", () =>
                    {
                        int n = CarriedAmmoCount(dockie, sniper);
                        return (n == 10, $"sniper ammo carried={n} (explicit row must suppress derived 2 mags)");
                    }),
                    C("shotgun-ammo-2-mags", () =>
                    {
                        int n = CarriedAmmoCount(dockie, shotgun);
                        int want = MagOf(shotgun) * 2;
                        return (n == want, $"shotgun ammo carried={n} want={want}");
                    }),
                    C("pistol-ammo-2-mags", () =>
                    {
                        int n = CarriedAmmoCount(dockie, pistol);
                        int want = MagOf(pistol) * 2;
                        return (n == want, $"pistol ammo carried={n} want={want}");
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "reorder-shotgun-top",
                deadlineTicks = 6000,
                mutate = () => { MoveTop(shotgun); ForceReconcile(dockie); },
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
                label = "manual-override-sticks",
                deadlineTicks = 6000,
                mutate = () =>
                {
                    Mem(dockie).SetRangedWeaponTypeAsDefault(new ThingDefStuffDefPair(pistol, null));
                    MoveTop(sniper);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("default-ranged-stays-pistol", () =>
                    {
                        ThingDef def = Mem(dockie).DefaultRangedWeapon?.thing;
                        return (def == pistol, "defaultRanged=" + (def?.defName ?? "null"));
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "template-forget",
                deadlineTicks = 6000,
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
                mutate = () =>
                {
                    // Re-remember shotgun MANUALLY (not template-tracked), then churn the
                    // template (remove + re-add pistol) — manual memory must survive.
                    Mem(dockie).RememberedWeapons.Add(new ThingDefStuffDefPair(shotgun, null));
                    LoadoutSlot pistolSlot = SlotOf(pistol);
                    loadout.RemoveSlot(pistolSlot);
                    ForceReconcile(dockie);
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
                    C("pistol-re-remembered", () =>
                    {
                        bool present = Mem(dockie).RememberedWeapons.Any(p => p.thing == pistol);
                        return (present, "pistol remembered=" + present);
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "adhoc-untick-parity",
                deadlineTicks = 20000,
                mutate = () => { loadout.adHoc = false; ForceReconcile(dockie); },
                checks =
                {
                    C("stream-no-derived-pistol-ammo", () =>
                    {
                        int n = StreamAmmoCount(dockie, pistol);
                        return (n == 0, $"pistol ammo demand in stream={n}");
                    }),
                    C("stream-sniper-explicit-persists", () =>
                    {
                        int n = StreamAmmoCount(dockie, sniper);
                        return (n == 10, $"sniper ammo demand in stream={n} want=10");
                    }),
                    C("physical-pistol-ammo-dropped", () =>
                    {
                        int n = CarriedAmmoCount(dockie, pistol);
                        return (n == 0, $"pistol ammo carried={n} (excess-drop should shed derived ammo)");
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "adhoc-retick",
                deadlineTicks = 6000,
                mutate = () => { loadout.adHoc = true; ForceReconcile(dockie); },
                checks =
                {
                    C("stream-derived-pistol-ammo-returns", () =>
                    {
                        int n = StreamAmmoCount(dockie, pistol);
                        int want = MagOf(pistol) * loadout.adHocMags;
                        return (n == want, $"pistol ammo demand in stream={n} want={want}");
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "ammo-for-all-remembered",
                deadlineTicks = 6000,
                mutate = () =>
                {
                    CESidearmsSupply.SupplyMod.Settings.ammoForAllRemembered = true;
                    Mem(dockie).RememberedWeapons.Add(new ThingDefStuffDefPair(revolver, null));
                },
                checks =
                {
                    C("stream-revolver-ammo-at-global-count", () =>
                    {
                        int n = StreamAmmoCount(dockie, revolver);
                        int want = MagOf(revolver) * CESidearmsSupply.SupplyMod.Settings.spareMagazines;
                        return (n == want, $"revolver ammo demand in stream={n} want={want}");
                    }),
                }
            });

            return phases;
        }

        // -- SUPPLY-2: refetch of manually remembered, uncarried weapons --

        private List<Phase> BuildSupply2()
        {
            Pawn fetchyLoadout = Colonist("Fetchy-Loadout");
            Pawn fetchyDefault = Colonist("Fetchy-Default");
            ThingDef pistol = D("Gun_Autopistol");

            return new List<Phase>
            {
                new Phase
                {
                    label = "refetch-from-memory",
                    deadlineTicks = 36000,
                    minTicks = 15000, // hold the phase open so the default-loadout pawn's outcome is observed late, not at first poll
                    checks =
                    {
                        C("setting-precondition", () =>
                        {
                            bool on = CESidearmsSupply.SupplyMod.Settings.refetchAllRemembered;
                            return (on, "refetchAllRemembered=" + on);
                        }),
                        C("assigned-loadout-pawn-fetches-pistol", () =>
                        {
                            bool has = CarriedWeaponDefs(fetchyLoadout).Contains(pistol);
                            return (has, "Fetchy-Loadout carries pistol=" + has);
                        }),
                        C("default-loadout-pawn-outcome", () =>
                        {
                            bool has = CarriedWeaponDefs(fetchyDefault).Contains(pistol);
                            return (has, "Fetchy-Default carries pistol=" + has +
                                " (informational: does CE evaluate default-loadout pawns?)");
                        }, informational: true),
                    }
                }
            };
        }
    }
}
