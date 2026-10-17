using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using PFE.Data.Definitions;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Sub-tab 3's map — the port of <c>visPipWMap</c> (sprite 834) and of the travel branch of
    /// <c>PipPageInfo</c> (<c>:143-205</c>, <c>:369-423</c>, <c>:716-766</c>).
    ///
    /// <para><b>The 27 placements <i>are</i> the map.</b> They exist nowhere else — not in the AS3, not
    /// in the land assets, not in any other clip — so <see cref="PipTravelMapLayout"/> carries them as
    /// data and this panel is a loop over that table. A node with no entry is simply not built, which is
    /// the faithful behaviour for the six lands that have no node (see
    /// <see cref="PipTravelMapLayout.LandsWithoutNode"/>).</para>
    ///
    /// <para><b>The art, when the composition has it.</b> The window is sprite 834's flat export (the
    /// continent the nodes sit on); each node draws <c>sim_wmap</c> (833) as its backdrop,
    /// <c>zad</c> (803) frames 1/2 as its selection outline, <c>sim</c> (829) frame
    /// <c>IconFrameIndexFor(landId)</c> as its land icon and <c>visualSign1</c> (832) frame 1 as its
    /// "new land" marker. Without the composition each of those falls back to the tinted rect it drew
    /// before the art landed, so a tree that has not had the importer run still shows the map — the
    /// geometry, the visibility gate, the two alpha values and the selection rule are the Oracle's either
    /// way, and all of them are testable in <see cref="PipTravelMapLayout"/>.</para>
    /// </summary>
    public sealed class PipInfoWorldPanel
    {
        /// <summary>The land icons' size — <c>sim</c> (829) exports 40x40.</summary>
        public const float IconSize = PipInfoFrame.WorldIconSize;

        /// <summary>The selection frame's size — <c>zad</c> (803) exports 42x42.</summary>
        public const float SelectionSize = PipInfoFrame.WorldSelectionSize;

        /// <summary>
        /// The selection frame's two tints — the port of <c>zad.gotoAndStop(1)</c> / <c>(2)</c>
        /// (<c>PipPageInfo.as:163</c>, <c>:725</c>). Frame 1 is the resting outline and frame 2 the
        /// "you have picked this one" outline.
        /// </summary>
        private static readonly Color SelectionIdle = new Color(0f, 0.4f, 0.25f, 0.55f);

        /// <inheritdoc cref="SelectionIdle"/>
        private static readonly Color SelectionActive = new Color(1f, 1f, 0.2f, 1f);

        /// <summary>
        /// The "new land" marker's tint. <c>sign</c> (832) is an animated clip in the Oracle; the port
        /// draws a static outline because the art is missing, and <see cref="PipInfoFrame.WorldSignInset"/>
        /// records that the inset is a choice rather than a dumped placement.
        /// </summary>
        private static readonly Color SignTint = new Color(1f, 1f, 0.2f, 1f);

        // ── Wiring ────────────────────────────────────────────────────────────

        /// <summary>Raised with a land id when its node is clicked — the Oracle's <c>itemClick</c>.</summary>
        public Action<string> NodeClicked { get; set; }

        /// <summary>Raised with a land id when the pointer enters its node — the Oracle's
        /// <c>statInfo</c>.</summary>
        public Action<string> NodeHovered { get; set; }

        /// <summary>Raised when the pointer leaves the map — the Oracle has no <c>MOUSE_OUT</c> handler
        /// on a node, so the page keeps the last hovered land on screen until another is hovered. This is
        /// surfaced for the page to decide what to do with.</summary>
        public Action NodeExited { get; set; }

        // ── Built objects ─────────────────────────────────────────────────────

        private RectTransform _root;
        private PipBuckCompositionDefinition _composition;
        private readonly Dictionary<string, NodeView> _nodes =
            new Dictionary<string, NodeView>(StringComparer.Ordinal);

        public bool IsBuilt => _root != null;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        /// <summary>How many nodes were built — the map's own node count, and what a play-test's
        /// "is anything missing" question is asked against.</summary>
        public int NodeCount => _nodes.Count;

        private sealed class NodeView
        {
            public RectTransform Root;

            /// <summary>The node clip's own art — <c>sim_wmap</c> (833). This IS the root's Image: the
            /// node clip and its backdrop are the same 118x118 box, and the Image doubles as the raycast
            /// target either way (transparent when there is no art).</summary>
            public Image Backdrop;

            public Image Icon;
            public Image Selection;
            public Image Sign;
        }

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Creates the 27 nodes under the page root, hidden.
        ///
        /// <para><b>The nodes are placed at their centres.</b> Sprite 834's placements are node
        /// <i>centres</i> (they span x 58..511 over a 571-wide panel), so each node rect is built with a
        /// centre pivot — the one place in this device where the top-left rule does not apply, and the
        /// reason <see cref="PipTravelMapLayout.Nodes"/> carries a scale as well as a position.</para>
        /// </summary>
        public void Build(RectTransform pageRoot, PipBuckCompositionDefinition composition)
        {
            if (pageRoot == null)
            {
                Debug.LogError("[PipInfoWorldPanel] Build called with no page root. Sub-tab 3 (World) " +
                               "will draw no map at all, which looks the same as a campaign with no lands.");
                return;
            }

            if (_root != null) return;

            _composition = composition;

            _root = CreateRect("visWMap", pageRoot);
            _root.sizeDelta = new Vector2(PipInfoFrame.WorldPanelWidth, PipInfoFrame.WorldPanelHeight);
            Place(_root, PipInfoFrame.WorldPanelX, PipInfoFrame.WorldPanelY);

            // The window's own art — the continent the 27 nodes sit on, sprite 834's flat export
            // (571x597 = this panel's own size, so art and panel are the same box). Created BEFORE the
            // nodes so the nodes are later siblings and draw on top.
            Sprite windowSprite = _composition != null ? _composition.infoWorldWindow : null;
            if (windowSprite != null)
            {
                Image art = _root.gameObject.AddComponent<Image>();
                art.sprite = windowSprite;
                art.raycastTarget = false;
            }
            else
            {
                Debug.LogWarning("[PipInfoWorldPanel] No infoWorldWindow in the composition — sub-tab 3 " +
                                 "draws its nodes but no continent under them. Run PFE/Art/Import Game " +
                                 "Interface step 2 to generate it.");
            }

            IReadOnlyList<TravelNode> nodes = PipTravelMapLayout.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                BuildNode(nodes[i]);
            }

            SetVisible(false);
        }

        private void BuildNode(TravelNode node)
        {
            var go = new GameObject("node_" + node.LandId, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);   // the placement is the node's CENTRE
            rect.sizeDelta = new Vector2(PipInfoFrame.WorldNodeSize, PipInfoFrame.WorldNodeSize);
            rect.localScale = new Vector3(node.Scale, node.Scale, 1f);
            rect.anchoredPosition = new Vector2(node.X, -node.Y);

            // A transparent Image still receives raycasts, so this is the node's hit area — the Oracle
            // attaches its CLICK and MOUSE_OVER listeners to the node clip itself
            // (PipPageInfo.as:167-168). With art it is also the node's backdrop: `visWMap[land.id]` IS
            // a sim_wmap instance (833, 118x118 — this rect's own size), so the backdrop belongs on the
            // clip itself rather than on a child.
            Image hit = go.GetComponent<Image>();
            Sprite nodeBack = _composition != null ? _composition.infoLandNodeBack : null;
            hit.sprite = nodeBack;
            hit.color = nodeBack != null ? Color.white : new Color(1f, 1f, 1f, 0f);

            var relay = go.AddComponent<NodeRelay>();
            relay.Panel = this;
            relay.LandId = node.LandId;

            var view = new NodeView { Root = rect, Backdrop = hit };

            // `zad` (803) — the selection frame, drawn UNDER the icon: the Oracle's own depth order is
            // zad at 1, sim at 3, sign at 5. Frame 1 is the resting outline every visible node shows;
            // frame 2 is the picked one (Refresh writes the frame, not a visibility flip).
            Sprite[] selectionFrames = _composition != null ? _composition.infoLandSelectionFrames : null;
            view.Selection = CreateChild(rect, "zad", SelectionSize, SelectionIdle,
                selectionFrames != null && selectionFrames.Length > 0 ? selectionFrames[0] : null);

            // `sim` (829) — the land icon. The FRAME is resolved per land in Refresh, through
            // PipTravelMapLayout.IconFrameIndexFor; the sprite is null here and the colour is only the
            // no-art fallback's.
            view.Icon = CreateChild(rect, "sim", IconSize, Color.white, null);

            // `sign` (832) — the "new land" flag. With art it is the node's own box (the clip is 118x118,
            // the same as the node); the inset size stays the no-art fallback's.
            Sprite[] signFrames = _composition != null ? _composition.infoSignFrames : null;
            bool hasSignArt = signFrames != null && signFrames.Length > 0;
            view.Sign = CreateChild(rect, "sign",
                hasSignArt ? PipInfoFrame.WorldNodeSize
                           : PipInfoFrame.WorldSelectionSize - 2f * PipInfoFrame.WorldSignInset,
                SignTint,
                hasSignArt ? signFrames[0] : null);

            _nodes[node.LandId] = view;
        }

        private static Image CreateChild(RectTransform parent, string name, float size, Color colour,
            Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(size, size);
            rect.anchoredPosition = Vector2.zero;

            Image image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        /// <summary>
        /// Applies the draw state the model computed — the port of the tail of the node loop
        /// (<c>PipPageInfo.as:160-197</c>).
        ///
        /// <para><b>The panel does not re-derive anything.</b> Visibility, alpha and the sign flag all
        /// arrive from <see cref="PipTravelMapLayout.Draw"/>, which is the half that can be run offline.
        /// A panel that re-tested <c>visited || access</c> here would be a second copy of the rule, and
        /// the two would drift the first time either changed.</para>
        /// </summary>
        /// <param name="draws">What <see cref="PipTravelMapLayout.Draw"/> returned.</param>
        /// <param name="selectedLandId">The Oracle's <c>targetLand</c> — the node whose <c>zad</c> is on
        /// frame 2. Empty when nothing is picked.</param>
        /// <param name="iconTint">The tint for the land icons. The Oracle's icons carry their own
        /// colours; the port's stand-in rects need one, and the page supplies it so the whole map reads
        /// consistently.</param>
        public void Refresh(
            IReadOnlyList<PipTravelMapLayout.TravelNodeDrawResult> draws,
            string selectedLandId,
            Color iconTint)
        {
            if (_root == null) return;

            Sprite[] selectionFrames = _composition != null ? _composition.infoLandSelectionFrames : null;
            Sprite[] landIcons = _composition != null ? _composition.infoLandIcons : null;

            // Every node is hidden first, then the ones the model offered are shown. The Oracle does not
            // do this — it relies on each node having been left hidden by the previous pass — but the
            // port needs it because a land can become un-offered (a `visited` flag can be cleared by a
            // save load) and a stale visible node would then be wrong with nothing to notice it.
            foreach (NodeView view in _nodes.Values)
            {
                if (view.Root != null && view.Root.gameObject.activeSelf)
                {
                    view.Root.gameObject.SetActive(false);
                }
            }

            if (draws == null) return;

            for (int i = 0; i < draws.Count; i++)
            {
                PipTravelMapLayout.TravelNodeDrawResult result = draws[i];
                if (!_nodes.TryGetValue(result.Node.LandId, out NodeView view)) continue;

                view.Root.gameObject.SetActive(result.Draw.Visible);
                if (!result.Draw.Visible) continue;

                // `sim.alpha` — the whole node dims, not just its icon: the Oracle sets it on the node
                // clip (`PipPageInfo.as:181`, `:186`), which carries the backdrop, the selection frame
                // and the icon. Each child gets the alpha; the hit area's own fill stays fully
                // transparent when there is no backdrop art, because it is a raycast target, not art.
                if (view.Backdrop != null && view.Backdrop.sprite != null)
                {
                    view.Backdrop.color = new Color(1f, 1f, 1f, result.Draw.Alpha);
                }

                bool selected = !string.IsNullOrEmpty(selectedLandId) &&
                                string.Equals(selectedLandId, result.Node.LandId, StringComparison.Ordinal);

                // `zad.gotoAndStop(1)` on every node at entry, `(2)` on the one the player picked
                // (:163, :725) — a FRAME, not a visibility flip: the resting outline is always on a
                // visible node.
                if (view.Selection != null)
                {
                    if (selectionFrames != null && selectionFrames.Length > 1)
                    {
                        view.Selection.sprite = selectionFrames[selected ? 1 : 0];
                        view.Selection.color = new Color(1f, 1f, 1f, result.Draw.Alpha);
                    }
                    else
                    {
                        view.Selection.color = new Color(
                            (selected ? SelectionActive : SelectionIdle).r,
                            (selected ? SelectionActive : SelectionIdle).g,
                            (selected ? SelectionActive : SelectionIdle).b,
                            result.Draw.Alpha);
                    }

                    view.Selection.gameObject.SetActive(true);
                }

                // `sim.sim.gotoAndStop(land.id)` (:172) — the icon is the land's own frame, resolved
                // through the layout's label table with the Oracle's own blank-frame fallback.
                if (view.Icon != null)
                {
                    if (landIcons != null && landIcons.Length > 0)
                    {
                        int frame = PipTravelMapLayout.IconFrameIndexFor(result.Node.LandId);
                        view.Icon.sprite = landIcons[frame];
                        view.Icon.color = new Color(1f, 1f, 1f, result.Draw.Alpha);
                    }
                    else
                    {
                        view.Icon.color = new Color(iconTint.r, iconTint.g, iconTint.b, result.Draw.Alpha);
                    }
                }

                // `sign.play(); sign.visible = true` — the marker only ever appears in the port; with
                // art it draws frame 1 of the 40, without it the inset outline. Animating it would need
                // a per-frame driver the page does not have and the Oracle does not require to be
                // legible.
                if (view.Sign != null)
                {
                    view.Sign.gameObject.SetActive(result.Draw.Sign);
                    if (view.Sign.sprite != null)
                    {
                        view.Sign.color = new Color(1f, 1f, 1f, result.Draw.Alpha);
                    }
                    else
                    {
                        view.Sign.color = new Color(SignTint.r, SignTint.g, SignTint.b, result.Draw.Alpha);
                    }
                }
            }
        }

        public void SetVisible(bool visible)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        // ── Interaction ───────────────────────────────────────────────────────

        internal void OnNodeClicked(string landId) => NodeClicked?.Invoke(landId);

        internal void OnNodeHovered(string landId) => NodeHovered?.Invoke(landId);

        internal void OnNodeExited() => NodeExited?.Invoke();

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

        private static void Place(RectTransform rect, float flashX, float flashY)
        {
            rect.anchoredPosition = new Vector2(flashX, -flashY);
        }

        // ── Event relay ───────────────────────────────────────────────────────

        private sealed class NodeRelay : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler,
            IPointerExitHandler
        {
            public PipInfoWorldPanel Panel;
            public string LandId;

            public void OnPointerClick(PointerEventData eventData) => Panel?.OnNodeClicked(LandId);

            public void OnPointerEnter(PointerEventData eventData) => Panel?.OnNodeHovered(LandId);

            public void OnPointerExit(PointerEventData eventData) => Panel?.OnNodeExited();
        }
    }
}
