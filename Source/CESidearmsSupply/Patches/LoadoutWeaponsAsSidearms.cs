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

namespace CESidearmsSupply.Patches
{
    /// <summary>
    /// Loadout weapons as sidearms. Weapon defs listed in a pawn's CE loadout are remembered
    /// as Simple Sidearms sidearms; defs removed from the loadout are forgotten again, which
    /// is what lets CE clear the weapon out of the inventory. The first declared ranged
    /// weapon becomes the default ranged weapon, the first declared melee the preferred melee.
    ///
    /// The reconcile computes what SS memory SHOULD contain and applies the difference. It
    /// infers nothing from how the state got the way it is: the loadout says what is
    /// declared, the record says what the player excluded, the pawn says what they carry.
    /// Running it twice on unchanged inputs is a no-op by construction rather than by
    /// argument, and no phase can destroy state a later phase needs, because the whole
    /// difference is computed before anything in SS's memory is written. (The exclusion
    /// prune is a write to this mod's own record and runs before Target() reads it —
    /// deliberately, since a pruned exclusion must not keep a weapon out of the target.)
    ///
    /// Player intent outranks the loadout and is recorded where the player expresses it
    /// (see PlayerIntent), never deduced from a weapon having gone missing — Simple Sidearms
    /// drops memories on its own, most often when the pawn simply equips something else.
    ///
    /// Reconciled on CE's job-giver cadence rather than event-driven, deliberately: a missed
    /// event is permanent, a missed pass lasts until the next one. The honest caveat: CE's
    /// job giver sits in the undrafted colonist think tree, so drafted, downed, mentally
    /// broken and caravan pawns get no passes at all until they return to it — player
    /// gestures still record during that window, they just take effect on the first pass
    /// after. Measured 2026-08-25
    /// (test/run-supply-bench.sh): 18.4us per call at 0.79 calls per colonist per 1000
    /// ticks — 0.0017% of a 60fps frame at 20 colonists.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), "TryGiveJob", new[] { typeof(Pawn) })]
    public static class JobGiver_UpdateLoadout_TryGiveJob_Patch
    {
        /// <summary>
        /// CE has no ABI policy, so a rename here should disable this feature with a named
        /// error rather than abort PatchAll. CE's own house rule: degrade, never throw.
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
                // The deferred release used to run here and does not any more, deliberately:
                // this hook can execute inside an open gizmo-interaction scope (a click that
                // ends a job restarts the think tree synchronously), and a release running
                // there had its own forgets recorded as player exclusions. It now runs from
                // SupplySessionComponent.FinalizeInit, once per load.
                if (!SupplyMod.Settings.loadoutWeaponsAsSidearms)
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
        /// CE's job giver does not only haul: when the pawn's current primary is empty or
        /// not covered by a loadout row, it issues a real Equip job on the priority ground
        /// item (JobGiver_UpdateLoadout.GetUpdateLoadoutJob), or sets the equip flag on a
        /// TakeFromOther when a fellow pawn carries it. For an excluded weapon that would
        /// wield the exact thing the player said not to wield — and Simple Sidearms' own
        /// equip hook would then re-remember it with no player anywhere in the chain.
        ///
        /// Carrying is what the loadout row asks for and what the exclusion permits, so the
        /// job is downgraded, not refused: Equip becomes TakeCountToInventory (the same job
        /// CE builds on its own haul branch), and TakeFromOther keeps the transfer but loses
        /// its equip flag. The row still gets satisfied; the weapon stays out of the hands.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            try
            {
                if (__result == null || pawn == null || !SupplyMod.Settings.loadoutWeaponsAsSidearms)
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
            // A gizmo click that drops or swaps a weapon ends the pawn's job, and
            // Pawn_JobTracker.EndCurrentJob restarts the think tree synchronously — so CE's
            // job giver, and this prefix, can run while the player-intent scope is still
            // open. Everything below would then be read back as the player's doing.
            // Skipping the pass costs nothing: this recomputes from scratch every time.
            if (PlayerIntent.PlayerIsDriving)
            {
                return;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
            CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
            // memory == null, not memory?.RememberedWeapons == null: the getter never
            // returns null — it regenerates the list from carried weapons as a side effect,
            // which is not something a null guard should be triggering.
            if (memory == null || rec == null)
            {
                return;
            }

            // No loadout means NO OPINION about what to claim — but claims already made
            // still belong to this projection, and CE reassigns every pawn of a deleted
            // loadout to the default one on an unconfirmed float-menu click. Without the
            // release here, those claims survived with nobody to release them, and the
            // compat patch's drop exemption then pinned the weapons in every pawn's
            // inventory forever. The player's own memories, exclusions and role vetoes are
            // not claims and survive untouched.
            Loadout loadout = pawn.GetLoadout();
            if (loadout == null || loadout.defaultLoadout)
            {
                if (rec.claimed.Count > 0)
                {
                    rec.Release(memory, memory.ForcedWeapon, memory.ForcedWeaponWhileDrafted);
                }
                return;
            }

            // loadout.Slots, deliberately, not GetSlotsFor(pawn): on an ad-hoc loadout CE
            // synthesises a slot for whatever the pawn is holding right now, so reading that
            // would make the claim set follow the pawn's current weapon and forget a
            // player's own sidearm the moment they switched back. What CE hauls and what
            // this projection registers as a sidearm are different questions.
            List<ThingDef> declared = loadout.Slots
                .Where(s => s?.thingDef != null && s.thingDef.IsWeapon && !s.isWeaponPlatform)
                .Select(s => s.thingDef).Distinct().ToList();

            // Read once, before anything is written. SS clears a forced weapon as a side
            // effect of forgetting its last copy.
            ThingDefStuffDefPair? forced = memory.ForcedWeapon;
            ThingDefStuffDefPair? forcedDrafted = memory.ForcedWeaponWhileDrafted;

            // An exclusion follows its row. Take the weapon out of the loadout and put it
            // back and the pawn manages it again, rather than it staying silently excluded
            // forever with nothing in any UI to say why.
            //
            // Note this is not reached on the default loadout — the early return above treats
            // that as no opinion, so deleting a loadout does not quietly discard the player's
            // exclusions along with everything else.
            rec.dontEquip.RemoveAll(p => p.thing == null || !declared.Contains(p.thing));

            HashSet<ThingDefStuffDefPair> target = Target(pawn, rec, declared);

            Apply(memory, rec, target, forced, forcedDrafted);
            AssertRoles(pawn, memory, rec, declared, target, forced);
        }

        /// <summary>
        /// What SS memory should hold on this pawn's behalf: every declared weapon they are
        /// actually carrying, minus the ones the player took out of the list, minus the ones
        /// Simple Sidearms would not accept as a sidearm at all.
        ///
        /// Carrying is the gate because a pair names a material, and guessing one for a
        /// weapon the pawn has not got yet sends SS hunting a specific stuff the loadout
        /// never asked for. The loadout row already makes CE fetch it; claim it when it lands.
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
        ///
        /// Composed from vanilla's own eligibility and Simple Sidearms' public type check.
        /// Deliberately NOT CanPickupSidearmType: that answers "is there room for one MORE
        /// of these", and every weapon reaching here is already in the pawn's hands, so its
        /// mass and bulk are already counted against them — it would refuse every weapon on
        /// exactly the loaded pawns this feature exists for.
        ///
        /// SS's slot-count and relative-mass limits are not enforced here. Those govern what
        /// a pawn picks up on their own; a loadout row is an explicit order, and this module
        /// treats it as outranking them. That is a decision, not an oversight — see README.
        /// </summary>
        private static bool IsLegalSidearm(ThingWithComps weapon, Pawn pawn)
        {
            // Vanilla: bonded and biocoded weapons belonging to someone else, and ideology
            // role bans. Nothing upstream of this module checked those, so a pawn could be
            // given another colonist's persona weapon to switch to.
            if (!EquipmentUtility.CanEquip(weapon, pawn, out string _))
            {
                return false;
            }
            // A pawn who cannot do violence is given nothing to switch to. SS is more
            // permissive — it allows tools — and being narrower is this module's own call:
            // a claim that will never be drawn only costs the pawn bulk.
            if (pawn.WorkTagIsDisabled(WorkTags.Violent))
            {
                return false;
            }
            return StatCalculator.isValidSidearm(weapon.toThingDefStuffDefPair(), out string _);
        }

        /// <summary>
        /// The difference, applied. Nothing is dropped here: forgetting ends the compat
        /// patch's drop exemption and CE's own rules decide from there, honouring that
        /// loadout's dropUndefined and adHoc settings as they do for any other item.
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
                // One claim in, one claim out. SS allows duplicate memories and treats them
                // as real, so draining to exhaustion would delete copies the player added.
                if (memory.RememberedWeapons.Contains(gone))
                {
                    memory.ForgetSidearmMemory(gone);
                }
            }

