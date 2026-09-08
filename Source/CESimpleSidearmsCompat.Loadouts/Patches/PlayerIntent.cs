using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using CombatExtended;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;

namespace CESimpleSidearmsCompat.Loadouts.Patches
{
    /// <summary>
    /// Wrapper for recording player interactions in the Simple Sidearms UI gizmo.
    /// </summary>
    public static class PlayerIntent
    {
        [ThreadStatic] private static int gizmoDepth;
        [ThreadStatic] private static int choiceDepth;

        internal static bool PlayerIsDriving => gizmoDepth > 0;

        internal static bool PlayerChoosing => choiceDepth > 0;

        internal static void EnterChoice() => choiceDepth++;

        internal static void ExitChoice()
        {
            if (choiceDepth > 0)
            {
                choiceDepth--;
            }
        }

        internal static void Enter() => gizmoDepth++;

        internal static void Exit()
        {
            if (gizmoDepth > 0)
            {
                gizmoDepth--;
            }
        }

        /// <summary>
        /// Flag for if the feature is on and the pawn has a real (non-default) loadout.
        /// </summary>
        internal static bool ManagedPawn(Pawn pawn)
        {
            if (pawn == null || !pawn.IsColonist || !LoadoutsSessionComponent.Enabled)
            {
                return false;
            }
            // Absent from the dictionary means default loadout means not managed.
            return LoadoutManager.AssignedLoadouts.TryGetValue(pawn, out Loadout loadout)
                   && loadout != null && !loadout.defaultLoadout;
        }

        internal static CompLoadoutSidearms RecordFor(CompSidearmMemory memory)
        {
            Pawn pawn = memory?.Owner;
            CompLoadoutSidearms rec = pawn != null && pawn.IsColonist ? CompLoadoutSidearms.For(pawn) : null;
            rec?.SyncAssignment(pawn);
            return rec;
        }
    }

    /// <summary>
    /// The scope.
    /// </summary>
    [HarmonyPatch(typeof(Gizmo_SidearmsList), nameof(Gizmo_SidearmsList.handleInteraction),
                  new[] { typeof(Gizmo_SidearmsList.SidearmsListInteraction), typeof(Event) })]
    public static class Gizmo_SidearmsList_handleInteraction_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(Gizmo_SidearmsList), "handleInteraction",
                                   new[] { typeof(Gizmo_SidearmsList.SidearmsListInteraction), typeof(Event) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] Gizmo_SidearmsList.handleInteraction not found — player "
                      + "decisions in the sidearm gizmo will not be recorded. Simple Sidearms "
                      + "probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix() => PlayerIntent.Enter();

