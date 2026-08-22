using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;

namespace CESidearmsSupply.Patches
{
    /// <summary>
    /// Ammo demand adapter. CE's loadout evaluator is the hauling engine and LoadoutSlots
    /// are its input language, so this postfix translates derived ammo needs into that
    /// language at Loadout.GetSlotsFor — the choke point CE's fetch AND excess-drop logic
    /// both consume, and where CE synthesizes its own ad-hoc ammo slots.
    ///
    /// Scope: ammo derives for weapons DECLARED in the loadout when its "Ad hoc" checkbox
    /// is ticked (at the loadout's adHocMags), or for all remembered weapons under the
    /// off-by-default full-automation setting. Explicit beats derived: any real or
    /// CE-generated slot for the same def suppresses the virtual one.
    ///
    /// The per-ammo arithmetic below mirrors CE's own ad-hoc block in Loadout.GetSlotsFor
    /// rather than approximating it — including its adHocMass/adHocBulk clamps and its
    /// carried-amount band. The band is what stops a pawn one round short from walking to
    /// storage for one round, and what stops a derived slot from turning into a cap on
    /// ammo the player loaded by hand.
    ///
    /// Weapon refetch is NOT here. Simple Sidearms has its own retrieval job giver
    /// (JobGiver_RetrieveWeapon, wired into the vanilla think tree, on by default);
    /// duplicating it produced two uncoordinated fetch engines, and a weapon-shaped
    /// LoadoutSlot could reach CE's equip branch and swap the pawn's primary. What SS's
    /// retrieval lacks is CE's capacity model — see CapacityAwareRetrieval.
    /// </summary>
    [HarmonyPatch(typeof(Loadout), nameof(Loadout.GetSlotsFor))]
    public static class Loadout_GetSlotsFor_Patch
    {
        private static readonly Dictionary<Pawn, (int tick, List<LoadoutSlot> slots)> cache
            = new Dictionary<Pawn, (int, List<LoadoutSlot>)>();

        static Loadout_GetSlotsFor_Patch()
        {
            // CE's own registry for exactly this, used by the class this file patches for
            // its throttle dictionary. Without it the cache pins Pawn objects from a
            // previously loaded save for the life of the process.
            CacheClearComponent.AddClearCacheAction(cache.Clear);
        }

