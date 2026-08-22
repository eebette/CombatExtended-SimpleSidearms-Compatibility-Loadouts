using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;

namespace CESidearmsSupply.Patches
{
    /// <summary>
    /// Loadout weapons as sidearms: specific weapon defs listed in the pawn's CE loadout are
    /// remembered as SS sidearms ("the template giveth"); defs removed from the loadout are
    /// forgotten again ("the template taketh away"), which is what lets CE clear the weapon
    /// out of the inventory. First ranged/melee slot in list order becomes default ranged /
    /// preferred melee; first weapon overall sets combat mode. Runs as a lazy reconcile on
    /// the same cadence CE evaluates loadouts.
    ///
    /// THE LOADOUT IS THE AUTHORITY. A def the loadout lists is claimed regardless of who
    /// remembered it first — Simple Sidearms auto-remembers anything a pawn equips as
    /// primary, so a loadout built around a gun the pawn already carries would otherwise
    /// never be claimed, and removing that row would leave the gun remembered (and so exempt
    /// from CE's drop) forever. The cost is real and deliberate: a sidearm the player chose
    /// by hand becomes loadout-managed once its def appears in the loadout, and is forgotten
    /// when that row goes away. Defs the loadout never lists are never touched.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), "TryGiveJob")]
    public static class JobGiver_UpdateLoadout_TryGiveJob_Patch
    {
        /// <summary>
        /// CE has no ABI policy, so a rename here should cost this one feature rather than
        /// aborting PatchAll and taking the ammo adapter down with it. CE's house rule:
        /// missing target degrades with a named error, never throws.
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
            catch (System.Exception e)
            {
                Log.WarningOnce($"[Sidearms&Supply] Loadout-sidearm reconcile failed for {pawn}: {e}", 0x53535231);
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
            if (memory == null || comp == null)
            {
                return;
            }

            Loadout loadout = pawn.GetLoadout();
            // count == 1 only: a multi-count weapon slot is cargo semantics (trade stock,
            // hauling), not kit declaration — remembering it would corrupt auto-switching.
            List<ThingDef> templateDefs = loadout != null && !loadout.defaultLoadout
                ? loadout.Slots.Where(s => s.thingDef != null && s.thingDef.IsWeapon && s.count == 1)
                          .Select(s => s.thingDef).Distinct().ToList()
                : new List<ThingDef>();

            PawnTemplateRecord rec = comp.GetRecord(pawn, create: false);

            // The template taketh away: forget defs the projection added that left the loadout.
            if (rec != null)
            {
                foreach (ThingDef gone in rec.weapons.Where(d => !templateDefs.Contains(d)).ToList())
                {
                    // Drain, don't iterate a Distinct() snapshot. SS's RememberedWeapons is a
                    // list that holds duplicates on purpose (its pickup path adds unguarded,
                    // and its own retrieval counts occurrences), and ForgetSidearmMemory
                    // removes ONE occurrence, then only clears the default/preferred/forced
                    // roles once the pair is fully absent. Forgetting once per distinct pair
                    // left the surplus copies remembered forever with no owner — exempt from
                    // CE's drop via the compat patch, and invisible to this record.
                    int guard = 0;
                    while (memory.RememberedWeapons.Any(p => p.thing == gone) && guard++ < 64)
                    {
                        memory.ForgetSidearmMemory(memory.RememberedWeapons.First(p => p.thing == gone));
                    }
                    if (memory.RememberedWeapons.Any(p => p.thing == gone))
                    {
                        // Something refused to be forgotten; keep the claim so the next
                        // reconcile retries rather than orphaning the memory silently.
                        continue;
                    }
                    rec.weapons.Remove(gone);
                }
                // A def that leaves the loadout also leaves suppression: otherwise removing
                // and re-adding the row would stay silently suppressed forever.
                rec.suppressed.RemoveWhere(d => !templateDefs.Contains(d));
            }

            // Hoisted: GetCarriedWeapons allocates a list and walks the whole inventory on
            // every call, and nothing in the loop below changes what the pawn carries — it
            // only edits SS memory. This runs once per think-tree selection of CE's loadout
            // job giver, not on the 1800-tick cooldown (that governs the giver's PRIORITY,
            // and TryGiveJob expires it deliberately whenever it issues a job), so a pawn
            // working through a loadout re-enters here continuously.
            List<ThingWithComps> carriedWeapons = pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true);
            // Reused across defs rather than allocated per def. It cannot be built once up
            // front: the loop adds and removes pairs as it goes.
            var rememberedOfDef = new List<ThingDefStuffDefPair>();

