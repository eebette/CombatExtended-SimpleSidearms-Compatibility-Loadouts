using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using CESimpleSidearmsCompat.Patches;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;

namespace CESidearmsSupply.Patches
{
    /// <summary>
    /// Doctrine projection: specific weapon defs listed in the pawn's CE loadout are
    /// remembered as SS sidearms ("the template giveth"); defs removed from the loadout
    /// are forgotten again, but only if the projection added them ("the template taketh
    /// away") — manual memories are never touched. First ranged/melee slot in list order
    /// becomes default ranged / preferred melee; first weapon overall sets combat mode.
    /// Runs as a lazy reconcile on the same cadence CE evaluates loadouts.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), "TryGiveJob")]
    public static class JobGiver_UpdateLoadout_TryGiveJob_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn pawn)
        {
            if (!SupplyMod.Settings.doctrineProjection)
            {
                return;
            }
            try
            {
                Reconcile(pawn);
            }
            catch (System.Exception e)
            {
                Log.WarningOnce($"[Sidearms&Supply] Doctrine reconcile failed for {pawn}: {e}", 0x53535231);
            }
        }

        private static void Reconcile(Pawn pawn)
        {
            if (pawn == null || !pawn.IsColonist || pawn.Dead)
            {
                return;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
            SupplyGameComponent comp = SupplyGameComponent.Instance;
            if (memory == null || comp == null)
            {
                return;
            }

            Loadout loadout = pawn.GetLoadout();
            List<ThingDef> templateDefs = loadout != null && !loadout.defaultLoadout
                ? loadout.Slots.Where(s => s.thingDef != null && s.thingDef.IsWeapon)
                          .Select(s => s.thingDef).Distinct().ToList()
                : new List<ThingDef>();

            PawnTemplateRecord rec = comp.GetRecord(pawn, create: false);

            // The template taketh away: forget defs the projection added that left the loadout.
            if (rec != null)
            {
                foreach (ThingDef gone in rec.weapons.Where(d => !templateDefs.Contains(d)).ToList())
                {
                    foreach (ThingDefStuffDefPair pair in memory.RememberedWeapons.Where(p => p.thing == gone).Distinct().ToList())
                    {
                        memory.ForgetSidearmMemory(pair);
                    }
                    rec.weapons.Remove(gone);
                }
            }

            // The template giveth: remember listed weapons; fix stuff up to the carried instance.
            foreach (ThingDef def in templateDefs)
            {
                ThingWithComps carried = pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                                             .FirstOrDefault(w => w.def == def);
                List<ThingDefStuffDefPair> rememberedOfDef = memory.RememberedWeapons.Where(p => p.thing == def).ToList();

                if (rememberedOfDef.Count == 0)
                {
                    ThingDefStuffDefPair pair = carried != null
                        ? carried.toThingDefStuffDefPair()
                        : new ThingDefStuffDefPair(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    memory.RememberedWeapons.Add(pair);
                    rec ??= comp.GetRecord(pawn, create: true);
                    rec.weapons.Add(def);
                    if (carried != null)
                    {
                        HoldSync.EnsureHeld(pawn, carried);
                    }
                }
                else if (rec != null && rec.weapons.Contains(def) && carried != null)
                {
                    // Projection-owned memory with a stuff mismatch: retarget to the real instance.
                    ThingDefStuffDefPair actual = carried.toThingDefStuffDefPair();
                    if (!rememberedOfDef.Contains(actual))
                    {
                        memory.RememberedWeapons.Remove(rememberedOfDef[0]);
                        memory.RememberedWeapons.Add(actual);
                    }
                }
            }

            if (templateDefs.Count > 0)
            {
                rec ??= comp.GetRecord(pawn, create: true);
            }
            if (rec == null)
            {
                return;
            }

            ApplyRoles(memory, rec, templateDefs);
        }

        /// <summary>
        /// Set default ranged / preferred melee / combat mode from list order, but only
        /// when the current value is unset or is the one we set last — a player override
        /// always sticks.
        /// </summary>
        private static void ApplyRoles(CompSidearmMemory memory, PawnTemplateRecord rec, List<ThingDef> templateDefs)
        {
            ThingDef firstRanged = templateDefs.FirstOrDefault(d => d.IsRangedWeapon);
            ThingDef firstMelee = templateDefs.FirstOrDefault(d => d.IsMeleeWeapon);

            ThingDefStuffDefPair? curRanged = memory.DefaultRangedWeapon;
            bool rangedOurs = curRanged == null || (rec.defaultRanged != null && curRanged.Value.thing == rec.defaultRanged);
            if (rangedOurs)
            {
                if (firstRanged != null)
                {
                    ThingDefStuffDefPair pair = memory.RememberedWeapons.FirstOrDefault(p => p.thing == firstRanged);
                    if (pair.thing != null && curRanged?.thing != firstRanged)
                    {
                        memory.SetRangedWeaponTypeAsDefault(pair);
                    }
                    rec.defaultRanged = firstRanged;
                }
                else if (rec.defaultRanged != null && curRanged != null)
                {
                    memory.UnsetRangedWeaponDefault();
                    rec.defaultRanged = null;
                }
            }

            ThingDefStuffDefPair? curMelee = memory.PreferredMeleeWeapon;
            bool meleeOurs = curMelee == null || (rec.preferredMelee != null && curMelee.Value.thing == rec.preferredMelee);
            if (meleeOurs)
            {
                if (firstMelee != null)
                {
                    ThingDefStuffDefPair pair = memory.RememberedWeapons.FirstOrDefault(p => p.thing == firstMelee);
                    if (pair.thing != null && curMelee?.thing != firstMelee)
                    {
                        memory.SetMeleeWeaponTypeAsPreferred(pair);
                    }
                    rec.preferredMelee = firstMelee;
                }
                else if (rec.preferredMelee != null && curMelee != null)
                {
                    memory.UnsetMeleeWeaponPreference();
                    rec.preferredMelee = null;
                }
            }

            ThingDef firstAny = templateDefs.FirstOrDefault();
            if (firstAny != null)
            {
                PrimaryWeaponMode desired = firstAny.IsRangedWeapon ? PrimaryWeaponMode.Ranged : PrimaryWeaponMode.Melee;
                bool modeOurs = rec.modeManaged
                    ? memory.primaryWeaponMode == rec.lastMode
                    : memory.primaryWeaponMode == PrimaryWeaponMode.BySkill;
                if (modeOurs && memory.primaryWeaponMode != desired)
                {
                    memory.primaryWeaponMode = desired;
                    rec.modeManaged = true;
                    rec.lastMode = desired;
                }
            }
        }
    }
}
