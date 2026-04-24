using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using R3;
using PFE.Systems.RPG.Data;

namespace PFE.UI.Menus.RPG
{
    /// <summary>
    /// View component for skill point allocation.
    /// Displays all skills and allows adding/removing skill points.
    ///
    /// This View binds to CharacterStatsViewModel and provides UI for:
    /// - Displaying all skills with their current levels
    /// - Showing skill tier progression (0-5 stars or indicators)
    /// - Adding skill points with + buttons
    /// - Displaying skill effects at each tier
    /// - Validation (max level, skill points available)
    /// </summary>
    public class SkillSelectionView : MonoBehaviour
    {
        [Header("ViewModel")]
        [SerializeField]
        [Tooltip("ViewModel to bind to")]
        private CharacterStatsViewModel viewModel;

        [Header("UI Prefabs")]
        [SerializeField]
        [Tooltip("Prefab for displaying a single skill entry")]
        private SkillEntryUI skillEntryPrefab;

        [SerializeField]
        [Tooltip("Parent transform for skill entries")]
        private Transform skillListContainer;

        [Header("Info Panel")]
        [SerializeField] private Text selectedSkillName;
        [SerializeField] private Text selectedSkillDescription;
        [SerializeField] private Text selectedSkillLevel;
        [SerializeField] private Text skillPointsAvailable;
        [SerializeField] private Button increaseSkillButton;
        [SerializeField] private Button decreaseSkillButton;

        // State
        private SkillDefinition _selectedSkill;
        private CompositeDisposable _disposables;
        private Dictionary<string, SkillEntryUI> _skillEntries = new Dictionary<string, SkillEntryUI>();

        // Skill ordering
        private readonly string[] _skillOrder = new string[]
        {
            "tele", "melee", "smallguns", "energy", "explosives", "magic",
            "repair", "medic", "lockpick", "science", "sneak", "barter", "survival",
            "attack", "defense", "knowl", "life", "spirit"
        };

        /// <summary>
        /// Initialize the view with a ViewModel.
        /// </summary>
        public void Initialize(CharacterStatsViewModel vm)
        {
            viewModel = vm;
            SetupBindings();
            PopulateSkillList();
        }

        private void Start()
        {
            if (viewModel != null)
            {
                SetupBindings();
            }

            if (increaseSkillButton != null)
                increaseSkillButton.onClick.AddListener(OnIncreaseSkillClicked);

            if (decreaseSkillButton != null)
                decreaseSkillButton.onClick.AddListener(OnDecreaseSkillClicked);
        }

        private void SetupBindings()
        {
            if (viewModel == null) return;

            _disposables = new CompositeDisposable();

            // Subscribe to skill points changes
            viewModel.SkillPoints.Subscribe(points =>
            {
                UpdateSkillPointsDisplay();
                UpdateButtonStates();
            }).AddTo(_disposables);

            // Subscribe to skill changes
            viewModel.OnSkillChanged += HandleSkillChanged;

            UpdateSkillPointsDisplay();
        }

        private void HandleSkillChanged(string skillId, int newLevel)
        {
            // Update the specific skill entry
            if (_skillEntries.TryGetValue(skillId, out var entry))
            {
                entry.UpdateLevel(newLevel);
            }

            // Update info panel if this skill is selected
            if (_selectedSkill != null && _selectedSkill.SkillId == skillId)
            {
                UpdateSelectedSkillInfo();
            }

            UpdateButtonStates();
        }

        private void PopulateSkillList()
        {
            if (viewModel == null || skillEntryPrefab == null || skillListContainer == null)
            {
                Debug.LogError("[SkillSelectionView] Missing required references for skill list");
                return;
            }

            // Clear existing entries
            foreach (Transform child in skillListContainer)
            {
                Destroy(child.gameObject);
            }
            _skillEntries.Clear();

            // Get all skills
            SkillDefinition[] allSkills = viewModel.GetAllSkills();
            if (allSkills == null || allSkills.Length == 0)
            {
                Debug.LogWarning("[SkillSelectionView] No skills found in database");
                return;
            }

            // Create skill entries in order
            foreach (string skillId in _skillOrder)
            {
                SkillDefinition skill = System.Array.Find(allSkills, s => s.SkillId == skillId);
                if (skill == null) continue;

                // Skip post-game skills if player is not post-game (level < 20)
                if (skill.IsPostGame && viewModel.Level.CurrentValue < 20)
                    continue;

                SkillEntryUI entry = Instantiate(skillEntryPrefab, skillListContainer);
                entry.Initialize(skill, viewModel);
                entry.OnSelected += OnSkillEntrySelected;

                _skillEntries[skillId] = entry;
            }

            // Select first skill by default
            if (_skillEntries.Count > 0)
            {
                string firstSkillId = _skillOrder[0];
                if (_skillEntries.TryGetValue(firstSkillId, out var firstEntry))
                {
                    firstEntry.Select();
                }
            }
        }

