using System.Collections.Generic;
using UnityEngine;
using PFE.Core;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Draws a rising figure at every resolved hit — the port's answer to AS3's
    /// <c>numbEmit</c> damage numbers (<c>Unit.as:3906-3961</c>).
    ///
    /// <para><b>What it is for.</b> A bullet that <i>misses</i> and a bullet that <i>never collided</i>
    /// look identical on screen, and they have completely different causes — one is the evasion roll,
    /// the other is the hit path. This overlay separates them: a landed hit draws a number, an evaded
    /// hit draws <c>MISS</c>, and a shot that passes through a unit draws <b>nothing at all</b>. That
    /// third case is the one worth having a tool for, and it is invisible without this.</para>
    ///
    /// <para><b>It draws what the game draws, not a per-hit column.</b> The figure accumulates while
    /// hits keep landing on the same unit — that is AS3's default (<c>World.showHit = 2</c>) and it is
    /// <see cref="DamageEventFeed"/>'s default too. The rule that reads oddly at first is the
    /// <b>ten-frame window</b>: ten frames after the last hit the sum is forgotten, but the figure stays
    /// on screen for another fifty, so a burst leaves a lingering total behind and the next burst starts
    /// a second figure over it (<c>Unit.as:3222-3233</c> vs <c>:3957</c>).</para>
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

        /// <summary>
        /// A crit's font size. <c>13 × 1.6 = 20.8</c>, which is the oracle's own <c>scale: 1.6</c>
        /// (<c>Unit.as:3935</c>) applied to the body size rather than a second, unrelated number.
        /// </summary>
        private const int CritFontSize = 21;

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
        /// once, all at age zero. Aging always keeps it a rolling two-second window, so enabling it
        /// shows the burst that is happening — which is the shot the user enabled it to catch. The cost
        /// is 64 struct copies per frame.</para>
        ///
        /// <para><b>It also ages the accumulate windows, and that is not optional.</b> The window is
        /// what decides whether the next hit adds to a figure or starts a new one; if it only counted
        /// down while the overlay was visible, switching the overlay on would leave every unit
        /// permanently accumulating into one number that never resets.</para>
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
                float rise = DamageEventFeed.RiseOffsetWorld(number.Age);
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

            // AS3 casts a miss at half strength (`"alpha": 0.5`, `Unit.as:4118`); everything else is
            // opaque for its whole life, because the `numb` emitter does not fade.
            if (number.IsMiss) alpha *= DamageEventFeed.MissOpacity;

            // Scale, not a bool: an accumulated figure that took a crit stays enlarged for the rest of
            // its life, which is AS3's `hitPart.vis.scaleX = 1.6 / cam.scaleV` on an update (`:3954`).
            GUIStyle style = number.Scale > 1f ? _crit : _body;
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

            // A landed hit that did no damage is the signature of armour absorbing everything, so it
            // gets its own colour rather than reading as a small orange number. AS3 prints nothing at
            // all for one of these (`param1 > 0.5`, `Unit.as:3683`) — see
            // DamageEventFeed.MinPrintedDamage for why the diagnostic keeps it.
            if (!number.IsMiss && number.Amount <= 0f) return new Color(1f, 0.85f, 0.2f);

            // Everything else takes the oracle's own tint, sampled from its `visualNumb` frames: orange
            // for a hit on a monster, red for the player or a stealth crit, grey for a miss, and so on.
            return DamageEventFeed.ColorFor(number.Frame);
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
            const float width = 380f;
            const float height = 20f;

            var box = new Rect(Screen.width - width - 8f, Screen.height - height - 8f, width, height);

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = previous;

            string text = $"<b>[damage]</b> {StyleName(_feed.Style)}, live {_visible.Count}" +
                          $", {_feed.AccumulatingCount} adding up, drawn {_drawn}" +
                          (_offscreen > 0 ? $", <color=#ff4040>{_offscreen} behind camera</color>" : "");

            GUI.Label(new Rect(box.x + 6f, box.y + 2f, width - 12f, height - 4f), text, _readout);
        }

        /// <summary>
        /// The live mode, in the readout's words. The mode is the thing a screenshot cannot otherwise
        /// show: the same three hits look completely different in <c>adding up</c> and
        /// <c>one per hit</c>, and a reader has no way to tell which they are looking at.
        /// </summary>
        private static string StyleName(HitNumberStyle style)
        {
            switch (style)
            {
                case HitNumberStyle.Off: return "off";
                case HitNumberStyle.PerHit: return "one per hit";
                default: return "adding up";
            }
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
