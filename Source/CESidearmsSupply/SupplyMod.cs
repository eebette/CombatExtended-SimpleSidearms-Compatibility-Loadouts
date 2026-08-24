using System.Linq;
using HarmonyLib;
using RimWorld;
using SimpleSidearms.rimworld;
using UnityEngine;
using Verse;

namespace CESidearmsSupply
{
    public class SupplySettings : ModSettings
    {
        public bool loadoutWeaponsAsSidearms = true;

        /// <summary>
        /// Set when the feature is switched off with no game loaded. Settings are global and
        /// records are per-save, so there is nothing to release at that moment — but every
        /// save still holds claims that now have nobody to release them.
        /// </summary>
        public bool releasePending;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref loadoutWeaponsAsSidearms, "loadoutWeaponsAsSidearms", true);
            Scribe_Values.Look(ref releasePending, "releasePending", false);
        }
    }

    public class SupplyMod : Mod
    {
        public static SupplySettings Settings { get; private set; }

        public SupplyMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<SupplySettings>();
        }

        public override string SettingsCategory()
        {
            return "Sidearms & Supply for CE";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            bool was = Settings.loadoutWeaponsAsSidearms;
            listing.CheckboxLabeled("Loadout weapons as sidearms", ref Settings.loadoutWeaponsAsSidearms,
                "Weapons listed in a CE loadout are auto-remembered as sidearms by assigned pawns. "
                + "The first ranged weapon in the list becomes the default ranged weapon and the first "
                + "melee the preferred melee weapon. Removing a weapon from the loadout makes the pawn "
                + "forget it as a sidearm, which is what lets CE clear it out of the inventory.");

            // Turning it off has to undo it, not freeze it: the compat patch exempts every
            // remembered weapon from CE's drop, so claims left behind with nobody to release
            // them pin weapons in inventories with no way back short of the gizmo.
            if (was && !Settings.loadoutWeaponsAsSidearms)
            {
                Release();
            }

            listing.Gap();
            bool inGame = Current.Game != null;
            if (listing.ButtonText("Release all claimed sidearms",
                                   "Forget every sidearm this mod added, on every colonist, and start "
                                   + "over. Weapons the loadout does not list are not touched.")
                && inGame)
            {
                Release();
            }
            if (!inGame)
            {
                GUI.color = Color.gray;
                listing.Label("  (available once a save is loaded — it acts on that colony's pawns)");
                GUI.color = Color.white;
            }

            listing.Gap();
            listing.Label("Ammo for sidearms is Combat Extended's own job: add the ammo to the loadout and "
                          + "CE keeps the pawn stocked to that count, the same as for any other item.");
            listing.End();
        }

        /// <summary>
        /// Hand back every claimed pair on every colonist. Returns false when there is no
        /// game to act on, so the caller can defer.
        /// </summary>
        public static bool Release()
        {
            if (Current.Game == null)
            {
                Settings.releasePending = true;
                Settings.Write();
                Messages.Message("[Sidearms&Supply] No save loaded — claimed sidearms will be released "
                                 + "when you next load one.", MessageTypeDefOf.CautionInput, historical: false);
                return false;
            }
            int released = 0;
            foreach (Pawn pawn in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists.ToList())
            {
                CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
                if (rec != null)
                {
                    released += rec.Release(CompSidearmMemory.GetMemoryCompForPawn(pawn));
                    rec.dontEquip.Clear();
                    rec.rangedRoleVetoed = false;
                    rec.meleeRoleVetoed = false;
                }
            }
            Settings.releasePending = false;
            Settings.Write();
            Messages.Message($"[Sidearms&Supply] Released {released} claimed sidearm(s).",
                             MessageTypeDefOf.TaskCompletion, historical: false);
            return true;
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                new Harmony("eebette.CESidearmsSupply").PatchAll(typeof(Bootstrap).Assembly);
                Log.Message("[Sidearms&Supply] Patches installed.");
            }
            catch (System.Exception e)
            {
                // PatchAll aborts the whole assembly on the first target it cannot resolve.
                // Every patch class has a Prepare() for that, so reaching here means
                // something else — say so rather than dying as a TypeInitializationException.
                Log.Error("[Sidearms&Supply] Patching failed; the mod will do nothing this session. " + e);
            }
        }
    }
}