        private void OnSkillEntrySelected(SkillDefinition skill)
        {
            _selectedSkill = skill;
            UpdateSelectedSkillInfo();
            UpdateButtonStates();
        }

        private void UpdateSelectedSkillInfo()
        {
            if (_selectedSkill == null || viewModel == null) return;

            int currentLevel = viewModel.GetSkillLevel(_selectedSkill.SkillId);
            int tier = _selectedSkill.GetSkillTier(currentLevel);
            int maxLevel = _selectedSkill.MaxLevel;

            if (selectedSkillName != null)
                selectedSkillName.text = _selectedSkill.DisplayName;

            if (selectedSkillDescription != null)
                selectedSkillDescription.text = _selectedSkill.Description;

            if (selectedSkillLevel != null)
                selectedSkillLevel.text = $"Level: {currentLevel} / {maxLevel} (Tier {tier})";
        }

        private void UpdateSkillPointsDisplay()
        {
            if (skillPointsAvailable != null && viewModel != null)
            {
                int points = viewModel.SkillPoints.CurrentValue;
                skillPointsAvailable.text = $"Available Skill Points: {points}";
            }
        }

        private void UpdateButtonStates()
        {
            if (viewModel == null || _selectedSkill == null) return;

            int availablePoints = viewModel.SkillPoints.CurrentValue;
            int currentLevel = viewModel.GetSkillLevel(_selectedSkill.SkillId);
            int maxLevel = _selectedSkill.MaxLevel;

            // Enable increase if we have points and haven't reached max
            if (increaseSkillButton != null)
            {
                increaseSkillButton.interactable = availablePoints > 0 && currentLevel < maxLevel;
            }

            // Enable decrease if we have points in the skill (above 0)
            if (decreaseSkillButton != null)
            {
                decreaseSkillButton.interactable = currentLevel > 0;
            }
        }

        private void OnIncreaseSkillClicked()
        {
            if (viewModel == null || _selectedSkill == null) return;

            bool success = viewModel.AddSkillPoint(_selectedSkill.SkillId, 1);

            if (!success)
            {
                Debug.LogWarning($"[SkillSelectionView] Failed to add point to {_selectedSkill.SkillId}");
            }
        }

        private void OnDecreaseSkillClicked()
        {
            // Note: The current CharacterStats implementation doesn't support
            // removing skill points. This would need to be added to the model.
            Debug.LogWarning("[SkillSelectionView] Skill point removal not implemented yet");
        }

        private void OnDestroy()
        {
            if (viewModel != null)
            {
                viewModel.OnSkillChanged -= HandleSkillChanged;
            }

            _disposables?.Dispose();

            if (increaseSkillButton != null)
                increaseSkillButton.onClick.RemoveListener(OnIncreaseSkillClicked);

            if (decreaseSkillButton != null)
                decreaseSkillButton.onClick.RemoveListener(OnDecreaseSkillClicked);
        }
    }

    /// <summary>
    /// Individual skill entry UI element.
    /// Displays a single skill in the list.
    /// </summary>
    [System.Serializable]
    public class SkillEntryUI : MonoBehaviour
    {
        [Header("UI Components")]
        public Text skillNameText;
        public Text skillLevelText;
        public Text skillTierText;
        public Button selectButton;

        private SkillDefinition _skill;
        private CharacterStatsViewModel _viewModel;

        public event System.Action<SkillDefinition> OnSelected;

        public void Initialize(SkillDefinition skill, CharacterStatsViewModel viewModel)
        {
            _skill = skill;
            _viewModel = viewModel;

            if (skillNameText != null)
                skillNameText.text = skill.DisplayName;

            // Get current level
            int currentLevel = viewModel.GetSkillLevel(skill.SkillId);
            UpdateLevel(currentLevel);

            if (selectButton != null)
            {
                string skillId = skill.SkillId; // Capture for closure
                selectButton.onClick.AddListener(() => OnSelected?.Invoke(_skill));
            }
        }

        public void UpdateLevel(int newLevel)
        {
            if (skillLevelText != null)
                skillLevelText.text = $"Lv. {newLevel}";

            if (skillTierText != null && _skill != null)
            {
                int tier = _skill.GetSkillTier(newLevel);
                skillTierText.text = GetTierDisplay(tier);
            }
        }

        private string GetTierDisplay(int tier)
        {
            // Display tier as stars or text
            switch (tier)
            {
                case 0: return "";
                case 1: return "*";
                case 2: return "**";
                case 3: return "***";
                case 4: return "****";
                case 5: return "*****";
                default: return "";
            }
        }

        public void Select()
        {
            OnSelected?.Invoke(_skill);
        }
    }
}
