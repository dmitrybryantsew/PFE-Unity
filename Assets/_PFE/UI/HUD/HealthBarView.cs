using UnityEngine;
using UnityEngine.UI;
using R3;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Health bar view component.
    /// Binds to HUDViewModel and displays player health as a filled bar.
    ///
    /// Setup:
    /// 1. Attach this script to a UI GameObject with a Slider component
    /// 2. Assign HUDViewModel reference (via Inspector or code)
    /// 3. Bar automatically updates when health changes
    ///
    /// Uses R3 for reactive binding - no Update() loop needed!
    /// </summary>
    public class HealthBarView : MonoBehaviour
    {
        [Header("UI Components")]
        [SerializeField]
        [Tooltip("Slider component for health bar fill")]
        private Slider _healthSlider;

        [SerializeField]
        [Tooltip("Optional text display for health value")]
        private Text _healthText;

        [Header("Data Source")]
        [SerializeField]
        [Tooltip("ViewModel to bind to")]
        private HUDViewModel _viewModel;

        [Header("Visual Settings")]
        [SerializeField]
        [Tooltip("Color for high health (> 50%)")]
        private Color _highHealthColor = Color.green;

        [SerializeField]
        [Tooltip("Color for medium health (25-50%)")]
        private Color _mediumHealthColor = Color.yellow;

        [SerializeField]
        [Tooltip("Color for low health (< 25%)")]
        private Color _lowHealthColor = Color.red;

        [SerializeField]
        [Tooltip("Image component to tint (optional)")]
        private Image _fillImage;

        private CompositeDisposable _disposables;

        /// <summary>
        /// True once <see cref="BindToViewModel"/> has run, so <see cref="Start"/> does not bind a
        /// second time. Without this, a caller that binds explicitly before <c>Start</c> (the
        /// runtime bootstrapper does) gets every subscription twice, because <c>Start</c> sees a
        /// non-null ViewModel and binds again.
        /// </summary>
        private bool _bound;

        private void Awake()
        {
            // Auto-find slider if not assigned
            if (_healthSlider == null)
            {
                _healthSlider = GetComponent<Slider>();
            }

            // Auto-find fill image if not assigned
            if (_fillImage == null && _healthSlider != null)
            {
                _fillImage = _healthSlider.fillRect?.GetComponent<Image>();
            }

            _disposables = new CompositeDisposable();
        }

        private void Start()
        {
            // If ViewModel is assigned, bind to it
            if (_viewModel != null && !_bound)
            {
                BindToViewModel(_viewModel);
            }
            else if (_viewModel == null)
            {
                Debug.LogWarning("[HealthBarView] No ViewModel assigned - health bar will not update");
            }
        }

        /// <summary>
        /// Assign the ViewModel <b>without</b> binding. The view then binds itself in
        /// <c>Start</c>, which is what lets a runtime builder hand over the reference without
        /// racing the component's own lifecycle. Use <see cref="BindToViewModel"/> to force a
        /// rebind (weapon swap) after <c>Start</c> has already run.
        /// </summary>
        public void SetViewModel(HUDViewModel viewModel) => _viewModel = viewModel;

        /// <summary>
        /// Assign the optional numeric health readout. Needed because the runtime HUD builder
        /// creates the Text as a sibling and cannot reach the serialized field.
        /// </summary>
        public void SetHealthText(Text text) => _healthText = text;

        /// <summary>
        /// Bind to HUDViewModel for reactive updates.
        /// Call this if ViewModel is not assigned in Inspector.
        /// </summary>
        /// <param name="viewModel">ViewModel to bind to</param>
        public void BindToViewModel(HUDViewModel viewModel)
        {
            _viewModel = viewModel;

            if (_viewModel == null)
            {
                Debug.LogError("[HealthBarView] Cannot bind to null ViewModel");
                return;
            }

            // Replace, don't accumulate. A rebind (weapon swap, or the HUD being built before the
            // weapon existed) subscribes to fresh ReadOnlyReactivePropertys; the previous ones are
            // disposed by HUDViewModel.Rebind() and would otherwise keep this view subscribed to
            // dead properties.
            _disposables?.Dispose();
            _disposables = new CompositeDisposable();
            _bound = true;

            // Every subscription below is guarded, because this method is legitimately called before
            // the ViewModel has any sources.
            //
            // Ordering: HudBootstrapper calls Initialize() from its Update(), and Update runs after
            // every Start() in the frame — but each view binds itself in its own Start(). So the first
            // bind always sees a ViewModel whose ReadOnlyReactiveProperty fields are still null, and
            // `null.Subscribe(...)` throws NullReferenceException *inside* R3, because Subscribe is an
            // extension method: the NRE names R3, not the real cause.
            //
            // The bootstrapper's RebindViews() does the real bind on the same frame, once Initialize()
            // has run. Leaving a subscription unbound here is therefore safe; leaving one UNGUARDED is
            // not. AmmoCounterView and ReloadIndicatorView were already guarded — this one was missed.
            int bound = 0;

            // Bind health percentage to slider
            if (_viewModel.HealthPercent != null)
            {
                _viewModel.HealthPercent.Subscribe(percent =>
                {
                    if (_healthSlider != null)
                    {
                        _healthSlider.value = percent;
                    }

                    // Update color based on health level
                    UpdateHealthColor(percent);

                }).AddTo(_disposables);
                bound++;
            }

            // Bind health text if assigned
            if (_healthText != null && _viewModel.CurrentHealth != null)
            {
                _viewModel.CurrentHealth.Subscribe(_ =>
                {
                    _healthText.text = _viewModel.GetHealthText();
                }).AddTo(_disposables);
                bound++;
            }

            // Bind alive state (e.g., disable bar when dead)
            if (_viewModel.IsAlive != null)
            {
                _viewModel.IsAlive.Subscribe(alive =>
                {
                    if (_healthSlider != null)
                    {
                        _healthSlider.interactable = alive;
                    }

                    // Optional: Show/hide bar based on alive state
                    // gameObject.SetActive(alive);

                }).AddTo(_disposables);
                bound++;
            }

            // Say so when nothing bound, rather than reporting a successful bind that did nothing.
            // If this line appears and is never followed by a fully-bound one, the bootstrapper never
            // rebound — which is the only way this view can end up permanently blank.
            Debug.Log(bound == 0
                ? "[HealthBarView] ViewModel has no sources yet — waiting for the bootstrapper to rebind."
                : $"[HealthBarView] Bound to ViewModel ({bound} subscription(s)).");
        }

        /// <summary>
        /// Update health bar color based on health percentage.
        /// </summary>
        private void UpdateHealthColor(float percent)
        {
            if (_fillImage == null)
                return;

            if (percent > 0.5f)
            {
                _fillImage.color = _highHealthColor;
            }
            else if (percent > 0.25f)
            {
                _fillImage.color = _mediumHealthColor;
            }
            else
            {
                _fillImage.color = _lowHealthColor;
            }
        }

        /// <summary>
        /// Manually set health value (for testing or non-reactive updates).
        /// </summary>
        /// <param name="percent">Health percentage 0-1</param>
        public void SetHealth(float percent)
        {
            if (_healthSlider != null)
            {
                _healthSlider.value = Mathf.Clamp01(percent);
                UpdateHealthColor(percent);
            }
        }

        private void OnDestroy()
        {
            _disposables?.Dispose();
        }
    }
}
