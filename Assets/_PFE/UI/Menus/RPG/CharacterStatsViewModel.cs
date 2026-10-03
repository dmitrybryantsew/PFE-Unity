using System;
using System.Collections.Generic;
using UnityEngine;
using R3;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;

namespace PFE.UI.Menus.RPG
{
    /// <summary>
    /// ViewModel for Character Stats UI.
    /// Bridges the gap between CharacterStats (game logic) and UI components.
    /// Exposes reactive properties that UI views can bind to using R3.
    ///
    /// Design Pattern: Model-View-ViewModel (MVVM)
    /// - Model: CharacterStats, SkillDefinition, PerkDefinition (game data and logic)
    /// - ViewModel: This class (transforms data for UI consumption)
    /// - View: CharacterStatsView, SkillSelectionView, PerkSelectionView (display data)
    ///
    /// Benefits:
    /// - UI doesn't need to know about game logic directly
    /// - Reactive properties update UI automatically when data changes
    /// - Testable (can mock ViewModel without UI)
    /// - Separation of concerns (UI vs game logic)
    /// </summary>
    public class CharacterStatsViewModel : MonoBehaviour
    {
        [Header("Game Data Sources")]
        [SerializeField]
        [Tooltip("Character stats to display")]
        private CharacterStats characterStats;

        // Reactive properties for base stats
        private ReadOnlyReactiveProperty<int> _level;
        private ReadOnlyReactiveProperty<int> _xp;
        private ReadOnlyReactiveProperty<int> _xpForNextLevel;
        private ReadOnlyReactiveProperty<float> _xpProgress;
        private ReadOnlyReactiveProperty<int> _skillPoints;
        private ReadOnlyReactiveProperty<int> _perkPoints;
        private ReadOnlyReactiveProperty<int> _totalPerkPoints;

        // Reactive properties for derived stats
        private ReadOnlyReactiveProperty<float> _maxHp;
        private ReadOnlyReactiveProperty<float> _maxMana;
        private ReadOnlyReactiveProperty<float> _organMaxHp;
        private ReadOnlyReactiveProperty<float> _allDamMult;
        private ReadOnlyReactiveProperty<float> _allVulnerMult;
        private ReadOnlyReactiveProperty<float> _critCh;
        private ReadOnlyReactiveProperty<float> _critDamMult;
        private ReadOnlyReactiveProperty<float> _dexter;
        private ReadOnlyReactiveProperty<float> _skin;
        private ReadOnlyReactiveProperty<float> _meleeDamMult;

        // Reactive properties for current health
        private ReadOnlyReactiveProperty<float> _currentHp;
        private ReadOnlyReactiveProperty<float> _currentMana;

        // Collections for skills and perks (updated via events)
        private Dictionary<string, int> _skillLevels;
        private Dictionary<string, int> _perkRanks;

        // Composite disposable for cleanup
        private CompositeDisposable _disposables;

        // Events
        public event Action<int> OnLevelUp;
        public event Action<string, int> OnSkillChanged;
        public event Action<string, int> OnPerkAdded;

        // ========== Public Properties for UI Binding ==========

        /// <summary>Current character level</summary>
        public ReadOnlyReactiveProperty<int> Level => _level;

        /// <summary>Current XP</summary>
        public ReadOnlyReactiveProperty<int> Xp => _xp;

        /// <summary>XP required for next level</summary>
        public ReadOnlyReactiveProperty<int> XpForNextLevel => _xpForNextLevel;

        /// <summary>XP progress 0-1</summary>
        public ReadOnlyReactiveProperty<float> XpProgress => _xpProgress;

        /// <summary>Available skill points</summary>
        public ReadOnlyReactiveProperty<int> SkillPoints => _skillPoints;

        /// <summary>Available perk points</summary>
        public ReadOnlyReactiveProperty<int> PerkPoints => _perkPoints;

        /// <summary>Total perk points (base + extra from knowl)</summary>
        public ReadOnlyReactiveProperty<int> TotalPerkPoints => _totalPerkPoints;

        /// <summary>Maximum HP</summary>
        public ReadOnlyReactiveProperty<float> MaxHp => _maxHp;

        /// <summary>Maximum Mana</summary>
        public ReadOnlyReactiveProperty<float> MaxMana => _maxMana;

        /// <summary>Organ max HP (head/torso/legs/blood)</summary>
        public ReadOnlyReactiveProperty<float> OrganMaxHp => _organMaxHp;

        /// <summary>Damage multiplier</summary>
        public ReadOnlyReactiveProperty<float> AllDamMult => _allDamMult;

