using UnityEngine;
using UnityEngine.UI;
using R3;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Armour condition bar. Fills with the equipped plate's remaining integrity, and hides itself
    /// when nothing is equipped.
    ///
    /// <para><b>This is an addition, not a port — and that is worth stating plainly.</b> AS3's only
    /// armour bar is <c>Unit.as:3004-3011</c>, and it is <i>not</i> a player bar:</para>
    ///
    /// <list type="bullet">
    /// <item><description>It lives on <c>hpBar</c>, which is created only in <c>Unit.as:2885</c>.
    /// <c>UnitPlayer</c> never references <c>hpbar</c> at all — the sole hit in that file is
    /// <c>this.pet.hpbar</c> — so <b>the player has no such bar</b>.</description></item>
    /// <item><description>It reads <c>armor_hp / armor_maxhp</c>, the <b>unit pool</b> fields. The only
    /// code that consumes them is guarded <c>!this.player</c> (<c>Unit.as:3578</c>), and the only
    /// assignments are the unit XML parse, a level-up, and two subclasses — so for a player they stay
    /// <c>0</c> and the oracle's own fill would divide by zero.</description></item>
    /// <item><description>Its visibility gate is <c>armor_qual &gt; 0</c>, the pool's <c>@aqual</c>,
    /// which a player never populates.</description></item>
    /// </list>
    ///
    /// <para>So there is nothing to be faithful <i>to</i> for the player. What is kept from the oracle
    /// is the <b>shape</b>: a second bar under the health bar, gated on having armour, filling with
    /// condition. What is deliberately different is the source — this reads the equipped item's
    /// integrity, which is the only armour condition the player model actually has.</para>
    ///
    /// <para><b>Colour is the condition factor, not a decoration.</b> AS3's <c>setArmor()</c> gives
    /// full ratings down to half integrity and then falls to half value at zero, so 50% is a real
    /// threshold — the bar changes colour exactly where the plate stops giving full protection, not at
    /// an arbitrary aesthetic breakpoint.</para>
    /// </summary>
    public class ArmourBarView : MonoBehaviour
    {
        [Header("UI Components")]
        [SerializeField]
        [Tooltip("Slider component for the armour fill")]
        private Slider _armourSlider;

        [SerializeField]
        [Tooltip("Optional text display for the armour value")]
        private Text _armourText;

        [Header("Data Source")]
        [SerializeField]
        [Tooltip("ViewModel to bind to")]
        private HUDViewModel _viewModel;

        [Header("Visual Settings")]
        [SerializeField]
        [Tooltip("Colour while the plate is at or above half integrity — AS3 gives full ratings here.")]
        private Color _intactColour = new Color(0.35f, 0.65f, 0.95f, 0.95f);

        [SerializeField]
        [Tooltip("Colour in the degraded band (quarter to half integrity), where AS3's rating falls off.")]
        private Color _degradedColour = new Color(0.95f, 0.75f, 0.2f, 0.95f);

        [SerializeField]
        [Tooltip("Colour below a quarter integrity — the plate is close to breaking.")]
        private Color _criticalColour = new Color(0.9f, 0.25f, 0.2f, 0.95f);

        [SerializeField]
        [Tooltip("Image component to tint")]
        private Image _fillImage;

        /// <summary>
        /// The subtree that hides when no armour is equipped.
        ///
        /// <para><b>Why a child and not <c>gameObject.SetActive(false)</c>.</b> A deactivated
        /// GameObject never runs its <c>Start</c>, and this view binds in <c>Start</c> — so hiding
        /// itself that way at build time would be a deadlock, the same trap
        /// <c>ReloadIndicatorView</c> documents. Deactivating a <i>child</i> instead keeps this
        /// component and its subscriptions alive, so equipping armour later still re-shows the bar.
        /// R3 subscriptions are plain delegates and keep firing on an inactive GameObject's behalf,
        /// but only for a component that was allowed to run in the first place.</para>
        /// </summary>
        [SerializeField]
        [Tooltip("Child object holding the bar's visuals; toggled off when nothing is equipped")]
        private GameObject _content;

        private CompositeDisposable _disposables;

        /// <summary>See <see cref="HealthBarView"/> — stops <c>Start</c> double-binding a forced bind.</summary>
        private bool _bound;

        private void Awake()
        {
            if (_armourSlider == null)
                _armourSlider = GetComponent<Slider>();

            if (_fillImage == null && _armourSlider != null)
                _fillImage = _armourSlider.fillRect?.GetComponent<Image>();

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
                Debug.LogWarning("[ArmourBarView] No ViewModel assigned - armour bar will not update");
            }
        }

        /// <summary>
        /// Assign the ViewModel <b>without</b> binding, so the view binds itself in <c>Start</c>.
        /// The runtime HUD builder uses this; see <see cref="HealthBarView.SetViewModel"/>.
        /// </summary>
        public void SetViewModel(HUDViewModel viewModel) => _viewModel = viewModel;

        /// <summary>Assign the optional numeric readout (built as a sibling by the runtime builder).</summary>
        public void SetArmourText(Text text) => _armourText = text;

        /// <summary>Assign the subtree that hides when nothing is equipped.</summary>
        public void SetContent(GameObject content) => _content = content;

        public void BindToViewModel(HUDViewModel viewModel)
        {
            _viewModel = viewModel;

            if (_viewModel == null)
            {
                Debug.LogError("[ArmourBarView] Cannot bind to null ViewModel");
                return;
            }

            // Replace, don't accumulate — see HealthBarView.
            _disposables?.Dispose();
            _disposables = new CompositeDisposable();
            _bound = true;

            int bound = 0;

            // Both guards matter: HudBootstrapper drives Initialize() from its Update, which runs
            // after every Start(), so the first bind legitimately sees null properties and
            // `null.Subscribe(...)` throws inside R3 with an NRE that names R3, not the real cause.
            if (_viewModel.ArmourPercent != null)
            {
                _viewModel.ArmourPercent.Subscribe(percent =>
                {
                    if (_armourSlider != null)
                        _armourSlider.value = percent;

                    UpdateArmourColour(percent);
                }).AddTo(_disposables);
                bound++;
            }

            if (_viewModel.HasArmour != null)
            {
                // Read the current value rather than subscribing alone: ReactiveProperty replays on
                // subscribe, so the initial show/hide is covered — but making it explicit is what
                // keeps the bar from rendering one frame of stale state if the source is replaced.
                ApplyVisibility(_viewModel.HasArmour.CurrentValue);

                _viewModel.HasArmour.Subscribe(ApplyVisibility).AddTo(_disposables);
                bound++;
            }

            if (_armourText != null && _viewModel.ArmourPercent != null)
            {
                _viewModel.ArmourPercent.Subscribe(_ => _armourText.text = GetArmourText()).AddTo(_disposables);
                bound++;
            }

            Debug.Log(bound == 0
                ? "[ArmourBarView] ViewModel has no sources yet - waiting for the bootstrapper to rebind."
                : $"[ArmourBarView] Bound to ViewModel ({bound} subscription(s)).");
        }

        private void ApplyVisibility(bool hasArmour)
        {
            if (_content != null && _content.activeSelf != hasArmour)
                _content.SetActive(hasArmour);
        }

        /// <summary>
        /// Colour by condition. The 0.5 boundary is AS3's own: <c>setArmor()</c> leaves the ratings
        /// untouched at or above half integrity, so above it the plate is genuinely at full strength.
        /// </summary>
        private void UpdateArmourColour(float integrity)
        {
            if (_fillImage == null)
                return;

            if (integrity > 0.5f)
                _fillImage.color = _intactColour;
            else if (integrity > 0.25f)
                _fillImage.color = _degradedColour;
            else
                _fillImage.color = _criticalColour;
        }

        /// <summary>Armour readout, e.g. "72%". Empty when nothing is equipped.</summary>
        public string GetArmourText()
        {
            if (_viewModel == null || !_viewModel.HasArmour.CurrentValue)
                return string.Empty;

            return $"{Mathf.RoundToInt(_viewModel.ArmourPercent.CurrentValue * 100f)}%";
        }

        private void OnDestroy()
        {
            _disposables?.Dispose();
        }
    }
}
