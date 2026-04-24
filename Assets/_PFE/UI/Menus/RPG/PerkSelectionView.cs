using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using R3;
using PFE.Systems.RPG.Data;

namespace PFE.UI.Menus.RPG
{
    /// <summary>
    /// View component for perk selection.
    /// Displays available perks and allows selecting perks.
    ///
    /// This View binds to CharacterStatsViewModel and provides UI for:
    /// - Displaying all perks with requirements
    /// - Filtering by requirements met/not met
    /// - Selecting perks
    /// - Showing perk effects and ranks
    /// </summary>
    public class PerkSelectionView : MonoBehaviour
    {
        [Header("ViewModel")]
        [SerializeField]
        [Tooltip("ViewModel to bind to")]
        private CharacterStatsViewModel viewModel;

        [Header("UI Prefabs")]
        [SerializeField]
        [Tooltip("Prefab for displaying a single perk entry")]
        private PerkEntryUI perkEntryPrefab;

        [SerializeField]
        [Tooltip("Parent transform for perk entries")]
        private Transform perkListContainer;

        [Header("Filters")]
        [SerializeField] private Toggle showAllToggle;
        [SerializeField] private Toggle showAvailableToggle;
        [SerializeField] private Toggle showLockedToggle;

        [Header("Info Panel")]
        [SerializeField] private Text selectedPerkName;
        [SerializeField] private Text selectedPerkDescription;
        [SerializeField] private Text selectedPerkRank;
        [SerializeField] private Text selectedPerkRequirements;
        [SerializeField] private Text perkPointsAvailable;
        [SerializeField] private Button selectPerkButton;

        // State
        private PerkDefinition _selectedPerk;
        private CompositeDisposable _disposables;
        private Dictionary<string, PerkEntryUI> _perkEntries = new Dictionary<string, PerkEntryUI>();
        private PerkFilterMode _filterMode = PerkFilterMode.Available;

        private enum PerkFilterMode
        {
            All,
            Available,
            Locked
        }

        /// <summary>
        /// Initialize the view with a ViewModel.
        /// </summary>
        public void Initialize(CharacterStatsViewModel vm)
        {
            viewModel = vm;
            SetupBindings();
            PopulatePerkList();
        }

        private void Start()
        {
            if (viewModel != null)
            {
                SetupBindings();
            }

            if (selectPerkButton != null)
                selectPerkButton.onClick.AddListener(OnSelectPerkClicked);

            if (showAllToggle != null)
                showAllToggle.onValueChanged.AddListener(_ => OnFilterChanged(PerkFilterMode.All));

            if (showAvailableToggle != null)
                showAvailableToggle.onValueChanged.AddListener(_ => OnFilterChanged(PerkFilterMode.Available));

            if (showLockedToggle != null)
                showLockedToggle.onValueChanged.AddListener(_ => OnFilterChanged(PerkFilterMode.Locked));
        }

        private void SetupBindings()
        {
            if (viewModel == null) return;

            _disposables = new CompositeDisposable();

            // Subscribe to perk points changes
            viewModel.PerkPoints.Subscribe(points =>
            {
                UpdatePerkPointsDisplay();
                UpdateButtonStates();
            }).AddTo(_disposables);

            // Subscribe to perk changes
            viewModel.OnPerkAdded += HandlePerkAdded;

            UpdatePerkPointsDisplay();
        }

        private void HandlePerkAdded(string perkId, int newRank)
        {
            // Update the specific perk entry
            if (_perkEntries.TryGetValue(perkId, out var entry))
            {
                entry.UpdateRank(newRank);
            }

            // Update info panel if this perk is selected
            if (_selectedPerk != null && _selectedPerk.PerkId == perkId)
            {
                UpdateSelectedPerkInfo();
            }

            UpdateButtonStates();
        }

