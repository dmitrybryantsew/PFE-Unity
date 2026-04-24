using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using PFE.Systems.RPG;

namespace PFE.UI.Menus.RPG
{
    /// <summary>
    /// View component for displaying stat modification factors.
    /// Shows what sources (skills, perks, equipment) contribute to a stat.
    ///
    /// Example display for Damage Multiplier:
    /// - Base: 100%
    /// - Melee Skill: +15% (tier 3)
    /// - Oak Perk: +25% (rank 1)
    /// - Total: 140%
    /// </summary>
    public class StatFactorDisplayView : MonoBehaviour
    {
        [Header("ViewModel")]
        [SerializeField]
        [Tooltip("ViewModel to get factor data from")]
        private CharacterStatsViewModel viewModel;

        [Header("UI Components")]
        [SerializeField] private Text statNameText;
        [SerializeField] private Text statValueText;
        [SerializeField] private Transform factorListContainer;
        [SerializeField] private GameObject factorEntryPrefab;

        // Stat selector buttons
        [SerializeField] private Button showDamageMultButton;
        [SerializeField] private Button showResistanceButton;
        [SerializeField] private Button showCritChanceButton;
        [SerializeField] private Button showMaxHpButton;
        [SerializeField] private Button showMaxManaButton;

        private string _currentStatId = "allDamMult";

        /// <summary>
        /// Initialize the view with a ViewModel.
        /// </summary>
        public void Initialize(CharacterStatsViewModel vm)
        {
            viewModel = vm;
            SetupButtons();
        }

        private void Start()
        {
            if (viewModel != null)
            {
                ShowStatFactors("allDamMult");
            }

            SetupButtons();
        }

        private void SetupButtons()
        {
            if (showDamageMultButton != null)
                showDamageMultButton.onClick.AddListener(() => ShowStatFactors("allDamMult"));

            if (showResistanceButton != null)
                showResistanceButton.onClick.AddListener(() => ShowStatFactors("allVulnerMult"));

            if (showCritChanceButton != null)
                showCritChanceButton.onClick.AddListener(() => ShowStatFactors("critCh"));

            if (showMaxHpButton != null)
                showMaxHpButton.onClick.AddListener(() => ShowStatFactors("maxhp"));

            if (showMaxManaButton != null)
                showMaxManaButton.onClick.AddListener(() => ShowStatFactors("maxmana"));
        }

        /// <summary>
        /// Display factors for a specific stat.
        /// </summary>
        public void ShowStatFactors(string statId)
        {
            _currentStatId = statId;

            if (viewModel == null)
            {
                Debug.LogError("[StatFactorDisplayView] ViewModel is null!");
                return;
            }

            // Get factors
            List<CharacterStats.StatFactor> factors = viewModel.GetStatFactors(statId);

            // Update stat name
            if (statNameText != null)
            {
                statNameText.text = GetStatDisplayName(statId);
            }

            // Update stat value
            if (statValueText != null)
            {
                statValueText.text = GetStatDisplayValue(statId);
            }

            // Clear and populate factor list
            if (factorListContainer != null)
            {
                foreach (Transform child in factorListContainer)
                {
                    Destroy(child.gameObject);
                }

                // Add base factor
                CreateFactorEntry("Base", GetBaseValue(statId), GetBaseValue(statId));

                // Add all contributing factors
                float runningTotal = GetBaseValue(statId);
                foreach (var factor in factors)
                {
                    runningTotal = factor.result;
                    CreateFactorEntry(factor.sourceId, factor.value, factor.result);
                }
            }
        }

        private void CreateFactorEntry(string sourceId, float value, float result)
        {
            if (factorEntryPrefab == null || factorListContainer == null) return;

            GameObject entry = Instantiate(factorEntryPrefab, factorListContainer);

            // Try to find Text components
            Text[] texts = entry.GetComponentsInChildren<Text>();
            if (texts.Length >= 2)
            {
                texts[0].text = GetSourceDisplayName(sourceId);
                texts[1].text = GetFactorValueText(value, result);
            }
        }

        private string GetStatDisplayName(string statId)
        {
            switch (statId)
            {
                case "allDamMult": return "Damage Multiplier";
                case "allVulnerMult": return "Damage Vulnerability";
                case "critCh": return "Critical Hit Chance";
                case "critDamMult": return "Critical Damage Multiplier";
                case "dexter": return "Dodge Chance";
                case "skin": return "Damage Resistance";
                case "maxhp": return "Maximum Health";
                case "maxmana": return "Maximum Mana";
                default: return statId;
            }
        }

        private string GetStatDisplayValue(string statId)
        {
            if (viewModel == null) return "--";

            switch (statId)
            {
                case "allDamMult":
                    return $"{(viewModel.AllDamMult.CurrentValue * 100f):F0}%";

                case "allVulnerMult":
                    float resist = (1f - viewModel.AllVulnerMult.CurrentValue) * 100f;
                    return $"{resist:F0}% Resistance";

                case "critCh":
                    return $"{(viewModel.CritCh.CurrentValue * 100f):F1}%";

                case "critDamMult":
                    return $"{viewModel.CritDamMult.CurrentValue:F1}x";

                case "dexter":
                    return $"{(viewModel.Dexter.CurrentValue * 100f):F0}%";

                case "skin":
                    return $"{viewModel.Skin.CurrentValue:F0}";

                case "maxhp":
                    return $"{viewModel.MaxHp.CurrentValue:F0}";

                case "maxmana":
                    return $"{viewModel.MaxMana.CurrentValue:F0}";

                default:
                    return "--";
            }
        }

        private float GetBaseValue(string statId)
        {
            switch (statId)
            {
                case "allDamMult": return 1.0f;
                case "allVulnerMult": return 1.0f;
                case "critCh": return 0.05f;
                case "critDamMult": return 2.0f;
                case "dexter": return 0.0f;
                case "skin": return 0.0f;
                case "maxhp": return 100f;
                case "maxmana": return 400f;
                default: return 0f;
            }
        }

        private string GetSourceDisplayName(string sourceId)
        {
            // Capitalize first letter
            if (string.IsNullOrEmpty(sourceId)) return "Unknown";

            return char.ToUpper(sourceId[0]) + (sourceId.Length > 1 ? sourceId.Substring(1) : "");
        }

        private string GetFactorValueText(float value, float result)
        {
            // Format based on value type
            if (value > 0)
                return $"+{value:F2}";
            else if (value < 0)
                return $"{value:F2}";
            else
                return "0.00";
        }

        /// <summary>
        /// Refresh the current stat display.
        /// </summary>
        public void Refresh()
        {
            ShowStatFactors(_currentStatId);
        }
    }
}
