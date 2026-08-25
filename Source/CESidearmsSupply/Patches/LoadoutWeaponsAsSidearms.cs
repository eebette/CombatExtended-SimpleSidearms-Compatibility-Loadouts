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
    /// difference is computed before anything is written.
    ///
    /// Player intent outranks the loadout and is recorded where the player expresses it
    /// (see PlayerIntent), never deduced from a weapon having gone missing — Simple Sidearms
    /// drops memories on its own, most often when the pawn simply equips something else.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), "TryGiveJob")]
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
                // Before the setting check: this is the case where the player turned the
                // feature OFF with no save loaded, so there was nothing to release then.
                if (SupplyMod.Settings.releasePending)
                {
                    SupplyMod.Release();
                }
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
            if (memory?.RememberedWeapons == null || rec == null)
            {
                return;
            }

            // No loadout means NO OPINION, not "declares nothing". CE reassigns every pawn of
            // a deleted loadout to the default one by writing its dictionary directly, and
            // deleting a loadout is an unconfirmed float-menu click.
            Loadout loadout = pawn.GetLoadout();
            if (loadout == null || loadout.defaultLoadout)
            {
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
                if (weapon?.def == null || !declared.Contains(weapon.def) || rec.dontEquip.Contains(weapon.def))
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
            // SS's setters only clear a forced weapon of their own category, so a forced
            // melee weapon is no reason to abandon the ranged default.
            bool forcedRanged = forced.HasValue && (forced.Value.thing?.IsRangedWeapon ?? false);
            bool forcedMelee = forced.HasValue && (forced.Value.thing?.IsMeleeWeapon ?? false);

            if (!forcedRanged && !rec.rangedRoleVetoed
                && !PlayersAndInHand(pawn, memory.DefaultRangedWeapon, declared))
            {
                ThingDefStuffDefPair? pick = First(declared, target, d => d.IsRangedWeapon);
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
                ThingDefStuffDefPair? pick = First(declared, target, d => d.IsMeleeWeapon);
                if (pick.HasValue && memory.PreferredMeleeWeapon != pick)
                {
                    memory.SetMeleeWeaponTypeAsPreferred(pick.Value);
                }
            }
        }

        /// <summary>First declared def of this category with a pair in the target set.</summary>
        private static ThingDefStuffDefPair? First(List<ThingDef> declared,
                                                   HashSet<ThingDefStuffDefPair> target,
                                                   Func<ThingDef, bool> category)
        {
            foreach (ThingDef def in declared.Where(category))
            {
                foreach (ThingDefStuffDefPair pair in target)
                {
                    if (pair.thing == def)
                    {
                        return pair;
                    }
                }
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
