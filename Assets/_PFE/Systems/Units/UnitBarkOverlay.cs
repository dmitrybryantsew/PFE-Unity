using UnityEngine;

namespace PFE.Systems.Units
{
    /// <summary>
    /// Draws the live speech bubbles — the port's renderer for AS3's <c>replic</c> particle.
    ///
    /// <para><b>What the oracle actually draws.</b> Not a bubble: <c>visReplic</c> is a 404×21 plate of
    /// <b>yellow text with a black outline</b> and no background (sampled from the extracted symbol:
    /// <c>#FFCC00</c> on black, transparent elsewhere). It is cast at the unit's position minus 110 px,
    /// scaled by <c>1 / cam.scaleV</c> so it holds a constant on-screen size, and held for 100 frames
    /// before a nine-frame fade. This overlay reproduces those four facts and nothing else — a plate,
    /// a drop shadow or a tail would all be invented.</para>
    ///
    /// <para><b>Why IMGUI.</b> The port has no in-world text renderer: <c>TextMeshPro</c> is referenced
    /// by no gameplay assembly, and the two text-emitting particle rows have no sprite frames, which is
    /// why <c>FloatingDamageOverlay</c> draws through <c>OnGUI</c> for the same reason. It needs no font
    /// asset, no prefab and no scene wiring, so it also works in a bare test scene.</para>
    ///
    /// <para><b>Always on, unlike the debug overlays.</b> Enemy speech is game content, not a
    /// diagnostic — the oracle shows it in every build. <see cref="Enabled"/> exists so a screenshot or
    /// a performance capture can silence it without touching the simulation.</para>
    /// </summary>
    public sealed class UnitBarkOverlay : MonoBehaviour
    {
        /// <summary>Live instance, if the scene-load hook has run.</summary>
        public static UnitBarkOverlay Instance { get; private set; }

        /// <summary>
        /// Master switch. Speech is on by default — it is content, not a debug channel — and this is
        /// the one lever a capture or a stress test needs.
        /// </summary>
        public static bool Enabled = true;

        /// <summary>
        /// Bubbles drawn per frame. The feed holds 32; a screen of overlapping taunts is less readable
        /// than the newest few, and each one costs nine label draws for the outline.
        /// </summary>
        private const int MaxDrawn = 16;

        /// <summary>
        /// The oracle's text height is 21 px for a 404 px plate at 1:1; 13 pt is the same optical size
        /// as the damage numbers and keeps a 63-character line inside a 520 px box.
        /// </summary>
        private const int FontSize = 13;

        /// <summary>Half-width of the centred label box, in screen pixels.</summary>
        private const float HalfWidth = 260f;

        /// <summary>
        /// The sampled text colour — <c>#FFCC00</c>, the dominant opaque pixel of
        /// <c>DefineSprite_2868_visReplic/1.png</c>.
        /// </summary>
        private static readonly Color TextColor = new Color(1f, 0.8f, 0f, 1f);

        private GUIStyle _style;
        private int _drawn;

        // ── Lifecycle ────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeInstance()
        {
            if (Instance == null && FindFirstObjectByType<UnitBarkOverlay>() == null)
            {
                var go = new GameObject("UnitBarkOverlay");
                Instance = go.AddComponent<UnitBarkOverlay>();
                DontDestroyOnLoad(go);
            }
        }

        /// <summary>Get the overlay, creating it if the scene-load bootstrap did not run.</summary>
        public static UnitBarkOverlay EnsureInstance()
        {
            if (Instance != null) return Instance;

            var found = FindFirstObjectByType<UnitBarkOverlay>();
            if (found != null)
            {
                Instance = found;
                return found;
            }

            var go = new GameObject("UnitBarkOverlay");
            Instance = go.AddComponent<UnitBarkOverlay>();
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
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Age the queue every frame, <b>whether or not the overlay is enabled</b> — same rule and same
        /// reason as <c>FloatingDamageOverlay.Update</c>: ageing only while visible would turn the
        /// queue into a log that replays every bark since the switch was flipped.
        /// </summary>
        private void Update()
        {
            UnitBarkFeed.Advance(Time.unscaledDeltaTime);
        }

        // ── Drawing ──────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (!Enabled) return;

            int count = UnitBarkFeed.Count;
            if (count == 0) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            EnsureStyle();

            _drawn = 0;

            // Newest first: when the cap bites, the line the player just triggered is the one that
            // must survive, and the queue is oldest-first.
            for (int i = count - 1; i >= 0 && _drawn < MaxDrawn; i--)
            {
                UnitBark bark = UnitBarkFeed.At(i);

                Vector3 screen = cam.WorldToScreenPoint(bark.Anchor);
                if (screen.z <= 0f) continue;

                DrawBark(bark, screen);
                _drawn++;
            }
        }

        private void DrawBark(in UnitBark bark, Vector3 screen)
        {
            float alpha = UnitBarkFeed.Alpha(bark);

            // IMGUI's y runs down from the top; WorldToScreenPoint's runs up from the bottom.
            float x = Mathf.Clamp(screen.x, HalfWidth, Mathf.Max(HalfWidth, Screen.width - HalfWidth));
            float y = Screen.height - screen.y;

            var rect = new Rect(x - HalfWidth, y - 10f, HalfWidth * 2f, 22f);

            Color previous = GUI.color;

            // The oracle's outline is a full eight-direction black stroke, not a drop shadow: the text
            // is drawn over arbitrary room art and yellow on a bright tile is unreadable without it.
            GUI.color = new Color(0f, 0f, 0f, alpha);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    GUI.Label(new Rect(rect.x + dx, rect.y + dy, rect.width, rect.height), bark.Text, _style);
                }
            }

            GUI.color = new Color(TextColor.r, TextColor.g, TextColor.b, alpha);
            GUI.Label(rect, bark.Text, _style);

            GUI.color = previous;
        }

        private void EnsureStyle()
        {
            if (_style != null) return;

            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = FontSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                richText = false,
                wordWrap = false
            };

            // Forced white, then tinted through GUI.color. The runtime IMGUI skin's default label
            // colour is dark, which on a dark game is the one combination that cannot be read.
            _style.normal.textColor = Color.white;
            _style.hover.textColor = Color.white;
            _style.active.textColor = Color.white;
        }
    }
}
