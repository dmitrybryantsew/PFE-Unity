using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using PFE.Data.Definitions;
using PFE.Systems.Map.Minimap;
using PFE.UI.Common;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Sub-tab 1's window — the port of <c>visPipMap</c> (sprite 990) and of the five interaction
    /// handlers on it (<c>PipPageInfo.as:787-850</c>).
    ///
    /// <para><b>What is the Oracle's and what is the port's.</b> The window box, the pan layer's
    /// resting offset, the three buttons' positions, the zoom range (1..6) and every piece of arithmetic
    /// are the Oracle's, and all of them live in <see cref="PipMinimapState"/> and
    /// <see cref="PipInfoFrame"/>. The window's frame and the three buttons' icons are the composition's
    /// <c>infoMapWindow</c> (sprite 990's flat export, which carries both). What remains a port choice is
    /// the player marker's size and tint — <c>plTag</c> (980) exports as a 198x198 clip canvas whose
    /// registration point cannot be verified, and a marker placed by assumption is a bug that looks like
    /// a working feature, so the tinted square stays.</para>
    ///
    /// <para><b>The transform hierarchy differs from the Oracle's, deliberately.</b> In AS3
    /// <c>vmap</c> is a <i>sibling</i> of <c>maska</c> and is clipped by it
    /// (<c>visMap.vmap.mask = visMap.maska</c>, <c>PipPageInfo.as:55</c>). Unity's <c>RectMask2D</c>
    /// clips its own descendants, so the port makes <c>vmap</c> a child of a rect that occupies the mask
    /// box. The visible result is identical; the one thing that changes is that <c>vmap</c>'s authored
    /// (15, 15) is now relative to the mask box rather than to <c>visPipMap</c>, so the offset is
    /// corrected once, in <see cref="Build"/>.</para>
    ///
    /// <para><b>Unity-free? No — and it does not need to be.</b> Every rule this panel applies is in
    /// <see cref="PipMinimapState"/>, which <i>is</i> Unity-free and is where the fixtures point. This
    /// class is the drawing half: texture upload, transforms and the three relays.</para>
    /// </summary>
    public sealed class PipInfoMapPanel
    {
        // ── Port choices ──────────────────────────────────────────────────────

        /// <summary>
        /// The player marker's size. <c>plTag</c> (980) is not in the composition, so there is no
        /// authored size. 10 px reads at the default zoom of 2 without covering a whole room.
        /// </summary>
        public const float MarkerSize = 10f;

        /// <summary>The three map buttons' size. See <see cref="MarkerSize"/> for why it is a choice.</summary>
        public static readonly Vector2 ButtonSize = new Vector2(30f, 26f);

        /// <summary>The zoom-in button's caption. The Oracle's art carries an icon, not a glyph.</summary>
        public const string ZoomInLabel = "+";

        /// <summary>The zoom-out button's caption.</summary>
        public const string ZoomOutLabel = "-";

        /// <summary>The centre button's caption.</summary>
        public const string CentreLabel = "o";

        private const int ButtonFontSize = 16;

        // ── State ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The pan / zoom / centre state — the Oracle's four map fields, kept here because this panel is
        /// the only thing that changes them. Exposed so the page and the fixtures can read the scale
        /// without a second copy.
        /// </summary>
        public PipMinimapState State { get; } = new PipMinimapState();

        private PipMinimapFacts _facts;

        // ── Built objects ─────────────────────────────────────────────────────

        private RectTransform _root;
        private RectTransform _window;
        private RectTransform _vmap;
        private RectTransform _mapRect;
        private RectTransform _tagRect;
        private Image _mapImage;
        private Texture2D _texture;
        private Sprite _sprite;

        /// <summary>Whether the panel has a composed image to draw. False means the page shows
        /// <c>emptymap</c> instead.</summary>
        public bool HasMap { get; private set; }

        public bool IsBuilt => _root != null;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Creates the window under the page root, hidden. <paramref name="composition"/> supplies the
        /// window art and (when absent) the stand-in the three buttons fall back to; either may be null.
        /// </summary>
        public void Build(RectTransform pageRoot, Font font, PipBuckCompositionDefinition composition)
        {
            if (pageRoot == null)
            {
                Debug.LogError("[PipInfoMapPanel] Build called with no page root. Sub-tab 1 (Map) will " +
                               "draw nothing, which looks the same as a location with no map.");
                return;
            }

            if (_root != null) return;

            Sprite windowSprite = composition != null ? composition.infoMapWindow : null;
            Sprite buttonSprite = composition != null ? composition.subCategoryButton : null;

            _root = CreateRect("visMap", pageRoot);
            _root.sizeDelta = new Vector2(PipInfoFrame.MapPanelWidth, PipInfoFrame.MapPanelHeight);
            Place(_root, PipInfoFrame.MapPanelX, PipInfoFrame.MapPanelY);

            // The window's own art — the frame, the mask area's fill and the three zoom buttons' icons,
            // all baked into sprite 990's flat export (843x676 = this panel's own size, so the art and
            // the panel are the same box and need no correction). It must be created BEFORE the mask
            // box below: Unity draws siblings in child order, and the buttons at the end of Build rely
            // on being later siblings to sit on the baked icons.
            if (windowSprite != null)
            {
                Image art = _root.gameObject.AddComponent<Image>();
                art.sprite = windowSprite;
                art.raycastTarget = false;
            }
            else
            {
                Debug.LogWarning("[PipInfoMapPanel] No infoMapWindow in the composition — sub-tab 1 " +
                                 "draws its pan and its marker but no window frame. Run PFE/Art/" +
                                 "Import Game Interface step 2 to generate it.");
            }

            // The mask box. `fon` and `maska` are the same clip at the same placement, so this rect is
            // the Oracle's own window: `PipInfoFrame.MapWindowLeft/Top/Width/Height`.
            _window = CreateRect("maska", _root);
            _window.sizeDelta = new Vector2(PipInfoFrame.MapWindowWidth, PipInfoFrame.MapWindowHeight);
            Place(_window, PipInfoFrame.MapWindowLeft, PipInfoFrame.MapWindowTop);
            _window.gameObject.AddComponent<RectMask2D>();

            // `vmap` (982) — the pan layer. See the class summary for why it hangs off the mask box
            // instead of off `visMap`, and why the offset is corrected here.
            _vmap = CreateRect("vmap", _window);
            _vmap.sizeDelta = Vector2.zero;
            Place(_vmap,
                PipInfoFrame.MapPanX0 - PipInfoFrame.MapWindowLeft,
                PipInfoFrame.MapPanY0 - PipInfoFrame.MapWindowTop);

            // The map bitmap. Flash's `map` is added at (0, 0) and scaled about its registration point,
            // which for a Bitmap is its top-left — so the rect's pivot is the top-left and the scale is
            // applied there, not about the centre.
            _mapRect = CreateRect("map", _vmap);
            _mapImage = _mapRect.gameObject.AddComponent<Image>();
            _mapImage.raycastTarget = false;
            _mapImage.enabled = false;

            // `plTag` (980) — the "you are here" marker. Flash sets its x/y directly
            // (`PipPageInfo.as:831-832`), so its authored placement is dead and only its size matters.
            _tagRect = CreateRect("plTag", _vmap);
            _tagRect.sizeDelta = new Vector2(MarkerSize, MarkerSize);
            var tagImage = _tagRect.gameObject.AddComponent<Image>();
            tagImage.color = MinimapPalette.Player;
            tagImage.raycastTarget = false;

            BuildButtons(font, windowSprite, buttonSprite);

            // The drag/scroll relay goes on the mask box, which is the region the player actually grabs.
            // One relay on the window, not one per child: Unity walks UP from the raycast hit, so a
            // relay on the parent serves the map, the marker and the gaps between them.
            var relay = _window.gameObject.AddComponent<DragRelay>();
            relay.Panel = this;

            SetVisible(false);
        }

        /// <summary>
        /// The three zoom buttons. <b>They are hit areas, not art, whenever the window art exists</b>:
        /// sprite 990's flat export already paints the three icons at exactly these placements
        /// (<c>PipInfoFrame.MapButtonX</c>, <c>MapZoomInY</c>/<c>MapZoomOutY</c>/<c>MapCentreY</c>), so a
        /// labelled button over it would draw twice — the same situation the device tabs are in over the
        /// bezel, and the same answer: a transparent Image that still receives raycasts. Without the
        /// window art the buttons fall back to the sub-category sprite and a caption, so a tree that has
        /// not had the importer run still shows three usable buttons.
        /// </summary>
        private void BuildButtons(Font font, Sprite windowSprite, Sprite buttonSprite)
        {
            float[] ys =
            {
                PipInfoFrame.MapZoomInY,
                PipInfoFrame.MapZoomOutY,
                PipInfoFrame.MapCentreY,
            };

            // The captions only exist for the no-art fallback; with the art they would sit on top of
            // the baked icons and read as doubled controls.
            bool drawFallback = windowSprite == null;
            string[] labels = drawFallback ? new[] { ZoomInLabel, ZoomOutLabel, CentreLabel }
                                           : new[] { "", "", "" };

            for (int i = 0; i < ys.Length; i++)
            {
                var go = new GameObject("but" + i, typeof(RectTransform), typeof(CanvasRenderer),
                    typeof(Image), typeof(Button));
                var rect = (RectTransform)go.transform;
                rect.SetParent(_root, false);
                AnchorTopLeft(rect);
                rect.sizeDelta = ButtonSize;
                Place(rect, PipInfoFrame.MapButtonX, ys[i]);

                Image background = go.GetComponent<Image>();
                background.sprite = drawFallback ? buttonSprite : null;
                background.color = drawFallback && buttonSprite != null
                    ? Color.white
                    : new Color(1f, 1f, 1f, 0f);   // transparent, still a raycast target

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
                label.fontSize = ButtonFontSize;
                label.color = PfeUiColors.PageR0;
                label.alignment = TextAnchor.MiddleCenter;
                label.raycastTarget = false;
                label.text = labels[i];

                // Capture `i`, not the loop variable — the same closure trap PipSubTabBar documents.
                int index = i;
                var button = go.GetComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => OnButton(index));
            }
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        /// <summary>
        /// Re-reads the minimap and repaints — the port of sub-page 1's build
        /// (<c>PipPageInfo.as:92-105</c>).
        ///
        /// <para><b>It re-anchors on every call, and that is the Oracle's behaviour, not an oversight.</b>
        /// <c>setSubPages</c> calls <c>setMapSize()</c> with no arguments after
        /// <c>drawMap()</c> (<c>:100</c>), which re-derives the marker from the player's tile with the
        /// method's own default anchor. A refresh therefore resets the marker and leaves the pan where the
        /// ratio puts it — exactly what the Oracle does when the player presses <c>M</c> again.</para>
        /// </summary>
        public void Refresh(PipMinimapFacts facts)
        {
            if (_root == null) return;

            _facts = facts;

            // `if(World.w.loc.noMap) emptytext = emptymap; else { drawMap(); setMapSize(); visMap.visible = true; }`
            bool canShow = facts != null && !facts.NoMap && facts.Buffer != null;
            HasMap = canShow;

            SetVisible(canShow);
            if (!canShow) return;

            EnsureTexture(facts.Buffer);

            Vector2 tag = facts.TagTile();
            State.SetMapSize(
                PipInfoFrame.MapDefaultAnchorX, PipInfoFrame.MapDefaultAnchorY, tag.x, tag.y);

            Apply();
        }

        /// <summary>Repaints the transform state without re-reading the world — used by the three
        /// buttons, the wheel and the drag.</summary>
        public void Apply()
        {
            if (_root == null || _mapRect == null) return;

            _mapRect.localScale = new Vector3(State.BitmapScale, State.BitmapScale, 1f);
            _mapRect.anchoredPosition = Vector2.zero;

            Place(_vmap, State.PanX - PipInfoFrame.MapWindowLeft, State.PanY - PipInfoFrame.MapWindowTop);
            Place(_tagRect, State.TagX, State.TagY);
        }

        public void SetVisible(bool visible)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Uploads the composed buffer as a point-filtered texture.
        ///
        /// <para><b>Point filtering is not a preference.</b> The image is one texel per tile, so any
        /// bilinear filtering smears each tile into its neighbour and the map reads as a blur — which on
        /// a 48x25 room at zoom 1 is the difference between "a map" and "a green smear".</para>
        /// </summary>
        private void EnsureTexture(LandMinimapBuffer buffer)
        {
            int width = buffer.Width;
            int height = buffer.Height;

            if (_texture != null && (_texture.width != width || _texture.height != height))
            {
                Object.Destroy(_texture);
                _texture = null;
                _sprite = null;
            }

            if (_texture == null)
            {
                _texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "PipInfoMinimap",
                };
            }

            _texture.SetPixels32(buffer.Pixels);
            _texture.Apply(false);

            if (_sprite == null)
            {
                // Pivot top-left: Flash's Bitmap is registered at (0, 0) and scaled about it, so the
                // rect must grow down-right from that corner when the zoom changes.
                _sprite = Sprite.Create(
                    _texture, new Rect(0f, 0f, width, height), new Vector2(0f, 1f), 100f);
                _sprite.name = "PipInfoMinimap";
            }

            _mapRect.sizeDelta = new Vector2(width, height);
            _mapImage.sprite = _sprite;
            _mapImage.enabled = true;
        }

        // ── Interaction ───────────────────────────────────────────────────────

        private Vector2 TagTile()
        {
            return _facts != null ? _facts.TagTile() : new Vector2(0f, 0f);
        }

        private void OnButton(int index)
        {
            Vector2 tag = TagTile();

            switch (index)
            {
                case 0:
                    State.ZoomBy(1, tag.x, tag.y);
                    break;
                case 1:
                    State.ZoomBy(-1, tag.x, tag.y);
                    break;
                default:
                    State.Centre();
                    break;
            }

            Apply();
        }

        internal void OnDrag(Vector2 flashDelta)
        {
            State.BeginDrag();
            State.Drag(flashDelta.x, flashDelta.y);
            Apply();
        }

        internal void OnDragEnd()
        {
            Vector2 tag = TagTile();
            State.EndDrag(tag.x, tag.y);
            Apply();
        }

        internal void OnScroll(float unityDeltaY, Vector2 localPointer)
        {
            // Unity: wheel up is a positive y. Flash: `delta > 0` steps the zoom up
            // (PipPageInfo.as:836-850) — so the sign carries across unchanged, and only the anchor
            // differs. The anchor is the mouse, in the mask box's own space, converted back to
            // `visPipMap` space so it matches the space `vmap`'s translation lives in.
            Vector2 tag = TagTile();

            float flashX = localPointer.x + PipInfoFrame.MapWindowLeft;
            float flashY = localPointer.y + PipInfoFrame.MapWindowTop;

            State.Scroll(unityDeltaY > 0f ? 1 : -1, flashX, flashY, tag.x, tag.y);
            Apply();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            return rect;
        }

        private static void AnchorTopLeft(RectTransform rect)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
        }

        /// <summary>The single y-negation for this file — see <c>PipAnchor.ToUnityAnchoredPosition</c>.</summary>
        private static void Place(RectTransform rect, float flashX, float flashY)
        {
            rect.anchoredPosition = new Vector2(flashX, -flashY);
        }

        // ── Event relays ──────────────────────────────────────────────────────
        //
        // Unity delivers pointer callbacks to a COMPONENT and this panel is a plain class, so this thin
        // relay hands the event straight back. It holds no state and decides nothing — every rule stays
        // in PipMinimapState, which is the half that can be run offline.

        private sealed class DragRelay : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler,
            IScrollHandler
        {
            public PipInfoMapPanel Panel;

            public void OnBeginDrag(PointerEventData eventData) => Panel?.State.BeginDrag();

            public void OnDrag(PointerEventData eventData)
            {
                if (Panel == null) return;

                // `eventData.delta` is in screen pixels and y-up. The device may be scaled by
                // `resizeScreen`, so the delta is divided by the panel's own scale before it becomes a
                // Flash delta — otherwise the map would pan faster than the pointer at a shrunk device.
                float scale = Panel._root != null ? Panel._root.lossyScale.x : 1f;
                if (scale <= 0f) scale = 1f;

                Panel.OnDrag(new Vector2(eventData.delta.x / scale, -eventData.delta.y / scale));
            }

            public void OnEndDrag(PointerEventData eventData) => Panel?.OnDragEnd();

            public void OnScroll(PointerEventData eventData)
            {
                if (Panel == null || Panel._window == null) return;

                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    Panel._window, eventData.position, eventData.pressEventCamera, out Vector2 local);

                // Unity's local space under a top-left pivot has +y down, which is already Flash's
                // direction — but the rect's own origin is its pivot (top-left), and the Oracle's anchor
                // is measured from `visPipMap`'s top-left. `OnScroll` adds the window offset back.
                Panel.OnScroll(eventData.scrollDelta.y, local);
            }
        }
    }
}
