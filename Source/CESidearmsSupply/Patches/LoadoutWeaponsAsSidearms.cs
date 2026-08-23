using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
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
    /// THE LOADOUT IS THE AUTHORITY. A def the loadout lists is claimed regardless of who
    /// remembered it first — SS auto-remembers anything a pawn equips as primary, so a loadout
    /// built around a gun the pawn already carries would otherwise never be claimed, and
    /// removing that row would leave the gun remembered (and so exempt from CE's drop) forever.
    /// Defs the loadout never lists are not touched.
    ///
    /// Reconciled rather than event-driven, deliberately. SS and CE both mutate this state and
    /// so do other mods: SS alone has five writers to its remembered list, one of which
    /// materialises the entire list from the pawn's carried weapons the first time anything
    /// reads it. A missed event is permanent; a missed reconcile cycle lasts until the next
    /// one. This is not player-triggered — CE registers JobGiver_UpdateLoadout in the colonist
    /// behaviour tree's priority sorter and its GetPriority bids 30f once the 1800-tick
    /// cooldown lapses, so every colonist reconciles about twice a minute on their own, and a
    /// save loaded in any state converges on the pawn's first job selection.
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
            if (AccessTools.Method(typeof(JobGiver_UpdateLoadout), "TryGiveJob") != null)
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
            try
            {
                Reconcile(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[Sidearms&Supply] Loadout-sidearm reconcile failed for {pawn}: {e}",
                              0x53535231 ^ (pawn?.thingIDNumber ?? 0));
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
            if (memory?.RememberedWeapons == null || comp == null)
            {
                return;
            }

            // Every weapon row declares a sidearm. Count is not a filter: a row of five pistols
            // still says "this pawn should have a pistol", and SS memory is per type. Generic
            // rows ("any ranged weapon") carry no def, so there is no type to remember.
            Loadout loadout = pawn.GetLoadout();
            List<ThingDef> declared = loadout != null && !loadout.defaultLoadout
                ? loadout.Slots.Where(s => s.thingDef != null && s.thingDef.IsWeapon)
                          .Select(s => s.thingDef).Distinct().ToList()
                : new List<ThingDef>();

            PawnTemplateRecord rec = comp.GetRecord(pawn, create: false);
            if (rec == null && declared.Count == 0)
            {
                return; // nothing claimed and nothing to claim
            }
            rec ??= comp.GetRecord(pawn, create: true);

            ForgetUndeclared(memory, rec, declared);
            ClaimDeclared(pawn, memory, rec, declared);
            AssertRoles(pawn, memory, declared);
        }

        /// <summary>
        /// The template taketh away: defs this projection claimed that the loadout no longer
        /// declares are forgotten. Nothing is dropped here — forgetting ends the compatibility
        /// patch's drop exemption, and CE's own rules then decide, honouring that loadout's
        /// dropUndefined and adHoc settings exactly as they do for any other undeclared item.
        /// </summary>
        private static void ForgetUndeclared(CompSidearmMemory memory, PawnTemplateRecord rec, List<ThingDef> declared)
        {
            foreach (ThingDef gone in rec.weapons.Where(d => !declared.Contains(d)).ToList())
            {
                // Drain rather than iterate a snapshot. SS's list holds duplicates on purpose,
                // and ForgetSidearmMemory removes ONE occurrence and only clears the role
                // fields once the pair is fully absent — so forgetting once per distinct pair
                // left the surplus copies remembered forever with no owner.
                int guard = 0;
                while (memory.RememberedWeapons.Any(p => p.thing == gone) && guard++ < 64)
                {
                    memory.ForgetSidearmMemory(memory.RememberedWeapons.First(p => p.thing == gone));
                }
                if (!memory.RememberedWeapons.Any(p => p.thing == gone))
                {
                    rec.weapons.Remove(gone);
                }
            }
            // Suppression follows the row: remove a weapon and re-add it and it is managed
            // again, rather than staying silently suppressed forever.
            rec.suppressed.RemoveWhere(d => !declared.Contains(d));
        }

        /// <summary>
        /// The template giveth: every declared def is remembered, retargeted to the material
        /// the pawn actually carries.
        ///
        /// With one exception, and it is the player's. A loadout row says CARRY; membership in
        /// the sidearm list says AND BE WILLING TO SWITCH TO IT. Forgetting a declared weapon
        /// in SS's gizmo is the only way to say the first without the second — removing the row
        /// would stop the pawn carrying it at all — so a def this projection claimed and the
        /// player then forgot is suppressed rather than re-claimed.
        /// </summary>
        private static void ClaimDeclared(Pawn pawn, CompSidearmMemory memory, PawnTemplateRecord rec, List<ThingDef> declared)
        {
            List<ThingWithComps> carriedWeapons = pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true);
            var rememberedOfDef = new List<ThingDefStuffDefPair>();

            foreach (ThingDef def in declared)
            {
                rememberedOfDef.Clear();
                for (int i = 0; i < memory.RememberedWeapons.Count; i++)
                {
                    if (memory.RememberedWeapons[i].thing == def)
                    {
                        rememberedOfDef.Add(memory.RememberedWeapons[i]);
                    }
                }

                if (rememberedOfDef.Count == 0 && rec.weapons.Contains(def))
                {
                    rec.weapons.Remove(def);
                    rec.suppressed.Add(def);
                    continue;
                }
                if (rec.suppressed.Contains(def))
                {
                    if (rememberedOfDef.Count == 0)
                    {
                        continue; // still out of the list; the row keeps CE hauling it
                    }
                    rec.suppressed.Remove(def); // put back by hand — manage it again
                }

                ThingWithComps carried = null;
                for (int i = 0; i < carriedWeapons.Count; i++)
                {
                    if (carriedWeapons[i].def == def)
                    {
                        carried = carriedWeapons[i];
                        break;
                    }
                }

                if (rememberedOfDef.Count == 0)
                {
                    memory.RememberedWeapons.Add(carried != null
                        ? carried.toThingDefStuffDefPair()
                        : new ThingDefStuffDefPair(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null));
                }
                else if (carried != null)
                {
                    // The loadout asked for a knife and the pawn fetched a plasteel one.
                    // Retarget through SS's own forget: it is what clears a role still pointing
                    // at the old pair, and SS's hasWeaponType is pair-exact, so a role left
                    // naming the wrong material never matches again.
                    ThingDefStuffDefPair actual = carried.toThingDefStuffDefPair();
                    if (!rememberedOfDef.Contains(actual))
                    {
                        memory.ForgetSidearmMemory(rememberedOfDef[0]);
                        memory.RememberedWeapons.Add(actual);
                    }
                }
                rec.weapons.Add(def);
            }
        }

        /// <summary>
        /// First declared ranged weapon is the default ranged weapon; first declared melee is
        /// the preferred melee weapon.
        ///
        /// One exception, and it is the player's: a role naming a weapon the loadout does not
        /// declare was set by the player equipping it, and it stands while they are carrying
        /// it. Put the weapon away and the loadout's first takes over — SS ignores an uncarried
        /// role anyway, since its hasWeaponType guard is pair-exact, and would otherwise fall
        /// back to picking by raw DPS.
        ///
        /// Forced weapons and the unarmed states are never touched. SS expresses "deliberately
        /// unarmed" by raising a flag and nulling the pair, and its role setters clear a
        /// same-category ForcedWeapon — and, for melee, PreferredUnarmed — as a side effect, so
        /// writing a role without these guards destroys an explicit player setting.
        /// </summary>
        private static void AssertRoles(Pawn pawn, CompSidearmMemory memory, List<ThingDef> declared)
        {
            if (memory.ForcedWeapon.HasValue || memory.ForcedUnarmed)
            {
                return;
            }

            ThingDef firstRanged = declared.FirstOrDefault(d => d.IsRangedWeapon);
            if (firstRanged != null && !PlayersAndInHand(pawn, memory.DefaultRangedWeapon, declared))
            {
                ThingDefStuffDefPair pair = memory.RememberedWeapons.FirstOrDefault(p => p.thing == firstRanged);
                if (pair.thing != null && memory.DefaultRangedWeapon != pair)
                {
                    memory.SetRangedWeaponTypeAsDefault(pair);
                }
            }

            if (memory.PreferredUnarmed)
            {
                return;
            }

            ThingDef firstMelee = declared.FirstOrDefault(d => d.IsMeleeWeapon);
            if (firstMelee != null && !PlayersAndInHand(pawn, memory.PreferredMeleeWeapon, declared))
            {
                ThingDefStuffDefPair pair = memory.RememberedWeapons.FirstOrDefault(p => p.thing == firstMelee);
                if (pair.thing != null && memory.PreferredMeleeWeapon != pair)
                {
                    memory.SetMeleeWeaponTypeAsPreferred(pair);
                }
            }
        }

        /// <summary>The player equipped something the loadout does not list, and still has it.</summary>
        private static bool PlayersAndInHand(Pawn pawn, ThingDefStuffDefPair? role, List<ThingDef> declared)
        {
            return role.HasValue && !declared.Contains(role.Value.thing) && pawn.hasWeaponType(role.Value);
        }
    }
}
