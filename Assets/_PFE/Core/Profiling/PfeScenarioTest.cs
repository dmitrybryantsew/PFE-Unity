// Tier-3 harness: scripted input replay + screenshots, headless.
//
// WHY THIS EXISTS
// The Tier-0 probe (ProfilerRecorderSmokeTest) answers "can a custom marker be read per frame in a
// Development player". This file answers the next two questions, both of which previously needed a
// human at the keyboard:
//
//   1. MOVEMENT - does injected input actually move the player? Not "does the input object hold a
//      value", but "did transform.position change, in the right direction, and stop when released".
//   2. SCREENSHOTS - does the headless player render anything at all, and does the frame change over
//      time? Today nothing in this repo asserts that the tile atlas composites to a non-blank image.
//
// INPUT IS INJECTED AT THE DEVICE, NOT AT InputReader.
// Writing InputReader.MoveInput.Value directly is the obvious shortcut and it is worthless: the
// action callback (InputReader.SetupEventSubscriptions) assigns MoveInput.Value itself, so the
// injected value is overwritten and a broken binding chain still "passes". QueueStateEvent instead
// drives the real path:
//     KeyboardState -> 2DVector composite -> Move action -> performed -> MoveInput.Value
// so a wrong key, a broken composite or a disabled action map all show up as "the player did not
// move". That is the whole point of the test.
//
// SCREENSHOTS: BOTH PATHS IN ONE RUN, DELIBERATELY.
// docs/Perf_HeadlessProfileHarness_Feasibility.md recommends rendering into a RenderTexture over
// ScreenCapture.CaptureScreenshot, which reads the back buffer and "is not guaranteed under
// -batchmode with no window". That is a claim, not a measurement, and the answer decides the design.
// So each shot is taken BOTH ways and both are measured:
//     <tag>/shot_0000_rt.png     ScreenCapture.CaptureScreenshotIntoRenderTexture + ReadPixels
//     <tag>/shot_0000_file.png   ScreenCapture.CaptureScreenshot + read-back + decode
// Whichever produces non-blank pixels wins; the other is reported as blank with its statistics.
// CaptureScreenshotIntoRenderTexture is used rather than Camera.Render() because this project is URP
// and Camera.Render() is not the supported path under SRP; the URP render-request API would need a
// new asmdef reference on PFE.Core, and this does not.
//
// HOW TO RUN
//   PFE_Unity.exe -batchmode -logFile <WINDOWS path> -pfe-scenario move+shots \
//     -pfe-scenario-tag m1 -pfe-scenario-frames 180 -pfe-scenario-warmup 60 \
//     -pfe-scenario-move right -pfe-scenario-shots 4
//   ./run-scenario.sh move+shots m1
//
// A HARD WATCHDOG, same as the Tier-0 probe, and for the same reason: on 2026-09-28 a throw inside
// Update() skipped the frame counter, so Finish() was never reached, Application.Quit() was never
// called and the player had to be killed from Task Manager - twice. A probe whose only exit is the
// end of its own measurement loop has no exit path at all. _updates increments OUTSIDE every try
// block and forces the exit.
//
// OUTPUT (player: %USERPROFILE%\AppData\LocalLow\DefaultCompany\PFE_Unity\ProfilerCaptures\scenario\)
//   scenario_<tag>_<stamp>.csv   one row per frame: phase, position, live input, shot index
//   scenario_<tag>_<stamp>.txt   verdict: movement deltas, screenshot statistics, pass/fail
//   scenario_<tag>_<stamp>/      the PNGs

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PFE.Core.Input;
using PFE.Entities.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using VContainer;

namespace PFE.Core.Profiling
{
    public sealed class PfeScenarioTest : MonoBehaviour
    {
        // ── Command line ─────────────────────────────────────────────────────────────

        const string EnableArg = "-pfe-scenario";
        const string TagArg = "-pfe-scenario-tag";
        const string FramesArg = "-pfe-scenario-frames";
        const string WarmupArg = "-pfe-scenario-warmup";
        const string MoveArg = "-pfe-scenario-move";
        const string ShotsArg = "-pfe-scenario-shots";
        const string ShotModeArg = "-pfe-scenario-shotmode";

