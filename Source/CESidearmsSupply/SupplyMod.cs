using HarmonyLib;
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
            listing.CheckboxLabeled("Loadout weapons as sidearms", ref Settings.loadoutWeaponsAsSidearms,
                "Weapons listed in a CE loadout are auto-remembered as sidearms by assigned pawns. First weapon in the list becomes the main.");
            listing.Gap();
            listing.Label("Ammo for sidearms is Combat Extended's own job: add the ammo to the loadout and "
                          + "CE keeps the pawn stocked to that count, the same as for any other item.");
            listing.End();
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
