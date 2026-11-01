// PlayMode perf baseline: "where is the frame going, right now?"
//
// WHAT THIS IS FOR
// The complaint is "the game has less fps in the game loop". That is not answerable from a boot
// capture — every dump in ProfilerCaptures/ is one (PfeProfilerAutoDump fires at frames 5 and 60) —
// and it is not answerable from an absolute frame time either, because a dev build on this machine is
// not the shipping build. What IS answerable, in one run, is the SHAPE of the frame:
//
//   1. frame budget      — min / median / p95 / max frame ms over a settled window
//   2. the split         — how much of that frame is inside the sim, and how much is outside it
//   2b. tick vs idle     — the same frames split by whether they ran a tick. With a 30 Hz clock on a
//                          60 Hz frame the sim lands in HALF the frames, so the average share halves it.
//                          This is the row that predicts stutter; the average share is the row that
//                          sends people to the renderer.
//   3. the sim, ranked   — every ISimTickable by cost, grouped by type (TickableCostTable)
//   4. the regions       — the ordinary PfeProfiler dump for the same window, in the same folder
//
// (2) is the decision that matters: a sim that costs 20 ms/tick and a renderer that costs 20 ms/frame
// produce the same fps and need opposite fixes. `sim.frame`'s self time is the line between them.
//
// WHAT THIS DOES NOT DO
// It does not assert a frame time. An absolute number from a dev build in the editor is not a gate —
// it is a different machine from the one the player runs. The only assertions here are on the
// INSTRUMENT: the tickable table must reconcile to the dispatch loop it claims to explain, and the
// sample must actually contain frames. Everything else is printed for a human to read.
//
// HOW TO RUN
//   Window > General > Test Runner > PlayMode > PFE.Tests.PlayMode > GameLoopPerfTests
//   (or filter on the "Perf" category). It loads SampleScene itself, so it does not need the editor
//   in Play mode first. Expect ~15-30 s: the game boots a world before the sample starts.
//
// Output: ProfilerCaptures/perf_playmode_<stamp>.txt  (the report)
//         ProfilerCaptures/perf_playmode_<stamp>.csv  (the per-frame series, for A/B by eye or script)
//         ProfilerCaptures/profiling_perf_playmode_<stamp>.txt (PfeProfiler regions, same window)
//
// Background: docs/Perf_FpsLoss_Plan_2026-10-27.md §3 P0/P1/P2.

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VContainer;
using PFE.Core;
using PFE.Core.Profiling;
using PFE.Systems.Map;

namespace PFE.Tests.PlayMode.Performance
{
    [Category("Perf")]
    public class GameLoopPerfTests
    {
        /// <summary>The playable scene. It is the only enabled scene in Build Settings.</summary>
        private const string SceneName = "SampleScene";

        /// <summary>
        /// Frames discarded before sampling. The first seconds after a room is built are still
        /// settling (coroutines finishing, pooled objects created, textures uploaded) and averaging
        /// them in is how a boot cost gets reported as a frame cost.
        /// </summary>
        private const int WarmupFrames = 120;

        /// <summary>Frames sampled. 300 at 60 fps is 5 s — long enough for a p95 to mean something.</summary>
        private const int MeasureFrames = 300;

        /// <summary>How long the world is given to finish building before the test fails.</summary>
        private const float BootTimeoutSeconds = 180f;

