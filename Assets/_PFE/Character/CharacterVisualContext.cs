using System;

namespace PFE.Character
{
    /// <summary>
    /// Runtime-only character composition state.
    /// Mirrors the original AS3 fields that were not part of the saved appearance itself.
    /// </summary>
    [Serializable]
    public struct CharacterVisualContext
    {
        public string armorId;
        public bool hideMane;
        public bool transparent;
        /// <summary>
        /// Whether to show wing parts (lwing/rwing).
        /// False by default — wings are granted by an in-game potion, not part of base appearance.
        /// </summary>
        public bool showWings;

        public static CharacterVisualContext Default => new()
        {
            armorId = string.Empty,
            hideMane = false,
            transparent = false,
            showWings = false
        };
    }
}
