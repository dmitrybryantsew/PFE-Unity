using System;
using UnityEngine;
using UnityEngine.UI;
using PFE.UI.Common;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The sub-tab row a page draws above its content — the port of the <c>but1</c>..<c>but5</c>
    /// buttons that <c>PipPage</c>'s constructor builds inside every page's own <c>vis</c>
    /// (<c>PipPage.as:143-152</c>).
    ///
    /// <para><b>Why this is shared rather than written once per page.</b> The Oracle's ten page
    /// classes all get the row from the same base constructor and all label it from the same
    /// expression (<c>Res.pipText(this.pp + _loc4_)</c>). Only two things differ per page — which of
    /// the five are visible, and which one is current — and both are already data
    /// (<see cref="PipSubTabs"/>). A per-page copy of the button loop would be nine more chances to
    /// get the closure, the pitch or the label key wrong, and the ten rows would drift apart
    /// visually the moment one of them was tweaked.</para>
    ///
    /// <para><b>It is not the shell's bar.</b> <c>PipBuckView</c> owns <c>but0</c>..<c>but5</c> — the
    /// <i>main</i> tabs, which live on the device and never move. This row lives inside the page
    /// root, is built and destroyed with the page, and is different on every page. The Oracle keeps
    /// the two in different parents for exactly that reason.</para>
    ///
    /// <para><b>Frames become tints.</b> The Oracle lights the current sub-tab by
    /// <c>gotoAndStop(2)</c> and a signed one by <c>gotoAndStop(signs[n] + 2)</c>
    /// (<c>PipPage.as:894-915</c>), so the button symbol has at least five frames. Only one was
    /// imported (<c>butCatFon 1019</c>, the composition's <c>subCategoryButton</c>), so the port
    /// renders the state as a background tint — the same substitution <c>PipPageInvView</c> already
    /// makes, which is what keeps the inventory's row and this one looking alike.</para>
    /// </summary>
    public sealed class PipSubTabBar
    {
        // ── Geometry ──────────────────────────────────────────────────────────
        //
        // PORT CHOICE. The Oracle positions these inside each page's own `vis`, whose symbol was not
        // imported, so there is no attested offset to read. What IS attested is that the row sits
        // above the page's list, and the page's own origin and row pitch (PipBuckLayout.PageRowX /
        // PageRowY0) — the row is placed just above the header row so the two do not overlap.
        //
        // These four are the single owner of the numbers: PipPageInvView aliases them rather than
        // keeping a second copy, so the inventory's row and every stub's row cannot drift apart.

        /// <summary>Top edge of the row, inside the page root.</summary>
        public const float TabY = 10f;

        /// <summary>Left edge of the first sub-tab.</summary>
        public const float TabX0 = 30f;

        /// <summary>Horizontal distance between sub-tab origins.</summary>
        public const float TabPitch = 124f;

        /// <summary>The button's own size.</summary>
        public static readonly Vector2 TabSize = new Vector2(118f, 40f);

        // ── Wiring ────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised with the sub-page number when a sub-tab is clicked. The Oracle's handler is
        /// <c>PipPage.page2Click</c> (<c>PipPage.as:883-892</c>), which assigns <c>page2</c>, calls
        /// <c>setStatus()</c> and plays sound 2 — the page (or the manager on its behalf) owns that,
        /// not the bar. The bar only reports which button was pressed.
        /// </summary>
        public Action<int> SubPageClicked { get; set; }

        // ── Built objects ─────────────────────────────────────────────────────

        private RectTransform _root;
        private readonly Image[] _backgrounds = new Image[PipSubTabs.MaxSubTabs];
        private readonly Text[] _labels = new Text[PipSubTabs.MaxSubTabs];

        /// <summary>The last sign value computed per sub-tab. See <see cref="SignAt"/>.</summary>
        private readonly int[] _signs = new int[PipSubTabs.MaxSubTabs];

        /// <summary>Whether <see cref="Build"/> has run.</summary>
        public bool IsBuilt => _root != null;

        /// <summary>
        /// The sign marker the last <see cref="Refresh"/> computed for a sub-tab, or 0.
        ///
        /// <para><b>Exposed but not drawn.</b> The marker lives in frames 3..5 of the sub-tab symbol,
        /// which were not imported — so the port can compute which tab wants a marker and cannot yet
        /// show one. It is surfaced rather than discarded so that landing the art changes one method
        /// instead of re-deriving the rule, and so a test can assert the rule now.</para>
        /// </summary>
        public int SignAt(int subPage)
        {
            if (subPage < 1 || subPage > PipSubTabs.MaxSubTabs) return 0;
            return _signs[subPage - 1];
        }

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Builds the five buttons under <paramref name="parent"/>.
        ///
        /// <para>All five are always built, including the ones the page hides — that is the Oracle's
        /// own shape (<c>PipPage.as:143-152</c> builds five for every page and the subclasses only
        /// set <c>visible</c>), and it is what lets a page's visibility change at runtime without
        /// rebuilding the row.</para>
        /// </summary>
        public void Build(RectTransform parent, Font font, Sprite buttonSprite)
        {
            if (parent == null)
            {
                Debug.LogError("[PipSubTabBar] Build called with no parent. The sub-tab row will not " +
                               "exist, and the page will look like it has no sub-pages.");
                return;
            }

            if (_root != null) return;   // idempotent, like every other Build in this device

            _root = CreateRect("SubTabs", parent);

            for (int i = 0; i < PipSubTabs.MaxSubTabs; i++)
            {
                int subPage = i + 1;

                var go = new GameObject("but" + subPage, typeof(RectTransform), typeof(CanvasRenderer),
                    typeof(Image), typeof(Button));
                var rect = (RectTransform)go.transform;
                rect.SetParent(_root, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.sizeDelta = TabSize;
                rect.anchoredPosition = new Vector2(TabX0 + i * TabPitch, -TabY);

                Image background = go.GetComponent<Image>();
                background.sprite = buttonSprite;
                background.color = Color.white;
                _backgrounds[i] = background;

                var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer),
                    typeof(Text));
                var labelRect = (RectTransform)labelGo.transform;
                labelRect.SetParent(rect, false);
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;

                Text label = labelGo.GetComponent<Text>();
                label.font = font;
                label.fontSize = 15;
                label.color = PfeUiColors.PageR0;
                label.alignment = TextAnchor.MiddleCenter;
                label.raycastTarget = false;
                // Left empty here and written by Refresh, which runs whenever the page is shown. The
                // caption depends on the localisation document, which may arrive after Build — and a
                // caption frozen at build time is the failure this device has already been bitten by
                // once (see PipBuckView.RefreshShell).
                label.text = string.Empty;
                _labels[i] = label;

                // Capture `subPage`, not `i`: a closure over the loop variable would make every
                // button report the last one. (Same trap PipBuckView.BuildTabs documents.)
                var button = go.GetComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => SubPageClicked?.Invoke(subPage));
            }
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the captions, the visibility and the lit frame for a page.
        ///
        /// <para><b>Called on every page activation, not once at build.</b> The caption comes from
        /// the localisation document, the visibility from the page's context and the lit frame from
        /// the current sub-page — all three can change while the row exists, and a row that drew
        /// itself once would show the previous page's labels the second time it was opened.</para>
        /// </summary>
        /// <param name="pageId">The Oracle's page id — decides the label key prefix and the rules.</param>
        /// <param name="subPage">The page's current <c>page2</c>.</param>
        /// <param name="text">The localisation document, or null.</param>
        /// <param name="context">What opened the page. See <see cref="PipSubTabContext"/>.</param>
        /// <param name="signals">The values behind the sign markers. See <see cref="PipSubTabSignals"/>.</param>
        public void Refresh(int pageId, int subPage, PipTextTable text, in PipSubTabContext context,
            in PipSubTabSignals signals)
        {
            if (_root == null) return;

            for (int i = 0; i < PipSubTabs.MaxSubTabs; i++)
            {
                int slot = i + 1;

                bool visible = PipSubTabs.IsVisible(pageId, slot, context);

                // The Oracle hides a button by `visible = false` on the clip, never by removing it.
                if (_backgrounds[i] != null && _backgrounds[i].gameObject.activeSelf != visible)
                {
                    _backgrounds[i].gameObject.SetActive(visible);
                }

                if (!visible)
                {
                    // Cleared rather than left behind: SignAt is public, and a hidden slot holding the
                    // previous page's marker would report a warning about a button nobody can see.
                    _signs[i] = 0;
                    continue;
                }

                int sign = PipSubTabs.SignFor(pageId, slot, signals);
                _signs[i] = sign;

                // PipPage.as:148 — `_loc3_.text.text = Res.pipText(this.pp + _loc4_)`.
                if (_labels[i] != null)
                {
                    _labels[i].text = PipSubTabs.Label(pageId, slot, text);
                }

                // PipPage.as:894-915 — frame 2 for the current sub-tab, `signs[n] + 2` for a signed
                // one, frame 1 otherwise. The rule is asked for rather than re-implemented, so
                // "the current tab wins over its own sign" cannot be got wrong here; frames above 2
                // are the un-imported markers, and render as the resting frame.
                int frame = PipSubTabs.FrameFor(slot, subPage, sign);
                bool active = frame == PipSubTabs.ActiveFrame;

                if (_backgrounds[i] != null)
                {
                    _backgrounds[i].color = active ? PfeUiColors.PageYellow : Color.white;
                }

                if (_labels[i] != null)
                {
                    _labels[i].color = active ? PfeUiColors.PageDark : PfeUiColors.PageR0;
                }
            }
        }

        /// <summary>Shows or hides the whole row. The Oracle's <c>vis.visible</c> on the page.</summary>
        public void SetVisible(bool visible)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            return rect;
        }
    }
}