        private void PopulatePerkList()
        {
            if (viewModel == null || perkEntryPrefab == null || perkListContainer == null)
            {
                Debug.LogError("[PerkSelectionView] Missing required references for perk list");
                return;
            }

            // Clear existing entries
            foreach (Transform child in perkListContainer)
            {
                Destroy(child.gameObject);
            }
            _perkEntries.Clear();

            // Get all perks
            PerkDefinition[] allPerks = viewModel.GetAllPerks();
            if (allPerks == null || allPerks.Length == 0)
            {
                Debug.LogWarning("[PerkSelectionView] No perks found in database");
                return;
            }

            // Sort by display name
            allPerks = allPerks
                .Where(p => p != null && p.IsPlayerSelectable)
                .OrderBy(p => p.DisplayName)
                .ToArray();

            // Create perk entries
            foreach (PerkDefinition perk in allPerks)
            {
                bool canUnlock = viewModel.CanUnlockPerk(perk.PerkId);
                int currentRank = viewModel.GetPerkRank(perk.PerkId);

                PerkEntryUI entry = Instantiate(perkEntryPrefab, perkListContainer);
                entry.Initialize(perk, viewModel, canUnlock, currentRank);
                entry.OnSelected += OnPerkEntrySelected;

                _perkEntries[perk.PerkId] = entry;
            }

            // Apply current filter
            ApplyFilter();

            // Select first perk by default
            if (_perkEntries.Count > 0)
            {
                var firstEntry = _perkEntries.Values.FirstOrDefault();
                firstEntry?.Select();
            }
        }

        private void OnPerkEntrySelected(PerkDefinition perk)
        {
            _selectedPerk = perk;
            UpdateSelectedPerkInfo();
            UpdateButtonStates();
        }

        private void UpdateSelectedPerkInfo()
        {
            if (_selectedPerk == null || viewModel == null) return;

            int currentRank = viewModel.GetPerkRank(_selectedPerk.PerkId);
            int maxRank = _selectedPerk.MaxRank;

            if (selectedPerkName != null)
                selectedPerkName.text = _selectedPerk.DisplayName;

            if (selectedPerkDescription != null)
                selectedPerkDescription.text = _selectedPerk.Description;

            if (selectedPerkRank != null)
                selectedPerkRank.text = $"Rank: {currentRank} / {maxRank}";

            if (selectedPerkRequirements != null)
            {
                selectedPerkRequirements.text = GetRequirementsText(_selectedPerk);
            }
        }

        private string GetRequirementsText(PerkDefinition perk)
        {
            var reqsList = perk.Requirements;
            if (reqsList == null || reqsList.Length == 0)
                return "No requirements";

            var reqs = new System.Text.StringBuilder();
            int currentRank = viewModel.GetPerkRank(perk.PerkId);
            int nextRank = currentRank + 1;

            foreach (var req in reqsList)
            {
                int requiredLevel = req.level;
                if (req.levelDelta > 0 && currentRank > 0)
                {
                    requiredLevel += currentRank * req.levelDelta;
                }

                bool met = false;
                string reqText = "";

                switch (req.type)
                {
                    case RequirementType.Level:
                        met = viewModel.Level.CurrentValue >= requiredLevel;
                        reqText = $"Level {requiredLevel}";
                        break;
                    case RequirementType.Skill:
                        met = viewModel.GetSkillLevel(req.skillId) >= requiredLevel;
                        SkillDefinition skill = viewModel.GetSkillDefinition(req.skillId);
                        string skillName = skill?.DisplayName ?? req.skillId;
                        reqText = $"{skillName} {requiredLevel}";
                        break;
                    case RequirementType.Guns:
                        int smallGuns = viewModel.GetSkillLevel("smallguns");
                        int energy = viewModel.GetSkillLevel("energy");
                        met = smallGuns >= requiredLevel || energy >= requiredLevel;
                        reqText = $"Guns {requiredLevel}";
                        break;
                }

                reqs.AppendLine(met ? $"✓ {reqText}" : $"✗ {reqText}");
            }

            return reqs.ToString();
        }

        private void UpdatePerkPointsDisplay()
        {
            if (perkPointsAvailable != null && viewModel != null)
            {
                int points = viewModel.PerkPoints.CurrentValue;
                perkPointsAvailable.text = $"Available Perk Points: {points}";
            }
        }

