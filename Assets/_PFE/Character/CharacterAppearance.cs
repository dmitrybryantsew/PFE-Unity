using System;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Character
{
    /// <summary>
    /// Runtime data model for a character's visual appearance.
    /// Replaces the original AS3 Appear.as static fields.
    ///
    /// Holds chosen colors for each tint channel and style selections.
    /// Serializable for save/load and inspector editing.
    /// </summary>
    [Serializable]
    public class CharacterAppearance
    {
        // ─── Colors ──────────────────────────────────────────

        [Header("Colors")]
        [Tooltip("Body, neck, legs, wings, horn base")]
        public Color furColor = new Color32(163, 163, 163, 255);

        [Tooltip("Mane h0, tail h0, forelock h0")]
        public Color primaryHairColor = new Color32(133, 80, 9, 255);

        [Tooltip("Mane h1, tail h1, forelock h1 (if visible)")]
        public Color secondaryHairColor = Color.white;

        [Tooltip("Eye iris tint")]
        public Color eyeColor = new Color32(22, 241, 67, 255);

        [Tooltip("Horn glow, magic circles, spell effects")]
        public Color magicColor = new Color32(0, 255, 0, 255);

        // ─── Styles ─────────────────────────────────────────

        [Header("Styles")]
        [Tooltip("Hair/mane style index (1-based, 1..5 in base game)")]
        [Range(1, 10)]
        public int hairStyle = 1;

        [Tooltip("Eye style index (1-based, 1..6 in base game)")]
        [Range(1, 10)]
        public int eyeStyle = 1;

        [Tooltip("Whether secondary hair color layer is visible")]
        public bool showSecondaryHair = false;

        // ─── API ────────────────────────────────────────────

        /// <summary>
        /// Get color by tint category.
        /// </summary>
        public Color GetColor(TintCategory category)
        {
            return category switch
            {
                TintCategory.Fur => furColor,
                TintCategory.Hair => primaryHairColor,
                TintCategory.Hair2 => secondaryHairColor,
                TintCategory.Eye => eyeColor,
                TintCategory.Magic => magicColor,
                _ => Color.white,
            };
        }

        /// <summary>
        /// Set color by tint category.
        /// </summary>
        public void SetColor(TintCategory category, Color color)
        {
            switch (category)
            {
                case TintCategory.Fur: furColor = color; break;
                case TintCategory.Hair: primaryHairColor = color; break;
                case TintCategory.Hair2: secondaryHairColor = color; break;
                case TintCategory.Eye: eyeColor = color; break;
                case TintCategory.Magic: magicColor = color; break;
            }
        }

        /// <summary>
        /// Get color by channel index (matches AS3 Obj.setColor indices).
        /// 0=Fur, 1=PrimaryHair, 2=SecondaryHair, 3=Eye, 4=Magic
        /// </summary>
        public Color GetColorByIndex(int index) => GetColor((TintCategory)index);

        /// <summary>
        /// Set color by channel index.
        /// </summary>
        public void SetColorByIndex(int index, Color color) => SetColor((TintCategory)index, color);

        /// <summary>
        /// Total number of editable color channels (for UI iteration).
        /// </summary>
        public const int ColorChannelCount = 5;

        /// <summary>
        /// Display name for each color channel (for UI labels).
        /// </summary>
        public static string GetChannelName(int index) => index switch
        {
            0 => "Hide color",
            1 => "Primary mane color",
            2 => "Secondary mane color",
            3 => "Eyes color and shape",
            4 => "Magical aura color",
            _ => "Unknown",
        };

        /// <summary>
        /// Whether the given channel has style arrows (◄ ►) in the UI.
        /// </summary>
        public static bool ChannelHasStyleArrows(int index) => index is 1 or 3;

        // ─── Defaults (match original AS3 Appear.as) ────────

        /// <summary>
        /// Returns a new CharacterAppearance with the original game's default values.
        /// </summary>
        public static CharacterAppearance CreateDefault()
        {
            return new CharacterAppearance
            {
                // Original AS3 defaults (converted from uint to Color):
                // cFur=10724259 → #A3A3A3
                furColor = new Color32(163, 163, 163, 255),
                // cHair=8734217 → #855009, trHair (163/255, 86/255, 11/255)
                primaryHairColor = new Color32(133, 80, 9, 255),
                // cHair1=16777215 → #FFFFFF
                secondaryHairColor = Color.white,
                // cEye=1504067 → #16F143, trEye (0, 0.9, 0)
                eyeColor = new Color32(22, 241, 67, 255),
                // cMagic=65280 → #00FF00
                magicColor = new Color32(0, 255, 0, 255),

                hairStyle = 1,
                eyeStyle = 1,
                showSecondaryHair = false,
            };
        }

        /// <summary>
        /// Copy all values from another appearance.
        /// </summary>
        public void CopyFrom(CharacterAppearance other)
        {
            furColor = other.furColor;
            primaryHairColor = other.primaryHairColor;
            secondaryHairColor = other.secondaryHairColor;
            eyeColor = other.eyeColor;
            magicColor = other.magicColor;
            hairStyle = other.hairStyle;
            eyeStyle = other.eyeStyle;
            showSecondaryHair = other.showSecondaryHair;
        }

        /// <summary>
        /// Create a deep copy.
        /// </summary>
        public CharacterAppearance Clone()
        {
            var copy = new CharacterAppearance();
            copy.CopyFrom(this);
            return copy;
        }

        // ─── Serialization (save/load) ──────────────────────

        /// <summary>
        /// Convert to save-friendly format. Uses string style IDs for mod compatibility.
        /// </summary>
        public CharacterAppearanceSaveData ToSaveData()
        {
            return new CharacterAppearanceSaveData
            {
                furColor = ColorToUint(furColor),
                primaryHairColor = ColorToUint(primaryHairColor),
                secondaryHairColor = ColorToUint(secondaryHairColor),
                eyeColor = ColorToUint(eyeColor),
                magicColor = ColorToUint(magicColor),
                hairStyle = hairStyle,
                eyeStyle = eyeStyle,
                showSecondaryHair = showSecondaryHair,
            };
        }

        /// <summary>
        /// Load from save data.
        /// </summary>
        public void LoadFromSaveData(CharacterAppearanceSaveData data)
        {
            furColor = UintToColor(data.furColor);
            primaryHairColor = UintToColor(data.primaryHairColor);
            secondaryHairColor = UintToColor(data.secondaryHairColor);
            eyeColor = UintToColor(data.eyeColor);
            magicColor = UintToColor(data.magicColor);
            hairStyle = data.hairStyle;
            eyeStyle = data.eyeStyle;
            showSecondaryHair = data.showSecondaryHair;
        }

        static uint ColorToUint(Color c)
        {
            byte r = (byte)(Mathf.Clamp01(c.r) * 255);
            byte g = (byte)(Mathf.Clamp01(c.g) * 255);
            byte b = (byte)(Mathf.Clamp01(c.b) * 255);
            return (uint)((r << 16) | (g << 8) | b);
        }

        static Color UintToColor(uint v)
        {
            float r = ((v >> 16) & 0xFF) / 255f;
            float g = ((v >> 8) & 0xFF) / 255f;
            float b = (v & 0xFF) / 255f;
            return new Color(r, g, b, 1f);
        }
    }

    /// <summary>
    /// Serializable save data for character appearance.
    /// Uses uint colors and int styles for compact storage.
    /// </summary>
    [Serializable]
    public class CharacterAppearanceSaveData
    {
        public uint furColor;
        public uint primaryHairColor;
        public uint secondaryHairColor;
        public uint eyeColor;
        public uint magicColor;
        public int hairStyle;
        public int eyeStyle;
        public bool showSecondaryHair;
    }
}
