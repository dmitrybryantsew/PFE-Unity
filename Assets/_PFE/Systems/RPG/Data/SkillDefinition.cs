using UnityEngine;
using PFE.ModAPI;

namespace PFE.Systems.RPG.Data
{
    /// <summary>
    /// ScriptableObject definition for RPG skills.
    /// Based on docs/task1_core_mechanics/08_rpg_system.md
    ///
    /// Supports 18 skills:
    /// - 13 Regular Skills (cap at level 20)
    /// - 3 Post-Game Skills (cap at level 100): attack, defense, knowl
    /// - 2 Special Skills (level 40+ rewards): life, spirit
    /// </summary>
    [CreateAssetMenu(fileName = "NewSkill", menuName = "RPG/Skill Definition")]
    public class SkillDefinition : ScriptableObject, IGameContent
    {
        [Header("Identity")]
        [SerializeField] public string skillId;

        // IGameContent
        string IGameContent.ContentId => skillId;
        ContentType IGameContent.ContentType => ContentType.Skill;

        [SerializeField] public string displayName;
        [SerializeField] public string description;

        [Header("Settings")]
        [SerializeField] public bool isPostGame = false;
        [SerializeField] public int maxLevel = 20;
        [SerializeField] public int sortOrder = 0;

        [Header("Level Effects")]
        [SerializeField] public SkillLevelEffect[] levelEffects;

        [Header("Stat Modifiers")]
        [SerializeField] public StatModifier[] modifiers;

        // Public properties (backwards compatible)
        public string SkillId => skillId;
        public string DisplayName => displayName;
        public string Description => description;
        public bool IsPostGame => isPostGame;
        public int MaxLevel => maxLevel;
        public int SortOrder => sortOrder;
        public SkillLevelEffect[] LevelEffects => levelEffects;
        public StatModifier[] Modifiers => modifiers;

        /// <summary>
        /// Get skill tier (0-5) for regular skills based on level.
        /// Thresholds: 2, 5, 9, 14, 20
        /// </summary>
        public int GetSkillTier(int skillLevel)
        {
            if (skillLevel >= 20) return 5;
            if (skillLevel >= 14) return 4;
            if (skillLevel >= 9) return 3;
            if (skillLevel >= 5) return 2;
            if (skillLevel >= 2) return 1;
            return 0;
        }

        /// <summary>
        /// Get post-game skill tier (0-10) for knowl skill.
        /// Thresholds: [5, 11, 18, 26, 35, 45, 56, 68, 82, 100]
        /// </summary>
        public int GetPostSkillTier(int skillLevel)
        {
            int[] thresholds = { 5, 11, 18, 26, 35, 45, 56, 68, 82, 100 };
            int tier = 0;
            for (int i = 0; i < thresholds.Length; i++)
            {
                if (skillLevel >= thresholds[i])
                    tier = i + 1;
            }
            return tier;
        }
    }

    /// <summary>
    /// Defines stat modifiers unlocked at specific skill levels/tiers.
    /// </summary>
    [System.Serializable]
    public class SkillLevelEffect
    {
        [Tooltip("The skill tier required for this effect (0-5 for regular skills)")]
        public int levelThreshold;

        [Tooltip("Stat modifiers applied at this tier")]
        public StatModifier[] modifiers;

        [Tooltip("Text variables set at this tier")]
        public TextVariable[] textVariables;
    }

    /// <summary>
    /// Modifies a character stat based on skill/perk level.
    /// </summary>
    [System.Serializable]
    public class StatModifier
    {
        [Tooltip("Stat ID to modify (e.g., 'maxhp', 'allDamMult', 'meleeR')")]
        public string statId;

        [Tooltip("How to apply the modifier")]
        public ModifierType type;

        [Tooltip("Which entity this affects")]
        public ModifierTarget target;

        [Tooltip("Value per level: v0, v1, v2, v3, v4, v5")]
        public float[] values;

        [Tooltip("Linear increase per level")]
        public float valueDelta;

        [Tooltip("Is this a stacking multiplier (dop=1 in XML)")]
        public bool isMultiplier;

        // Oracle AS3 <sk> mirror fields
        public string tip;
        public string refType;
        public bool dop;
        public float v0;
        public bool hasV0;
        public float vd;
        public bool hasVd;
        public float[] v;