        /// <summary>
        /// Largest fraction of the dispatch loop that may go unattributed before the table is declared
        /// untrustworthy. The residual is loop overhead (one branch, one `continue`, one array read per
        /// tickable), so a few percent at most; 5 % is slack, not a budget.
        /// </summary>
        private const double MaxUnaccountedFraction = 0.05;

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator GameLoop_FrameBudget_WritesReport()
        {
            yield return LoadGameScene();

            GameLifetimeScope scope = null;
            yield return WaitFor(
                () => (scope = UnityEngine.Object.FindFirstObjectByType<GameLifetimeScope>()) != null,
                60f,
                "a GameLifetimeScope in the loaded scene");

            SimLoop simLoop = null;
            yield return WaitFor(
                () =>
                {
                    try
                    {
                        simLoop = scope.Container.Resolve<SimLoop>();
                        return simLoop != null;
                    }
                    catch (System.Exception)
                    {
                        return false;
                    }
                },
                60f,
                "SimLoop from the container");

            GameManager gameManager = null;
            LandMap landMap = null;
            yield return WaitFor(
                () =>
                {
                    try
                    {
                        gameManager = gameManager ?? scope.Container.Resolve<GameManager>();
                        landMap = landMap ?? scope.Container.Resolve<LandMap>();
                        return true;
                    }
                    catch (System.Exception)
                    {
                        return false;
                    }
                },
                60f,
                "GameManager and LandMap from the container");

            // The world is built across frames, so "initialized" arrives well after the scene loads.
            // Waiting on all three is deliberate: IsInitialized() alone can be true while the current
            // room is still null, and a sample taken then measures an empty room and calls it cheap.
            yield return WaitFor(
                () => gameManager.IsInitialized()
                      && landMap.currentRoom != null
                      && simLoop.TickableCount > 0,
                BootTimeoutSeconds,
                "the world to finish building (GameManager.IsInitialized, currentRoom != null, tickables registered)");

            // Let the last build coroutines drain before the instrument is armed.
            for (int i = 0; i < 30; i++)
            {
                yield return null;
            }

            string roomLabel = landMap.currentRoom != null
                ? $"{landMap.currentRoom.id} (template {landMap.currentRoom.templateId})"
                : "<no room>";

            // ── arm the instruments ──────────────────────────────────────────────────────
            simLoop.TickableCosts.Enabled = true;
            simLoop.TickableCosts.Reset();

            bool profilerWasEnabled = PfeProfiler.Enabled;
            PfeProfiler.Reset();

            for (int i = 0; i < WarmupFrames; i++)
            {
                yield return null;
            }

            // Reset AFTER the warmup, so the measured window contains no warmup frames. Resetting the
            // table also clears a mid-run registration invalidation, which is why the report checks it.
            simLoop.TickableCosts.Reset();
            PfeProfiler.Reset();

            long gcStart = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            int droppedAtStart = simLoop.Metrics.DroppedTicks;

            // ── sample ───────────────────────────────────────────────────────────────────
            var frameMs = new double[MeasureFrames];
            var simMs = new double[MeasureFrames];
            var ticksPerFrame = new int[MeasureFrames];
            double dispatchTotalMs = 0.0;
            int sampled = 0;

            // The frame duration is timed with a Stopwatch across the yield, NOT with
            // Time.unscaledDeltaTime.
            //
            // Time.deltaTime at frame i is the duration of frame i-1 — that is Unity's definition, not a
            // quirk — so pairing it with a sim value read at the same instant compares THIS frame's work
            // against the PREVIOUS frame's length. The first run of this fixture did exactly that and
            // reported a NEGATIVE correlation between frame time and sim time (-0.335), i.e. "more sim
            // work makes the frame faster", which is the one reading that would send someone to the
            // renderer. The interval between two consecutive resume points is the frame that actually
            // contained the tick, so it pairs exactly with LastFrameTickMs read right after it.
            long previousTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            for (int i = 0; i < MeasureFrames; i++)
            {
                yield return null;

                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                frameMs[sampled] = (nowTicks - previousTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                previousTicks = nowTicks;

                // LastFrameTickMs, not LastTickMs: a frame that ran two ticks must contribute both, or
                // the reconciliation below compares a whole-frame table against a single tick.
                simMs[sampled] = simLoop.LastFrameTickMs;
                ticksPerFrame[sampled] = simLoop.Metrics.TicksThisFrame;
                dispatchTotalMs += simLoop.LastFrameTickMs;
                sampled++;
            }

            long gcEnd = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            int droppedAtEnd = simLoop.Metrics.DroppedTicks;

            // ── close the instruments ────────────────────────────────────────────────────
            simLoop.TickableCosts.Enabled = false;

            if (profilerWasEnabled)
            {
                PfeProfiler.Dump("perf_playmode");
            }

            // ── report ───────────────────────────────────────────────────────────────────
            var sb = new StringBuilder();
            sb.AppendLine("===== PFE PLAY MODE PERF =====");
            sb.AppendLine($"unity            : {Application.unityVersion}");
            sb.AppendLine($"scene            : {SceneName}");
            sb.AppendLine($"room             : {roomLabel}");
            sb.AppendLine($"isDebugBuild     : {Debug.isDebugBuild}");
            sb.AppendLine($"tickables        : {simLoop.TickableCount}");
            sb.AppendLine($"clock            : {simLoop.Clock.TicksPerSecond} Hz");
            sb.AppendLine($"warmup / measured: {WarmupFrames} / {MeasureFrames} frames");
            sb.AppendLine($"profiler regions : {(profilerWasEnabled ? "dumped alongside" : "OFF (PfeDebugSettings.ProfilingEnabled is false)")}");
            sb.AppendLine("note             : this is a Play-mode / editor build on this machine, NOT a shipping");
            sb.AppendLine("                   build. Compare two runs against each other; never compare an");
            sb.AppendLine("                   absolute frame time here to a target fps.");
            sb.AppendLine();

            AppendFrameBudget(sb, frameMs, sampled, simMs, ticksPerFrame, dispatchTotalMs);
            AppendGcAndDrops(sb, gcStart, gcEnd, droppedAtStart, droppedAtEnd, sampled);
            sb.AppendLine(simLoop.TickableCosts.BuildReport(dispatchTotalMs, sampled));
            AppendReadingGuide(sb);

            sb.AppendLine("==============================");

            string report = sb.ToString();
            Debug.Log(report);

            string written = WriteReport(report, frameMs, simMs, ticksPerFrame, sampled);

            // ── assertions: the instrument, never the frame time ─────────────────────────
            //
            // There is deliberately no assertion on fps, frame ms, or any absolute number. This runs in
            // the editor on this machine; the player runs on another build and another GPU. A gate on
            // an absolute figure here would be red for reasons nobody can act on. The two assertions
            // below fail only when the measurement itself is untrustworthy.

            Assert.Greater(simLoop.TickableCosts.Calls, 0,
                "[perf] the tickable table recorded no calls. Either TickableCosts.Enabled never took " +
                "effect, or no ISimTickable ran during the window. A zero here is not 'the sim is free'.");

            double unaccounted = simLoop.TickableCosts.UnaccountedFraction(dispatchTotalMs);
            Assert.Less(unaccounted, MaxUnaccountedFraction,
                $"[perf] the tickable table does not reconcile to the dispatch loop: " +
                $"{simLoop.TickableCosts.TotalMs:F1} ms accounted against {dispatchTotalMs:F1} ms measured " +
                $"({unaccounted * 100.0:F1} % unaccounted). The ranking must not be acted on until this " +
                "closes — a wrong instrument reads exactly like a fast tick. See the report at " +
                written + ".");

            if (simLoop.TickableCosts.InvalidatedSinceReset)
            {
                Debug.LogWarning(
                    "[perf] the tickable registration changed DURING the measured window, so the table " +
                    "covers only the period after that change. The report carries the same warning. " +
                    "Re-run before comparing this ranking to anything.");
            }

            Debug.Log($"[perf] report: {written}");
        }

        // ── Report sections ────────────────────────────────────────────────────────────

        /// <summary>
        /// The frame budget. p95 is the number to quote: a mean hides a room that stutters twice a
        /// second, and the max is one unlucky frame.
        /// </summary>
        private static void AppendFrameBudget(
            StringBuilder sb,
            double[] frameMs,
            int count,
            double[] simMs,
            int[] ticksPerFrame,
            double dispatchTotalMs)
        {
            double[] sorted = (double[])frameMs.Clone();
            System.Array.Sort(sorted, 0, count);

            double mean = 0.0;
            double simMean = 0.0;
            int tickSum = 0;
            for (int i = 0; i < count; i++)
            {
                mean += frameMs[i];
                simMean += simMs[i];
                tickSum += ticksPerFrame[i];
            }

            mean /= count;
            simMean /= count;

            sb.AppendLine("-- frame budget (ms) --");
            sb.AppendLine($"  mean {mean,8:F2}   median {Percentile(sorted, count, 0.50),8:F2}   " +
                          $"p95 {Percentile(sorted, count, 0.95),8:F2}   max {sorted[count - 1],8:F2}   " +
                          $"min {sorted[0],8:F2}");
            sb.AppendLine($"  fps (1/mean) {1000.0 / System.Math.Max(mean, 1e-6),6:F1}");
            sb.AppendLine();

            sb.AppendLine("-- the split: sim vs everything else --");
            sb.AppendLine($"  sim dispatch / frame   {simMean,8:F3} ms   ({simMean / System.Math.Max(mean, 1e-6) * 100.0,5:F1} % of the frame)");
            sb.AppendLine($"  outside the sim        {mean - simMean,8:F3} ms   ({(mean - simMean) / System.Math.Max(mean, 1e-6) * 100.0,5:F1} %)");
            sb.AppendLine($"  ticks / frame          {tickSum / (double)count,8:F2}");
            sb.AppendLine();

            // ── the number that predicts stutter, not the average share ───────────────────────
            //
            // The average share above is the wrong statistic when the clock is slower than the frame
            // rate: a 30 Hz tick on a 60 Hz frame puts ALL of the sim into half the frames and none into
            // the other half. "The sim is 29 % of the frame" then reads as "look at the renderer" when
            // the truth is "every second frame is 2x too long". Split the sample by whether the frame
            // ran a tick and print both populations.
            int idleCount = 0;
            int tickCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (ticksPerFrame[i] > 0) tickCount++;
                else idleCount++;
            }

            sb.AppendLine("-- tick frames vs idle frames (the stutter shape) --");
            if (tickCount == 0 || idleCount == 0)
            {
                sb.AppendLine($"  every sampled frame ran the same number of ticks (tick frames {tickCount}, " +
                              $"idle frames {idleCount}), so there is no split to report. That happens when");
                sb.AppendLine("  the clock is faster than the frame rate — one tick per frame, no idle frames.");
                sb.AppendLine();
            }
            else
            {
                var idleMs = new double[idleCount];
                var tickMs = new double[tickCount];
                int io = 0;
                int it = 0;
                for (int i = 0; i < count; i++)
                {
                    if (ticksPerFrame[i] > 0) tickMs[it++] = frameMs[i];
                    else idleMs[io++] = frameMs[i];
                }

                System.Array.Sort(idleMs);
                System.Array.Sort(tickMs);

                double idleMean = 0.0;
                for (int i = 0; i < idleCount; i++) idleMean += idleMs[i];
                idleMean /= idleCount;

                double tickMean = 0.0;
                for (int i = 0; i < tickCount; i++) tickMean += tickMs[i];
                tickMean /= tickCount;

                sb.AppendLine($"  idle frames (no tick)  n={idleCount,4}   mean {idleMean,7:F2} ms   " +
                              $"median {Percentile(idleMs, idleCount, 0.50),7:F2} ms   p95 {Percentile(idleMs, idleCount, 0.95),7:F2} ms   " +
                              $"({1000.0 / System.Math.Max(idleMean, 1e-6),5:F0} fps)");
                sb.AppendLine($"  tick frames            n={tickCount,4}   mean {tickMean,7:F2} ms   " +
                              $"median {Percentile(tickMs, tickCount, 0.50),7:F2} ms   p95 {Percentile(tickMs, tickCount, 0.95),7:F2} ms   " +
                              $"({1000.0 / System.Math.Max(tickMean, 1e-6),5:F0} fps)");
                sb.AppendLine($"  one tick adds          {tickMean - idleMean,8:F2} ms to the frame " +
                              $"({tickMean / System.Math.Max(idleMean, 1e-6),4:F2}x)");
                sb.AppendLine();
                sb.AppendLine("  READ: this is the frame the player actually feels — it alternates between the");
                sb.AppendLine("  two rows above. 'One tick adds' is the sim's true frame cost; the mean share");
                sb.AppendLine("  divides it by the tick-to-frame ratio and understates it by that factor.");
                sb.AppendLine("  Against a 16.6 ms (60 fps) budget, a tick that adds more than ~16 ms cannot be");
                sb.AppendLine("  fixed by trimming the renderer, and one that adds more than ~8 ms cannot hold");
                sb.AppendLine("  60 fps once the rest of the frame is counted.");
                sb.AppendLine();
            }

            sb.AppendLine("  READ: 'sim dispatch' is wall time inside the tickable loop, so it EXCLUDES the");
            sb.AppendLine("  accumulator, the rate window and the region bookkeeping. 'Outside the sim' is");
            sb.AppendLine("  Update/LateUpdate, animation, rendering, UI and audio. It is the cost of the");
            sb.AppendLine("  frames that did NOT tick, so if it is large the renderer is ALSO a target — but");
            sb.AppendLine("  a low 'outside' figure is not evidence that the renderer is fine; it is only");
            sb.AppendLine("  evidence that it is not the slow half of the tick frames.");
            sb.AppendLine();
        }

