using System.Collections.Generic;
using System.Linq;
using SimpleSidearms.rimworld;
using Verse;

namespace CESidearmsSupply
{
    public class CompProperties_LoadoutSidearms : CompProperties
    {
        public CompProperties_LoadoutSidearms()
        {
            compClass = typeof(CompLoadoutSidearms);
        }
    }

    /// <summary>
    /// The projection's per-pawn record, on the pawn rather than in a side table, so it is
    /// saved and destroyed with them and follows them through caravans, death, capture and
    /// faction changes without any lifecycle code of its own.
    /// </summary>
    public class CompLoadoutSidearms : ThingComp
    {
        /// <summary>
        /// The pairs this projection put into Simple Sidearms' memory. A cache, not a source
        /// of truth: the reconcile recomputes what it wants from the loadout and the pawn's
        /// inventory every pass, and this only tells it what to take back. Losing it means
        /// some pairs are never released, not that anything behaves wrongly.
        /// </summary>
        public List<ThingDefStuffDefPair> claimed = new List<ThingDefStuffDefPair>();

        /// <summary>
        /// Weapons the player took out of the sidearm list by hand — "carry it, do not
        /// wield it", which removing the loadout row cannot say because that would stop the
        /// pawn carrying it at all.
        ///
        /// Pairs, not defs, matching how a claim is recorded: forgetting the steel knife is
        /// not a statement about the plasteel one, which may well be the player's own.
        ///
        /// The one piece of state here that cannot be recovered if it is lost. Simple
        /// Sidearms stores no provenance, so nothing distinguishes a weapon the player
        /// removed from one it never held.
        /// </summary>
        public List<ThingDefStuffDefPair> dontEquip = new List<ThingDefStuffDefPair>();

        /// <summary>
        /// The player cleared this role by hand. SS persists a flag for "deliberately no
        /// melee preference" but has no ranged equivalent, so without recording it the
        /// projection would restore a cleared default ranged weapon on the next pass.
        /// </summary>
        public bool rangedRoleVetoed;
        public bool meleeRoleVetoed;

        public Pawn Pawn => parent as Pawn;

        public static CompLoadoutSidearms For(Pawn pawn)
        {
            return pawn?.TryGetComp<CompLoadoutSidearms>();
        }

        /// <summary>
        /// The def patch attaches this to every pawn ThingDef, which by inheritance means
        /// animals, insects and mechanoids too. Simple Sidearms attaches its own comp the
        /// same way and then removes it here; do the same, or every muffalo carries this.
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
            // A ThingComp has no node of its own — these are written as direct children of
            // the pawn's, sharing a namespace with every other comp on it. Prefixed so a
            // field called "claimed" on someone else's comp cannot resolve to ours.
            Scribe_Collections.Look(ref claimed, "ceSupply_claimed", LookMode.Deep);
            Scribe_Collections.Look(ref dontEquip, "ceSupply_dontEquip", LookMode.Deep);
            Scribe_Values.Look(ref rangedRoleVetoed, "ceSupply_rangedRoleVetoed", false);
            Scribe_Values.Look(ref meleeRoleVetoed, "ceSupply_meleeRoleVetoed", false);
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
        ///
        /// Returns -1 when the pawn's sidearm memory cannot be resolved — Simple Sidearms
        /// populates it on spawn, so an unspawned pawn in a caravan has none. Clearing the
        /// record then would strand exactly the memories this is meant to release.
        /// </summary>
        public int Release(CompSidearmMemory memory, ThingDefStuffDefPair? forced,
                           ThingDefStuffDefPair? forcedDrafted)
        {
            if (memory?.RememberedWeapons == null)
            {
                return -1;
            }
            int released = 0;
            foreach (ThingDefStuffDefPair pair in claimed.Distinct().ToList())
            {
                // Same courtesy the reconcile pays: forgetting the last copy of a forced
                // weapon clears the force as a side effect, and that is the player's.
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
