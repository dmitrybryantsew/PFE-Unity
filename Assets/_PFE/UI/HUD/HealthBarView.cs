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
            if (_viewModel != null)
            {
                BindToViewModel(_viewModel);
            }
            else
            {
                Debug.LogWarning("[HealthBarView] No ViewModel assigned - health bar will not update");
            }
        }

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

            // Bind health percentage to slider
            _viewModel.HealthPercent.Subscribe(percent =>
            {
                if (_healthSlider != null)
                {
                    _healthSlider.value = percent;
                }

                // Update color based on health level
                UpdateHealthColor(percent);

            }).AddTo(_disposables);

            // Bind health text if assigned
            if (_healthText != null)
            {
                _viewModel.CurrentHealth.Subscribe(_ =>
                {
                    _healthText.text = _viewModel.GetHealthText();
                }).AddTo(_disposables);
            }

            // Bind alive state (e.g., disable bar when dead)
            _viewModel.IsAlive.Subscribe(alive =>
            {
                if (_healthSlider != null)
                {
                    _healthSlider.interactable = alive;
                }

                // Optional: Show/hide bar based on alive state
                // gameObject.SetActive(alive);

            }).AddTo(_disposables);

            Debug.Log("[HealthBarView] Bound to ViewModel");
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