            // Self-heal: a pair that is both excluded and remembered means a machine path
            // wrote the memory back behind the recorder — every player gesture that re-adds
            // a weapon withdraws its exclusion first, so nothing legitimate is ever on both
            // lists. Not gated on rec.claimed: the machine's copy never entered it, which is
            // exactly why nothing else can release it. Drained to exhaustion for the same
            // reason — none of the copies can be the player's.
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
        /// First declared ranged weapon is the default ranged weapon, first declared melee the
        /// preferred melee — first that this pawn actually has, so a weapon still being
        /// fetched does not leave the role unset and SS choosing by raw DPS.
        ///
        /// Skipped for: a forced weapon of that category, the unarmed states, a role the
        /// player cleared by hand, and a role naming a weapon the loadout does not list which
        /// the pawn is still carrying.
        /// </summary>
        private static void AssertRoles(Pawn pawn, CompSidearmMemory memory, CompLoadoutSidearms rec,
                                        List<ThingDef> declared, HashSet<ThingDefStuffDefPair> target,
                                        ThingDefStuffDefPair? forced)
        {
            if (memory.ForcedUnarmed)
            {
                return;
            }
            // A role is a stronger statement than a claim: SS equips the default ranged
            // weapon UNCONDITIONALLY, skipping every filter its own picker applies. So a
            // pair may be claimed (carried per the loadout) yet ineligible for a role —
            // tools, manual-use weapons, incendiary/building-destroyers, EMP — or SS will
            // draft-equip a fire extinguisher. Composed from SS's own public predicates,
            // so its policy changes follow automatically.
            var roleEligible = new HashSet<ThingDefStuffDefPair>();
            foreach (ThingWithComps weapon in pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true))
            {
                if (weapon?.def == null)
                {
                    continue;
                }
                ThingDefStuffDefPair pair = weapon.toThingDefStuffDefPair();
                if (!target.Contains(pair) || roleEligible.Contains(pair) || pair.isToolNotWeapon()
                    || GettersFilters.isManualUse(weapon) || GettersFilters.isDangerousWeapon(weapon)
                    || GettersFilters.isEMPWeapon(weapon))
                {
                    continue;
                }
                roleEligible.Add(pair);
            }
            // SS's setters only clear a forced weapon of their own category, so a forced
            // melee weapon is no reason to abandon the ranged default.
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
        ///
        /// `target` is a set, so with two materials of one def carried the answer would
        /// otherwise depend on enumeration order — which follows inventory order, which
        /// every equip/unequip and CE's ammo churn reorder. Every flip re-set the role and
        /// made the pawn physically swap weapons. The pair currently holding the role wins
        /// outright: it is one value, so it can actually discriminate between two materials
        /// of one def — a list of previously-claimed pairs contained both candidates and
        /// the preference collapsed back to inventory order. It also means a player's
        /// hand-set role on a declared weapon is its own proof against the next pass.
        ///
        /// A fresh pick uses Simple Sidearms' own "best copy" key — market value,
        /// descending, the ordering equipSpecificWeaponTypeFromInventory applies when
        /// choosing among copies of a pair — then stuff name for determinism.
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
}