            // The template giveth: remember listed weapons; fix stuff up to the carried instance.
            foreach (ThingDef def in templateDefs)
            {
                ThingWithComps carried = null;
                for (int i = 0; i < carriedWeapons.Count; i++)
                {
                    if (carriedWeapons[i].def == def)
                    {
                        carried = carriedWeapons[i];
                        break;
                    }
                }
                rememberedOfDef.Clear();
                for (int i = 0; i < memory.RememberedWeapons.Count; i++)
                {
                    if (memory.RememberedWeapons[i].thing == def)
                    {
                        rememberedOfDef.Add(memory.RememberedWeapons[i]);
                    }
                }

                // THE LOADOUT OWNS WHAT IT LISTS — claim the def regardless of who
                // remembered it first. Simple Sidearms auto-remembers any weapon a pawn
                // equips as primary (InformOfAddedPrimary), so a loadout built around a
                // gun the pawn already carries would otherwise never be claimed, and
                // removing that gun from the loadout would leave it remembered (and so
                // exempt from CE's drop) forever. Weapons the loadout does NOT list are
                // untouched — deliberate sidearms still beat the projection.
                rec ??= comp.GetRecord(pawn, create: true);

                // "Carry it, but do not wield it." A loadout row says CARRY; membership in
                // the sidearm list says AND BE WILLING TO SWITCH TO IT. Forgetting a declared
                // weapon in SS's gizmo is the only way to say the first without the second —
                // removing the row would stop the pawn carrying it at all. Re-claiming it on
                // the next reconcile drove over that, so a def we already claimed and the
                // player then forgot becomes suppressed instead.
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
                        continue; // still suppressed; the loadout keeps hauling it, SS ignores it
                    }
                    rec.suppressed.Remove(def); // player put it back in the list — manage it again
                }

                if (!rec.weapons.Contains(def))
                {
                    rec.weapons.Add(def);
                }

                if (rememberedOfDef.Count == 0)
                {
                    ThingDefStuffDefPair pair = carried != null
                        ? carried.toThingDefStuffDefPair()
                        : new ThingDefStuffDefPair(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    memory.RememberedWeapons.Add(pair);
                    // No hold-record write: the compatibility patch answers CE's drop question
                    // from SS memory directly (GetExcessThing / GetExcessEquipment postfixes),
                    // so adding the pair above is already the whole exemption. Writing into
                    // CE's hold-tracker clobbered records the player set with CE's own command.
                }
                else if (carried != null)
                {
                    // Stuff mismatch: the loadout asked for a knife, the pawn fetched a
                    // plasteel one. Retarget through SS's own forget rather than a raw
                    // list Remove — ForgetSidearmMemory is what clears a default/preferred/
                    // forced role still pointing at the old pair, and skipping it left those
                    // roles naming a pair the pawn does not own, which SS's pair-exact
                    // hasWeaponType then never matches again.
                    ThingDefStuffDefPair actual = carried.toThingDefStuffDefPair();
                    if (!rememberedOfDef.Contains(actual))
                    {
                        memory.ForgetSidearmMemory(rememberedOfDef[0]);
                        memory.RememberedWeapons.Add(actual);
                    }
                }
            }

            if (templateDefs.Count > 0)
            {
                rec ??= comp.GetRecord(pawn, create: true);
            }
            if (rec == null)
            {
                return;
            }