        /// <summary>
        /// Managed heap growth and dropped ticks. Neither is a frame cost, and both change the reading
        /// when they are non-zero — so they are printed next to the budget rather than in a footnote.
        /// </summary>
        private static void AppendGcAndDrops(
            StringBuilder sb,
            long gcStart,
            long gcEnd,
            int droppedAtStart,
            int droppedAtEnd,
            int count)
        {
            long delta = gcEnd - gcStart;
            sb.AppendLine("-- side channels --");
            sb.AppendLine($"  managed heap delta     {delta / 1024.0,8:F1} KB over {count} frames " +
                          $"({delta / 1024.0 / count,7:F2} KB/frame)");
            sb.AppendLine($"  dropped ticks          {droppedAtEnd - droppedAtStart,8}");
            sb.AppendLine();

            if (delta > 0 && delta / 1024.0 / count > 64.0)
            {
                sb.AppendLine("  NOTE: >64 KB/frame of managed growth is enough to produce a GC stutter");
                sb.AppendLine("  every few seconds. That is a different fault from a slow frame and it will");
                sb.AppendLine("  show up as the p95/max gap above, not as the mean.");
                sb.AppendLine();
            }

            if (delta < 0)
            {
                // A negative delta is a collection, not "the game allocated nothing". It also means the
                // figure is a NET figure over a window that contains a GC, so it cannot be divided by the
                // frame count and quoted as an allocation rate — that number is not a rate at all.
                sb.AppendLine("  NOTE: the delta is NEGATIVE, so at least one garbage collection ran inside the");
                sb.AppendLine("  window and the figure above is a net, not a rate. Read the max frame above: a");
                sb.AppendLine("  single large hitch with a modest mean is the GC, and it is not the sim.");
                sb.AppendLine();
            }

            if (droppedAtEnd > droppedAtStart)
            {
                sb.AppendLine("  NOTE: dropped ticks are non-zero, so the sim hit the catch-up cap");
                sb.AppendLine("  (SimClock.MaxCatchupTicks). Frame time is ~5 x one tick, and the fix is");
                sb.AppendLine("  inside a tick — read the table below, not the renderer.");
                sb.AppendLine();
            }
        }

