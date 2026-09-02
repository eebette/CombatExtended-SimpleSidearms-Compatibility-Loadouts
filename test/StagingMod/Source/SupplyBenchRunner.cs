using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CombatExtended;
using SimpleSidearms.rimworld;
using HarmonyLib;
using Verse;

namespace CESupplyTestStaging
{
    /// <summary>
    /// In-game benchmark for the loadout-sidearm reconcile, run with
    ///   -celoadsave=SUPPLY-1-loadout-sidearms -ceassert=supplybench
    ///
    /// Two questions, both of which decide whether a single reconciling hook is the right
    /// trigger, and neither of which should be answered by reasoning about the code:
    ///
    ///   cost — microseconds per reconcile, with the module's patches active and again with
    ///          them removed, in one process so the save and JIT state match
    ///   rate — how often CE actually calls it per colonist, counted over a live sample
    ///          rather than derived from CE's own throttle cooldown
    ///
    /// Combat Extended benchmarks inside RimWorld rather than in a desktop harness.
    /// </summary>
    public class SupplyBenchRunnerComponent : GameComponent
    {
        private const int WarmupIterations = 5000;
        // Sub-microsecond per call, so a short round measures scheduler noise rather than the
        // work: 200k iterations puts each round in the tens of milliseconds.
        private const int TimedIterations = 200000;
        private const int Rounds = 5;
        private const float FrameBudgetMs = 1000f / 60f;
        private const int ProjectedColonists = 20;
        /// <summary>Live sample of CE's real call rate before anything is timed.</summary>
        private const int SampleTicks = 6000;

        private static int observedCalls;
        private double observedRate;

        private string scenario;
        private bool active;
        private bool done;
        private int startTick;

        private readonly List<string> results = new List<string>();

        public SupplyBenchRunnerComponent(Game game)
        {
        }

