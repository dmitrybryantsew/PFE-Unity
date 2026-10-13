using UnityEngine;

namespace PFE.UI.Common
{
    /// <summary>
    /// The Oracle's interface colours as <see cref="Color"/> values, for code that needs to tint an
    /// <c>Image</c>, a bar fill or a cursor rather than emit a rich-text tag.
    ///
    /// <para>Every value here is derived from <see cref="FlashTextStyle"/> rather than written out
    /// twice, so the hex string a text tag uses and the <see cref="Color"/> a bar uses cannot drift
    /// apart. That matters because several of these colours are used both ways — the health bar's
    /// threshold colour and the health readout's text colour are the same Oracle constant.</para>
    ///
    /// <para>Note that the two style sheets genuinely disagree, so the sheet-specific colours are
    /// exposed under sheet-specific names (<see cref="HudR0"/> versus <see cref="PageR0"/>). Do not
    /// "unify" them.</para>
    /// </summary>
    public static class PfeUiColors
    {
        // ── HUD sheet (GUI.as:189-217) ────────────────────────────────────────

        public static readonly Color Green = FromHex(FlashTextStyle.HudR);      // .r
        public static readonly Color Grey = FromHex(FlashTextStyle.HudR0);       // .r0
        public static readonly Color Cyan = FromHex(FlashTextStyle.HudR1);       // .r1
        public static readonly Color Yellow = FromHex(FlashTextStyle.HudR2);     // .r2
        public static readonly Color Orange = FromHex(FlashTextStyle.HudR3);     // .r3
        public static readonly Color Magenta = FromHex(FlashTextStyle.HudR4);    // .r4
        public static readonly Color Red = FromHex(FlashTextStyle.HudR5);        // .r5 / .warn
        public static readonly Color Violet = FromHex(FlashTextStyle.HudR6);     // .r6
        public static readonly Color Tan = FromHex(FlashTextStyle.HudR7);        // .r7
        public static readonly Color Olive = FromHex(FlashTextStyle.HudR8);      // .r8
        public static readonly Color Blue = FromHex(FlashTextStyle.HudR9);       // .r9
        public static readonly Color Crimson = FromHex(FlashTextStyle.HudCrim);  // .crim

        /// <summary>Hotkey substitution colour — see <see cref="FlashTextStyle.Imp"/> for why this is a choice.</summary>
        public static readonly Color Important = FromHex(FlashTextStyle.Imp);

        // ── Page sheet (PipPage.as:163-190) ───────────────────────────────────
        // Deliberately distinct names: these are NOT the HUD colours of the same class name.

        public static readonly Color PageR0 = FromHex(FlashTextStyle.PageR0);
        public static readonly Color PageRed = FromHex(FlashTextStyle.PageRed);
        public static readonly Color PageYellow = FromHex(FlashTextStyle.PageYel);
        public static readonly Color PageOrange = FromHex(FlashTextStyle.PageOr);
        public static readonly Color PagePink = FromHex(FlashTextStyle.PagePink);
        public static readonly Color PageBlue = FromHex(FlashTextStyle.PageBlu);
        public static readonly Color PageMass = FromHex(FlashTextStyle.PageMass);
        public static readonly Color PageDark = FromHex(FlashTextStyle.PageDark);
        public static readonly Color PageLight = FromHex(FlashTextStyle.PageLight);
        public static readonly Color PageGreen = FromHex(FlashTextStyle.PageGreen);
        public static readonly Color PagePurple = FromHex(FlashTextStyle.PagePurp);

        // ── Threshold colours used by the PipBuck right-hand monitor ───────────
        // PipBuck.med() (PipBuck.as:586-597) returns a CLASS NAME, not a colour: 'red' below 25 %,
        // 'or' below 50 %, otherwise the default. Resolved here to the page sheet's values, because the
        // monitor rows are drawn with PipPage's style sheet, not the HUD's.

        /// <summary>Below 25 % — <c>med()</c> returns <c>red</c>.</summary>
        public static readonly Color MonitorCritical = PageRed;

        /// <summary>Below 50 % — <c>med()</c> returns <c>or</c>.</summary>
        public static readonly Color MonitorLow = PageOrange;

        /// <summary>25 % / 50 % thresholds, as fractions, matching <c>PipBuck.med()</c>.</summary>
        public const float MonitorCriticalFraction = 0.25f;
        public const float MonitorLowFraction = 0.50f;

        /// <summary>
        /// The colour <c>PipBuck.med()</c> would pick for a value of <paramref name="fraction"/>.
        /// </summary>
        public static Color ForMonitorFraction(float fraction)
        {
            if (fraction < MonitorCriticalFraction) return MonitorCritical;
            if (fraction < MonitorLowFraction) return MonitorLow;
            return PageR0;
        }

        /// <summary>
        /// Parses <c>#RRGGBB</c> (or <c>#RRGGBBAA</c>). Falls back to magenta on a malformed string,
        /// because an unparseable colour is a typo in this file and magenta is impossible to miss on a
        /// green-on-black interface — a silent black would render as invisible text instead.
        /// </summary>
        public static Color FromHex(string hex)
        {
            if (!string.IsNullOrEmpty(hex) && hex[0] == '#') hex = hex.Substring(1);

            if (string.IsNullOrEmpty(hex) || (hex.Length != 6 && hex.Length != 8))
            {
                Debug.LogWarning($"[PfeUiColors] Malformed hex colour '{hex}' — using magenta so it is visible.");
                return Color.magenta;
            }

            if (!TryHexByte(hex, 0, out byte r) ||
                !TryHexByte(hex, 2, out byte g) ||
                !TryHexByte(hex, 4, out byte b))
            {
                Debug.LogWarning($"[PfeUiColors] Malformed hex colour '#{hex}' — using magenta so it is visible.");
                return Color.magenta;
            }

            byte a = 255;
            if (hex.Length == 8 && !TryHexByte(hex, 6, out a))
            {
                Debug.LogWarning($"[PfeUiColors] Malformed alpha in '#{hex}' — using opaque.");
                a = 255;
            }

            return new Color32(r, g, b, a);
        }

        private static bool TryHexByte(string hex, int index, out byte value)
        {
            value = 0;
            int high = HexDigit(hex[index]);
            int low = HexDigit(hex[index + 1]);
            if (high < 0 || low < 0) return false;

            value = (byte)((high << 4) | low);
            return true;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
