using UnityEngine;
using UnityEngine.UI;
using R3;
using PFE.Systems.RPG;

namespace PFE.UI.Menus.RPG
{
    /// <summary>
    /// View component for displaying character stats.
    /// Binds to CharacterStatsViewModel and updates UI reactively.
    ///
    /// This is the View in the MVVM pattern:
    /// - Model: CharacterStats
    /// - ViewModel: CharacterStatsViewModel (transforms data for UI)
    /// - View: This class (displays data using Unity UI components)
    ///
    /// Setup:
    /// 1. Attach this script to a GameObject with UI Text/Image components
    /// 2. Assign UI references in the Inspector
    /// 3. Call Initialize() with a CharacterStatsViewModel
    /// </summary>
    public class CharacterStatsView : MonoBehaviour
    {
        [Header("ViewModel")]
        [SerializeField]
        [Tooltip("ViewModel to bind to (can be assigned via code)")]
        private CharacterStatsViewModel viewModel;

        [Header("Base Stats UI")]
        [SerializeField] private Text levelText;
        [SerializeField] private Text xpText;
        [SerializeField] private Slider xpSlider;
        [SerializeField] private Text skillPointsText;
        [SerializeField] private Text perkPointsText;

        [Header("Health UI")]
        [SerializeField] private Text healthText;
        [SerializeField] private Slider healthSlider;
        [SerializeField] private Image healthFill;
        [SerializeField] private Text manaText;
        [SerializeField] private Slider manaSlider;
        [SerializeField] private Image manaFill;

        [Header("Derived Stats UI")]
        [SerializeField] private Text damageMultText;
        [SerializeField] private Text resistanceText;
        [SerializeField] private Text critChanceText;
        [SerializeField] private Text dodgeChanceText;

        [Header("Colors")]
        [SerializeField] private Color healthColorHigh = new Color(0.2f, 0.8f, 0.2f);
        [SerializeField] private Color healthColorMedium = new Color(1f, 0.8f, 0f);
        [SerializeField] private Color healthColorLow = new Color(1f, 0.2f, 0.2f);
        [SerializeField] private Color manaColor = new Color(0.2f, 0.6f, 1f);

        // Composite disposable for cleanup
        private CompositeDisposable _disposables;

        /// <summary>
        /// Initialize the view with a ViewModel.
        /// Call this when setting up the UI.
        /// </summary>
        public void Initialize(CharacterStatsViewModel vm)
        {
            viewModel = vm;
            SetupBindings();
        }

        private void Start()
        {
            if (viewModel != null)
            {
                SetupBindings();
            }
        }

        /// <summary>
        /// Set up reactive bindings to ViewModel.
        /// UI will automatically update when data changes.
        /// </summary>
        private void SetupBindings()
        {
            if (viewModel == null)
            {
                Debug.LogError("[CharacterStatsView] Cannot setup bindings: ViewModel is null!");
                return;
            }

            _disposables = new CompositeDisposable();

            // Bind base stats
            viewModel.Level.Subscribe(level =>
            {
                if (levelText != null)
                    levelText.text = $"Level {level}";
            }).AddTo(_disposables);

            viewModel.XpProgress.Subscribe(progress =>
            {
                if (xpSlider != null)
                    xpSlider.value = progress;
            }).AddTo(_disposables);

            viewModel.SkillPoints.Subscribe(points =>
            {
                if (skillPointsText != null)
                    skillPointsText.text = $"Skill Points: {points}";
            }).AddTo(_disposables);

            viewModel.PerkPoints.Subscribe(points =>
            {
                if (perkPointsText != null)
                    perkPointsText.text = $"Perk Points: {points}";
            }).AddTo(_disposables);

            // Bind health
            viewModel.MaxHp.Subscribe(maxHp =>
            {
                UpdateHealthDisplay();
            }).AddTo(_disposables);

            viewModel.CurrentHp.Subscribe(hp =>
            {
                UpdateHealthDisplay();
            }).AddTo(_disposables);

            // Bind mana
            viewModel.MaxMana.Subscribe(maxMana =>
            {
                UpdateManaDisplay();
            }).AddTo(_disposables);

            viewModel.CurrentMana.Subscribe(mana =>
            {
                UpdateManaDisplay();
            }).AddTo(_disposables);

            // Bind derived stats
            viewModel.AllDamMult.Subscribe(mult =>
            {
                if (damageMultText != null)
                    damageMultText.text = GetDamageMultiplierText(mult);
            }).AddTo(_disposables);

            viewModel.AllVulnerMult.Subscribe(mult =>
            {
                if (resistanceText != null)
                    resistanceText.text = GetDamageResistanceText(mult);
            }).AddTo(_disposables);

            viewModel.CritCh.Subscribe(crit =>
            {
                if (critChanceText != null)
                    critChanceText.text = GetCritChanceText(crit);
            }).AddTo(_disposables);

            viewModel.Dexter.Subscribe(dexter =>
            {
                if (dodgeChanceText != null)
                    dodgeChanceText.text = GetDodgeChanceText(dexter);
            }).AddTo(_disposables);

            // Subscribe to events
            viewModel.OnLevelUp += HandleLevelUp;

            Debug.Log("[CharacterStatsView] Reactive bindings established");
        }

