using PFE.Character;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.UI.MainMenu
{
    /// <summary>
    /// Dialog-time working copy for the character customization flow.
    /// Mirrors the original Flash dialog behavior: open snapshot, working copy, default reset, commit, cancel.
    /// </summary>
    public sealed class CharacterCustomizationSession
    {
        readonly CharacterAppearance _defaultAppearance;
        readonly int _hairStyleCount;
        readonly int _eyeStyleCount;

        public CharacterCustomizationSession(CharacterAppearance source, int hairStyleCount, int eyeStyleCount)
        {
            Original = source?.Clone() ?? CharacterAppearance.CreateDefault();
            Working = Original.Clone();
            _defaultAppearance = CharacterAppearance.CreateDefault();
            _hairStyleCount = Mathf.Max(1, hairStyleCount);
            _eyeStyleCount = Mathf.Max(1, eyeStyleCount);
            ActiveChannel = TintCategory.Fur;
        }

        public CharacterAppearance Original { get; }
        public CharacterAppearance Working { get; }
        public TintCategory ActiveChannel { get; private set; }

        public void SetActiveChannel(TintCategory channel)
        {
            if (channel < TintCategory.Fur || channel > TintCategory.Magic)
            {
                return;
            }

            ActiveChannel = channel;
        }

        public Color GetColor(TintCategory channel)
        {
            return Working.GetColor(channel);
        }

        public Color GetActiveColor()
        {
            return GetColor(ActiveChannel);
        }

        public void SetColor(TintCategory channel, Color color)
        {
            Working.SetColor(channel, NormalizeColor(color));
        }

        public void SetActiveColor(Color color)
        {
            SetColor(ActiveChannel, color);
        }

        public void SetChannelRgb(byte red, byte green, byte blue)
        {
            SetActiveColor(new Color32(red, green, blue, 255));
        }

        public void GetActiveRgb(out byte red, out byte green, out byte blue)
        {
            Color32 color = GetActiveColor();
            red = color.r;
            green = color.g;
            blue = color.b;
        }

        public void CycleHair(int direction)
        {
            Working.hairStyle = WrapStyleIndex(Working.hairStyle, direction, _hairStyleCount);
        }

        public void CycleEyes(int direction)
        {
            Working.eyeStyle = WrapStyleIndex(Working.eyeStyle, direction, _eyeStyleCount);
        }

        public void SetSecondaryHairVisible(bool visible)
        {
            Working.showSecondaryHair = visible;
        }

        public void ResetToDefaults()
        {
            Working.CopyFrom(_defaultAppearance);
            ActiveChannel = TintCategory.Fur;
        }

        public CharacterAppearance Commit()
        {
            return Working.Clone();
        }

        public CharacterAppearance Cancel()
        {
            return Original.Clone();
        }

        static int WrapStyleIndex(int currentValue, int direction, int count)
        {
            if (count <= 0)
            {
                return 1;
            }

            int zeroBased = Mathf.Clamp(currentValue - 1, 0, count - 1);
            zeroBased = (zeroBased + direction) % count;
            if (zeroBased < 0)
            {
                zeroBased += count;
            }

            return zeroBased + 1;
        }

        static Color NormalizeColor(Color color)
        {
            Color32 rgb = color;
            return new Color32(rgb.r, rgb.g, rgb.b, 255);
        }
    }
}
