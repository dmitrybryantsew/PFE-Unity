using System;
using System.Collections.Generic;
using System.Text;

namespace PFE.UI.Common
{
    /// <summary>
    /// Which of the Oracle's <b>two</b> style sheets a string is written against.
    ///
    /// <para><b>There are two, and they disagree.</b> This is the single most easily-missed detail in
    /// the interface text system. <c>GUI.as:189-217</c> registers one sheet for the HUD, the dialogue
    /// window and the terminal reader; <c>PipPage.setStyle</c> (<c>PipPage.as:163-190</c>) registers a
    /// <i>different</i> sheet for every PipBuck page. They share class names with different meanings —
    /// <c>.r0</c> is muted grey <c>#999999</c> on the HUD and phosphor green <c>#00FF99</c> on a page,
    /// and <c>.yel</c> is <c>#FFFF00</c> on the HUD and <c>#FFFF33</c> on a page. A port that resolves
    /// one sheet for both renders the inventory page in the wrong colours with nothing to notice it.</para>
    /// </summary>
    public enum FlashStyleSheet
    {
        /// <summary>GUI.as:189-217 — HUD, dialogue, terminal reader.</summary>
        Hud,

        /// <summary>PipPage.as:163-190 — every PipBuck page.</summary>
        Page,
    }

    /// <summary>
    /// The Oracle's text style sheets as plain hex strings.
    ///
    /// <para><b>Unity-free on purpose.</b> Keeping this file free of <c>UnityEngine</c> means the
    /// formatter can be exercised from a plain console harness against the real Oracle strings, which is
    /// the only way to check a pure string translation without the editor. The <c>Color</c> values live
    /// in <see cref="PfeUiColors"/>, which is where a Unity type belongs.</para>
    ///
    /// <para>Values transcribed from the source, not from the specification: the specification lists 14
    /// HUD styles and omits the page sheet entirely.</para>
    /// </summary>
    public static class FlashTextStyle
    {
        // ── HUD sheet — GUI.as:189-217 ────────────────────────────────────────

        public const string HudR = "#00FF99";   // .r   — primary PipBuck phosphor green
        public const string HudR0 = "#999999";  // .r0  — muted / empty / disabled
        public const string HudR1 = "#00FFFF";  // .r1  — cyan, secondary highlight
        public const string HudR2 = "#FFFF00";  // .r2  — warning yellow
        public const string HudR3 = "#FF9900";  // .r3  — orange, severe warning
        public const string HudR4 = "#FC7FED";  // .r4  — magenta, magic / alchemy
        public const string HudR5 = "#FF3333";  // .r5  — red, hostile / lethal
        public const string HudR6 = "#BB99FF";  // .r6  — violet
        public const string HudR7 = "#C4926C";  // .r7  — tan / leather
        public const string HudR8 = "#98BD34";  // .r8  — olive green
        public const string HudR9 = "#7777FF";  // .r9  — blue
        public const string HudWarn = "#FF3333"; // .warn — same value as .r5
        public const string HudCrim = "#FF236A"; // .crim — alicorn corruption
        public const string HudYel = "#FFFF00";  // .yel  — keyword highlight, BOLD

        // ── Page sheet — PipPage.as:163-190 ───────────────────────────────────

        public const string PageR0 = "#00FF99";    // .r0   — green (NOT the HUD's grey)
        public const string PageRed = "#FF3333";   // .red
        public const string PageYel = "#FFFF33";   // .yel  (NOT the HUD's #FFFF00)
        public const string PageOr = "#FF9900";    // .or
        public const string PagePink = "#FC7FED";  // .pink
        public const string PageBlu = "#00FFFF";   // .blu
        public const string PageMass = "#99CCFF";  // .mass — right-side monitor mass rows
        public const string PageDark = "#007E4B";  // .dark
        public const string PageLight = "#8AFFD0"; // .light
        public const string PageGreen = "#33FF33"; // .green
        public const string PagePurp = "#B466FF";  // .purp

