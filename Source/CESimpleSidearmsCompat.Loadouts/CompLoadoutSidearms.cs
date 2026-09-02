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
    /// The projection's per-pawn record, on the pawn rather than in a side table, so it is
    /// saved and destroyed with them and follows them through caravans, death, capture and
    /// faction changes without any lifecycle code of its own.
    /// </summary>
    public sealed class CompLoadoutSidearms : ThingComp
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

        /// <summary>
        /// The loadout assignment the player intent above belongs to (-1 = the default
        /// loadout). Exclusions and role vetoes are deliberately EPHEMERAL: assigning a
        /// different loadout clears them all, because there is no UI to review them and
        /// a rule recorded under one loadout has no defined meaning under another.
        /// </summary>
        public int lastLoadoutId = -1;

        /// <summary>
        /// Enforce the per-assignment rule at the moment the record is touched, not just
        /// on the reconcile's cadence. The reconcile-only compare left two holes: a
        /// gesture recorded between an assignment change and the first pass was destroyed
        /// by the pending clear (the record still carried the OLD id, so the player's
        /// brand-new exclusion read as stale), and enforcement kept honouring stale rules
        /// on pawns that get no passes (drafted, caravan). Called at the top of every
        /// recorder and enforcement read; a dictionary lookup and an int compare.
        /// (Loadout DELETION is the one writer this cannot see in time — CE reuses ids —
        /// and is handled by the RemoveLoadout postfix instead.)
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
            Scribe_Collections.Look(ref claimed, "cessLoadouts_claimed", LookMode.Deep);
            Scribe_Collections.Look(ref dontEquip, "cessLoadouts_dontEquip", LookMode.Deep);
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
        ///
        /// Returns -1 when the pawn's sidearm memory cannot be resolved — Simple Sidearms
        /// populates it on spawn, so an unspawned pawn in a caravan has none. Clearing the
        /// record then would strand exactly the memories this is meant to release.
        /// </summary>
        public int Release(CompSidearmMemory memory, ThingDefStuffDefPair? forced,
                           ThingDefStuffDefPair? forcedDrafted)
        {
            // Empty claims are a non-event regardless of whether the memory comp is
            // resolvable — counting an away pawn with nothing claimed as "deferred" made
            // every save with a caravan toast "Released 0 ... 1 pawn(s) are away" on
            // every load while the flag was armed.
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
