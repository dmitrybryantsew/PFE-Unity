using PFE.Entities.Pets;
using UnityEngine;
using UnityEngine.UI;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Oracle <c>vis.hpPet</c> — the companion health bar. The port of <c>GUI.setPet</c>
    /// (<c>GUI.as:1086-1113</c>), fed by <see cref="PFE.Entities.Pets.PetHealthBarFeed"/>.
    ///
    /// <para><b>What it draws, and what it deliberately does not.</b> The oracle's widget is a frame,
    /// a fill, an icon and a countdown label; the port has no imported art for <c>vis.hpPet</c> (the
    /// boss bar's symbol was in the interface composition, this one is not), so the frame and the fill
    /// are flat <see cref="Image"/> colours, the same fallback the boss bar takes when its sprite is
    /// missing. The label is real: it is <c>floor(noPet / 30)</c> seconds, which is the number a player
    /// actually reads while waiting for the phoenix.</para>
    ///
    /// <para><b>Hidden when the pet is dead.</b> <c>GUI.setPet</c> hides the bar on <c>hp &lt;= 0</c>
    /// (<c>:1090-1093</c>) and hides the icon while <c>noPet &gt; 0</c>. A dead phoenix therefore shows a
    /// countdown and no bar, which is exactly the oracle's picture.</para>
    ///
    /// <para><b>Sim tick records, frame draws.</b> <see cref="Show"/> and <see cref="Hide"/> only store
    /// the value and set a dirty flag; <see cref="LateUpdate"/> does the rect and <c>SetActive</c> writes.
    /// Same split as <see cref="BossHealthBarView"/>, and for the same reason: a 30 Hz tick must not
    /// dirty the canvas mesh. The toggled object is a <b>child</b>, so deactivating it cannot stop this
    /// component's own <c>LateUpdate</c>.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PetHealthBarView : MonoBehaviour
    {
        /// <summary>Frame width in reference-canvas pixels. A companion bar, not a boss bar.</summary>
        public const float BarWidth = 120f;

        /// <summary>Frame height.</summary>
        public const float BarHeight = 10f;

        /// <summary>The frame's stroke — 1 px, the same as every other port-drawn plate.</summary>
        public const float BorderThickness = 1f;

        /// <summary>The fill's width: the frame less its stroke on each side.</summary>
        public const float FillWidth = BarWidth - 2f * BorderThickness;

        /// <summary>The fill's height.</summary>
        public const float FillHeight = BarHeight - 2f * BorderThickness;

        /// <summary>The fill's colour — a companion bar reads green in the original's icon set.</summary>
        public static readonly Color FillColour = new Color32(90, 200, 120, 255);

        /// <summary>The frame's colour, the same red the boss bar uses.</summary>
        public static readonly Color BorderColour = new Color32(255, 0, 0, 255);

        /// <summary>
        /// The fill is <b>left-anchored</b>, unlike the boss bar.
        ///
        /// <para>The boss bar's fill is centred because <c>GUI.hpBarBoss</c> writes
        /// <c>bar.scaleX</c> on a clip registered at the symbol's own origin. The pet bar's fill is a
        /// plain rect in this port (no imported symbol to scale), and a bar that drains from the middle
        /// would read as a boss bar — so it drains from the left, which is what the oracle's own
        /// <c>vis.hpPet.bar</c> does with a left-aligned child.</para>
        /// </summary>
        public static readonly Vector2 FillAnchor = new Vector2(0f, 0.5f);

        private RectTransform _fillRect;
        private GameObject _content;
        private Text _label;
        private HudLayoutScaler _layout;

        private float _pendingFraction;
        private float _pendingCooldown;
        private bool _dirty;

        /// <summary>True when the bar is currently drawn. For tests and inspection.</summary>
        public bool IsShown { get; private set; }

        /// <summary>The last fraction drawn, 0&#8594;1. For tests and inspection.</summary>
        public float LastFraction { get; private set; }

        /// <summary>The last cooldown printed, in whole seconds. For tests and inspection.</summary>
        public int LastCooldownSeconds { get; private set; }

        /// <summary>Build the bar under this component's own rect. Called once by the HUD bootstrapper.</summary>
        public void Build()
        {
            if (_content != null) return;

            _content = new GameObject("Content", typeof(RectTransform));
            var contentRect = (RectTransform)_content.transform;
            contentRect.SetParent(transform, false);
            contentRect.anchorMin = contentRect.anchorMax = new Vector2(0.5f, 0.5f);
            contentRect.pivot = new Vector2(0.5f, 0.5f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(BarWidth, BarHeight);

            BuildFill(contentRect);
            BuildBorder(contentRect);
            BuildLabel(contentRect);

            _content.SetActive(false);
            IsShown = false;
        }

        /// <summary>
        /// Hand the view the layout scaler, so the bar's appearance can run <c>setOtstup</c>
        /// (<c>GUI.as:1112</c>). Called once by <c>HudBootstrapper</c>; null is tolerated (the bar then
        /// draws but does not shift the item text), which is the state a fixture builds.
        /// </summary>
        public void SetLayout(HudLayoutScaler layout)
        {
            _layout = layout;
        }

        /// <summary>Report the live pet: its health fraction and the remaining cooldown in seconds.</summary>
        public void Show(float fraction, float cooldownSeconds)
        {
            _pendingFraction = fraction;
            _pendingCooldown = cooldownSeconds;
            _dirty = true;
        }

        /// <summary>Hide the bar — no pet out.</summary>
        public void Hide()
        {
            _pendingFraction = 0f;
            _pendingCooldown = 0f;
            _dirty = true;
        }

        /// <summary>
        /// The fill's width for a health fraction — the port of <c>vis.hpPet.bar.scaleX = hp / maxhp</c>.
        /// Clamped, because a fraction above one would overhang the frame.
        /// </summary>
        public static float FillWidthFor(float fraction) => FillWidth * Mathf.Clamp01(fraction);

        /// <summary>
        /// The countdown label — <c>floor(noPet / 30)</c> (<c>GUI.as:1100-1106</c>). Zero seconds prints
        /// nothing rather than "0".
        /// </summary>
        public static string CooldownLabel(float cooldownSeconds)
        {
            int seconds = Mathf.FloorToInt(Mathf.Max(0f, cooldownSeconds));
            return seconds > 0 ? seconds.ToString() : string.Empty;
        }

        private void OnEnable()
        {
            PetHealthBarFeed.Reported += Show;
            PetHealthBarFeed.Cleared += Hide;

            // Start hidden on every enable: a re-enable is a blackout ending, and the pet may have been
            // un-summoned while this view was unsubscribed.
            Hide();
        }

        private void OnDisable()
        {
            PetHealthBarFeed.Reported -= Show;
            PetHealthBarFeed.Cleared -= Hide;
        }

        private void LateUpdate()
        {
            if (!_dirty) return;
            _dirty = false;
            ApplyPending();
        }

        private void ApplyPending()
        {
            // The oracle hides the bar when hp <= 0 (`:1090-1093`) but keeps the widget on screen while a
            // cooldown is running, because that is when the countdown is worth reading.
            float fraction = Mathf.Clamp01(_pendingFraction);
            bool visible = fraction > 0f || _pendingCooldown > 0f;

            if (visible == IsShown
                && Mathf.Approximately(fraction, LastFraction)
                && LastCooldownSeconds == Mathf.FloorToInt(Mathf.Max(0f, _pendingCooldown)))
            {
                return;
            }

            bool wasShown = IsShown;

            IsShown = visible;
            LastFraction = fraction;
            LastCooldownSeconds = Mathf.FloorToInt(Mathf.Max(0f, _pendingCooldown));

            // `GUI.setPet` ends with `this.setOtstup();` (`:1112`) — the item readout, its icon and the
            // mana line move down 30 px while this bar is on screen. Only on a change: `ApplyAll` is a
            // full table pass and the feed reports every sim tick.
            if (wasShown != visible)
            {
                _layout?.SetPetBarVisible(visible);
            }

            if (_content != null && _content.activeSelf != visible)
            {
                _content.SetActive(visible);
            }

            if (!visible) return;

            if (_fillRect != null)
            {
                _fillRect.sizeDelta = new Vector2(FillWidthFor(fraction), FillHeight);
            }

            if (_label != null)
            {
                _label.text = CooldownLabel(_pendingCooldown);
            }
        }

        private void BuildFill(RectTransform parent)
        {
            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _fillRect = (RectTransform)fillGo.transform;
            _fillRect.SetParent(parent, false);
            _fillRect.anchorMin = _fillRect.anchorMax = FillAnchor;
            _fillRect.pivot = FillAnchor;
            _fillRect.anchoredPosition = new Vector2(BorderThickness, 0f);
            _fillRect.sizeDelta = new Vector2(FillWidth, FillHeight);

            var image = fillGo.GetComponent<Image>();
            image.color = FillColour;
            image.raycastTarget = false;
        }

        private void BuildBorder(RectTransform parent)
        {
            float width = BarWidth;
            float height = BarHeight;
            float t = BorderThickness;

            Strip(parent, "Border Top", new Vector2(0f, 1f), new Vector2(0f, 1f),
                Vector2.zero, new Vector2(width, t));
            Strip(parent, "Border Bottom", new Vector2(0f, 0f), new Vector2(0f, 0f),
                Vector2.zero, new Vector2(width, t));
            Strip(parent, "Border Left", new Vector2(0f, 0f), new Vector2(0f, 0f),
                Vector2.zero, new Vector2(t, height - 2f * t));
            Strip(parent, "Border Right", new Vector2(1f, 0f), new Vector2(1f, 0f),
                Vector2.zero, new Vector2(t, height - 2f * t));
        }

        private void BuildLabel(RectTransform parent)
        {
            var go = new GameObject("Cooldown", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(BarWidth + 4f, 0f);
            rect.sizeDelta = new Vector2(40f, BarHeight + 4f);

            _label = go.GetComponent<Text>();
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _label.fontSize = 10;
            _label.alignment = TextAnchor.MiddleLeft;
            _label.color = Color.white;
            _label.raycastTarget = false;
            _label.text = string.Empty;
        }

        private static void Strip(RectTransform parent, string name, Vector2 anchor, Vector2 pivot,
            Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var image = go.GetComponent<Image>();
            image.color = BorderColour;
            image.raycastTarget = false;
        }
    }
}