            ApplyRoles(pawn, memory, rec, templateDefs);
            ApplyMode(memory, rec, templateDefs);
        }

        /// <summary>
        /// The role a pawn falls back to is the head of one ordered list:
        ///
        ///     [the weapon the player put in their hands] ++ [the loadout's order]
        ///
        /// filtered to what the pawn is actually carrying. Equipping a weapon the loadout
        /// does not list makes it the head — Simple Sidearms sets the role itself on equip,
        /// and that is a deliberate player action, so it stands. Stop carrying it and the
        /// head falls through to the loadout's first declared weapon of that kind; pick it
        /// back up (SS fetches it on its own) and it is the head again. The displaced choice
        /// is SHELVED in the record rather than overwritten away, so it can come back.
        ///
        /// Nothing here is a judgement about who "owns" the field. The old ownership test
        /// surrendered the role forever the first time anything else wrote it, which left a
        /// pawn whose battlefield pickup was later stashed with a role pointing at a weapon
        /// SS then ignored (its hasWeaponType guard is pair-exact) and a loadout order that
        /// never came back.
        ///
        /// Forced weapons and the unarmed states are checked first and never touched: SS's
        /// role setters clear a same-category ForcedWeapon as a side effect, and
        /// SetMeleeWeaponTypeAsPreferred also clears PreferredUnarmed — so writing a role
        /// here would destroy an explicit player setting. "No pair" is not "no owner".
        /// </summary>
        private static void ApplyRoles(Pawn pawn, CompSidearmMemory memory, PawnTemplateRecord rec, List<ThingDef> templateDefs)
        {
            ApplyRangedRole(pawn, memory, rec, templateDefs.FirstOrDefault(d => d.IsRangedWeapon));
            ApplyMeleeRole(pawn, memory, rec, templateDefs.FirstOrDefault(d => d.IsMeleeWeapon));
        }

        private static void ApplyRangedRole(Pawn pawn, CompSidearmMemory memory, PawnTemplateRecord rec, ThingDef loadoutFirst)
        {
            if (memory.ForcedWeapon.HasValue || memory.ForcedUnarmed)
            {
                return; // an explicit force outranks the list, and writing here would clear it
            }

            ThingDefStuffDefPair? current = memory.DefaultRangedWeapon;

            // The shelved choice comes back the moment the pawn carries it again.
            if (rec.shelvedRanged.HasValue && pawn.hasWeaponType(rec.shelvedRanged.Value))
            {
                memory.SetRangedWeaponTypeAsDefault(rec.shelvedRanged.Value);
                rec.shelvedRanged = null;
                rec.defaultRanged = null; // the head is the player's again, not ours
                return;
            }

            bool ours = current == null || (rec.defaultRanged != null && current.Value.thing == rec.defaultRanged);
            if (!ours)
            {
                if (pawn.hasWeaponType(current.Value))
                {
                    rec.shelvedRanged = null; // their choice is in hand; it IS the head
                    return;
                }
                rec.shelvedRanged = current; // not carried — step aside, but remember it
            }

            if (loadoutFirst != null)
            {
                ThingDefStuffDefPair pair = memory.RememberedWeapons.FirstOrDefault(p => p.thing == loadoutFirst);
                if (pair.thing != null && current != pair)
                {
                    memory.SetRangedWeaponTypeAsDefault(pair);
                }
                rec.defaultRanged = loadoutFirst;
            }
            else if (rec.defaultRanged != null && current != null)
            {
                memory.UnsetRangedWeaponDefault();
                rec.defaultRanged = null;
            }
        }

        private static void ApplyMeleeRole(Pawn pawn, CompSidearmMemory memory, PawnTemplateRecord rec, ThingDef loadoutFirst)
        {
            if (memory.ForcedWeapon.HasValue || memory.ForcedUnarmed || memory.PreferredUnarmed)
            {
                return; // see above; PreferredUnarmed is a real choice that nulls the pair
            }

            ThingDefStuffDefPair? current = memory.PreferredMeleeWeapon;

            if (rec.shelvedMelee.HasValue && pawn.hasWeaponType(rec.shelvedMelee.Value))
            {
                memory.SetMeleeWeaponTypeAsPreferred(rec.shelvedMelee.Value);
                rec.shelvedMelee = null;
                rec.preferredMelee = null;
                return;
            }

            bool ours = current == null || (rec.preferredMelee != null && current.Value.thing == rec.preferredMelee);
            if (!ours)
            {
                if (pawn.hasWeaponType(current.Value))
                {
                    rec.shelvedMelee = null;
                    return;
                }
                rec.shelvedMelee = current;
            }

            if (loadoutFirst != null)
            {
                ThingDefStuffDefPair pair = memory.RememberedWeapons.FirstOrDefault(p => p.thing == loadoutFirst);
                if (pair.thing != null && current != pair)
                {
                    memory.SetMeleeWeaponTypeAsPreferred(pair);
                }
                rec.preferredMelee = loadoutFirst;
            }
            else if (rec.preferredMelee != null && current != null)
            {
                memory.UnsetMeleeWeaponPreference();
                rec.preferredMelee = null;
            }
        }

        private static void ApplyMode(CompSidearmMemory memory, PawnTemplateRecord rec, List<ThingDef> templateDefs)
        {
            ThingDef firstAny = templateDefs.FirstOrDefault();
            bool modeOurs = rec.modeManaged
                ? memory.primaryWeaponMode == rec.lastMode
                : memory.primaryWeaponMode == PrimaryWeaponMode.BySkill;
            if (firstAny != null)
            {
                PrimaryWeaponMode desired = firstAny.IsRangedWeapon ? PrimaryWeaponMode.Ranged : PrimaryWeaponMode.Melee;
                if (modeOurs && memory.primaryWeaponMode != desired)
                {
                    if (!rec.modeManaged)
                    {
                        rec.modeBefore = memory.primaryWeaponMode; // what to hand back later
                    }
                    memory.primaryWeaponMode = desired;
                    rec.modeManaged = true;
                    rec.lastMode = desired;
                }
            }
            else if (rec.modeManaged && modeOurs)
            {
                // The roles above have an unset branch and this did not, so emptying a
                // loadout took the sidearms back and left the pawn locked in the mode the
                // projection chose — a melee-first loadout could leave a shooter permanently
                // in Melee, persisted across saves.
                memory.primaryWeaponMode = rec.modeBefore;
                rec.modeManaged = false;
                rec.lastMode = rec.modeBefore;
            }
        }
    }
}