        /// <summary>
        /// Evaluates the modifier value according to the AS3 setSkillParam rules
        /// (<c>Pers.as:1470-1515</c>).
        /// tierOrRank is 0..5 for standard skills, 0..10 for post-game skills, 1..maxRank for perks.
        /// rawPoints is the raw skill points (0..20/0..100) when dop="1".
        ///
        /// <para><b>Level indexing is <c>v[level]</c>, with no shift.</b> The importer builds the
        /// array as <c>v[0] = v0, v[i] = v_i</c>, which is exactly AS3's <c>attribute("v"+level)</c>.
        /// An earlier revision special-cased a 5-element array with <c>level - 1</c>, which shifted
        /// every tier down by one <i>and</i> disagreed with the branch directly below it.</para>
        ///
        /// <para><b>Out of range falls back to <c>v1</c></b>, not to <c>v[0]</c>: AS3's last resort is
        /// <c>Number(_loc4_.@v1)</c> (<c>:1511</c>), and <c>v[0]</c> is a fabricated <c>0</c> whenever
        /// the XML carries no <c>v0</c> attribute. Returning it turned every "past the end of the
        /// table" read into a zero — e.g. <c>maxTeleMassa</c> and <c>spellsDamMult</c> at mana-trauma
        /// stage 1.</para>
        ///
        /// <para><b><c>ref="mult"</c> is linear, not compound.</b> AS3 computes
        /// <c>base + level * vd</c> for every ref type; only the <i>application</i> differs
        /// (<c>*=</c> vs <c>+=</c>). Compounding here diverged from the oracle for
        /// <c>survival</c>'s <c>allVulnerMult</c> and <c>potion_speed</c>'s rank-2 entries.</para>
        /// </summary>
        public float Evaluate(int tierOrRank, int rawPoints)
        {
            int level = dop ? rawPoints : tierOrRank;

            // Discrete array v — AS3: attribute("v"+level) if present, else Number(v1).
            if (v != null && v.Length > 0)
            {
                if (level == 0) return As3Base();
                if (level < v.Length && !float.IsNaN(v[level])) return v[level];
                if (v.Length > 1 && !float.IsNaN(v[1])) return v[1];
                return As3Base();
            }

            // Legacy hand-authored `values` array. The importer always fills `v` too, so production
            // never reaches this; kept so older assets keep loading.
            //
            // Index IS the level here, so values[0] is the level-0 value — which is exactly why this
            // branch must NOT reuse the `level == 0 -> As3Base()` rule from the `v` branch above. In
            // the importer's `v`, slot 0 is a fabricated placeholder meaning "no v0 attribute"; in a
            // hand-authored `values` array it is real data. Applying the AS3 rule here made
            // GetValueForLevel(0) return As3Base() (= 0 for an Add modifier) and silently discarded
            // every level-0 entry — which is what SkillDefinitionTests
            // .StatModifier_GetValueForLevel_WithValuesArray_ReturnsCorrectValue caught.
            if (values != null && values.Length > 0)
            {
                if (level >= 0 && level < values.Length && !float.IsNaN(values[level]))
                {
                    return values[level];
                }

                if (valueDelta != 0f) return values[0] + level * valueDelta;

                return values[0];
            }

            float baseV = As3Base();

            if (level == 0) return baseV;

            // AS3 Pers.as:1501-1503 — one linear term for every ref type.
            if (hasVd) return baseV + level * vd;
            if (valueDelta != 0f) return baseV + level * valueDelta;

            return baseV;
        }

        /// <summary>
        /// AS3's <c>_loc7_</c> (<c>Pers.as:1476-1496</c>) — the level-0 value, and the term the
        /// <c>vd</c> slope is added to.
        ///
        /// <para>AS3 declares <c>_loc7_:Number = NaN</c> at <c>:1473</c> but overwrites it with
        /// <c>_loc7_ = 0</c> as the first statement of every loop iteration (<c>:1476</c>), before any
        /// of the <c>v0</c>/<c>add</c>/<c>res</c>/<c>mult</c> tests. So the base is <b>0</b> whenever
        /// none of those match — the <c>0</c> returned here is faithful, not a substitution. (Only the
        /// <i>final</i> fallback differs: AS3's <c>Number(@v1)</c> on a missing <c>v1</c> yields
        /// <c>NaN</c>, where this returns the base. No element in AllData.as reaches that branch —
        /// every skill/perk <c>&lt;sk&gt;</c> has either a <c>vd</c> or a <c>v1</c>.)</para>
        /// </summary>
        private float As3Base()
        {
            if (hasV0) return v0;
            if (refType == "add" || tip == "res" || type == ModifierType.Add) return 0f;
            if (refType == "mult" || type == ModifierType.Multiply) return 1f;
            return 0f;
        }

        /// <summary>
        /// Get the modifier value for a specific level (backwards compatibility).
        /// </summary>
        public float GetValueForLevel(int level)
        {
            return Evaluate(level, level);
        }
    }

    public enum ModifierType
    {
        Add,           // Add to base value
        Multiply,      // Multiply with current value (stacking)
        Set,           // Set to absolute value
        WeaponSkill    // +5% weapon skill per level
    }

    public enum ModifierTarget
    {
        Player,
        Pers,
        Unit
    }

    /// <summary>
    /// Key-value text pair for dynamic text variables.
    /// </summary>
    [System.Serializable]
    public class TextVariable
    {
        public string key;   // e.g., "s1", "s2"
        public string value;
    }
}
