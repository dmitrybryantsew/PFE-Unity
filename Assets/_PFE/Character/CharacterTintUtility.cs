using UnityEngine;

namespace PFE.Character
{
    /// <summary>
    /// Converts selected avatar RGB colors into the softer Flash-style render multipliers
    /// used by the original customization system.
    /// </summary>
    public static class CharacterTintUtility
    {
        const float FlashTintDenominator = 290f;
        const float FlashTintBase = 35f / 255f;

        public static Color ToFlashMultiplier(Color color)
        {
            Color32 rgb = color;
            return new Color(
                rgb.r / FlashTintDenominator + FlashTintBase,
                rgb.g / FlashTintDenominator + FlashTintBase,
                rgb.b / FlashTintDenominator + FlashTintBase,
                1f);
        }

        public static Color GetRenderTint(Color selectedColor, bool useFlashMultiplier)
        {
            return useFlashMultiplier ? ToFlashMultiplier(selectedColor) : selectedColor;
        }
    }
}
