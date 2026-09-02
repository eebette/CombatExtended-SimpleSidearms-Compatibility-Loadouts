using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace CESimpleSidearmsCompat.Loadouts.Patches
{
    /// <summary>
    /// Save back-compatibility for this mod's own GameComponent renames.
    ///
    /// The session component has shipped under three names as the working title
    /// was retired:
    ///   CESidearmsSupply.SupplyGameComponent
    ///     -> CESidearmsSupply.SupplySessionComponent
    ///       -> CESimpleSidearmsCompat.Loadouts.LoadoutsSessionComponent (current).
    /// A save written under an older version stores the game-component node under
    /// the retired class string. GameComponents are scribed as a list of
    /// &lt;li Class="..."&gt; and resolved by that string, so
    /// ScribeExtractor.SaveableFromNode cannot find the type, falls back to the
    /// abstract Verse.GameComponent, and THROWS "Can't load abstract class" — a
    /// blocking load error. This maps the retired names to the current component
    /// so the node binds cleanly and the error is gone.
    ///
    /// No data migration is required: LoadoutsSessionComponent scribes nothing
    /// persistent (it is a runtime liveness canary plus a release trigger), and the
    /// loadout projection reconciles from live CE-loadout + Simple Sidearms memory
    /// state on load. The old node's scribed records are simply left unread.
    ///
    /// Only GameComponents need this. The ThingComp (CompLoadoutSidearms) was
    /// renamed too, but comps are recreated from their ThingDef, not resolved from
    /// the saved Class string, so a comp rename never raises could-not-find-class.
    ///
    /// Gated with the rest of the mod (Bootstrap patches nothing when CE or SS is
    /// absent): loading an old Loadouts save with Simple Sidearms removed is an
    /// already-broken config the mod errors loudly about, so one more orphan line
    /// there is noise on noise, not worth breaking the all-or-nothing activation.
    /// </summary>
    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.GetBackCompatibleType),
                  new[] { typeof(Type), typeof(string), typeof(XmlNode) })]
    public static class BackCompatibility_GetBackCompatibleType_Patch
    {
        // Every fully-qualified name this mod's GameComponent has shipped under
        // before the current one. Extend if it is ever renamed again.
        private static readonly HashSet<string> RetiredGameComponentClasses = new HashSet<string>
        {
            "CESidearmsSupply.SupplyGameComponent",
            "CESidearmsSupply.SupplySessionComponent",
        };

        public static bool Prepare()
        {
            if (AccessTools.Method(typeof(BackCompatibility), "GetBackCompatibleType",
                    new[] { typeof(Type), typeof(string), typeof(XmlNode) }) != null)
            {
                return true;
            }
            Log.Error("[CE+SS Loadouts] Verse.BackCompatibility.GetBackCompatibleType not found — "
                      + "a save written under this mod's earlier class names will log a blocking "
                      + "could-not-find-class error on load (the projection still rebuilds). "
                      + "RimWorld probably moved it.");
            return false;
        }

        // Runs only when vanilla could not resolve the class (__result == null) and
        // the name is one this mod retired — every other resolution is untouched.
        [HarmonyPostfix]
        public static void Postfix(string providedClassName, ref Type __result)
        {
            if (__result == null && providedClassName != null
                && RetiredGameComponentClasses.Contains(providedClassName))
            {
                __result = typeof(LoadoutsSessionComponent);
            }
        }
    }
}
