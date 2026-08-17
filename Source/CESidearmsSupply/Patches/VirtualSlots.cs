using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using SimpleSidearms.rimworld;
using Verse;

namespace CESidearmsSupply.Patches
{
    /// <summary>
    /// Ammo/refetch demand adapter. SS has no logistics — it never fetches or hauls;
    /// CE's loadout evaluator is the only engine for that, and LoadoutSlots are its
    /// input language. This postfix translates derived needs into that language at
    /// Loadout.GetSlotsFor, the single choke point CE's fetch AND excess-drop logic
    /// both consume (and where CE itself synthesizes its ad-hoc ammo slots).
    ///
    /// Scope: ammo derives for weapons DECLARED in the loadout when its "Ad hoc"
    /// checkbox is ticked (at the loadout's adHocMags), or for all remembered weapons
    /// under the off-by-default full-automation setting. Refetch slots exist only for
    /// MISSING weapons under the off-by-default opt-in — a carried weapon never gains
    /// keep-protection from here (drop exemption for remembered weapons is the compat
    /// patch's axis 10, and it ends when the memory is forgotten). Explicit beats
    /// derived: any real or CE-generated slot for the same def suppresses the virtual
    /// one. Stateless — recomputed from memory, nothing to desync.
    /// </summary>
    [HarmonyPatch(typeof(Loadout), nameof(Loadout.GetSlotsFor))]
    public static class Loadout_GetSlotsFor_Patch
    {
        private static readonly Dictionary<Pawn, (int tick, List<LoadoutSlot> slots)> cache
            = new Dictionary<Pawn, (int, List<LoadoutSlot>)>();

        [HarmonyPostfix]
        public static void Postfix(Loadout __instance, Pawn pawn, ref IEnumerable<LoadoutSlot> __result)
        {
            SupplySettings s = SupplyMod.Settings;
            if (pawn == null || !pawn.IsColonist
                || (!__instance.adHoc && !s.ammoForAllRemembered && !s.refetchAllRemembered))
            {
                return;
            }
            List<LoadoutSlot> baseSlots = __result.ToList();
            List<LoadoutSlot> virtuals = VirtualSlotsFor(__instance, pawn, baseSlots);
            __result = virtuals.Count == 0 ? baseSlots : baseSlots.Concat(virtuals);
        }

        private static List<LoadoutSlot> VirtualSlotsFor(Loadout loadout, Pawn pawn, List<LoadoutSlot> baseSlots)
        {
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (cache.TryGetValue(pawn, out var cached) && cached.tick == tick)
            {
                return cached.slots;
            }

            var result = new List<LoadoutSlot>();
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, fillExistingIfCreating: false);
            if (memory?.RememberedWeapons == null || memory.RememberedWeapons.Count == 0)
            {
                cache[pawn] = (tick, result);
                return result;
            }

            // "Explicit beats derived": suppress per-def against everything already in the
            // slot stream — real slots, matching generics, and CE's own ad-hoc virtuals.
            bool Covered(ThingDef def) => baseSlots.Any(s =>
                s.thingDef == def || (s.genericDef != null && s.genericDef.lambda(def)));

            List<ThingWithComps> carried = pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true).ToList();
            var ammoDemand = new Dictionary<ThingDef, int>();
            SupplySettings settings = SupplyMod.Settings;
            // Ammo derivation rides CE's own per-loadout "Ad hoc" opt-in (which vanilla CE
            // uses to auto-supply the equipped primary): with it ticked, derivation extends
            // to every weapon DECLARED in this loadout, at the loadout's adHocMags count.
            // Unticked = pure CE curated contract: no ammo rows, no ammo, no demand.
            // The full-automation setting extends derivation to all remembered weapons.
            HashSet<ThingDef> loadoutDeclaredDefs = loadout.adHoc
                ? SupplyGameComponent.Instance?.GetRecord(pawn, create: false)?.weapons
                : null;

            foreach (ThingDefStuffDefPair pair in memory.RememberedWeapons.Distinct())
            {
                ThingDef weaponDef = pair.thing;
                if (weaponDef == null)
                {
                    continue;
                }
                ThingWithComps carriedInstance = carried.FirstOrDefault(w => w.def == weaponDef);

                // Loadout-declared weapons live in real slots and refetch natively; the
                // virtual refetch path is the opt-in extension for manual memories.
                if (settings.refetchAllRemembered && carriedInstance == null && !Covered(weaponDef))
                {
                    result.Add(new LoadoutSlot(weaponDef, 1));
                }

                bool viaLoadoutDeclaration = loadoutDeclaredDefs != null && loadoutDeclaredDefs.Contains(weaponDef);
                if (!viaLoadoutDeclaration && !settings.ammoForAllRemembered)
                {
                    continue;
                }
                int magazines = viaLoadoutDeclaration ? loadout.adHocMags : settings.spareMagazines;
                var props = weaponDef.GetCompProperties<CompProperties_AmmoUser>();
                if (props?.ammoSet?.ammoTypes == null || props.ammoSet.ammoTypes.Count == 0)
                {
                    continue;
                }
                CompAmmoUser ammoUser = carriedInstance?.TryGetComp<CompAmmoUser>();
                if (ammoUser != null && !ammoUser.UseAmmo)
                {
                    continue; // CE ammo system disabled
                }
                ThingDef ammoDef = ammoUser?.SelectedAmmo ?? ammoUser?.CurrentAmmo ?? props.ammoSet.ammoTypes[0].ammo;
                if (ammoDef == null || Covered(ammoDef))
                {
                    continue;
                }
                int perMag = props.AmmoGenPerMagOverride > 0 ? props.AmmoGenPerMagOverride
                             : props.magazineSize > 0 ? props.magazineSize : 25;
                int count = perMag * magazines;
                if (count <= 0)
                {
                    continue;
                }
                // Several remembered weapons sharing a caliber: keep the largest demand.
                if (!ammoDemand.TryGetValue(ammoDef, out int existing) || count > existing)
                {
                    ammoDemand[ammoDef] = count;
                }
            }

            foreach (var kv in ammoDemand)
            {
                result.Add(new LoadoutSlot(kv.Key, kv.Value));
            }

            cache[pawn] = (tick, result);
            if (cache.Count > 500)
            {
                cache.Clear(); // crude leak guard; entries are per-tick anyway
            }
            return result;
        }
    }
}
