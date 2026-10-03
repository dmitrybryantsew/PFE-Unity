using System.Collections.Generic;
using UnityEngine;
using PFE.Core;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Draws a rising, fading figure at every resolved hit — the port's answer to AS3's
    /// <c>numbEmit</c> damage numbers (<c>Unit.as:4103-4117</c>).
    ///
    /// <para><b>What it is for.</b> A bullet that <i>misses</i> and a bullet that <i>never collided</i>
    /// look identical on screen, and they have completely different causes — one is the evasion roll,
    /// the other is the hit path. This overlay separates them: a landed hit draws a number, an evaded
    /// hit draws <c>MISS</c>, and a shot that passes through a unit draws <b>nothing at all</b>. That
    /// third case is the one worth having a tool for, and it is invisible without this.</para>
    ///
    /// <para><b>Why IMGUI rather than a TextMesh prefab.</b> Every other overlay in this project draws
    /// through <c>OnGUI</c>, which needs no font asset, no prefab and no scene wiring — so the tool is
    /// available in a bare test scene and cannot be broken by a missing reference.
    /// <c>FloatingTextManager</c> is the prefab-based attempt at this and it is inert for exactly that
    /// reason: its <c>[SerializeField]</c> prefab has nothing to assign it. This class is the working
    /// renderer.</para>
    ///
    /// <para><b>Self-bootstrapping, and gated by the channel rather than by a reference.</b> See
    /// <see cref="DebugOverlays"/>: a serialized reference cannot be switched off, which is how three
    /// earlier readouts ended up drawing unconditionally.</para>
    /// </summary>
    public sealed class FloatingDamageOverlay : MonoBehaviour
    {
        /// <summary>Live instance, if the scene-load hook has run.</summary>
        public static FloatingDamageOverlay Instance { get; private set; }

        /// <summary>
        /// Cap on figures drawn per frame. The feed holds 64; a burst can legitimately fill it, and a
        /// screen of overlapping numbers is less readable than the last few.
        /// </summary>
        private const int MaxDrawn = 48;

        private const int BodyFontSize = 13;
        private const int CritFontSize = 19;

        private readonly List<DamageNumber> _visible =
            new List<DamageNumber>(DamageEventFeed.DefaultCapacity);

        private DamageEventFeed _feed;

        private GUIStyle _body;
        private GUIStyle _crit;
        private GUIStyle _readout;

        private int _drawn;
        private int _offscreen;

        // ── Lifecycle ────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeInstance()
        {
            if (Instance == null && FindFirstObjectByType<FloatingDamageOverlay>() == null)
            {
                var go = new GameObject("FloatingDamageOverlay");
                Instance = go.AddComponent<FloatingDamageOverlay>();
                DontDestroyOnLoad(go);
            }
        }

        /// <summary>Get the overlay, creating it if the scene-load bootstrap did not run.</summary>
        public static FloatingDamageOverlay EnsureInstance()
        {
            if (Instance != null) return Instance;

            var found = FindFirstObjectByType<FloatingDamageOverlay>();
            if (found != null)
            {
                Instance = found;
                return found;
            }

            var go = new GameObject("FloatingDamageOverlay");
            Instance = go.AddComponent<FloatingDamageOverlay>();
            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            if (Application.isPlaying) DontDestroyOnLoad(gameObject);

            _feed = DamageEventFeed.Default;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Age the window every frame, <b>whether or not the channel is on</b>.
        ///
        /// <para>Aging only while visible would make the buffer a log of everything since the overlay
        /// was last enabled: switch it on mid-fight and the last sixty-four hits appear stacked at
        /// once, all at age zero. Aging always keeps it a rolling 0.9-second window, so enabling it
        /// shows the burst that is happening — which is the shot the user enabled it to catch. The cost
        /// is 64 struct copies per frame.</para>
        /// </summary>
        private void Update()
        {
            _feed.AdvanceAndSnapshot(Time.unscaledDeltaTime, _visible);
        }

        // ── Drawing ──────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (!DebugOverlays.IsOn(DebugOverlayChannel.DamageNumbers)) return;

            EnsureStyles();

            Camera cam = Camera.main;
            if (cam == null) return;

            _drawn = 0;
            _offscreen = 0;

            for (int i = 0; i < _visible.Count; i++)
            {
                if (_drawn >= MaxDrawn) break;

                DamageNumber number = _visible[i];

                // Rise is a world-space offset, so a number climbs at the same apparent speed
                // regardless of distance — an offset applied in screen space would make a distant hit
                // crawl and a near one fly.
                float rise = DamageEventFeed.RiseOffset(number.Age, _feed.Lifetime);
                Vector3 world = number.Position + Vector3.up * rise;

                Vector3 screen = cam.WorldToScreenPoint(world);
                if (screen.z <= 0f)
                {
                    // Behind the camera. Counting these separately rather than silently skipping: a
                    // large number here means the impact point is not where the shot looked like it
                    // landed, which is itself a finding.
                    _offscreen++;
                    continue;
                }

                DrawFigure(number, screen);
                _drawn++;
            }

            DrawReadout();
        }

        private void DrawFigure(in DamageNumber number, Vector3 screen)
        {
            float alpha = DamageEventFeed.Alpha(number.Age, _feed.Lifetime);

            GUIStyle style = number.IsCritical ? _crit : _body;
            Color tint = ColorFor(number);

            // IMGUI's y axis runs down from the top; WorldToScreenPoint's runs up from the bottom.
            var rect = new Rect(screen.x - 50f, Screen.height - screen.y - 14f, 100f, 28f);

            Color previous = GUI.color;

            // Shadow first. These numbers are drawn over the game, which is arbitrary-coloured and
            // often dark; a single-pixel dark copy underneath is what makes white text legible on a
            // bright tile without a plate behind every figure. The legend needs a plate because it is
            // one long line; a per-figure plate would be more ink than number.
            GUI.color = new Color(0f, 0f, 0f, alpha * 0.75f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height),
                      DamageEventFeed.TextFor(number), style);

            GUI.color = new Color(tint.r, tint.g, tint.b, alpha);
            GUI.Label(rect, DamageEventFeed.TextFor(number), style);

            GUI.color = previous;
        }

        private static Color ColorFor(in DamageNumber number)
        {
            if (number.CustomColor.HasValue) return number.CustomColor.Value;
            if (number.IsMiss) return new Color(0.72f, 0.72f, 0.78f);   // grey: nothing happened
            if (number.IsCritical) return new Color(1f, 0.36f, 0.24f);  // red: crit

            // A landed hit that did no damage is the signature of armour absorbing everything, so it
            // gets its own colour rather than reading as a small white number.
            if (number.Amount <= 0f) return new Color(1f, 0.85f, 0.2f);

            return new Color(1f, 0.97f, 0.85f);
        }

        /// <summary>
        /// A one-line readout, so a screenshot is unambiguous about what was on and how much was
        /// actually drawn.
        /// </summary>
        /// <remarks>
        /// Bottom-right. Bottom-left is <c>ColliderDebugOverlay</c>, top-right is <c>SimDebugOverlay</c>
        /// and the console button, and top-left is <c>RoomStreamingManager</c>; the bottom-right corner
        /// is the one nothing else claims.
        /// </remarks>
        private void DrawReadout()
        {
            const float width = 300f;
            const float height = 20f;

            var box = new Rect(Screen.width - width - 8f, Screen.height - height - 8f, width, height);

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = previous;

            string text = $"<b>[damage]</b> live {_visible.Count}, drawn {_drawn}" +
                          $", window {_feed.Lifetime:0.00}s" +
                          (_offscreen > 0 ? $", <color=#ff4040>{_offscreen} behind camera</color>" : "");

            GUI.Label(new Rect(box.x + 6f, box.y + 2f, width - 12f, height - 4f), text, _readout);
        }

        private void EnsureStyles()
        {
            if (_body != null) return;

            _body = MakeFigureStyle(BodyFontSize, FontStyle.Normal);
            _crit = MakeFigureStyle(CritFontSize, FontStyle.Bold);
            _readout = MakeFigureStyle(12, FontStyle.Normal);
            _readout.alignment = TextAnchor.MiddleLeft;
        }

        private static GUIStyle MakeFigureStyle(int size, FontStyle fontStyle)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = fontStyle,
                alignment = TextAnchor.MiddleCenter,
                richText = true,
                wordWrap = false
            };

            // Forced white, then tinted through GUI.color per figure. The runtime IMGUI skin's default
            // label colour is dark, which on a dark game is the one combination that cannot be read.
            style.normal.textColor = Color.white;
            style.hover.textColor = Color.white;
            style.active.textColor = Color.white;

            return style;
        }
    }
}
