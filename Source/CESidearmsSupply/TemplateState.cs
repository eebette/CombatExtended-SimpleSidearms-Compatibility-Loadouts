using System.Collections.Generic;
using System.Linq;
using SimpleSidearms.rimworld;
using Verse;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;

namespace CESidearmsSupply
{
    /// <summary>
    /// Per-pawn record of what the doctrine projection put into SS memory, so template
    /// changes can take back exactly what they gave and never touch manual memories.
    /// Tracked per-def (stuff fix-ups change pairs; defs are stable).
    /// </summary>
    public class PawnTemplateRecord : IExposable
    {
        public HashSet<ThingDef> weapons = new HashSet<ThingDef>();
        public ThingDef defaultRanged;   // last default-ranged def WE set (null = we never set it)
        public ThingDef preferredMelee;  // last preferred-melee def WE set
        public bool modeManaged;
        public PrimaryWeaponMode lastMode = PrimaryWeaponMode.BySkill;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref weapons, "weapons", LookMode.Def);
            Scribe_Defs.Look(ref defaultRanged, "defaultRanged");
            Scribe_Defs.Look(ref preferredMelee, "preferredMelee");
            Scribe_Values.Look(ref modeManaged, "modeManaged", false);
            Scribe_Values.Look(ref lastMode, "lastMode", PrimaryWeaponMode.BySkill);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && weapons == null)
            {
                weapons = new HashSet<ThingDef>();
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

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                foreach (Pawn dead in records.Keys.Where(p => p == null || p.Destroyed).ToList())
                {
                    records.Remove(dead);
                }
            }
            Scribe_Collections.Look(ref records, "templateRecords", LookMode.Reference, LookMode.Deep, ref scribeKeys, ref scribeValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                records ??= new Dictionary<Pawn, PawnTemplateRecord>();
                foreach (Pawn broken in records.Keys.Where(p => p == null).ToList())
                {
                    records.Remove(broken);
                }
            }
        }
    }
}