        const int DefaultFrames = 180;
        const int DefaultWarmup = 60;
        const int DefaultShots = 4;
        const int MaxFrames = 20000;

        // ── Phases ───────────────────────────────────────────────────────────────────
        // 0 settle (no input) -> 1 hold the direction -> 2 release (no input).
        // Phase 2 is not decoration: without it "the player moved" cannot be distinguished from
        // "something else moved the player", and it proves the release path (Move.canceled ->
        // MoveInput.Value = zero) actually reaches the controller.
        const int PhaseSettle = 0;
        const int PhaseHold = 1;
        const int PhaseRelease = 2;

        // ── State ────────────────────────────────────────────────────────────────────

        PlayerController _player;
        InputReader _input;
        Camera _camera;
        GameLifetimeScope _scope;
        // Frame on which the player was FIRST resolved, or -1. The player is not a scene-authored
        // object - it is a prefab instance that MapBridge.SpawnPlayer repositions once a room has
        // loaded - so the settle baseline is only valid from the frame the player actually exists.
        // Without this, a player resolved after the warmup would be differenced against the default
        // (0,0) and the reported displacement would be pure noise.
        int _playerFirstFrame = -1;

        int _measuredFrames;
        int _warmupFrames;
        int _totalFrames;
        int _shotCount;
        int _holdEndFrame;
        string _tag;
        string _moveName;
        Key[] _holdKeys;

        bool _wantsRt;
        bool _wantsFile;

        // Per-frame rows. Preallocated: a per-frame allocation would make this harness the thing
        // being measured.
        int[] _phase;
        float[] _posX;
        float[] _posY;
        float[] _inputX;
        float[] _inputY;
        int[] _shotIndex;
        int[] _unityFrame;

        int _frame;
        // Incremented OUTSIDE every try block - the watchdog depends on it, so nothing may skip it.
        int _updates;
        int _exceptions;
        string _lastException = "(none)";
        bool _finished;
        readonly System.Diagnostics.Stopwatch _wall = new System.Diagnostics.Stopwatch();

        // ── Screenshots ──────────────────────────────────────────────────────────────

        RenderTexture _rt;
        Texture2D _rtReadback;
        int _shotW, _shotH;
        readonly List<ShotResult> _shots = new List<ShotResult>();
        // file-path captures are asynchronous: remember which frame each was requested on so the
        // read-back happens after the encoder has written it.
        readonly List<PendingFileShot> _pendingFileShots = new List<PendingFileShot>();
        string _shotDir;

        struct PendingFileShot
        {
            public int Index;
            public int RequestedFrame;
            public string Path;
        }

        sealed class ShotResult
        {
            public int Index;
            public string Mode;      // "rt" | "file"
            public bool Written;
            public string Path;
            public string Note = "";
            public int Width;
            public int Height;
            public double Mean;
            public double Std;
            public string Fingerprint = "";

            public bool NonBlank => Written && Std >= 1.0 && Mean >= 1.0;
        }

        // ── Entry hook ───────────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            if (!HasArg(args, EnableArg)) return;

            var go = new GameObject("[PfeScenarioTest]");
            Object.DontDestroyOnLoad(go);
            var test = go.AddComponent<PfeScenarioTest>();
            test.Configure(args);
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
            // `-pfe-scenario move+shots` and `-pfe-scenario shots` are both valid; the value itself
            // selects the work, so only its PRESENCE is required.
            string mode = StringArg(args, EnableArg, "move+shots").ToLowerInvariant();
            bool wantsMove = mode.Contains("move") || mode.Contains("all");
            bool wantsShots = mode.Contains("shot") || mode.Contains("all");
            if (!wantsMove && !wantsShots) wantsMove = true;

            _measuredFrames = Mathf.Clamp(IntArg(args, FramesArg, DefaultFrames), 1, MaxFrames);
            _warmupFrames = Mathf.Clamp(IntArg(args, WarmupArg, DefaultWarmup), 0, MaxFrames);
            _shotCount = wantsShots ? Mathf.Clamp(IntArg(args, ShotsArg, DefaultShots), 0, 64) : 0;
            _tag = Sanitize(StringArg(args, TagArg, "scenario"));
            _moveName = StringArg(args, MoveArg, "right").ToLowerInvariant();
            _holdKeys = KeysFor(_moveName);

