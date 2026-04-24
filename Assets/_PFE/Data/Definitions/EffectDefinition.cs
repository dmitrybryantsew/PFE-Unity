using UnityEngine;
using PFE.ModAPI;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace PFE.Data.Definitions
{
    /// <summary>
    /// ScriptableObject definition for status effect data.
    /// Replaces XML-based effect definitions from AllData.as in ActionScript.
    /// Create instances via Assets > Create > PFE > Effect Definition
    ///
    /// Supports ~50 effects including: damage bonuses, status effects (poison, bleed),
    /// chem comedown, temporary stat modifications, and special abilities.
    /// </summary>
    [CreateAssetMenu(fileName = "NewEffectDef", menuName = "PFE/Effect Definition")]
    public class EffectDefinition : ScriptableObject, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique ID for this effect")]
        public string effectId;

        // IGameContent
        string IGameContent.ContentId => effectId;
        ContentType IGameContent.ContentType => ContentType.Effect;

        // Legacy property for compatibility
        public string ID => effectId;

#if ODIN_INSPECTOR
        [BoxGroup("Core")]
#else
        [Header("Core")]
#endif
        [Tooltip("Effect type (beneficial, harmful, or special)")]
        public EffectType type = EffectType.Neutral;

        [Tooltip("Duration in ticks (30 ticks = 1 second at 30 FPS)")]
        public int durationTicks = 100;

        [Tooltip("Is this a chem-based effect")]
        public bool isChem = false;

        [Tooltip("Is this a bad comedown effect")]
        public bool isBadComedown = false;

        [Tooltip("Base value (used for various effects)")]
        public int value = 0;

#if ODIN_INSPECTOR
        [BoxGroup("Effects")]
#else
        [Header("Effects")]
#endif
        [Tooltip("Skill modifiers applied by this effect")]
        public SkillModifier[] effects;

#if ODIN_INSPECTOR
        [BoxGroup("Aftereffects")]
#else
        [Header("Aftereffects")]
#endif
        [Tooltip("Effect ID to apply after duration ends")]
        public string afterEffectId;

        [Header("Display")]
        [TextArea(3, 10)]
        [Tooltip("Display name in UI")]
        public string displayName;

        [Tooltip("Display value (formatted)")]
        public string displayValue;
    }
}
