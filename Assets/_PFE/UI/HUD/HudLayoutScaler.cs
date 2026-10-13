using System.Collections.Generic;
using UnityEngine;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Applies <see cref="HudLayout"/> to the live HUD and re-applies it whenever the canvas changes
    /// size — the port's replacement for <c>GUI.resizeScreen</c> being called again on a screen resize
    /// (<c>GUI.as:310-349</c>).
    ///
    /// <para><b>Why this is needed at all, when uGUI already has anchors.</b> Anchoring alone is not
    /// enough, because the Oracle's offsets are in <i>pixels of a 1200 × 800 sheet</i>, and a
    /// <see cref="CanvasScaler"/> in <c>ScaleWithScreenSize</c> mode with <c>match = 0.5</c> gives the
    /// canvas a rect whose size changes with the display's aspect ratio — a 21:9 screen makes the
    /// canvas wider than 1200, so an element pinned 20 px from the left edge is 20 <i>canvas</i> pixels
    /// in, which is a different fraction of the screen than on 16:9. That is exactly the behaviour the
    /// Oracle has: it recomputes against the real <c>screenX</c> every time the stage resizes. So the
    /// table is re-applied on the same trigger.</para>
    ///
    /// <para><b>Trigger choice: <see cref="OnRectTransformDimensionsChange"/>, not <c>Update</c>.</b>
    /// Unity sends this message to components on the GameObject whose RectTransform changed, which for
    /// a canvas root is exactly "the canvas is now a different size". An <c>Update</c> poll would run
    /// this every frame forever to catch an event that fires a handful of times per session.</para>
    ///
    /// <para><b>Registration is by slot, not by hierarchy.</b> Nothing here searches for children: the
    /// bootstrapper hands over the rects it built, keyed by the same <see cref="HudSlot"/> the table
    /// uses. A view that is not registered is simply not laid out, which is the honest state for an
    /// element that does not exist yet — as opposed to a placeholder that silently sits at (0, 0).</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HudLayoutScaler : MonoBehaviour
    {
        private readonly List<KeyValuePair<HudSlot, RectTransform>> _registered =
            new List<KeyValuePair<HudSlot, RectTransform>>();

        /// <summary>
        /// Optional: the S.A.T.S. overlay, which the Oracle stretches to the whole screen rather than
        /// anchoring (<c>GUI.as:322-323</c>).
        /// </summary>
        public RectTransform SatsLayer;

        /// <summary>Optional: the notification toast, whose <i>width</i> the Oracle recomputes (<c>GUI.as:325-335</c>).</summary>
        public RectTransform ToastBox;

        /// <summary>Optional: the dialogue window, which the Oracle scales down below 1200 px wide (<c>GUI.as:336-345</c>).</summary>
        public RectTransform DialogueWindow;

        private RectTransform _self;

        /// <summary>The canvas rect this scaler lays out against. Null before <c>Awake</c>.</summary>
        public RectTransform CanvasRect => _self;

        /// <summary>Current canvas width in canvas units. 0 before the first layout pass.</summary>
        public float CanvasWidth => _self != null ? _self.rect.width : 0f;

        /// <summary>Current canvas height in canvas units. 0 before the first layout pass.</summary>
        public float CanvasHeight => _self != null ? _self.rect.height : 0f;

        /// <summary>How many slots are registered. Exposed so a test can assert the HUD registered its set.</summary>
        public int RegisteredCount => _registered.Count;

        private void Awake()
        {
            _self = GetComponent<RectTransform>();
        }

        private void OnEnable()
        {
            // OnEnable can run before Awake on a component added at runtime in some orders; re-resolve.
            if (_self == null) _self = GetComponent<RectTransform>();
            ApplyAll();
        }

        private void Start()
        {
            // The canvas rect is only correct once the CanvasScaler has run, which happens after
            // OnEnable. Applying again here is cheap and is what makes the first frame correct rather
            // than the second.
            ApplyAll();
        }

        /// <summary>
        /// Binds a rect to a slot and places it immediately. Idempotent: registering the same slot
        /// twice replaces the rect rather than stacking a second entry, so a rebuild does not leave the
        /// scaler laying out an object that has been destroyed.
        /// </summary>
        public void Register(HudSlot slot, RectTransform rect)
        {
            if (rect == null) return;

            for (int i = 0; i < _registered.Count; i++)
            {
                if (_registered[i].Key != slot) continue;

                _registered[i] = new KeyValuePair<HudSlot, RectTransform>(slot, rect);
                HudLayout.Apply(slot, rect);
                return;
            }

            _registered.Add(new KeyValuePair<HudSlot, RectTransform>(slot, rect));
            HudLayout.Apply(slot, rect);
        }

        /// <summary>
        /// Re-applies the whole table. Public so a caller that changed a state the layout depends on —
        /// the pet bar appearing, say, which shifts three elements (<c>GUI.as:1063-1083</c>) — can ask
        /// for a pass without waiting for a resize.
        /// </summary>
        public void ApplyAll()
        {
            if (_self == null) _self = GetComponent<RectTransform>();

            for (int i = 0; i < _registered.Count; i++)
            {
                KeyValuePair<HudSlot, RectTransform> entry = _registered[i];

                // A destroyed rect compares equal to null through Unity's overload; skipping it is what
                // stops a scene teardown throwing out of a layout pass.
                if (entry.Value == null) continue;

                HudLayout.Apply(entry.Key, entry.Value);
            }

            ApplyScaledSpecials();
        }

        private void ApplyScaledSpecials()
        {
            float width = CanvasWidth;
            float height = CanvasHeight;
            if (width <= 0f || height <= 0f) return;

            if (SatsLayer != null)
            {
                Vector2 scale = HudLayout.SatsScale(width, height);
                SatsLayer.localScale = new Vector3(scale.x, scale.y, 1f);
            }

            if (ToastBox != null)
            {
                ToastBox.sizeDelta = new Vector2(HudLayout.ToastWidth(width), ToastBox.sizeDelta.y);
            }

            if (DialogueWindow != null)
            {
                float scale = HudLayout.DialogueScale(width);
                DialogueWindow.localScale = new Vector3(scale, scale, 1f);
                DialogueWindow.anchoredPosition = new Vector2(HudLayout.DialogueCentreOffset(width), 0f);
            }
        }

        /// <summary>
        /// The resize trigger. See the class summary for why this message rather than a poll.
        /// </summary>
        private void OnRectTransformDimensionsChange()
        {
            ApplyAll();
        }
    }
}
