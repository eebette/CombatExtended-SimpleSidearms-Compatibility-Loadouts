using System.Linq;
using HarmonyLib;
using RimWorld;
using SimpleSidearms.rimworld;
using UnityEngine;
using Verse;

namespace CESimpleSidearmsCompat.Loadouts
{
    public class LoadoutsMod : Mod
    {
        public LoadoutsMod(ModContentPack content) : base(content)
        {
        }

        public override string SettingsCategory()
        {
            return "CE+SS Compatibility - Loadouts";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            // The toggle is per-colony (scribed on the game component).
            LoadoutsSessionComponent comp = Current.Game?.GetComponent<LoadoutsSessionComponent>();
            if (comp == null)
            {
                GUI.color = Color.gray;
                listing.Label("Load a colony to configure — this setting is per-colony.");
                GUI.color = Color.white;
                listing.End();
                return;
            }

            // The checkbox writes scribed sim state, and toggle-off Release() forgets SS memory.
            bool was = comp.loadoutWeaponsAsSidearms;
            listing.CheckboxLabeled("Loadout weapons as sidearms", ref comp.loadoutWeaponsAsSidearms,
                "Weapons listed in a CE loadout are auto-remembered as sidearms by assigned pawns. "
                + "The first ranged weapon in the list becomes the default ranged weapon and the first "
                + "melee the preferred melee weapon. Removing a weapon from the loadout makes the pawn "
                + "forget it as a sidearm, which is what lets CE clear it out of the inventory.");

            // Turning it off also releases state store.
            if (was && !comp.loadoutWeaponsAsSidearms)
            {
                Release(interactive: true);
            }

            listing.Gap();
            if (listing.ButtonText("Release all claimed sidearms",
                                   "Forget every sidearm this mod added, on every colonist, and start "
                                   + "over. Weapons the loadout does not list are not touched."))
            {
                Release(interactive: true);
            }

            listing.Gap();
            listing.Label("Ammo for sidearms is Combat Extended's own job: add the ammo to the loadout and "
                          + "CE keeps the pawn stocked to that count, the same as for any other item.");
            listing.End();
        }

        /// <summary>
        /// Hand back every claimed pair on every colonist in the loaded game.
        /// </summary>
        public static bool Release(bool interactive = false)
        {
            if (Current.Game == null)
            {
                return false;
            }
            int released = 0;
            int deferred = 0;
            foreach (Pawn pawn in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists.ToList())
            {
                CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
                if (rec == null)
                {
                    continue;
                }
                CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
                int n = rec.Release(memory, memory?.ForcedWeapon, memory?.ForcedWeaponWhileDrafted);
                if (n < 0)
                {
                    deferred++; // unspawned; its memory comp is not resolvable yet
                }
                else
                {
                    released += n;
                }
            }
            // Don't release anything not in the Loadout
            if (interactive || released > 0 || deferred > 0)
            {
                string away = deferred == 0 ? ""
                    : $" {deferred} pawn(s) are away; they are released on the next load of this colony after they return.";
                Messages.Message($"[CE+SS Loadouts] Released {released} claimed sidearm(s)." + away,
                                 MessageTypeDefOf.TaskCompletion, historical: false);
            }
            return true;
        }
    }

    /// <summary>
    /// Holds the per-colony feature toggle (scribed into the save) and runs a once-per-load
    /// release sweep for a colony that has the feature off.
    /// </summary>
    public class LoadoutsSessionComponent : GameComponent
    {
        /// <summary>Per-colony feature toggle, scribed into the save. Default on.</summary>
        public bool loadoutWeaponsAsSidearms = true;

        public LoadoutsSessionComponent(Game game)
        {
        }

        /// <summary>Whether the feature is on for the loaded colony.</summary>
        public static bool Enabled
        {
            get
            {
                Game g = Current.Game;
                if (g == null)
                {
                    return true;
                }
                LoadoutsSessionComponent c = g.GetComponent<LoadoutsSessionComponent>();
                return c == null || c.loadoutWeaponsAsSidearms;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref loadoutWeaponsAsSidearms, "loadoutWeaponsAsSidearms", true);
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // Release on a colony whose feature is off.
            if (!loadoutWeaponsAsSidearms)
            {
                LoadoutsMod.Release();
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            bool ceActive = ModsConfig.IsActive("CETeam.CombatExtended");
            bool ssActive = ModsConfig.IsActive("PeteTimesSix.SimpleSidearms");
            if (!ceActive || !ssActive)
            {
                Log.Error("[CE+SS Loadouts] Required mod missing:"
                          + (ceActive ? "" : " Combat Extended")
                          + (ssActive ? "" : " Simple Sidearms")
                          + " — nothing is patched; the mod is inert this session.");
                return;
            }

            // Per-class patching.
            var harmony = new Harmony("eebette.CESimpleSidearmsCompat.Loadouts");
            int failedClasses = 0;
            foreach (System.Type type in typeof(Bootstrap).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), inherit: true).Length == 0)
                {
                    continue;
                }
                try
                {
                    new PatchClassProcessor(harmony, type).Patch();
                }
                catch (System.Exception e)
                {
                    failedClasses++;
                    Log.Error($"[CE+SS Loadouts] {type.Name} failed to patch and is disabled "
                              + $"for this session: {e.Message}");
                }
            }
            Log.Message(failedClasses == 0
                ? "[CE+SS Loadouts] Patches installed."
                : $"[CE+SS Loadouts] Patches installed with {failedClasses} class(es) disabled — see errors above.");

            // Comp-attach canary: if the XML patch stops matching pawn defs, every patch
            // quietly no-ops behind a null comp.
            if (ThingDefOf.Human?.comps?.Any(c => c is CompProperties_LoadoutSidearms) != true)
            {
                Log.Error("[CE+SS Loadouts] CompLoadoutSidearms is not attached to Human — "
                          + "Patches/pawnComp.xml no longer matches the pawn defs and the whole "
                          + "feature is inert.");
            }
        }
    }
}