        [HarmonyFinalizer]
        public static void Finalizer() => PlayerIntent.Exit();
    }

    /// <summary>Weapons forgotten in the gizmo are not part of the sidearm inventory.</summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.ForgetSidearmMemory),
                  new[] { typeof(ThingDefStuffDefPair) })]
    public static class CompSidearmMemory_ForgetSidearmMemory_Patch
    {
        public static bool Prepare() => PatchGuard.Require("ForgetSidearmMemory",
            new[] { typeof(ThingDefStuffDefPair) }, "taking a loadout weapon out of the sidearm list by hand will not stick.");

        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance, ThingDefStuffDefPair weaponMemory)
        {
            if (!PlayerIntent.PlayerIsDriving || weaponMemory.thing == null
                || !PlayerIntent.ManagedPawn(__instance?.Owner))
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
            int i = rec.claimed.IndexOf(weaponMemory);
            if (i >= 0)
            {
                rec.claimed.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Manual Equip action withdraws the exclusion.
    /// </summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.InformOfAddedSidearm),
                  new[] { typeof(Thing) })]
    public static class CompSidearmMemory_InformOfAddedSidearm_Patch
    {
        public static bool Prepare() => PatchGuard.Require("InformOfAddedSidearm",
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
            // Withdraw the exclusion.
            PlayerIntent.RecordFor(__instance)?.dontEquip.Remove(new ThingDefStuffDefPair(weapon.def, weapon.Stuff));
        }
    }

    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.UnsetRangedWeaponDefault), new Type[0])]
    public static class CompSidearmMemory_UnsetRangedWeaponDefault_Patch
    {
        public static bool Prepare() => PatchGuard.Require("UnsetRangedWeaponDefault",
            Type.EmptyTypes, "clearing the default ranged weapon by hand will be undone by the next reconcile.");

        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance)
        {
            if (!PlayerIntent.PlayerIsDriving || !PlayerIntent.ManagedPawn(__instance?.Owner))
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
    /// The melee twin of the patch.
    /// </summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.UnsetMeleeWeaponPreference), new Type[0])]
    public static class CompSidearmMemory_UnsetMeleeWeaponPreference_Patch
    {
        public static bool Prepare() => PatchGuard.Require("UnsetMeleeWeaponPreference",
            Type.EmptyTypes, "clearing the preferred melee weapon by hand will be undone by the next reconcile.");

        [HarmonyPrefix]
        public static void Prefix(CompSidearmMemory __instance, out bool __state)
        {
            __state = __instance?.PreferredMeleeWeapon.HasValue ?? false;
        }

        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance, bool __state)
        {
            if (!PlayerIntent.PlayerIsDriving || !__state
                || !PlayerIntent.ManagedPawn(__instance?.Owner))
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
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.SetRangedWeaponTypeAsDefault),
                  new[] { typeof(ThingDefStuffDefPair) })]
    public static class CompSidearmMemory_SetRangedWeaponTypeAsDefault_Patch
    {
        public static bool Prepare() => PatchGuard.Require("SetRangedWeaponTypeAsDefault",
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

    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.SetMeleeWeaponTypeAsPreferred),
                  new[] { typeof(ThingDefStuffDefPair) })]
    public static class CompSidearmMemory_SetMeleeWeaponTypeAsPreferred_Patch
    {
        public static bool Prepare() => PatchGuard.Require("SetMeleeWeaponTypeAsPreferred",
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

    /// <summary>
    /// Blocks the game from arming a pawn with a manually excluded weapon.
    /// </summary>
    [HarmonyPatch(typeof(EquipmentUtility), nameof(EquipmentUtility.CanEquip),
                  new[] { typeof(Thing), typeof(Pawn), typeof(string), typeof(bool) },
                  new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal })]
    public static class EquipmentUtility_CanEquip_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(EquipmentUtility), nameof(EquipmentUtility.CanEquip),
                    new[] { typeof(Thing), typeof(Pawn), typeof(string).MakeByRefType(), typeof(bool) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] EquipmentUtility.CanEquip not found — Combat Extended's "
                      + "inventory-side weapon picks will stop refusing excluded weapons "
                      + "(the sidearm list itself stays clean). RimWorld probably moved it.");
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(Thing thing, Pawn pawn, ref string cantReason, ref bool __result)
        {
            try
            {
                PostfixInner(thing, pawn, ref cantReason, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[CE+SS Loadouts] CanEquip veto failed: " + e,
                              0x53535245 ^ (thing?.thingIDNumber ?? 0));
            }
        }

        private static void PostfixInner(Thing thing, Pawn pawn, ref string cantReason, ref bool __result)
        {
            if (!__result || PlayerIntent.PlayerChoosing || thing?.def == null || pawn == null)
            {
                return;
            }
            // Only weapons the pawn is already carrying.
            if (pawn.inventory?.innerContainer == null || !pawn.inventory.innerContainer.Contains(thing))
            {
                return;
            }
            CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
            rec?.SyncAssignment(pawn);
            if (rec == null || rec.dontEquip.Count == 0 || !PlayerIntent.ManagedPawn(pawn))
            {
                return;
            }
            if (rec.dontEquip.Contains(new ThingDefStuffDefPair(thing.def, thing.Stuff)))
            {
                __result = false;
                cantReason = "excluded from " + pawn.LabelShort + "'s sidearm rotation";
            }
        }
    }

    /// <summary>
    /// Forget the exclusion when manually forced via gizmo action.
    /// </summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.SetWeaponAsForced),
                  new[] { typeof(ThingDefStuffDefPair), typeof(bool) })]
    public static class CompSidearmMemory_SetWeaponAsForced_Patch
    {
        public static bool Prepare() => PatchGuard.Require("SetWeaponAsForced",
            new[] { typeof(ThingDefStuffDefPair), typeof(bool) },
            "forcing an excluded weapon while drafted will not withdraw its exclusion.");

        [HarmonyPostfix]
        public static void Postfix(CompSidearmMemory __instance, ThingDefStuffDefPair weapon)
        {
            if (!PlayerIntent.PlayerIsDriving || weapon.thing == null
                || !PlayerIntent.ManagedPawn(__instance?.Owner))
            {
                return;
            }
            PlayerIntent.RecordFor(__instance)?.dontEquip.Remove(weapon);
        }
    }

    /// <summary>
    /// Wrap the caravan gear tab's Equip surface with player intent.
    /// </summary>
    [HarmonyPatch(typeof(RimWorld.Planet.WITab_Caravan_Gear), "TryEquipDraggedItem",
                  new[] { typeof(Pawn) })]
    public static class WITab_Caravan_Gear_TryEquipDraggedItem_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(RimWorld.Planet.WITab_Caravan_Gear), "TryEquipDraggedItem",
                                   new[] { typeof(Pawn) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] WITab_Caravan_Gear.TryEquipDraggedItem not found — "
                      + "equipping an excluded weapon from the caravan gear tab will be refused. "
                      + "RimWorld probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix() => PlayerIntent.EnterChoice();

        [HarmonyFinalizer]
        public static void Finalizer() => PlayerIntent.ExitChoice();
    }

    /// <summary>
    /// Register weapon exclusions with the equipSpecificWeapon funnel (SS's idle re-arm, its melee
    /// swap when an enemy closes, its post-shot swap for single-use weapons, and its auto-undraft re-arm)
    /// </summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipSpecificWeapon),
                  new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) })]
    public static class WeaponAssingment_equipSpecificWeapon_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(WeaponAssingment), nameof(WeaponAssingment.equipSpecificWeapon),
                    new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] WeaponAssingment.equipSpecificWeapon not found — Simple "
                      + "Sidearms can still arm a pawn with an excluded weapon on its own. "
                      + "Simple Sidearms probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, ThingWithComps weapon, ref bool __result)
        {
            try
            {
                return PrefixInner(pawn, weapon, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[CE+SS Loadouts] equip funnel guard failed: " + e,
                              0x53535246 ^ (pawn?.thingIDNumber ?? 0));
                return true;
            }
        }

        private static bool PrefixInner(Pawn pawn, ThingWithComps weapon, ref bool __result)
        {
            // weapon == null is SS unequipping to unarmed.
            if (weapon?.def == null || pawn == null || !PlayerIntent.ManagedPawn(pawn))
            {
                return true;
            }
            // Player exemption here would leak machine work through.
            CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
            rec?.SyncAssignment(pawn);
            if (rec == null || rec.dontEquip.Count == 0)
            {
                return true;
            }
            ThingDefStuffDefPair pair = weapon.toThingDefStuffDefPair();
            if (!rec.dontEquip.Contains(pair))
            {
                return true;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
            if (memory != null && (memory.ForcedWeapon == pair || memory.ForcedWeaponWhileDrafted == pair))
            {
                return true;
            }
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Wrap CE's inventory tab context menu with PlayerIntent
    /// </summary>
    [HarmonyPatch(typeof(ITab_Inventory), nameof(ITab_Inventory.DrawThingRowCE),
                  new[] { typeof(float), typeof(float), typeof(Thing), typeof(bool) },
                  new[] { ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    public static class ITab_Inventory_DrawThingRowCE_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(ITab_Inventory), nameof(ITab_Inventory.DrawThingRowCE),
                    new[] { typeof(float).MakeByRefType(), typeof(float), typeof(Thing), typeof(bool) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] ITab_Inventory.DrawThingRowCE not found — the inventory "
                      + "tab will show an excluded weapon's Equip entry as refused instead of "
                      + "offering it. Combat Extended probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix() => PlayerIntent.EnterChoice();

        [HarmonyFinalizer]
        public static void Finalizer() => PlayerIntent.ExitChoice();
    }

    /// <summary>
    /// Array filtering functions for excluded weapons.
    /// </summary>
    public static class SelectionFilter
    {
        [ThreadStatic] private static Pawn hidingExcludedFor;

        internal static bool Begin(Pawn pawn)
        {
            if (hidingExcludedFor != null || pawn == null)
            {
                return false;
            }
            hidingExcludedFor = pawn;
            return true;
        }

        internal static void End(bool ours)
        {
            if (ours)
            {
                hidingExcludedFor = null;
            }
        }

        internal static void Filter(Pawn pawn, List<ThingWithComps> list)
        {
            if (list == null || pawn == null || hidingExcludedFor != pawn
                || !PlayerIntent.ManagedPawn(pawn))
            {
                return;
            }
            CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
            if (rec == null)
            {
                return;
            }
            rec.SyncAssignment(pawn);
            if (rec.dontEquip.Count == 0)
            {
                return;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
            list.RemoveAll(w =>
            {
                if (w?.def == null)
                {
                    return false;
                }
                ThingDefStuffDefPair pair = w.toThingDefStuffDefPair();
                if (!rec.dontEquip.Contains(pair))
                {
                    return false;
                }
                // A force outranks the exclusion, matching the funnel and the reconcile.
                return memory == null
                       || (memory.ForcedWeapon != pair && memory.ForcedWeaponWhileDrafted != pair);
            });
        }
    }

    [HarmonyPatch(typeof(GettersFilters), nameof(GettersFilters.findBestRangedWeapon),
                  new[] { typeof(Pawn), typeof(LocalTargetInfo?), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
    public static class GettersFilters_findBestRangedWeapon_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(GettersFilters), nameof(GettersFilters.findBestRangedWeapon),
                    new[] { typeof(Pawn), typeof(LocalTargetInfo?), typeof(bool), typeof(bool), typeof(bool), typeof(bool) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] GettersFilters.findBestRangedWeapon not found — Simple "
                      + "Sidearms' ranged picker can nominate an excluded weapon, and the late "
                      + "refusal makes its preference tree fall through to melee/unarmed instead "
                      + "of the runner-up gun. Simple Sidearms probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix(Pawn pawn, out bool __state) => __state = SelectionFilter.Begin(pawn);

        [HarmonyFinalizer]
        public static void Finalizer(bool __state) => SelectionFilter.End(__state);
    }

    [HarmonyPatch(typeof(GettersFilters), nameof(GettersFilters.findBestMeleeWeapon),
                  new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool), typeof(Pawn) },
                  new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    public static class GettersFilters_findBestMeleeWeapon_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(GettersFilters), nameof(GettersFilters.findBestMeleeWeapon),
                    new[] { typeof(Pawn), typeof(ThingWithComps).MakeByRefType(), typeof(bool), typeof(bool), typeof(Pawn) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] GettersFilters.findBestMeleeWeapon not found — Simple "
                      + "Sidearms' melee picker can nominate an excluded weapon, and the late "
                      + "refusal leaves the pawn unarmed instead of taking the runner-up blade. "
                      + "Simple Sidearms probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix(Pawn pawn, out bool __state) => __state = SelectionFilter.Begin(pawn);

        [HarmonyFinalizer]
        public static void Finalizer(bool __state) => SelectionFilter.End(__state);
    }

    /// <summary>Applies SelectionFilter to equipBestByStatModifiers (for tools).</summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipBestWeaponFromInventoryByStatModifiers),
                  new[] { typeof(Pawn), typeof(List<StatDef>) })]
    public static class WeaponAssingment_equipBestByStatModifiers_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(WeaponAssingment), nameof(WeaponAssingment.equipBestWeaponFromInventoryByStatModifiers),
                    new[] { typeof(Pawn), typeof(List<StatDef>) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] WeaponAssingment.equipBestWeaponFromInventoryByStatModifiers "
                      + "not found — excluding a tool can suspend tool auto-switching. "
                      + "Simple Sidearms probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix(Pawn pawn, out bool __state) => __state = SelectionFilter.Begin(pawn);

        [HarmonyFinalizer]
        public static void Finalizer(bool __state) => SelectionFilter.End(__state);
    }

    [HarmonyPatch(typeof(PeteTimesSix.SimpleSidearms.Extensions), nameof(PeteTimesSix.SimpleSidearms.Extensions.GetCarriedWeapons),
                  new[] { typeof(Pawn), typeof(bool), typeof(bool) })]
    public static class Extensions_GetCarriedWeapons_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(PeteTimesSix.SimpleSidearms.Extensions), nameof(PeteTimesSix.SimpleSidearms.Extensions.GetCarriedWeapons),
                    new[] { typeof(Pawn), typeof(bool), typeof(bool) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] Extensions.GetCarriedWeapons not found — the exclusion "
                      + "cannot be hidden from Simple Sidearms' pickers. "
                      + "Simple Sidearms probably moved it.");
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, List<ThingWithComps> __result)
        {
            try
            {
                SelectionFilter.Filter(pawn, __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[CE+SS Loadouts] selection filter failed: " + e,
                              0x53535247 ^ (pawn?.thingIDNumber ?? 0));
            }
        }
    }

    /// <summary>
    /// Wraps ITab_Inventory_SyncedTrySwitchToWeapon with PlayerIntent for Inventory context menu.
    /// </summary>
    [HarmonyPatch(typeof(ITab_Inventory), "SyncedTrySwitchToWeapon",
                  new[] { typeof(CompInventory), typeof(ThingWithComps) })]
    public static class ITab_Inventory_SyncedTrySwitchToWeapon_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(ITab_Inventory), "SyncedTrySwitchToWeapon",
                    new[] { typeof(CompInventory), typeof(ThingWithComps) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] ITab_Inventory.SyncedTrySwitchToWeapon not found — "
                      + "equipping an excluded weapon from the inventory tab will not clear "
                      + "its exclusion. Combat Extended probably moved it.");
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix() => PlayerIntent.EnterChoice();

        [HarmonyFinalizer]
        public static void Finalizer() => PlayerIntent.ExitChoice();
    }

    /// <summary>
    /// Patches vanilla's equip event to withdraw a weapon's exclusion and role veto, and
    /// re-remember it, when the player equips it from a choice surface (tab or caravan gear).
    /// </summary>
    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.AddEquipment),
                  new[] { typeof(ThingWithComps) })]
    public static class Pawn_EquipmentTracker_AddEquipment_Patch
    {
        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.AddEquipment),
                    new[] { typeof(ThingWithComps) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] Pawn_EquipmentTracker.AddEquipment not found — "
                      + "equipping an excluded weapon by hand will not clear its exclusion. "
                      + "RimWorld probably moved it.");
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(Pawn_EquipmentTracker __instance, ThingWithComps newEq)
        {
            try
            {
                PostfixInner(__instance, newEq);
            }
            catch (Exception e)
            {
                // AddEquipment runs inside think-tree job drivers; a throw here breaks
                // the pawn's whole decision loop, not just this feature.
                Log.ErrorOnce($"[CE+SS Loadouts] equip recorder failed: {e}",
                              0x53535233 ^ (newEq?.thingIDNumber ?? 0));
            }
        }

        private static void PostfixInner(Pawn_EquipmentTracker __instance, ThingWithComps newEq)
        {
            if (newEq?.def == null || !(__instance?.pawn is Pawn pawn))
            {
                return;
            }
            // Filters based on PlayerChoosing
            Verse.AI.Job curJob = pawn.CurJob;
            bool playerContext = PlayerIntent.PlayerChoosing
                || (!pawn.Spawned && RimWorld.Planet.CaravanUtility.GetCaravan(pawn) != null)
                // The def is DLC content and its DefOf field is null without it so the def
                // must be checked first or this dereferences a null CurJob inside every
                // think-tree equip.
                || (JobDefOf.UseOutfitStand != null && curJob != null
                    && curJob.def == JobDefOf.UseOutfitStand && curJob.playerForced);
            if (!playerContext || !PlayerIntent.ManagedPawn(pawn))
            {
                return;
            }
            CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
            rec?.SyncAssignment(pawn);
            if (rec == null || !rec.dontEquip.Remove(new ThingDefStuffDefPair(newEq.def, newEq.Stuff)))
            {
                return;
            }
            if (newEq.def.IsRangedWeapon)
            {
                rec.rangedRoleVetoed = false;
            }
            if (newEq.def.IsMeleeWeapon)
            {
                rec.meleeRoleVetoed = false;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
            // Guard InformOfAddedPrimary on RememberedWeapons to avoid dupes.
            if (memory != null && !memory.RememberedWeapons.Any(p => p == newEq.toThingDefStuffDefPair()))
            {
                memory.InformOfAddedPrimary(newEq);
            }
        }
    }
}
