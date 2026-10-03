using NUnit.Framework;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="WeaponSpreadMath"/> — the muzzle-angle deviation and pellet fan of the base
    /// <c>Weapon.shoot</c> (<c>Weapon.as:1460</c>, <c>:1496</c>).
    ///
    /// <para><b>Why this fixture exists at all.</b> The only other caller is
    /// <c>RangedWeaponController</c>, whose constructor takes a <c>WeaponDefinition</c>
    /// <c>ScriptableObject</c> — so every test that drives it is unreachable outside the editor. The
    /// formula was extracted to a pure static precisely so it could be executed here: these tests run
    /// in the offline wall, the controller tests do not. The split is the point, not a side effect.</para>
    ///
    /// <para><b>Expectations are written as expressions, not decimal literals.</b> A hand-computed
    /// literal for something like <c>0.5 * (4/1.01) * 3.1415/180</c> is a second, silent copy of the
    /// formula — and a wrong literal makes the test agree with a wrong implementation.</para>
    /// </summary>
    [TestFixture]
    public class WeaponSpreadMathTests
    {
        private const float Tolerance = 1e-6f;

        /// <summary>One pellet, no wear, no skill, mid roll — the identity case the others scale from.</summary>
        private static float Baseline(float random01 = 1f) =>
            WeaponSpreadMath.Deviation(random01, deviation: 10f, breaking: 0f,
                                       skillConfidence: 1f, weaponSkillMultiplier: 1f, mazil: 0f);

        // ── The roll ──────────────────────────────────────────────────────────

        [Test]
        public void Deviation_AtTheMidRoll_IsExactlyZero()
        {
            // (r - 0.5) with r = 0.5. Every other term is finite, so the whole product is 0 — this is
            // what makes a pinned RNG of 0.5 able to neutralise deviation in a fixture.
            Assert.AreEqual(0f, WeaponSpreadMath.Deviation(0.5f, 10f, 0.5f, 0.6f, 1.45f, 2f), Tolerance,
                "A 0.5 roll must cancel the entire cone, however wide the other terms are.");
        }

        [Test]
        public void Deviation_IsSymmetricAboutTheMidRoll()
        {
            float low  = WeaponSpreadMath.Deviation(0.25f, 10f, 0f, 1f, 1f, 0f);
            float high = WeaponSpreadMath.Deviation(0.75f, 10f, 0f, 1f, 1f, 0f);

            Assert.AreEqual(-low, high, Tolerance, "0.25 and 0.75 are equidistant from the mid roll.");
        }

        // ── The skill terms ───────────────────────────────────────────────────

        [Test]
        public void Deviation_HigherWeaponSkill_TightensTheCone()
        {
            // _loc1_ sits in the DIVISOR, so more points in the skill means less spread.
            float unskilled = WeaponSpreadMath.Deviation(1f, 10f, 0f, 1f, 1f, 0f);
            float skilled   = WeaponSpreadMath.Deviation(1f, 10f, 0f, 1f, 2f, 0f);

            Assert.Less(skilled, unskilled,
                "The weapon-skill multiplier divides the spread; raising it must tighten the cone.");
        }

        [Test]
        public void Deviation_DivisorIsSkillPlusOneHundredth_NotSkillAlone()
        {
            // The oracle divides by `(_loc1_ + 0.01)`, not `_loc1_`. The guard is what keeps a shooter
            // with ZERO points in the skill finite instead of dividing by zero, and it is worth
            // pinning because "simplify away the +0.01" is an easy, silent edit.
            float onePoint = WeaponSpreadMath.Deviation(1f, 10f, 0f, 1f, 1f, 0f);
            float noPoints = WeaponSpreadMath.Deviation(1f, 10f, 0f, 1f, 0f, 0f);

            Assert.IsFalse(float.IsInfinity(noPoints) || float.IsNaN(noPoints),
                "With no points in the skill the cone must still be a finite number.");
            Assert.That(noPoints / onePoint, Is.EqualTo(101f).Within(1e-3f),
                "1.01 / 0.01 = 101 — the no-skill cone is exactly 101x the one-point cone.");
        }

        [Test]
        public void Deviation_SkillConfidenceBelowOne_WidensTheCone()
        {
            // skillConf comes from checkAvail: 1 when qualified, 0.8 at a one-tier deficit, 0.6 at
            // two. It also sits in the divisor, so an under-skilled shooter sprays.
            float qualified = WeaponSpreadMath.Deviation(1f, 10f, 0f, 1f, 1f, 0f);
            float deficit1  = WeaponSpreadMath.Deviation(1f, 10f, 0f, 0.8f, 1f, 0f);
            float deficit2  = WeaponSpreadMath.Deviation(1f, 10f, 0f, 0.6f, 1f, 0f);

            Assert.Greater(deficit1, qualified, "skillConf 0.8 divides by less, so the cone widens.");
            Assert.Greater(deficit2, deficit1, "skillConf 0.6 widens it further still.");
        }

        // ── Wear ──────────────────────────────────────────────────────────────

        [Test]
        public void Deviation_WearTriplesTheConeAtFullBreaking()
        {
            // `(1 + breaking*2)`: a weapon at half durability (breaking 0.5) is 2x, and one about to
            // break (1.0) is 3x — measured against the same shot from a pristine weapon.
            float pristine = WeaponSpreadMath.Deviation(1f, 10f, 0f, 1f, 1f, 0f);
            float half     = WeaponSpreadMath.Deviation(1f, 10f, 0.5f, 1f, 1f, 0f);
            float full     = WeaponSpreadMath.Deviation(1f, 10f, 1f, 1f, 1f, 0f);

            Assert.That(half / pristine, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(full / pristine, Is.EqualTo(3f).Within(1e-4f));
        }

        // ── mazil ─────────────────────────────────────────────────────────────

        [Test]
        public void Deviation_MazilSitsInsideTheRoll_SoTheMidRollCancelsIt()
        {
            // The oracle is `(r - 0.5) * (… / (_loc1_+0.01) + mazil) * 3.1415/180` — mazil is added to
            // the QUOTIENT (after the divisions) but still INSIDE the (r - 0.5) factor. So it is not a
            // flat angular offset: at the mid roll it contributes nothing at all. That distinction is
            // the difference between a miss-radius addend and a constant aim error, and it is easy to
            // get wrong by "tidying" mazil outside the parentheses.
            Assert.AreEqual(0f, WeaponSpreadMath.Deviation(0.5f, 10f, 0f, 1f, 1f, mazil: 5f), Tolerance,
                "Inside the roll, mazil cannot move a mid-roll shot.");

            float without = WeaponSpreadMath.Deviation(1f, 10f, 0f, 1f, 1f, 0f);
            float with    = WeaponSpreadMath.Deviation(1f, 10f, 0f, 1f, 1f, 5f);

            Assert.That(with - without, Is.EqualTo(0.5f * 5f * (3.1415f / 180f)).Within(Tolerance),
                "mazil is scaled by (r - 0.5) like every other term in the parentheses.");
        }

        // ── The constant ──────────────────────────────────────────────────────

        [Test]
        public void OraclePi_IsTheOraclesRoundedConstant_NotTheTranscendentalOne()
        {
            // Weapon.as hard-codes 3.1415 in both terms. The port keeps it. This test exists so that
            // "clean this up to Mathf.PI" is a failing change rather than a silent one — the
            // difference is ~1e-5 relative and invisible in play, which is exactly why nothing else
            // would ever catch it.
            Assert.AreEqual(3.1415f, WeaponSpreadMath.OraclePi);
            Assert.AreNotEqual((float)System.Math.PI, WeaponSpreadMath.OraclePi,
                "If this ever passes, someone substituted the true pi for the oracle's constant.");
            Assert.That(WeaponSpreadMath.OraclePi, Is.EqualTo((float)System.Math.PI).Within(1e-4f),
                "...but it is still pi to four places — a different constant here is a typo, not a port.");
        }

        // ── The pellet fan ────────────────────────────────────────────────────

        [Test]
        public void PelletSpread_PutsTheMiddlePelletOfAnOddBurstOnTheAimAxis()
        {
            Assert.AreEqual(0f, WeaponSpreadMath.PelletSpread(pelletIndex: 2, pelletCount: 5, deviation: 6f), Tolerance);
            Assert.AreEqual(0f, WeaponSpreadMath.PelletSpread(pelletIndex: 1, pelletCount: 3, deviation: 6f), Tolerance);
        }

        [Test]
        public void PelletSpread_IsZeroForASinglePellet()
        {
            // pelletCount 1 gives `0 - 0/2` = 0. The controller used to guard this with an explicit
            // `pellets > 1 ?` — the guard is redundant, and this test is what lets it stay removed.
            Assert.AreEqual(0f, WeaponSpreadMath.PelletSpread(0, 1, 6f), Tolerance);
        }

        [Test]
        public void PelletSpread_FansSymmetricallyAboutTheCentre()
        {
            // The oracle's coefficient, now pinned: `deviation * 3.1415 / 360` per step.
            const int pellets = 5;
            const float deviation = 6f;
            float step = deviation * (3.1415f / 360f);

            for (int i = 0; i < pellets; i++)
            {
                float expected = (i - (pellets - 1) / 2f) * step;
                Assert.That(WeaponSpreadMath.PelletSpread(i, pellets, deviation),
                    Is.EqualTo(expected).Within(Tolerance), $"pellet {i}");
            }

            // ...and the mirror property, stated independently of the coefficient: pellet i and its
            // opposite sit at equal and opposite offsets.
            for (int i = 0; i < pellets / 2; i++)
            {
                Assert.AreEqual(-WeaponSpreadMath.PelletSpread(i, pellets, deviation),
                                 WeaponSpreadMath.PelletSpread(pellets - 1 - i, pellets, deviation),
                                 Tolerance, $"pellets {i} and {pellets - 1 - i} must mirror.");
            }
        }

        [Test]
        public void PelletSpread_UsesTheRawDeviation_SoWearDoesNotWidenTheFan()
        {
            // The `(1 + breaking*2)` term belongs to Deviation() alone; the fan takes the weapon's
            // authored `deviation`. There is no breaking parameter to pass, which is the point — but
            // assert the consequence, so a future "helpfully" wear-scaled fan shows up here.
            float oneStep = WeaponSpreadMath.PelletSpread(1, 3, 6f) - WeaponSpreadMath.PelletSpread(0, 3, 6f);
            Assert.That(oneStep, Is.EqualTo(6f * (3.1415f / 360f)).Within(Tolerance),
                "One step is the raw deviation's coefficient, with no wear factor in it.");
        }

        [Test]
        public void PelletSpread_GrowsWithTheWeaponsOwnDeviation()
        {
            // Compared by MAGNITUDE. Pellet 0 of an odd burst sits to the LEFT of the aim axis, so a
            // wider fan makes its signed offset more negative — `Greater(rawWide, rawNarrow)` fails on
            // a correct implementation, which is how this test was first written.
            float narrow = System.Math.Abs(WeaponSpreadMath.PelletSpread(0, 3, 6f));
            float wide   = System.Math.Abs(WeaponSpreadMath.PelletSpread(0, 3, 12f));

            Assert.Greater(wide, narrow, "Doubling the weapon's deviation must double how far pellet 0 sits off-axis.");
            Assert.That(wide, Is.EqualTo(narrow * 2f).Within(Tolerance), "The fan is linear in the deviation.");

            Assert.AreEqual(0f, WeaponSpreadMath.PelletSpread(0, 3, 0f), Tolerance,
                "A weapon with zero deviation has no fan at all — the case "
                + "Shot_WithZeroDeviation_EmitsPelletsOnTheAimAxis relies on.");
        }
    }
}
