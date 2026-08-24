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
        /// Weapon defs the player took out of the sidearm list by hand — "carry it, do not
        /// wield it", which removing the loadout row cannot say because that would stop the
        /// pawn carrying it at all.
        ///
        /// The one piece of state here that cannot be recovered if it is lost. Simple
        /// Sidearms stores no provenance, so nothing distinguishes a weapon the player
        /// removed from one it never held.
        /// </summary>
        public HashSet<ThingDef> dontEquip = new HashSet<ThingDef>();

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

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref claimed, "claimed", LookMode.Deep);
            Scribe_Collections.Look(ref dontEquip, "dontEquip", LookMode.Def);
            Scribe_Values.Look(ref rangedRoleVetoed, "rangedRoleVetoed", false);
            Scribe_Values.Look(ref meleeRoleVetoed, "meleeRoleVetoed", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                claimed ??= new List<ThingDefStuffDefPair>();
                dontEquip ??= new HashSet<ThingDef>();
                // Scribe leaves an entry behind for every def that no longer resolves.
                claimed.RemoveAll(p => p.thing == null);
                dontEquip.RemoveWhere(d => d == null);
            }
        }

        /// <summary>Forget every pair this projection wrote, and drop the record.</summary>
        public int Release(CompSidearmMemory memory)
        {
            int released = 0;
            if (memory?.RememberedWeapons != null)
            {
                foreach (ThingDefStuffDefPair pair in claimed.Distinct())
                {
                    while (memory.RememberedWeapons.Contains(pair))
                    {
                        memory.ForgetSidearmMemory(pair);
                        released++;
                    }
                }
            }
            claimed.Clear();
            return released;
        }
    }
}
