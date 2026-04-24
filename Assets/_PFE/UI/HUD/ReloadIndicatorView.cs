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
            if (_viewModel != null)
            {
                BindToViewModel(_viewModel);
            }
            else
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

            // Bind reload progress
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

            // Bind reload state to show/hide indicator
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

            // Set initial state
            if (_autoShowHide)
            {
                bool isReloading = false;
                _viewModel.IsReloading.Subscribe(v => isReloading = v).AddTo(_disposables);
                gameObject.SetActive(isReloading);
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
