using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Weapons;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Tests for the port of <c>Unit.getXmlWeapon</c> (<c>Unit.as:1450-1475</c>) — the rule that decides
    /// which weapon an armed enemy spawns holding.
    ///
    /// <para><b>Why this fixture exists at all.</b> Neither <see cref="UnitWeaponSelector"/> nor
    /// <see cref="WeaponRowParser"/> had a single test, and the defect that shipped as a result was
    /// invisible: the importer dropped AS3's <c>f</c> attribute, so a unit's <i>secondary</i> weapon
    /// became an ordinary candidate — and because a secondary authors no <c>ch</c> it defaults to
    /// certainty and wins the roll on iteration 0. Every <c>merc1</c> therefore spawned holding
    /// <c>mercgr</c>, a 50-damage rocket, as its primary, with its four real candidates unreachable.
    /// Nothing went red, because nothing was looking.</para>
    ///
    /// <para>The two guards below are the ones that fail <b>silently</b> rather than loudly, which is
    /// exactly why they are asserted directly: <c>f='</c> is a substring of <c>dif='</c>, and the
    /// oracle's chance test treats an absent <c>ch</c> as certainty.</para>
    /// </summary>
    [TestFixture]
    public class WeaponRollTests
    {
        // ── WeaponRowParser.IsFixed ──────────────────────────────────────────────

        /// <summary>
        /// AS3's guard is <c>if(!n.@f.length())</c> — <b>presence</b> of <c>f</c>, not its value. The
        /// <c>dif='…'</c> case is the trap: an unanchored <c>f='</c> search matches it, which would mark
        /// every difficulty-gated row in the game as fixed (83 rows instead of 23 on the real data).
        /// </summary>
        [TestCase(" f='1'", true)]
        [TestCase("f='1'", true)]              // no leading space; `(?:^|\s)` must still match
        [TestCase(" f='0'", true)]             // presence, not value
        [TestCase("dif='18'", false)]          // THE TRAP
        [TestCase("dif='1' f='1'", true)]      // trap and real one side by side
        [TestCase("ch='0.2' dif='6'", false)]
        [TestCase("ch='0.5'", false)]
        [TestCase(" dif='16'", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsFixed_ReadsPresenceOfF_NotTheValue_AndNotTheTailOfDif(string attributes, bool expected)
        {
            Assert.AreEqual(expected, WeaponRowParser.IsFixed(attributes));
        }

        // ── UnitWeaponSelector.SelectIndex ───────────────────────────────────────

        /// <summary>
        /// The regression, in the shape of the real <c>merc1</c> row: a fixed <c>mercgr</c> first, then
        /// the four rollable candidates. With <c>f</c> honoured the roll must reach <c>revo</c>; with it
        /// dropped the first iteration returns <c>mercgr</c> and the loop never continues.
        /// </summary>
        [Test]
        public void SelectIndex_StepsOverAFixedRow_InsteadOfLettingItWinTheRoll()
        {
            List<WeaponOption> merc1 = new List<WeaponOption>
            {
                new WeaponOption("mercgr",  1f,    0,  isFixedWeapon: true),
                new WeaponOption("psc",     0.1f,  18),
                new WeaponOption("p127mm",  0.25f, 16),
                new WeaponOption("p308c",   0.5f,  0),
                new WeaponOption("revo",    1f,    0),
            };

            // A near-1 roll rejects every ch<1 row and accepts the first ch==1 row — which must be
            // `revo`, not the fixed `mercgr`.
            int picked = UnitWeaponSelector.SelectIndex(merc1, 0, () => 0.9999999f);
            Assert.AreEqual(4, picked);
            Assert.AreEqual("revo", merc1[picked].WeaponId);
            Assert.IsFalse(merc1[picked].IsFixedWeapon);
        }

        /// <summary>
        /// A unit whose every row is fixed rolls <b>nothing</b> — AS3 returns <c>null</c> and the unit
        /// falls to <c>attackerType = 0</c>. This is the shape of the four pure-<c>f</c> bosses, which
        /// hard-code their loadout in their own constructors instead.
        /// </summary>
        [Test]
        public void SelectIndex_ReturnsNoWeapon_WhenEveryRowIsFixed()
        {
            List<WeaponOption> bossraider = new List<WeaponOption>
            {
                new WeaponOption("flamer", 1f, 0, isFixedWeapon: true),
                new WeaponOption("assr",   1f, 0, isFixedWeapon: true),
            };

            Assert.AreEqual(UnitWeaponSelector.NoWeapon,
                UnitWeaponSelector.SelectIndex(bossraider, 0, () => 0f));
        }

        /// <summary>
        /// <c>n.@dif &gt; dif</c> is <b>strictly</b> greater, so a weapon whose <c>dif</c> equals the
        /// location's is eligible. Getting this off by one would hide every high-tier weapon.
        ///
        /// <para>Roll 0 accepts every <c>ch &gt; 0</c> row, so the pick is simply the first row that
        /// clears the gate. The rows are the real <c>merc1</c> ones: <c>psc</c> at <c>dif 18</c>,
        /// <c>p127mm</c> at <c>16</c>, <c>p308c</c> and <c>revo</c> at <c>0</c>.</para>
        /// </summary>
        [TestCase(0,  3, "p308c")]   // both dif-gated rows hidden
        [TestCase(6,  3, "p308c")]
        [TestCase(15, 3, "p308c")]   // dif 16 > 15 -> p127mm still hidden
        [TestCase(16, 2, "p127mm")]  // dif == 16 -> eligible, strictly-greater boundary
        [TestCase(17, 2, "p127mm")]
        [TestCase(18, 1, "psc")]     // dif == 18 -> eligible
        [TestCase(20, 1, "psc")]
        public void SelectIndex_AppliesTheDifficultyGate_StrictlyGreater(int difficulty, int expectedIndex, string expectedId)
        {
            List<WeaponOption> merc1 = new List<WeaponOption>
            {
                new WeaponOption("mercgr",  1f,    0,  isFixedWeapon: true),
                new WeaponOption("psc",     0.1f,  18),
                new WeaponOption("p127mm",  0.25f, 16),
                new WeaponOption("p308c",   0.5f,  0),
                new WeaponOption("revo",    1f,    0),
            };

            int picked = UnitWeaponSelector.SelectIndex(merc1, difficulty, () => 0f);
            Assert.AreEqual(expectedIndex, picked);
            Assert.AreEqual(expectedId, merc1[picked].WeaponId);
        }

        /// <summary>
        /// <c>n.@ch.length() == 0 || this.isrnd(n.@ch)</c> — an absent <c>ch</c> (imported as
        /// <c>chance == 1</c>) is unconditional, and a row is skipped when <c>roll &gt;= ch</c>, because
        /// <c>isrnd(p)</c> is <c>Math.random() &lt; p</c>.
        ///
        /// <para>At <c>dif 0</c> the only rows past the gate are <c>p308c</c> (<c>ch 0.5</c>) and
        /// <c>revo</c> (<c>ch 1</c>), so the roll decides between index 3 and index 4 — and
        /// <c>0.5</c> exactly is the boundary, where <c>&gt;=</c> skips.</para>
        /// </summary>
        [TestCase(0.49f,      3, "p308c")]  // 0.49 < 0.5 -> p308c taken
        [TestCase(0.5f,       4, "revo")]   // 0.5 >= 0.5 -> p308c skipped, falls through
        [TestCase(0.6f,       4, "revo")]
        [TestCase(0.9999999f, 4, "revo")]
        public void SelectIndex_AppliesTheChanceTest(float roll, int expectedIndex, string expectedId)
        {
            List<WeaponOption> merc1 = new List<WeaponOption>
            {
                new WeaponOption("mercgr",  1f,    0,  isFixedWeapon: true),
                new WeaponOption("psc",     0.1f,  18),
                new WeaponOption("p127mm",  0.25f, 16),
                new WeaponOption("p308c",   0.5f,  0),
                new WeaponOption("revo",    1f,    0),
            };

            int picked = UnitWeaponSelector.SelectIndex(merc1, 0, () => roll);
            Assert.AreEqual(expectedIndex, picked);
            Assert.AreEqual(expectedId, merc1[picked].WeaponId);
        }

        /// <summary>
        /// <c>Weapon.create</c> returns <c>null</c> for an id that is not in the weapon table, and the
        /// oracle then <b>continues</b> to the next candidate (<c>Unit.as:1470-1474</c>) rather than
        /// giving up. Dropping the <c>if(weap)</c> would leave such a unit holding nothing.
        /// </summary>
        [Test]
        public void SelectIndex_FallsThroughToTheNextRow_WhenAnIdDoesNotResolve()
        {
            List<WeaponOption> rows = new List<WeaponOption>
            {
                new WeaponOption("gone",  1f, 0),
                new WeaponOption("alsoGone", 1f, 0),
                new WeaponOption("revo",  1f, 0),
            };

            int picked = UnitWeaponSelector.SelectIndex(
                rows, 0, () => 0f, id => id == "revo");

            Assert.AreEqual(2, picked);
            Assert.AreEqual("revo", rows[picked].WeaponId);
        }

        /// <summary>An empty table is "no weapon", not an exception.</summary>
        [Test]
        public void SelectIndex_ReturnsNoWeapon_ForAnEmptyTable()
        {
            Assert.AreEqual(UnitWeaponSelector.NoWeapon,
                UnitWeaponSelector.SelectIndex(new List<WeaponOption>(), 0, () => 0f));
            Assert.AreEqual(UnitWeaponSelector.NoWeapon,
                UnitWeaponSelector.SelectIndex(null, 0, () => 0f));
        }

        // ── UnitWeaponSelector.AttackerType ──────────────────────────────────────

        /// <summary>
        /// The ladder at <c>UnitRaider.as:257-272</c>. The first arm is tested first, so a
        /// <c>tip &lt;= 1</c> weapon with <c>krep == 1</c> is a <b>contact</b> attacker and never swings
        /// the weapon it is holding — which is why a knife-armed raider body-slams.
        /// </summary>
        [TestCase(0, true,  false, 0)]   // no weapon -> contact, whatever the krep
        [TestCase(1, false, false, 0)]
        [TestCase(4, true,  false, 0)]
        [TestCase(0, true,  true,  0)]   // tip <= 1 && krep == 1 -> contact
        [TestCase(1, true,  true,  0)]
        [TestCase(1, false, true,  1)]   // tip == 1 && krep == 0 -> melee swing
        [TestCase(4, true,  true,  3)]   // tip == 4 -> thrown, regardless of krep
        [TestCase(4, false, true,  3)]
        [TestCase(0, false, true,  2)]   // tip 0 with krep 0 is NOT contact: 0 is not 1 or 4
        [TestCase(2, true,  true,  2)]
        [TestCase(3, true,  true,  2)]
        [TestCase(5, true,  true,  2)]
        public void AttackerType_FollowsTheOracleLadder(int tip, bool krep, bool hasWeapon, int expected)
        {
            Assert.AreEqual(expected, UnitWeaponSelector.AttackerType(tip, krep, hasWeapon));
        }

        // ── the data struct ─────────────────────────────────────────────────────

        /// <summary>
        /// <c>WeaponChance</c> is what the importer writes into every unit asset, so its positional
        /// constructor must keep working for the three-argument call and default <c>isFixedWeapon</c> to
        /// false — a unit asset that predates the field must not read as "all rows are fixed".
        /// </summary>
        [Test]
        public void WeaponChance_DefaultsToNotFixed()
        {
            WeaponChance legacy = new WeaponChance("revo", 1f, 0);
            Assert.IsFalse(legacy.isFixedWeapon);

            WeaponChance fixedRow = new WeaponChance("mercgr", 1f, 0, true);
            Assert.IsTrue(fixedRow.isFixedWeapon);
        }
    }
}
