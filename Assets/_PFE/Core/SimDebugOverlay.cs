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

        // Tickable-cost measurement. Armed only while this overlay's channel is on, so the per-tick
        // Stopwatch cost is paid only when someone is reading the number. The report is a rolling
        // window: the table is Reset after each snapshot, which keeps "total" and "avg" meaningful
        // (a cumulative table only ever shows the session average, and a spike 10 seconds old is
        // invisible inside it).
        private bool _costsArmed;
        private string _costReport;
        private double _costReportAtMs;
        private double _windowDispatchMs;
        private int _windowFrames;

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

            // Sum the dispatch cost for the cost window. LastFrameTickMs is the whole frame's
            // tickable dispatch total (SimLoop accumulates it across catch-up ticks and clears it
            // once per frame), and it is zero while the table is disabled — which it is unless this
            // overlay armed it — so an unarmed overlay adds one double add per frame.
            if (_costsArmed && _loop != null)
            {
                _windowDispatchMs += _loop.LastFrameTickMs;
                _windowFrames++;
            }

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

            // Arm the tickable cost table exactly while this channel is shown. This must run BEFORE
            // the visibility gate below: when the channel turns off, OnGUI returns at that gate, so
            // logic placed after it would never see the transition and the per-tick Stopwatches would
            // stay armed forever. Writing only on transitions — not every frame — keeps this overlay
            // from fighting a perf fixture that enables the same table for its own measurement.
            bool channelOn = DebugOverlays.IsOn(DebugOverlayChannel.Clock);
            if (channelOn != _costsArmed)
            {
                _costsArmed = channelOn;
                _loop.TickableCosts.Enabled = channelOn;
                _windowDispatchMs = 0.0;
                _windowFrames = 0;
                if (!channelOn) _costReport = null;
            }

            // The channel gate, so `col off clock` hides this without destroying it. Resolved per
            // frame rather than cached: a cached answer is a second copy of the state, and a debug
            // overlay that disagrees with its own toggle is worse than no overlay. SimLoop still
            // keeps measuring frame time while hidden, which costs one accumulator and means the fps
            // figure is already warm when the readout comes back.
            if (!channelOn)
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

            DrawCostReport(new Rect(rect.x, rect.yMax + 8, 640f, 400f));
        }

        /// <summary>The rolling per-tickable cost window. The expensive half (the Stopwatch per
        /// tickable per tick) runs inside SimLoop and is gated by <see cref="TickableCostTable.Enabled"/>;
        /// this method only formats what has already been measured. The window is half a second, and
        /// the table is Reset when each snapshot is taken, so a fresh spike shows up within a second
        /// instead of being averaged into a cumulative total.</summary>
        private void DrawCostReport(Rect rect)
        {
            if (!_costsArmed) return;
            if (_loop == null) return;

            double nowMs = UnityEngine.Time.unscaledTimeAsDouble * 1000.0;
            if (_costReport == null || nowMs - _costReportAtMs >= 500.0)
            {
                _costReportAtMs = nowMs;
                _costReport = BuildCostWindowText(_loop.TickableCosts, _windowDispatchMs, _windowFrames);
                _loop.TickableCosts.Reset();
                _windowDispatchMs = 0.0;
                _windowFrames = 0;
            }
            if (_costReport == null) return;

            string[] reportLines = _costReport.Split('\n');
            var boxRect = new Rect(rect.x, rect.y, rect.width, reportLines.Length * LineHeight + 10);
            GUI.DrawTexture(boxRect, _background);
            for (int i = 0; i < reportLines.Length; i++)
            {
                var lineRect = new Rect(boxRect.x + 8, boxRect.y + 5 + i * LineHeight, boxRect.width - 16, LineHeight);
                GUI.Label(lineRect, reportLines[i], _style);
            }
        }

        private static string BuildCostWindowText(PFE.Core.Profiling.TickableCostTable table,
            double dispatchMs, int frames)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("TICKABLE COSTS  (").Append(frames).Append(" frame")
              .Append(frames == 1 ? "" : "s").Append(", rolling ~0.5s)");
            if (table.InvalidatedSinceReset)
            {
                sb.Append("  !! tickables registered mid-window — totals cover only the settled tail");
            }
            sb.Append('\n');
            sb.Append("type".PadRight(36)).Append("inst".PadLeft(6))
              .Append("calls".PadLeft(8)).Append("total ms".PadLeft(11))
              .Append("avg ms".PadLeft(9)).Append("max ms".PadLeft(9)).Append('\n');

            double accounted = table.TotalMs;
            var entries = table.Snapshot();
            int shown = 0;
            for (int i = 0; i < entries.Count && shown < 10; i++)
            {
                var e = entries[i];
                sb.Append(e.Name.PadRight(36))
                  .Append(e.Instances.ToString().PadLeft(6))
                  .Append(e.Calls.ToString().PadLeft(8))
                  .Append(e.TotalMs.ToString("F1").PadLeft(11))
                  .Append(e.AverageMs.ToString("F3").PadLeft(9))
                  .Append(e.MaxMs.ToString("F3").PadLeft(9))
                  .Append('\n');
                shown++;
            }

            double perFrame = frames > 0 ? dispatchMs / frames : 0.0;
            sb.Append("dispatch ").Append(dispatchMs.ToString("F1")).Append(" ms across ")
              .Append(frames).Append(" frame").Append(frames == 1 ? "" : "s")
              .Append(" (").Append(perFrame.ToString("F2")).Append(" ms/frame of tickables; ")
              .Append("accounted ").Append(accounted.ToString("F1")).Append(" ms)");
            return sb.ToString();
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