            string shotMode = StringArg(args, ShotModeArg, "both").ToLowerInvariant();
            _wantsRt = shotMode == "both" || shotMode == "rt";
            _wantsFile = shotMode == "both" || shotMode == "file";

            _totalFrames = _warmupFrames + _measuredFrames;
            _holdEndFrame = _warmupFrames + _measuredFrames / 2;

            _phase = new int[_totalFrames];
            _posX = new float[_totalFrames];
            _posY = new float[_totalFrames];
            _inputX = new float[_totalFrames];
            _inputY = new float[_totalFrames];
            _shotIndex = new int[_totalFrames];
            _unityFrame = new int[_totalFrames];
            for (int i = 0; i < _totalFrames; i++) _shotIndex[i] = -1;

            // Tests must not make noise. Harness detail, not game behaviour: the user had to kill
            // hung players by hand twice; loud audio on top of that is just rude.
            AudioListener.volume = 0f;
            AudioListener.pause = true;

            _shotDir = null;

            _wall.Start();

            Debug.Log($"[Scenario] start mode={mode} tag={_tag} warmup={_warmupFrames} " +
                      $"frames={_measuredFrames} move={_moveName} shots={_shotCount} " +
                      $"shotmode={shotMode} isDebugBuild={Debug.isDebugBuild} " +
                      $"batchmode={Application.isBatchMode} screen={Screen.width}x{Screen.height}");
        }

        static Key[] KeysFor(string name)
        {
            switch (name)
            {
                case "left": return new[] { Key.A };
                case "up": return new[] { Key.W };
                case "down": return new[] { Key.S };
                case "right": return new[] { Key.D };
                case "none": return new Key[0];
                default: return new[] { Key.D };
            }
        }

        static Vector2 ExpectedDirection(string name)
        {
            switch (name)
            {
                case "left": return new Vector2(-1f, 0f);
                case "up": return new Vector2(0f, 1f);
                case "down": return new Vector2(0f, -1f);
                default: return new Vector2(1f, 0f);
            }
        }

        // ── Per-frame ────────────────────────────────────────────────────────────────

        void Update()
        {
            if (_finished) return;
            if (_phase == null) return;

            _updates++;

            // HARD WATCHDOG - see the header. No exception can skip this, because _updates is
            // incremented above the try block.
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
                    Debug.LogError($"[Scenario] tick threw (frame {_frame}): {_lastException}");
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

            // Lazy, and every frame until it succeeds: at AfterSceneLoad the container may not be
            // built yet, and a null here would be a timing artefact rather than a wiring fault.
            ResolveSceneObjects();

            int phase = _frame < _warmupFrames
                ? PhaseSettle
                : (_frame < _holdEndFrame ? PhaseHold : PhaseRelease);

            // Injection happens on the phase BOUNDARY, not every frame. The Input System device holds
            // its state until it is changed, so re-queueing the same state every frame is pointless;
            // and the release must be a distinct event, because Move.canceled is what zeroes
            // MoveInput.Value.
            int previousPhase = _frame > 0 ? _phase[_frame - 1] : PhaseSettle;
            if (_frame == 0 || phase != previousPhase)
            {
                InjectFor(phase);
            }

            _phase[_frame] = phase;
            if (_player != null)
            {
                Vector3 p = _player.transform.position;
                _posX[_frame] = p.x;
                _posY[_frame] = p.y;
            }
            // The LIVE input the controller will read this frame. If this stays zero while keys are
            // held, the binding chain is broken and the movement verdict below is meaningless - so
            // it is recorded rather than assumed.
            if (_input != null)
            {
                Vector2 live = _input.CurrentMoveInput;
                _inputX[_frame] = live.x;
                _inputY[_frame] = live.y;
            }
            // Fully qualified on purpose: `namespace PFE.Core.Time` exists, so a bare `Time` inside
            // PFE.Core.* resolves to that namespace, not UnityEngine.Time (CS0234).
            _unityFrame[_frame] = UnityEngine.Time.frameCount;

            if (_shotCount > 0 && IsShotFrame(_frame, out int shotIdx))
            {
                _shotIndex[_frame] = shotIdx;
                BeginShot(shotIdx);
            }

            PumpFileShotReadbacks();

            _frame++;
        }

