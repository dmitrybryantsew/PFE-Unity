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

        /// <summary>True once <see cref="BindToViewModel"/> has run — see the note in HealthBarView.</summary>
        private bool _bound;

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
            if (_viewModel != null && !_bound)
            {
                BindToViewModel(_viewModel);
            }
            else if (_viewModel == null)
            {
                Debug.LogWarning("[AmmoCounterView] No ViewModel assigned - ammo counter will not update");
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
                Debug.LogError("[AmmoCounterView] Cannot bind to null ViewModel");
                return;
            }

            // Replace, don't accumulate — see HealthBarView.BindToViewModel.
            _disposables?.Dispose();
            _disposables = new CompositeDisposable();
            _bound = true;

            // Bind current ammo changes. Guarded because the loadout may not have equipped its
            // starting weapon yet; HUDViewModel leaves the ammo properties null until Rebind().
            // Written as explicit ifs rather than `x?.Subscribe(...).AddTo(...)`: the null-conditional
            // form makes the safety of the AddTo call depend on chain short-circuiting rules, which is
            // not something a reader should have to reason about.
            if (_viewModel.CurrentAmmo != null)
            {
                _viewModel.CurrentAmmo.Subscribe(_ =>
                {
                    UpdateAmmoDisplay();
                }).AddTo(_disposables);
            }

            // Bind reload state to show/hide reload text
            if (_viewModel.IsReloading != null)
            {
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
            }

            // Bind ammo percent for color updates
            if (_viewModel.AmmoPercent != null)
            {
                _viewModel.AmmoPercent.Subscribe(_ =>
                {
                    UpdateAmmoColor();
                }).AddTo(_disposables);
            }

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
            if (_ammoIcon == null || _viewModel?.AmmoPercent == null)
                return;

            // Read the current value. This used to Subscribe() and stash the value in a local,
            // which added a brand-new subscription to _disposables on every call — and it is called
            // from inside the AmmoPercent subscription, so each ammo change spawned another
            // subscriber that fired immediately. Quadratic growth on a hot path.
            //
            // CurrentValue, not Value: R3's ReadOnlyReactiveProperty<T> has no `Value` — only the
            // mutable ReactiveProperty<T> does. (CharacterStatsView reads .CurrentValue off the same
            // type.)
            float ammoPercent = _viewModel.AmmoPercent.CurrentValue;

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