        /// <summary>
        /// The colour used for the <c>imp</c> class — hotkey substitutions in dialogue
        /// (<c>GUI.as:1823</c>) and the <c>@1..@5</c> tokens.
        ///
        /// <para><b>Deliberate deviation.</b> <c>imp</c> is registered by <i>neither</i> sheet — the
        /// Oracle writes <c>&lt;span class='imp'&gt;</c> and never defines it, so Flash renders those
        /// runs with the TextField's default format, which is black. Black text on the PipBuck's dark
        /// screen is invisible, and a hotkey hint the player cannot read is worse than one in the wrong
        /// colour, so the port assigns it the cyan already used for "secondary highlight"
        /// (<see cref="HudR1"/>) rather than reproducing an unreadable default.</para>
        /// </summary>
        public const string Imp = HudR1;

        /// <summary>
        /// Resolves a Flash style class name for a sheet.
        /// </summary>
        /// <param name="sheet">Which of the two sheets to resolve against.</param>
        /// <param name="className">The class name from <c>&lt;span class='…'&gt;</c>, with or without the leading dot.</param>
        /// <param name="hex">The resolved <c>#RRGGBB</c> colour, or null when the class is unknown.</param>
        /// <param name="bold">True for classes the Oracle declares <c>fontWeight = "bold"</c> (only <c>.yel</c>).</param>
        /// <returns>True when the class is defined by that sheet.</returns>
        public static bool TryResolve(FlashStyleSheet sheet, string className, out string hex, out bool bold)
        {
            hex = null;
            bold = false;

            if (string.IsNullOrEmpty(className)) return false;

            // The Oracle writes both `<span class='r'>` and `<span class = 'yel'>`, and callers pass
            // the name with and without the dot, so normalise once here rather than at four call sites.
            string name = className.Trim();
            if (name.StartsWith(".", StringComparison.Ordinal)) name = name.Substring(1);

            if (sheet == FlashStyleSheet.Hud)
            {
                switch (name)
                {
                    case "r": hex = HudR; return true;
                    case "r0": hex = HudR0; return true;
                    case "r1": hex = HudR1; return true;
                    case "r2": hex = HudR2; return true;
                    case "r3": hex = HudR3; return true;
                    case "r4": hex = HudR4; return true;
                    case "r5": hex = HudR5; return true;
                    case "r6": hex = HudR6; return true;
                    case "r7": hex = HudR7; return true;
                    case "r8": hex = HudR8; return true;
                    case "r9": hex = HudR9; return true;
                    case "warn": hex = HudWarn; return true;
                    case "crim": hex = HudCrim; return true;
                    case "yel": hex = HudYel; bold = true; return true;

                    // Unregistered in the Oracle; see Imp's remarks.
                    case "imp": hex = Imp; return true;

                    default: return false;
                }
            }

            switch (name)
            {
                case "r0": hex = PageR0; return true;
                case "red": hex = PageRed; return true;
                case "yel": hex = PageYel; return true;
                case "or": hex = PageOr; return true;
                case "pink": hex = PagePink; return true;
                case "blu": hex = PageBlu; return true;
                case "mass": hex = PageMass; return true;
                case "dark": hex = PageDark; return true;
                case "light": hex = PageLight; return true;
                case "green": hex = PageGreen; return true;
                case "purp": hex = PagePurp; return true;

                case "imp": hex = Imp; return true;

                default: return false;
            }
        }
    }

