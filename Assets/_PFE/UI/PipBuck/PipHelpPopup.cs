using UnityEngine;
using UnityEngine.UI;
using PFE.UI.Common;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The device's shared hover tooltip — the port of <c>PipBuck.vishelp</c>
    /// (<c>PipBuck.as:114-127</c>, <c>:452-472</c>).
    ///
    /// <para><b>One popup, two buttons.</b> The Oracle builds <b>one</b> <c>visPipHelp</c> and points
    /// both <c>butHelp</c> and <c>butMass</c> at it: <c>helpShow</c> copies <c>helpText</c> into
    /// <c>vishelp.txt.htmlText</c> and reveals it, <c>massShow</c> copies <c>massText</c> into the same
    /// field, and either <c>MOUSE_OUT</c> hides it. So this is not a per-page element and it must not be
    /// built per page — a second copy would show the wrong text the moment both buttons existed.</para>
    ///
    /// <para><b>It is a child of the device root, not of a page.</b> <c>this.vis.addChild(this.vishelp)</c>
    /// (<c>:117</c>) puts it beside <c>skin</c>/<c>fon</c>/<c>but0</c>, and <c>setPage</c> hides it on every
    /// page change (<c>:437</c>) so a tooltip cannot survive a tab press. Both facts are why
    /// <see cref="PipBuckView"/> owns it rather than <see cref="Pages.PipPageInfoView"/>.</para>
    ///
    /// <para><b>The art is a stand-in, and this is the one place it shows.</b> <c>visPipHelp</c> is not in
    /// the composition and is not among the seven symbols the page needs (see
    /// <c>docs/GameInterface/09_INFO_PAGE_SCOPE.md</c> §5), so the panel below is drawn as a filled rect
    /// with an outline rather than as the Oracle's own clip. The <i>text</i> is exact — it is whatever the
    /// page put in <c>helpText</c> — and the position is the Oracle's own <c>(168, 138)</c>. What is a
    /// port choice is the panel's size and the absence of the symbol's own chrome.</para>
    /// </summary>
    public sealed class PipHelpPopup
    {
        // ── Geometry (PORT CHOICE) ────────────────────────────────────────────
        //
        // The Oracle's `vishelp.txt` bounds were not dumped, so there is no attested size to read. These
        // are sized for the longest help string the port ships (`helpWorld`, ~640 characters) at 15 px:
        // 620 px wide wraps it to about 14 lines, which is what the 320 px height holds.
        //
        // What is NOT a choice: the position. `PipBuck.as:115-116` sets it to (168, 138) and
        // PipBuckLayout carries that number with its citation.

        /// <summary>The panel's width.</summary>
        public const float PanelWidth = 620f;

        /// <summary>The panel's height.</summary>
        public const float PanelHeight = 320f;

        /// <summary>Inset from the panel edge to the text.</summary>
        private const float TextInset = 10f;

        private const int FontSize = 15;

        // ── Built objects ─────────────────────────────────────────────────────

        private RectTransform _root;
        private Image _panel;
        private Text _text;

        /// <summary>Whether the popup exists. False until <see cref="Build"/> runs.</summary>
        public bool IsBuilt => _root != null;

        /// <summary>Whether the popup is on screen — the Oracle's <c>vishelp.visible</c>.</summary>
        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        /// <summary>The text currently shown, or empty. The Oracle's <c>vishelp.txt.htmlText</c>.</summary>
        public string Text => _text != null ? _text.text : string.Empty;

        /// <summary>
        /// Creates the popup under the device root, hidden — <c>PipBuck.as:117</c> adds it and
        /// <c>:123</c> immediately sets <c>visible = false</c>.
        ///
        /// <para>Idempotent, like every other <c>Build</c> in this device: a canvas resize re-runs the
        /// shell's layout, and a second copy would draw a second panel over the first.</para>
        /// </summary>
        public void Build(RectTransform deviceRoot, Font font)
        {
            if (deviceRoot == null)
            {
                Debug.LogError("[PipHelpPopup] Build called with no device root. The \"?\" and weight " +
                               "tooltips will never appear, and hovering either button will look like a " +
                               "dead button rather than a missing popup.");
                return;
            }

            if (_root != null) return;

            var go = new GameObject("vishelp", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image));
            _root = (RectTransform)go.transform;
            _root.SetParent(deviceRoot, false);
            _root.anchorMin = _root.anchorMax = new Vector2(0f, 1f);
            _root.pivot = new Vector2(0f, 1f);
            _root.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            _root.anchoredPosition = PipBuckLayout.AnchorFor(PipElement.HelpPopup).ToUnityAnchoredPosition();

            _panel = go.GetComponent<Image>();
            _panel.color = PanelBackground;

            // Above every page: the popup is a device-level overlay and the Oracle adds it last
            // (`PipBuck.as:117`), so it draws over the page root that was added before it.
            _root.SetAsLastSibling();

            var textGo = new GameObject("txt", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Text));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(_root, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(TextInset, TextInset);
            textRect.offsetMax = new Vector2(-TextInset, -TextInset);

            _text = textGo.GetComponent<Text>();
            _text.font = font;
            _text.fontSize = FontSize;
            _text.color = PfeUiColors.PageR0;
            _text.alignment = TextAnchor.UpperLeft;
            _text.horizontalOverflow = HorizontalWrapMode.Wrap;
            _text.verticalOverflow = VerticalWrapMode.Truncate;

            // The Oracle sets the field's style sheet with `PipPage.setStyle(this.vishelp.txt)`
            // (PipBuck.as:124) and then writes `htmlText` — so the string may carry spans. The port's
            // one-formatter policy resolves those to <color> tags, which needs rich text on.
            _text.supportRichText = true;
            _text.raycastTarget = false;
            _text.text = string.Empty;

            SetVisible(false);
        }

        /// <summary>
        /// Shows the popup with <paramref name="text"/> — the port of <c>helpShow</c> / <c>massShow</c>
        /// (<c>PipBuck.as:452-467</c>), which differ only in which field they copy.
        ///
        /// <para>An empty string still reveals the panel, exactly as the Oracle does: the guard is on the
        /// button's hover, not on the text. Hiding an empty tooltip would make a page that forgot to set
        /// <c>helpText</c> look like a page whose help button is broken.</para>
        /// </summary>
        public void Show(string text)
        {
            if (_text != null) _text.text = text ?? string.Empty;
            SetVisible(true);
        }

        /// <summary>The port of <c>helpUnshow</c> / <c>massUnshow</c> (<c>PipBuck.as:457-460</c>,
        /// <c>:469-472</c>) and of <c>setPage</c>'s own hide (<c>:437</c>).</summary>
        public void Hide() => SetVisible(false);

        /// <summary>Shows or hides the popup without touching its text.</summary>
        public void SetVisible(bool visible)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        /// <summary>
        /// The panel's fill. A near-black green rather than the page's own background: the tooltip sits
        /// over page content, so it needs to be distinguishable from whatever is under it.
        /// </summary>
        private static Color PanelBackground
        {
            get
            {
                Color c = PfeUiColors.PageDark;
                return new Color(c.r * 0.35f, c.g * 0.35f, c.b * 0.35f, 0.96f);
            }
        }
    }
}
