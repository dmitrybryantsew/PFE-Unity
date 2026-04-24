using UnityEngine;
using UnityEngine.UI;
using R3;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Ammo counter view component.
    /// Binds to HUDViewModel and displays current ammo as text and/or icon fill.
    ///
    /// Setup:
    /// 1. Attach this script to a UI GameObject
    /// 2. Assign Text component (for "12 / 12" display)
    /// 3. Assign HUDViewModel reference
    /// 4. Counter automatically updates when ammo changes
    /// </summary>
    public class AmmoCounterView : MonoBehaviour
    {
        [Header("UI Components")]
        [SerializeField]
        [Tooltip("Text component for ammo count (e.g., '12 / 12')")]
        private Text _ammoText;

        [SerializeField]
        [Tooltip("Optional icon/image to show ammo state")]
        private Image _ammoIcon;

        [Header("Data Source")]
        [SerializeField]
        [Tooltip("ViewModel to bind to")]
        private HUDViewModel _viewModel;

        [Header("Visual Settings")]
        [SerializeField]
        [Tooltip("Color for full ammo (> 50%)")]
        private Color _fullAmmoColor = Color.white;

        [SerializeField]
        [Tooltip("Color for low ammo (<= 50%)")]
        private Color _lowAmmoColor = Color.yellow;

        [SerializeField]
        [Tooltip("Color for empty ammo (0)")]
        private Color _emptyAmmoColor = Color.red;

        [SerializeField]
        [Tooltip("Show 'RELOADING' text when reloading")]
        private bool _showReloadText = true;

        [SerializeField]
        [Tooltip("Text to display when reloading")]
        private string _reloadText = "RELOADING...";

        private CompositeDisposable _disposables;
        private string _previousAmmoText;

        private void Awake()
        {
            // Auto-find text if not assigned
            if (_ammoText == null)
            {
                _ammoText = GetComponent<Text>();
            }

            _disposables = new CompositeDisposable();
        }

        private void Start()
        {
            if (_viewModel != null)
            {
                BindToViewModel(_viewModel);
            }
            else
            {
                Debug.LogWarning("[AmmoCounterView] No ViewModel assigned - ammo counter will not update");
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
                Debug.LogError("[AmmoCounterView] Cannot bind to null ViewModel");
                return;
            }

            // Bind current ammo changes
            _viewModel.CurrentAmmo.Subscribe(_ =>
            {
                UpdateAmmoDisplay();
            }).AddTo(_disposables);

            // Bind reload state to show/hide reload text
            _viewModel.IsReloading.Subscribe(isReloading =>
            {
                if (isReloading && _showReloadText)
                {
                    _previousAmmoText = _ammoText?.text;
                    if (_ammoText != null)
                    {
                        _ammoText.text = _reloadText;
                    }
                }
                else if (_ammoText != null)
                {
                    _ammoText.text = _previousAmmoText ?? _viewModel.GetAmmoText();
                }

                UpdateAmmoColor();

            }).AddTo(_disposables);

            // Bind ammo percent for color updates
            _viewModel.AmmoPercent.Subscribe(_ =>
            {
                UpdateAmmoColor();
            }).AddTo(_disposables);

            // Initial update
            UpdateAmmoDisplay();

            Debug.Log("[AmmoCounterView] Bound to ViewModel");
        }

        /// <summary>
        /// Update ammo text display.
        /// </summary>
        private void UpdateAmmoDisplay()
        {
            if (_ammoText == null || _viewModel == null)
                return;

            // Always update text - the IsReloading binding will handle showing "RELOADING..."
            _ammoText.text = _viewModel.GetAmmoText();
            UpdateAmmoColor();
        }

        /// <summary>
        /// Update ammo color based on ammo level.
        /// </summary>
        private void UpdateAmmoColor()
        {
            if (_ammoIcon == null || _viewModel == null)
                return;

            // Get ammo percent from the reactive property
            float ammoPercent = 1f;
            _viewModel.AmmoPercent.Subscribe(v => ammoPercent = v).AddTo(_disposables);

            if (ammoPercent <= 0)
            {
                _ammoIcon.color = _emptyAmmoColor;
            }
            else if (ammoPercent <= 0.5f)
            {
                _ammoIcon.color = _lowAmmoColor;
            }
            else
            {
                _ammoIcon.color = _fullAmmoColor;
            }
        }

        /// <summary>
        /// Manually set ammo count (for testing).
        /// </summary>
        public void SetAmmo(int current, int max)
        {
            if (_ammoText != null)
            {
                _ammoText.text = $"{current} / {max}";
            }
            UpdateAmmoColor();
        }

        private void OnDestroy()
        {
            _disposables?.Dispose();
        }
    }
}