    /// <summary>
    /// Translates the Oracle's Flash HTML into Unity rich text.
    ///
    /// <para><b>What the Oracle actually does</b> (<c>GUI.as:1818-1829</c>), in order:</para>
    /// <list type="number">
    /// <item>For <c>i</c> in 1..5, when the dialogue node carries an <c>s{i}</c> attribute, replace
    /// <c>@i</c> with <c>&lt;span class='imp'&gt;{current keybinding}&lt;/span&gt;</c>. These are
    /// <b>keybind placeholders, not choice slots</b> — there are no choice buttons in the AS3 layer.</item>
    /// <item>Replace every <c>[</c> with <c>&lt;span class='yel'&gt;</c> and every <c>]</c> with
    /// <c>&lt;/span&gt;</c>.</item>
    /// <item>Strip backspace, CR and tab (newline survives).</item>
    /// </list>
    /// <para>Flash then renders the <c>&lt;span&gt;</c> runs through the active style sheet. Unity has no
    /// style sheets, so this class resolves each class to a colour tag up front.</para>
    ///
    /// <para><b>An unknown class is consumed, not passed through.</b> The Oracle has a genuine bug where
    /// two <c>celObj</c> branches assign rich-text markup to <c>.text</c> instead of <c>.htmlText</c>
    /// (<c>GUI.as:1278,1300</c>), leaking the literal characters <c>&lt;span class='r'&gt;</c> onto the
    /// screen. Porting that faithfully would put raw markup in front of the player, so this formatter
    /// always consumes a recognised span tag and reports classes it did not recognise through
    /// <c>unrecognisedClasses</c> rather than dropping them silently.</para>
    /// </summary>
    public static class PfeTextFormatter
    {
        /// <summary>
        /// Formats Oracle text for Unity, with no keybind substitution.
        /// </summary>
        public static string Format(string flashText, FlashStyleSheet sheet)
            => Format(flashText, sheet, null, null);

        /// <summary>
        /// Formats Oracle text for Unity.
        /// </summary>
        /// <param name="flashText">Raw Oracle string, possibly containing <c>&lt;span&gt;</c>, <c>[]</c> and <c>@1..@5</c>.</param>
        /// <param name="sheet">Which style sheet the string is written against.</param>
        /// <param name="keyLookup">
        /// Returns the current keybinding for slot <paramref name="n"/> (1..5), or null to leave the
        /// <c>@n</c> token alone. A null lookup means "no substitution", which is what a HUD string with
        /// no dialogue context needs.
        /// </param>
        /// <param name="unrecognisedClasses">
        /// Optional sink for style class names this formatter does not know. Reported rather than
        /// dropped: an unrecognised class means either a new Oracle style or a typo, and both are worth
        /// seeing once.
        /// </param>
        public static string Format(
            string flashText,
            FlashStyleSheet sheet,
            Func<int, string> keyLookup,
            ICollection<string> unrecognisedClasses)
        {
            if (string.IsNullOrEmpty(flashText)) return string.Empty;

            string text = flashText;

            // Step 1 — @1..@5 keybind placeholders. Only substituted when a lookup is supplied AND
            // returns something: the Oracle gates on `xml.attribute("s"+i).length()`.
            if (keyLookup != null)
            {
                for (int i = 1; i <= 5; i++)
                {
                    string key = keyLookup(i);
                    if (string.IsNullOrEmpty(key)) continue;
                    text = text.Replace("@" + i, "<span class='imp'>" + key + "</span>");
                }
            }

            // Step 2 — brackets become the bold yellow keyword style.
            text = text.Replace("[", "<span class='yel'>").Replace("]", "</span>");

            // Step 3 — strip backspace, CR and tab. Newline is preserved: pages rely on it.
            text = Strip(text, '\b', '\r', '\t');

            // Step 4 — resolve every span to a colour tag.
            return ConvertSpans(text, sheet, unrecognisedClasses);
        }

