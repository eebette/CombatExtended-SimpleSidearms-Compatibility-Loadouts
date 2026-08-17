using HarmonyLib;
using UnityEngine;
using Verse;

namespace CESidearmsSupply
{
    public class SupplySettings : ModSettings
    {
        public bool doctrineProjection = true;
        public bool ammoResupply = true;
        public bool weaponRefetch = true;
        public int spareMagazines = 2;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref doctrineProjection, "doctrineProjection", true);
            Scribe_Values.Look(ref ammoResupply, "ammoResupply", true);
            Scribe_Values.Look(ref weaponRefetch, "weaponRefetch", true);
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
            listing.CheckboxLabeled("Ammo sustainment", ref Settings.ammoResupply,
                "Remembered weapons automatically generate spare-magazine ammo demand. Hand-added caliber rows in the loadout override this per ammo type.");
            listing.CheckboxLabeled("Weapon refetch", ref Settings.weaponRefetch,
                "A remembered weapon that goes missing is fetched again like any loadout item.");
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
