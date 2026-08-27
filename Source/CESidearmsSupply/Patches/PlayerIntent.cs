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
        [ThreadStatic] private static int choiceDepth;

        internal static bool PlayerIsDriving => gizmoDepth > 0;

        /// <summary>
        /// Raised around the player's own equip surfaces: CE's inventory tab while it
        /// builds the menu for one carried item (so the CanEquip veto stands down and the
        /// Equip entry stays live), and the click call chains of that tab and the caravan
        /// gear tab (so the AddEquipment recorder knows an equip landing inside the scope
        /// is the player's). Everything the veto refuses outside this scope is an
        /// inventory-side machine selection; map pickups are never vetoed at all.
        /// </summary>
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
        /// The one gate recording and blocking share, matching the cleanup's own conditions:
        /// the feature is on and the pawn has a real (non-default) loadout. A colony that
        /// never uses CE loadouts is never touched by the exclusion system, and switching
        /// the feature off switches all of it off — the cleanup in Reconcile only runs under
        /// these same conditions, so nothing is ever recorded that nothing can remove.
        /// </summary>
        internal static bool ManagedPawn(Pawn pawn)
        {
            if (pawn == null || !pawn.IsColonist || !SupplyMod.Settings.loadoutWeaponsAsSidearms)
            {
                return false;
            }
            // Not Utility_Loadouts.GetLoadout: that INSERTS the pawn into CE's assignment
            // dictionary when absent, and this runs inside CE's own search loops via the
            // CanEquip postfix. A question about a pawn must not write CE state. Absent
            // from the dictionary means default loadout means not managed.
            return LoadoutManager.AssignedLoadouts.TryGetValue(pawn, out Loadout loadout)
                   && loadout != null && !loadout.defaultLoadout;
        }

        internal static CompLoadoutSidearms RecordFor(CompSidearmMemory memory)
        {
            Pawn pawn = memory?.Owner;
            CompLoadoutSidearms rec = pawn != null && pawn.IsColonist ? CompLoadoutSidearms.For(pawn) : null;
            // Every recorder and withdrawal acts on the CURRENT assignment's record:
            // stale rules from a previous assignment are cleared before anything reads
            // or writes, so a gesture made right after reassigning a loadout is recorded
            // under the new assignment instead of being destroyed by the pending clear.
            rec?.SyncAssignment(pawn);
            return rec;
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
            Log.Error($"[CE+SS Loadouts] CompSidearmMemory.{method} not found — {consequence} "
                      + "Simple Sidearms probably moved it.");
            return false;
        }
    }

    /// <summary>
    /// The scope. A finalizer rather than a postfix: finalizers run even when the original
    /// throws, and a leaked depth would silently disable every hook below for the session.
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

    /// <summary>Inside the gizmo, a forget is the player saying "carry it, do not wield it".</summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.ForgetSidearmMemory),
                  new[] { typeof(ThingDefStuffDefPair) })]
    public static class CompSidearmMemory_ForgetSidearmMemory_Patch
    {
        public static bool Prepare() => PlayerIntent.Require("ForgetSidearmMemory",
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
            // ONE occurrence, matching what SS itself just did: ForgetSidearmMemory
            // removes a single entry, and the ledgers must agree entry for entry. (Today
            // rec.claimed never holds duplicates, so this equals the old RemoveAll — the
            // one-for-one shape is kept so a future multiplicity change cannot silently
            // desynchronise the two lists.)
            int i = rec.claimed.IndexOf(weaponMemory);
            if (i >= 0)
            {
                rec.claimed.RemoveAt(i);
            }
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
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.InformOfAddedSidearm),
                  new[] { typeof(Thing) })]
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

    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.UnsetRangedWeaponDefault), new Type[0])]
    public static class CompSidearmMemory_UnsetRangedWeaponDefault_Patch
    {
        public static bool Prepare() => PlayerIntent.Require("UnsetRangedWeaponDefault",
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
    /// The melee twin, with one extra condition. SS reaches the same method from the Unarmed
    /// icon to mean "stop preferring unarmed", which is not a statement about melee weapons
    /// at all — so only treat it as a veto if there was a preference to clear.
    /// </summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.UnsetMeleeWeaponPreference), new Type[0])]
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

    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.SetMeleeWeaponTypeAsPreferred),
                  new[] { typeof(ThingDefStuffDefPair) })]
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

    /// <summary>
    /// Blocks the game from arming a pawn with a weapon the player excluded — and nothing
    /// else. CE asks EquipmentUtility.CanEquip two different questions: "may I draw this
    /// from the pawn's inventory?" (its weapon-switch code, CompInventory) and "may I pick
    /// this up off the map for a loadout row?" (its loadout job's search filter). The
    /// exclusion is only an answer to the first, so this patch acts only on weapons already
    /// in that pawn's inventory. Refusing map items was the original mistake: the gizmo's
    /// removal gesture drops the weapon, and refusing the pickup left it on the ground with
    /// its loadout row permanently unsatisfiable.
    ///
    /// While the player's own Equip menu entry is being built (PlayerChoosing — CE's
    /// inventory tab, the one Equip surface that offers carried weapons), the patch does
    /// nothing, so the option stays visible; choosing it clears the exclusion (see the
    /// SyncedTrySwitchToWeapon patch below).
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
            // Only weapons the pawn is already carrying. A map item being evaluated here is
            // CE deciding whether to HAUL it for a loadout row, and the exclusion must not
            // stop the pawn carrying what their loadout declares.
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
    /// Forcing a weapon is the strongest statement of intent the gizmo offers, and the
    /// DRAFTED branch of clicking an unremembered weapon goes through here rather than
    /// InformOfAddedSidearm — so without this, a drafted player clicking an excluded
    /// weapon back into use got the force and kept the exclusion, which then outlived
    /// the force (undraft clears the force, nothing cleared the exclusion).
    /// </summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.SetWeaponAsForced),
                  new[] { typeof(ThingDefStuffDefPair), typeof(bool) })]
    public static class CompSidearmMemory_SetWeaponAsForced_Patch
    {
        public static bool Prepare() => PlayerIntent.Require("SetWeaponAsForced",
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
    /// The caravan gear tab is the one Equip surface a caravan pawn has, and caravan
    /// items live in pawn inventories — so the inventory-side veto refused a player
    /// dragging an excluded weapon onto a pawn, with no way to withdraw the exclusion
    /// until the caravan landed. Same pair of moves as CE's inventory tab: the veto
    /// stands down while the player's own gesture runs, and the AddEquipment recorder
    /// (see Pawn_EquipmentTracker_AddEquipment_Patch) withdraws the exclusion when the
    /// equip actually lands — the equip path here goes through the same vanilla
    /// primitive. Off the map the SS memory comp is usually unresolvable, so the
    /// re-remember half is best-effort — the reconcile claims the pair as soon as the
    /// pawn spawns again, declared and carried.
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
    /// Simple Sidearms never asks EquipmentUtility.CanEquip when switching weapons — it
    /// honours vanilla's carry-but-do-not-wield bans (bladelink, biocode) by re-checking
    /// those cases itself, and it selects from the pawn's CARRIED weapons, not from its own
    /// remembered list. So an excluded weapon sitting in the inventory (where the loadout
    /// row keeps it) is a live candidate for SS's idle re-arm, its melee swap when an enemy
    /// closes, its post-shot swap for single-use weapons, and its auto-undraft re-arm — all
    /// of which funnel through equipSpecificWeapon. This prefix registers the exclusion
    /// with that funnel the same way SS already honours bladelink's.
    ///
    /// A forced weapon outranks the exclusion, matching the reconcile. Player paths
    /// need no exemption: every gizmo gesture that equips an excluded pair withdraws the
    /// exclusion in the same click before the equip call reaches here.
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
            // weapon == null is SS unequipping to unarmed; never blocked.
            if (weapon?.def == null || pawn == null || !PlayerIntent.ManagedPawn(pawn))
            {
                return true;
            }
            // No exemption for player context here, deliberately — twice over. A
            // CurJob.playerForced exemption switched the ban off during player-directed
            // combat (vanilla stamps that flag on every right-click order). And a
            // PlayerIsDriving exemption protected no reachable player path — every gizmo
            // equip of an excluded pair withdraws the exclusion in the same click before
            // any equip call, so the Contains check below already passes — while a
            // think-tree restart nested INSIDE a gizmo click (SS ends a Hunt job with a
            // synchronous restart; the re-issued job's first toil can fire the autotool)
            // is pure machine work that the exemption wrongly waved through.
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
    /// While CE's inventory tab builds the menu for a carried item, the CanEquip patch
    /// above does nothing — so the Equip entry for an excluded weapon stays clickable
    /// instead of showing as dead. This is the only Equip surface that offers weapons the
    /// pawn is carrying (the map right-click menus offer ground items, which the patch
    /// never touches), so it is the only place the flag is still needed.
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
    /// The exclusion, registered at the picker INPUTS. It briefly lived inside
    /// StatCalculator.canUseSidearmInstance — SS's shared usability predicate — which
    /// gave the gizmo's blocked cross for free, and with it SS's UI contract that a
    /// crossed icon is permanently uninteractable: the draw returns before registering
    /// any click region, so the undo gestures died. A retractable state must not ride a
    /// UI built for unretractable ones (review ruling, round 6). Instead, while one of
    /// SS's three pickers is asking, the carried-weapon list simply omits excluded
    /// pairs — the sibling compat patch's own proven seam (its P03 hides ammo-dry guns
    /// exactly this way). The pickers pick the runner-up; the gizmo never sees anything
    /// unusual (an excluded weapon renders as a normal unmemorised entry, clicks and
    /// all); the funnel prefix above stays as the backstop; and none of it sits behind
    /// SS's AllowBlockedWeaponUse gate.
    /// </summary>
    public static class SelectionFilter
    {
        [ThreadStatic] private static Pawn hidingExcludedFor;

        /// <summary>Re-entrancy-safe: the sibling's P03 re-runs a picker inside its own
        /// postfix, so only the outermost bracket owns the flag.</summary>
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

    /// <summary>The autotool picker enumerates carried tools directly and tries no
    /// runner-up on a refusal — unbracketed, excluding the best tool silently disabled
    /// tool-switching for its stat class outright.</summary>
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
    /// The click half of the tab path: a scope, not a recorder. The old hook here acted on
    /// the CLICK — TrySwitchToWeapon returns void and exits silently when the weapon has
    /// left the container between the menu frame and the click frame, so a failed switch
    /// still cleared the exclusion and wrote a memory for a weapon never equipped. The
    /// call chain from here down to Pawn_EquipmentTracker.AddEquipment is synchronous, so
    /// raising the choice scope around it hands the recording to the AddEquipment
    /// recorder below, which only ever fires on an equip that actually happened.
    ///
    /// Player-only by construction: SyncedTrySwitchToWeapon's single caller in all of CE is
    /// this tab's menu entry. Machine switches call CompInventory.TrySwitchToWeapon
    /// directly and never pass through it.
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
    /// The recorder for every player-choice equip surface: the equip EVENT, gated by the
    /// choice scope. AddEquipment fires exactly when a weapon lands in a pawn's hands, so
    /// success is implied by construction — a click whose switch silently failed never
    /// reaches it. The tab and caravan brackets above raise the scope; anything that
    /// equips inside it is the player's word on both fronts: the exclusion is withdrawn
    /// and the matching role veto lifted, and the weapon is re-remembered behind Simple
    /// Sidearms' own duplicate guard (this path is a direct switch, not an equip job, so
    /// SS's remember-on-equip hook never runs on its own).
    ///
    /// Gated on PlayerChoosing, not PlayerIsDriving, deliberately: gizmo clicks also reach
    /// AddEquipment through SS's own funnel, and the gizmo's recorders already handle
    /// those — distinct scopes avoid double-recording.
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
            // Player context, three proofs. The scope covers the tab and caravan click
            // chains. An equip landing on an UNSPAWNED caravan pawn is the player's even
            // without it: the caravan gear tab is the only writer of equipment there (its
            // persona-weapon path equips from a confirmation dialog one frame after the
            // bracket closed — no job giver, SS switch or loadout pass runs off-map). And
            // UseOutfitStand jobs exist only as player orders — the think tree never
            // issues one — so for THAT def alone playerForced is genuinely the player's
            // hand (contrast the deleted blanket playerForced exemption in the SS funnel
            // prefix, which trusted the flag on attack orders too).
            Verse.AI.Job curJob = pawn.CurJob;
            bool playerContext = PlayerIntent.PlayerChoosing
                || (!pawn.Spawned && RimWorld.Planet.CaravanUtility.GetCaravan(pawn) != null)
                // The def is DLC content and its DefOf field is null without it — and
                // null == null?.def is TRUE for an idle pawn, so the def must be checked
                // first or this dereferences a null CurJob inside every think-tree equip.
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
            // The Any() guard mirrors the one SS's own equip path applies BEFORE calling
            // InformOfAddedPrimary (JobDriver_Equip's MemoriseWeaponAboutToBeEquipped) —
            // InformOfAddedSidearm itself has no duplicate check. Using InformOfAddedPrimary
            // for a not-yet-remembered weapon, side effects included (it becomes the
            // category default; a same-category force is cleared), is exactly what SS does
            // when the same weapon is equipped from the ground — mirrored deliberately.
            if (memory != null && !memory.RememberedWeapons.Any(p => p == newEq.toThingDefStuffDefPair()))
            {
                memory.InformOfAddedPrimary(newEq);
            }
        }
    }
}
