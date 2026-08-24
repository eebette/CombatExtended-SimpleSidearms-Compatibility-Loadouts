using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SimpleSidearms.rimworld;
using Verse;

namespace CESidearmsSupply.Patches
{
    /// <summary>
    /// Records what the player meant, so the reconcile never has to guess it.
    ///
    /// The projection needs two facts Simple Sidearms does not store: "I took this weapon out
    /// of the list on purpose" and "I cleared this role on purpose". Neither can be recovered
    /// afterwards — SS's forget button calls the same ForgetSidearmMemory that its equip
    /// interception calls on every weapon swap, with nothing to tell them apart. Inferring
    /// intent from a missing memory therefore reads an ordinary equip as a deliberate forget.
    ///
    /// So observe the gizmo instead: snapshot before the click, diff after. Anything that
    /// disappeared across that call disappeared because the player clicked. This does not
    /// depend on SS's interaction cascade or on which branch ran, only on the outcome, which
    /// is the part of it least likely to change.
    /// </summary>
    [HarmonyPatch(typeof(Gizmo_SidearmsList), nameof(Gizmo_SidearmsList.handleInteraction))]
    public static class Gizmo_SidearmsList_handleInteraction_Patch
    {
        public class Snapshot
        {
            public Pawn pawn;
            public List<ThingDefStuffDefPair> remembered;
            public ThingDefStuffDefPair? ranged;
            public ThingDefStuffDefPair? melee;
        }

        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(Gizmo_SidearmsList), "handleInteraction") != null)
            {
                return true;
            }
            Log.Error("[Sidearms&Supply] Gizmo_SidearmsList.handleInteraction not found — the "
                      + "sidearm gizmo will not be observed, so forgetting a loadout weapon by hand "
                      + "will not stick. Simple Sidearms probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix(Gizmo_SidearmsList __instance, ref Snapshot __state)
        {
            __state = null;
            try
            {
                Pawn pawn = __instance?.parent;
                if (pawn == null || !pawn.IsColonist)
                {
                    return;
                }
                CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
                if (memory?.RememberedWeapons == null)
                {
                    return;
                }
                __state = new Snapshot
                {
                    pawn = pawn,
                    remembered = new List<ThingDefStuffDefPair>(memory.RememberedWeapons),
                    ranged = memory.DefaultRangedWeapon,
                    melee = memory.PreferredMeleeWeapon,
                };
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Sidearms&Supply] Gizmo snapshot failed: " + e, 0x53535233);
            }
        }

        [HarmonyPostfix]
        public static void Postfix(Gizmo_SidearmsList __instance, Snapshot __state)
        {
            if (__state == null)
            {
                return;
            }
            try
            {
                Diff(__state);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Sidearms&Supply] Gizmo intent capture failed: " + e, 0x53535234);
            }
        }

        private static void Diff(Snapshot before)
        {
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(before.pawn);
            SupplyGameComponent comp = SupplyGameComponent.Instance;
            if (memory?.RememberedWeapons == null || comp == null)
            {
                return;
            }
            List<ThingDefStuffDefPair> after = memory.RememberedWeapons;
            PawnTemplateRecord rec = null;

            // A def with no copies left is one the player just removed from the list.
            foreach (ThingDef gone in before.remembered.Select(p => p.thing).Distinct()
                                            .Where(d => d != null && !after.Any(p => p.thing == d)))
            {
                rec ??= comp.GetRecord(before.pawn, create: true);
                rec.forgotten.Add(gone);
                rec.claimed.RemoveAll(p => p.thing == gone);
            }

            // Adding it back by hand is how the player takes that back.
            foreach (ThingDef added in after.Select(p => p.thing).Distinct()
                                            .Where(d => d != null && !before.remembered.Any(p => p.thing == d)))
            {
                rec ??= comp.GetRecord(before.pawn, create: true);
                rec.forgotten.Remove(added);
            }

            // Clearing a role is a veto; setting one by hand withdraws the veto. Simple
            // Sidearms has a flag for "deliberately no melee preference" but none for ranged,
            // so without this the projection would restore a cleared ranged default forever.
            rec = ApplyRoleVeto(before.ranged, memory.DefaultRangedWeapon, rec, before.pawn, comp,
                                (r, v) => r.rangedRoleVetoed = v);
            ApplyRoleVeto(before.melee, memory.PreferredMeleeWeapon, rec, before.pawn, comp,
                          (r, v) => r.meleeRoleVetoed = v);
        }

        private static PawnTemplateRecord ApplyRoleVeto(ThingDefStuffDefPair? before, ThingDefStuffDefPair? after,
                                                        PawnTemplateRecord rec, Pawn pawn, SupplyGameComponent comp,
                                                        Action<PawnTemplateRecord, bool> set)
        {
            if (before.HasValue && !after.HasValue)
            {
                rec ??= comp.GetRecord(pawn, create: true);
                set(rec, true);
            }
            else if (after.HasValue && after != before)
            {
                rec ??= comp.GetRecord(pawn, create: true);
                set(rec, false);
            }
            return rec;
        }
    }
}
