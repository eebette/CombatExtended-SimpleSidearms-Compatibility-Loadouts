using HarmonyLib;
using UnityEngine;
using Verse;

namespace CESidearmsSupply
{
    public class SupplySettings : ModSettings
    {
        public bool doctrineProjection = true;
        public bool ammoForDoctrine = true;
        public bool ammoForAllRemembered = false;
        public bool refetchAllRemembered = false;
        public int spareMagazines = 2;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref doctrineProjection, "doctrineProjection", true);
            Scribe_Values.Look(ref ammoForDoctrine, "ammoForDoctrine", true);
            Scribe_Values.Look(ref ammoForAllRemembered, "ammoForAllRemembered", false);
            Scribe_Values.Look(ref refetchAllRemembered, "refetchAllRemembered", false);
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
            listing.CheckboxLabeled("Doctrine projection", ref Settings.doctrineProjection,
                "Weapons listed in a CE loadout are auto-remembered as sidearms by assigned pawns. First weapon in the list becomes the main.");
            listing.CheckboxLabeled("Ammo for loadout-declared weapons", ref Settings.ammoForDoctrine,
                "Weapons declared in the loadout derive spare-magazine ammo demand automatically. Hand-added caliber rows override this per ammo type; curated ammo rows for other purposes are never touched.");
            listing.CheckboxLabeled("Ammo for ALL remembered weapons", ref Settings.ammoForAllRemembered,
                "Full automation: every SS-remembered weapon (including battlefield pickups) derives ammo demand. Off by default — incidental memories should not drain the ammo economy.");
            listing.CheckboxLabeled("Refetch ALL remembered weapons", ref Settings.refetchAllRemembered,
                "A remembered weapon that goes missing is fetched again from storage. Loadout-declared weapons already refetch natively; this extends it to manually remembered ones. Off by default.");
            listing.Label($"Spare magazines per weapon: {Settings.spareMagazines}");
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
