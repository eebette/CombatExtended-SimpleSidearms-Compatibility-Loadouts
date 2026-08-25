using System.Collections.Generic;
using System.Linq;
using SimpleSidearms.rimworld;
using Verse;

namespace CESidearmsSupply
{
    /// <summary>
    /// Reads the per-pawn record this mod used to keep in a GameComponent and moves it onto
    /// the pawn's own comp.
    ///
    /// Without this, a save written by the old build hits a class that no longer resolves:
    /// RimWorld logs two red errors, each dumping the whole serialized dictionary, and drops
    /// the node. What goes with it is not a cache — the player's "carry it, do not wield it"
    /// decisions cannot be recovered from anything else, and the claims become memories no
    /// release path can ever see, pinned in inventories by the compat patch's drop exemption.
    ///
    /// This class exists only to be found by the scribe. It writes nothing back, so one save
    /// after upgrading the old node is gone and it can be deleted.
    /// </summary>
    public class SupplyGameComponent : GameComponent
    {
        private Dictionary<Pawn, LegacyRecord> records = new Dictionary<Pawn, LegacyRecord>();
        private List<Pawn> scribeKeys;
        private List<LegacyRecord> scribeValues;

        public SupplyGameComponent(Game game)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                return; // migrated on load; never persisted again
            }
            Scribe_Collections.Look(ref records, "templateRecords", LookMode.Reference, LookMode.Deep,
                                    ref scribeKeys, ref scribeValues, logNullErrors: false);
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (records == null || records.Count == 0)
            {
                return;
            }
            int moved = 0;
            foreach (KeyValuePair<Pawn, LegacyRecord> entry in records)
            {
                CompLoadoutSidearms rec = CompLoadoutSidearms.For(entry.Key);
                if (rec == null || entry.Value == null)
                {
                    continue;
                }
                // The old record tracked claims per def; the pair is only recoverable from
                // what the pawn is actually remembering now, which is the same set the old
                // build would have released.
                CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(entry.Key);
                if (memory?.RememberedWeapons != null && entry.Value.weapons != null)
                {
                    foreach (ThingDefStuffDefPair pair in memory.RememberedWeapons
                                 .Where(p => p.thing != null && entry.Value.weapons.Contains(p.thing)))
                    {
                        if (!rec.claimed.Contains(pair))
                        {
                            rec.claimed.Add(pair);
                        }
                    }
                }
                // The irreplaceable half. Old "suppressed" was per def and this is per pair,
                // so widen it to every remembered material of that def — over-honouring the
                // player's exclusion is the safe direction.
                if (entry.Value.suppressed != null && memory?.RememberedWeapons != null)
                {
                    foreach (ThingDef def in entry.Value.suppressed.Where(d => d != null))
                    {
                        foreach (ThingDefStuffDefPair pair in memory.RememberedWeapons.Where(p => p.thing == def))
                        {
                            if (!rec.dontEquip.Contains(pair))
                            {
                                rec.dontEquip.Add(pair);
                            }
                        }
                    }
                }
                moved++;
            }
            records.Clear();
            Log.Message($"[Sidearms&Supply] Migrated {moved} pawn record(s) from the pre-comp format.");
        }
    }

    /// <summary>The old record's shape, kept only so the scribe can read it back.</summary>
    public class LegacyRecord : IExposable
    {
        public HashSet<ThingDef> weapons = new HashSet<ThingDef>();
        public HashSet<ThingDef> suppressed = new HashSet<ThingDef>();

        public void ExposeData()
        {
            // The field names the old build wrote. "claimed"/"forgotten" were a later
            // rename; both spellings exist in the wild, so read either.
            Scribe_Collections.Look(ref weapons, "weapons", LookMode.Def);
            Scribe_Collections.Look(ref suppressed, "suppressed", LookMode.Def);
            if (weapons == null || weapons.Count == 0)
            {
                Scribe_Collections.Look(ref weapons, "claimed", LookMode.Def);
            }
            if (suppressed == null || suppressed.Count == 0)
            {
                Scribe_Collections.Look(ref suppressed, "forgotten", LookMode.Def);
            }
            weapons ??= new HashSet<ThingDef>();
            suppressed ??= new HashSet<ThingDef>();
        }
    }
}
