using System;
using UnityEngine;
using UnityEngine.UI;
using R3;
using PFE.Data.Definitions;
using PFE.UI.Common;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Renders the PipBuck device shell: the bezel, the six tabs, the title strip and the page root.
    /// The port of everything <c>fe.inter.PipBuck</c> does to its <c>vis</c> MovieClip.
    ///
    /// <para><b>Raw pixels, not a scaled canvas.</b> The HUD uses a <c>CanvasScaler</c> at 1200x800
    /// because <c>GUI.resizeScreen</c> anchors elements to the edges of a virtual screen. The PipBuck
    /// is different: <c>PipBuck.resizeScreen</c> positions <c>vis</c> in <b>stage pixels</b> and scales
    /// it by a ratio of stage size to 1200x800 — it is a MovieClip sitting directly on the stage, with
    /// no scaling layer above it. So this canvas runs at <c>ConstantPixelSize</c> and
    /// <see cref="ApplyResize"/> converts <see cref="Screen.width"/>/<see cref="Screen.height"/>
    /// straight through. Introducing a reference resolution here would mean dividing the Oracle's own
    /// output by the scaler's factor, which is a units conversion with no counterpart in the Oracle —
    /// the class of bug that already cost this project a 100x overlay.</para>
    ///
    /// <para><b>The backdrop is one flat PNG, and that is why the tabs are drawn on top of it.</b>
    /// FFDec exports <c>visPipBuck</c> as a single composite image, so the tab buttons, the "?" and
    /// weight buttons and the scanlines are already painted into it — but they are painted with
    /// <i>placeholder</i> text and a fixed frame. Interactive tabs therefore have to be drawn over the
    /// top, at exactly the SWF's own positions (see <see cref="PipBuckLayout"/>), so they land on the
    /// baked artwork rather than beside it. The scanlines are deliberately <b>not</b> redrawn: they are
    /// already in the composite, and layering the tile again would darken the screen twice.</para>
    /// </summary>
    public sealed class PipBuckView : MonoBehaviour
    {
        /// <summary>Tab index 0 is the Close button; 1..5 are the page tabs.</summary>
        private const int TabSlotCount = 6;

        private PipBuckCompositionDefinition _composition;
        private PipBuckManager _manager;
        private Font _font;

        private RectTransform _deviceRoot;
        private Image _backdrop;
        private Text _topText;
        private Text _notice;
        private RectTransform _pageRoot;
        private RectTransform _statusPanel;

        private readonly Image[] _tabImages = new Image[TabSlotCount];
        private readonly Text[] _tabTexts = new Text[TabSlotCount];

        /// <summary>
        /// Generated button art, built only when the composition has none. See
        /// <see cref="TabSprite"/> and <see cref="PipBuckTabArt"/>.
        /// </summary>
        private Sprite _tabSpriteNormal;
        private Sprite _tabSpriteActive;

        private readonly CompositeDisposable _subscriptions = new CompositeDisposable();

        /// <summary>The last screen size the layout was computed for, so a resize is not recomputed per frame.</summary>
        private Vector2 _appliedScreenSize = new Vector2(-1f, -1f);

        public PipBuckManager Manager => _manager;

        /// <summary>The page container, handed to every <see cref="IPipPage"/>'s <c>Build</c>.</summary>
        public RectTransform PageRoot => _pageRoot;

        /// <summary>The status panel (<c>pr</c>) the I8 monitor mounts its 15 rows under.</summary>
        public RectTransform StatusPanel => _statusPanel;

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Builds the shell under <paramref name="canvas"/>. Called once by
        /// <see cref="PipBuckBootstrapper"/>.
        /// </summary>
        public void Build(Canvas canvas, PipBuckCompositionDefinition composition, PipBuckManager manager, Font font)
        {
            _composition = composition;
            _manager = manager;
            _font = font;

            _deviceRoot = CreateRect("PipBuck Device", canvas.transform, Vector2.zero, Vector2.zero);
            _deviceRoot.anchorMin = _deviceRoot.anchorMax = new Vector2(0f, 1f);   // canvas top-left
            _deviceRoot.pivot = new Vector2(0f, 1f);
            _deviceRoot.sizeDelta = PipBuckLayout.ReferenceSize;
            _deviceRoot.anchoredPosition = Vector2.zero;

            BuildBackdrop();
            BuildTabs();
            BuildTopText();

            _statusPanel = CreateRect("Status Panel (pr)", _deviceRoot, Vector2.zero, Vector2.zero);
            Place(_statusPanel, PipElement.StatusPanel);

            _pageRoot = CreateRect("Page Root", _deviceRoot, Vector2.zero, Vector2.zero);
            Place(_pageRoot, PipElement.PageRoot);

            BuildNotice();

            // Start hidden — the device is closed at boot, exactly as `this.vis.visible = false`
            // (PipBuck.as:93) makes it in the Oracle's constructor.
            _deviceRoot.gameObject.SetActive(false);

            Subscribe();

            ApplyResize();
        }

        private void BuildBackdrop()
        {
            var go = new GameObject("Backdrop", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _backdrop = go.GetComponent<Image>();
            _backdrop.sprite = _composition != null ? _composition.bezel : null;
            _backdrop.color = Color.white;
            _backdrop.preserveAspect = false;

            // The backdrop must not eat clicks: the tabs sit on top of it, but any part of the
            // artwork the player clicks where no button exists should fall through rather than
            // silently swallowing the event.
            _backdrop.raycastTarget = false;

            var rect = (RectTransform)go.transform;
            rect.SetParent(_deviceRoot, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = PipBuckLayout.BackdropSize;
            rect.anchoredPosition = PipBuckLayout.BackdropAnchor.ToUnityAnchoredPosition();

            if (_backdrop.sprite == null)
            {
                Debug.LogError("[PipBuckView] No bezel sprite in the composition asset. The device will " +
                               "render as an empty rectangle. Run PFE/Art/Import Game Interface step 2.");
            }
        }

        private void BuildTabs()
        {
            // Slot 0 is `but0` (Close, PipBuck.as:108-110); slots 1..5 are `but1`..`but5`
            // (PipBuck.as:167-176). All six are the same ButPage symbol, which is why one loop can
            // build them.
            for (int slot = 0; slot < TabSlotCount; slot++)
            {
                PipElement element = slot == 0 ? PipElement.CloseTab : (PipElement)((int)PipElement.Tab1 + slot - 1);

                var go = new GameObject(slot == 0 ? "but0 (Close)" : "but" + slot,
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));

                var rect = (RectTransform)go.transform;
                rect.SetParent(_deviceRoot, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.sizeDelta = PipBuckLayout.TabButtonSize;
                Place(rect, element);

                _tabImages[slot] = go.GetComponent<Image>();
                _tabImages[slot].sprite = TabSprite(false);
                _tabImages[slot].color = Color.white;

                var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                var labelRect = (RectTransform)labelGo.transform;
                labelRect.SetParent(rect, false);
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(4f, 2f);
                labelRect.offsetMax = new Vector2(-4f, -2f);

                Text label = labelGo.GetComponent<Text>();
                label.font = _font;
                label.fontSize = 14;
                label.color = PfeUiColors.PageR0;
                label.alignment = TextAnchor.MiddleCenter;
                label.horizontalOverflow = HorizontalWrapMode.Wrap;
                label.verticalOverflow = VerticalWrapMode.Truncate;
                label.raycastTarget = false;
                label.text = string.Empty;
                _tabTexts[slot] = label;

                // Capture `slot`, not the loop variable: a closure over `slot` would make every tab
                // open the last page. The `slot` declared inside the loop body is per-iteration.
                int capturedSlot = slot;
                var button = go.GetComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => OnTabClicked(capturedSlot));
            }
        }

        private void BuildTopText()
        {
            var go = new GameObject("toptext", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_deviceRoot, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(600f, 28f);
            Place(rect, PipElement.TopText);

            _topText = go.GetComponent<Text>();
            _topText.font = _font;
            _topText.fontSize = 18;
            _topText.color = PfeUiColors.PageR0;
            _topText.alignment = TextAnchor.MiddleLeft;
            _topText.raycastTarget = false;
            _topText.text = string.Empty;
        }

        /// <summary>
        /// The "this page is not implemented yet" notice.
        ///
        /// <para><b>Why the shell needs one.</b> Pages arrive in separate work items (I9 inventory,
        /// I10 stats, I11 info). Without this, opening a tab whose page has not landed shows an empty
        /// screen — which is indistinguishable from a page that loaded and drew nothing, and would
        /// make the tab bar look broken during exactly the play-test that is meant to validate it.</para>
        /// </summary>
        private void BuildNotice()
        {
            var go = new GameObject("Page Notice", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_deviceRoot, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(700f, 200f);
            rect.anchoredPosition = new Vector2(260f, -280f);

            _notice = go.GetComponent<Text>();
            _notice.font = _font;
            _notice.fontSize = 20;
            _notice.color = PfeUiColors.PageYellow;
            _notice.alignment = TextAnchor.UpperLeft;
            _notice.raycastTarget = false;
            _notice.text = string.Empty;
        }

        private void Subscribe()
        {
            PipBuckViewModel vm = _manager.ViewModel;

            // Visibility. PipBuck.as:282 — `this.vis.visible = this.active;`
            vm.Active.Subscribe(active => _deviceRoot.gameObject.SetActive(active)).AddTo(_subscriptions);

            // Which tab is lit. PipBuck.as:207-228.
            vm.ActiveTab.Subscribe(_ => RefreshTabFrames()).AddTo(_subscriptions);

            // Tab 4's label and visibility. PipBuck.as:275-281.
            vm.ContextualTab.Subscribe(_ => RefreshTabFrames()).AddTo(_subscriptions);

            // The page changed: re-render the tabs (page 6..9 lights tab 4 without owning it) and
            // decide whether the notice is needed.
            vm.Page.Subscribe(_ =>
            {
                RefreshTabFrames();
                RefreshNotice();
            }).AddTo(_subscriptions);

            vm.TopText.Subscribe(text =>
            {
                if (_topText == null) return;
                _topText.text = text ?? string.Empty;
            }).AddTo(_subscriptions);

            // The "no power" overlay. PipBuck.as:386-402. No shipped path reaches it — the port has no
            // `pipOff` — so it renders as a message rather than a sprite, and says so.
            vm.SupplyError.Subscribe(error =>
            {
                if (_notice == null) return;
                if (error)
                {
                    _notice.color = PfeUiColors.PageRed;
                    _notice.text = "PIP-BUCK: NO POWER\n\n(The Oracle's `pipError` symbol is not in the " +
                                   "composition asset — PipBuck.as:393-396.)";
                }
                else
                {
                    _notice.color = PfeUiColors.PageYellow;
                    RefreshNotice();
                }
            }).AddTo(_subscriptions);
        }

        /// <summary>
        /// Re-renders everything the shell derives from <see cref="PipBuckManager"/>: the tab frames
        /// and their labels, and the "not implemented" notice.
        ///
        /// <para><b>Why this has to exist, and why the subscriptions are not enough.</b> The shell's
        /// rendering is driven by <see cref="Subscribe"/>, which fires on changes to
        /// <c>Active</c>, <c>ActiveTab</c>, <c>ContextualTab</c> and <c>Page</c>. All four of those
        /// already hold their final values <i>before</i> the device is first opened —
        /// <see cref="PipBuckViewModel.Page"/> and <see cref="PipBuckViewModel.ActiveTab"/> are both
        /// <c>1</c>, and opening on page 1 leaves them there — so pressing Tab emits <b>nothing</b>
        /// and the shell keeps whatever it drew while it was being built.</para>
        ///
        /// <para>That is not a corner case, it is the shipped path: the device opens on page 1
        /// (<c>PipBuck.as:178</c>), which is the same page the view-model already holds. And what
        /// the shell drew at build time was wrong twice over, because
        /// <see cref="PipBuckBootstrapper"/> calls <c>Build</c> <i>before</i> it registers the pages
        /// and sets the tab labels — so the notice said "page 1 is not implemented" when no page had
        /// been registered yet, and every tab label was the empty string, which left the exported
        /// art's baked placeholder text showing through. Neither ever corrected itself. The
        /// bootstrapper therefore calls this once, after it has finished wiring.</para>
        /// </summary>
        public void RefreshShell()
        {
            RefreshTabFrames();
            RefreshNotice();
        }

        private void RefreshTabFrames()
        {
            if (_manager == null) return;

            PipBuckViewModel vm = _manager.ViewModel;
            int activeTab = vm.ActiveTab.Value;
            int contextualTab = vm.ContextualTab.Value;

            // Slot 0 (Close) is never lit: `setButtons` lights index `page`, and `page` is 1..9, so
            // but0 always draws frame 1. PipBuck.as:211-227.
            for (int slot = 0; slot < TabSlotCount; slot++)
            {
                if (_tabImages[slot] == null) continue;

                bool lit = slot != 0 && slot == activeTab;

                _tabImages[slot].sprite = TabSprite(lit);

                // Tab 4 is hidden unless a contextual page owns it (PipBuck.as:275). The other four
                // are always visible in normal mode.
                if (slot == 4)
                {
                    _tabImages[slot].gameObject.SetActive(contextualTab == 4);
                }
            }

            // Labels. `but0` shows `mainclose`; but1..but5 show `main1`..`main5`, except that but4 is
            // relabelled to the page that opened the device (`main` + param1, PipBuck.as:279).
            SetTabLabel(0, _manager.TabLabel(0));
            for (int slot = 1; slot <= 5; slot++)
            {
                int labelPageId = slot;
                if (slot == 4 && contextualTab != 0)
                {
                    labelPageId = contextualTab;
                }

                SetTabLabel(slot, _manager.TabLabel(labelPageId));
            }
        }

        private void SetTabLabel(int slot, string text)
        {
            if (_tabTexts[slot] == null) return;
            _tabTexts[slot].text = text ?? string.Empty;
        }

        /// <summary>
        /// The button sprite for a tab: the composition's, or — when the composition has none —
        /// one rendered from the Oracle's own shape data by <see cref="PipBuckTabArt"/>.
        ///
        /// <para><b>The fallback is not decoration.</b> The tab button's exported PNG cannot be used
        /// at all: <c>ButPage</c> (SWF symbol 1143) carries two design-time text fields that FFDec
        /// rasterises into the export, so it reads "Инвентарь1" whatever the port labels it. A tree
        /// that has had the interface importer run over it now gets a clean button written to the
        /// same path, and this fallback covers the tree that has not — the tabs draw as buttons
        /// either way, instead of as nothing.</para>
        /// </summary>
        private Sprite TabSprite(bool active)
        {
            Sprite fromComposition = _composition != null
                ? (active ? _composition.tabButtonActive : _composition.tabButtonNormal)
                : null;

            if (fromComposition != null) return fromComposition;

            if (active)
            {
                if (_tabSpriteActive == null) _tabSpriteActive = PipBuckTabArt.CreateSprite(true);
                return _tabSpriteActive;
            }

            if (_tabSpriteNormal == null) _tabSpriteNormal = PipBuckTabArt.CreateSprite(false);
            return _tabSpriteNormal;
        }

        private void RefreshNotice()
        {
            if (_notice == null || _manager == null) return;
            if (_manager.ViewModel.SupplyError.Value) return;

            int page = _manager.CurrentPage;
            IPipPage instance = _manager.PageFor(page);

            if (instance == null || !instance.IsImplemented)
            {
                string name = PipBuckPages.IdentifierFor(page);
                _notice.color = PfeUiColors.PageYellow;
                _notice.text = $"PAGE {page} ({name.ToUpperInvariant()}) HAS NO CONTENTS YET.\n\n" +
                               "The device shell, the tab bar, the status panel and this page's own " +
                               "sub-tab strip are all live; the page's contents land in a later " +
                               "work item.";
            }
            else
            {
                _notice.text = string.Empty;
            }
        }

        private void OnTabClicked(int slot)
        {
            if (_manager == null) return;

            if (slot == 0)
            {
                _manager.Close();          // PipBuck.pipClose (PipBuck.as:198-205)
            }
            else
            {
                _manager.PageClick(slot);  // PipBuck.pageClick (PipBuck.as:182-196)
            }
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void OnRectTransformDimensionsChange()
        {
            // The canvas resized. The Oracle re-runs `resizeScreen` from the stage's own resize event
            // (World.resizeScreen, World.as:783-812); this is the Unity equivalent.
            ApplyResize();
        }

        /// <summary>
        /// The port of <c>PipBuck.resizeScreen</c>, applied to the device root.
        ///
        /// <para>Reads <see cref="Screen.width"/>/<see cref="Screen.height"/> directly because this
        /// canvas is <c>ConstantPixelSize</c> — canvas local units and stage pixels are the same unit,
        /// so no conversion is needed and none is invented. See the class summary.</para>
        /// </summary>
        public void ApplyResize()
        {
            ApplyResize(Screen.width, Screen.height);
        }

        /// <summary>Resize against an explicit stage size. Split out so the rule is testable.</summary>
        public void ApplyResize(float stageWidth, float stageHeight)
        {
            if (_deviceRoot == null) return;

            // Guard the degenerate case: a zero-size stage (a minimised window, or the first frame
            // before the player loop has a size) would scale the device to zero and then stay there
            // until the next resize event — which a static window never sends.
            if (stageWidth <= 0f || stageHeight <= 0f) return;

            _appliedScreenSize = new Vector2(stageWidth, stageHeight);

            PipBuckLayout.DevicePlacement placement =
                PipBuckLayout.ComputeDevicePlacement(stageWidth, stageHeight);

            _deviceRoot.anchoredPosition = placement.ToUnityAnchoredPosition();
            _deviceRoot.localScale = new Vector3(placement.Scale, placement.Scale, 1f);
        }

        private void LateUpdate()
        {
            // A window that is resized without the canvas changing size (a fullscreen toggle on some
            // platforms) would otherwise never re-run the layout. One vector compare per frame.
            if (!Mathf.Approximately(_appliedScreenSize.x, Screen.width) ||
                !Mathf.Approximately(_appliedScreenSize.y, Screen.height))
            {
                ApplyResize();
            }
        }

        private void OnDestroy()
        {
            _subscriptions.Dispose();

            // The generated buttons are ours, not assets — `PipBuckTabArt` marks them
            // HideFlags.DontSave, so nothing else would ever collect them.
            DestroyGenerated(ref _tabSpriteNormal);
            DestroyGenerated(ref _tabSpriteActive);
        }

        private static void DestroyGenerated(ref Sprite sprite)
        {
            if (sprite == null) return;

            if (sprite.texture != null) Destroy(sprite.texture);
            Destroy(sprite);
            sprite = null;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Places a child of the device root at its Oracle anchor.</summary>
        private static void Place(RectTransform rect, PipElement element)
        {
            rect.anchoredPosition = PipBuckLayout.AnchorFor(element).ToUnityAnchoredPosition();
        }

        private static RectTransform CreateRect(string name, Transform parent, Vector2 size, Vector2 anchored)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchored;
            return rect;
        }
    }
}
