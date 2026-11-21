// Priority-0 per-frame render & GC recorder (headless, Development player only).
//
// WHY THIS EXISTS
// PfeProfiler accumulates since reset and PfeProfilerAutoDump only snapshots boot frames 5 and 60,
// so the steady-state frame had no instrument at all: the 72 view-path Update/LateUpdate methods,
// camera rendering and GC spikes were invisible. ProfilerRecorderSmokeTest proved the two facts
// this recorder is built on, measured 2026-09-28 in a standalone headless Development player:
//   1. ProfilerRecorder(SumAllSamplesInFrame | CollectOnlyOnCurrentThread) reads custom markers,
//      "Main Thread" and "GC.Alloc" with NO Profiler session attached — but CurrentValue is
//      CUMULATIVE from StartNew, so per-frame cost is the FIRST DIFFERENCE of the series.
//   2. Per-frame call counts do NOT come through (capacity-1 sample Count is 0 mid-frame and
//      after the boundary alike), so this recorder tracks bytes and milliseconds only.
//
// WHAT IT RECORDS (one CSV row per frame)
//   cpuMainThread_ms  — first difference of the internal "Main Thread" counter (CPU main-thread
//                       time of the PREVIOUS completed frame — sampled at the top of Update).
//   gcAlloc_bytes     — first difference of the internal "GC.Alloc" counter.
//   batches/setPass   — first difference of the Render counters (SRP batcher health readout;
//                       a ~1:1 batches-per-renderer ratio means the SRP batcher is proliferating
//                       renderers, see the guide's anti-pattern 3).
//   simFrame/simTick  — first difference of PfeProfiler's cumulative sim.frame / sim.tick region
//                       totals (ProfilerRecorder cannot see Stopwatch regions; TryGetRegionTotals
//                       exists for exactly this sampler).
//   ticksDelta        — TickIndex difference from SimLoop.Metrics; == ticks that ran last frame.
//                       Mean ticks/frame approaching SimClock.MaxCatchupTicks is the catch-up
//                       spiral the guide's triage rule is about.
//   view markers      — per-frame deltas of PfeViewMarkers scopes (room visuals LateUpdate,
//                       object visuals, fog mask, camera, HUD, NPC animator FixedUpdate).
//   renderers         — live Renderer count, sampled every 60th frame (the query itself costs
//                       milliseconds; doing it every frame would put the harness inside the
//                       measurement). Rows without a sample carry the previous value.
//
// RUNS
//   Alone:   PFE_Unity.exe -batchmode -logFile run.log -pfe-frameseries \
//              -pfe-frameseries-tag base -pfe-frameseries-warmup 120 -pfe-frameseries-frames 600
//   Companion (with the scenario harness driving input): just add -pfe-scenario ... — the
//   recorder adopts the scenario's warmup/frames/tag and defers process exit to the scenario
//   test, so one run produces the movement verdict AND the frame series.
//   Boot into gameplay headless with -pfe-autostart (MainMenuSceneBootstrapper).
//
// OUTPUT (same convention as PfeProfiler)
//   ProfilerCaptures/frames_<tag>_<stamp>.csv  one row per frame, all timing columns are deltas
//   ProfilerCaptures/frames_<tag>_<stamp>.txt  triage verdict + P50/P95/P99 + spike flags
//
// A HARD WATCHDOG, same pattern as PfeScenarioTest/ProfilerRecorderSmokeTest: _updates increments
// OUTSIDE every try block and forces the exit, because a throw inside Update otherwise skips the
// frame counter and the player has to be killed by hand (that happened, twice, 2026-09-28).

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using VContainer;

namespace PFE.Core.Profiling
{
    public sealed class PfeFrameSeriesRecorder : MonoBehaviour
    {
        // ── Command line ─────────────────────────────────────────────────────────────

        const string EnableArg = "-pfe-frameseries";
        const string ScenarioArg = "-pfe-scenario";          // companion mode trigger
        const string FramesArg = "-pfe-frameseries-frames";
        const string WarmupArg = "-pfe-frameseries-warmup";
        const string TagArg = "-pfe-frameseries-tag";
        const string ScenarioFramesArg = "-pfe-scenario-frames";
        const string ScenarioWarmupArg = "-pfe-scenario-warmup";
        const string ScenarioTagArg = "-pfe-scenario-tag";

        const int DefaultFrames = 600;
        const int DefaultWarmup = 120;
        const int MaxFrames = 20000;
        const int RendererSampleInterval = 60;

