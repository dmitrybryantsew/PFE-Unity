using UnityEngine;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Pins a HUD element to a point in the <b>world</b> rather than to a corner of the screen.
    ///
    /// <para><b>Why this exists.</b> Three of the Oracle's readouts are not screen furniture at all —
    /// they hang off the thing they describe and are re-projected every frame:</para>
    /// <list type="bullet">
    /// <item><c>reloadBar</c> is a child of the player's own world sprite
    /// (<c>UnitPlayer.as:338-340</c>) at <c>y = -scY - 10</c> (<c>:367</c>) — above the player's
    /// head, not on the HUD;</item>
    /// <item><c>pr_bar</c> (the hold bar) is re-projected onto its target every frame —
    /// <c>cam.setKoord(this.pr_bar, this.gg.actionObj.owner.X, this.gg.actionObj.owner.Y)</c>
    /// (<c>GUI.as:1302</c>);</item>
    /// <item><c>celobj</c> (the interaction prompt) likewise —
    /// <c>cam.setKoord(this.celobj, this.celObj.X, this.celObj.Y)</c> (<c>GUI.as:1461</c>).</item>
    /// </list>
    /// <para>Putting those on a fixed screen anchor is a visible divergence, and it is exactly the
    /// class of defect <see cref="HudLayout"/> tags <see cref="HudAnchorSource.PortChoice"/> rather
    /// than quoting as a citation.</para>
    ///
    /// <para><b>How the projection is done, and why the offset is in <i>world</i> units.</b> The
    /// anchor point is moved by <see cref="WorldOffset"/> <i>before</i> it is projected, then the
    /// projected screen point is converted into the canvas' own local space
    /// (<see cref="RectTransformUtility.ScreenPointToLocalPointInRectangle"/>). Offsetting in world
    /// space is what the Oracle does — <c>reloadbar.y = -scY - 10</c> is 80 source pixels measured in
    /// the sprite's own space — and it keeps the gap between the label and its object constant at every
    /// resolution and every camera size, which a pixel offset after projection would not.</para>
    ///
    /// <para><b>Requirement: the element must be a direct child of the root canvas.</b> The conversion
    /// yields a point relative to the canvas' pivot (its centre), and it is written to
    /// <c>anchoredPosition</c> — which is relative to the element's own parent anchor. The HUD builds
    /// every top-level element with a centre anchor on the canvas (<c>HudBootstrapper.CreateHudElement</c>),
    /// so the two coincide. An element nested under a repositioned parent would need its own parent
    /// offset added, and this class deliberately does not guess at one.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldAnchoredHudElement : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("The RectTransform moved. Defaults to this GameObject's own RectTransform.")]
        private RectTransform _rect;

        [SerializeField]
        [Tooltip("Camera used for the projection. Defaults to Camera.main, re-resolved while null.")]
        private Camera _camera;

        [SerializeField]
        [Tooltip("Offset from the anchor, in WORLD units, applied before projection. Positive y is up.")]
        private Vector3 _worldOffset = new Vector3(0f, 0.5f, 0f);

        /// <summary>A live transform to follow; takes precedence over <see cref="SetWorldPoint"/>.</summary>
        private Transform _target;

        /// <summary>A fixed world point, used when there is no transform to follow.</summary>
        private Vector3 _worldPoint;

        private bool _hasWorldPoint;

        /// <summary>Cached root canvas rect. Resolved lazily so a runtime-built HUD needs no wiring.</summary>
        private RectTransform _canvasRect;

        /// <summary>Offset from the anchor, in world units, applied before projection. Positive y is up.</summary>
        public Vector3 WorldOffset
        {
            get => _worldOffset;
            set => _worldOffset = value;
        }

        /// <summary>True while there is something to project. False means <see cref="Apply"/> does nothing.</summary>
        public bool HasAnchor => _target != null || _hasWorldPoint;

        private void Awake()
        {
            if (_rect == null)
            {
                _rect = transform as RectTransform;
            }
        }

        /// <summary>
        /// Follow <paramref name="target"/> every frame. A null target clears the anchor — use
        /// <see cref="SetWorldPoint"/> instead when the thing to follow is not a transform (the hold
        /// bar's target is reported as a position, not an object).
        /// </summary>
        public void SetTarget(Transform target)
        {
            _target = target;
            _hasWorldPoint = false;
        }

        /// <summary>Pin to a fixed world point until told otherwise.</summary>
        public void SetWorldPoint(Vector3 worldPoint)
        {
            _worldPoint = worldPoint;
            _hasWorldPoint = true;
            _target = null;
        }

        /// <summary>Stop projecting anything. The element keeps whatever position it last had.</summary>
        public void ClearAnchor()
        {
            _target = null;
            _hasWorldPoint = false;
        }

        // LateUpdate, not Update: the thing being followed (the player, a door) has already moved by
        // this point in the frame, so the label cannot lag its object by one frame. This is the same
        // frame/tick split ActionProgressBarView documents from the other side.
        private void LateUpdate()
        {
            Apply();
        }

        /// <summary>
        /// Project the anchor and move the rect. Public so a test or a caller that drives the layout
        /// itself can run it without a frame.
        /// </summary>
        public void Apply()
        {
            if (_rect == null)
            {
                return;
            }

            Vector3 world;
            if (_target != null)
            {
                world = _target.position;
            }
            else if (_hasWorldPoint)
            {
                world = _worldPoint;
            }
            else
            {
                return;
            }

            if (_camera == null)
            {
                // Re-resolved rather than cached once: the HUD is built in code at runtime and the
                // gameplay camera is created after it, so a one-shot Awake lookup would leave this
                // null forever. Camera.main is cheap and cached by Unity, unlike a scene scan.
                _camera = Camera.main;
                if (_camera == null)
                {
                    return;
                }
            }

            if (_canvasRect == null)
            {
                Canvas canvas = _rect.GetComponentInParent<Canvas>();
                if (canvas == null)
                {
                    return;
                }

                // rootCanvas, so an element parented to a nested canvas still projects into the
                // screen-sized space its coordinates are measured in.
                Canvas root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
                _canvasRect = root.transform as RectTransform;
                if (_canvasRect == null)
                {
                    return;
                }
            }

            Vector3 screen = _camera.WorldToScreenPoint(world + _worldOffset);

            // z < 0 means the point is behind the camera. Projecting it would fold the element onto
            // the wrong side of the screen, which reads as a label that teleports when the player
            // walks past the camera plane.
            if (screen.z < 0f)
            {
                return;
            }

            // A null camera is correct here: the HUD canvas is ScreenSpaceOverlay, for which screen
            // space and canvas space differ only by the scaler's factor — which this conversion
            // applies. Passing a camera would be wrong for that mode.
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, screen, null, out Vector2 local))
            {
                _rect.anchoredPosition = local;
            }
        }
    }
}
