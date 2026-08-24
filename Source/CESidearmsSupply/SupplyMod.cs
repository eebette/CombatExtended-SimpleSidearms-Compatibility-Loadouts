using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CESidearmsSupply
{
    public class SupplySettings : ModSettings
    {
        public bool loadoutWeaponsAsSidearms = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref loadoutWeaponsAsSidearms, "loadoutWeaponsAsSidearms", true);
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

            // Turning it off has to undo it, not freeze it. The compat patch exempts every
            // remembered weapon from CE's drop, so memories left behind with nobody to release
            // them would pin weapons in inventories with no way back short of the gizmo.
            if (was && !Settings.loadoutWeaponsAsSidearms)
            {
                Release();
            }

            listing.Gap();
            if (SupplyGameComponent.Instance != null
                && listing.ButtonText("Release all claimed sidearms", "Forget every sidearm this mod "
                                      + "added, on every pawn, and start over. Weapons you added by hand "
                                      + "are not touched."))
            {
                Release();
            }

            listing.Gap();
            listing.Label("Ammo for sidearms is Combat Extended's own job: add the ammo to the loadout and "
                          + "CE keeps the pawn stocked to that count, the same as for any other item.");
            listing.End();
        }

        private static void Release()
        {
            SupplyGameComponent comp = SupplyGameComponent.Instance;
            if (comp == null)
            {
                return; // no game loaded; nothing was ever claimed
            }
            int released = comp.ReleaseAll();
            Messages.Message($"[Sidearms&Supply] Released {released} claimed sidearm(s).",
                             MessageTypeDefOf.TaskCompletion, historical: false);
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            new Harmony("eebette.CESidearmsSupply").PatchAll(typeof(Bootstrap).Assembly);
            Log.Message("[Sidearms&Supply] Patches installed.");
        }
    }
}
