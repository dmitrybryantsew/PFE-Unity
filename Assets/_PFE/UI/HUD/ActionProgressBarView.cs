using UnityEngine;
using UnityEngine.UI;
using PFE.Systems.Interaction;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The hold bar: fills while the player holds the action key on a timed object, and is invisible
    /// the rest of the time. The presentation half of AS3's action timer, whose fill is
    /// <c>perc = (mt_action - t_action) / mt_action</c> (<c>GUI.as:1288</c>).
    ///
    /// <para><b>It records on the tick and draws on the frame.</b> <see cref="Show"/> and
    /// <see cref="Hide"/> are called from the simulation tick — that is what
    /// <see cref="IActionProgressView"/> promises — so they must not touch the UI. They store the value
    /// and set a dirty flag; <see cref="LateUpdate"/> does the actual <c>Slider</c> and
    /// <c>SetActive</c> writes. This is the same sim/view split <c>ISimTickable</c> describes for
    /// position, applied to a bar: the tick owns the number, the frame owns the pixels.</para>
    ///
    /// <para><b>Why a child holds the visuals.</b> Same trap <c>ArmourBarView</c> documents: a
    /// deactivated GameObject never runs <c>Start</c> or <c>LateUpdate</c>, so hiding this component's
    /// own GameObject would freeze the bar off forever. The toggled object is a child that carries no
    /// component, so this one keeps running and can show itself again.</para>
    ///
    /// <para><b>Colour is the hold state, not decoration.</b> A bar that fills is only meaningful if it
    /// reads as "committed progress"; the fill colour is set once from the field below so it can be
    /// made distinct from the health and armour bars at a glance.</para>
    /// </summary>
    public sealed class ActionProgressBarView : MonoBehaviour, IActionProgressView
    {
        [Header("UI Components")]
        [SerializeField]
        [Tooltip("Slider that renders the fill. Auto-found on this GameObject when left empty.")]
        private Slider _slider;

        [Header("Visual Settings")]
        [SerializeField]
        [Tooltip("Fill colour while a hold is in progress.")]
        private Color _holdColour = new Color(0.95f, 0.85f, 0.2f, 0.95f);

        /// <summary>
        /// The subtree toggled for show/hide. A child rather than this GameObject — see the class
        /// remarks.
        /// </summary>
        [SerializeField]
        [Tooltip("Child object holding the bar's visuals; toggled with the hold.")]
        private GameObject _content;

        private Image _fillImage;

        // Pending state written by the tick and consumed by LateUpdate. Not a copy of the timer — just
        // the last report, so a frame that sees no report simply redraws nothing.
        private float _pendingProgress;
        private bool _pendingVisible;
        private bool _dirty;

        /// <summary>True when the bar is currently shown. For tests and inspection.</summary>
        public bool IsShown { get; private set; }

        /// <summary>The last progress reported, 0&#8594;1. For tests and inspection.</summary>
        public float LastProgress { get; private set; }

        private void Awake()
        {
            if (_slider == null)
            {
                _slider = GetComponent<Slider>();
            }

            if (_slider != null)
            {
                // A hold bar is an indicator, never a control.
                _slider.transition = Selectable.Transition.None;
                _slider.interactable = false;
                _slider.minValue = 0f;
                _slider.maxValue = 1f;

                if (_slider.fillRect != null)
                {
                    _fillImage = _slider.fillRect.GetComponent<Image>();
                }
            }

            if (_fillImage != null)
            {
                _fillImage.color = _holdColour;
            }

            // Start hidden; nothing is being held at boot.
            _pendingVisible = false;
            _dirty = true;
            ApplyPending();
        }

        /// <summary>Assign the child subtree that shows and hides with the hold.</summary>
        public void SetContent(GameObject content) => _content = content;

        /// <summary>
        /// Reports progress and makes the bar visible. Called from the tick; the actual UI write
        /// happens in <see cref="LateUpdate"/>.
        /// </summary>
        public void Show(float progress)
        {
            _pendingProgress = Mathf.Clamp01(progress);
            _pendingVisible = true;
            _dirty = true;
        }

        /// <summary>
        /// Hides the bar. Idempotent — a caller that cancels defensively will hide an already-hidden
        /// bar, and that must not cost a redundant <c>SetActive</c>.
        /// </summary>
        public void Hide()
        {
            _pendingVisible = false;
            _dirty = true;
        }

        private void LateUpdate()
        {
            if (!_dirty)
            {
                return;
            }

            ApplyPending();
        }

        private void ApplyPending()
        {
            _dirty = false;

            if (_content != null && _content.activeSelf != _pendingVisible)
            {
                _content.SetActive(_pendingVisible);
            }

            // Only touch the fill while visible: writing a Slider every frame it is hidden would
            // re-dirty the canvas for nothing.
            if (_pendingVisible && _slider != null)
            {
                _slider.value = _pendingProgress;
            }

            LastProgress = _pendingProgress;
            IsShown = _pendingVisible;
        }
    }
}
