using PFE.Entities.Enemies.Bosses;
using UnityEngine;
using UnityEngine.UI;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Oracle <c>vis.hpbarboss</c> — the boss health bar across the top of the screen. The port of
    /// <c>GUI.hpBarBoss</c> (<c>GUI.as:1693-1705</c>), driven by <see cref="BossHealthBarFeed"/>.
    ///
    /// <para><b>What the symbol actually is.</b> <c>hpBarBoss</c> is sprite 4360, and its display list
    /// is two children: <c>bar</c> (4358, holding the 400 × 12 gradient shape 4357) at depth 1, and the
    /// frame (shape 4359) at depth 3. So the frame's 1 px stroke draws <i>over</i> the fill, and the
    /// whole thing measures <b>401 × 13 px</b> — shape 4359's bounds are
    /// <c>-4010..4010 × -70..190</c> twips about the symbol origin. The third child the AS3 class
    /// declares, <c>goldstar</c> (240), is an <b>empty sprite</b> in the shipped SWF, which is why the
    /// imported PNG has 15 blank rows above the bar and nothing at either end of it.</para>
    ///
    /// <para><b>The fill shrinks from <i>both</i> ends, and that is not a mistake.</b>
    /// <c>GUI.hpBarBoss</c> writes <c>bar.scaleX = hp / maxhp</c>, and <c>bar</c> sits at the symbol's
    /// own origin with its shape spanning −200..+200 about it — so a scale about the registration point
    /// pulls the left and right ends inward equally. Measured on the owner's screenshot of the running
    /// game: frame 501..769, fill 535..734 — the fill is centred on the frame, not pinned to its left
    /// edge. A left-anchored bar is the obvious port and it is wrong; the fill is sized from the centre
    /// for that reason.</para>
    ///
    /// <para><b>Where the numbers come from.</b> The imported sprite
    /// (<see cref="PFE.Data.Definitions.PipBuckCompositionDefinition.bossBar"/>, symbol 4360) is the
    /// flattened symbol at 1:1: 401 × 28, with the 13-row bar on the bottom rows and the 15 blank rows
    /// above them. The fill is cropped out of it at runtime — one <c>Sprite.Create</c> over a sub-rect,
    /// which does not require a readable texture — because the frame and the fill have to move
    /// independently and the PNG cannot be split at import time without touching the asset pipeline.</para>
    ///
    /// <para><b>Sim tick records, frame draws.</b> <see cref="Show"/> and <see cref="Hide"/> are called
    /// from <c>BossController.TickControl</c>, so they store the value and set a dirty flag;
    /// <see cref="LateUpdate"/> does the <c>sizeDelta</c> and <c>SetActive</c> writes. Same split as
    /// <see cref="ActionProgressBarView"/>, and for the same reason: a 30 Hz tick must not touch the UI.
    /// The toggled object is a <b>child</b> — deactivating this component's own GameObject would stop
    /// <c>LateUpdate</c> and freeze the bar off forever, the trap <see cref="ArmourBarView"/>
    /// documents.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BossHealthBarView : MonoBehaviour
    {
        /// <summary>The frame's width in reference-canvas pixels — shape 4359, 8020 twips.</summary>
        public const float BarWidth = 401f;

        /// <summary>The frame's height — shape 4359, 260 twips.</summary>
        public const float BarHeight = 13f;

        /// <summary>
        /// The fill's width: the frame less its 1 px stroke on each side. The SWF's own fill shape is
        /// 400 px and is drawn under the stroke, so its visible width is this.
        /// </summary>
        public const float FillWidth = 399f;

        /// <summary>The fill's height, same reasoning as <see cref="FillWidth"/>.</summary>
        public const float FillHeight = 11f;

        /// <summary>The frame stroke's thickness — 1 px, the same as the dialogue plate's border.</summary>
        public const float BorderThickness = 1f;

        /// <summary>
        /// The frame's colour, <c>#FF0000</c>. Read off the imported PNG, which is the rasterised
        /// symbol: every border row and column is exactly <c>(255, 0, 0)</c> and the fill's gradient
        /// starts one pixel inside it.
        /// </summary>
        public static readonly Color BorderColour = new Color32(255, 0, 0, 255);

        /// <summary>The imported sprite's width — used only to prove a crop is still valid.</summary>
        public const int SourceWidth = 401;

        /// <summary>
        /// The imported sprite's height: 13 rows of bar plus 15 blank rows. The blank rows are the
        /// empty <c>goldstar</c>'s bounds, and the bar sits on the <b>bottom</b> of them (Unity's
        /// texture space grows up, so the blank rows are above the bar on screen).
        /// </summary>
        public const int SourceHeight = 28;

        /// <summary>
        /// The fill's anchor and pivot — the centre of the frame, and the whole reason the bar drains
        /// from both ends. A left-anchored fill, <c>(0, 0.5)</c>, is the obvious port and it is wrong:
        /// see the class remarks. Named so the fixture can assert it, because this is a one-token
        /// difference that produces a bar which still looks like a bar.
        /// </summary>
        public static readonly Vector2 FillAnchor = new Vector2(0.5f, 0.5f);

        /// <summary>Used when the composition is missing: the fill's mid-gradient colour.</summary>
        private static readonly Color FallbackFillColour = new Color32(255, 183, 0, 255);

        private RectTransform _fillRect;
        private GameObject _content;
        private float _pending;
        private bool _dirty;

        /// <summary>True when the bar is currently drawn. For tests and inspection.</summary>
        public bool IsShown { get; private set; }

        /// <summary>The last fraction drawn, 0&#8594;1. For tests and inspection.</summary>
        public float LastFraction { get; private set; }

        /// <summary>
        /// Build the bar's two halves under this component's own rect. Called once by
        /// <c>HudBootstrapper</c>, which owns the rect's size and placement
        /// (<see cref="HudSlot.BossHealthBar"/>).
        /// </summary>
        /// <param name="barSprite">
        /// <c>PipBuckCompositionDefinition.bossBar</c>, or null when the composition is not in the
        /// scene — in which case the fill is drawn as a flat amber rectangle rather than not at all,
        /// because a missing gradient must not mean a missing boss bar.
        /// </param>
        public void Build(Sprite barSprite)
        {
            if (_content != null) return;

            _content = new GameObject("Content", typeof(RectTransform));
            var contentRect = (RectTransform)_content.transform;
            contentRect.SetParent(transform, false);
            contentRect.anchorMin = contentRect.anchorMax = new Vector2(0.5f, 0.5f);
            contentRect.pivot = new Vector2(0.5f, 0.5f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(BarWidth, BarHeight);

            // Fill first, frame second: the SWF puts `bar` at depth 1 and the frame at depth 3, so the
            // stroke covers the fill's ends. Sibling order is the port's depth.
            BuildFill(contentRect, barSprite);
            BuildBorder(contentRect);

            // Nothing is being fought at boot.
            _content.SetActive(false);
            IsShown = false;
        }

        /// <summary>
        /// Report a boss's health. A fraction of zero or less hides the bar — the Oracle's
        /// <c>param1 &lt;= 0</c> arm, which is also what a corpse's <c>visDetails()</c> produces.
        /// </summary>
        public void Show(float fraction)
        {
            _pending = fraction;
            _dirty = true;
        }

        /// <summary>Hide the bar. The <c>hpBarBoss()</c> no-argument call.</summary>
        public void Hide()
        {
            _pending = 0f;
            _dirty = true;
        }

        /// <summary>
        /// The fill's width for a health fraction — the port of <c>vis.hpbarboss.bar.scaleX = hp / maxhp</c>
        /// (<c>GUI.as:1702</c>) expressed as the rect width it produces.
        ///
        /// <para><b>Pure, and separately testable, because the number is the whole point.</b> The fill is
        /// <i>centred</i> in the frame (see the class remarks), so this width is applied with the rect's
        /// pivot at its centre — a left-anchored bar of the same width would drain the wrong way. A
        /// fraction above 1 cannot happen (HP is clamped to the maximum) but is clamped here anyway: the
        /// Oracle would happily let <c>scaleX</c> run past 1 and overhang the frame.</para>
        /// </summary>
        public static float FillWidthFor(float fraction)
        {
            return FillWidth * Mathf.Clamp01(fraction);
        }

        private void OnEnable()
        {
            BossHealthBarFeed.Reported += Show;
            BossHealthBarFeed.Cleared += Hide;

            // Start from hidden on every enable, not just the first. A re-enable is a blackout ending
            // or a cutscene handing the HUD back, and the boss may have died while this view was
            // unsubscribed — so the last fraction it remembers is not evidence that anything is alive.
            // The next report re-shows the bar within one sim tick, which is the Oracle's own behaviour
            // on a room change (`World.ativateLoc` calls `hpBarBoss()` to hide, `World.as:1088`).
            Hide();
        }

        private void OnDisable()
        {
            BossHealthBarFeed.Reported -= Show;
            BossHealthBarFeed.Cleared -= Hide;
        }

        private void LateUpdate()
        {
            if (!_dirty) return;
            _dirty = false;
            ApplyPending();
        }

        private void ApplyPending()
        {
            bool visible = _pending > 0f;
            float fraction = visible ? Mathf.Clamp01(_pending) : 0f;

            // Nothing to write, so write nothing. A boss reports the same fraction on every tick while
            // it is not being hit, and a `sizeDelta` assignment dirties the canvas mesh for the whole
            // HUD — text included — even when the value is unchanged.
            if (visible == IsShown && Mathf.Approximately(fraction, LastFraction))
            {
                return;
            }

            IsShown = visible;
            LastFraction = fraction;

            if (_content != null && _content.activeSelf != visible)
            {
                _content.SetActive(visible);
            }

            if (visible && _fillRect != null)
            {
                _fillRect.sizeDelta = new Vector2(FillWidthFor(fraction), FillHeight);
            }
        }

        /// <summary>
        /// The gradient. Stretched into a narrower rect rather than clipped, which is what a
        /// <c>scaleX</c> on the SWF clip does — the gradient only varies vertically, so squashing and
        /// clipping are the same picture, and squashing is one rect write per tick.
        /// </summary>
        private void BuildFill(RectTransform parent, Sprite barSprite)
        {
            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _fillRect = (RectTransform)fillGo.transform;
            _fillRect.SetParent(parent, false);
            _fillRect.anchorMin = _fillRect.anchorMax = FillAnchor;
            _fillRect.pivot = FillAnchor;
            _fillRect.anchoredPosition = Vector2.zero;
            _fillRect.sizeDelta = new Vector2(FillWidth, FillHeight);

            var image = fillGo.GetComponent<Image>();
            image.raycastTarget = false;

            Sprite fill = CropFill(barSprite);
            if (fill != null)
            {
                image.sprite = fill;
                image.color = Color.white;
                return;
            }

            // Loud once, at build time. A composition that is missing from the scene, or a boss-bar
            // PNG that was re-exported at a different size, would otherwise show a solid block with
            // nothing anywhere saying why.
            Debug.LogWarning("[BossHealthBarView] The boss-bar sprite is missing or is not " +
                             SourceWidth + "x" + SourceHeight + " — drawing a flat fill instead of the " +
                             "imported gradient.");
            image.color = FallbackFillColour;
        }

        /// <summary>
        /// The 1 px frame, as four strips — the same construction <c>HudBootstrapper.CreateBorder</c>
        /// uses for the dialogue plate, and for the same reason: the port's HUD is flat
        /// <see cref="Image"/> colours and has no plate art to nine-slice.
        /// </summary>
        private void BuildBorder(RectTransform parent)
        {
            float width = BarWidth;
            float height = BarHeight;
            float t = BorderThickness;

            // Top and bottom span the full width; left and right span the full height between them, so
            // no corner is drawn twice.
            Strip(parent, "Border Top", new Vector2(0f, 1f), new Vector2(0f, 1f),
                Vector2.zero, new Vector2(width, t));
            Strip(parent, "Border Bottom", new Vector2(0f, 0f), new Vector2(0f, 0f),
                Vector2.zero, new Vector2(width, t));
            Strip(parent, "Border Left", new Vector2(0f, 0f), new Vector2(0f, 0f),
                Vector2.zero, new Vector2(t, height - 2f * t));
            Strip(parent, "Border Right", new Vector2(1f, 0f), new Vector2(1f, 0f),
                Vector2.zero, new Vector2(t, height - 2f * t));
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

        /// <summary>
        /// The fill's sub-rect out of the imported symbol: one pixel in from each edge, which drops the
        /// frame's stroke and keeps the gradient. Returns null when the sprite is not the 401 × 28
        /// export this arithmetic assumes, so the caller can fall back loudly rather than crop the
        /// wrong rows.
        /// </summary>
        /// <remarks>
        /// <b>Wrapped, because this runs inside <c>BuildHud</c>.</b> <c>Sprite.Create</c> throws rather
        /// than returning null for a texture it cannot map (a packed atlas sprite, a rect outside the
        /// texture), and an exception here would abort the whole HUD build — every other element in the
        /// canvas included — over one bar. The texture is not required to be readable: the crop only
        /// sets UVs, it never touches pixels.
        /// </remarks>
        public static Sprite CropFill(Sprite source)
        {
            if (source == null) return null;

            Texture2D texture = source.texture;
            if (texture == null) return null;
            if (texture.width != SourceWidth || texture.height != SourceHeight) return null;

            try
            {
                return Sprite.Create(
                    texture,
                    new Rect(1f, 1f, FillWidth, FillHeight),
                    new Vector2(0.5f, 0.5f),
                    source.pixelsPerUnit);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BossHealthBarView] Could not crop the boss-bar fill " +
                                 $"({e.GetType().Name}: {e.Message}).");
                return null;
            }
        }
    }
}