        private void UpdateButtonStates()
        {
            if (viewModel == null || _selectedPerk == null) return;

            int availablePoints = viewModel.PerkPoints.CurrentValue;
            int currentRank = viewModel.GetPerkRank(_selectedPerk.PerkId);
            int maxRank = _selectedPerk.MaxRank;
            bool canUnlock = viewModel.CanUnlockPerk(_selectedPerk.PerkId);

            // Enable select if we have points, haven't reached max, and requirements are met
            if (selectPerkButton != null)
            {
                selectPerkButton.interactable = availablePoints > 0 && currentRank < maxRank && canUnlock;
            }
        }

        private void OnSelectPerkClicked()
        {
            if (viewModel == null || _selectedPerk == null) return;

            bool success = viewModel.AddPerk(_selectedPerk.PerkId);

            if (!success)
            {
                Debug.LogWarning($"[PerkSelectionView] Failed to add perk {_selectedPerk.PerkId}");
            }
        }

        private void OnFilterChanged(PerkFilterMode mode)
        {
            _filterMode = mode;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            foreach (var entry in _perkEntries.Values)
            {
                bool show = false;

                switch (_filterMode)
                {
                    case PerkFilterMode.All:
                        show = true;
                        break;
                    case PerkFilterMode.Available:
                        show = entry.IsAvailable;
                        break;
                    case PerkFilterMode.Locked:
                        show = !entry.IsAvailable;
                        break;
                }

                entry.gameObject.SetActive(show);
            }
        }

        private void OnDestroy()
        {
            if (viewModel != null)
            {
                viewModel.OnPerkAdded -= HandlePerkAdded;
            }

            _disposables?.Dispose();

            if (selectPerkButton != null)
                selectPerkButton.onClick.RemoveListener(OnSelectPerkClicked);

            if (showAllToggle != null)
                showAllToggle.onValueChanged.RemoveListener(_ => OnFilterChanged(PerkFilterMode.All));

            if (showAvailableToggle != null)
                showAvailableToggle.onValueChanged.RemoveListener(_ => OnFilterChanged(PerkFilterMode.Available));

            if (showLockedToggle != null)
                showLockedToggle.onValueChanged.RemoveListener(_ => OnFilterChanged(PerkFilterMode.Locked));
        }
    }

    /// <summary>
    /// Individual perk entry UI element.
    /// Displays a single perk in the list.
    /// </summary>
    [System.Serializable]
    public class PerkEntryUI : MonoBehaviour
    {
        [Header("UI Components")]
        public Text perkNameText;
        public Text perkRankText;
        public Text requirementsMetText;
        public Button selectButton;
        public Image background;

        [Header("Colors")]
        public Color availableColor = new Color(0.2f, 0.8f, 0.2f);
        public Color lockedColor = new Color(0.8f, 0.2f, 0.2f);

        private PerkDefinition _perk;
        private CharacterStatsViewModel _viewModel;
        private bool _isAvailable;

        public bool IsAvailable => _isAvailable;

        public event System.Action<PerkDefinition> OnSelected;

        public void Initialize(PerkDefinition perk, CharacterStatsViewModel viewModel, bool canUnlock, int currentRank)
        {
            _perk = perk;
            _viewModel = viewModel;
            _isAvailable = canUnlock;

            if (perkNameText != null)
                perkNameText.text = perk.DisplayName;

            UpdateRank(currentRank);
            UpdateAvailability(canUnlock);

            if (selectButton != null)
            {
                selectButton.onClick.AddListener(() => OnSelected?.Invoke(_perk));
            }
        }

        public void UpdateRank(int newRank)
        {
            if (perkRankText != null && _perk != null)
            {
                perkRankText.text = $"Rank {newRank} / {_perk.MaxRank}";
            }
        }

        public void UpdateAvailability(bool canUnlock)
        {
            _isAvailable = canUnlock;

            if (requirementsMetText != null)
            {
                requirementsMetText.text = canUnlock ? "Available" : "Locked";
                requirementsMetText.color = canUnlock ? availableColor : lockedColor;
            }

            if (background != null)
            {
                background.color = canUnlock ? new Color(0.2f, 0.2f, 0.2f, 0.8f) : new Color(0.1f, 0.1f, 0.1f, 0.5f);
            }
        }

        public void Select()
        {
            OnSelected?.Invoke(_perk);
        }
    }
}
