using System;
using UnityEngine;
using UnityEngine.UI;
using PFE.Data.Definitions;
using PFE.UI.Common;

namespace PFE.UI.PipBuck.Pages
{
    /// <summary>
    /// Page 5 — the tab the Oracle itself captions <b>"Main menu"</b> (<c>main5</c>,
    /// <c>PipBuck.as:172</c>), and the one place in the device that can leave the session.
    ///
    /// <para><b>What the Oracle puts here, and what this page is.</b> In AS3, <c>pages[5]</c> is
    /// <c>PipPageOpt</c> — the in-game options screen: Load, Save, Options, Control and Log sub-tabs
    /// (<c>PipPageOpt.as:142-348</c>). That page is <b>not</b> ported; it needs sliders, checkboxes, a
    /// key-capture popup and ten save slots, and it is a work item of its own. What this class
    /// implements is the thing the tab's own caption promises and the thing a player who presses it
    /// expects: <b>a way back to the title screen</b>. It is a port addition, not an Oracle behaviour —
    /// <c>MainMenu.as</c> is shown once at world construction and nothing in the AS3 scripts ever
    /// brings it back.</para>
    ///
    /// <para><b>Why a confirmation page and not a tab that quits.</b> The tab sits one slot away from
    /// "Information" and the action throws away the session. The Oracle protects its own destructive
    /// actions the same way (<c>PipPageInfo.as</c>'s travel confirm is a separate press), and a
    /// misclick that ends the run is not recoverable by anything in the port today.</para>
    ///
    /// <para><b>No sub-tab row, deliberately.</b> <see cref="PipStubPage"/> draws one for every
    /// unported page because the row is the page's <i>shape</i> and constrains the work item that
    /// fills it in. This page is not that stub and must not pretend to be: five dead buttons reading
    /// Load / Save / Options / Control / Log over a confirmation prompt would advertise a screen that
    /// does not exist. When <c>PipPageOpt</c> proper lands it replaces this class, and its row comes
    /// with it.</para>
    ///
    /// <para><b>It does not touch the world or the scene.</b> The click raises
    /// <see cref="ReturnRequested"/>; <see cref="PipBuckManager.RequestMainMenu"/> owns the ordering
    /// (close the device, then ask), and the bootstrapper owns the load. A page that called
    /// <c>SceneLoader</c> itself would be untestable and would leave the world paused on the way
    /// out.</para>
    /// </summary>
    public sealed class PipPageMainMenuView : IPipPage
    {
        /// <summary>The Oracle's page index — <c>pages[5]</c> (<c>PipBuck.as:111</c>).</summary>
        public const int MainMenuPageId = 5;

        /// <summary>
        /// The scene this page returns to. Pinned by <c>PipMainMenuReturnTests</c> against both the
        /// literal string and the scene asset on disk: a rename here that the build settings do not
        /// follow fails at the one moment the player has already lost the session.
        /// </summary>
        public const string MainMenuSceneName = "MainMenuScene";

        // ── Geometry ──────────────────────────────────────────────────────────
        //
        // Page space is the Oracle's `vis` frame (PipBuck.as:89-90 puts it at 165,72) and the rows
        // every list page uses start at PipBuckLayout.PageRowPosition(0) = (30, 100). The block below
        // is placed on that grid so the prompt sits where a page's first rows would, rather than at a
        // centre the other eight pages do not share.

        private const float TextX = 30f;
        private const float TitleY = 64f;
        private const float BodyY = 104f;
        private const float BodyWidth = 740f;
        private const float BodyHeight = 120f;
        private const float ButtonY = 236f;
        private const float HintY = 276f;

        /// <summary><c>butOk</c> (symbol 508) is 187x28 and is the device's own confirm button.</summary>
        private const float ButtonWidth = 187f;

        private const float ButtonHeight = 28f;

        private const int TitleFontSize = 20;
        private const int BodyFontSize = 16;
        private const int ButtonFontSize = 13;
        private const int HintFontSize = 13;

