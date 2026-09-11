using CombatExtended;
using System.Collections.Generic;
using System.Linq;
using SimpleSidearms.rimworld;
using Verse;

namespace CESimpleSidearmsCompat.Loadouts
{
    public class CompProperties_LoadoutSidearms : CompProperties
    {
        public CompProperties_LoadoutSidearms()
        {
            compClass = typeof(CompLoadoutSidearms);
        }
    }

    /// <summary>
    /// The projection's per-pawn record.
    /// </summary>
    public sealed class CompLoadoutSidearms : ThingComp
    {
        /// <summary>
        /// The pairs this projection put into Simple Sidearms' memory.
        /// </summary>
        public List<ThingDefStuffDefPair> claimed = new List<ThingDefStuffDefPair>();

        /// <summary>
        /// Weapons the player took out of the sidearm list by hand.
        /// </summary>
        public List<ThingDefStuffDefPair> dontEquip = new List<ThingDefStuffDefPair>();

        /// <summary>
        /// The player's hand-equipped primary that is not a loadout weapon - the list's index-0
        /// pick. While set, a loadout weapon the pawn lacks is fetched as a sidearm instead of
        /// retaking the primary slot. Cleared when the player equips a loadout weapon (hands the
        /// slot back to the loadout) or the loadout assignment changes.
        /// </summary>
        public ThingDefStuffDefPair? playerPrimary;

        /// <summary>
        /// Weapons whose roles the player cleared by hand.
        /// </summary>
        public bool rangedRoleVetoed;

        /// <summary>
        /// The loadout assignment the player intent above belongs to (default = -1).
        /// </summary>
        public int lastLoadoutId = -1;

        /// <summary>
        /// Enforce the per-assignment rule at the moment the record is touched.
        /// </summary>
        public void SyncAssignment(Pawn pawn)
        {
            int id = -1;
            if (LoadoutManager.AssignedLoadouts.TryGetValue(pawn, out Loadout assigned)
                && assigned != null && !assigned.defaultLoadout)
            {
                id = assigned.UniqueID;
            }
            if (lastLoadoutId != id)
            {
                dontEquip.Clear();
                playerPrimary = null;
                rangedRoleVetoed = false;
                meleeRoleVetoed = false;
                lastLoadoutId = id;
            }
        }
        public bool meleeRoleVetoed;

        public Pawn Pawn => parent as Pawn;

        public static CompLoadoutSidearms For(Pawn pawn)
        {
            return pawn?.TryGetComp<CompLoadoutSidearms>();
        }

        /// <summary>
        /// Attach the def to human-like only.
        /// </summary>
        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            if (!(parent is Pawn pawn) || !(pawn.RaceProps?.Humanlike ?? false))
            {
                parent.AllComps.Remove(this);
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref claimed, "cessLoadouts_claimed", LookMode.Deep);
            Scribe_Collections.Look(ref dontEquip, "cessLoadouts_dontEquip", LookMode.Deep);
            Scribe_Deep.Look(ref playerPrimary, "cessLoadouts_playerPrimary");
            Scribe_Values.Look(ref rangedRoleVetoed, "cessLoadouts_rangedRoleVetoed", false);
            Scribe_Values.Look(ref meleeRoleVetoed, "cessLoadouts_meleeRoleVetoed", false);
            Scribe_Values.Look(ref lastLoadoutId, "cessLoadouts_lastLoadoutId", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                claimed ??= new List<ThingDefStuffDefPair>();
                dontEquip ??= new List<ThingDefStuffDefPair>();
                // Scribe leaves an entry behind for every def that no longer resolves.
                claimed.RemoveAll(p => p.thing == null);
                dontEquip.RemoveAll(p => p.thing == null);
            }
        }

        /// <summary>
        /// Forget every pair this projection wrote, and drop the record.
        /// </summary>
        public int Release(CompSidearmMemory memory, ThingDefStuffDefPair? forced,
                           ThingDefStuffDefPair? forcedDrafted)
        {
            // Empty claims are a non-event regardless of the memory comp being resolvable.
            if (claimed.Count == 0)
            {
                return 0;
            }
            if (memory?.RememberedWeapons == null)
            {
                return -1;
            }
            int released = 0;
            foreach (ThingDefStuffDefPair pair in claimed.Distinct().ToList())
            {
                // Clear the force when forgetting the last copy of a forced.
                if (pair == forced || pair == forcedDrafted)
                {
                    continue;
                }
                if (memory.RememberedWeapons.Contains(pair))
                {
                    memory.ForgetSidearmMemory(pair);
                    released++;
                }
            }
            claimed.RemoveAll(p => p != forced && p != forcedDrafted);
            return released;
        }
    }
}
