using System.Globalization;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The mana readout's rule — AS3 <c>GUI.setMana</c>'s three branches
    /// (<c>…\pfe\scripts\fe\inter\GUI.as:987-998</c>):
    ///
    /// <code>
    /// if(gg.mana &lt; 10)      txtMagiaOver                       // "out of magic"
    /// else if(gg.mana &lt; 995) txtMagia + " " + round(mana/10) + "%"
    /// else                    ""                                 // a full pool shows NOTHING
    /// </code>
    ///
    /// <para><b>It is the <i>budget</i>, and it is a percentage — not "current / max".</b> The pool's
    /// ceiling is 1000 (<c>Unit.maxmana</c>), which is the only reason <c>mana / 10</c> is a percentage;
    /// the adjacent bar draws a <i>different</i> pool (the organ, <c>Pers.manaHP</c>). Both live in the
    /// same method, four lines apart, and a port that unified them would be wrong in a way that only
    /// shows up when the budget regenerates while the bar stays put.</para>
    ///
    /// <para><b>Pulled out of <see cref="HUDViewModel"/> so a fixture can run it.</b> The thresholds are
    /// the oracle's, including the 995 — not 990, and not "== max": a pool at 996..1000 is silent, and a
    /// spell on cooldown forces the percentage back on regardless of the pool (that term is not ported;
    /// see the ViewModel).</para>
    ///
    /// <para><b>This is the first paragraph of <c>setMana</c>, not the whole method.</b> After the three
    /// branches the Oracle appends up to five more lines to the same multi-line <c>TextField</c>:
    /// the held object's mass in kg (<c>:1000-1003</c>, <c>round(teleObj.massa * 50) + "kg"</c>), a
    /// "(Heavy)" tag when <c>dmana &lt; -5</c> (<c>:1004-1007</c>), the water percentage when
    /// <c>h2o &lt; 500</c> (<c>:1008-1022</c>), the stamina percentage when <c>stam &lt; 500</c> — or
    /// <c>&lt; 980</c> in test-battle mode — (<c>:1023-1037</c>), and the worn armour's own mana when it
    /// is below its maximum (<c>:1038-1045</c>); then it dims the whole field to alpha <c>0.6</c> while a
    /// spell is cooling down (<c>:1046-1053</c>). <b>None of that is ported, and none of it can be
    /// yet:</b> <see cref="PFE.Entities.Units.UnitStats"/> carries <c>Mana</c>/<c>MaxMana</c> and no
    /// stamina, water, <c>dmana</c>, telekinesis mass or armour-mana pool at all. The gap is recorded
    /// here rather than in a TODO because a readout that silently shows only "Magic 34%" is not a
    /// partial port of this method — it is a different readout.</para>
    ///
    /// <para><b>And the method is not called every frame.</b> <c>GUI.step</c> gates it
    /// (<c>GUI.as:1989</c>): <c>setMana</c> runs only while <c>h2o &lt; 500 || stam &lt; 500 ||
    /// mana &lt; 1000 || teleObj || t_culd &gt; 0 || currentArmor.mana &lt; maxmana || currentSpell.t_culd
    /// &gt; 0</c>, and otherwise blanks the field once. That gate is why a full pool at full water and
    /// stamina shows <i>nothing</i> — the emptiness is the Oracle's, not a missing update — and the port
    /// reproduces it by formatting every frame and returning <see cref="string.Empty"/> in the same
    /// condition.</para>
    /// </summary>
    public static class ManaReadout
    {
        /// <summary><c>mana &lt; 10</c> — the "out of magic" branch.</summary>
        public const float OutOfMagicBelow = 10f;

        /// <summary><c>mana &lt; 995</c> — the upper bound of the percentage branch.</summary>
        public const float SilentAtOrAbove = 995f;

        /// <summary>The budget's ceiling, <c>Unit.maxmana</c> — the divisor that makes <c>/10</c> a percentage.</summary>
        public const float MaxBudget = 1000f;

        /// <summary>
        /// Format the readout.
        /// </summary>
        /// <param name="mana">AS3 <c>gg.mana</c> — the magic budget, 0..1000.</param>
        /// <param name="label">AS3 <c>txtMagia</c> — <c>Res.guiText("magia")</c>, "Magic" in the shipped table.</param>
        /// <param name="outLabel">AS3 <c>txtMagiaOver</c> — <c>Res.guiText("magiaover")</c>.</param>
        public static string Format(float mana, string label, string outLabel)
        {
            if (mana < OutOfMagicBelow)
            {
                return outLabel ?? string.Empty;
            }

            if (mana < SilentAtOrAbove)
            {
                int percent = (int)System.Math.Round(mana / 10f, System.MidpointRounding.AwayFromZero);
                return (label ?? string.Empty) + " " + percent.ToString(CultureInfo.InvariantCulture) + "%";
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// The experience bar's fraction — AS3 <c>GUI.setXp</c> (<c>GUI.as:1057-1061</c>):
    ///
    /// <code>
    /// (pers.xpCur - pers.xpPrev) / (pers.xpNext - pers.xpPrev)
    /// </code>
    ///
    /// <para><b>Both ends are cumulative totals, not a "xp into this level" counter.</b>
    /// <c>xpPrev = xpProgress(level - 1)</c> and <c>xpNext = xpProgress(level)</c>
    /// (<c>Pers.as:579-580</c>), while <c>xpCur</c> is the lifetime total (<c>:967</c> accumulates it
    /// and never resets it). Subtracting is what turns the pair into progress through the current level,
    /// and it is why the bar does not jump when the level rises: at the instant of a level-up the total
    /// is exactly the new <c>xpPrev</c>, so the fraction restarts at 0.</para>
    /// </summary>
    public static class ExperienceReadout
    {
        /// <summary>
        /// The 0..1 fill. A degenerate range (a level whose thresholds are equal — the first level, where
        /// <c>xpProgress(0) == xpProgress(1) == 0</c> is impossible but a malformed curve could produce
        /// it) answers 0 rather than dividing by zero.
        /// </summary>
        public static float Fraction(int xpCur, int xpPrev, int xpNext)
        {
            int span = xpNext - xpPrev;
            if (span <= 0)
            {
                return 0f;
            }

            float fraction = (xpCur - xpPrev) / (float)span;
            if (fraction < 0f)
            {
                return 0f;
            }

            return fraction > 1f ? 1f : fraction;
        }
    }
}
