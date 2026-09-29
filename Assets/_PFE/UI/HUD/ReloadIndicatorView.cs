using UnityEngine;
using UnityEngine.UI;
using R3;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Reload indicator view component.
    /// Shows reload progress as a filled circle or bar.
    /// Only visible when weapon is reloading.
    ///
    /// Setup:
    /// 1. Attach this script to a UI GameObject with an Image component
    /// 2. Set Image Type to "Filled" (Radial or Horizontal)
    /// 3. Assign HUDViewModel reference
    /// 4. Indicator automatically shows/hides and fills during reload
    /// </summary>
    public class ReloadIndicatorView : MonoBehaviour
    {
        [Header("UI Components")]
        [SerializeField]
        [Tooltip("Image component for reload indicator (must be Filled type)")]
        private Image _reloadImage;

        [SerializeField]
        [Tooltip("Optional text for reload percentage")]
        private Text _reloadText;

        [Header("Data Source")]
        [SerializeField]
        [Tooltip("ViewModel to bind to")]
        private HUDViewModel _viewModel;

        [Header("Visual Settings")]
        [SerializeField]
        [Tooltip("Color of reload indicator")]
        private Color _reloadColor = new Color(1f, 0.5f, 0f); // Orange

        [SerializeField]
        [Tooltip("Show/hide indicator based on reload state")]
        private bool _autoShowHide = true;

        private CompositeDisposable _disposables;
        private Color _originalColor;

        /// <summary>True once <see cref="BindToViewModel"/> has run — see the note in HealthBarView.</summary>
        private bool _bound;

        private void Awake()
        {
            // Auto-find image if not assigned
            if (_reloadImage == null)
            {
                _reloadImage = GetComponent<Image>();
            }

            _disposables = new CompositeDisposable();

            // Store original color
            if (_reloadImage != null)
            {
                _originalColor = _reloadImage.color;
            }
        }

        private void Start()
        {
            if (_viewModel != null && !_bound)
            {
                BindToViewModel(_viewModel);
            }
            else if (_viewModel == null)
            {
                Debug.LogWarning("[ReloadIndicatorView] No ViewModel assigned - reload indicator will not update");

                // Hide by default if no ViewModel
                if (_autoShowHide && gameObject.activeSelf)
                {
                    gameObject.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Assign the ViewModel <b>without</b> binding; the view binds itself in <c>Start</c>.
        /// See the equivalent note on <see cref="HealthBarView.SetViewModel"/>.
        /// </summary>
        public void SetViewModel(HUDViewModel viewModel) => _viewModel = viewModel;

        /// <summary>
        /// Bind to HUDViewModel for reactive updates.
        /// </summary>
        public void BindToViewModel(HUDViewModel viewModel)
        {
            _viewModel = viewModel;

            if (_viewModel == null)
            {
                Debug.LogError("[ReloadIndicatorView] Cannot bind to null ViewModel");
                return;
            }

            // Replace, don't accumulate — see HealthBarView.BindToViewModel.
            _disposables?.Dispose();
            _disposables = new CompositeDisposable();
            _bound = true;

            // Bind reload progress. Guarded because the loadout may not have equipped yet, in which
            // case HUDViewModel leaves these properties null and Rebind() will re-enter here.
            // Explicit ifs rather than `x?.Subscribe(...).AddTo(...)` — see AmmoCounterView.
            if (_viewModel.ReloadProgress != null)
            {
                _viewModel.ReloadProgress.Subscribe(progress =>
                {
                    if (_reloadImage != null)
                    {
                        _reloadImage.fillAmount = progress;
                    }

                    // Update reload text if assigned
                    if (_reloadText != null)
                    {
                        _reloadText.text = $"{Mathf.RoundToInt(progress * 100)}%";
                    }

                }).AddTo(_disposables);
            }

            // Bind reload state to show/hide indicator
            if (_viewModel.IsReloading != null)
            {
                _viewModel.IsReloading.Subscribe(isReloading =>
                {
                    if (_autoShowHide)
                    {
                        gameObject.SetActive(isReloading);
                    }

                    // Change color when reloading
                    if (_reloadImage != null)
                    {
                        _reloadImage.color = isReloading ? _reloadColor : _originalColor;
                    }

                }).AddTo(_disposables);

                // Set initial state. Read the current value rather than subscribing a second time
                // just to sample it — the old code added a permanent extra subscriber per bind.
                //
                // CurrentValue, not Value: IsReloading is a ReadOnlyReactiveProperty<bool>, and R3
                // only puts `Value` on the mutable ReactiveProperty<T>. Same trap as AmmoCounterView.
                if (_autoShowHide)
                {
                    gameObject.SetActive(_viewModel.IsReloading.CurrentValue);
                }
            }

            Debug.Log("[ReloadIndicatorView] Bound to ViewModel");
        }

        /// <summary>
        /// Manually set reload progress (for testing).
        /// </summary>
        public void SetReloadProgress(float progress)
        {
            if (_reloadImage != null)
            {
                _reloadImage.fillAmount = Mathf.Clamp01(progress);
            }
        }

        /// <summary>
        /// Manually show/hide indicator (for testing).
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (_autoShowHide)
            {
                gameObject.SetActive(visible);
            }
        }

        private void OnDestroy()
        {
            _disposables?.Dispose();
        }
    }
}
