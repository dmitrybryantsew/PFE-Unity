using UnityEngine;
using PFE.ModAPI;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace PFE.Data.Definitions
{
    /// <summary>
    /// ScriptableObject definition for perk data.
    /// Replaces XML-based perk definitions from AllData.as in ActionScript.
    /// Create instances via Assets > Create > PFE > Perk Definition
    ///
    /// Supports ~80 perks including: good perks, bad perks (traits), and multi-rank perks
    /// with prerequisites based on skills, level, and difficulty.
    /// </summary>
    [CreateAssetMenu(fileName = "NewPerkDef", menuName = "PFE/Perk Definition")]
    public class PerkDefinition : ScriptableObject, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique ID for this perk")]
        public string perkId;

        // IGameContent
        string IGameContent.ContentId => perkId;
        ContentType IGameContent.ContentType => ContentType.Perk;

        // Legacy property for compatibility
        public string ID => perkId;

#if ODIN_INSPECTOR
        [BoxGroup("Core")]
#else
        [Header("Core")]
#endif
        [Tooltip("Perk type classification")]
        public PerkType type = PerkType.Selectable;

        [Tooltip("Maximum ranks (1 for single-rank perks, >1 for multi-rank)")]
        public int maxRanks = 1;

#if ODIN_INSPECTOR
        [BoxGroup("Requirements")]
#else
        [Header("Requirements")]
#endif
        [Tooltip("Prerequisites to unlock this perk")]
        public PerkRequirement[] requirements;

#if ODIN_INSPECTOR
        [BoxGroup("Effects")]
#else
        [Header("Effects")]
#endif
        [Tooltip("Skill modifiers granted by this perk")]
        public SkillModifier[] effects;

        [Header("Display")]
        [TextArea(3, 10)]
        [Tooltip("Display name in UI")]
        public string displayName;

        [TextArea(3, 10)]
        [Tooltip("Perk description")]
        public string description;

        [Tooltip("Display values for each rank")]
        public string[] displayValues;
    }
}