        [HarmonyPostfix]
        public static void Postfix(Loadout __instance, Pawn pawn, ref IEnumerable<LoadoutSlot> __result)
        {
            SupplySettings s = SupplyMod.Settings;
            if (pawn == null || !pawn.IsColonist || (!__instance.adHoc && !s.ammoForAllRemembered))
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
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
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

            // Read the loadout's own rows, not the sidearm projection's saved record: this
            // feature is documented as riding CE's per-loadout "Ad hoc" checkbox alone, and
            // keying it off the projection made it silently derive nothing when the sidearm
            // setting was off, and keep deriving from a frozen record after it was turned
            // off. Same filter Reconcile uses to decide what a loadout declares.
            HashSet<ThingDef> loadoutDeclaredDefs = loadout.adHoc && !loadout.defaultLoadout
                ? new HashSet<ThingDef>(loadout.Slots
                    .Where(s => s.thingDef != null && s.thingDef.IsWeapon && s.count == 1)
                    .Select(s => s.thingDef))
                : null;

            Dictionary<ThingDef, Integer> storage = pawn.GetStorageByThingDef();

            foreach (ThingDefStuffDefPair pair in memory.RememberedWeapons.Distinct())
            {
                ThingDef weaponDef = pair.thing;
                if (weaponDef == null)
                {
                    continue;
                }
                bool viaLoadoutDeclaration = loadoutDeclaredDefs != null && loadoutDeclaredDefs.Contains(weaponDef);
                if (!viaLoadoutDeclaration && !settings.ammoForAllRemembered)
                {
                    continue;
                }
                var props = weaponDef.GetCompProperties<CompProperties_AmmoUser>();
                if (props?.ammoSet?.ammoTypes == null || props.ammoSet.ammoTypes.Count == 0)
                {
                    continue;
                }
                // The ammo system is a global CE setting, so ask the ammo set. Reading it off
                // the carried instance skipped the check exactly when the weapon is absent,
                // which is the case supply exists for.
                if (!AmmoUtility.IsAmmoSystemActive(props.ammoSet))
                {
                    continue;
                }
                ThingWithComps carriedInstance = carried.FirstOrDefault(w => w.def == weaponDef);
                CompAmmoUser ammoUser = carriedInstance?.TryGetComp<CompAmmoUser>();
                ThingDef ammoDef = ammoUser?.SelectedAmmo ?? ammoUser?.CurrentAmmo ?? props.ammoSet.ammoTypes[0].ammo;
                if (ammoDef == null || Covered(ammoDef))
                {
                    continue;
                }
                // MagSize when the weapon is in hand — it reads the MagazineCapacity stat, so
                // attachments and weapon platforms are accounted for. Otherwise the def's own
                // numbers, where AmmoGenPerMagOverride is a floor CE takes the max of, not a
                // replacement (LoadoutPropertiesExtension.TryGenerateAmmoFor).
                int magSize = ammoUser?.MagSize ?? Math.Max(props.AmmoGenPerMagOverride, props.magazineSize);
                if (magSize <= 0)
                {
                    continue;
                }
                int magazines = viaLoadoutDeclaration ? loadout.adHocMags : settings.spareMagazines;
                int held = storage.TryGetValue(ammoDef, out Integer carriedAmmo) ? carriedAmmo.value : 0;
                int demand = DemandFor(loadout, ammoDef, magSize, magazines, held, capped: viaLoadoutDeclaration);
                if (demand <= 0)
                {
                    continue;
                }
                // Several remembered weapons sharing a caliber: keep the largest demand.
                if (!ammoDemand.TryGetValue(ammoDef, out int existing) || demand > existing)
                {
                    ammoDemand[ammoDef] = demand;
                }
            }

            foreach (var kv in ammoDemand)
            {
                result.Add(new LoadoutSlot(kv.Key, kv.Value));
            }

            cache[pawn] = (tick, result);
            return result;
        }

        /// <summary>
        /// CE's own ad-hoc ammo sizing, applied to a derived weapon instead of the equipped
        /// primary: clamp the target by the loadout's mass and bulk budgets, then leave the
        /// carried amount alone while it sits inside the band CE treats as stocked. A slot
        /// asking for exactly what the pawn already holds creates no fetch trip and no excess.
        ///
        /// <paramref name="capped"/> is false for weapons the loadout does not declare (the
        /// full-automation setting). There the slot may create demand but must never sit below
        /// what the pawn carries, or enabling a supply setting would make CE haul the player's
        /// own surplus back to storage.
        /// </summary>
        private static int DemandFor(Loadout loadout, ThingDef ammoDef, int magSize, int magazines, int held, bool capped)
        {
            int magLimit = magazines * magSize;
            if (loadout.adHocMass > 0)
            {
                magLimit = Math.Min(magLimit, (int)(loadout.adHocMass / ammoDef.GetStatValueAbstract(StatDefOf.Mass)));
            }
            if (loadout.adHocBulk > 0)
            {
                magLimit = Math.Min(magLimit, (int)(loadout.adHocBulk / ammoDef.GetStatValueAbstract(CE_StatDefOf.Bulk)));
            }
            if (magLimit <= 0)
            {
                return 0;
            }

            int magCount = magLimit / magSize;
            int minMags = (int)(magCount * 0.75f);
            int minAmmo = minMags * magSize;
            if (magCount < 2)
            {
                minAmmo = magSize;
            }
            else if (minMags < 4)
            {
                minAmmo = (magCount - 1) * magSize;
            }

            if (!capped)
            {
                return held >= minAmmo ? Math.Max(held, minAmmo) : magLimit;
            }
            return (held < minAmmo || held > magLimit) ? magLimit : held;
        }
    }
}
