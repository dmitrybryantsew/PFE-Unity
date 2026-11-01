using NUnit.Framework;
using PFE.Sim.RPG;

namespace PFE.Tests.EditMode.Systems.RPG
{
    /// <summary>
    /// Pins <see cref="HealthRegenRules.Step"/> against AS3 <c>UnitPlayer.actions()</c>'s regen block,
    /// <c>UnitPlayer.as:963-967</c>:
    /// <code>
    /// if(this.pers.regenFew > 0 &amp;&amp; hp &lt; Math.min(maxhp * this.pers.regenMax, maxhp - this.rad) &amp;&amp; hp > 0)
    /// {
    ///    hp += this.pers.regenFew;
    /// }
    /// </code>
    ///
    /// <para><b>The bug being guarded.</b> <c>impl_regen</c> — the doctor's 15 000-cap regeneration
    /// implant — writes <c>regenFew 0.1</c> / <c>regenMax 0.25</c> into <c>CharacterStats</c> through the
    /// held-item <c>&lt;sk&gt;</c> path, and <b>nothing read either field</b>. The implant was
    /// purchasable, visible in the stat dump, and completely inert: no passive healing at all. A field
    /// with a writer and no reader is silent by construction, which is why the rule is a standalone
    /// Unity-free function with this fixture on it rather than three lines inside a ticker.</para>
    ///
    /// <para><b>Every number below comes from the oracle or from the implant's own row.</b>
    /// <c>regenFew</c> 0.1 and <c>regenMax</c> 0.25 are <c>impl_regen</c>'s two <c>&lt;sk&gt;</c> values
    /// (<c>AllData.as:4139-4141</c>).</para>
    /// </summary>
    public class HealthRegenRulesTests
    {
        /// <summary><c>impl_regen</c>'s <c>regenFew</c> — hp per frame (<c>AllData.as:4140</c>).</summary>
        private const float ImplantRegenFew = 0.1f;

        /// <summary><c>impl_regen</c>'s <c>regenMax</c> — the fraction of maxhp it tops up to
        /// (<c>AllData.as:4141</c>).</summary>
        private const float ImplantRegenMax = 0.25f;

        /// <summary>
        /// The gate that matters most: without the implant there is no passive healing. AS3 checks
        /// <c>regenFew &gt; 0</c> first, so a character with none skips the block entirely.
        /// </summary>
        [Test]
        public void NoImplantRegeneratesNothing()
        {
            Assert.AreEqual(40f, HealthRegenRules.Step(40f, 100f, 0f, 0f, 0f));
            Assert.AreEqual(40f, HealthRegenRules.Step(40f, 100f, 0f, 0f, ImplantRegenMax));
        }

        /// <summary>
        /// One frame below the cap adds exactly <c>regenFew</c>. The implant's own description calls
        /// that 3 hp per <i>second</i> (<c>text_en.xml</c>, <c>impl_regen</c>: "#1 units per second"
        /// with <c>s1='3'</c>) — 0.1 at 30 Hz, which is what makes the per-frame rate verifiable
        /// rather than assumed.
        /// </summary>
        [Test]
        public void OneFrameAddsRegenFew()
        {
            Assert.AreEqual(10.1f, HealthRegenRules.Step(10f, 100f, 0f, ImplantRegenFew, ImplantRegenMax), 1e-5f);

            // The rate the item promises: 0.1 per tick at AS3's 30 Hz is 3 hp/s.
            Assert.AreEqual(3f, ImplantRegenFew * 30f, 1e-5f);
        }

        /// <summary>
        /// <c>regenMax</c> caps the top-up at a quarter of maxhp. The comparison is strict
        /// (<c>hp &lt; cap</c>), so a player parked exactly at 25% does not creep upward.
        /// </summary>
        [Test]
        public void TheRegenMaxCapIsStrict()
        {
            Assert.AreEqual(25f, HealthRegenRules.Step(25f, 100f, 0f, ImplantRegenFew, ImplantRegenMax));
            Assert.AreEqual(30f, HealthRegenRules.Step(30f, 100f, 0f, ImplantRegenFew, ImplantRegenMax));
            Assert.AreEqual(24.95f + 0.1f,
                HealthRegenRules.Step(24.95f, 100f, 0f, ImplantRegenFew, ImplantRegenMax), 1e-5f);
        }

        /// <summary>
        /// The second half of the <c>min</c>: radiation lowers the ceiling, and the <b>lower</b> of the
        /// two caps wins. 90 rad on 100 maxhp leaves 10 usable hp, so the implant stops there — not at
        /// 25. Without this arm the implant would heal straight through radiation damage.
        /// </summary>
        [Test]
        public void RadiationCanLowerTheCapBelowRegenMax()
        {
            Assert.AreEqual(9.1f, HealthRegenRules.Step(9f, 100f, 90f, ImplantRegenFew, ImplantRegenMax), 1e-5f);
            Assert.AreEqual(10f, HealthRegenRules.Step(10f, 100f, 90f, ImplantRegenFew, ImplantRegenMax));
            Assert.AreEqual(15f, HealthRegenRules.Step(15f, 100f, 90f, ImplantRegenFew, ImplantRegenMax),
                "hp above the radiation ceiling is left alone, not pulled down.");
        }

        /// <summary>
        /// <c>hp &gt; 0</c> — a corpse does not regenerate. The oracle's hp goes negative on overkill,
        /// and regen is not what climbs back out of it (<c>heal</c> is).
        /// </summary>
        [Test]
        public void ADeadUnitDoesNotRegenerate()
        {
            Assert.AreEqual(0f, HealthRegenRules.Step(0f, 100f, 0f, ImplantRegenFew, ImplantRegenMax));
            Assert.AreEqual(-12f, HealthRegenRules.Step(-12f, 100f, 0f, ImplantRegenFew, ImplantRegenMax));
        }

        /// <summary>
        /// The result is <b>not</b> clamped to the cap: AS3 does a bare <c>hp += regenFew</c>. A mirror,
        /// not a floor — clamping would be a correction the oracle does not make, and it would hide a
        /// future <c>regenMax</c> above 1.
        /// </summary>
        [Test]
        public void TheResultMayOvershootTheCap()
        {
            Assert.AreEqual(25.09f, HealthRegenRules.Step(24.99f, 100f, 0f, ImplantRegenFew, ImplantRegenMax), 1e-5f);
        }

        /// <summary>
        /// <c>regenMax</c> of 0 with a positive <c>regenFew</c> cannot happen in the shipped data (the
        /// two rows are written together), but the expression's answer is "cap 0, so no healing" and the
        /// rule must not divide, throw or heal. Pinned because it is the shape a half-authored row
        /// would take.
        /// </summary>
        [Test]
        public void AZeroRegenMaxHealsNothing()
        {
            Assert.AreEqual(10f, HealthRegenRules.Step(10f, 100f, 0f, ImplantRegenFew, 0f));
        }

        /// <summary>
        /// Radiation past the whole health pool leaves a ceiling of zero (<c>HpCeiling</c> floors at 0),
        /// so the implant stops dead rather than healing into a pool that cannot hold it.
        /// </summary>
        [Test]
        public void RadiationBeyondMaxHpBlocksRegenEntirely()
        {
            Assert.AreEqual(5f, HealthRegenRules.Step(5f, 100f, 130f, ImplantRegenFew, ImplantRegenMax));
        }
    }
}
