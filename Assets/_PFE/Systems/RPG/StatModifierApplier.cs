using UnityEngine;
using PFE.Systems.RPG.Data;

namespace PFE.Systems.RPG
{
    /// <summary>
    /// Pure C# stat modifier applier.
    /// Direct port of AS3 Pers.setSkillParam (Pers.as:1468-1573).
    /// Uniformly evaluates and applies <sk> modifiers from skills, perks, items, and status effects.
    /// </summary>
    public static class StatModifierApplier
    {
        /// <summary>
        /// Evaluate modifier value according to AS3 formula.
        /// </summary>
        public static float Evaluate(StatModifier mod, int tierOrRank, int rawPoints)
        {
            if (mod == null) return 0f;
            return mod.Evaluate(tierOrRank, rawPoints);
        }

        /// <summary>
        /// Applies the modifier to the CharacterStats instance following AS3 setSkillParam rules.
        /// </summary>
        /// <param name="sourceType">
        /// The <see cref="CharacterStats.StatFactor.sourceType"/> to record: "skill", "perk", ...
        /// Distinct from <c>sourceId</c> (which instance) and from the modifier's own <c>refType</c>
        /// (how: add vs mult). Defaults to "skill" because this applier's callers are the skill and
        /// perk loops.
        /// </param>
        public static void Apply(CharacterStats stats, StatModifier mod, int tierOrRank, int rawPoints,
                                 string sourceId = null, string sourceType = "skill")
        {
            if (stats == null || mod == null) return;

            float val = mod.Evaluate(tierOrRank, rawPoints);
            stats.ApplyNamedStat(mod.statId, mod.tip, mod.refType, val, sourceId, sourceType);
        }
    }
}
