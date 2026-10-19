using UnityEngine;
using UnityEngine.UI;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The rising label a pickup shows — the port of <c>GUI.floatText</c> (<c>inter/GUI.as:1608-1638</c>)
    /// and the <c>take</c> particle it emits.
    ///
    /// <para><b>Why this is HUD text and not a particle.</b> The oracle spawns a <c>take</c> particle
    /// (<c>ParticleDefinitions</c> row 4228: <c>Vis: visBulb</c>, <c>Sloy 5</c>, <c>MinLiv 50</c>,
    /// <c>DY -1</c>, <c>Brake 1</c>) and then fills a text field <i>inside</i> the symbol
    /// (<c>Emitter.as:375-379</c>: <c>_loc6_.vis.text.text.htmlText = param4.txt</c>). The port's
    /// particle pipeline draws sprites only, and <c>ParticleSpec</c>'s own remarks already record the
    /// decision: <i>"<c>gui</c>/<c>take</c> carry text and render a text field, not frames. They belong
    /// with the HUD, not the particle system."</i> So the numbers below are the particle row's numbers,
    /// applied to a projected label instead of a sprite.</para>
    ///
    /// <para><b>Where it is anchored, and the trap.</b> <c>floatText</c> is called with
    /// <c>this.gg.X, this.gg.Y</c> (<c>Invent.as:1291</c>) — <c>gg</c> is the <b>unit that owns the
    /// inventory</b>, i.e. the player, <b>not the item being taken</b>. So the label rises from the
    /// player's feet, not from the loot's position. Anchoring it to the item looks right in a screenshot
    /// and is wrong whenever the player takes something from an arm's length away.</para>
    ///
    /// <para><b>Stacking.</b> Consecutive takes do not overlap: <c>GUI</c> keeps a <c>float_dy</c>
    /// accumulator that steps <b>−20 px</b> per call while <c>t_bulb &gt; 0</c> and resets to 0
    /// otherwise, then arms <c>t_bulb = 10</c> (<c>:1615-1637</c>). So a burst of pickups within ten
    /// frames fans upward by 20 px each, and the fan resets as soon as ten frames pass with no take.</para>
    ///
    /// <para><b>The fade is the particle's own.</b> <c>Part.step</c> sets
    /// <c>vis.alpha = liv / 10</c> only while <c>liv &lt; 9</c> (<c>Part.as:174-186</c>), so the label is
    /// fully opaque for its first 41 frames and then steps 0.8, 0.7 … 0.1 over the last nine. There is no
    /// fade-<i>in</i>: <c>take</c> sets <c>PreAlph: 0</c>.</para>
    ///
    /// <para><b>Not modelled: the two edge clamps.</b> The oracle pins the label inside the room —
    /// <c>if (param3 &lt; 40) param3 -= 100</c> and <c>param2</c> clamped to <c>[50, limX - 50]</c>
    /// (<c>:1624-1632</c>). Both are expressed in <b>room-local AS3 pixels</b> and this view is handed a
    /// Unity world point, with no room in hand to convert against. The effect is confined to pickups
    /// within ~80 px of a room edge, where the label can now poke over the wall.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PickupFloatTextView : MonoBehaviour
    {
        /// <summary><c>take</c>'s <c>MinLiv</c> — the label lives 50 frames.</summary>
        public const int LifeFrames = 50;

        /// <summary><c>take</c>'s <c>DY</c> — one source pixel per frame, and <c>Brake 1</c> means it never decays.</summary>
        public const float RisePerFramePixels = 1f;

        /// <summary><c>param3 - 80</c> (<c>GUI.as:1623</c>) — the label starts 80 source px above the anchor.</summary>
        public const float LiftPixels = 80f;

        /// <summary><c>float_dy -= 20</c> (<c>:1621</c>) — the per-call fan step.</summary>
        public const float StackStepPixels = 20f;

        /// <summary><c>this.t_bulb = 10</c> (<c>:1637</c>) — how long the fan stays armed.</summary>
        public const int StackWindowFrames = 10;

        /// <summary>Source pixels per world unit — the same 100 px scale the rest of the port uses.</summary>
        private const float PixelsToUnits = 0.01f;

        /// <summary>
        /// How many labels can be in flight at once. <b>A port choice, not an oracle number:</b>
        /// <c>take</c> declares <c>MaxKol: 0</c> (no per-type cap) and <c>Imp: 1</c> (bypasses the global
        /// part budget), so the oracle really is unbounded here. Twelve covers a full loot spill — a
        /// container emptying is the busiest case — and beyond that the oldest label is recycled rather
        /// than a thirteenth object being allocated mid-frame.
        /// </summary>
        public const int PoolSize = 12;

        private sealed class Label
        {
            public RectTransform Rect;
            public Text Text;
            public CanvasGroup Group;
            public WorldAnchoredHudElement Anchor;
            public int Life;
            public float StackPixels;
        }

        private readonly Label[] _labels = new Label[PoolSize];
        private int _next;

        /// <summary>AS3 <c>t_bulb</c> — frames left in which a new take fans upward.</summary>
        private int _tBulb;

        /// <summary>AS3 <c>float_dy</c> — the accumulated fan offset, always ≤ 0.</summary>
        private float _floatDy;

        /// <summary>Labels created, for tests.</summary>
        public int PoolBuilt => _built;

        private int _built;

        /// <summary>Live labels, for the F2 readout.</summary>
        public int LiveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _built; i++)
                {
                    if (_labels[i].Life > 0) n++;
                }
                return n;
            }
        }

        /// <summary>
        /// Builds the pool. <paramref name="canvas"/> must be the <b>root canvas</b> transform:
        /// <see cref="WorldAnchoredHudElement"/> writes an <c>anchoredPosition</c> measured from the
        /// canvas pivot, so a label nested under a repositioned parent would land in the wrong place.
        /// </summary>
        public void Construct(Transform canvas, Font font, int fontSize = 18)
        {
            if (_built > 0) return;

            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject($"Pickup Label {i}", typeof(RectTransform));
                var rect = (RectTransform)go.transform;
                rect.SetParent(canvas, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(320f, 28f);

                var group = go.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.blocksRaycasts = false;
                group.interactable = false;

                var text = go.AddComponent<Text>();
                text.font = font;
                text.fontSize = fontSize;
                text.alignment = TextAnchor.MiddleCenter;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;
                text.text = string.Empty;

                var anchor = go.AddComponent<WorldAnchoredHudElement>();

                _labels[i] = new Label
                {
                    Rect = rect,
                    Text = text,
                    Group = group,
                    Anchor = anchor,
                    Life = 0,
                };
            }

            _built = PoolSize;
            _next = 0;
        }

        /// <summary>
        /// One <c>gui.floatText</c> call. <paramref name="colourIndex"/> is the Flash style index from
        /// <see cref="PickupFeedbackRules.FloatColour"/>; <paramref name="worldPosition"/> is the
        /// <b>player's</b> position, not the item's.
        /// </summary>
        public void Show(string label, int colourIndex, Vector3 worldPosition)
        {
            if (_built == 0) return;
            if (string.IsNullOrEmpty(label)) return;

            // GUI.as:1615-1622. The fan resets only when the previous take has aged out of its window;
            // within the window each new label steps another 20 px up.
            if (_tBulb <= 0) _floatDy = 0f;
            else _floatDy -= StackStepPixels;

            Label slot = Rent();
            slot.Life = LifeFrames;
            slot.StackPixels = _floatDy;

            slot.Text.text = label;
            slot.Text.color = PickupFeedbackRules.FlashColour(colourIndex);
            slot.Group.alpha = 1f;

            // The anchor is the fixed world point; the rise is folded into WorldOffset, which
            // WorldAnchoredHudElement applies BEFORE projection so the lift stays constant in world
            // units at every camera size (see its remarks).
            slot.Anchor.SetWorldPoint(worldPosition);
            ApplyOffset(slot, risenPixels: 0f);

            _tBulb = StackWindowFrames;
        }

        /// <summary>Drops every live label. A room change wants this.</summary>
        public void Clear()
        {
            for (int i = 0; i < _built; i++)
            {
                Retire(_labels[i]);
            }

            _tBulb = 0;
            _floatDy = 0f;
        }

        private void Update()
        {
            // GUI.step:2017-2019 — the fan window counts down once per frame.
            if (_tBulb > 0) _tBulb--;

            for (int i = 0; i < _built; i++)
            {
                Label label = _labels[i];
                if (label.Life <= 0) continue;

                label.Life--;

                if (label.Life <= 0)
                {
                    Retire(label);
                    continue;
                }

                // Part.as:174-186 — opaque until the last nine frames, then liv/10. No fade-in:
                // `take` sets PreAlph 0.
                label.Group.alpha = label.Life < 9 ? label.Life / 10f : 1f;

                ApplyOffset(label, (LifeFrames - label.Life) * RisePerFramePixels);
            }
        }

        /// <summary>
        /// Lift + fan + rise, in world units, written into the anchor's offset. Positive is up, which is
        /// the mirror of AS3's <c>param3 - 80</c> and <c>float_dy -= 20</c> in a Y-down space.
        /// </summary>
        private static void ApplyOffset(Label label, float risenPixels)
        {
            float pixels = LiftPixels - label.StackPixels + risenPixels;
            label.Anchor.WorldOffset = new Vector3(0f, pixels * PixelsToUnits, 0f);
        }

        private Label Rent()
        {
            // Prefer a free slot; otherwise take the oldest live one, which is the one closest to
            // expiring anyway.
            for (int i = 0; i < _built; i++)
            {
                int index = (_next + i) % _built;
                if (_labels[index].Life <= 0)
                {
                    _next = (index + 1) % _built;
                    return _labels[index];
                }
            }

            Label recycled = _labels[_next];
            _next = (_next + 1) % _built;
            return recycled;
        }

        private static void Retire(Label label)
        {
            label.Life = 0;
            label.Group.alpha = 0f;
            label.Anchor.ClearAnchor();
            label.Text.text = string.Empty;
        }
    }
}