        public override void LoadedGame()
        {
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("supplybench"))
            {
                return;
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                active = true;
                startTick = Find.TickManager.TicksGame;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            });
        }

        public override void GameComponentTick()
        {
            if (!active || done)
            {
                return;
            }
            if (Find.TickManager.TicksGame - startTick < 120)
            {
                return; // let CE finish its first inventory passes
            }
            // Count real invocations for a while before timing anything: the cost only
            // matters multiplied by the rate, and the rate is CE's to decide.
            if (Find.TickManager.TicksGame - startTick < 120 + SampleTicks)
            {
                return;
            }
            int colonists = Math.Max(1, Find.CurrentMap?.mapPawns?.FreeColonistsSpawnedCount ?? 1);
            observedRate = observedCalls * 1000.0 / SampleTicks / colonists;
            done = true;
            try
            {
                Run();
            }
            catch (Exception e)
            {
                Log.Error("[SupplyBench] Failed: " + e);
                results.Add($"  \"crashed\": \"{Escape(e.ToString())}\"");
            }
            Write();
            Root.Shutdown();
        }

        private void Run()
        {
            Pawn pawn = Find.CurrentMap?.mapPawns?.FreeColonists?
                .FirstOrDefault(p => p.TryGetComp<CompInventory>() != null && !p.GetLoadout().defaultLoadout);
            if (pawn == null)
            {
                throw new InvalidOperationException("No colonist with a non-default loadout");
            }
            Loadout loadout = pawn.GetLoadout();
            results.Add($"  \"pawn\": \"{Escape(pawn.LabelShort)}\"");
            results.Add($"  \"loadoutSlots\": {loadout.Slots.Count}");
            results.Add($"  \"rememberedWeapons\": {CompSidearmMemory.GetMemoryCompForPawn(pawn)?.RememberedWeapons?.Count ?? 0}");
            results.Add($"  \"timedIterations\": {TimedIterations}");
            results.Add($"  \"observedCallsPerColonistPer1000Ticks\": {observedRate:F2}");
            results.Add($"  \"observedSampleTicks\": {SampleTicks}");

            // The reconcile is a prefix on TryGiveJob, so calling it is what the game does.
            // The returned job is discarded; physical work stays with the pawn's think tree.
            var giver = new JobGiver_UpdateLoadout();
            Func<int> reconcile = () => giver.TryGiveJob(pawn) != null ? 1 : 0;

            double patched = Measure(reconcile);
            new Harmony("eebette.CESimpleSidearmsCompat.LoadoutsBench").UnpatchAll("eebette.CESimpleSidearmsCompat.Loadouts");
            Log.Message("[SupplyBench] Module patches removed; measuring stock CE.");
            double stock = Measure(reconcile);

            double overhead = patched - stock;
            results.Add($"  \"patchedUsPerCall\": {patched:F3}");
            results.Add($"  \"stockUsPerCall\": {stock:F3}");
            results.Add($"  \"reconcileOverheadUsPerCall\": {overhead:F3}");

            // What that costs at colony scale, at the rate CE was actually observed to call it.
            double msPerTick = overhead * ProjectedColonists * (observedRate / 1000.0) / 1000.0;
            results.Add($"  \"projectedColonists\": {ProjectedColonists}");
            results.Add($"  \"msPerTickAtScale\": {msPerTick:F5}");
            results.Add($"  \"pctOfFrameAtScale\": {msPerTick / FrameBudgetMs * 100.0:F4}");
            Log.Message($"[SupplyBench] reconcile overhead {overhead:F3} us/call, observed {observedRate:F2} calls "
                        + $"per colonist per 1000 ticks → {msPerTick / FrameBudgetMs * 100.0:F4}% of a 60fps frame "
                        + $"at {ProjectedColonists} colonists");
        }

        /// <summary>Best-of-N: the minimum is the least noisy estimate; GC only adds time.</summary>
        private static double Measure(Func<int> body)
        {
            int sink = 0;
            for (int i = 0; i < WarmupIterations; i++)
            {
                sink += body();
            }
            double best = double.MaxValue;
            for (int round = 0; round < Rounds; round++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                for (int i = 0; i < TimedIterations; i++)
                {
                    sink += body();
                }
                watch.Stop();
                double usPerCall = watch.Elapsed.TotalMilliseconds * 1000.0 / TimedIterations;
                if (usPerCall < best)
                {
                    best = usPerCall;
                }
            }
            if (sink < 0)
            {
                Log.Message("[SupplyBench] sink " + sink);
            }
            return best;
        }

        private void Report(string arm, double full, double firstOnly, double anyMatch)
        {
            results.Add($"  \"{arm}FullUsPerCall\": {full:F3}");
            results.Add($"  \"{arm}FirstOnlyUsPerCall\": {firstOnly:F3}");
            results.Add($"  \"{arm}AnyMatchUsPerCall\": {anyMatch:F3}");
            Log.Message($"[SupplyBench] {arm}: full {full:F3} us, firstOnly {firstOnly:F3} us, anyMatch {anyMatch:F3} us");
        }

        private void Write()
        {
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, $"bench-results-{scenario}.json");
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"scenario\": \"{Escape(scenario)}\",\n");
            sb.Append(string.Join(",\n", results));
            sb.Append("\n}\n");
            File.WriteAllText(path, sb.ToString());
            Log.Message("[SupplyBench] Wrote " + path);
        }

        internal static void CountCall()
        {
            observedCalls++;
        }

        private static string Escape(string s)
        {
            return s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") ?? "";
        }
    }

    /// <summary>Counts how often CE actually reaches the reconcile, for the rate half.</summary>
    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), "TryGiveJob")]
    public static class SupplyBenchCallCounter
    {
        public static bool Prepare()
        {
            return GenCommandLine.TryGetCommandLineArg("ceassert", out string scenario)
                   && !scenario.NullOrEmpty() && scenario.StartsWith("supplybench");
        }

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn)
        {
            if (pawn != null && pawn.IsColonist)
            {
                SupplyBenchRunnerComponent.CountCall();
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class SupplyBenchBoot
    {
        static SupplyBenchBoot()
        {
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out string scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("supplybench"))
            {
                return;
            }
            // The staging assembly has no Harmony bootstrap of its own, so the call counter
            // below is only applied for bench runs — and without this it silently never runs,
            // which is exactly how the first measurement reported a rate of 0.00.
            new Harmony("eebette.CESimpleSidearmsCompat.LoadoutsBench").PatchAll(typeof(SupplyBenchBoot).Assembly);

            if (GenCommandLine.TryGetCommandLineArg("celoadsave", out string save) && !save.NullOrEmpty())
            {
                LongEventHandler.ExecuteWhenFinished(() => GameDataSaveLoader.LoadGame(save));
            }
        }
    }
}