        // ── Recorder set ─────────────────────────────────────────────────────────────
        // All capacity 1: only CurrentValue (cumulative) is ever read, and only the first
        // difference of that series is meaningful. The smoke test proved per-frame sample Counts
        // unusable, so no wide recorder is kept.

        const ProfilerRecorderOptions Opts =
            ProfilerRecorderOptions.SumAllSamplesInFrame |
            ProfilerRecorderOptions.CollectOnlyOnCurrentThread;

        ProfilerRecorder _mainThreadRec;   // ProfilerCategory.Internal, "Main Thread"
        ProfilerRecorder _gcAllocRec;      // ProfilerCategory.Internal, "GC.Alloc"
        ProfilerRecorder _batchesRec;      // ProfilerCategory.Render, "Batches"
        ProfilerRecorder _setPassRec;      // ProfilerCategory.Render, "SetPass Calls"
        ProfilerRecorder _roomVisualsRec;
        ProfilerRecorder _objectVisualsRec;
        ProfilerRecorder _fogMaskRec;
        ProfilerRecorder _cameraRec;
        ProfilerRecorder _hudRec;
        ProfilerRecorder _npcAnimRec;

        // ── State ────────────────────────────────────────────────────────────────────

        SimLoop _simLoop;
        bool _tickCostsEnabled;

        int _measuredFrames;
        int _warmupFrames;
        int _totalFrames;
        string _tag;
        bool _ownsProcessExit;               // false in scenario-companion mode
        bool _scenarioPresent;

        // Raw CUMULATIVE series (first-differenced in the report) — preallocated so the per-frame
        // path allocates nothing; otherwise the harness would own part of the GC column.
        double[] _cpuMainMs;
        double[] _gcAllocBytes;
        double[] _batches;
        double[] _setPass;
        double[] _simFrameMs;
        double[] _simTickMs;
        double[] _simTickSelfMs;
        double[] _viewRoomVisualsNs;
        double[] _viewObjectVisualsNs;
        double[] _viewFogMaskNs;
        double[] _viewCameraNs;
        double[] _viewHudNs;
        double[] _viewNpcAnimNs;
        int[] _tickIndex;
        int[] _droppedTicks;                 // cumulative counter as seen that frame
        int[] _tickableCount;
        int[] _renderers;
        int[] _unityFrame;
        int[] _phase;                        // 0 settle / 1 hold / 2 release (scenario mode only)

        // FrameTimingManager per-frame values (NOT cumulative — one struct per completed frame).
        // Authoritative CPU numbers for this recorder: the "Main Thread" ProfilerRecorder came
        // back flat-zero in the first baseline run (all 2400 frames, valid=true, value constant),
        // while FrameTimingManager was enabled and is a second independent source (the smoke test
        // already proved it produces numbers headless). Both are recorded so a disagreement is
        // visible instead of silently choosing one.
        double[] _ftCpuMainMs;
        double[] _ftCpuFrameMs;
        double[] _ftGpuMs;
        FrameTiming[] _timings;

        int _frame;
        // Incremented OUTSIDE every try block — the watchdog depends on it, so nothing may skip it.
        int _updates;
        int _exceptions;
        string _lastException = "(none)";
        bool _finished;
        bool _frameTimingEnabled;
        readonly System.Diagnostics.Stopwatch _wall = new System.Diagnostics.Stopwatch();

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ── Entry hook ───────────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            bool frameseries = HasArg(args, EnableArg);
            bool scenario = HasArg(args, ScenarioArg);
            if (!frameseries && !scenario) return;

            var go = new GameObject("[PfeFrameSeriesRecorder]");
            Object.DontDestroyOnLoad(go);
            var rec = go.AddComponent<PfeFrameSeriesRecorder>();
            rec.Configure(args, frameseries, scenario);
        }

        static bool HasArg(string[] args, string name)
        {
            foreach (string a in args)
            {
                if (string.Equals(a, name, System.StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static int IntArg(string[] args, string name, int fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], name, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int value))
                {
                    return value;
                }
            }
            return fallback;
        }

        static string StringArg(string[] args, string name, string fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return fallback;
        }