        /// <summary>
        /// What the two numbers above are supposed to mean together. Written out because the table is
        /// easy to misread in the one direction that costs a week: a wrapper looks expensive.
        /// </summary>
        private static void AppendReadingGuide(StringBuilder sb)
        {
            sb.AppendLine("-- how to read the table --");
            sb.AppendLine("  * 'total' is the WHOLE SimTick span of every instance of that type, so it");
            sb.AppendLine("    includes anything nested inside it. A type whose cost is covered by a");
            sb.AppendLine("    PfeProfiler child region is a wrapper, not a hotspot — cross-read the");
            sb.AppendLine("    regions dump written beside this file.");
            sb.AppendLine("  * 'avg' is one call = one instance for one tick. Compare it against");
            sb.AppendLine("    'inst': 27 units at 0.30 ms each and 1 unit at 8 ms read very differently.");
            sb.AppendLine("  * 'max' is the worst single call. A large max with a small avg is a spike,");
            sb.AppendLine("    usually an allocation or a first-touch — a different fix from a flat cost.");
            sb.AppendLine();
            sb.AppendLine("-- A/B --");
            sb.AppendLine("  Run this twice (once on each commit / with each toggle), then compare the");
            sb.AppendLine("  two .csv files: frame_ms median and p95 are the verdict, and sim_share says");
            sb.AppendLine("  whether the sim or the render side moved. Same room, same machine, same");
            sb.AppendLine("  build type — otherwise the comparison means nothing.");
            sb.AppendLine();
        }

