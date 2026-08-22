using CombatExtended;
using HarmonyLib;
using SimpleSidearms.rimworld;
using Verse;
using Verse.AI;

namespace CESidearmsSupply.Patches
{
    /// <summary>
    /// Simple Sidearms already fetches remembered weapons that a pawn is not carrying:
    /// JobGiver_RetrieveWeapon sits in the vanilla think tree for every colonist, and both
    /// of its gates (ReEquipOutOfCombat / ReEquipInCombat) default to on. This module used
    /// to emit its own weapon-shaped LoadoutSlots for the same purpose, which meant two
    /// uncoordinated fetch engines — and CE's loadout evaluator will EQUIP a weapon slot
    /// as the primary when the pawn's current primary matches no slot, so a refetch of a
    /// remembered knife could stow a pawn's rifle.
    ///
    /// What SS's retrieval genuinely lacks is CE's capacity model. Neither the job giver
    /// nor its pickup driver consults mass or bulk — the driver ends in a bare
    /// innerContainer.TryAdd — so a pawn can be sent across the map for a weapon that puts
    /// them over CE's bulk limit, where it then counts against everything else they carry.
    ///
    /// So this does one thing: cancel the retrieval when CE says the weapon will not fit.
    /// SS still decides which weapons are worth remembering and fetching.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_RetrieveWeapon), nameof(JobGiver_RetrieveWeapon.TryGiveJobStatic))]
    public static class JobGiver_RetrieveWeapon_TryGiveJobStatic_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result == null || !SupplyMod.Settings.capacityAwareRetrieval)
            {
                return;
            }
            Thing target = __result.targetA.Thing;
            CompInventory inventory = pawn?.TryGetComp<CompInventory>();
            if (target == null || inventory == null)
            {
                return;
            }
            if (!inventory.CanFitInInventory(target, out int count) || count < 1)
            {
                __result = null;
            }
        }
    }
}
