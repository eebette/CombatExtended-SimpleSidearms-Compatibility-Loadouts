using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CombatExtended;
using HarmonyLib;
using Verse;

namespace CESupplyTestStaging
{
    /// <summary>
    /// In-game benchmark for the Loadout.GetSlotsFor postfix, run with
    ///   -celoadsave=SUPPLY-1-loadout-sidearms -ceassert=supplybench
    ///
    /// Combat Extended benchmarks inside RimWorld rather than in a desktop harness, so this
    /// measures the calls CE actually makes on a loaded save with a real ad-hoc loadout.
    ///
    /// Three shapes, because they cost different things:
    ///   full        — enumerate every slot (what GetPrioritySlot does in the worst case)
    ///   firstOnly   — FirstOrDefault, what Utility_HoldTracker.GetExcessEquipment does
    ///   anyMatch    — Any(predicate), what JobGiver_UpdateLoadout.GetUpdateLoadoutJob does
    ///
    /// The last two are the point: CE wrote them to stop early, and a postfix that
    /// materialises the stream takes that away. Each is measured with the module's patches
    /// active and again with them removed, in one process, so the save and JIT state match.
    /// </summary>
    public class SupplyBenchRunnerComponent : GameComponent
    {
        private const int WarmupIterations = 5000;
        // Sub-microsecond per call, so a short round measures scheduler noise rather than the
        // work: 200k iterations puts each round in the tens of milliseconds.
        private const int TimedIterations = 200000;
        private const int Rounds = 5;

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
            results.Add($"  \"adHoc\": {(loadout.adHoc ? "true" : "false")}");
            results.Add($"  \"timedIterations\": {TimedIterations}");

            ThingDef probe = loadout.Slots.Select(s => s.thingDef).FirstOrDefault(d => d != null);

            Func<int> full = () => loadout.GetSlotsFor(pawn).Count();
            Func<int> firstOnly = () => loadout.GetSlotsFor(pawn).FirstOrDefault() != null ? 1 : 0;
            Func<int> anyMatch = () => loadout.GetSlotsFor(pawn).Any(s => s.thingDef == probe) ? 1 : 0;

            Report("patched", Measure(full), Measure(firstOnly), Measure(anyMatch));

            new Harmony("eebette.CESidearmsSupplyBench").UnpatchAll("eebette.CESidearmsSupply");
            Log.Message("[SupplyBench] Module patches removed; measuring stock CE.");

            Report("stock", Measure(full), Measure(firstOnly), Measure(anyMatch));
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

        private static string Escape(string s)
        {
            return s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") ?? "";
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
            if (GenCommandLine.TryGetCommandLineArg("celoadsave", out string save) && !save.NullOrEmpty())
            {
                LongEventHandler.ExecuteWhenFinished(() => GameDataSaveLoader.LoadGame(save));
            }
        }
    }
}
