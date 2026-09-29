// Tier-0 smoke test for a headless per-frame profiling harness.
//
// WHY THIS EXISTS
// The whole harness design rests on one unproven assumption: that ProfilerRecorder can read a
// CUSTOM ProfilerMarker per frame in a Development Build with NO Profiler session attached. Unity's
// docs say ProfilerRecorder works in players; they do not say it works without Profiler.enabled.
// This file answers that in one run, and it deliberately includes a KNOWN-BAD input so a broken
// setup cannot masquerade as success.
//
// THE TRAP IT GUARDS AGAINST
// ProfilerMarker.Begin/End are [Conditional] and are compiled away in non-Development builds.
// A Release player therefore reports ZERO for every custom marker, silently, and looks exactly
// like "the recorder API is broken". This test records Debug.isDebugBuild into its report and
// shouts if it is false, so that case can never be mistaken for a real result.
//
// HOW TO RUN (a Development Build is REQUIRED - see above)
//   Unity.exe -batchmode -quit -projectPath <repo> -logFile build.log
//             -executeMethod HeadlessBuilder.BuildDevelopment
//   (BuildDevelopment is the Development-Build sibling of Build(); Build() alone produces a
//    RELEASE build, where every custom marker is compiled away and this test reports all zeros.)
//
//   PFE_Unity.exe -batchmode -logFile smoke.log -pfe-profilesmoke
//     optional: -pfe-profilesmoke-frames 120  -pfe-profilesmoke-warmup 30
//               -pfe-profilesmoke-tag run1   -pfe-profilesmoke-poison-at 60
//               -pfe-profilesmoke-poison-ms 50 -pfe-profilesmoke-poison-count 5
//
// Background and interpretation: docs/Perf_HeadlessProfileHarness_Feasibility.md
//
// A player has NO command-line way to exit itself (-quit is Editor-only, verified against the
// 6.3 offline PlayerCommandLineArguments page), so this component calls Application.Quit() when
// it is done. Without that, the harness would have to kill the process and could never be
// trusted to flush its own output.
//
// OUTPUT
//   <persistentDataPath>/ProfilerCaptures/smoke_<tag>_<stamp>.csv    one row per frame
//   <persistentDataPath>/ProfilerCaptures/smoke_<tag>_<stamp>.txt    verdict summary
// In the Editor those land in <repo>/ProfilerCaptures/ instead, matching PfeProfiler's convention.
//
// DELETE THIS FILE once the questions in the summary are answered - it is a probe, not a feature.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using Thread = System.Threading.Thread;

namespace PFE.Core.Profiling
{
    public sealed class ProfilerRecorderSmokeTest : MonoBehaviour
    {
        // ── Tunables (all overridable from the command line) ─────────────────────────

        const string EnableArg = "-pfe-profilesmoke";
        const string FramesArg = "-pfe-profilesmoke-frames";
        const string WarmupArg = "-pfe-profilesmoke-warmup";
        const string TagArg = "-pfe-profilesmoke-tag";
        const string PoisonAtArg = "-pfe-profilesmoke-poison-at";
        const string PoisonMsArg = "-pfe-profilesmoke-poison-ms";
        const string PoisonCountArg = "-pfe-profilesmoke-poison-count";

        const int DefaultFrames = 120;
        const int DefaultWarmup = 30;
        const int DefaultPoisonMs = 50;
        const int DefaultPoisonCount = 5;
        const int MaxFrames = 20000;

        // ── Markers: the names MUST be static strings so they form a fixed, small set ──
        // ProfilerCategory.Scripts is the category our own gameplay markers will live in.
        static readonly ProfilerMarker CleanMarker =
            new ProfilerMarker(ProfilerCategory.Scripts, "PFE.Smoke.Clean");

        static readonly ProfilerMarker PoisonedMarker =
            new ProfilerMarker(ProfilerCategory.Scripts, "PFE.Smoke.Poisoned");

        // ── State ────────────────────────────────────────────────────────────────────