        void Configure(string[] args, bool frameseries, bool scenario)
        {
            _scenarioPresent = scenario;
            // Companion mode adopts the scenario's budget so both finish on the same frame and the
            // CSV aligns row-for-row with scenario_<tag>.csv; standalone mode uses its own args.
            int framesFallback = scenario ? IntArg(args, ScenarioFramesArg, 180) : DefaultFrames;
            int warmupFallback = scenario ? IntArg(args, ScenarioWarmupArg, 60) : DefaultWarmup;
            string tagFallback = scenario ? StringArg(args, ScenarioTagArg, "scenario") : "frames";

            _measuredFrames = Mathf.Clamp(IntArg(args, FramesArg, framesFallback), 1, MaxFrames);
            _warmupFrames = Mathf.Clamp(IntArg(args, WarmupArg, warmupFallback), 0, MaxFrames);
            _tag = Sanitize(StringArg(args, TagArg, tagFallback));

            // Who quits: the scenario harness owns process exit when present (it may run longer
            // watchdog tails); standalone, this recorder must quit itself or run forever.
            _ownsProcessExit = !scenario || frameseries;
            if (scenario && frameseries) _ownsProcessExit = false; // both present: scenario quits

            _totalFrames = _warmupFrames + _measuredFrames;

            _cpuMainMs = new double[_totalFrames];
            _gcAllocBytes = new double[_totalFrames];
            _batches = new double[_totalFrames];
            _setPass = new double[_totalFrames];
            _simFrameMs = new double[_totalFrames];
            _simTickMs = new double[_totalFrames];
            _simTickSelfMs = new double[_totalFrames];
            _viewRoomVisualsNs = new double[_totalFrames];
            _viewObjectVisualsNs = new double[_totalFrames];
            _viewFogMaskNs = new double[_totalFrames];
            _viewCameraNs = new double[_totalFrames];
            _viewHudNs = new double[_totalFrames];
            _viewNpcAnimNs = new double[_totalFrames];
            _tickIndex = new int[_totalFrames];
            _droppedTicks = new int[_totalFrames];
            _tickableCount = new int[_totalFrames];
            _renderers = new int[_totalFrames];
            _unityFrame = new int[_totalFrames];
            _phase = new int[_totalFrames];
            _ftCpuMainMs = new double[_totalFrames];
            _ftCpuFrameMs = new double[_totalFrames];
            _ftGpuMs = new double[_totalFrames];
            _timings = new FrameTiming[1];

            _mainThreadRec = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1, Opts);
            _gcAllocRec = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Opts);
            _batchesRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches", 1, Opts);
            _setPassRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls", 1, Opts);
            _roomVisualsRec = ProfilerRecorder.StartNew(PfeViewMarkers.RoomVisualsLateUpdate, 1, Opts);
            _objectVisualsRec = ProfilerRecorder.StartNew(PfeViewMarkers.ObjectVisuals, 1, Opts);
            _fogMaskRec = ProfilerRecorder.StartNew(PfeViewMarkers.FogVisibilityMask, 1, Opts);
            _cameraRec = ProfilerRecorder.StartNew(PfeViewMarkers.CameraLateUpdate, 1, Opts);
            _hudRec = ProfilerRecorder.StartNew(PfeViewMarkers.HudUpdate, 1, Opts);
            _npcAnimRec = ProfilerRecorder.StartNew(PfeViewMarkers.NpcAnimatorFixedUpdate, 1, Opts);

            // Tests must not make noise (same reasoning as PfeScenarioTest).
            AudioListener.volume = 0f;
            AudioListener.pause = true;

            _wall.Start();
            _frameTimingEnabled = FrameTimingManager.IsFeatureEnabled();

            Debug.Log($"[FrameSeries] start tag={_tag} warmup={_warmupFrames} frames={_measuredFrames} " +
                      $"scenarioCompanion={_scenarioPresent} ownsExit={_ownsProcessExit} " +
                      $"isDebugBuild={Debug.isDebugBuild} frameTiming={_frameTimingEnabled}");
        }

        // ── Per-frame ────────────────────────────────────────────────────────────────

        void Update()
        {
            if (_finished) return;
            if (_cpuMainMs == null) return;

            _updates++;

            // HARD WATCHDOG — see the header. Nothing may skip this increment or this check.
            if (_updates > _totalFrames + 120)
            {
                Finish();
                return;
            }

            try
            {
                Tick();
            }
            catch (System.Exception e)
            {
                _exceptions++;
                _lastException = e.GetType().Name + ": " + e.Message;
                if (_exceptions <= 3)
                {
                    Debug.LogError($"[FrameSeries] tick threw (frame {_frame}): {_lastException}");
                }
            }
        }

        void Tick()
        {
            if (_frame >= _totalFrames)
            {
                Finish();
                return;
            }

            // All samples are read at the TOP of Update: every recorder's CurrentValue then covers
            // exactly the previously COMPLETED frame, so the first difference computed in the
            // report attributes last frame's cost to last frame's row. Reading later in the frame
            // would mix "frame N-1 complete" (render) with "frame N partial" (scripts) per recorder.
            _cpuMainMs[_frame] = _mainThreadRec.CurrentValue * 1e-6;           // ns -> ms
            _gcAllocBytes[_frame] = _gcAllocRec.CurrentValue;
            _batches[_frame] = _batchesRec.CurrentValue;
            _setPass[_frame] = _setPassRec.CurrentValue;
            _viewRoomVisualsNs[_frame] = _roomVisualsRec.CurrentValue;
            _viewObjectVisualsNs[_frame] = _objectVisualsRec.CurrentValue;
            _viewFogMaskNs[_frame] = _fogMaskRec.CurrentValue;
            _viewCameraNs[_frame] = _cameraRec.CurrentValue;
            _viewHudNs[_frame] = _hudRec.CurrentValue;
            _viewNpcAnimNs[_frame] = _npcAnimRec.CurrentValue;

            if (PfeProfiler.TryGetRegionTotals("sim.frame", out _, out double simFrameTotal, out _))
            {
                _simFrameMs[_frame] = simFrameTotal;
            }
            if (PfeProfiler.TryGetRegionTotals("sim.tick", out _, out double simTickTotal, out double simTickSelf))
            {
                _simTickMs[_frame] = simTickTotal;
                _simTickSelfMs[_frame] = simTickSelf;
            }

            ResolveSimLoop();
            if (_simLoop != null)
            {
                SimMetrics m = _simLoop.Metrics;
                _tickIndex[_frame] = m.TickIndex;
                _droppedTicks[_frame] = m.DroppedTicks;
                _tickableCount[_frame] = m.TickableCount;
            }

            if (_frame % RendererSampleInterval == 0)
            {
                // Expensive (a full scene walk) — that is why it is every 60th frame, and why the
                // column carries the previous value everywhere else. The walk lands INSIDE this
                // frame's cpuMainThread number; the CSV's sampledRenderer=1 flag makes those rows
                // identifiable rather than letting them masquerade as game spikes.
                _renderers[_frame] = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length;
            }
            else if (_frame > 0)
            {
                _renderers[_frame] = _renderers[_frame - 1];
            }

            // Fully qualified on purpose: `namespace PFE.Core.Time` exists (see PfeScenarioTest).
            _unityFrame[_frame] = UnityEngine.Time.frameCount;

            if (_frameTimingEnabled)
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
                {
                    _ftCpuMainMs[_frame] = _timings[0].cpuMainThreadFrameTime;
                    _ftCpuFrameMs[_frame] = _timings[0].cpuFrameTime;
                    _ftGpuMs[_frame] = _timings[0].gpuFrameTime;
                }
            }
            _phase[_frame] = _frame < _warmupFrames
                ? 0
                : (_frame < _warmupFrames + _measuredFrames / 2 ? 1 : 2);

            _frame++;
        }

        /// <summary>
        /// Lazy, and retried every frame until it succeeds: at AfterSceneLoad the VContainer scope
        /// may not be built yet. A null here is a timing artefact, not a wiring fault.
        ///
        /// On first resolve the per-tickable cost table is switched on: sim.tick's ~3.7 ms self
        /// time is the biggest unexplained number in the Oct-28 dump and the table is the only
        /// instrument that can say which tickable owns it. Two Stopwatch reads per tickable per
        /// tick is noise against a multi-millisecond tick.
        /// </summary>
        void ResolveSimLoop()
        {
            if (_simLoop != null || _tickCostsEnabled) return;

            GameLifetimeScope scope = Object.FindFirstObjectByType<GameLifetimeScope>();
            if (scope == null || scope.Container == null) return;
            if (!scope.Container.TryResolve(out SimLoop loop)) return;

            _simLoop = loop;
            _simLoop.TickableCosts.Enabled = true;
            _tickCostsEnabled = true;
            Debug.Log("[FrameSeries] SimLoop resolved; TickableCosts.Enabled=true");
        }

        // ── Report ───────────────────────────────────────────────────────────────────

        void Finish()
        {
            if (_finished) return;
            _finished = true;
            _wall.Stop();

            // Snapshot recorder validity BEFORE Dispose — Valid is false after disposal, which
            // made the first smoke run's diagnostic useless (documented in ProfilerRecorderSmokeTest).
            string recorderDiag =
                $"mainThread={_mainThreadRec.Valid} gcAlloc={_gcAllocRec.Valid} " +
                $"batches={_batchesRec.Valid} setPass={_setPassRec.Valid} " +
                $"roomVisuals={_roomVisualsRec.Valid} fogMask={_fogMaskRec.Valid}";

            _mainThreadRec.Dispose();
            _gcAllocRec.Dispose();
            _batchesRec.Dispose();
            _setPassRec.Dispose();
            _roomVisualsRec.Dispose();
            _objectVisualsRec.Dispose();
            _fogMaskRec.Dispose();
            _cameraRec.Dispose();
            _hudRec.Dispose();
            _npcAnimRec.Dispose();

            string dir = DumpDirectory();
            string stamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Inv);
            string baseName = "frames_" + _tag + "_" + stamp;

            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, baseName + ".csv"), BuildCsv());
                string summary = BuildSummary(recorderDiag);
                File.WriteAllText(Path.Combine(dir, baseName + ".txt"), summary);
                Debug.Log(summary);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[FrameSeries] could not write report: {e.Message}");
            }

            if (_ownsProcessExit)
            {
                // A player has no command-line way to exit itself (-quit is Editor-only).
                Application.Quit();
            }
        }

        string BuildCsv()
        {
            double[] cpu = DeltasMs(_cpuMainMs);
            double[] gc = Deltas(_gcAllocBytes);
            double[] bat = Deltas(_batches);
            double[] sp = Deltas(_setPass);
            double[] simFrame = Deltas(_simFrameMs);
            double[] simTick = Deltas(_simTickMs);
            double[] simTickSelf = Deltas(_simTickSelfMs);
            double[] roomVis = DeltasNsToMs(_viewRoomVisualsNs);
            double[] objVis = DeltasNsToMs(_viewObjectVisualsNs);
            double[] fog = DeltasNsToMs(_viewFogMaskNs);
            double[] cam = DeltasNsToMs(_viewCameraNs);
            double[] hud = DeltasNsToMs(_viewHudNs);
            double[] npc = DeltasNsToMs(_viewNpcAnimNs);

            var sb = new StringBuilder(_totalFrames * 96);
            sb.AppendLine("frame,unityFrame,phase,sampledRenderer,cpuMainThread_ms,ftCpuMain_ms,ftCpuFrame_ms,ftGpu_ms,gcAlloc_bytes," +
                          "batches,setPassCalls,simFrame_ms,simTick_ms,simTickSelf_ms," +
                          "viewRoomVisualsLate_ms,viewObjectVisuals_ms,viewFogMask_ms," +
                          "viewCameraLate_ms,viewHud_ms,viewNpcAnim_ms," +
                          "ticksDelta,tickIndex,droppedTicks,tickableCount,renderers");
            for (int i = 0; i < _frame; i++)
            {
                int ticksDelta = i > 0 ? _tickIndex[i] - _tickIndex[i - 1] : 0;
                bool sampledRenderer = i % RendererSampleInterval == 0;
                sb.Append(i.ToString(Inv)).Append(',')
                  .Append(_unityFrame[i].ToString(Inv)).Append(',')
                  .Append(_phase[i].ToString(Inv)).Append(',')
                  .Append(sampledRenderer ? '1' : '0').Append(',')
                  .Append(cpu[i].ToString("F3", Inv)).Append(',')
                  .Append(_ftCpuMainMs[i].ToString("F3", Inv)).Append(',')
                  .Append(_ftCpuFrameMs[i].ToString("F3", Inv)).Append(',')
                  .Append(_ftGpuMs[i].ToString("F3", Inv)).Append(',')
                  .Append(gc[i].ToString("F0", Inv)).Append(',')
                  .Append(bat[i].ToString("F0", Inv)).Append(',')
                  .Append(sp[i].ToString("F0", Inv)).Append(',')
                  .Append(simFrame[i].ToString("F3", Inv)).Append(',')
                  .Append(simTick[i].ToString("F3", Inv)).Append(',')
                  .Append(simTickSelf[i].ToString("F3", Inv)).Append(',')
                  .Append(roomVis[i].ToString("F3", Inv)).Append(',')
                  .Append(objVis[i].ToString("F3", Inv)).Append(',')
                  .Append(fog[i].ToString("F3", Inv)).Append(',')
                  .Append(cam[i].ToString("F3", Inv)).Append(',')
                  .Append(hud[i].ToString("F3", Inv)).Append(',')
                  .Append(npc[i].ToString("F3", Inv)).Append(',')
                  .Append(ticksDelta.ToString(Inv)).Append(',')
                  .Append(_tickIndex[i].ToString(Inv)).Append(',')
                  .Append(_droppedTicks[i].ToString(Inv)).Append(',')
                  .Append(_tickableCount[i].ToString(Inv)).Append(',')
                  .Append(_renderers[i].ToString(Inv)).Append('\n');
            }
            return sb.ToString();
        }

        string BuildSummary(string recorderDiag)
        {
            var lines = new List<string>();
            lines.Add("===== PFE FRAME SERIES =====");
            lines.Add($"tag                : {_tag}");
            lines.Add($"isDebugBuild       : {Debug.isDebugBuild}   (False makes every custom marker a silent zero)");
            lines.Add($"unityVersion       : {Application.unityVersion}");
            lines.Add($"batchmode          : {Application.isBatchMode}");
            lines.Add($"warmup / measured  : {_warmupFrames} / {_measuredFrames}");
            lines.Add($"frames recorded    : {_frame}");
            lines.Add($"ticks / exceptions : {_updates} / {_exceptions}   last={_lastException}");
            lines.Add($"probe wall time    : {_wall.Elapsed.TotalSeconds:F2} s for {_updates} ticks " +
                      $"({(_updates > 0 ? _updates / System.Math.Max(_wall.Elapsed.TotalSeconds, 1e-6) : 0):F1} fps)");
            lines.Add($"recorders valid    : {recorderDiag}   (False = counter name unknown this build)");
            lines.Add($"frameTimingManager : {_frameTimingEnabled}");
            lines.Add("");

            if (!Debug.isDebugBuild)
            {
                lines.Add("*** RELEASE BUILD DETECTED ***");
                lines.Add("ProfilerMarker.Begin/End are [Conditional] and compiled away outside a");
                lines.Add("Development Build. Every custom marker column is ZERO and this run proves");
                lines.Add("NOTHING. Rebuild with Tools/PFE/Build Development Player.");
                lines.Add("");
            }

            double[] cpu = DeltasMs(_cpuMainMs);
            double[] simFrame = Deltas(_simFrameMs);
            double[] simTick = Deltas(_simTickMs);
            double[] simTickSelf = Deltas(_simTickSelfMs);

            // The authoritative per-frame CPU number. The "Main Thread" ProfilerRecorder read
            // flat-zero through the whole first baseline run (valid but constant), so when its
            // delta series is dead flat and FrameTiming has data, FrameTiming's
            // cpuMainThreadFrameTime is used instead — and the substitution is printed, because
            // a silently swapped instrument is how wrong numbers become folklore.
            bool mainThreadFlat = Max(cpu) < 0.01;
            double[] cpuUse = mainThreadFlat && Max(_ftCpuMainMs) > 0.0 ? _ftCpuMainMs : cpu;
            string cpuSource = ReferenceEquals(cpuUse, cpu) ? "ProfilerRecorder Main Thread" : "FrameTiming cpuMainThreadFrameTime (Main Thread recorder was flat-zero)";

            // ── Cardinal triage (guide §1): sim saturation + sim vs render share ──
            lines.Add("--- triage: sim vs render ---");
            double meanTicks = MeanDelta(_tickIndex);
            lines.Add($"ticks/frame (mean) : {meanTicks:F2}   (MaxCatchupTicks={SimClock.MaxCatchupTicks}; " +
                      $"approaching it = catch-up spiral)");
            lines.Add($"droppedTicks (cum) : {(MeanDelta(_droppedTicks) > 0 ? "INCREASING — spiral guard firing" : "0 (steady)")}");

            double meanCpu = Mean(cpuUse);
            double meanSimFrame = Mean(simFrame);
            double meanSimTick = Mean(simTick);
            double meanRender = meanCpu - meanSimFrame;
            lines.Add($"cpu source           : {cpuSource}");
            lines.Add($"cpu main (ms/frame): mean={meanCpu:F2}  p95={Percentile(cpuUse, 0.95):F2}  max={Max(cpuUse):F2}");
            lines.Add($"sim frame (ms)     : mean={meanSimFrame:F2}   sim.tick inside it: mean={meanSimTick:F2} " +
                      $"(self {Mean(simTickSelf):F2})");
            lines.Add($"render+rest (ms)   : mean={meanRender:F2}   = cpu main - sim frame");
            string owner = meanSimFrame > 8.0
                ? "SIM dominates (>8 ms/frame) — drill into sim.tick: TickableCostTable below"
                : meanRender > meanSimFrame
                    ? "RENDER/VIEW dominates — drill into the view marker columns below"
                    : "balanced — both sides cheap; check spikes, not means";
            lines.Add($"verdict            : {owner}");
            lines.Add("");

            // ── Percentiles for every delta column ──
            lines.Add("--- measured-window percentiles (warmup excluded) ---");
            lines.Add("column                       n   nonzero      p50        p95        p99        max");
            AppendPercentiles(lines, "cpuMainThread_ms", cpu);
            AppendPercentiles(lines, "ftCpuMain_ms", _ftCpuMainMs);
            AppendPercentiles(lines, "ftCpuFrame_ms", _ftCpuFrameMs);
            AppendPercentiles(lines, "ftGpu_ms", _ftGpuMs);
            AppendPercentiles(lines, "gcAlloc_bytes", Deltas(_gcAllocBytes));
            AppendPercentiles(lines, "batches", Deltas(_batches));
            AppendPercentiles(lines, "setPassCalls", Deltas(_setPass));
            AppendPercentiles(lines, "simFrame_ms", simFrame);
            AppendPercentiles(lines, "simTick_ms", simTick);
            AppendPercentiles(lines, "simTickSelf_ms", simTickSelf);
            AppendPercentiles(lines, "viewRoomVisualsLate_ms", DeltasNsToMs(_viewRoomVisualsNs));
            AppendPercentiles(lines, "viewObjectVisuals_ms", DeltasNsToMs(_viewObjectVisualsNs));
            AppendPercentiles(lines, "viewFogMask_ms", DeltasNsToMs(_viewFogMaskNs));
            AppendPercentiles(lines, "viewCameraLate_ms", DeltasNsToMs(_viewCameraNs));
            AppendPercentiles(lines, "viewHud_ms", DeltasNsToMs(_viewHudNs));
            AppendPercentiles(lines, "viewNpcAnim_ms", DeltasNsToMs(_viewNpcAnimNs));
            AppendPercentiles(lines, "ticksDelta", IntDeltas(_tickIndex));
            lines.Add("");

            // ── Spike flags ──
            lines.Add("--- spike flags ---");
            int slowFrames = 0, gcFrames = 0, maxTickFrames = 0;
            double gcTotal = 0, gcPeak = 0;
            int gcPeakFrame = -1;
            double[] gcDelta = Deltas(_gcAllocBytes);
            double[] ticksDeltaSeries = IntDeltas(_tickIndex);
            for (int i = _warmupFrames; i < _frame; i++)
            {
                if (cpuUse[i] > 16.6) slowFrames++;
                double g = gcDelta[i];
                if (g > 0)
                {
                    gcFrames++;
                    gcTotal += g;
                    if (g > gcPeak) { gcPeak = g; gcPeakFrame = i; }
                }
                if (ticksDeltaSeries[i] >= SimClock.MaxCatchupTicks) maxTickFrames++;
            }
            lines.Add($"frames cpuMainThread>16.6ms : {slowFrames}/{_frame - _warmupFrames}");
            lines.Add($"frames with GC.Alloc > 0    : {gcFrames}   total={gcTotal / 1024.0:F1} KiB " +
                      $"peak={gcPeak / 1024.0:F1} KiB at frame {gcPeakFrame}");
            lines.Add($"frames at MaxCatchupTicks   : {maxTickFrames}");
            int rendererLast = _frame > 0 ? _renderers[_frame - 1] : 0;
            lines.Add($"renderers (last sample)     : {rendererLast}   " +
                      $"(sampled every {RendererSampleInterval}th frame; those rows' cpu includes the walk)");
            lines.Add("");

            // ── Reconciliation: how much of the mean frame is attributed ──
            double viewSum =
                Mean(DeltasNsToMs(_viewRoomVisualsNs)) +
                Mean(DeltasNsToMs(_viewCameraNs)) +
                Mean(DeltasNsToMs(_viewHudNs));
            double accounted = meanSimFrame + viewSum;
            lines.Add("--- reconciliation (means, ms/frame) ---");
            lines.Add($"sim.frame {meanSimFrame:F2} + roomVisuals/camera/hud {viewSum:F2} " +
                      $"= {accounted:F2} of cpu main {meanCpu:F2} " +
                      $"→ unattributed {meanCpu - accounted:F2} (render submission, other Update()s, engine)");
            lines.Add("");

            // ── Tickable cost table: what inside sim.tick owns the time ──
            if (_simLoop != null)
            {
                List<TickableCostTable.Entry> rows = _simLoop.TickableCosts.Snapshot();
                lines.Add("--- tickables (cumulative since TickableCosts enabled; whole run incl. warmup) ---");
                lines.Add("type                                inst   calls      total        avg        max");
                int shown = 0;
                foreach (TickableCostTable.Entry e in rows)
                {
                    if (shown >= 15) break;
                    if (e.TotalMs < 0.5) break;
                    lines.Add("  " + e.Name.PadRight(34)
                        + e.Instances.ToString(Inv).PadLeft(5)
                        + e.Calls.ToString(Inv).PadLeft(8)
                        + e.TotalMs.ToString("F1", Inv).PadLeft(11)
                        + e.AverageMs.ToString("F3", Inv).PadLeft(11)
                        + e.MaxMs.ToString("F3", Inv).PadLeft(11));
                    shown++;
                }
                lines.Add($"tickable count: {_simLoop.TickableCount}   " +
                          $"invalidatedSinceReset: {_simLoop.TickableCosts.InvalidatedSinceReset}");
            }
            else
            {
                lines.Add("--- tickables: SimLoop never resolved — no per-tickable data ---");
            }
            lines.Add("");

            lines.Add("read the CSV deltas, not the raw columns: CurrentValue is cumulative from");
            lines.Add("recorder start, so only the first difference is a per-frame cost.");
            lines.Add("");

            // The region table accumulated since boot covers the whole run INCLUDING one-shot
            // hitches the per-frame window missed (the 3.5 s room-transition frame of baseline2
            // is invisible to the percentile table above — one row in 1800). Appended verbatim so
            // every region's self time, call count and call site travel with this report.
            lines.Add("===== PfeProfiler region report (cumulative since boot) =====");
            try
            {
                lines.Add(PfeProfiler.BuildReport());
            }
            catch (System.Exception e)
            {
                lines.Add("(region report threw: " + e.Message + ")");
            }
            lines.Add("==============================");
            return string.Join("\n", lines);
        }

        // ── Statistics helpers (run once, in the report — allocation here is free) ───

        /// <summary>First difference of a cumulative series; index 0 is 0 (no predecessor).</summary>
        double[] Deltas(double[] series)
        {
            var d = new double[_totalFrames];
            for (int i = 1; i < _totalFrames; i++) d[i] = series[i] - series[i - 1];
            return d;
        }

        /// <summary>First difference where the raw series is stored in nanoseconds.</summary>
        double[] DeltasNsToMs(double[] seriesNs)
        {
            var d = new double[_totalFrames];
            for (int i = 1; i < _totalFrames; i++) d[i] = (seriesNs[i] - seriesNs[i - 1]) * 1e-6;
            return d;
        }

        /// <summary>The main-thread counter reads in ns; convert the differenced series to ms.</summary>
        double[] DeltasMs(double[] seriesNs)
        {
            var d = new double[_totalFrames];
            for (int i = 1; i < _totalFrames; i++) d[i] = (seriesNs[i] - seriesNs[i - 1]) * 1e-6;
            return d;
        }

        double[] IntDeltas(int[] series)
        {
            var d = new double[_totalFrames];
            for (int i = 1; i < _totalFrames; i++) d[i] = series[i] - series[i - 1];
            return d;
        }

        double Mean(double[] d)
        {
            int n = 0;
            double sum = 0;
            for (int i = _warmupFrames; i < _frame; i++) { sum += d[i]; n++; }
            return n > 0 ? sum / n : 0.0;
        }

        double MeanDelta(int[] series) => Mean(IntDeltas(series));

        double Max(double[] d)
        {
            double m = double.MinValue;
            for (int i = _warmupFrames; i < _frame; i++) if (d[i] > m) m = d[i];
            return m == double.MinValue ? 0.0 : m;
        }

        double Percentile(double[] series, double p)
        {
            var window = new List<double>(_frame - _warmupFrames);
            for (int i = _warmupFrames; i < _frame; i++) window.Add(series[i]);
            if (window.Count == 0) return 0.0;
            window.Sort();
            int idx = Mathf.Clamp((int)System.Math.Ceiling(p * window.Count) - 1, 0, window.Count - 1);
            return window[idx];
        }

        void AppendPercentiles(List<string> lines, string label, double[] series)
        {
            int n = _frame - _warmupFrames;
            if (n <= 0) return;
            int nonzero = 0;
            for (int i = _warmupFrames; i < _frame; i++) if (series[i] != 0.0) nonzero++;
            lines.Add(label.PadRight(26)
                + n.ToString(Inv).PadLeft(5)
                + nonzero.ToString(Inv).PadLeft(9)
                + Percentile(series, 0.50).ToString("F3", Inv).PadLeft(11)
                + Percentile(series, 0.95).ToString("F3", Inv).PadLeft(11)
                + Percentile(series, 0.99).ToString("F3", Inv).PadLeft(11)
                + Max(series).ToString("F3", Inv).PadLeft(11));
        }

        // ── Output location & hygiene ────────────────────────────────────────────────

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "frames";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            return sb.ToString();
        }

        static string DumpDirectory()
        {
#if UNITY_EDITOR
            return Path.Combine(Application.dataPath, "..", "ProfilerCaptures");
#else
            return Path.Combine(Application.persistentDataPath, "ProfilerCaptures");
#endif
        }
    }
}