        /// <summary>Vulnerability multiplier</summary>
        public ReadOnlyReactiveProperty<float> AllVulnerMult => _allVulnerMult;

        /// <summary>Critical hit chance</summary>
        public ReadOnlyReactiveProperty<float> CritCh => _critCh;

        /// <summary>Critical damage multiplier</summary>
        public ReadOnlyReactiveProperty<float> CritDamMult => _critDamMult;

        /// <summary>Dodge chance</summary>
        public ReadOnlyReactiveProperty<float> Dexter => _dexter;

        /// <summary>Damage resistance</summary>
        public ReadOnlyReactiveProperty<float> Skin => _skin;

        /// <summary>Melee damage multiplier</summary>
        public ReadOnlyReactiveProperty<float> MeleeDamMult => _meleeDamMult;

        /// <summary>Current HP</summary>
        public ReadOnlyReactiveProperty<float> CurrentHp => _currentHp;

        /// <summary>Current Mana</summary>
        public ReadOnlyReactiveProperty<float> CurrentMana => _currentMana;

        /// <summary>Skill levels dictionary (read-only, updated via OnSkillChanged event)</summary>
        public IReadOnlyDictionary<string, int> SkillLevels => _skillLevels;

        /// <summary>Perk ranks dictionary (read-only, updated via OnPerkAdded event)</summary>
        public IReadOnlyDictionary<string, int> PerkRanks => _perkRanks;

        /// <summary>Access to underlying CharacterStats for advanced operations</summary>
        public CharacterStats Stats => characterStats;

        private void Awake()
        {
            _disposables = new CompositeDisposable();
            _skillLevels = new Dictionary<string, int>();
            _perkRanks = new Dictionary<string, int>();
        }

        private void Start()
        {
            if (characterStats == null)
            {
                var player = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
                if (player != null && player.CharacterStats != null)
                {
                    Initialize(player.CharacterStats);
                    return;
                }
            }

            if (characterStats != null)
            {
                SetupBindings();
            }
        }

        /// <summary>
        /// Initialize the ViewModel with character stats.
        /// Call this when setting up the UI.
        /// </summary>
        public void Initialize(CharacterStats stats)
        {
            characterStats = stats;
            SetupBindings();
        }

        /// <summary>
        /// Set up reactive property bindings from CharacterStats.
        /// This is where we transform raw game data into UI-friendly reactive properties.
        /// </summary>
        private void SetupBindings()
        {
            if (characterStats == null)
            {
                Debug.LogError("[CharacterStatsViewModel] Cannot setup bindings: characterStats is null!");
                return;
            }

            _disposables ??= new CompositeDisposable();

            // Subscribe to CharacterStats events
            characterStats.onLevelUp += HandleLevelUp;
            characterStats.onSkillChanged += HandleSkillChanged;
            characterStats.onPerkAdded += HandlePerkAdded;
            characterStats.onStatsRecalculated += HandleStatsRecalculated;

            // Create reactive properties that poll CharacterStats
            // Note: In a production environment, CharacterStats should use ReactiveProperty internally
            // For now, we'll use Observable.EveryUpdate to poll for changes

            _level = Observable.EveryUpdate()
                .Select(_ => characterStats.Level)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.Level);

            _xp = Observable.EveryUpdate()
                .Select(_ => characterStats.Xp)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.Xp);

