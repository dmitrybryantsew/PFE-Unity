using UnityEngine;

namespace PFE.Core
{
    /// <summary>
    /// Minimal IMGUI readout of <see cref="SimLoop"/> timing. Created at runtime by
    /// <see cref="SimLoop.Start"/> only when <c>PfeDebugSettings.SimTickOverlayEnabled</c> is on,
    /// so it costs nothing in a normal build and requires no scene setup.
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
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimDebugOverlay : MonoBehaviour
    {
        private const int Width = 268;
        private const int LineHeight = 16;

        private SimLoop _loop;

        private GUIStyle _style;
        private Texture2D _background;

        /// <summary>Points the overlay at the loop it reports on. Called once by <see cref="SimLoop"/>.</summary>
        public void Bind(SimLoop loop)
        {
            _loop = loop;
        }

        private void OnGUI()
        {
            if (_loop == null)
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
                "consumers  " + m.TickableCount
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
