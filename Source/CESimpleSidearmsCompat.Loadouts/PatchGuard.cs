using System;
using HarmonyLib;
using SimpleSidearms.rimworld;
using Verse;

namespace CESimpleSidearmsCompat.Loadouts
{
    /// <summary>Shared failure-doctrine helpers: the log prefix and the SS-method existence guard.</summary>
    internal static class PatchGuard
    {
        internal const string LogPrefix = "[CE+SS Loadouts] ";

        /// <summary>True if the CompSidearmMemory method exists; else logs the consequence and skips the class.</summary>
        internal static bool Require(string method, Type[] args, string consequence)
        {
            if (AccessTools.Method(typeof(CompSidearmMemory), method, args) != null)
            {
                return true;
            }
            Log.Error($"{LogPrefix}CompSidearmMemory.{method} not found — {consequence} "
                      + "Simple Sidearms probably moved it.");
            return false;
        }
    }
}
