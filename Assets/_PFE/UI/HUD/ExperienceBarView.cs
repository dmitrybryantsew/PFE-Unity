using UnityEngine;
using UnityEngine.UI;
using R3;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Oracle <c>vis.xpBar</c> — the experience meter. A thin bar pinned above the health bar, filling
    /// with progress through the current level.
    ///
    /// <para><b>Why it is thin and why it is above the health bar.</b> The SWF places <c>xpBar</c>
    /// (30481) at <c>translate(3.80, 1.00)</c> with scale <c>(2.2999, 1.0)</c> — the x is stretched and
    /// the y is not, so the symbol is a hairline strip sitting at the very top of the 1200 x 800 sheet,
    /// one pixel row band above the health bar's <c>y = 6</c>. Read out of <c>visualGUI</c> (4651) with
    /// <c>.workbuddy-ai/tools/swf_sprite_layout.py</c>. It is not a decorative choice and it is not
    /// <c>GUI.resizeScreen</c>'s — that method never mentions <c>xpBar</c>.</para>
    ///
    /// <para><b>The value is cumulative-total arithmetic, not a stored counter.</b> See
    /// <see cref="ExperienceReadout"/>: AS3 subtracts the previous level's threshold from the lifetime
    /// total, so the bar restarts at zero on the frame the level rises instead of jumping.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ExperienceBarView : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Slider driving the experience fill")]
        private Slider _xpSlider;

        [SerializeField]
        [Tooltip("ViewModel to bind to")]
        private HUDViewModel _viewModel;

        private CompositeDisposable _disposables;

        /// <summary>See <see cref="HealthBarView"/> — stops <c>Start</c> double-binding a forced bind.</summary>
        private bool _bound;

        private void Awake()
        {
            if (_xpSlider == null)
                _xpSlider = GetComponent<Slider>();

            _disposables = new CompositeDisposable();
        }

        private void Start()
        {
            if (_viewModel != null && !_bound)
            {
                BindToViewModel(_viewModel);
            }
            else if (_viewModel == null)
            {
                Debug.LogWarning("[ExperienceBarView] No ViewModel assigned - XP bar will not update");
            }
        }

        /// <summary>Assign the ViewModel <b>without</b> binding, so the view binds in its own <c>Start</c>.</summary>
        public void SetViewModel(HUDViewModel viewModel) => _viewModel = viewModel;

        public void BindToViewModel(HUDViewModel viewModel)
        {
            _viewModel = viewModel;

            if (_viewModel == null)
            {
                Debug.LogError("[ExperienceBarView] Cannot bind to null ViewModel");
                return;
            }

            _disposables?.Dispose();
            _disposables = new CompositeDisposable();
            _bound = true;

            int bound = 0;

            // Null-guarded for the same reason as every other HUD view: the bootstrapper's Initialize()
            // runs from Update, after this Start().
            if (_viewModel.ExperiencePercent != null)
            {
                _viewModel.ExperiencePercent.Subscribe(percent =>
                {
                    if (_xpSlider != null) _xpSlider.value = percent;
                }).AddTo(_disposables);
                bound++;
            }

            // No "no character block" branch: without CharacterStats the property is simply never
            // created, so the bar keeps its authored 0 fill and the log below says so. A bar at zero
            // is the honest reading for "the port has no XP source bound", and it is visible.
            Debug.Log(bound == 0
                ? "[ExperienceBarView] ViewModel has no experience source (no CharacterStats bound)."
                : "[ExperienceBarView] Bound to ViewModel (1 subscription).");
        }

        /// <summary>Manually set the fill (tests / non-reactive callers).</summary>
        public void SetProgress(float percent)
        {
            if (_xpSlider != null) _xpSlider.value = Mathf.Clamp01(percent);
        }

        private void OnDestroy()
        {
            _disposables?.Dispose();
        }
    }
}
