using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using LudeonTK;
using Verse;

namespace CESupplyTestStaging
{
    public static class DebugActions
    {
        /// <summary>
        /// Dumps the pawn's loadout slot stream: raw slots vs the full GetSlotsFor
        /// output (which includes CE's ad-hoc virtuals and the Loadouts module's derived
        /// slots). The difference IS the derivation — direct verification of ammo
        /// sustainment and refetch without waiting for fetch jobs.
        /// </summary>
        [DebugAction("CE+SS Loadouts", "Log loadout slot stream", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogSlotStream(Pawn pawn)
        {
            Loadout loadout = pawn.GetLoadout();
            if (loadout == null)
            {
                Log.Message($"[SupplyStaging] {pawn.LabelShort}: no loadout.");
                return;
            }
            List<LoadoutSlot> raw = loadout.Slots?.ToList() ?? new List<LoadoutSlot>();
            List<LoadoutSlot> full = loadout.GetSlotsFor(pawn).ToList();
            string Label(LoadoutSlot s) => s.thingDef != null ? $"{s.thingDef.defName} x{s.count}" : $"[generic {s.genericDef?.defName}] x{s.count}";

            Log.Message($"[SupplyStaging] {pawn.LabelShort}: loadout '{loadout.label}' ({(loadout.defaultLoadout ? "default" : "custom")})");
            Log.Message($"[SupplyStaging]   raw slots ({raw.Count}): {string.Join(", ", raw.Select(Label))}");
            IEnumerable<LoadoutSlot> derived = full.Skip(raw.Count);
            Log.Message($"[SupplyStaging]   derived slots ({full.Count - raw.Count}): {string.Join(", ", derived.Select(Label))}");
        }
    }
}
