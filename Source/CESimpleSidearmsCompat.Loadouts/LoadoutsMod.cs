using System.Linq;
using HarmonyLib;
using RimWorld;
using SimpleSidearms.rimworld;
using UnityEngine;
using Verse;

namespace CESimpleSidearmsCompat.Loadouts
{
    public class LoadoutsMod : Mod
    {
        public LoadoutsMod(ModContentPack content) : base(content)
        {
        }

        public override string SettingsCategory()
        {
            // Matches how the mod is actually named where players see it — the old
            // working title made the settings entry unfindable.
            return "CE+SS Compatibility - Loadouts";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            // The toggle is per-colony (scribed on the game component), so it is only
            // readable/settable with a save loaded. Disabling the feature for every colony is
            // uninstalling the mod, not a global switch here.
            LoadoutsSessionComponent comp = Current.Game?.GetComponent<LoadoutsSessionComponent>();
            if (comp == null)
            {
                GUI.color = Color.gray;
                listing.Label("Load a colony to configure — this setting is per-colony.");
                GUI.color = Color.white;
                listing.End();
                return;
            }

            // The checkbox writes scribed sim state, and toggle-off Release() forgets SS memory
            // (also sim state) at once — both outside RimWorld-Multiplayer's command-sync, so a
            // mid-session flip desyncs MP clients. Load-time is safe (every client reads the same
            // save). Not gated: this suite is not multiplayer-targeted.
            bool was = comp.loadoutWeaponsAsSidearms;
            listing.CheckboxLabeled("Loadout weapons as sidearms", ref comp.loadoutWeaponsAsSidearms,
                "Weapons listed in a CE loadout are auto-remembered as sidearms by assigned pawns. "
                + "The first ranged weapon in the list becomes the default ranged weapon and the first "
                + "melee the preferred melee weapon. Removing a weapon from the loadout makes the pawn "
                + "forget it as a sidearm, which is what lets CE clear it out of the inventory.");

            // Turning it off has to undo it, not freeze it: the compat patch's drop shield
            // protects remembered copies, so claims left behind stay stuck in inventories with
            // no way back short of the gizmo. The setting is per-colony, so this loaded save is
            // the only one to clean — release now, no cross-save deferral.
            if (was && !comp.loadoutWeaponsAsSidearms)
            {
                Release(interactive: true);
            }

            listing.Gap();
            if (listing.ButtonText("Release all claimed sidearms",
                                   "Forget every sidearm this mod added, on every colonist, and start "
                                   + "over. Weapons the loadout does not list are not touched."))
            {
                Release(interactive: true);
            }

            listing.Gap();
            listing.Label("Ammo for sidearms is Combat Extended's own job: add the ammo to the loadout and "
                          + "CE keeps the pawn stocked to that count, the same as for any other item.");
            listing.End();
        }

