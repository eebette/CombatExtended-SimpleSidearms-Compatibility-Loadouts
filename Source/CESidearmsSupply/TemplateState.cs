using System.Collections.Generic;
using System.Linq;
using SimpleSidearms.rimworld;
using Verse;

namespace CESidearmsSupply
{
    /// <summary>
    /// Per-pawn record of what this projection did, so it can take back exactly what it gave
    /// and leave everything else alone. Simple Sidearms records no provenance — its remembered
    /// list is flat pairs — so without this there is no way to tell a weapon the loadout put
    /// there from one the player curated by hand.
    ///
    /// Claims are tracked as pairs, not defs, because that is the granularity SS deletes at:
    /// forgetting by def would take a plasteel knife the player added alongside the steel one
    /// this projection claimed.
    /// </summary>
    public class PawnTemplateRecord : IExposable
    {
        /// <summary>Exactly the pairs this projection wrote into SS memory.</summary>
        public List<ThingDefStuffDefPair> claimed = new List<ThingDefStuffDefPair>();

        /// <summary>
        /// Defs the player took back out of the sidearm list by hand. "Carry it, do not wield
        /// it" is an intent the loadout alone cannot express. Recorded from the gizmo, never
        /// inferred from a missing memory — Simple Sidearms drops memories on its own.
        /// </summary>
        public HashSet<ThingDef> forgotten = new HashSet<ThingDef>();

        /// <summary>The player cleared this role by hand; stop asserting it.</summary>
        public bool rangedRoleVetoed;
        public bool meleeRoleVetoed;

        public bool IsEmpty => claimed.Count == 0 && forgotten.Count == 0
                               && !rangedRoleVetoed && !meleeRoleVetoed;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref claimed, "claimed", LookMode.Deep);
            Scribe_Collections.Look(ref forgotten, "forgotten", LookMode.Def);
            Scribe_Values.Look(ref rangedRoleVetoed, "rangedRoleVetoed", false);
            Scribe_Values.Look(ref meleeRoleVetoed, "meleeRoleVetoed", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                claimed ??= new List<ThingDefStuffDefPair>();
                forgotten ??= new HashSet<ThingDef>();
                // Scribe leaves an entry behind for every def that no longer resolves (a
                // removed weapon mod). Left alone it is re-saved and outlives the cause.
                claimed.RemoveAll(p => p.thing == null);
                forgotten.RemoveWhere(d => d == null);
            }
        }
    }

    public class SupplyGameComponent : GameComponent
    {
        private Dictionary<Pawn, PawnTemplateRecord> records = new Dictionary<Pawn, PawnTemplateRecord>();
        private List<Pawn> scribeKeys;
        private List<PawnTemplateRecord> scribeValues;

        public SupplyGameComponent(Game game)
        {
        }

        public static SupplyGameComponent Instance => Current.Game?.GetComponent<SupplyGameComponent>();

        public PawnTemplateRecord GetRecord(Pawn pawn, bool create)
        {
            if (records.TryGetValue(pawn, out PawnTemplateRecord rec))
            {
                return rec;
            }
            if (!create)
            {
                return null;
            }
            rec = new PawnTemplateRecord();
            records[pawn] = rec;
            return rec;
        }

        public void RemoveRecord(Pawn pawn)
        {
            records.Remove(pawn);
        }

        /// <summary>
        /// Hand every claimed pair back: forget it in SS memory and drop the record. Used when
        /// the player turns the feature off, so disabling it actually undoes it rather than
        /// freezing it — the sibling compat patch exempts remembered weapons from CE's drop,
        /// so memories nobody owns would otherwise pin weapons in inventories forever.
        /// </summary>
        public int ReleaseAll()
        {
            int released = 0;
            foreach (KeyValuePair<Pawn, PawnTemplateRecord> entry in records.ToList())
            {
                CompSidearmMemory memory = entry.Key != null
                    ? CompSidearmMemory.GetMemoryCompForPawn(entry.Key)
                    : null;
                if (memory?.RememberedWeapons != null)
                {
                    using (Patches.PlayerIntent.Ours())
                    {
                        foreach (ThingDefStuffDefPair pair in entry.Value.claimed)
                        {
                            if (memory.RememberedWeapons.Contains(pair))
                            {
                                memory.ForgetSidearmMemory(pair);
                                released++;
                            }
                        }
                    }
                }
            }
            records.Clear();
            return released;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // CE prunes its own per-pawn dictionaries on dead/destroyed
                // (LoadoutManager.PurgeHoldTrackerRolls); a dead colonist inside a corpse is
                // not Destroyed. !IsColonist is ours to add: quest lodgers, banished pawns and
                // pawns sold away are none of those three, and the reconcile can no longer
                // touch their records, so without it they accumulate for the life of the save.
                foreach (Pawn gone in records.Keys
                             .Where(p => p == null || p.Destroyed || p.Dead || !p.IsColonist
                                         || records[p].IsEmpty)
                             .ToList())
                {
                    records.Remove(gone);
                }
            }
            Scribe_Collections.Look(ref records, "templateRecords", LookMode.Reference, LookMode.Deep,
                                    ref scribeKeys, ref scribeValues, logNullErrors: false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                records ??= new Dictionary<Pawn, PawnTemplateRecord>();
            }
        }
    }
}
