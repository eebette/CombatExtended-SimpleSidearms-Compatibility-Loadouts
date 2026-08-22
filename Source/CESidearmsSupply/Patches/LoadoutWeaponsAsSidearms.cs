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

            ApplyRoles(memory, rec, templateDefs);
        }

        /// <summary>
        /// Set default ranged / preferred melee / combat mode from list order, but only
        /// when the current value is unset or is the one we set last — a player override
        /// always sticks.
        ///
        /// "Unset" is not the same as null. Simple Sidearms expresses *deliberately unarmed*
        /// by raising a flag and nulling the pair, so reading a null pair as "nobody owns
        /// this" made the projection re-assert a melee weapon every reconcile and silently
        /// clear the player's unarmed choice — permanently, for any pawn whose loadout lists
        /// a melee weapon. The unarmed and forced states are checked explicitly.
        /// </summary>
        private static void ApplyRoles(CompSidearmMemory memory, PawnTemplateRecord rec, List<ThingDef> templateDefs)
        {
            ThingDef firstRanged = templateDefs.FirstOrDefault(d => d.IsRangedWeapon);
            ThingDef firstMelee = templateDefs.FirstOrDefault(d => d.IsMeleeWeapon);

            ThingDefStuffDefPair? curRanged = memory.DefaultRangedWeapon;
            bool rangedOurs = (curRanged == null && !memory.ForcedUnarmed && !memory.ForcedWeapon.HasValue)
                              || (rec.defaultRanged != null && curRanged?.thing == rec.defaultRanged);
            if (rangedOurs)
            {
                if (firstRanged != null)
                {
                    ThingDefStuffDefPair pair = memory.RememberedWeapons.FirstOrDefault(p => p.thing == firstRanged);
                    if (pair.thing != null && curRanged != pair)
                    {
                        memory.SetRangedWeaponTypeAsDefault(pair);
                    }
                    rec.defaultRanged = firstRanged;
                }
                else if (rec.defaultRanged != null && curRanged != null)
                {
                    memory.UnsetRangedWeaponDefault();
                    rec.defaultRanged = null;
                }
            }

            ThingDefStuffDefPair? curMelee = memory.PreferredMeleeWeapon;
            bool meleeOurs = (curMelee == null && !memory.PreferredUnarmed && !memory.ForcedUnarmed)
                             || (rec.preferredMelee != null && curMelee?.thing == rec.preferredMelee);
            if (meleeOurs)
            {
                if (firstMelee != null)
                {
                    ThingDefStuffDefPair pair = memory.RememberedWeapons.FirstOrDefault(p => p.thing == firstMelee);
                    // Compare the whole pair, not just the def: a stuff retarget leaves the
                    // role naming a pair the pawn no longer owns, and a def-level test cannot
                    // see that.
                    if (pair.thing != null && curMelee != pair)
                    {
                        memory.SetMeleeWeaponTypeAsPreferred(pair);
                    }
                    rec.preferredMelee = firstMelee;
                }
                else if (rec.preferredMelee != null && curMelee != null)
                {
                    memory.UnsetMeleeWeaponPreference();
                    rec.preferredMelee = null;
                }
            }

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