        // ── Copy ──────────────────────────────────────────────────────────────
        //
        // PORT ADDITION. The title is the tab's own caption resolved through the Oracle's string table
        // (`main5`), so it cannot drift from the tab. Everything else has no key in `text_en.xml`
        // because the screen does not exist in the Oracle, and inventing a key would put a port
        // string into a document the importer regenerates.

        private const string BodyText =
            "Leave the wasteland and return to the title screen.\n\n" +
            "Progress since your last save is lost — this path does not autosave.";

        private const string ConfirmCaption = "Return to main menu";

        private const string HintText = "Close [TAB] cancels and returns to the game.";

        // ── Wiring ────────────────────────────────────────────────────────────

        private PipBuckCompositionDefinition _composition;
        private Font _font;
        private PipTextTable _text = PipTextTable.Empty;

        /// <summary>
        /// Raised when the player confirms. Set by <c>PipBuckBootstrapper</c> to
        /// <see cref="PipBuckManager.RequestMainMenu"/>, which is the only object that knows the
        /// device has to close before the scene goes away.
        /// </summary>
        public Action ReturnRequested { get; set; }

        // ── Built objects ─────────────────────────────────────────────────────

        private RectTransform _root;
        private Button _button;
        private Text _buttonLabel;

        /// <summary>
        /// The button has been pressed and the return is in flight. Re-armed by
        /// <see cref="SetStatus"/>, which is the page's own "I am being shown again" hook — a held
        /// latch that nothing releases would leave the button dead for the rest of the session if the
        /// load failed or the player came back to the device.
        /// </summary>
        private bool _requested;

        public int PageId => MainMenuPageId;

        /// <summary>
        /// True: this page <i>is</i> its contents. Reporting false would make the shell draw its
        /// "PAGE 5 (OPT) HAS NO CONTENTS YET" notice over a prompt that works.
        /// </summary>
        public bool IsImplemented => true;

        /// <summary>
        /// Supplies the composition (for the confirm button's art), the font and the localisation
        /// document. Called by <c>PipBuckBootstrapper</c> before <see cref="Build"/>, for the reason
        /// <c>PipStubPage.Configure</c> gives: <see cref="IPipPage"/>'s seam is
        /// <c>Build(RectTransform)</c> and widening it would make every page claim dependencies it
        /// does not have.
        /// </summary>
        public void Configure(PipBuckCompositionDefinition composition, Font font, PipTextTable text)
        {
            _composition = composition;
            _font = font;
            _text = text ?? PipTextTable.Empty;
        }

        // ── IPipPage ──────────────────────────────────────────────────────────

        /// <summary>
        /// Creates the prompt under <paramref name="pageRoot"/>.
        ///
        /// <para><b>The root starts inactive</b>, which is the Oracle's own contract for a page:
        /// <c>this.vis.visible = false</c> in <c>PipPage</c>'s constructor (<c>PipPage.as:92</c>), and
        /// <c>PipBuckManager.SetPage</c> reveals exactly one page. A page that built itself visible
        /// would draw over whichever page was actually open.</para>
        /// </summary>
        public void Build(RectTransform pageRoot)
        {
            if (pageRoot == null)
            {
                Debug.LogError("[PipPageMainMenuView] Build called with no page root. Page 5 will " +
                               "show the shell's empty frame and nothing else.");
                return;
            }

            if (_font == null)
            {
                // Not fatal — the prompt still lays out — but every caption would be blank, which
                // reads as "the localisation failed" rather than "Configure was not called".
                Debug.LogWarning("[PipPageMainMenuView] Built before Configure, so every caption on " +
                                 "page 5 will be blank.");
            }

            _root = CreateRect("PipPageOpt (Main menu)", pageRoot);

            Text title = CreateText("nazv", TextX, TitleY, BodyWidth, 30f, TitleFontSize,
                PfeUiColors.PageYellow, TextAnchor.MiddleLeft);
            title.text = PipBuckPages.TabLabelFor(MainMenuPageId, _text);

            Text body = CreateText("info", TextX, BodyY, BodyWidth, BodyHeight, BodyFontSize,
                PfeUiColors.PageR0, TextAnchor.UpperLeft);
            body.text = BodyText;

            BuildButton();

            Text hint = CreateText("bottext", TextX, HintY, BodyWidth, 24f, HintFontSize,
                PfeUiColors.PageOrange, TextAnchor.MiddleLeft);
            hint.text = HintText;

            _root.gameObject.SetActive(false);
            Arm();
        }