        /// <summary>
        /// Converts <c>&lt;span class='x'&gt;…&lt;/span&gt;</c> runs to Unity colour tags, using a stack
        /// so nesting closes in the right order.
        ///
        /// <para>The Oracle writes the class attribute three different ways — <c>class='r'</c>,
        /// <c>class = 'yel'</c> (with spaces, from <c>PipPage.yel</c>) and <c>class="red"</c> — so the
        /// attribute is matched with optional whitespace and either quote character.</para>
        /// </summary>
        private static string ConvertSpans(string text, FlashStyleSheet sheet, ICollection<string> unrecognisedClasses)
        {
            var sb = new StringBuilder(text.Length + 32);
            var open = new Stack<(string Hex, bool Bold)>();

            int i = 0;
            while (i < text.Length)
            {
                int lt = text.IndexOf('<', i);

                if (lt < 0)
                {
                    sb.Append(text, i, text.Length - i);
                    break;
                }

                sb.Append(text, i, lt - i);

                if (MatchesCloseSpan(text, lt, out int closeLength))
                {
                    // A stray close with nothing open is dropped rather than emitted, so an unbalanced
                    // Oracle string cannot inject a dangling </color> into the middle of the text.
                    if (open.Count > 0) sb.Append(CloseTag(open.Pop()));
                    i = lt + closeLength;
                    continue;
                }

                if (TryMatchOpenSpan(text, lt, out string className, out int openLength))
                {
                    if (FlashTextStyle.TryResolve(sheet, className, out string hex, out bool bold))
                    {
                        var tag = (hex, bold);
                        sb.Append(OpenTag(tag));
                        open.Push(tag);
                    }
                    else
                    {
                        // Consumed but unstyled: the run keeps the surrounding format, and the name is
                        // reported so a new Oracle style cannot go unnoticed.
                        unrecognisedClasses?.Add(className);
                        open.Push((null, false));
                    }

                    i = lt + openLength;
                    continue;
                }

                // Any other tag (or a bare '<') is passed through verbatim.
                sb.Append('<');
                i = lt + 1;
            }

            while (open.Count > 0) sb.Append(CloseTag(open.Pop()));

            return sb.ToString();
        }

        private static string OpenTag((string Hex, bool Bold) tag)
        {
            if (tag.Hex == null) return string.Empty;
            return tag.Bold ? $"<b><color={tag.Hex}>" : $"<color={tag.Hex}>";
        }

        private static string CloseTag((string Hex, bool Bold) tag)
        {
            if (tag.Hex == null) return string.Empty;
            return tag.Bold ? "</color></b>" : "</color>";
        }

        private static bool MatchesCloseSpan(string text, int index, out int length)
        {
            const string close = "</span>";
            length = close.Length;

            if (index + close.Length > text.Length) return false;
            return string.Compare(text, index, close, 0, close.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>
        /// Matches <c>&lt;span class = 'x'&gt;</c> anchored at <paramref name="index"/>, reporting the
        /// number of characters consumed through <paramref name="length"/>.
        ///
        /// <para>Written by hand rather than with a Regex so it allocates nothing per call and cannot be
        /// derailed by catastrophic backtracking on a malformed string.</para>
        ///
        /// <para><b><paramref name="length"/> is a length, not the end index.</b> The first version
        /// returned the cursor position, so the caller advanced <c>i</c> past the whole following run —
        /// every span silently ate the text after it, which is exactly the kind of failure a
        /// looks-plausible translation produces: the tags were all correct and the words were gone.</para>
        /// </summary>
        private static bool TryMatchOpenSpan(string text, int index, out string className, out int length)
        {
            className = null;
            length = 0;

            int start = index;

            if (!Consume(text, ref index, "<span")) return false;
            SkipWhitespace(text, ref index);
            if (!Consume(text, ref index, "class")) return false;
            SkipWhitespace(text, ref index);
            if (!Consume(text, ref index, "=")) return false;
            SkipWhitespace(text, ref index);

            if (index >= text.Length) return false;
            char quote = text[index];
            if (quote != '\'' && quote != '"') return false;
            index++;

            int nameStart = index;
            while (index < text.Length && text[index] != quote) index++;
            if (index >= text.Length) return false;

            className = text.Substring(nameStart, index - nameStart);
            index++; // closing quote
            SkipWhitespace(text, ref index);
            if (!Consume(text, ref index, ">")) return false;

            length = index - start;
            return true;
        }

        private static bool Consume(string text, ref int index, string token)
        {
            if (index + token.Length > text.Length) return false;
            if (string.Compare(text, index, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            index += token.Length;
            return true;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }

        private static string Strip(string text, params char[] remove)
        {
            int first = text.IndexOfAny(remove);
            if (first < 0) return text;

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                bool drop = false;
                foreach (char r in remove)
                {
                    if (c == r) { drop = true; break; }
                }
                if (!drop) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