        // ── Output ─────────────────────────────────────────────────────────────────────

        private static string WriteReport(string report, double[] frameMs, double[] simMs, int[] ticksPerFrame, int count)
        {
            try
            {
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProfilerCaptures"));
                Directory.CreateDirectory(dir);

                string stamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                string baseName = "perf_playmode_" + stamp;

                string textPath = Path.Combine(dir, baseName + ".txt");
                File.WriteAllText(textPath, report);

                var csv = new StringBuilder();
                csv.AppendLine("frame,frame_ms,sim_ms,ticks,sim_share");
                for (int i = 0; i < count; i++)
                {
                    double share = frameMs[i] > 1e-6 ? simMs[i] / frameMs[i] : 0.0;
                    csv.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
                       .Append(frameMs[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                       .Append(simMs[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                       .Append(ticksPerFrame[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                       .Append(share.ToString("F4", CultureInfo.InvariantCulture)).AppendLine();
                }

                File.WriteAllText(Path.Combine(dir, baseName + ".csv"), csv.ToString());

                return textPath;
            }
            catch (System.Exception e)
            {
                // A report that could not be written is a finding, not a crash: the test's assertions
                // are on the instrument and they still ran.
                Debug.LogWarning($"[perf] could not write the report: {e.Message}");
                return "(write failed)";
            }
        }

        // ── Plumbing ───────────────────────────────────────────────────────────────────

        private static IEnumerator LoadGameScene()
        {
            if (SceneManager.GetActiveScene().name != SceneName)
            {
                AsyncOperation load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
                Assert.IsNotNull(load,
                    $"[perf] could not load '{SceneName}'. Is it still enabled in Build Settings?");

                while (!load.isDone)
                {
                    yield return null;
                }
            }

            // One frame so Awake/Start (and VContainer's build) have run before anyone looks for them.
            yield return null;
        }

        /// <summary>
        /// Bounded wait. Every wait in this fixture goes through here so a boot that never finishes
        /// fails with the condition that never became true, instead of hanging the Test Runner until
        /// the timeout attribute fires with no explanation.
        /// </summary>
        private static IEnumerator WaitFor(System.Func<bool> condition, float timeoutSeconds, string what)
        {
            float start = UnityEngine.Time.realtimeSinceStartup;
            int frames = 0;

            while (!condition())
            {
                if (UnityEngine.Time.realtimeSinceStartup - start > timeoutSeconds)
                {
                    Assert.Fail(
                        $"[perf] timed out after {timeoutSeconds:0} s waiting for {what}. " +
                        $"{frames} frames elapsed. If the game boots but this condition never holds, " +
                        "the predicate is wrong — do not raise the timeout.");
                }

                frames++;
                yield return null;
            }
        }

        private static double Percentile(double[] sorted, int count, double fraction)
        {
            if (count <= 0) return 0.0;

            int index = (int)System.Math.Round(fraction * (count - 1));
            if (index < 0) index = 0;
            if (index >= count) index = count - 1;
            return sorted[index];
        }
    }
}