        /// <summary>
        /// The port of <c>PipPage.setStatus</c> for this page: there is nothing to re-read from the
        /// world, so it only re-arms the button. Called by <c>PipBuckManager.SetPage</c> every time
        /// the page becomes active, which is exactly when a stale "Returning…" caption must go.
        /// </summary>
        public void SetStatus(int subPage)
        {
            if (_root == null) return;

            Arm();
        }

        public void Step()
        {
            // Nothing to advance: no list, no hover countdown, no scroll. Deliberately silent —
            // Step runs 30 times a second while the device is open.
        }

        public void SetVisible(bool visible)
        {
            if (_root != null) _root.gameObject.SetActive(visible);
        }

        // ── The action ────────────────────────────────────────────────────────

        private void OnConfirm()
        {
            if (_requested) return;

            // A page that was registered but never wired would otherwise swallow the click and show
            // "Returning…" forever. That is the failure mode the shell's notice exists to prevent on
            // a page with no contents; here the button has contents, so it has to say so itself.
            if (ReturnRequested == null)
            {
                Debug.LogError("[PipPageMainMenuView] The return-to-main-menu button has no handler. " +
                               "PipBuckBootstrapper must wire ReturnRequested to " +
                               "PipBuckManager.RequestMainMenu. The click was ignored.");
                return;
            }

            _requested = true;

            if (_button != null) _button.interactable = false;
            if (_buttonLabel != null) _buttonLabel.text = "Returning…";

            ReturnRequested.Invoke();
        }

        /// <summary>
        /// Puts the button back into its armed state. The explicit release half of
        /// <see cref="_requested"/>: every path that does not end in a scene load has to come through
        /// here, and <see cref="SetStatus"/> is the page's only such path.
        /// </summary>
        private void Arm()
        {
            _requested = false;

            if (_button != null) _button.interactable = true;
            if (_buttonLabel != null) _buttonLabel.text = ConfirmCaption;
        }

        // ── Construction helpers ──────────────────────────────────────────────

        private void BuildButton()
        {
            var go = new GameObject("butOk", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(Button));

            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            rect.anchoredPosition = new Vector2(TextX, -ButtonY);

            var background = go.GetComponent<Image>();

            // The device's own confirm button (symbol 508). The sub-category sprite is the stand-in
            // the info page also uses when the imported art is absent, and a nearly transparent tint
            // is what it falls back to with no composition at all — a click target is still a click
            // target, and a button drawn as nothing is indistinguishable from a missing one.
            Sprite art = _composition != null ? _composition.infoConfirmButton : null;
            background.sprite = art != null
                ? art
                : _composition != null ? _composition.subCategoryButton : null;
            background.color = _composition != null ? Color.white : new Color(0f, 0f, 0f, 0.01f);

            var labelGo = new GameObject("text", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Text));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            _buttonLabel = labelGo.GetComponent<Text>();
            _buttonLabel.font = _font;
            _buttonLabel.fontSize = ButtonFontSize;
            _buttonLabel.color = PfeUiColors.PageR0;
            _buttonLabel.alignment = TextAnchor.MiddleCenter;
            _buttonLabel.raycastTarget = false;

            _button = go.GetComponent<Button>();
            _button.transition = Selectable.Transition.None;
            _button.onClick.AddListener(OnConfirm);
        }

        private Text CreateText(string name, float x, float y, float width, float height, int fontSize,
            Color color, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));

            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(x, -y);

            var text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;

            return text;
        }

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
