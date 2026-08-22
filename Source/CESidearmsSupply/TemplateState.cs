using System.Collections.Generic;
using System.Linq;
using SimpleSidearms.rimworld;
using Verse;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;

namespace CESidearmsSupply
{
    /// <summary>
    /// Per-pawn record of what loadout-weapons-as-sidearms put into SS memory, so template
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
        public PrimaryWeaponMode modeBefore = PrimaryWeaponMode.BySkill; // what the pawn had before we claimed the mode

        public void ExposeData()
        {
            Scribe_Collections.Look(ref weapons, "weapons", LookMode.Def);
            Scribe_Defs.Look(ref defaultRanged, "defaultRanged");
            Scribe_Defs.Look(ref preferredMelee, "preferredMelee");
            Scribe_Values.Look(ref modeManaged, "modeManaged", false);
            Scribe_Values.Look(ref lastMode, "lastMode", PrimaryWeaponMode.BySkill);
            Scribe_Values.Look(ref modeBefore, "modeBefore", PrimaryWeaponMode.BySkill);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                weapons ??= new HashSet<ThingDef>();
                // Scribe inserts a null for every def that no longer resolves (a removed
                // weapon mod), and the collection guard above only covers a null collection.
                // Left alone the null is re-saved and outlives the mod that caused it.
                weapons.RemoveWhere(d => d == null);
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

        /// <summary>A record claiming nothing and managing nothing is not worth persisting.</summary>
        private static bool IsEmpty(PawnTemplateRecord rec)
        {
            return rec == null
                   || (rec.weapons.Count == 0 && rec.defaultRanged == null && rec.preferredMelee == null && !rec.modeManaged);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // Same three conditions CE prunes its own per-pawn dictionary on
                // (LoadoutManager.PurgeHoldTrackerRolls): a dead colonist inside a corpse is
                // not Destroyed, and a record that claims nothing is dead weight that still
                // pins the Pawn reference.
                foreach (Pawn gone in records.Keys
                             .Where(p => p == null || p.Destroyed || p.Dead || IsEmpty(records[p]))
                             .ToList())
                {
                    records.Remove(gone);
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
