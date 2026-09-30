using UnityEngine;

namespace PFE.Core
{
    /// <summary>
    /// Minimal IMGUI readout of <see cref="SimLoop"/> timing. Created at runtime by
    /// <see cref="SimLoop"/> the first time <see cref="DebugOverlayChannel.Clock"/> is on — at startup
    /// if it was already on, otherwise the first tick after `col on clock` — so it costs nothing in a
    /// normal build and requires no scene setup. It draws only while that channel is on.
    ///
    /// <para>The channel is what the console's <c>col on clock</c> reaches; it is the same mask as
    /// every other overlay, so <c>col on all</c> includes this readout and <c>col off</c> removes it.</para>
    ///
    /// <para>This is the primary verification instrument for the P1 tick migration. What to look for:</para>
    /// <list type="bullet">
    ///   <item><description><b>Rate</b> should sit at the configured value (30.0 at canonical). If it
    ///     reads ~2× the display refresh rate, ticks are being driven from the wrong place.</description></item>
    ///   <item><description><b>Alpha</b> must sweep the full 0&#8594;1 range. A value pinned near 0 or 1
    ///     means the accumulator is not being drained and the view cannot interpolate.</description></item>
    ///   <item><description><b>Dropped</b> must stay 0. A non-zero count means frames are overrunning
    ///     the catch-up budget — the sim is losing ticks.</description></item>
    ///   <item><description><b>Ticks</b> (this frame) should be 1 on a healthy 30 Hz sim on a 60 Hz
    ///     display, alternating 0/1/2 with occasional 2s.</description></item>
    ///   <item><description><b>FPS</b> is measured over the same 60-frame window as the tick rate, so
    ///     the two numbers are directly comparable. This is the reading to quote when asking whether
    ///     a change cost frames: it is a real average over 60 frames, not an instantaneous sample, and
    ///     it is independent of the tick rate — a sim that drops ticks and a renderer that drops frames
    ///     are different faults, and only this line distinguishes them.</description></item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimDebugOverlay : MonoBehaviour
    {
        private const int Width = 268;
        private const int LineHeight = 16;

        /// <summary>Frames per averaged frame-time sample. Matches SimLoop's rate window so the two
        /// readings cover the same stretch of time.</summary>
        private const int FrameWindow = 60;

        private SimLoop _loop;

        private GUIStyle _style;
        private Texture2D _background;

        // Frame timing. Measured in Update, never in OnGUI: OnGUI runs twice per frame (Layout then
        // Repaint), so sampling there would halve the reported frame time.
        private int _frameCount;
        private float _frameSeconds;
        private float _averageFps;
        private float _averageFrameMs;

        /// <summary>Points the overlay at the loop it reports on. Called once by <see cref="SimLoop"/>.</summary>
        public void Bind(SimLoop loop)
        {
            _loop = loop;
        }

        private void Update()
        {
            // UnityEngine.Time, fully qualified: this file lives in namespace PFE.Core, which also
            // contains the namespace PFE.Core.Time (UnityTimeProvider.cs). Inside PFE.Core a bare
            // `Time` binds to that NAMESPACE, not to UnityEngine.Time, giving
            // CS0234 "the type or namespace name 'unscaledDeltaTime' does not exist in the namespace
            // 'PFE.Core.Time'". SimLoop.cs:186 already qualifies it for the same reason.
            _frameSeconds += UnityEngine.Time.unscaledDeltaTime;
            _frameCount++;

            if (_frameCount < FrameWindow)
            {
                return;
            }

            _averageFrameMs = _frameSeconds / _frameCount * 1000f;
            _averageFps = _frameCount / Mathf.Max(_frameSeconds, 1e-5f);

            _frameCount = 0;
            _frameSeconds = 0f;
        }

        private void OnGUI()
        {
            if (_loop == null)
            {
                return;
            }

            // The channel gate, so `col off clock` hides this without destroying it. Resolved per
            // frame rather than cached: a cached answer is a second copy of the state, and a debug
            // overlay that disagrees with its own toggle is worse than no overlay. SimLoop still
            // keeps measuring frame time while hidden, which costs one accumulator and means the fps
            // figure is already warm when the readout comes back.
            if (!DebugOverlays.IsOn(DebugOverlayChannel.Clock))
            {
                return;
            }

            EnsureStyle();

            SimMetrics m = _loop.Metrics;

            string[] lines =
            {
                "SIM CLOCK",
                "rate       " + m.TicksPerSecond + " Hz" + (m.TicksPerSecond == SimClock.CanonicalTicksPerSecond ? "  (canonical)" : "  scale " + _loop.Clock.StepScale.ToString("0.###")),
                "measured   " + m.AverageTicksPerSecond.ToString("0.0") + " ticks/s",
                "tick index " + m.TickIndex,
                "ticks/frame" + m.TicksThisFrame,
                "accum      " + (m.Accumulator * 1000f).ToString("0.0") + " ms",
                "alpha      " + m.Alpha.ToString("0.00"),
                "dropped    " + m.DroppedTicks,
                "consumers  " + m.TickableCount,
                "fps        " + _averageFps.ToString("0.0") + "  (" + _averageFrameMs.ToString("0.0") + " ms)"
            };

            int height = lines.Length * LineHeight + 10;
            var rect = new Rect(Screen.width - Width - 12, 12, Width, height);

            GUI.DrawTexture(rect, _background);

            for (int i = 0; i < lines.Length; i++)
            {
                var lineRect = new Rect(rect.x + 8, rect.y + 5 + i * LineHeight, Width - 16, LineHeight);
                GUI.Label(lineRect, lines[i], _style);
            }
        }

        private void EnsureStyle()
        {
            if (_style != null)
            {
                return;
            }

            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                richText = false
            };
            _style.normal.textColor = new Color(0.85f, 0.95f, 0.85f, 1f);

            _background = new Texture2D(1, 1);
            _background.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
            _background.Apply();
        }

        private void OnDestroy()
        {
            if (_background != null)
            {
                Destroy(_background);
                _background = null;
            }
        }
    }
}
