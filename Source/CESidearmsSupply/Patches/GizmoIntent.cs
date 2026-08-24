using System;
using HarmonyLib;
using SimpleSidearms.rimworld;
using Verse;

namespace CESidearmsSupply.Patches
{
    /// <summary>
    /// Player intent, read at the points Simple Sidearms lets a player express it.
    ///
    /// The projection needs two facts SS does not store: "I took this weapon out of the list
    /// on purpose" and "I cleared this role on purpose". Neither can be recovered afterwards
    /// from the state alone — SS drops memories by itself, forgetting the outgoing primary on
    /// every equip — so a missing memory says nothing about what the player wanted.
    ///
    /// Both are reachable directly, without guessing:
    ///
    ///   UnsetRangedWeaponDefault and UnsetMeleeWeaponPreference have no callers anywhere in
    ///   SS outside Gizmo_SidearmsList. Reaching them at all means the player clicked.
    ///
    ///   ForgetSidearmMemory has exactly two callers: the gizmo's forget button, and
    ///   InformOfDroppedSidearm — the one that fires on equips and drops. So a forget that
    ///   did not come from a drop came from the player.
    ///
    /// The module's own writes are bracketed by Ours() so the projection never reads its own
    /// bookkeeping as a decision.
    ///
    /// Caveat worth knowing: these are public methods, so another mod calling ForgetSidearmMemory
    /// outside a drop would be read as a player forget. That is a far narrower exposure than
    /// inferring intent from state, where SS's own equip path tripped it several times a day.
    /// </summary>
    public static class PlayerIntent
    {
        [ThreadStatic] private static int dropDepth;
        [ThreadStatic] private static int oursDepth;

        internal static bool Ignoring => dropDepth > 0 || oursDepth > 0;

        /// <summary>Bracket the module's own memory writes so they are not read as intent.</summary>
        public static Scope Ours() => new Scope(ours: true);

        internal static void EnterDrop() => dropDepth++;
        internal static void ExitDrop() => dropDepth--;

        public struct Scope : IDisposable
        {
            private readonly bool ours;

            internal Scope(bool ours)
            {
                this.ours = ours;
                if (ours)
                {
                    oursDepth++;
                }
                else
                {
                    dropDepth++;
                }
            }

            public void Dispose()
            {
                if (ours)
                {
                    oursDepth--;
                }
                else
                {
                    dropDepth--;
                }
            }
        }

        internal static PawnTemplateRecord RecordFor(CompSidearmMemory memory, bool create)
        {
            Pawn pawn = memory?.Owner;
            SupplyGameComponent comp = SupplyGameComponent.Instance;
            if (pawn == null || !pawn.IsColonist || comp == null)
            {
                return null;
            }
            return comp.GetRecord(pawn, create);
        }
    }

    /// <summary>A forget that came from a drop or an equip is not a decision about the weapon.</summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.InformOfDroppedSidearm))]
    public static class CompSidearmMemory_InformOfDroppedSidearm_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ref bool __state)
        {
            __state = true;
            PlayerIntent.EnterDrop();
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (__state)
            {
                PlayerIntent.ExitDrop();
            }
        }
    }

    /// <summary>Everything else that reaches ForgetSidearmMemory is the gizmo's forget button.</summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.ForgetSidearmMemory))]
    public static class CompSidearmMemory_ForgetSidearmMemory_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance, ThingDefStuffDefPair weaponMemory)
        {
            if (PlayerIntent.Ignoring || weaponMemory.thing == null)
            {
                return;
            }
            try
            {
                PawnTemplateRecord rec = PlayerIntent.RecordFor(__instance, create: true);
                if (rec == null)
                {
                    return;
                }
                rec.forgotten.Add(weaponMemory.thing);
                rec.claimed.RemoveAll(p => p.thing == weaponMemory.thing);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Sidearms&Supply] Could not record a sidearm forget: " + e, 0x53535233);
            }
        }
    }

    /// <summary>Putting it back in the list by hand withdraws the forget.</summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.InformOfAddedSidearm))]
    public static class CompSidearmMemory_InformOfAddedSidearm_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance, Thing weapon)
        {
            if (PlayerIntent.Ignoring || weapon?.def == null)
            {
                return;
            }
            PlayerIntent.RecordFor(__instance, create: false)?.forgotten.Remove(weapon.def);
        }
    }

    /// <summary>
    /// Clearing a role is a veto. SS has a persisted flag for "deliberately no melee
    /// preference" but none for ranged, so without recording it the projection would put a
    /// cleared default ranged weapon back within the minute and the click would look broken.
    /// </summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.UnsetRangedWeaponDefault))]
    public static class CompSidearmMemory_UnsetRangedWeaponDefault_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance)
        {
            if (PlayerIntent.Ignoring)
            {
                return;
            }
            PawnTemplateRecord rec = PlayerIntent.RecordFor(__instance, create: true);
            if (rec != null)
            {
                rec.rangedRoleVetoed = true;
            }
        }
    }

    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.UnsetMeleeWeaponPreference))]
    public static class CompSidearmMemory_UnsetMeleeWeaponPreference_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance)
        {
            if (PlayerIntent.Ignoring)
            {
                return;
            }
            PawnTemplateRecord rec = PlayerIntent.RecordFor(__instance, create: true);
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
        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance)
        {
            if (PlayerIntent.Ignoring)
            {
                return;
            }
            PawnTemplateRecord rec = PlayerIntent.RecordFor(__instance, create: false);
            if (rec != null)
            {
                rec.rangedRoleVetoed = false;
            }
        }
    }

    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.SetMeleeWeaponTypeAsPreferred))]
    public static class CompSidearmMemory_SetMeleeWeaponTypeAsPreferred_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance)
        {
            if (PlayerIntent.Ignoring)
            {
                return;
            }
            PawnTemplateRecord rec = PlayerIntent.RecordFor(__instance, create: false);
            if (rec != null)
            {
                rec.meleeRoleVetoed = false;
            }
        }
    }
}