        // Preallocated so the per-frame path does not allocate. If it did, the GC.Alloc column
        // would measure this test rather than the game, and would be worthless as a baseline.
        double[] _cleanNs;
        double[] _cleanLastNs;
        double[] _poisonNs;
        double[] _poisonLastNs;
        double[] _mainThreadNs;
        double[] _gcAllocBytes;
        int[] _cleanCalls;
        int[] _poisonCalls;
        int[] _gcAllocCalls;
        // Same measurement taken AFTER the frame boundary instead of mid-frame. SumAllSamplesInFrame
        // is documented as collapsing a frame's samples into one "per frame", so a mid-frame read
        // may legitimately see nothing yet. Both columns are kept so one run can tell them apart
        // rather than needing a rebuild to find out (a rebuild costs the owner a menu click).
        int[] _cleanCallsPrev;
        // recorder.Count itself, mid-frame. Distinguishes "Count == 0" (nothing collected yet)
        // from "a sample exists but its .Count is 0".
        int[] _cleanCount;
        // The index-0 control for the newest-vs-oldest question - see SampleCountOldest.
        int[] _cleanCallsOldest;
        // Count read from the wide-capacity recorder. If this is non-zero while the capacity-1
        // columns are zero, per-frame call counts simply need a wider buffer.
        int[] _cleanCallsWide;
        double[] _cpuFrameMs;
        double[] _gpuFrameMs;
        double[] _cpuMainMs;
        int[] _tickCounts;
        bool[] _poisoned;

        ProfilerRecorder _cleanRec;
        ProfilerRecorder _poisonRec;
        ProfilerRecorder _mainThreadRec;
        ProfilerRecorder _gcAllocRec;
        // Same marker as _cleanRec but with capacity = frame count, so the two configurations are
        // compared in ONE run instead of two. Capacity is the open question: Count grew to 151 on a
        // capacity-1 recorder, which contradicts the documented "Count < Capacity", and only a wide
        // buffer can show whether per-frame call counts need the extra storage.
        ProfilerRecorder _cleanRecWide;

        FrameTiming[] _timings;

        int _frame;
        // Counts every Update, including ones whose tick threw. The watchdog uses it to guarantee
        // the probe exits - see Update().
        int _updates;
        int _exceptions;
        string _lastException = "(none)";
        // The probe reports its own duration, so "how long does a test take" is answered by the
        // artefact rather than by timing the shell. Started in Configure, read in the report.
        readonly System.Diagnostics.Stopwatch _wall = new System.Diagnostics.Stopwatch();
        int _measuredFrames;
        int _warmupFrames;
        int _poisonAt;
        int _poisonMs;
        int _poisonCount;
        string _tag;
        bool _finished;
        bool _frameTimingEnabled;

        // ── Entry hook ───────────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            if (!HasArg(args, EnableArg)) return;

            var go = new GameObject("[ProfilerRecorderSmokeTest]");
            Object.DontDestroyOnLoad(go);
            var probe = go.AddComponent<ProfilerRecorderSmokeTest>();
            probe.Configure(args);
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

        void Configure(string[] args)
        {
            _measuredFrames = Mathf.Clamp(IntArg(args, FramesArg, DefaultFrames), 1, MaxFrames);
            _warmupFrames = Mathf.Clamp(IntArg(args, WarmupArg, DefaultWarmup), 0, MaxFrames);
            _poisonMs = Mathf.Clamp(IntArg(args, PoisonMsArg, DefaultPoisonMs), 0, 5000);
            _poisonCount = Mathf.Clamp(IntArg(args, PoisonCountArg, DefaultPoisonCount), 0, 10000);
            _tag = Sanitize(StringArg(args, TagArg, "smoke"));

            // Default: poison the middle of the measured window, so the per-frame CSV must show
            // the injection APPEAR and DISAPPEAR. A single aggregate number cannot prove that.
            _poisonAt = IntArg(args, PoisonAtArg, _warmupFrames + _measuredFrames / 2);
            _poisonAt = Mathf.Clamp(_poisonAt, 0, _warmupFrames + _measuredFrames);

            int total = _warmupFrames + _measuredFrames;
            _cleanNs = new double[total];
            _cleanLastNs = new double[total];
            _poisonNs = new double[total];
            _poisonLastNs = new double[total];
            _mainThreadNs = new double[total];
            _gcAllocBytes = new double[total];
            _cleanCalls = new int[total];
            _poisonCalls = new int[total];
            _gcAllocCalls = new int[total];
            _cleanCallsPrev = new int[total];
            _cleanCount = new int[total];
            _cleanCallsOldest = new int[total];
            _cleanCallsWide = new int[total];
            _cpuFrameMs = new double[total];
            _gpuFrameMs = new double[total];
            _cpuMainMs = new double[total];
            _tickCounts = new int[total];
            _poisoned = new bool[total];
            _timings = new FrameTiming[1];

            // SumAllSamplesInFrame + CollectOnlyOnCurrentThread mirrors Unity's own documented
            // example for counting GC.Alloc: it collapses a frame's samples into one row whose
            // .Count is the number of samples that went into it - i.e. the CALL COUNT.
            const ProfilerRecorderOptions opts =
                ProfilerRecorderOptions.SumAllSamplesInFrame |
                ProfilerRecorderOptions.CollectOnlyOnCurrentThread;

            _cleanRec = ProfilerRecorder.StartNew(CleanMarker, 1, opts);
            _poisonRec = ProfilerRecorder.StartNew(PoisonedMarker, 1, opts);
            _mainThreadRec = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1, opts);
            _gcAllocRec = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, opts);
            _cleanRecWide = ProfilerRecorder.StartNew(CleanMarker, total, opts);