        /// <summary>
        /// Evenly spaced inside the MEASURED window (not the warmup), so shots land on frames where
        /// the player is actually moving rather than on scene-load frames that are already known to
        /// dominate the run.
        /// </summary>
        bool IsShotFrame(int frame, out int index)
        {
            index = -1;
            if (frame < _warmupFrames) return false;
            int span = _totalFrames - _warmupFrames;
            for (int i = 0; i < _shotCount; i++)
            {
                int at = _warmupFrames + (int)((long)span * (i + 1) / (_shotCount + 1));
                if (at == frame)
                {
                    index = i;
                    return true;
                }
            }
            return false;
        }

        void InjectFor(int phase)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                Debug.LogWarning("[Scenario] Keyboard.current is null - cannot inject input.");
                return;
            }

            // PhaseSettle and PhaseRelease both inject an EMPTY state, which is exactly the release
            // path under test. PhaseHold presses the direction keys.
            Key[] keys = phase == PhaseHold ? _holdKeys : new Key[0];
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
        }

        // ── Screenshots ──────────────────────────────────────────────────────────────

        void BeginShot(int index)
        {
            if (_shotDir == null)
            {
                _shotDir = DumpDirectory();
                try
                {
                    // Both variants write into this directory, so it must exist before the FIRST
                    // shot - CaptureScreenshot into a missing directory fails silently.
                    Directory.CreateDirectory(_shotDir);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Scenario] cannot create shot dir {_shotDir}: {e.Message}");
                }
            }

            if (_wantsRt)
            {
                EnsureRenderTarget();
                if (_rt != null) StartCoroutine(CaptureRt(index));
            }

            if (_wantsFile)
            {
                string path = Path.Combine(_shotDir, $"shot_{index:D4}_file.png");
                try
                {
                    ScreenCapture.CaptureScreenshot(path);
                    _pendingFileShots.Add(new PendingFileShot
                    {
                        Index = index,
                        RequestedFrame = _frame,
                        Path = path
                    });
                }
                catch (System.Exception e)
                {
                    _shots.Add(new ShotResult
                    {
                        Index = index,
                        Mode = "file",
                        Written = false,
                        Path = path,
                        Note = "CaptureScreenshot threw: " + e.Message
                    });
                }
            }
        }

        void EnsureRenderTarget()
        {
            if (_rt != null) return;

            // Prefer the real back-buffer size so the capture is 1:1. In batchmode Screen.width can
            // legitimately be 0, hence the floor - a 0-sized RenderTexture would throw.
            _shotW = Screen.width > 0 ? Screen.width : 640;
            _shotH = Screen.height > 0 ? Screen.height : 360;
            _rt = new RenderTexture(_shotW, _shotH, 24, RenderTextureFormat.ARGB32);
            _rt.name = "PfeScenarioShot";
            _rtReadback = new Texture2D(_shotW, _shotH, TextureFormat.RGBA32, false);
            _rtReadback.name = "PfeScenarioShotReadback";
            Debug.Log($"[Scenario] render target {_shotW}x{_shotH} (Screen {Screen.width}x{Screen.height})");
        }

        /// <summary>
        /// The docs for CaptureScreenshotIntoRenderTexture are explicit: "always wait until the frame
        /// rendering process is complete before calling this method... call it from a coroutine that
        /// yields on WaitForEndOfFrame". So this is a coroutine, not an Update call. The watchdog
        /// still guarantees the run ends even if this never resumes.
        /// </summary>
        IEnumerator CaptureRt(int index)
        {
            yield return new WaitForEndOfFrame();

            try
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_rt);
            }
            catch (System.Exception e)
            {
                _shots.Add(new ShotResult
                {
                    Index = index,
                    Mode = "rt",
                    Written = false,
                    Note = "CaptureScreenshotIntoRenderTexture threw: " + e.Message
                });
                yield break;
            }

            // One more frame so the GPU has finished filling the target before a synchronous read.
            yield return null;

            var result = new ShotResult { Index = index, Mode = "rt" };
            try
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = _rt;
                _rtReadback.ReadPixels(new Rect(0, 0, _shotW, _shotH), 0, 0);
                _rtReadback.Apply();
                RenderTexture.active = previous;

                result.Width = _rtReadback.width;
                result.Height = _rtReadback.height;
                result.Path = Path.Combine(_shotDir, $"shot_{index:D4}_rt.png");
                WriteAndMeasure(_rtReadback, result, writePng: true);
            }
            catch (System.Exception e)
            {
                result.Note = "readback failed: " + e.Message;
            }

            _shots.Add(result);
        }

        void PumpFileShotReadbacks()
        {
            for (int i = _pendingFileShots.Count - 1; i >= 0; i--)
            {
                PendingFileShot pending = _pendingFileShots[i];
                // CaptureScreenshot writes at the END of the requested frame, so give it two frames
                // before reading; a shorter wait reads a file that does not exist yet and would be
                // misreported as "blank".
                if (_frame < pending.RequestedFrame + 2) continue;
                _pendingFileShots.RemoveAt(i);

                var result = new ShotResult { Index = pending.Index, Mode = "file", Path = pending.Path };
                try
                {
                    if (!File.Exists(pending.Path))
                    {
                        result.Note = "no file written";
                    }
                    else
                    {
                        byte[] bytes = File.ReadAllBytes(pending.Path);
                        if (bytes.Length == 0)
                        {
                            result.Note = "file is empty";
                        }
                        else
                        {
                            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                            bool decoded = ImageConversion.LoadImage(tex, bytes);
                            if (!decoded)
                            {
                                result.Note = "LoadImage returned false";
                            }
                            else
                            {
                                result.Width = tex.width;
                                result.Height = tex.height;
                                result.Path = pending.Path;
                                result.Note = bytes.Length + " bytes";
                                WriteAndMeasure(tex, result, writePng: false);
                            }
                            Object.Destroy(tex);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    result.Note = "readback failed: " + e.Message;
                }

                _shots.Add(result);
            }
        }

        /// <summary>
        /// Encodes the texture to PNG and computes the statistics that make the screenshot a TEST
        /// rather than a file: a blank frame and a correct frame both produce a valid PNG, so the
        /// only thing that distinguishes them is the pixel content.
        ///
        ///   Mean  - average luminance; 0 means black.
        ///   Std   - spread; near 0 means a uniform fill (blank), regardless of its brightness.
        ///   Fingerprint - an 8x8 grid of cell luminance as hex. Comparing two fingerprints detects
        ///                 "the frame never changes", which a per-shot mean cannot.
        /// </summary>
        static void WriteAndMeasure(Texture2D tex, ShotResult result, bool writePng)
        {
            // The file variant was already encoded and written by Unity itself, so it only needs
            // measuring - re-encoding it here would rewrite the same bytes for no reason.
            if (writePng)
            {
                try
                {
                    File.WriteAllBytes(result.Path, tex.EncodeToPNG());
                }
                catch (System.Exception e)
                {
                    result.Note = "encode/write failed: " + e.Message;
                    return;
                }
            }

            result.Written = true;

            Color32[] px = tex.GetPixels32();
            double sum = 0;
            double sumSq = 0;
            for (int i = 0; i < px.Length; i++)
            {
                double lum = 0.299 * px[i].r + 0.587 * px[i].g + 0.114 * px[i].b;
                sum += lum;
                sumSq += lum * lum;
            }
            double n = px.Length > 0 ? px.Length : 1;
            double mean = sum / n;
            double variance = (sumSq / n) - (mean * mean);
            result.Mean = mean;
            result.Std = variance > 0 ? System.Math.Sqrt(variance) : 0;

            var sb = new StringBuilder(64);
            for (int gy = 0; gy < 8; gy++)
            {
                for (int gx = 0; gx < 8; gx++)
                {
                    int x0 = gx * tex.width / 8;
                    int x1 = System.Math.Max(x0 + 1, (gx + 1) * tex.width / 8);
                    int y0 = gy * tex.height / 8;
                    int y1 = System.Math.Max(y0 + 1, (gy + 1) * tex.height / 8);
                    double acc = 0;
                    int count = 0;
                    for (int y = y0; y < y1; y += 2)
                    {
                        for (int x = x0; x < x1; x += 2)
                        {
                            Color32 c = tex.GetPixel(x, y);
                            acc += 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
                            count++;
                        }
                    }
                    double cell = count > 0 ? acc / count : 0;
                    int bucket = Mathf.Clamp((int)(cell / 16.0), 0, 15);
                    sb.Append("0123456789abcdef"[bucket]);
                }
            }
            result.Fingerprint = sb.ToString();
        }

        // ── Report ───────────────────────────────────────────────────────────────────

        void Finish()
        {
            if (_finished) return;
            _finished = true;
            _wall.Stop();

            // Drain any file shots still in flight rather than losing them. One more frame is enough
            // because the capture was requested at least two frames ago by now in the common case;
            // anything still missing is reported as missing, which is itself a result.
            try
            {
                PumpFileShotReadbacks();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Scenario] final readback failed: {e.Message}");
            }

            if (_rt != null)
            {
                _rt.Release();
                Object.Destroy(_rt);
                _rt = null;
            }
            if (_rtReadback != null)
            {
                Object.Destroy(_rtReadback);
                _rtReadback = null;
            }

            string dir = DumpDirectory();
            string stamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string baseName = "scenario_" + _tag + "_" + stamp;

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
                Debug.LogError($"[Scenario] could not write report: {e.Message}");
            }

            // A player has no command-line way to exit itself (-quit is Editor-only). Without this
            // the process would have to be killed, and a killed process cannot be trusted to have
            // flushed its output.
            Application.Quit();
        }

        string BuildCsv()
        {
            var sb = new StringBuilder(_totalFrames * 48);
            sb.AppendLine("frame,unityFrame,phase,inputX,inputY,posX,posY,shot");
            for (int i = 0; i < _frame; i++)
            {
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_unityFrame[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_phase[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(_inputX[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_inputY[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_posX[i].ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_posY[i].ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                  .Append(_shotIndex[i].ToString(CultureInfo.InvariantCulture)).AppendLine();
            }
            return sb.ToString();
        }

        string BuildSummary()
        {
            var lines = new List<string>();
            lines.Add("=== PFE scenario test ===");
            lines.Add($"tag                : {_tag}");
            lines.Add($"move direction     : {_moveName}   (keys: {KeyList(_holdKeys)})");
            lines.Add($"warmup / measured  : {_warmupFrames} / {_measuredFrames}   (hold ends at frame {_holdEndFrame})");
            lines.Add($"frames recorded    : {_frame}");
            lines.Add($"ticks / exceptions : {_updates} / {_exceptions}   last={_lastException}");
            lines.Add($"probe wall time    : {_wall.Elapsed.TotalSeconds:F2} s for {_updates} ticks");
            lines.Add($"isDebugBuild       : {Debug.isDebugBuild}   (False makes every custom marker a silent zero)");
            lines.Add($"batchmode          : {Application.isBatchMode}");
            lines.Add($"screen             : {Screen.width}x{Screen.height}");
            lines.Add($"camera             : {(_camera == null ? "(none found)" : _camera.name)}");
            lines.Add($"player             : {(_player == null ? "(NOT FOUND)" : _player.name)}");
            lines.Add($"input reader       : {(_input == null ? "(NOT RESOLVED)" : "resolved")}");
            lines.Add("");

            // ── Movement ──
            lines.Add("--- movement ---");
            if (_player == null)
            {
                lines.Add("Q1 player resolved                                 : NO  (movement INCONCLUSIVE)");
            }
            else if (_input == null)
            {
                lines.Add("Q1 player resolved                                 : YES");
                lines.Add("Q2 InputReader resolved from container              : NO  - could not inject");
            }
            else if (_frame <= _holdEndFrame || _playerFirstFrame >= _holdEndFrame)
            {
                // Either the run ended before the hold phase finished, or the player only appeared
                // after it had already ended. Reported rather than indexed into, because an
                // IndexOutOfRange here would abort the report - and the report is the only output.
                lines.Add("Q1 player resolved                                 : YES");
                lines.Add("Q2 InputReader resolved from container              : YES");
                lines.Add($"Q3 hold window measurable                           : " +
                          $"INCONCLUSIVE - run ended at frame {_frame}, hold ends at {_holdEndFrame}, " +
                          $"player first seen at frame {_playerFirstFrame}");
            }
            else
            {
                Vector2 expected = ExpectedDirection(_moveName);
                // Baseline = the later of the scheduled settle end and the frame the player was
                // first resolved (see _playerFirstFrame). Differencing against a frame where the
                // player did not exist yet would report the spawn position as displacement.
                int baseline = _playerFirstFrame > _warmupFrames ? _playerFirstFrame : _warmupFrames;
                double holdDx = _posX[_holdEndFrame] - _posX[baseline];
                double holdDy = _posY[_holdEndFrame] - _posY[baseline];
                double releaseDx = _posX[_frame - 1] - _posX[_holdEndFrame];
                double releaseDy = _posY[_frame - 1] - _posY[_holdEndFrame];

                // Along the expected axis, so a diagonal player or a diagonal input does not muddy it.
                double holdAlong = holdDx * expected.x + holdDy * expected.y;
                double releaseAlong = releaseDx * expected.x + releaseDy * expected.y;

                int heldFrames = 0, liveFrames = 0;
                for (int i = baseline; i < _holdEndFrame; i++)
                {
                    heldFrames++;
                    if (Mathf.Abs(_inputX[i]) > 0.01f || Mathf.Abs(_inputY[i]) > 0.01f) liveFrames++;
                }

                lines.Add("Q1 player resolved                                 : YES");
                lines.Add("Q2 InputReader resolved from container              : YES");
                lines.Add($"Q3 live input non-zero while keys held              : " +
                          $"{(liveFrames > 0 ? "YES" : "NO")}   ({liveFrames}/{heldFrames} frames; " +
                          $"max |x|={MaxAbs(_inputX, baseline, _holdEndFrame):F3})");
                lines.Add($"Q4 player MOVED during hold                         : " +
                          $"{(holdAlong > 0.1 ? "YES" : "NO")}   ({holdAlong:F4} along {_moveName}; " +
                          $"dx={holdDx:F4} dy={holdDy:F4})");
                lines.Add($"   baseline / hold end / run end frames             : " +
                          $"{baseline} / {_holdEndFrame} / {_frame - 1}" +
                          (_playerFirstFrame > _warmupFrames
                              ? $"   (player first seen at frame {_playerFirstFrame})"
                              : ""));
                lines.Add($"   position at baseline / hold end / run end        : " +
                          $"({_posX[baseline]:F4},{_posY[baseline]:F4}) -> " +
                          $"({_posX[_holdEndFrame]:F4},{_posY[_holdEndFrame]:F4}) -> " +
                          $"({_posX[_frame - 1]:F4},{_posY[_frame - 1]:F4})");
                // Not a failure either way: a controller with momentum drifts, one without stops dead.
                // What WOULD be damning is moving further after release than during the hold, because
                // that means something other than the injected input is driving the player.
                bool releaseIsSane = System.Math.Abs(releaseAlong) <= System.Math.Abs(holdAlong) + 0.01;
                lines.Add($"Q5 after release, drift <= hold displacement        : " +
                          $"{(releaseIsSane ? "YES" : "NO")}   ({releaseAlong:F4} along {_moveName}; " +
                          $"INFO only - momentum is not a failure)");
                if (holdAlong <= 0.1)
                {
                    lines.Add("   NOTE: no displacement. Check the action map is enabled, the composite binding");
                    lines.Add("   keys match -pfe-scenario-move, and that the player is not blocked by geometry");
                    lines.Add("   (the player spawns into a room; a wall in the hold direction looks identical).");
                }
            }
            lines.Add("");

            // ── Screenshots ──
            lines.Add("--- screenshots ---");
            if (_shotCount == 0)
            {
                lines.Add("not requested");
            }
            else
            {
                lines.Add($"requested / produced                              : {_shotCount} / {_shots.Count}");
                // The rt variant completes via a coroutine and the file variant via a read-back, so
                // they arrive out of order; sort so the report reads shot by shot.
                _shots.Sort((a, b) => a.Index != b.Index
                    ? a.Index.CompareTo(b.Index)
                    : string.CompareOrdinal(a.Mode, b.Mode));
                var fingerprints = new List<string>();
                int nonBlank = 0;
                foreach (ShotResult s in _shots)
                {
                    if (s.NonBlank) nonBlank++;
                    if (!string.IsNullOrEmpty(s.Fingerprint)) fingerprints.Add(s.Fingerprint);
                    lines.Add($"  #{s.Index} {s.Mode,-4} " +
                              $"{(s.Written ? "written" : "FAILED ")} " +
                              $"{s.Width}x{s.Height} mean={s.Mean:F2} std={s.Std:F2} " +
                              $"{s.Path}" + (s.Note.Length > 0 ? "  [" + s.Note + "]" : ""));
                    if (s.Fingerprint.Length > 0) lines.Add($"        fp {s.Fingerprint}");
                }

                lines.Add($"Q6 at least one non-blank shot                      : " +
                          $"{(nonBlank > 0 ? "YES" : "NO")}   ({nonBlank} non-blank)");
                if (fingerprints.Count > 0)
                {
                    int distinct = 1;
                    for (int i = 1; i < fingerprints.Count; i++)
                    {
                        if (fingerprints[i] != fingerprints[i - 1]) distinct++;
                    }
                    lines.Add($"Q7 frames differ over the run                       : " +
                              $"{(distinct > 1 ? "YES" : "NO")}   ({distinct} distinct fingerprints " +
                              $"of {fingerprints.Count})");
                }
                if (nonBlank == 0)
                {
                    lines.Add("   NOTE: every shot was blank. If BOTH the rt and file variants are blank the");
                    lines.Add("   headless player is not rendering a frame - which is a real finding about the");
                    lines.Add("   harness, not about the game. See the rt-vs-file comparison above.");
                }
            }

            return string.Join("\n", lines);
        }

        static string KeyList(Key[] keys)
        {
            if (keys == null || keys.Length == 0) return "(none)";
            var parts = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++) parts[i] = keys[i].ToString();
            return string.Join("+", parts);
        }

        static float MaxAbs(float[] values, int from, int to)
        {
            float max = 0f;
            int end = Mathf.Min(to, values.Length);
            for (int i = Mathf.Max(0, from); i < end; i++)
            {
                float a = Mathf.Abs(values[i]);
                if (a > max) max = a;
            }
            return max;
        }

        // ── Resolution ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolved lazily and once, because at AfterSceneLoad the container may not be built yet -
        /// GameLifetimeScope builds in Awake, and a null container here would be a timing artefact
        /// rather than a wiring fault.
        /// </summary>
        void ResolveSceneObjects()
        {
            if (_player == null)
            {
                _player = Object.FindFirstObjectByType<PlayerController>();
                if (_player != null && _playerFirstFrame < 0) _playerFirstFrame = _frame;
            }
            if (_camera == null)
            {
                _camera = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
            }
            if (_input == null)
            {
                if (_scope == null) _scope = Object.FindFirstObjectByType<GameLifetimeScope>();
                if (_scope != null && _scope.Container != null)
                {
                    // NOTE: no explicit <InputReader> type argument here, deliberately.
                    // VContainer's IObjectResolver declares TryResolve as a NON-generic instance
                    // method - bool TryResolve(Type type, out object resolved, object key = null) -
                    // and exposes the generic form only as an EXTENSION method in
                    // IObjectResolverExtensions. Writing TryResolve<InputReader>(...) therefore binds
                    // against the instance member and fails with CS0308 ("cannot be used with type
                    // arguments"); the extension is never consulted. Letting the out-parameter infer
                    // T is what makes the extension applicable, and it is the idiom the rest of this
                    // project already uses (HudBootstrapper.cs:335, DeveloperConsoleController.cs:132).
                    if (_scope.Container.TryResolve(out InputReader reader)) _input = reader;
                }
            }
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "scenario";
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
            return Path.Combine(Application.dataPath, "..", "ProfilerCaptures", "scenario");
#else
            return Path.Combine(Application.persistentDataPath, "ProfilerCaptures", "scenario");
#endif
        }
    }
}
