using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;

namespace CESupplyTestStaging
{
    /// <summary>
    /// Builds the SUPPLY-* staged saves described in TESTPLAN.md.
    /// Only runs when the game is launched with: -quicktest -cesupplystage
    /// Staging happens while the game is paused (no ticks), so saves capture the
    /// PRE-reconcile state — projection and fetching are observed live after loading.
    /// </summary>
    public class SupplyStagingComponent : GameComponent
    {
        private readonly List<Thing> staged = new List<Thing>();
        private readonly List<Loadout> stagedLoadouts = new List<Loadout>();
        private IntVec3 anchor = IntVec3.Invalid;

        public SupplyStagingComponent(Game game)
        {
        }

        public override void StartedNewGame()
        {
            if (!GenCommandLine.CommandLineArgPassed("cesupplystage"))
            {
                return;
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    StageAll();
                }
                catch (Exception e)
                {
                    Log.Error("[SupplyStaging] Staging failed: " + e);
                }
            });
        }

        private void StageAll()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                Log.Error("[SupplyStaging] No current map; launch with -quicktest -cesupplystage.");
                return;
            }
            anchor = ComputeAnchor(map);
            Log.Message($"[SupplyStaging] Map {map.Size}, staging anchor {anchor}.");

            Stage1_LoadoutSidearms(map);
            SaveAndReset("SUPPLY-1-loadout-sidearms");

            Find.TickManager.Pause();
            Log.Message("[SupplyStaging] All SUPPLY saves created.");
            Find.LetterStack.ReceiveLetter("SUPPLY saves created",
                "Staged save written: SUPPLY-1-loadout-sidearms.\n\nQuit to main menu and Load it, then UNPAUSE and watch the reconcile happen. See TESTPLAN.md.",
                LetterDefOf.PositiveEvent);

            // -cesupplystage is an automated run by definition; waiting for a human to quit
            // meant an unattended stage-then-assert chain idled here forever with the saves
            // already written. Pass -cesupplystagehold to keep the game open and look around.
            if (!GenCommandLine.CommandLineArgPassed("cesupplystagehold"))
            {
                Log.Message("[SupplyStaging] Staging complete; shutting down.");
                LongEventHandler.ExecuteWhenFinished(Root.Shutdown);
            }
        }

        // ---- scenarios -----------------------------------------------------

        // Loadout-weapons-as-sidearms + ammo sustainment + suppression + stuff fix-up.
        // Unarmed colonist, loadout listing sniper > shotgun > pistol > gladius,
        // plus one EXPLICIT ammo row (sniper's caliber, odd count 10) that must
        // suppress the derived demand. Weapons and ammo piles on the ground so
        // every fetch can actually complete. Two gladius stuff variants for the
        // pair fix-up check.
        private void Stage1_LoadoutSidearms(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Dockie", new IntVec3(-4, 0, 0));

            ThingDef sniper = Need("Gun_SniperRifle");
            ThingDef shotgun = Need("Gun_PumpShotgun");
            ThingDef pistol = Need("Gun_Autopistol");
            ThingDef gladius = Need("MeleeWeapon_Gladius");

            SpawnNear(map, anchor, sniper, null);
            SpawnNear(map, anchor, shotgun, null);
            SpawnNear(map, anchor, pistol, null);
            SpawnNear(map, anchor, gladius, ThingDefOf.Steel);
            SpawnNear(map, anchor, gladius, ThingDefOf.Plasteel);

            foreach (ThingDef weapon in new[] { sniper, shotgun, pistol })
            {
                ThingDef ammo = FirstAmmoOf(weapon);
                if (ammo != null)
                {
                    SpawnStack(map, anchor, ammo, 200);
                }
            }

            var loadout = new Loadout("SUPPLY loadout-sidearms test");
            loadout.adHoc = true;    // CE's own weapon-implies-ammo opt-in; extended by mod 2 to declared weapons
            loadout.adHocMags = 2;
            loadout.AddSlot(new LoadoutSlot(sniper, 1));
            loadout.AddSlot(new LoadoutSlot(shotgun, 1));
            loadout.AddSlot(new LoadoutSlot(pistol, 1));
            loadout.AddSlot(new LoadoutSlot(gladius, 1));
            ThingDef sniperAmmo = FirstAmmoOf(sniper);
            if (sniperAmmo != null)
            {
                loadout.AddSlot(new LoadoutSlot(sniperAmmo, 10)); // explicit row: must win over derived demand
            }
            LoadoutManager.AddLoadout(loadout);
            stagedLoadouts.Add(loadout);
            pawn.SetLoadout(loadout);
        }

        // Simple Sidearms fetches remembered-but-uncarried weapons on its own
        // (JobGiver_RetrieveWeapon, vanilla think tree, on by default) without consulting
        // CE's capacity model. Two colonists remember a pistol neither is carrying:
        // "Roomy" has space for it, "Stuffed" is loaded until CE reports no room. SS wants
        // to fetch for both; only Roomy should end up with one.
        
        /// <summary>
        /// Load the pawn with cargo until CE reports no room for another weapon — the state
        /// SS's own retrieval ignores. Steel reaches the weight cap first, which is fine:
        /// CanFitInInventory refuses on either limit, and the runner asserts the refusal
        /// rather than assuming which one bound.
        /// </summary>
        
        // ---- helpers -------------------------------------------------------

        
        private static ThingDef FirstAmmoOf(ThingDef weapon)
        {
            return weapon.GetCompProperties<CompProperties_AmmoUser>()?.ammoSet?.ammoTypes?.FirstOrDefault()?.ammo;
        }

        private static ThingDef Need(string defName)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                throw new InvalidOperationException("[SupplyStaging] Missing def: " + defName);
            }
            return def;
        }

        private void SaveAndReset(string name)
        {
            GameDataSaveLoader.SaveGame(name);
            foreach (Thing thing in staged)
            {
                if (thing is Pawn pawn)
                {
                    LoadoutManager._current?._assignedLoadouts?.Remove(pawn);
                    LoadoutManager._current?._assignedTrackers?.Remove(pawn);
                }
                if (thing != null && !thing.Destroyed)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
            staged.Clear();
            // Loadout DEFINITIONS stay for the already-written save; remove them from the
            // live manager so the next scenario's save doesn't accumulate them.
            foreach (Loadout loadout in stagedLoadouts)
            {
                LoadoutManager._current?._loadouts?.Remove(loadout);
            }
            stagedLoadouts.Clear();
        }

        private Pawn SpawnColonist(Map map, string nick, IntVec3 offset)
        {
            var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                          PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                          canGeneratePawnRelations: false, colonistRelationChanceFactor: 0f);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            pawn.Name = new NameTriple("Test", nick, "SUPPLY");
            pawn.equipment?.DestroyAllEquipment();
            pawn.inventory?.DestroyAll();
            GenSpawn.Spawn(pawn, FindCell(map, anchor + offset), map);
            staged.Add(pawn);
            return pawn;
        }

        private void SpawnNear(Map map, IntVec3 near, ThingDef def, ThingDef stuff)
        {
            Thing thing = ThingMaker.MakeThing(def, stuff ?? (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null));
            GenSpawn.Spawn(thing, FindCell(map, near + new IntVec3(3, 0, 3)), map);
            staged.Add(thing);
        }

        private void SpawnStack(Map map, IntVec3 near, ThingDef def, int count)
        {
            Thing thing = ThingMaker.MakeThing(def);
            thing.stackCount = count;
            GenSpawn.Spawn(thing, FindCell(map, near + new IntVec3(3, 0, -3)), map);
            staged.Add(thing);
        }

        private static IntVec3 ComputeAnchor(Map map)
        {
            bool Valid(IntVec3 c) => c.Standable(map) && !c.Fogged(map);
            if (CellFinder.TryFindRandomCellNear(map.Center, map, 30, Valid, out IntVec3 cell))
            {
                return cell;
            }
            if (CellFinderLoose.TryGetRandomCellWith(Valid, map, 1000, out cell))
            {
                return cell;
            }
            foreach (IntVec3 c in map.AllCells)
            {
                if (c.Standable(map))
                {
                    return c;
                }
            }
            return map.Center;
        }

        private IntVec3 FindCell(Map map, IntVec3 near)
        {
            IntVec3 root = near.ClampInsideMap(map);
            if (CellFinder.TryFindRandomCellNear(root, map, 20, c => c.Standable(map) && !c.Fogged(map), out IntVec3 cell))
            {
                return cell;
            }
            return anchor;
        }
    }
}
