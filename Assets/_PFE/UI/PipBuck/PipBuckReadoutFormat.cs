using UnityEngine;
using PFE.UI.Common;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The PipBuck's number formatting: the port of <c>PipBuck.med</c>, <c>ritem1</c>, <c>ritem2</c>
    /// and <c>ritem3</c> (<c>PipBuck.as:547-597</c>), plus the caps row.
    ///
    /// <para><b>Pure, and deliberately so.</b> No <c>MonoBehaviour</c>, no <c>RectTransform</c>, no
    /// asset references — everything here is a function of its arguments. That is what lets the whole
    /// thing be executed offline against the shipped assembly, which is the only way these rules get
    /// tested at all in a project where the editor is owner-only.</para>
    ///
    /// <para><b>Output is Unity rich text, not Flash spans.</b> The Oracle writes
    /// <c>&lt;span class='red'&gt;</c> against a <c>StyleSheet</c>. The port renders through legacy
    /// <c>UnityEngine.UI.Text</c>, whose rich-text subset understands
    /// <c>&lt;color=#RRGGBB&gt;</c> but not CSS classes. So the class name is resolved to a hex colour
    /// here, using the same <see cref="PfeUiColors"/> the rest of the interface uses. The colour
    /// <i>decision</i> (<see cref="MedClass"/>) is kept separate from the <i>rendering</i> of it, so
    /// the Oracle's rule stays assertable on its own terms.</para>
    /// </summary>
    public static class PipBuckReadoutFormat
    {
        /// <summary>
        /// AS3's <c>Math.round</c> — round half <b>toward +Infinity</b>.
        ///
        /// <para><b>Not <c>Mathf.Round</c>.</b> Unity's <c>Mathf.Round</c> uses banker's rounding
        /// (round-half-to-even): <c>Mathf.Round(0.5f) == 0</c> and <c>Mathf.Round(2.5f) == 2</c>.
        /// AS3 rounds half up: <c>Math.round(0.5) == 1</c>, <c>Math.round(2.5) == 3</c>. Every value on
        /// the status panel goes through this, so the divergence would show as an off-by-one on exactly
        /// the half-integer readings — the ones a player is most likely to be watching. It is a
        /// one-line difference that no compiler and no smoke test would ever catch.</para>
        /// </summary>
        public static int As3Round(float value)
        {
            // Not Mathf.FloorToInt: that truncates toward zero for negatives after the offset.
            return Mathf.FloorToInt(value + 0.5f);
        }

        /// <summary>
        /// The port of <c>Res.numb()</c> (<c>Res.as:348-360</c>) — the character sheet's number
        /// formatter: <b>at most one decimal</b>, and a trailing <c>.0</c> dropped.
        ///
        /// <para><b>It is not a rounding of the value.</b> The Oracle scales by ten, rounds
        /// <i>that</i>, and then splits the integer and the tenth: <c>50.04</c> renders <c>"50"</c>
        /// (500 % 10 == 0) while <c>50.05</c> renders <c>"50.1"</c> (<c>Math.round(500.5) == 501</c>).
        /// Rendering it as <c>value.ToString("0.#")</c> would agree on almost every input and differ
        /// on exactly the half-tenths — the same class of trap <see cref="As3Round"/> exists for.</para>
        ///
        /// <para><b>The negative branch is the Oracle's, including its oddity.</b> AS3 writes
        /// <c>Math.ceil(t / 10) + "." + Math.abs(t % 10)</c>, and <c>Math.ceil(-0.5)</c> is
        /// <c>-0</c>, which prints <c>"0"</c> — so <c>numb(-0.05)</c> is <c>"0.5"</c>, not
        /// <c>"-0.5"</c>. C# integer division truncates toward zero, which is exactly AS3's
        /// <c>floor</c> for positives and <c>ceil</c> for negatives, so <c>t / 10</c> reproduces both
        /// branches without a sign test. Ported as-is rather than corrected: this formatter is the
        /// game's, and a reader comparing a screenshot to the port is comparing numbers.</para>
        /// </summary>
        public static string Numb(float value)
        {
            int scaled = As3Round(value * 10f);
            if (scaled % 10 == 0) return (scaled / 10).ToString();

            // `t / 10` == Math.floor(t/10) for t >= 0 and Math.ceil(t/10) for t < 0, which is
            // precisely the pair of branches AS3 writes out separately. `System.Math` is spelled out
            // because this file imports `UnityEngine` and `PFE.UI.Common` only — adding `using System;`
            // for one call would put `Random` and `Object` in scope alongside Unity's.
            return (scaled / 10) + "." + System.Math.Abs(scaled % 10);
        }

        /// <summary>
        /// The port of <c>PipBuck.med()</c> (<c>PipBuck.as:586-597</c>) — returns the style-sheet
        /// <b>class name</b>, not a colour: <c>"red"</c> below 25 %, <c>"or"</c> below 50 %, else
        /// <c>""</c> (which means the sheet's default, the terminal green).
        ///
        /// <para>Kept returning a name rather than a <see cref="Color"/> because that is the Oracle's
        /// contract and it is the thing worth asserting. A caller that wants the colour calls
        /// <see cref="PfeUiColors.ForMonitorFraction"/>, which is the same rule expressed in colour.</para>
        /// </summary>
        public static string MedClass(float value, float max)
        {
            if (value < max * PfeUiColors.MonitorCriticalFraction) return "red";
            if (value < max * PfeUiColors.MonitorLowFraction) return "or";
            return "";
        }

        /// <summary>
        /// The port of <c>ritem1</c> (<c>PipBuck.as:547-558</c>): <c>"&lt;coloured&gt;v&lt;/&gt; / max"</c>.
        /// Used by the HP, organ, pet, weapon and armour rows.
        /// </summary>
        public static string Value(float value, float max)
        {
            string colour = ClassToHex(MedClass(value, max));
            return Wrap(As3Round(value).ToString(), colour) + " / " + As3Round(max);
        }

        /// <summary>
        /// The port of <c>ritem2</c> (<c>PipBuck.as:560-571</c>): <c>"&lt;coloured&gt;N%&lt;/&gt;"</c>.
        /// Used by the weapon-condition and armour-condition rows, which report a percentage rather
        /// than a raw pair.
        /// </summary>
        public static string Percent(float value, float max)
        {
            string colour = ClassToHex(MedClass(value, max));

            // The Oracle divides without guarding: `value / max * 100` is NaN at max == 0, and AS3
            // renders that as the literal string "NaN%". Reproducing "NaN%" would be faithful and
            // useless, so the port shows "0%" instead. Recorded rather than silently "fixed": the two
            // behaviours differ, and this is the only place they can.
            if (max <= 0f)
            {
                return Wrap("0%", colour);
            }

            return Wrap(As3Round(value / max * 100f) + "%", colour);
        }

        /// <summary>
        /// The port of <c>ritem3</c> (<c>PipBuck.as:573-584</c>): the mass rows. Same shape as
        /// <see cref="Value"/>, but the threshold is different — the number turns red only when it
        /// <b>exceeds</b> the capacity, not when it is a low fraction of it. Carrying too much is the
        /// failure; carrying little is not.
        /// </summary>
        public static string Mass(float value, float max)
        {
            string colour = value > max ? ClassToHex("red") : ClassToHex("");
            return Wrap(As3Round(value).ToString(), colour) + " / " + As3Round(max);
        }

        /// <summary>
        /// The caps row, row 9 of the status panel (<c>PipBuck.as:539</c>): the amount in the
        /// <c>yel</c> class with no denominator. Note it is <b>not</b> a <c>ritem*</c> call — it is
        /// written inline, which is why it has no threshold colouring.
        /// </summary>
        public static string Caps(int money)
        {
            return Wrap(money.ToString(), ClassToHex("yel"));
        }

        /// <summary>
        /// The port of the page-2/3/4/5 routing test in <c>PipPageInv.setSubPages</c>
        /// (<c>PipPageInv.as:244-253</c>). The Oracle derives a three-way kind from the item's XML
        /// and then matches it against the active sub-page:
        ///
        /// <list type="bullet">
        /// <item><description><c>tip == "a" || tip == "e"</c> → kind <b>2</b> (ammunition / magic)</description></item>
        /// <item><description>otherwise <c>@us &gt; 0</c> → kind <b>1</b> (usable: aid, food, books)</description></item>
        /// <item><description>otherwise → kind <b>0</b> (miscellaneous)</description></item>
        /// </list>
        ///
        /// <para>and the sub-page then selects one kind: <b>3</b> shows kind 1, <b>4</b> shows kind 0,
        /// <b>5</b> shows kind 2. Note the order — <c>"a"</c>/<c>"e"</c> is tested <i>before</i>
        /// <c>@us</c>, so an ammo type that happens to be usable still lands on the ammo page. Swapping
        /// those two branches moves items between tabs and would look like a data problem.</para>
        /// </summary>
        public static int ItemKind(bool isAmmoOrMagic, bool isUsable)
        {
            if (isAmmoOrMagic) return 2;
            if (isUsable) return 1;
            return 0;
        }

        /// <summary>Which <c>ItemKind</c> sub-page <paramref name="subPage"/> (3/4/5) displays.</summary>
        public static int ItemKindForSubPage(int subPage)
        {
            switch (subPage)
            {
                case 3: return 1;
                case 4: return 0;
                case 5: return 2;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(subPage), subPage,
                        "Only sub-pages 3 (aid), 4 (misc) and 5 (ammo) filter by item kind; " +
                        "1 and 2 list weapons and armour instead.");
            }
        }

        /// <summary>Resolves a style-sheet class name to a hex colour for Unity rich text.</summary>
        public static string ClassToHex(string className)
        {
            Color c;
            switch (className)
            {
                case "red": c = PfeUiColors.PageRed; break;
                case "or": c = PfeUiColors.PageOrange; break;
                case "yel": c = PfeUiColors.PageYellow; break;
                case "pink": c = PfeUiColors.PagePink; break;
                case "blu": c = PfeUiColors.PageBlue; break;
                case "mass": c = PfeUiColors.PageMass; break;
                case "dark": c = PfeUiColors.PageDark; break;
                case "light": c = PfeUiColors.PageLight; break;
                case "green": c = PfeUiColors.PageGreen; break;
                case "purp": c = PfeUiColors.PagePurple; break;

                // "" is the sheet's default. PipPage.setStyle only styles `.r0`; unstyled text falls
                // back to the TextField's own colour, which for every field it touches is the
                // terminal green. So "" and "r0" resolve to the same value, and that is faithful.
                case "":
                case "r0":
                default: c = PfeUiColors.PageR0; break;
            }

            return ToHex(c);
        }

        /// <summary>Wraps <paramref name="text"/> in a Unity rich-text colour tag.</summary>
        public static string Wrap(string text, string hex)
        {
            return "<color=#" + hex + ">" + text + "</color>";
        }

        /// <summary><c>#RRGGBB</c> for a colour. Alpha is dropped: every interface colour is opaque.</summary>
        public static string ToHex(Color color)
        {
            Color32 c = color;
            return c.r.ToString("X2") + c.g.ToString("X2") + c.b.ToString("X2");
        }
    }
}