        private void UpdateHealthDisplay()
        {
            if (viewModel == null) return;

            float current = viewModel.CurrentHp.CurrentValue;
            float max = viewModel.MaxHp.CurrentValue;
            float percent = max > 0 ? current / max : 0f;

            if (healthText != null)
                healthText.text = $"{Mathf.Round(current)} / {Mathf.Round(max)}";

            if (healthSlider != null)
            {
                healthSlider.maxValue = max;
                healthSlider.value = current;
            }

            if (healthFill != null)
            {
                healthFill.color = percent > 0.6f ? healthColorHigh :
                                   percent > 0.3f ? healthColorMedium :
                                   healthColorLow;
            }
        }

        private void UpdateManaDisplay()
        {
            if (viewModel == null) return;

            float current = viewModel.CurrentMana.CurrentValue;
            float max = viewModel.MaxMana.CurrentValue;
            float percent = max > 0 ? current / max : 0f;

            if (manaText != null)
                manaText.text = $"{Mathf.Round(current)} / {Mathf.Round(max)}";

            if (manaSlider != null)
            {
                manaSlider.maxValue = max;
                manaSlider.value = current;
            }

            if (manaFill != null)
            {
                manaFill.color = manaColor;
            }
        }

        // ========== Event Handlers ==========

        private void HandleLevelUp(int newLevel)
        {
            // Could trigger level-up animation or notification here
            Debug.Log($"[CharacterStatsView] Level up event received: {newLevel}");
        }

        // ========== Formatted Display Methods ==========

        private string GetDamageMultiplierText(float mult)
        {
            float bonus = (mult - 1f) * 100f;
            return bonus >= 0 ? $"+{Mathf.RoundToInt(bonus)}% Damage" : $"{Mathf.RoundToInt(bonus)}% Damage";
        }

        private string GetDamageResistanceText(float vulnerMult)
        {
            float resist = (1f - vulnerMult) * 100f;
            return $"{Mathf.RoundToInt(resist)}% Resistance";
        }

        private string GetCritChanceText(float crit)
        {
            return $"{Mathf.RoundToInt(crit * 100f)}% Crit";
        }

        private string GetDodgeChanceText(float dexter)
        {
            return $"{Mathf.RoundToInt(dexter * 100f)}% Dodge";
        }

        // ========== Manual Refresh ==========

        /// <summary>
        /// Manually refresh all UI elements.
        /// Useful when first setting up the view.
        /// </summary>
        public void RefreshUI()
        {
            if (viewModel == null) return;

            // Refresh all displays
            UpdateHealthDisplay();
            UpdateManaDisplay();

            if (levelText != null)
                levelText.text = $"Level {viewModel.Level.CurrentValue}";

            if (xpText != null)
                xpText.text = viewModel.GetXpText();

            if (xpSlider != null)
                xpSlider.value = viewModel.XpProgress.CurrentValue;

            if (skillPointsText != null)
                skillPointsText.text = $"Skill Points: {viewModel.SkillPoints.CurrentValue}";

            if (perkPointsText != null)
                perkPointsText.text = $"Perk Points: {viewModel.PerkPoints.CurrentValue}";

            if (damageMultText != null)
                damageMultText.text = GetDamageMultiplierText(viewModel.AllDamMult.CurrentValue);

            if (resistanceText != null)
                resistanceText.text = GetDamageResistanceText(viewModel.AllVulnerMult.CurrentValue);

            if (critChanceText != null)
                critChanceText.text = GetCritChanceText(viewModel.CritCh.CurrentValue);

            if (dodgeChanceText != null)
                dodgeChanceText.text = GetDodgeChanceText(viewModel.Dexter.CurrentValue);
        }

        private void OnDestroy()
        {
            if (viewModel != null)
            {
                viewModel.OnLevelUp -= HandleLevelUp;
            }

            _disposables?.Dispose();
        }

        /// <summary>
        /// Set a new ViewModel and re-bind.
        /// </summary>
        public void SetViewModel(CharacterStatsViewModel vm)
        {
            // Clean up old bindings
            if (viewModel != null)
            {
                viewModel.OnLevelUp -= HandleLevelUp;
            }

            _disposables?.Dispose();

            // Set new ViewModel
            viewModel = vm;
            SetupBindings();
        }
    }
}
