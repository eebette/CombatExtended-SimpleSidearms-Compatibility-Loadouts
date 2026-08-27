using System.Linq;
using HarmonyLib;
using RimWorld;
using SimpleSidearms.rimworld;
using UnityEngine;
using Verse;

namespace CESimpleSidearmsCompat.Loadouts
{
    public class LoadoutsSettings : ModSettings
    {
        public bool loadoutWeaponsAsSidearms = true;

        /// <summary>
        /// "The feature is off with unfinished cleanup." Armed whenever a release runs
        /// with the feature off — the off-toggle (with or without a game loaded) and any
        /// sweep that had to defer away pawns — and consumed once per load by
        /// LoadoutsSessionComponent until the feature is turned back on. Settings are
        /// global and records are per-save, so the flag is what carries the cleanup to
        /// every other save.
        /// </summary>
        public bool releasePending;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref loadoutWeaponsAsSidearms, "loadoutWeaponsAsSidearms", true);
            Scribe_Values.Look(ref releasePending, "releasePending", false);
        }
    }

    public class LoadoutsMod : Mod
    {
        public static LoadoutsSettings Settings { get; private set; }

        public LoadoutsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<LoadoutsSettings>();
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

            bool was = Settings.loadoutWeaponsAsSidearms;
            listing.CheckboxLabeled("Loadout weapons as sidearms", ref Settings.loadoutWeaponsAsSidearms,
                "Weapons listed in a CE loadout are auto-remembered as sidearms by assigned pawns. "
                + "The first ranged weapon in the list becomes the default ranged weapon and the first "
                + "melee the preferred melee weapon. Removing a weapon from the loadout makes the pawn "
                + "forget it as a sidearm, which is what lets CE clear it out of the inventory.");

            // Turning it off has to undo it, not freeze it: the compat patch exempts every
            // remembered weapon from CE's drop, so claims left behind with nobody to release
            // them pin weapons in inventories with no way back short of the gizmo. Release()
            // itself arms releasePending whenever the feature is off — settings are global
            // and the sweep is per-colony, so every OTHER save gets its sweep on next load.
            if (was && !Settings.loadoutWeaponsAsSidearms)
            {
                Release(interactive: true);
            }
            else if (!was && Settings.loadoutWeaponsAsSidearms && Settings.releasePending)
            {
                // Changed their mind — with no save loaded, or after an off-toggle armed the
                // flag. Without this the deferred release fires anyway, on an enabled
                // feature, and wipes their exclusions.
                Settings.releasePending = false;
                Settings.Write();
            }

            listing.Gap();
            bool inGame = Current.Game != null;
            if (!inGame)
            {
                GUI.color = Color.gray;
            }
            bool clicked = listing.ButtonText("Release all claimed sidearms",
                                   "Forget every sidearm this mod added, on every colonist, and start "
                                   + "over. Weapons the loadout does not list are not touched.");
            GUI.color = Color.white;
            if (clicked && inGame)
            {
                Release(interactive: true);
            }
            if (!inGame)
            {
                GUI.color = Color.gray;
                listing.Label("  (available once a save is loaded — it acts on that colony's pawns)");
                GUI.color = Color.white;
            }

            listing.Gap();
            listing.Label("Ammo for sidearms is Combat Extended's own job: add the ammo to the loadout and "
                          + "CE keeps the pawn stocked to that count, the same as for any other item.");
            listing.End();
        }

        /// <summary>
        /// Hand back every claimed pair on every colonist. Returns false when there is no
        /// game to act on, so the caller can defer.
        /// </summary>
        /// <param name="interactive">True for the settings toggle and button: they always
        /// show the result. The once-per-load sweep passes false, so a colony with nothing
        /// to release loads without a "Released 0" toast — forever, since the flag
        /// deliberately never clears while the feature is off.</param>
        public static bool Release(bool interactive = false)
        {
            if (Current.Game == null)
            {
                Settings.releasePending = true;
                Settings.Write();
                Messages.Message("[CE+SS Loadouts] No save loaded — claimed sidearms will be released "
                                 + "when you next load one.", MessageTypeDefOf.CautionInput, historical: false);
                return false;
            }
            // Settings are global, the sweep is per-colony. While the feature is off, any
            // release arms the once-per-load sweep so every other save is cleaned the next
            // time it is loaded. (With the feature on there is nothing to arm — the
            // reconcile re-claims on its own cadence.)
            if (!Settings.loadoutWeaponsAsSidearms && !Settings.releasePending)
            {
                Settings.releasePending = true;
                Settings.Write();
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
            // The flag deliberately survives a successful sweep. It means "the feature is
            // off with unfinished cleanup", and it stays set — releasing on every load, for
            // every save — until the player turns the feature back on. Clearing it after the
            // first save meant a second colony never got released at all. Away pawns ride
            // the same flag: their memory comps resolve on the load that brings them back.
            if (interactive || released > 0 || deferred > 0)
            {
                // The "later load" promise is only made when the armed flag actually
                // delivers it. With the feature on nothing retries — the reconcile
                // re-claims on its own — so away pawns are reported as skipped instead.
                string away = deferred == 0 ? ""
                    : Settings.releasePending
                        ? $" {deferred} pawn(s) are away; they are released on a later save load."
                        : $" {deferred} pawn(s) are away and were skipped.";
                Messages.Message($"[CE+SS Loadouts] Released {released} claimed sidearm(s)." + away,
                                 MessageTypeDefOf.TaskCompletion, historical: false);
            }
            return true;
        }
    }

    /// <summary>
    /// Runs the deferred release once per loaded game. It used to run from the reconcile's
    /// Harmony prefix, which was wrong three ways: that hook can fire inside an open
    /// gizmo-interaction scope (its forgets were then recorded as player exclusions), it
    /// re-ran the release every pass while any colonist was unspawned (a caravan out meant
    /// a notification chime forever), and the global flag was cleared after the first save
    /// so a second colony was never cleaned.
    /// </summary>
    public class LoadoutsSessionComponent : GameComponent
    {
        /// <summary>Incremented by the reconcile prefix; consumed by the liveness canary.</summary>
        public static int reconcilePasses;

        private int lastLivenessTick;

        public LoadoutsSessionComponent(Game game)
        {
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
            if (firstWindow || hadPasses || LoadoutsMod.Settings == null
                || !LoadoutsMod.Settings.loadoutWeaponsAsSidearms)
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
            if (LoadoutsMod.Settings != null && LoadoutsMod.Settings.releasePending
                && !LoadoutsMod.Settings.loadoutWeaponsAsSidearms)
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
