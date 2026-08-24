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
    /// is what lets CE clear the weapon out of the inventory. The first declared ranged weapon
    /// becomes the pawn's default ranged weapon, the first declared melee their preferred
    /// melee weapon.
    ///
    /// THE LOADOUT IS THE AUTHORITY over the defs it lists, and only those. It claims a def
    /// regardless of who remembered it first, because SS auto-remembers anything a pawn
    /// equips and a loadout built around a gun the pawn already carries would otherwise never
    /// be claimed. Player intent still outranks it: a forced weapon, a hand-cleared role, and
    /// a weapon taken out of the list by hand are all honoured, and each is recorded from the
    /// gizmo rather than guessed at (see GizmoIntent).
    ///
    /// Reconciled rather than event-driven, deliberately. SS and CE both mutate this state and
    /// so do other mods; a missed event is permanent, a missed reconcile cycle lasts until the
    /// next one. CE registers JobGiver_UpdateLoadout in the colonist behaviour tree, so this
    /// runs about twice a minute per colonist on its own — measured at 0.67 calls per colonist
    /// per 1000 ticks, 0.0005% of a 60fps frame at 20 colonists.
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
            if (!SupplyMod.Settings.loadoutWeaponsAsSidearms)
            {
                return;
            }
            Reconcile(pawn);
        }

        private static void Reconcile(Pawn pawn)
        {
            if (pawn == null || !pawn.IsColonist || pawn.Dead)
            {
                return;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
            SupplyGameComponent comp = SupplyGameComponent.Instance;
            if (memory?.RememberedWeapons == null || comp == null)
            {
                return;
            }

            Loadout loadout = pawn.GetLoadout();
            PawnTemplateRecord rec = comp.GetRecord(pawn, create: false);

            // No loadout means NO OPINION, not "declares nothing". CE reassigns every pawn of
            // a deleted loadout to the default one by writing its dictionary directly
            // (LoadoutManager.RemoveLoadout), and deleting a loadout is an unconfirmed
            // float-menu click — so treating that as an empty declaration would wipe every
            // sidearm on every pawn that used it, silently, with no undo.
            if (loadout == null || loadout.defaultLoadout)
            {
                if (rec != null && !pawn.IsColonist)
                {
                    comp.RemoveRecord(pawn);
                }
                return;
            }

            // GetSlotsFor, not Slots: on an ad-hoc loadout CE synthesises a slot for the
            // equipped primary, and both of CE's own readers use it. Reading raw Slots makes
            // the two mods disagree about what the same loadout says. It is a lazy iterator
            // over live state, so materialise it now.
            List<ThingDef> declared;
            try
            {
                declared = loadout.GetSlotsFor(pawn)
                                  .Where(s => s?.thingDef != null && s.thingDef.IsWeapon
                                              // A platform row specifies attachments; SS matches
                                              // def+stuff only, so claiming one would have it fetch
                                              // a platform built to the wrong spec.
                                              && !s.isWeaponPlatform)
                                  .Select(s => s.thingDef).Distinct().ToList();
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[Sidearms&Supply] Could not read {pawn}'s loadout slots: {e}",
                              0x53535235 ^ pawn.thingIDNumber);
                return;
            }

            if (rec == null && declared.Count == 0)
            {
                return;
            }
            rec ??= comp.GetRecord(pawn, create: true);

            // Read the forced state once, before anything can clear it. SS nulls a role as a
            // side effect of forgetting its last copy, so a guard that reads it after the
            // forget phase is reading wreckage.
            ThingDefStuffDefPair? forced = memory.ForcedWeapon;
            ThingDefStuffDefPair? forcedDrafted = memory.ForcedWeaponWhileDrafted;

            Step(pawn, () => ForgetUndeclared(memory, rec, declared, forced, forcedDrafted), "forget");
            Step(pawn, () => ClaimDeclared(pawn, memory, rec, declared), "claim");
            Step(pawn, () => AssertRoles(pawn, memory, rec, declared, forced), "roles");
        }

        /// <summary>
        /// Phases are wrapped one at a time: the forget phase commits before the claim phase
        /// starts, so a throw inside claim must not also cost the roles. Keyed per pawn AND
        /// per phase so a second, different failure is not swallowed by the first.
        /// </summary>
        private static void Step(Pawn pawn, Action step, string label)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[Sidearms&Supply] Loadout-sidearm {label} failed for {pawn}: {e}",
                              0x53535231 ^ (pawn?.thingIDNumber ?? 0) ^ label.GetHashCode());
            }
        }

        /// <summary>
        /// The template taketh away: pairs this projection claimed whose def the loadout no
        /// longer declares are forgotten. Exactly those pairs — a different material of the
        /// same def, added by the player, is not ours to delete. Nothing is dropped here;
        /// forgetting ends the compat patch's drop exemption and CE's own rules take over.
        /// </summary>
        private static void ForgetUndeclared(CompSidearmMemory memory, PawnTemplateRecord rec,
                                             List<ThingDef> declared, ThingDefStuffDefPair? forced,
                                             ThingDefStuffDefPair? forcedDrafted)
        {
            foreach (ThingDefStuffDefPair gone in rec.claimed.Where(p => !declared.Contains(p.thing)).ToList())
            {
                // A forced weapon outranks the loadout. Leaving the row does not revoke the
                // player's most explicit instruction, and forgetting the last copy would clear
                // it as a side effect with nothing to tell them it happened.
                if (gone == forced || gone == forcedDrafted)
                {
                    continue;
                }
                if (memory.RememberedWeapons.Contains(gone))
                {
                    memory.ForgetSidearmMemory(gone);
                }
                rec.claimed.Remove(gone);
            }
            // Suppression follows the row: remove a weapon and re-add it and it is managed
            // again, rather than staying silently suppressed forever.
            rec.forgotten.RemoveWhere(d => !declared.Contains(d));
        }

        /// <summary>
        /// The template giveth — but only for a weapon the pawn actually holds. A declared
        /// weapon the pawn has not got yet is CE's problem: the loadout row already makes CE
        /// fetch it, and guessing a material here would send SS chasing a specific stuff the
        /// loadout never asked for, since SS matches pairs exactly and CE matches defs.
        /// </summary>
        private static void ClaimDeclared(Pawn pawn, CompSidearmMemory memory, PawnTemplateRecord rec,
                                          List<ThingDef> declared)
        {
            List<ThingWithComps> carriedWeapons = pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true);

            foreach (ThingDef def in declared)
            {
                if (rec.forgotten.Contains(def))
                {
                    continue; // the player said carry it, do not wield it
                }
                ThingWithComps carried = carriedWeapons.FirstOrDefault(w => w.def == def);
                if (carried == null)
                {
                    continue;
                }
                ThingDefStuffDefPair pair = carried.toThingDefStuffDefPair();

                if (memory.RememberedWeapons.Contains(pair))
                {
                    if (!rec.claimed.Contains(pair))
                    {
                        rec.claimed.Add(pair); // the loadout owns what it lists, whoever got there first
                    }
                }
                else
                {
                    // SS decides what may be a sidearm at all — slot count, mass limits, the
                    // selection whitelist, the pacifist rule — and its retrieval never
                    // re-checks. Writing straight into the list would make those settings
                    // silently void for anything a loadout happens to name.
                    if (!StatCalculator.CanPickupSidearmType(pair, pawn, out string _))
                    {
                        continue;
                    }
                    memory.RememberedWeapons.Add(pair);
                    rec.claimed.Add(pair);
                }

                // The loadout asked for a knife and the pawn now carries a plasteel one:
                // release our claim on the material they no longer have. Only ours.
                foreach (ThingDefStuffDefPair stale in rec.claimed
                             .Where(p => p.thing == def && p != pair
                                         && !carriedWeapons.Any(w => w.toThingDefStuffDefPair() == p)).ToList())
                {
                    if (memory.RememberedWeapons.Contains(stale))
                    {
                        memory.ForgetSidearmMemory(stale);
                    }
                    rec.claimed.Remove(stale);
                }
            }
        }

        /// <summary>
        /// First declared ranged weapon is the default ranged weapon; first declared melee is
        /// the preferred melee weapon — first that the pawn actually remembers, so a weapon
        /// they have not got yet does not leave the role unset and SS picking by raw DPS.
        ///
        /// Player intent outranks all of it: a forced weapon of that category, the unarmed
        /// states, a role the player cleared by hand, and a role naming a weapon the loadout
        /// does not list which the pawn is still carrying.
        /// </summary>
        private static void AssertRoles(Pawn pawn, CompSidearmMemory memory, PawnTemplateRecord rec,
                                        List<ThingDef> declared, ThingDefStuffDefPair? forced)
        {
            if (memory.ForcedUnarmed)
            {
                return;
            }
            // SS's setters only clear a forced weapon of their OWN category, so a forced melee
            // weapon is no reason to abandon the ranged default.
            bool forcedRanged = forced.HasValue && forced.Value.thing != null && forced.Value.thing.IsRangedWeapon;
            bool forcedMelee = forced.HasValue && forced.Value.thing != null && forced.Value.thing.IsMeleeWeapon;

            if (!forcedRanged && !rec.rangedRoleVetoed
                && !PlayersAndInHand(pawn, memory.DefaultRangedWeapon, declared))
            {
                ThingDefStuffDefPair? pick = FirstRemembered(memory, declared, rec, d => d.IsRangedWeapon);
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
                ThingDefStuffDefPair? pick = FirstRemembered(memory, declared, rec, d => d.IsMeleeWeapon);
                if (pick.HasValue && memory.PreferredMeleeWeapon != pick)
                {
                    memory.SetMeleeWeaponTypeAsPreferred(pick.Value);
                }
            }
        }

        /// <summary>
        /// First declared def of this category that is remembered and not vetoed, preferring a
        /// pair this projection claimed so the role never names a material the pawn dropped.
        /// </summary>
        private static ThingDefStuffDefPair? FirstRemembered(CompSidearmMemory memory, List<ThingDef> declared,
                                                             PawnTemplateRecord rec, Func<ThingDef, bool> category)
        {
            foreach (ThingDef def in declared.Where(d => category(d) && !rec.forgotten.Contains(d)))
            {
                ThingDefStuffDefPair mine = rec.claimed.FirstOrDefault(
                    p => p.thing == def && memory.RememberedWeapons.Contains(p));
                if (mine.thing != null)
                {
                    return mine;
                }
                ThingDefStuffDefPair any = memory.RememberedWeapons.FirstOrDefault(p => p.thing == def);
                if (any.thing != null)
                {
                    return any;
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