        /// <summary>
        /// Hand back every claimed pair on every colonist in the loaded game. Returns false
        /// when there is no game to act on.
        /// </summary>
        public static bool Release(bool interactive = false)
        {
            if (Current.Game == null)
            {
                return false;
            }
            int released = 0;
            int deferred = 0;
            foreach (Pawn pawn in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists.ToList())
            {
                CompLoadoutSidearms rec = CompLoadoutSidearms.For(pawn);
                if (rec == null)
                {
                    continue;
                }
                CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn);
                int n = rec.Release(memory, memory?.ForcedWeapon, memory?.ForcedWeaponWhileDrafted);
                if (n < 0)
                {
                    deferred++; // unspawned; its memory comp is not resolvable yet
                }
                else
                {
                    released += n;
                }
            }
            // dontEquip and the role vetoes are the player's, not this projection's, and the
            // button's own text promises not to touch what the loadout does not list. They
            // survive a release.
            if (interactive || released > 0 || deferred > 0)
            {
                string away = deferred == 0 ? ""
                    : $" {deferred} pawn(s) are away; they are released on the next load of this colony after they return.";
                Messages.Message($"[CE+SS Loadouts] Released {released} claimed sidearm(s)." + away,
                                 MessageTypeDefOf.TaskCompletion, historical: false);
            }
            return true;
        }
    }

    /// <summary>
    /// Holds the per-colony feature toggle (scribed into the save) and runs a once-per-load
    /// release sweep for a colony that has the feature off — idempotent, and it catches away
    /// pawns that have since returned. No global flag: the setting's grain is the save's.
    /// </summary>
    public class LoadoutsSessionComponent : GameComponent
    {
        /// <summary>Per-colony feature toggle, scribed into the save. Default on.</summary>
        public bool loadoutWeaponsAsSidearms = true;

        /// <summary>Incremented by the reconcile prefix; consumed by the liveness canary.</summary>
        public static int reconcilePasses;

        private int lastLivenessTick;

        public LoadoutsSessionComponent(Game game)
        {
        }

        /// <summary>Whether the feature is on for the loaded colony. On when no game is loaded,
        /// so out-of-game callers never act on a phantom colony.</summary>
        public static bool Enabled
        {
            get
            {
                Game g = Current.Game;
                if (g == null)
                {
                    return true;
                }
                LoadoutsSessionComponent c = g.GetComponent<LoadoutsSessionComponent>();
                return c == null || c.loadoutWeaponsAsSidearms;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref loadoutWeaponsAsSidearms, "loadoutWeaponsAsSidearms", true);
        }

        public override void GameComponentTick()
        {
            // Liveness canary, ~10 in-game hours apart: the whole feature rides CE's
            // job-giver cadence, and if a CE update reroutes loadout enforcement, every
            // patch stays applied while nothing ever runs — the one break with no other
            // signal. A managed colonist with zero reconcile passes across a window is
            // that state.
            int now = Find.TickManager.TicksGame;
            if (now - lastLivenessTick < 25000)
            {
                return;
            }
            bool hadPasses = reconcilePasses > 0;
            reconcilePasses = 0;
            bool firstWindow = lastLivenessTick == 0;
            lastLivenessTick = now;
            if (firstWindow || hadPasses || !loadoutWeaponsAsSidearms)
            {
                return;
            }
            if (PawnsFinder.AllMaps_FreeColonistsSpawned
                    .Any(p => Patches.PlayerIntent.ManagedPawn(p)))
            {
                Log.ErrorOnce("[CE+SS Loadouts] The loadout reconcile has not run for 10+ in-game "
                              + "hours despite a managed colonist — Combat Extended has probably "
                              + "rerouted its loadout updates and the projection is inert. "
                              + "Please report this.", 0x53535243);
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // A colony whose feature is off gets its claims released on every load —
            // idempotent, and it catches away pawns that have since returned. No global flag,
            // because the setting itself is per-colony.
            if (!loadoutWeaponsAsSidearms)
            {
                LoadoutsMod.Release();
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            // Named absence beats misattributed spam: without SS, the CE-attributed
            // classes would otherwise apply and then JIT-fail inside the think tree on
            // every pass, with stacks pointing at Combat Extended.
            bool ceActive = ModsConfig.IsActive("CETeam.CombatExtended");
            bool ssActive = ModsConfig.IsActive("PeteTimesSix.SimpleSidearms");
            if (!ceActive || !ssActive)
            {
                Log.Error("[CE+SS Loadouts] Required mod missing:"
                          + (ceActive ? "" : " Combat Extended")
                          + (ssActive ? "" : " Simple Sidearms")
                          + " — nothing is patched; the mod is inert this session.");
                return;
            }

            // Per-class patching, deliberately not PatchAll: Harmony also binds patch
            // PARAMETERS by name and __result by return type, and neither is visible to
            // a Prepare() guard — an upstream parameter rename would abort PatchAll
            // mid-assembly, leaving the mod half-patched (enforcement alive, its
            // withdrawal recorders dead) under a message claiming it is fully off.
            // Per-class, one binding failure costs that class alone, with its own named
            // error — the same degrade contract every Prepare() already promises.
            var harmony = new Harmony("eebette.CESimpleSidearmsCompat.Loadouts");
            int failedClasses = 0;
            foreach (System.Type type in typeof(Bootstrap).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), inherit: true).Length == 0)
                {
                    continue;
                }
                try
                {
                    new PatchClassProcessor(harmony, type).Patch();
                }
                catch (System.Exception e)
                {
                    failedClasses++;
                    Log.Error($"[CE+SS Loadouts] {type.Name} failed to patch and is disabled "
                              + $"for this session: {e.Message}");
                }
            }
            Log.Message(failedClasses == 0
                ? "[CE+SS Loadouts] Patches installed."
                : $"[CE+SS Loadouts] Patches installed with {failedClasses} class(es) disabled — see errors above.");

            // Comp-attach canary: if the XML patch stops matching pawn defs, every patch
            // quietly no-ops behind a null comp — the one failure mode with no other
            // signal anywhere.
            if (ThingDefOf.Human?.comps?.Any(c => c is CompProperties_LoadoutSidearms) != true)
            {
                Log.Error("[CE+SS Loadouts] CompLoadoutSidearms is not attached to Human — "
                          + "Patches/pawnComp.xml no longer matches the pawn defs and the whole "
                          + "feature is inert.");
            }
        }
    }
}