            // Tests must not make noise. This is a harness detail, not a game behaviour: the audio
            // listener's master gain is zeroed and the mixer paused, so music/SFX emitters cost
            // nothing and cannot be heard during a run. The user had to kill hung players by hand
            // twice; loud audio on top of that is just rude.
            AudioListener.volume = 0f;
            AudioListener.pause = true;

            _wall.Start();

            // FrameTimingManager is documented as always active in a Development Player, so this
            // needs no Player Setting. It is a second, independent per-frame number.
            _frameTimingEnabled = FrameTimingManager.IsFeatureEnabled();

            Debug.Log($"[ProfileSmoke] start tag={_tag} warmup={_warmupFrames} frames={_measuredFrames} " +
                      $"poisonAt={_poisonAt} poisonMs={_poisonMs} poisonCount={_poisonCount} " +
                      $"isDebugBuild={Debug.isDebugBuild} frameTiming={_frameTimingEnabled}");
        }

        // ── Per-frame work ───────────────────────────────────────────────────────────

        void Update()
        {
            if (_finished) return;
            // Defensive: Hook() calls Configure() synchronously, but an Update before that would
            // dereference null recorders. Same class of trap as HUDViewModel's deferred binding.
            if (_cleanNs == null) return;

            _updates++;

            // HARD WATCHDOG. This is not a precaution - it exists because of a real hang.
            //
            // An exception thrown inside Update() aborts the REST of that method, so a throw before
            // `_frame++` means Finish() is never reached and Application.Quit() is never called.
            // The player then runs forever and has to be killed by hand. That is exactly what
            // happened on 2026-09-28: GetSample(Count - 1) was out of range for a capacity-1
            // recorder and threw on EVERY frame, so the probe hung twice.
            //
            // Quitting is therefore driven by this counter, which no exception can skip.
            if (_updates > _warmupFrames + _measuredFrames + 30)
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
                // Record rather than rethrow. A report saying "N frames threw
                // ArgumentOutOfRangeException" is a result; a hung process is not.
                _exceptions++;
                _lastException = e.GetType().Name + ": " + e.Message;
                if (_exceptions <= 3)
                {
                    Debug.LogError($"[ProfileSmoke] tick threw (frame {_frame}): {_lastException}");
                }
            }
        }

        void Tick()
        {
            // Read the PREVIOUS frame's sample first, before this frame's work touches the recorder.
            // At the top of Update the recorder still holds frame N-1's collapsed sample, so this is
            // the after-boundary read; it is stored against N-1 so the two call-count columns line
            // up on the same row and can be compared directly.
            if (_frame > 0)
            {
                _cleanCallsPrev[_frame - 1] = SampleCount(_cleanRec);
            }

            int total = _warmupFrames + _measuredFrames;
            if (_frame >= total)
            {
                Finish();
                return;
            }

            bool poisonThisFrame = _poisonCount > 0 &&
                                   _frame >= _poisonAt && _frame < _poisonAt + _poisonCount;

            // The known-bad input. If the harness is working, this shows up as a ~_poisonMs spike
            // in exactly these frames. If it shows up nowhere, the harness is blind.
            if (poisonThisFrame)
            {
                using (PoisonedMarker.Auto())
                {
                    Thread.Sleep(_poisonMs);
                }
            }

            using (CleanMarker.Auto())
            {
                CleanWork(_frame);
            }

            // Sample immediately, in the same frame. Both CurrentValue and LastValue are recorded
            // because it is NOT documented which one carries a SumAllSamplesInFrame aggregate
            // mid-frame - that is one of the questions this probe exists to answer. LastValue is
            // documented as readable only after a frame change, so it is expected to lag by one
            // frame; the CSV makes that visible instead of hiding it.
            _cleanNs[_frame] = _cleanRec.CurrentValue;
            _cleanLastNs[_frame] = _cleanRec.LastValue;
            _poisonNs[_frame] = _poisonRec.CurrentValue;
            _poisonLastNs[_frame] = _poisonRec.LastValue;
            _mainThreadNs[_frame] = _mainThreadRec.CurrentValue;
            _gcAllocBytes[_frame] = _gcAllocRec.CurrentValue;

            _cleanCalls[_frame] = SampleCount(_cleanRec);
            _poisonCalls[_frame] = SampleCount(_poisonRec);
            _gcAllocCalls[_frame] = SampleCount(_gcAllocRec);
            _cleanCount[_frame] = _cleanRec.Count;
            _cleanCallsOldest[_frame] = SampleCountOldest(_cleanRec);
            _cleanCallsWide[_frame] = SampleCount(_cleanRecWide);

            _poisoned[_frame] = poisonThisFrame;
            // Fully qualified on purpose: `namespace PFE.Core.Time` exists, so a bare `Time`
            // inside PFE.Core.* resolves to that namespace, not to UnityEngine.Time (CS0234).
            // Same reason CameraFollow / SceneLoader / SimLoop write UnityEngine.Time.
            _tickCounts[_frame] = UnityEngine.Time.frameCount;

            if (_frameTimingEnabled)
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
                {
                    _cpuFrameMs[_frame] = _timings[0].cpuFrameTime;
                    _gpuFrameMs[_frame] = _timings[0].gpuFrameTime;
                    _cpuMainMs[_frame] = _timings[0].cpuMainThreadFrameTime;
                }
            }

            _frame++;
        }

        /// <summary>
        /// Marker invocations in the NEWEST collected sample.
        ///
        /// Two corrections live here, both paid for with a wasted run:
        ///
        /// 1. Guard on Count, NOT on Valid. Valid is documented as false until the marker/counter
        ///    has been CREATED, so it answers a registration question, not "does this frame have
        ///    samples". Gating on it reported 0 for every frame.
        ///
        /// 2. Read the NEWEST sample, not index 0. Unity's own SumAllSamplesInFrame example reads
        ///    GetSample(0), but that example creates a fresh recorder and Stops it immediately, so
        ///    its Count is 1 and index 0 is also the newest - the example cannot distinguish them.
        ///    In a long-running recorder Count grows by one per frame (measured: 31 -> 150 across a
        ///    150-frame run, one sample per frame), and GetSample(0).Count was 0 for all 120
        ///    measured frames while Count was large: index 0 is the OLDEST retained sample, whose
        ///    count is 0 because the marker was still being registered that frame.
        ///
        /// NOTE: ProfilerRecorderSample.Count is a long, and ProfilerRecorder.Count is an int.
        /// The cast is safe - this is a per-frame sample count, nowhere near int.MaxValue.
        /// </summary>
        static int SampleCount(ProfilerRecorder recorder)
        {
            int n = recorder.Count;
            if (n <= 0) return 0;

            // CLAMP. GetSample(index) THROWS for an out-of-range index, and a throw in Update()
            // hangs the whole run (see the watchdog in Update). Count is documented as "< Capacity",
            // so with the capacity-1 recorders here the only legal index is 0 - passing Count-1
            // threw on every single frame and produced the hang this clamp exists to prevent.
            int capacity = recorder.Capacity;
            int index = Mathf.Clamp(n - 1, 0, capacity > 0 ? capacity - 1 : 0);
            return (int)recorder.GetSample(index).Count;
        }

        /// <summary>
        /// Index-0 count, kept purely as the control for the claim above. If this stays 0 while
        /// <see cref="SampleCount"/> is non-zero, the oldest-vs-newest reading is proven by the
        /// data rather than argued from the docs.
        /// </summary>
        static int SampleCountOldest(ProfilerRecorder recorder)
        {
            if (recorder.Count <= 0) return 0;
            return (int)recorder.GetSample(0).Count;
        }

        static long _sink;

        static void CleanWork(int seed)
        {
            // Deterministic, non-trivial, allocation-free. The absolute number does not matter;
            // what matters is that it is reproducibly non-zero.
            long acc = seed;
            for (int i = 0; i < 2000; i++)
            {
                acc = (acc * 1103515245L + 12345L) & 0x7FFFFFFFL;
            }
            _sink = acc;
        }

        // ── Report ───────────────────────────────────────────────────────────────────

        // Captured while the recorders are still alive. ProfilerRecorder.Valid is documented as
        // false until the marker/counter has been created, and a DISPOSED recorder is associated
        // with nothing at all - so reading Valid inside the report produced a diagnostic that was
        // always "False" and explained nothing (first run, 2026-09-28). Snapshot before Dispose.
        string _recorderDiag = "(not captured)";

        void Finish()
        {
            _finished = true;

            // The after-boundary read for the FINAL measured frame: Update() writes index _frame-1,
            // so without this the last row's prev column would stay 0 and undercount by one frame.
            if (_frame > 0) _cleanCallsPrev[_frame - 1] = SampleCount(_cleanRec);

            _recorderDiag =
                $"clean={_cleanRec.Valid}/count={_cleanRec.Count} " +
                $"poison={_poisonRec.Valid}/count={_poisonRec.Count} " +
                $"mainThread={_mainThreadRec.Valid}/count={_mainThreadRec.Count} " +
                $"gcAlloc={_gcAllocRec.Valid}/count={_gcAllocRec.Count} " +
                $"cleanWide={_cleanRecWide.Valid}/count={_cleanRecWide.Count}/cap={_cleanRecWide.Capacity}";

            _cleanRec.Dispose();
            _poisonRec.Dispose();
            _mainThreadRec.Dispose();
            _gcAllocRec.Dispose();
            _cleanRecWide.Dispose();

            string dir = DumpDirectory();
            string stamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string baseName = "smoke_" + _tag + "_" + stamp;

            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, baseName + ".csv"), BuildCsv());
                string summary = BuildSummary();
                File.WriteAllText(Path.Combine(dir, baseName + ".txt"), summary);
                Debug.Log(summary);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ProfileSmoke] could not write report: {e.Message}");
            }

            Application.Quit();
        }

        string BuildCsv()
        {
            int total = _warmupFrames + _measuredFrames;
            var sb = new StringBuilder(total * 112);
            // The raw columns accumulate (see Deltas()); the *_delta columns are the per-frame
            // numbers. Read the deltas, keep the raw ones only to sanity-check the accumulation.
            double[] cleanDelta = Deltas(_cleanNs);
            double[] poisonDeltaCsv = Deltas(_poisonNs);
            sb.AppendLine("frame,unityFrame,phase,poisoned,clean_ns,clean_last_ns,clean_calls," +
                          "clean_calls_prev,clean_count,clean_calls_oldest,clean_calls_wide," +
                          "poison_ns,poison_last_ns,poison_calls," +
                          "mainthread_ns,gcalloc_bytes,gcalloc_calls,cpuFrameTime_ms,gpuFrameTime_ms," +
                          "cpuMainThread_ms,clean_delta_ns,poison_delta_ns");

            for (int i = 0; i < total; i++)
            {
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_tickCounts[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(i < _warmupFrames ? "warmup" : "measured").Append(',')
                  .Append(_poisoned[i] ? '1' : '0').Append(',')
                  .Append(_cleanNs[i].ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_cleanLastNs[i].ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_cleanCalls[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_cleanCallsPrev[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_cleanCount[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_cleanCallsOldest[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_cleanCallsWide[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_poisonNs[i].ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_poisonLastNs[i].ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_poisonCalls[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_mainThreadNs[i].ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_gcAllocBytes[i].ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_gcAllocCalls[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_cpuFrameMs[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_gpuFrameMs[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_cpuMainMs[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(cleanDelta[i].ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                  .Append(poisonDeltaCsv[i].ToString("F0", CultureInfo.InvariantCulture))
                  .Append('\n');
            }
            return sb.ToString();
        }

        string BuildSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== PROFILE SMOKE TEST =====");
            sb.AppendLine($"tag              : {_tag}");
            sb.AppendLine($"isDebugBuild     : {Debug.isDebugBuild}");
            sb.AppendLine($"unityVersion     : {Application.unityVersion}");
            sb.AppendLine($"frameTimingOn    : {_frameTimingEnabled}");
            sb.AppendLine($"warmup / measured: {_warmupFrames} / {_measuredFrames}");
            // Total wall time from Configure to Finish, and the implied frame rate. Engine boot is
            // NOT included - the probe is created after the scene loads - so a full test is
            // (player boot) + this number.
            double secs = _wall.Elapsed.TotalSeconds;
            sb.AppendLine($"probe wall time  : {secs:F2} s for {_updates} ticks " +
                          $"({(_updates > 0 ? _updates / secs : 0):F0} ticks/s)");
            // Read the sink so the field is genuinely used (a private field that is only ever
            // assigned is CS0414, and it also keeps the JIT from eliding the clean work loop).
            sb.AppendLine($"clean work sink  : {_sink}");
            sb.AppendLine($"poison window    : frames [{_poisonAt} .. {_poisonAt + _poisonCount - 1}] at {_poisonMs} ms");
            sb.AppendLine($"recorder state   : {_recorderDiag}   (sampled before Dispose)");
            // Non-zero here means the tick threw and the run is suspect - but it still produced a
            // report and exited, which is the whole point of the watchdog.
            sb.AppendLine($"ticks/exceptions : {_updates} / {_exceptions}   last={_lastException}");

            if (!Debug.isDebugBuild)
            {
                sb.AppendLine();
                sb.AppendLine("*** RELEASE BUILD DETECTED ***");
                sb.AppendLine("ProfilerMarker.Begin/End are [Conditional] and are compiled away outside a");
                sb.AppendLine("Development Build. Every custom marker column below is expected to be ZERO");
                sb.AppendLine("and this run proves NOTHING. Rebuild with Development Build enabled.");
            }

            sb.AppendLine();
            sb.AppendLine("-- measured-window statistics --");
            // *_delta_* rows are the PER-FRAME numbers. Everything else with a marker name is a
            // running total from StartNew and must not be read as a frame cost - see Deltas().
            sb.AppendLine("metric                    n   nonzero      min      median        max");

            AppendStat(sb, "clean_ns", _cleanNs, false);
            AppendStat(sb, "clean_last_ns", _cleanLastNs, false);
            AppendStat(sb, "poison_ns", _poisonNs, false);
            AppendStat(sb, "poison_last_ns", _poisonLastNs, false);
            AppendStat(sb, "mainthread_ns", _mainThreadNs, false);
            AppendStat(sb, "clean_calls", _cleanCalls, true);
            AppendStat(sb, "clean_calls_prev", _cleanCallsPrev, true);
            AppendStat(sb, "clean_count", _cleanCount, true);
            AppendStat(sb, "clean_calls_oldest", _cleanCallsOldest, true);
            AppendStat(sb, "clean_calls_wide", _cleanCallsWide, true);
            AppendStat(sb, "poison_calls", _poisonCalls, true);
            AppendStat(sb, "gcalloc_bytes", _gcAllocBytes, false);
            AppendStat(sb, "cpuFrameTime_ms", _cpuFrameMs, false);
            AppendStat(sb, "gpuFrameTime_ms", _gpuFrameMs, false);
            AppendStat(sb, "clean_delta_ns", Deltas(_cleanNs), false);
            AppendStat(sb, "poison_delta_ns", Deltas(_poisonNs), false);

            sb.AppendLine();
            sb.AppendLine("-- verdict --");
            sb.AppendLine(Verdict());
            sb.AppendLine("==============================");
            return sb.ToString();
        }

        /// <summary>
        /// First difference of a cumulative series.
        ///
        /// REQUIRED, not a nicety. Measured on the 2026-09-28 run: a ProfilerRecorder created with
        /// SumAllSamplesInFrame reports a value that ACCUMULATES from StartNew and never resets -
        /// CurrentValue went 0 -> 50.9 -> 100.7 -> 151.0 -> 201.2 -> 252.5 ms across the five
        /// poisoned frames and then stayed at 252.5 ms for the remaining 55 frames. A raw read
        /// therefore cannot say which frame was slow; only the frame-to-frame delta can.
        /// Index 0 is left at 0 (no predecessor).
        /// </summary>
        double[] Deltas(double[] series)
        {
            int total = _warmupFrames + _measuredFrames;
            var d = new double[total];
            for (int i = 1; i < total; i++) d[i] = series[i] - series[i - 1];
            return d;
        }

        // Call counts are naturally int[] (SampleCount returns int) while every timing series is
        // double[]. Copy instead of duplicating the statistics body: this runs once, in the report,
        // so the allocation costs nothing and there is only one place for the maths to be wrong.
        void AppendStat(StringBuilder sb, string label, int[] series, bool integerish)
        {
            var asDouble = new double[series.Length];
            for (int i = 0; i < series.Length; i++) asDouble[i] = series[i];
            AppendStat(sb, label, asDouble, integerish);
        }

        void AppendStat(StringBuilder sb, string label, double[] series, bool integerish)
        {
            int n = 0, nonzero = 0;
            double min = double.MaxValue, max = double.MinValue;
            var measured = new List<double>();
            int total = _warmupFrames + _measuredFrames;
            for (int i = _warmupFrames; i < total; i++)
            {
                double v = series[i];
                if (v != 0.0) nonzero++;
                if (v < min) min = v;
                if (v > max) max = v;
                measured.Add(v);
                n++;
            }
            if (n == 0) return;
            measured.Sort();
            double median = measured[n / 2];
            sb.Append(label.PadRight(24))
              .Append(n.ToString(CultureInfo.InvariantCulture).PadLeft(5))
              .Append(nonzero.ToString(CultureInfo.InvariantCulture).PadLeft(9))
              .Append((integerish ? min.ToString("F0", CultureInfo.InvariantCulture)
                                  : min.ToString("F1", CultureInfo.InvariantCulture)).PadLeft(9))
              .Append((integerish ? median.ToString("F0", CultureInfo.InvariantCulture)
                                  : median.ToString("F1", CultureInfo.InvariantCulture)).PadLeft(11))
              .Append((integerish ? max.ToString("F0", CultureInfo.InvariantCulture)
                                  : max.ToString("F1", CultureInfo.InvariantCulture)).PadLeft(11))
              .AppendLine();
        }

        string Verdict()
        {
            var lines = new List<string>();

            int total = _warmupFrames + _measuredFrames;

            // Judged on the DELTA, not the raw series - see Deltas() for the measurement that
            // forced this. A cumulative series makes any peak test pass trivially and says nothing
            // about per-frame resolution; what actually proves the harness works is that the jump
            // appears in exactly the injected frames and nowhere else.
            double expectedNs = _poisonMs * 1_000_000.0;
            double[] poisonDelta = Deltas(_poisonNs);
            int spikeFrames = 0, spikesInWindow = 0;
            double deltaPeak = 0;
            for (int i = _warmupFrames; i < total; i++)
            {
                if (poisonDelta[i] < expectedNs * 0.5) continue;
                spikeFrames++;
                if (poisonDelta[i] > deltaPeak) deltaPeak = poisonDelta[i];
                if (_poisoned[i]) spikesInWindow++;
            }

            double cleanPeak = 0;
            for (int i = _warmupFrames; i < total; i++)
            {
                if (_cleanNs[i] > cleanPeak) cleanPeak = _cleanNs[i];
            }

            // Q1 - the load-bearing question.
            bool cleanWorks = cleanPeak > 0;
            lines.Add($"Q1 custom marker recorded without a Profiler session : {(cleanWorks ? "YES" : "NO")}" +
                      $"   (peak {cleanPeak:F0} ns)");

            // Q2 - the known-bad input. A probe that has never failed on a broken input proves
            // nothing, and "the number went up" is not a failure mode it can detect. The strict
            // form is: every spike lands inside the injected window, and there are exactly as many
            // spikes as injected frames.
            bool poisonDetected = spikeFrames > 0 && spikesInWindow == spikeFrames &&
                                  spikesInWindow == _poisonCount;
            lines.Add($"Q2 {_poisonMs} ms injection isolated to its own frames     : " +
                      $"{(poisonDetected ? "YES" : "NO")}   ({spikesInWindow}/{spikeFrames} spikes inside the " +
                      $"injected window of {_poisonCount}, peak delta {deltaPeak:F0} ns, expected ~{expectedNs:F0})");

            // Q3 - call counts, which is what "log calls and their change" needs. BOTH read points
            // are checked because SumAllSamplesInFrame is documented as collapsing a frame's samples
            // into one "per frame" sample: a mid-frame read can legitimately be empty while the
            // after-boundary read is not. Reporting which one worked is the whole point.
            int midFrames = 0, prevFrames = 0, oldestFrames = 0, wideFrames = 0;
            for (int i = _warmupFrames; i < total; i++)
            {
                if (_cleanCalls[i] > 0) midFrames++;
                if (_cleanCallsPrev[i] > 0) prevFrames++;
                if (_cleanCallsOldest[i] > 0) oldestFrames++;
                if (_cleanCallsWide[i] > 0) wideFrames++;
            }
            int bestFrames = Mathf.Max(Mathf.Max(midFrames, prevFrames), wideFrames);
            string q3How = midFrames == 0 && prevFrames == 0
                ? "no count from either read point"
                : (prevFrames >= midFrames ? "read after the frame boundary" : "read mid-frame");
            lines.Add($"Q3 per-frame call counts reported                   : " +
                      $"{(bestFrames > 0 ? "YES" : "NO")}   ({bestFrames}/{_measuredFrames} frames non-zero; " +
                      $"cap1 mid={midFrames} prev={prevFrames} -> {q3How})");
            lines.Add($"   capacity sweep: wide-capacity recorder non-zero in {wideFrames}/{_measuredFrames} " +
                      $"frames; GetSample(0) non-zero in {oldestFrames}/{_measuredFrames} (index 0 is the OLDEST)");
            if (wideFrames > 0 && midFrames == 0)
            {
                lines.Add("   -> per-frame call counts DO work, but only with capacity >= frame count; " +
                          "the capacity-1 recorder cannot hold a per-frame series.");
            }

            // Q4 - the second, independent frame-timing source.
            int cpuFrames = 0;
            for (int i = _warmupFrames; i < total; i++)
            {
                if (_cpuFrameMs[i] > 0) cpuFrames++;
            }
            lines.Add($"Q4 FrameTimingManager cpu/gpu frame time            : " +
                      $"{(cpuFrames > 0 ? "YES" : "NO")}   ({cpuFrames}/{_measuredFrames} frames non-zero, " +
                      $"enabled={_frameTimingEnabled})");

            // Q5 - which property carries a SumAllSamplesInFrame aggregate mid-frame, and by how
            // much LastValue lags. The CSV has both columns; this just says which one to use.
            double lastPeak = 0;
            for (int i = _warmupFrames; i < total; i++)
            {
                if (_cleanLastNs[i] > lastPeak) lastPeak = _cleanLastNs[i];
            }
            bool lastWorks = lastPeak > 0;
            string q5 = cleanWorks && lastWorks
                ? "both work - prefer CurrentValue (same-frame, no lag)"
                : cleanWorks
                    ? "CurrentValue only - LastValue stayed 0, so do not sample it in the same frame"
                    : lastWorks
                        ? "LastValue only - read it one frame behind the marker"
                        : "neither - the marker is not being recorded at all";
            lines.Add($"Q5 CurrentValue vs LastValue                      : {q5}");

            lines.Add("");
            lines.Add(cleanWorks && poisonDetected
                ? ">>> HARNESS VIABLE: proceed to Tier 1."
                : ">>> HARNESS NOT PROVEN: read the notes in the feasibility doc before building more.");
            return string.Join("\n", lines);
        }

        // ── Output location: same folder convention as PfeProfiler ───────────────────

        static string DumpDirectory()
        {
#if UNITY_EDITOR
            return Path.Combine(Application.dataPath, "..", "ProfilerCaptures");
#else
            return Path.Combine(Application.persistentDataPath, "ProfilerCaptures");
#endif
        }

        static string Sanitize(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "smoke";
            var sb = new StringBuilder(tag.Length);
            foreach (char c in tag)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            return sb.ToString();
        }
    }
}