            _skillPoints = Observable.EveryUpdate()
                .Select(_ => characterStats.SkillPoints)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.SkillPoints);

            _perkPoints = Observable.EveryUpdate()
                .Select(_ => characterStats.PerkPoints)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.PerkPoints);

            _totalPerkPoints = Observable.EveryUpdate()
                .Select(_ => characterStats.PerkPoints + characterStats.PerkPointsExtra)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.PerkPoints + characterStats.PerkPointsExtra);

            _maxHp = Observable.EveryUpdate()
                .Select(_ => characterStats.MaxHp)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.MaxHp);

            _maxMana = Observable.EveryUpdate()
                .Select(_ => characterStats.MaxMana)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.MaxMana);

            _organMaxHp = Observable.EveryUpdate()
                .Select(_ => characterStats.OrganMaxHp)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.OrganMaxHp);

            _allDamMult = Observable.EveryUpdate()
                .Select(_ => characterStats.AllDamMult)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.AllDamMult);

            _allVulnerMult = Observable.EveryUpdate()
                .Select(_ => characterStats.AllVulnerMult)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.AllVulnerMult);

            _critCh = Observable.EveryUpdate()
                .Select(_ => characterStats.critCh)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.critCh);

            _critDamMult = Observable.EveryUpdate()
                .Select(_ => characterStats.critDamMult)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.critDamMult);

            _dexter = Observable.EveryUpdate()
                .Select(_ => characterStats.dexter)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.dexter);

            _skin = Observable.EveryUpdate()
                .Select(_ => characterStats.skin)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.skin);

            _meleeDamMult = Observable.EveryUpdate()
                .Select(_ => characterStats.meleeDamMult)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.meleeDamMult);

            _currentHp = Observable.EveryUpdate()
                .Select(_ => characterStats.BoundUnitStats != null ? characterStats.BoundUnitStats.CurrentHp.Value : characterStats.headHp)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.BoundUnitStats != null ? characterStats.BoundUnitStats.CurrentHp.Value : characterStats.headHp);

            _currentMana = Observable.EveryUpdate()
                .Select(_ => characterStats.manaHp)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(characterStats.manaHp);

            // Calculate XP progress and next level XP
            LevelCurve levelCurve = GetLevelCurve();
            _xpForNextLevel = Observable.EveryUpdate()
                .Select(_ => levelCurve != null ? levelCurve.GetXpForLevel(characterStats.Level) : 5000 * characterStats.Level * (characterStats.Level + 1) / 2)
                .DistinctUntilChanged()
                .ToReadOnlyReactiveProperty(levelCurve != null ? levelCurve.GetXpForLevel(characterStats.Level) : 5000 * characterStats.Level * (characterStats.Level + 1) / 2);

            _xpProgress = Observable.EveryUpdate()
                .Select(_ =>
                {
                    int currentXp = characterStats.Xp;
                    int currentLevel = characterStats.Level;
                    int nextLevelXp = levelCurve?.GetXpForLevel(currentLevel) ?? 0;
                    int prevLevelXp = currentLevel > 1
                        ? levelCurve?.GetXpForLevel(currentLevel - 1) ?? 0
                        : 0;

                    if (nextLevelXp == prevLevelXp) return 0f;
                    return Mathf.Clamp01((float)(currentXp - prevLevelXp) / (nextLevelXp - prevLevelXp));
                })
                .ToReadOnlyReactiveProperty(0f);

            // Initialize skill levels
            InitializeSkillLevels();

            // Initialize perk ranks
            InitializePerkRanks();

            // Every property above is a live R3 subscription (Observable.EveryUpdate().…ToReadOnly-
            // ReactiveProperty). They were held in fields but never registered anywhere, so
            // OnDestroy disposed an empty composite and all of them leaked on scene teardown.
            _disposables.Add(_level);
            _disposables.Add(_xp);
            _disposables.Add(_skillPoints);
            _disposables.Add(_perkPoints);
            _disposables.Add(_totalPerkPoints);
            _disposables.Add(_maxHp);
            _disposables.Add(_maxMana);
            _disposables.Add(_organMaxHp);
            _disposables.Add(_allDamMult);
            _disposables.Add(_allVulnerMult);
            _disposables.Add(_critCh);
            _disposables.Add(_critDamMult);
            _disposables.Add(_dexter);
            _disposables.Add(_skin);
            _disposables.Add(_meleeDamMult);
            _disposables.Add(_currentHp);
            _disposables.Add(_currentMana);
            _disposables.Add(_xpForNextLevel);
            _disposables.Add(_xpProgress);

            Debug.Log("[CharacterStatsViewModel] Reactive bindings established");
        }

        private void InitializeSkillLevels()
        {
            if (characterStats == null) return;

            _skillLevels.Clear();

            // The oracle's 16 skills (AllData.as). `life`/`spirit` are perks, not skills.
            string[] skillIds = new string[]
            {
                "tele", "melee", "smallguns", "energy", "explosives", "magic",
                "repair", "medic", "lockpick", "science", "sneak", "barter", "survival",
                "attack", "defense", "knowl"
            };

            foreach (string skillId in skillIds)
            {
                _skillLevels[skillId] = characterStats.GetSkillLevel(skillId);
            }
        }

        private void InitializePerkRanks()
        {
            if (characterStats == null) return;

            _perkRanks.Clear();

            // Get all perk IDs from database
            SkillDefinitionDatabase db = GetSkillDatabase();
            if (db != null)
            {
                PerkDefinition[] allPerks = db.GetAllPerks();
                foreach (var perk in allPerks)
                {
                    if (perk != null && !string.IsNullOrEmpty(perk.PerkId))
                    {
                        _perkRanks[perk.PerkId] = characterStats.GetPerkRank(perk.PerkId);
                    }
                }
            }
        }

        // ========== Event Handlers ==========

        private void HandleLevelUp(int newLevel)
        {
            OnLevelUp?.Invoke(newLevel);
            Debug.Log($"[CharacterStatsViewModel] Level up! Now level {newLevel}");
        }

        private void HandleSkillChanged(string skillId, int newLevel)
        {
            if (_skillLevels.ContainsKey(skillId))
            {
                _skillLevels[skillId] = newLevel;
            }
            OnSkillChanged?.Invoke(skillId, newLevel);
        }

        private void HandlePerkAdded(string perkId, int newRank)
        {
            if (_perkRanks.ContainsKey(perkId))
            {
                _perkRanks[perkId] = newRank;
            }
            else
            {
                _perkRanks[perkId] = newRank;
            }
            OnPerkAdded?.Invoke(perkId, newRank);
        }

        private void HandleStatsRecalculated()
        {
            // Reactive properties will update automatically via polling
        }

        // ========== Public Methods for UI ==========

        /// <summary>
        /// Add skill points to a skill.
        /// Returns true if successful.
        /// </summary>
        public bool AddSkillPoint(string skillId, int points = 1)
        {
            if (characterStats == null) return false;

            bool success = characterStats.AddSkillPoint(skillId, points);

            if (success && _skillLevels.ContainsKey(skillId))
            {
                _skillLevels[skillId] = characterStats.GetSkillLevel(skillId);
            }

            return success;
        }

        /// <summary>
        /// Add a perk to the character.
        /// Returns true if successful.
        /// </summary>
        public bool AddPerk(string perkId)
        {
            if (characterStats == null) return false;

            bool success = characterStats.AddPerk(perkId);

            if (success)
            {
                int newRank = characterStats.GetPerkRank(perkId);
                if (_perkRanks.ContainsKey(perkId))
                {
                    _perkRanks[perkId] = newRank;
                }
                else
                {
                    _perkRanks[perkId] = newRank;
                }
            }

            return success;
        }

        /// <summary>
        /// Add XP to the character.
        /// </summary>
        public void AddXp(int amount)
        {
            characterStats?.AddXp(amount);
        }

        /// <summary>
        /// Get skill level for a specific skill.
        /// </summary>
        public int GetSkillLevel(string skillId)
        {
            return characterStats?.GetSkillLevel(skillId) ?? 0;
        }

        /// <summary>
        /// Get skill tier (0-5 for regular skills).
        /// </summary>
        public int GetSkillTier(string skillId)
        {
            return characterStats?.GetSkillTier(skillId) ?? 0;
        }

        /// <summary>
        /// Get perk rank for a specific perk.
        /// </summary>
        public int GetPerkRank(string perkId)
        {
            return characterStats?.GetPerkRank(perkId) ?? 0;
        }

        /// <summary>
        /// Get stat factors for a specific stat (for UI display).
        /// </summary>
        public List<CharacterStats.StatFactor> GetStatFactors(string statId)
        {
            return characterStats?.GetFactorsForStat(statId) ?? new List<CharacterStats.StatFactor>();
        }

        /// <summary>
        /// Get skill definition by ID.
        /// </summary>
        public SkillDefinition GetSkillDefinition(string skillId)
        {
            SkillDefinitionDatabase db = GetSkillDatabase();
            return db?.GetSkill(skillId);
        }

        /// <summary>
        /// Get perk definition by ID.
        /// </summary>
        public PerkDefinition GetPerkDefinition(string perkId)
        {
            SkillDefinitionDatabase db = GetSkillDatabase();
            return db?.GetPerk(perkId);
        }

        /// <summary>
        /// Get all available perks for the current character.
        /// </summary>
        public PerkDefinition[] GetAvailablePerks()
        {
            SkillDefinitionDatabase db = GetSkillDatabase();
            return db?.GetAvailablePerks(characterStats) ?? new PerkDefinition[0];
        }

        /// <summary>
        /// Get all skill definitions.
        /// </summary>
        public SkillDefinition[] GetAllSkills()
        {
            SkillDefinitionDatabase db = GetSkillDatabase();
            return db?.GetAllSkills() ?? new SkillDefinition[0];
        }

        /// <summary>
        /// Get all perk definitions.
        /// </summary>
        public PerkDefinition[] GetAllPerks()
        {
            SkillDefinitionDatabase db = GetSkillDatabase();
            return db?.GetAllPerks() ?? new PerkDefinition[0];
        }

        // ========== Helper Methods ==========

        private SkillDefinitionDatabase _skillDatabase;

        /// <summary>
        /// The database the skill/perk lists are read from.
        ///
        /// <para>This used to <c>return null</c> unconditionally with a TODO. Every consumer treated
        /// that as "no data": <c>InitializePerkRanks</c> left <c>_perkRanks</c> empty,
        /// <c>CanUnlockPerk</c> always returned false, and <c>GetLevelCurve</c> (which delegates here)
        /// made the XP text read <c>/ 0</c>. The database is the same Resources asset
        /// <c>CharacterStats.EnsureResources</c> loads.</para>
        /// </summary>
        private SkillDefinitionDatabase GetSkillDatabase()
        {
            if (_skillDatabase != null) return _skillDatabase;

            _skillDatabase = Resources.Load<SkillDefinitionDatabase>("SkillDefinitionDatabase");
            if (_skillDatabase == null)
            {
                Debug.LogWarning(
                    "[CharacterStatsViewModel] SkillDefinitionDatabase not found in Resources — " +
                    "the skill and perk lists will be empty. Run " +
                    "'PFE/Data/Import Skills and Perks from AllData.as'.");
            }
            return _skillDatabase;
        }

        private LevelCurve GetLevelCurve()
        {
            SkillDefinitionDatabase db = GetSkillDatabase();
            return db?.GetLevelCurve();
        }

        /// <summary>
        /// Check if a perk can be unlocked.
        /// </summary>
        public bool CanUnlockPerk(string perkId)
        {
            PerkDefinition perk = GetPerkDefinition(perkId);
            int currentRank = characterStats != null ? characterStats.GetPerkRank(perkId) : 0;
            return perk?.CanUnlock(characterStats, currentRank) ?? false;
        }

        // ========== Formatted Display Methods ==========

        /// <summary>
        /// Get XP text formatted for display (e.g., "15000 / 275000").
        /// </summary>
        public string GetXpText()
        {
            if (characterStats == null) return "-- / --";

            int current = characterStats.Xp;
            int currentLevel = characterStats.Level;
            LevelCurve levelCurve = GetLevelCurve();
            int next = levelCurve?.GetXpForLevel(currentLevel) ?? 0;

            return $"{current:N0} / {next:N0}";
        }

        /// <summary>
        /// Get health text formatted for display (e.g., "85 / 100").
        /// </summary>
        public string GetHealthText()
        {
            if (characterStats == null) return "-- / --";

            float current = characterStats.headHp;
            float max = characterStats.MaxHp;

            return $"{Mathf.Round(current)} / {Mathf.Round(max)}";
        }

        /// <summary>
        /// Get mana text formatted for display (e.g., "50 / 100").
        /// </summary>
        public string GetManaText()
        {
            if (characterStats == null) return "-- / --";

            float current = characterStats.manaHp;
            float max = characterStats.MaxMana;

            return $"{Mathf.Round(current)} / {Mathf.Round(max)}";
        }

        /// <summary>
        /// Get critical hit chance as percentage (e.g., "5%").
        /// </summary>
        public string GetCritChanceText()
        {
            if (characterStats == null) return "--";

            float crit = characterStats.critCh * 100f;
            return $"{Mathf.RoundToInt(crit)}%";
        }

        /// <summary>
        /// Get damage multiplier as percentage (e.g., "+15%").
        /// </summary>
        public string GetDamageMultiplierText()
        {
            if (characterStats == null) return "--";

            float mult = (characterStats.AllDamMult - 1f) * 100f;
            return mult >= 0 ? $"+{Mathf.RoundToInt(mult)}%" : $"{Mathf.RoundToInt(mult)}%";
        }

        /// <summary>
        /// Get damage resistance as percentage (e.g., "10%").
        /// </summary>
        public string GetDamageResistanceText()
        {
            if (characterStats == null) return "--";

            float resist = (1f - characterStats.AllVulnerMult) * 100f;
            return $"{Mathf.RoundToInt(resist)}%";
        }

        private void OnDestroy()
        {
            if (characterStats != null)
            {
                characterStats.onLevelUp -= HandleLevelUp;
                characterStats.onSkillChanged -= HandleSkillChanged;
                characterStats.onPerkAdded -= HandlePerkAdded;
                characterStats.onStatsRecalculated -= HandleStatsRecalculated;
            }

            _disposables?.Dispose();
        }
    }
}
