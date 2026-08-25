using System;
using HarmonyLib;
using UnityEngine;
using SimpleSidearms.rimworld;
using Verse;

namespace CESidearmsSupply.Patches
{
    /// <summary>
    /// Records what the player asked for, at the one place in Simple Sidearms where "the
    /// player" exists.
    ///
    /// No method that mutates sidearm memory distinguishes the player from SS's own
    /// automation — ForgetSidearmMemory is reached both from the gizmo's forget button and
    /// from InformOfDroppedSidearm, which fires on every weapon swap. Asking each method
    /// "was this the player?" has no answer.
    ///
    /// The gizmo's input handler does have one. Gizmo_SidearmsList.handleInteraction has a
    /// single caller, guarded on a real mouse button, and nothing automated can reach it. So
    /// mark that call and let the memory hooks read the mark: anything they see inside it is
    /// the player's doing, transitively, however deep SS routes it.
    ///
    /// The default is "not the player". If a patch here fails to apply, the gesture goes
    /// quiet and the player clicks again; nothing is recorded that they did not ask for.
    /// The previous design defaulted the other way and silently invented intent for years
    /// of ordinary play.
    /// </summary>
    public static class PlayerIntent
    {
        [ThreadStatic] private static int gizmoDepth;

        internal static bool PlayerIsDriving => gizmoDepth > 0;

        internal static void Enter() => gizmoDepth++;

        internal static void Exit()
        {
            if (gizmoDepth > 0)
            {
                gizmoDepth--;
            }
        }

        internal static CompLoadoutSidearms RecordFor(CompSidearmMemory memory)
        {
            Pawn pawn = memory?.Owner;
            return pawn != null && pawn.IsColonist ? CompLoadoutSidearms.For(pawn) : null;
        }

        /// <summary>
        /// Shared by every patch here: no target, no feature, named error, no throw.
        ///
        /// The parameter types are mandatory. AccessTools.Method with a null parameter list
        /// rethrows on an ambiguous match, which would abort PatchAll for the whole assembly
        /// and take the loadout projection down with it — the failure these guards exist to
        /// prevent.
        /// </summary>
        internal static bool Require(string method, Type[] args, string consequence)
        {
            if (AccessTools.Method(typeof(CompSidearmMemory), method, args) != null)
            {
                return true;
            }
            Log.Error($"[Sidearms&Supply] CompSidearmMemory.{method} not found — {consequence} "
                      + "Simple Sidearms probably moved it.");
            return false;
        }
    }

