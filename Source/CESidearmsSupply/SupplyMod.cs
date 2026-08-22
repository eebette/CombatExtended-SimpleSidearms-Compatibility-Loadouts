using HarmonyLib;
using UnityEngine;
using Verse;

namespace CESidearmsSupply
{
    public class SupplySettings : ModSettings
    {
        public bool loadoutWeaponsAsSidearms = true;
        public bool ammoForAllRemembered = false;
        public bool capacityAwareRetrieval = true;
        public int spareMagazines = 2;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref loadoutWeaponsAsSidearms, "loadoutWeaponsAsSidearms", true);
            Scribe_Values.Look(ref ammoForAllRemembered, "ammoForAllRemembered", false);
            Scribe_Values.Look(ref capacityAwareRetrieval, "capacityAwareRetrieval", true);
            Scribe_Values.Look(ref spareMagazines, "spareMagazines", 2);
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
            listing.Label("Ammo for loadout-declared weapons: controlled per loadout by CE's own \"Ad hoc\" checkbox. Ticked, it extends from the equipped primary to every weapon declared in that loadout, at the loadout's magazine count. Unticked = vanilla CE behavior (no ammo rows, no ammo).");
            listing.CheckboxLabeled("Ammo for ALL remembered weapons", ref Settings.ammoForAllRemembered,
                "Full automation: every SS-remembered weapon (including battlefield pickups) derives ammo demand at the spare-magazine count below. Off by default — incidental memories should not drain the ammo economy.");
            listing.CheckboxLabeled("Don't fetch sidearms that won't fit", ref Settings.capacityAwareRetrieval,
                "Simple Sidearms fetches remembered weapons on its own, without checking CE's weight and bulk limits. This cancels a retrieval CE says the pawn has no room for, instead of letting them haul it back and count it against everything else they carry.");
            listing.Label($"Spare magazines per remembered weapon (full-automation mode): {Settings.spareMagazines}");
            Settings.spareMagazines = Mathf.RoundToInt(listing.Slider(Settings.spareMagazines, 0f, 10f));
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
