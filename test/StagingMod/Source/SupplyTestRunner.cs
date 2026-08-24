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
            public bool passed;
            public string lastDetail = "not evaluated";
        }

        private class Phase
        {
            public string label;
            public Action mutate;
            public List<Check> checks = new List<Check>();
            public int deadlineTicks;
            // Unused since SUPPLY-2 moved to the compat patch. Keep it: the fix for the
            // latching-negative-check bug needs a window a must-not-happen check holds over.
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
                || scenario.NullOrEmpty() || !scenario.StartsWith("supply") || scenario.StartsWith("supplybench"))
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
                label = "preexisting-memory-claimed-by-loadout",
                deadlineTicks = 8000,
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
                    C("ce-free-to-drop-it", () =>
                    {
                        bool excess = Utility_HoldTracker.GetExcessThing(dockie, out Thing dropThing, out int _);
                        bool targeted = excess && dropThing?.def == revolver;
                        bool stillCarried = CarriedWeaponDefs(dockie).Contains(revolver);
                        return (targeted || !stillCarried,
                            $"excess={excess} dropThing={dropThing?.def?.defName ?? "none"} stillCarried={stillCarried}");
                    }),
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
                    C("nothing-shelved-to-restore", () =>
                    {
                        // Deliberately not remembered: the player expressed the choice by
                        // EQUIPPING. Simple Sidearms' retrieval brings a weapon back to the
                        // inventory, not to their hands, so restoring it as the role would be
                        // inferring intent from an automatic action. Equip it again to lead.
                        var rec = CESidearmsSupply.SupplyGameComponent.Instance.GetRecord(dockie, create: false);
                        bool claimed = rec != null && rec.weapons.Contains(playerPick.def);
                        return (!claimed, $"undeclared pick claimed={claimed}");
                    }),
                }
            });

            // "Carry it, but do not wield it": forgetting a DECLARED weapon in SS's gizmo is
            // the only way to say that, and the projection used to re-claim it every pass.
            phases.Add(new Phase
            {
                label = "gizmo-forget-of-declared-weapon-sticks",
                deadlineTicks = 6000,
                mutate = () =>
                {
                    foreach (var pair in Mem(dockie).RememberedWeapons.Where(p => p.thing == pistol).ToList())
                    {
                        Mem(dockie).ForgetSidearmMemory(pair);
                    }
                    ForceReconcile(dockie);
                    ForceReconcile(dockie); // a second pass is where the old code re-claimed it
                },
                checks =
                {
                    C("stays-forgotten", () =>
                    {
                        bool remembered = Mem(dockie).RememberedWeapons.Any(p => p.thing == pistol);
                        return (!remembered, $"pistol remembered={remembered} (player took it out of the list)");
                    }),
                    C("recorded-as-suppressed", () =>
                    {
                        var rec = CESidearmsSupply.SupplyGameComponent.Instance.GetRecord(dockie, create: false);
                        bool sup = rec != null && rec.suppressed.Contains(pistol);
                        return (sup, $"suppressed={sup}");
                    }),
                    C("still-declared-so-ce-keeps-hauling-it", () =>
                    {
                        // Suppression must not touch the loadout: the row is still there, so CE
                        // still carries the weapon. That is the whole point of the distinction.
                        bool declared = loadout.Slots.Any(sl => sl.thingDef == pistol);
                        return (declared, $"pistol still in loadout={declared}");
                    }),
                }
            });

            phases.Add(new Phase
            {
                label = "re-remembering-resumes-management",
                deadlineTicks = 6000,
                mutate = () =>
                {
                    ThingWithComps carriedPistol = dockie.inventory.innerContainer.OfType<ThingWithComps>()
                        .FirstOrDefault(t => t.def == pistol);
                    if (carriedPistol == null)
                    {
                        carriedPistol = (ThingWithComps)ThingMaker.MakeThing(pistol);
                        dockie.inventory.innerContainer.TryAdd(carriedPistol, true);
                    }
                    Mem(dockie).InformOfAddedSidearm(carriedPistol);
                    ForceReconcile(dockie);
                },
                checks =
                {
                    C("suppression-cleared", () =>
                    {
                        var rec = CESidearmsSupply.SupplyGameComponent.Instance.GetRecord(dockie, create: false);
                        bool sup = rec != null && rec.suppressed.Contains(pistol);
                        bool claimed = rec != null && rec.weapons.Contains(pistol);
                        return (!sup && claimed, $"suppressed={sup} claimed={claimed}");
                    }),
                }
            });

            return phases;
        }


            }
}