    /// <summary>
    /// The scope. A finalizer rather than a postfix: finalizers run even when the original
    /// throws, and a leaked depth would silently disable every hook below for the session.
    /// </summary>
    [HarmonyPatch(typeof(Gizmo_SidearmsList), nameof(Gizmo_SidearmsList.handleInteraction))]
    public static class Gizmo_SidearmsList_handleInteraction_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(Gizmo_SidearmsList), "handleInteraction",
                                   new[] { typeof(Gizmo_SidearmsList.SidearmsListInteraction), typeof(Event) }) != null)
            {
                return true;
            }
            Log.Error("[Sidearms&Supply] Gizmo_SidearmsList.handleInteraction not found — player "
                      + "decisions in the sidearm gizmo will not be recorded. Simple Sidearms "
                      + "probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix() => PlayerIntent.Enter();

        [HarmonyFinalizer]
        public static void Finalizer() => PlayerIntent.Exit();
    }

    /// <summary>Inside the gizmo, a forget is the player saying "carry it, do not wield it".</summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.ForgetSidearmMemory))]
    public static class CompSidearmMemory_ForgetSidearmMemory_Patch
    {
        public static bool Prepare() => PlayerIntent.Require("ForgetSidearmMemory",
            new[] { typeof(ThingDefStuffDefPair) }, "taking a loadout weapon out of the sidearm list by hand will not stick.");

        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance, ThingDefStuffDefPair weaponMemory)
        {
            if (!PlayerIntent.PlayerIsDriving || weaponMemory.thing == null)
            {
                return;
            }
            CompLoadoutSidearms rec = PlayerIntent.RecordFor(__instance);
            if (rec == null)
            {
                return;
            }
            if (!rec.dontEquip.Contains(weaponMemory))
            {
                rec.dontEquip.Add(weaponMemory);
            }
            // Pair-level, matching how the claim was recorded: a different material of the
            // same def may be the player's own and is not ours to disown.
            rec.claimed.RemoveAll(p => p == weaponMemory);
        }
    }

    /// <summary>
    /// Putting it back by hand withdraws the exclusion.
    ///
    /// Wider than the gizmo, deliberately. "Equip as sidearm" and plain "Equip" from the
    /// right-click menu both mean the player wants this weapon, and both reach here from a
    /// job rather than from the gizmo — SS's own float menu issues EquipSecondary through
    /// TryTakeOrderedJob, which stamps playerForced, and vanilla's Equip does the same. CE's
    /// autonomous equip does not, which is the distinction that matters.
    ///
    /// Only the WITHDRAWAL side is widened. Recording an exclusion the player did not ask
    /// for is silent and permanent; lifting one they did not ask for costs a single click to
    /// re-express. The asymmetry is the point.
    /// </summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.InformOfAddedSidearm))]
    public static class CompSidearmMemory_InformOfAddedSidearm_Patch
    {
        public static bool Prepare() => PlayerIntent.Require("InformOfAddedSidearm",
            new[] { typeof(Thing) }, "putting a weapon back in the sidearm list by hand will not resume management.");

        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance, Thing weapon)
        {
            if (weapon?.def == null)
            {
                return;
            }
            bool playerOrdered = __instance?.Owner?.CurJob?.playerForced ?? false;
            if (!PlayerIntent.PlayerIsDriving && !playerOrdered)
            {
                return;
            }
            // Symmetric with the forget above: putting this exact weapon back withdraws the
            // exclusion on it, and leaves any other material of the same def alone.
            PlayerIntent.RecordFor(__instance)?.dontEquip.Remove(new ThingDefStuffDefPair(weapon.def, weapon.Stuff));
        }
    }

    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.UnsetRangedWeaponDefault))]
    public static class CompSidearmMemory_UnsetRangedWeaponDefault_Patch
    {
        public static bool Prepare() => PlayerIntent.Require("UnsetRangedWeaponDefault",
            Type.EmptyTypes, "clearing the default ranged weapon by hand will be undone by the next reconcile.");

        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance)
        {
            if (!PlayerIntent.PlayerIsDriving)
            {
                return;
            }
            CompLoadoutSidearms rec = PlayerIntent.RecordFor(__instance);
            if (rec != null)
            {
                rec.rangedRoleVetoed = true;
            }
        }
    }

    /// <summary>
    /// The melee twin, with one extra condition. SS reaches the same method from the Unarmed
    /// icon to mean "stop preferring unarmed", which is not a statement about melee weapons
    /// at all — so only treat it as a veto if there was a preference to clear.
    /// </summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.UnsetMeleeWeaponPreference))]
    public static class CompSidearmMemory_UnsetMeleeWeaponPreference_Patch
    {
        public static bool Prepare() => PlayerIntent.Require("UnsetMeleeWeaponPreference",
            Type.EmptyTypes, "clearing the preferred melee weapon by hand will be undone by the next reconcile.");

        [HarmonyPrefix]
        public static void Prefix(CompSidearmMemory __instance, out bool __state)
        {
            __state = __instance?.PreferredMeleeWeapon.HasValue ?? false;
        }

        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance, bool __state)
        {
            if (!PlayerIntent.PlayerIsDriving || !__state)
            {
                return;
            }
            CompLoadoutSidearms rec = PlayerIntent.RecordFor(__instance);
            if (rec != null)
            {
                rec.meleeRoleVetoed = true;
            }
        }
    }

    /// <summary>Setting a role by hand withdraws the veto on that category.</summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.SetRangedWeaponTypeAsDefault))]
    public static class CompSidearmMemory_SetRangedWeaponTypeAsDefault_Patch
    {
        public static bool Prepare() => PlayerIntent.Require("SetRangedWeaponTypeAsDefault",
            new[] { typeof(ThingDefStuffDefPair) }, "setting the default ranged weapon by hand will not resume loadout management.");

        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance)
        {
            if (!PlayerIntent.PlayerIsDriving)
            {
                return;
            }
            CompLoadoutSidearms rec = PlayerIntent.RecordFor(__instance);
            if (rec != null)
            {
                rec.rangedRoleVetoed = false;
            }
        }
    }

    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.SetMeleeWeaponTypeAsPreferred))]
    public static class CompSidearmMemory_SetMeleeWeaponTypeAsPreferred_Patch
    {
        public static bool Prepare() => PlayerIntent.Require("SetMeleeWeaponTypeAsPreferred",
            new[] { typeof(ThingDefStuffDefPair) }, "setting the preferred melee weapon by hand will not resume loadout management.");

        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance)
        {
            if (!PlayerIntent.PlayerIsDriving)
            {
                return;
            }
            CompLoadoutSidearms rec = PlayerIntent.RecordFor(__instance);
            if (rec != null)
            {
                rec.meleeRoleVetoed = false;
            }
        }
    }
}
