using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using Verse.AI;

namespace CESimpleSidearmsCompat.Loadouts.Patches
{
    /// <summary>
    /// Loadout weapons as sidearms. Weapon defs listed in a pawn's CE loadout are remembered
    /// as Simple Sidearms sidearms; defs removed from the loadout are forgotten again.
    /// The first declared ranged weapon becomes the default ranged weapon, the first declared
    /// melee the preferred melee. The reconcile computes what SS memory SHOULD contain and applies
    /// the difference.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), "TryGiveJob", new[] { typeof(Pawn) })]
    public static class JobGiver_UpdateLoadout_TryGiveJob_Patch
    {
        /// <summary>
        /// Check for required method(s).
        /// </summary>
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(JobGiver_UpdateLoadout), "TryGiveJob",
                                   new[] { typeof(Pawn) }) != null)
            {
                return true;
            }
            Log.Error("[Sidearms&Supply] JobGiver_UpdateLoadout.TryGiveJob not found — loadout weapons "
                      + "will not be projected as sidearms. Combat Extended probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix(Pawn pawn)
        {
            try
            {
                if (!LoadoutsSessionComponent.Enabled)
                {
                    return;
                }
                Reconcile(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[Sidearms&Supply] Reconcile failed for {pawn}: {e}",
                              0x53535231 ^ (pawn?.thingIDNumber ?? 0) ^ e.GetType().Name.GetHashCode());
            }
        }

        /// <summary>
        /// Patches CE's loadout sync to turn an Equip of a player-excluded weapon into a carry.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            try
            {
                if (__result == null || pawn == null || !LoadoutsSessionComponent.Enabled)
                {
                    return;
                }
                bool equip = __result.def == JobDefOf.Equip;
                bool takeAndEquip = __result.def == CE_JobDefOf.TakeFromOther
                                    && __result.GetTarget(TargetIndex.C).HasThing;
                if (!equip && !takeAndEquip)
                {
                    return;
                }
                CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
                rec?.SyncAssignment(pawn);
                if (rec == null || rec.dontEquip.Count == 0
                    || !(__result.GetTarget(TargetIndex.A).Thing is ThingWithComps weapon)
                    || weapon.def == null
                    || !rec.dontEquip.Contains(weapon.toThingDefStuffDefPair())
                    || !PlayerIntent.ManagedPawn(pawn))
                {
                    return;
                }
                if (equip)
                {
                    Job haul = JobMaker.MakeJob(JobDefOf.TakeCountToInventory, weapon);
                    haul.count = 1;
                    haul.MakeDriver(pawn);
                    __result = haul;
                }
                else
                {
                    // JobDriver_TakeFromOther reads "equip afterwards" from target C holding
                    // a thing; clearing it leaves a plain take-to-inventory.
                    __result.SetTarget(TargetIndex.C, LocalTargetInfo.Invalid);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[Sidearms&Supply] Excluded-weapon job downgrade failed for {pawn}: {e}",
                              0x53535232 ^ (pawn?.thingIDNumber ?? 0) ^ e.GetType().Name.GetHashCode());
            }
        }

        public static void Reconcile(Pawn pawn)
        {
            if (pawn == null || !pawn.IsColonist || pawn.Dead)
            {
                return;
            }
            if (PlayerIntent.PlayerIsDriving)
            {
                return;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
            CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
            if (memory == null || rec == null)
            {
                return;
            }

            // Release our claims on no/default loadout.
            Loadout loadout = pawn.GetLoadout();

            // Clear exclusions/vetoes on assignment change.
            rec.SyncAssignment(pawn);

            if (loadout == null || loadout.defaultLoadout)
            {
                if (rec.claimed.Count > 0)
                {
                    rec.Release(memory, memory.ForcedWeapon, memory.ForcedWeaponWhileDrafted);
                }
                return;
            }

            List<ThingDef> declared = loadout.Slots
                .Where(s => s?.thingDef != null && s.thingDef.IsWeapon && !s.isWeaponPlatform)
                .Select(s => s.thingDef).Distinct().ToList();

            // Read once, before anything is written.
            ThingDefStuffDefPair? forced = memory.ForcedWeapon;
            ThingDefStuffDefPair? forcedDrafted = memory.ForcedWeaponWhileDrafted;

            // Remove the exclusion when a weapon is removed from the Loadout.
            rec.dontEquip.RemoveAll(p => p.thing == null || !declared.Contains(p.thing));

            HashSet<ThingDefStuffDefPair> target = Target(pawn, rec, declared);

            Apply(memory, rec, target, forced, forcedDrafted);
            AssertRoles(pawn, memory, rec, declared, target, forced);
        }

        /// <summary>
        /// What SS memory should hold on this pawn's behalf: every declared weapon they are
        /// actually carrying, minus the ones the player took out of the list, minus the ones
        /// Simple Sidearms would not accept as a sidearm at all.
        /// </summary>
        private static HashSet<ThingDefStuffDefPair> Target(Pawn pawn, CompLoadoutSidearms rec,
                                                            List<ThingDef> declared)
        {
            var target = new HashSet<ThingDefStuffDefPair>();
            foreach (ThingWithComps weapon in pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true))
            {
                if (weapon?.def == null || !declared.Contains(weapon.def)
                    || rec.dontEquip.Contains(weapon.toThingDefStuffDefPair()))
                {
                    continue;
                }
                if (IsLegalSidearm(weapon, pawn))
                {
                    target.Add(weapon.toThingDefStuffDefPair());
                }
            }
            return target;
        }

        /// <summary>
        /// Whether this pawn may hold this weapon as a sidearm at all.
        /// </summary>
        private static bool IsLegalSidearm(ThingWithComps weapon, Pawn pawn)
        {
            // Vanilla: bonded and biocoded weapons, and ideology role bans.
            if (!EquipmentUtility.CanEquip(weapon, pawn, out string _))
            {
                return false;
            }
            // A pawn who cannot do violence is given nothing to switch to.
            if (pawn.WorkTagIsDisabled(WorkTags.Violent))
            {
                return false;
            }
            return StatCalculator.isValidSidearm(weapon.toThingDefStuffDefPair(), out string _);
        }

        /// <summary>
        /// The difference, applied.
        /// </summary>
        private static void Apply(CompSidearmMemory memory, CompLoadoutSidearms rec,
                                  HashSet<ThingDefStuffDefPair> target,
                                  ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)
        {
            // A forced weapon outranks the loadout, and forgetting its last copy would clear
            // the force as a side effect with nothing to tell the player it happened. Keep
            // claiming it so it is released once they unforce it.
            var stranded = new List<ThingDefStuffDefPair>();
            // Materialised: the hooks on SS's memory methods write to rec.claimed, so a lazy
            // enumeration of it here can be invalidated mid-loop.
            foreach (ThingDefStuffDefPair gone in rec.claimed.Distinct().Where(p => !target.Contains(p)).ToList())
            {
                if (gone == forced || gone == forcedDrafted)
                {
                    stranded.Add(gone);
                    continue;
                }
                if (memory.RememberedWeapons.Contains(gone))
                {
                    memory.ForgetSidearmMemory(gone);
                }
            }

            // Self-heal: a pair is both excluded and remembered - every player gesture that re-adds
            // a weapon withdraws its exclusion first, so nothing should be both.
            foreach (ThingDefStuffDefPair banned in rec.dontEquip)
            {
                if (banned == forced || banned == forcedDrafted)
                {
                    continue;
                }
                int guard = memory.RememberedWeapons.Count;
                while (guard-- > 0 && memory.RememberedWeapons.Contains(banned))
                {
                    memory.ForgetSidearmMemory(banned);
                }
            }

            foreach (ThingDefStuffDefPair wanted in target.Where(p => !memory.RememberedWeapons.Contains(p)))
            {
                memory.RememberedWeapons.Add(wanted);
            }

            rec.claimed = target.Concat(stranded).Distinct().ToList();
        }

        /// <summary>
        /// Set first declared ranged and melee weapons as the defaults.
        /// </summary>
        private static bool DefNatureEMP(ThingDef def)
        {
            ProjectileProperties projectile = def?.Verbs?.FirstOrDefault()?.defaultProjectile?.projectile;
            return projectile != null && projectile.damageDef == DamageDefOf.EMP;
        }

        /// <summary>
        /// Def-level "dangerous": building-destroyers by verb flag, incendiary/explosive
        /// by the default projectile.
        /// </summary>
        private static bool DefNatureDangerous(ThingDef def)
        {
            VerbProperties verb = def?.Verbs?.FirstOrDefault();
            if (verb == null)
            {
                return false;
            }
            if (verb.ai_IsBuildingDestroyer)
            {
                return true;
            }
            ProjectileProperties projectile = verb.defaultProjectile?.projectile;
            return projectile != null
                && (projectile.damageDef == DamageDefOf.Flame || projectile.explosionRadius > 0.1f);
        }

        private static void AssertRoles(Pawn pawn, CompSidearmMemory memory, CompLoadoutSidearms rec,
                                        List<ThingDef> declared, HashSet<ThingDefStuffDefPair> target,
                                        ThingDefStuffDefPair? forced)
        {
            if (memory.ForcedUnarmed)
            {
                return;
            }
            // Check is carried item is eligible for a role ("default").
            var roleEligible = new HashSet<ThingDefStuffDefPair>();
            foreach (ThingWithComps weapon in pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true))
            {
                if (weapon?.def == null)
                {
                    continue;
                }
                ThingDefStuffDefPair pair = weapon.toThingDefStuffDefPair();
                if (!target.Contains(pair) || roleEligible.Contains(pair) || pair.isToolNotWeapon()
                    || GettersFilters.isManualUse(weapon) || DefNatureDangerous(weapon.def)
                    || DefNatureEMP(weapon.def))
                {
                    continue;
                }
                roleEligible.Add(pair);
            }
            bool forcedRanged = forced.HasValue && (forced.Value.thing?.IsRangedWeapon ?? false);
            bool forcedMelee = forced.HasValue && (forced.Value.thing?.IsMeleeWeapon ?? false);

            if (!forcedRanged && !rec.rangedRoleVetoed
                && !PlayersAndInHand(pawn, memory.DefaultRangedWeapon, declared))
            {
                ThingDefStuffDefPair? pick = First(declared, roleEligible, memory.DefaultRangedWeapon,
                                                   d => d.IsRangedWeapon);
                if (pick.HasValue && memory.DefaultRangedWeapon != pick)
                {
                    memory.SetRangedWeaponTypeAsDefault(pick.Value);
                }
            }

            if (memory.PreferredUnarmed || forcedMelee || rec.meleeRoleVetoed)
            {
                return;
            }
            if (!PlayersAndInHand(pawn, memory.PreferredMeleeWeapon, declared))
            {
                ThingDefStuffDefPair? pick = First(declared, roleEligible, memory.PreferredMeleeWeapon,
                                                   d => d.IsMeleeWeapon);
                if (pick.HasValue && memory.PreferredMeleeWeapon != pick)
                {
                    memory.SetMeleeWeaponTypeAsPreferred(pick.Value);
                }
            }
        }

        /// <summary>
        /// First declared def of this category with a pair in the target set.
        /// </summary>
        private static ThingDefStuffDefPair? First(List<ThingDef> declared,
                                                   HashSet<ThingDefStuffDefPair> eligible,
                                                   ThingDefStuffDefPair? currentRole,
                                                   Func<ThingDef, bool> category)
        {
            foreach (ThingDef def in declared.Where(category))
            {
                List<ThingDefStuffDefPair> candidates = eligible.Where(p => p.thing == def).ToList();
                if (candidates.Count == 0)
                {
                    continue;
                }
                if (currentRole.HasValue && candidates.Contains(currentRole.Value))
                {
                    return currentRole.Value;
                }
                return candidates
                    .OrderByDescending(p => p.thing.GetStatValueAbstract(StatDefOf.MarketValue, p.stuff))
                    .ThenBy(p => p.stuff?.defName ?? string.Empty)
                    .First();
            }
            return null;
        }

        /// <summary>The player equipped something the loadout does not list, and still has it.</summary>
        private static bool PlayersAndInHand(Pawn pawn, ThingDefStuffDefPair? role, List<ThingDef> declared)
        {
            return role.HasValue && role.Value.thing != null
                   && !declared.Contains(role.Value.thing) && pawn.hasWeaponType(role.Value);
        }

    }

    /// <summary>
    /// Sync assignments after removing a loadout because CE reuses loadout ids
    /// (GetUniqueLoadoutID is max-plus-one over SURVIVORS).
    /// </summary>
    [HarmonyPatch(typeof(LoadoutManager), nameof(LoadoutManager.RemoveLoadout), new[] { typeof(Loadout) })]
    public static class LoadoutManager_RemoveLoadout_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(LoadoutManager), nameof(LoadoutManager.RemoveLoadout),
                                   new[] { typeof(Loadout) }) != null)
            {
                return true;
            }
            Log.Error("[Sidearms&Supply] LoadoutManager.RemoveLoadout not found — deleting a "
                      + "loadout can leave its exclusions governing a recreated one. "
                      + "Combat Extended probably moved it.");
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(Loadout loadout)
        {
            if (loadout == null || Current.Game == null)
            {
                return;
            }
            foreach (Pawn pawn in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists)
            {
                CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
                if (rec != null && rec.lastLoadoutId == loadout.UniqueID)
                {
                    rec.SyncAssignment(pawn);
                }
            }
        }
    }
}
