using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>unit pool</b> armour model — the one every NPC uses and no production code could
    /// build before <see cref="UnitArmourMath"/>.
    ///
    /// <para>The pool's rules were already implemented and tested
    /// (<c>ArmourWear.PoolIntegrityDamage</c>, the resolver's reliability roll); what was missing was the
    /// <i>constructor</i>. So these cases are about the two things a constructor can get wrong: the
    /// capacity rule (which has two arms that scale differently) and the model flag (which decides
    /// whether the condition curve applies at all).</para>
    ///
    /// <para>Oracle: <c>Unit.as:1146-1165</c> (parse), <c>:3578-3628</c> (use).</para>
    /// </summary>
    [TestFixture]
    public class UnitArmourMathTests
    {
        private DamageCalculator _calculator;

        [SetUp]
        public void Setup()
        {
            _calculator = new DamageCalculator(new CombatCalculator());
        }

        /// <summary>Scripted roll answers, so "the reliability roll passed" needs no seed.</summary>
        private sealed class ScriptedRng : IRngService
        {
            private readonly Queue<bool> _chanceResults;

            public ScriptedRng(params bool[] chanceResults)
            {
                _chanceResults = new Queue<bool>(chanceResults);
            }

            public int ChanceCallCount { get; private set; }

            public bool Chance(float probability)
            {
                ChanceCallCount++;
                return _chanceResults.Count > 0 && _chanceResults.Dequeue();
            }

            public uint NextUInt() => 0u;
            public float NextFloat() => 0f;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public void Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }

        private static ScriptedRng AlwaysRolls() => new ScriptedRng(true, true, true, true);

        // === The gate — Unit.as:3578 `(armor > 0 || marmor > 0)` ===

        [Test]
        public void HasPool_WithAPhysicalRating_IsTrue()
        {
            Assert.IsTrue(UnitArmourMath.HasPool(armour: 5, magicArmour: 0));
        }

        [Test]
        public void HasPool_WithOnlyAnEnergyRating_IsTrue()
        {
            // `<comb hp='80' marmor='5' aqual='0.5' armorhp='120' .../>` — an energy-armoured unit still
            // wears a pool. Reading only `@armor` here would leave the whole `marmor` roster unarmoured.
            Assert.IsTrue(UnitArmourMath.HasPool(armour: 0, magicArmour: 5));
        }

        [Test]
        public void HasPool_WithNeitherRating_IsFalse_EvenWithAQuality()
        {
            // `<comb skill='0.8' aqual='0.5' damage='8'/>` — a quality with no rating to gate protects
            // nothing: the reduction block would add `armor` = 0. Building a pool here would produce a
            // durability bar that drains while absorbing nothing.
            Assert.IsFalse(UnitArmourMath.HasPool(armour: 0, magicArmour: 0));
        }

        // === The capacity rule — Unit.as:1158-1165, and the two arms scale differently ===

        [Test]
        public void PoolCapacity_AuthoredArmourHp_IsTakenVerbatim()
        {
            Assert.AreEqual(400f, UnitArmourMath.PoolCapacity(authoredArmourHp: 400, hpAfterDifficulty: 70f),
                1e-4f, "AS3: `armor_hp = @armorhp * hpmult` — the port has no hpmult, so the raw value.");
        }

        [Test]
        public void PoolCapacity_AuthoredArmourHp_IgnoresTheDifficultyCut()
        {
            // The parse order is the rule: `@armorhp` is read at :1158, AFTER the difficulty cut at
            // :1130-1141. So an explicit pool is NOT scaled down on easy, while the unit's health is.
            Assert.AreEqual(400f, UnitArmourMath.PoolCapacity(authoredArmourHp: 400, hpAfterDifficulty: 28f),
                1e-4f,
                "100 hp cut to 40 % is 40; the authored 400-pool must still be 400. Scaling it here " +
                "would make every armoured robot on easy lose 60 % of its armour.");
        }

        [Test]
        public void PoolCapacity_NoAuthoredArmourHp_FallsBackToThePostDifficultyHp()
        {
            // `else this.armor_hp = this.armor_maxhp = this.hp;` (:1164) — and `this.hp` has ALREADY been
            // cut by difficulty at :1140. The opposite of the authored arm, deliberately.
            Assert.AreEqual(28f, UnitArmourMath.PoolCapacity(authoredArmourHp: 0, hpAfterDifficulty: 28f),
                1e-4f,
                "The fallback reads the cut HP, so a pool-by-fallback DOES shrink on easy. This is the " +
                "asymmetry: one `if`, two different answers to 'does difficulty touch this'.");
        }

        // === Build — the projection the damage path reads ===

        [Test]
        public void Build_StartsFull_AndCarriesTheRatings()
        {
            ArmourState pool = UnitArmourMath.Build(
                armour: 15, magicArmour: 10, authoredArmourHp: 400, hpAfterDifficulty: 120f, quality: 0.8f);

            Assert.AreEqual(400f, pool.maxIntegrity, 1e-4f);
            Assert.AreEqual(400f, pool.integrity, 1e-4f, "AS3 assigns hp = maxhp in the same statement.");
            Assert.AreEqual(15f, pool.physicalRating, 1e-4f);
            Assert.AreEqual(10f, pool.energyRating, 1e-4f);
            Assert.AreEqual(0.8f, pool.reliability, 1e-4f);
            Assert.AreEqual(ArmourModel.UnitPool, pool.model);
            Assert.IsTrue(pool.IsEquipped);
        }

        [Test]
        public void Build_WithoutARating_IsNone()
        {
            ArmourState pool = UnitArmourMath.Build(
                armour: 0, magicArmour: 0, authoredArmourHp: 400, hpAfterDifficulty: 120f, quality: 0.8f);

            Assert.AreEqual(ArmourModel.None, pool.model);
            Assert.IsFalse(pool.IsEquipped,
                "A pool with no rating must not be built — the gate at :3578 refuses it in the oracle too.");
        }

        [Test]
        public void Build_WithZeroQuality_StillBuildsThePool()
        {
            // `armor_qual` gates the REDUCTION, not the pool. A unit with `armor='5'` and no `aqual`
            // wears its armour down over the fight and takes full damage throughout — odd, authored, and
            // exactly what a default of 1 would erase.
            ArmourState pool = UnitArmourMath.Build(
                armour: 5, magicArmour: 0, authoredArmourHp: 0, hpAfterDifficulty: 100f, quality: 0f);

            Assert.IsTrue(pool.IsEquipped);
            Assert.AreEqual(0f, pool.reliability, 1e-4f);
        }

        [Test]
        public void Build_WithNoCapacity_LeavesNothingToEquip()
        {
            ArmourState pool = UnitArmourMath.Build(
                armour: 5, magicArmour: 0, authoredArmourHp: 0, hpAfterDifficulty: 0f, quality: 1f);

            Assert.IsFalse(pool.IsEquipped, "A zero-capacity pool cannot be equipped — IsEquipped is maxIntegrity > 0.");
        }

        [Test]
        public void Build_DoesNotApplyTheBodyArmourPinkRule()
        {
            // `Armor.as:180-183` forces resist[pink] = -0.5 on every tip == 1 plate. That is a rule of the
            // Armor class, and a unit pool is a Unit — so pink must wear it ×0 (it is outside the pool's
            // type range at :3578) and never be amplified by an item rule.
            ArmourState pool = UnitArmourMath.Build(
                armour: 10, magicArmour: 0, authoredArmourHp: 200, hpAfterDifficulty: 100f, quality: 1f);

            Assert.AreEqual(0f, pool.resists.GetResist(DamageType.Pink), 1e-6f);
            Assert.AreEqual(0f,
                ArmourWear.PoolIntegrityDamage(DamageType.Pink, damage: 100f), 1e-4f,
                "Pink (19) is outside the pool's `param2 <= D_BALE` range, so it wears the pool not at all.");
        }

        // === The model flag is load-bearing: no condition curve on a pool ===
        //
        // This pair is the discriminating check. Each case alone passes under either rule; only the
        // DIFFERENCE between them proves the resolver reads `model` rather than always applying (or
        // never applying) the condition factor.

        [Test]
        public void DamagedPool_KeepsItsFullRating()
        {
            // A quarter-full pool. `setArmor()` is an `Armor` method and an NPC's pool is never
            // re-projected, so AS3 gives this the FULL rating — the only states are intact and empty.
            var pool = ArmourState.FromUnitPool(
                integrity: 25f, maxIntegrity: 100f, physicalRating: 20f, energyRating: 0f, reliability: 1f);

            var outcome = _calculator.ResolveDamage(
                incomingDamage: 100f,
                armourIntegrityDamage: 0f,
                armour: pool,
                rng: AlwaysRolls());

            Assert.AreEqual(80f, outcome.HpDamage, 1e-4f,
                "100 - 20. NOT 100 - 20 * (0.5 + 25/100) = 85 — the pool has no condition curve.");
            Assert.IsTrue(outcome.ArmourReduced);
        }

        [Test]
        public void DamagedItem_DegradesItsRating()
        {
            // The same numbers on the item model, where `setArmor()` DOES run: 0.5 + 25/100 = 0.75, so the
            // rating is 15 and the hit is 85. The control for the case above.
            var plate = ArmourState.FromItem(
                integrity: 25f, maxIntegrity: 100f, physicalRating: 20f, energyRating: 0f, reliability: 1f);

            var outcome = _calculator.ResolveDamage(
                incomingDamage: 100f,
                armourIntegrityDamage: 0f,
                armour: plate,
                rng: AlwaysRolls());

            Assert.AreEqual(85f, outcome.HpDamage, 1e-4f, "100 - 20 * 0.75");
            Assert.IsTrue(outcome.ArmourReduced);
        }

        [Test]
        public void PoolEmptiedByThisHit_GivesNoReduction()
        {
            // AS3 zeroes `armor_qual` inside the pool block (:3601) BEFORE the reduction block reads it,
            // so the breaking hit gets nothing. Same rule as the item's unequip, different mechanism.
            var pool = ArmourState.FromUnitPool(
                integrity: 25f, maxIntegrity: 100f, physicalRating: 20f, energyRating: 0f, reliability: 1f);

            var outcome = _calculator.ResolveDamage(
                incomingDamage: 100f,
                armourIntegrityDamage: 25f,
                armour: pool,
                rng: AlwaysRolls());

            Assert.AreEqual(100f, outcome.HpDamage, 1e-4f, "The hit that empties the pool gets no rating.");
            Assert.IsFalse(outcome.ArmourReduced);
            Assert.IsTrue(outcome.ArmourBroke);
        }

        [Test]
        public void PoolReliability_IsAProbability_NotARating()
        {
            // `isrnd(0.65)` is `Math.random() < 0.65` — a failed roll subtracts nothing, it does not
            // subtract 65 %. The most common misreading of `aqual`.
            var pool = ArmourState.FromUnitPool(
                integrity: 100f, maxIntegrity: 100f, physicalRating: 25f, energyRating: 0f, reliability: 0.65f);

            var failed = _calculator.ResolveDamage(
                incomingDamage: 100f, armourIntegrityDamage: 0f, armour: pool, rng: new ScriptedRng(false));
            var passed = _calculator.ResolveDamage(
                incomingDamage: 100f, armourIntegrityDamage: 0f, armour: pool, rng: new ScriptedRng(true));

            Assert.AreEqual(100f, failed.HpDamage, 1e-4f, "Roll failed: full damage.");
            Assert.AreEqual(75f, passed.HpDamage, 1e-4f, "Roll passed: the full 25 rating, not 25 * 0.65.");
        }

        [Test]
        public void ZeroReliability_ConsumesNoRoll()
        {
            // `if(armor_qual > 0 && isrnd(...))` short-circuits, so a quality of 0 draws nothing. Keeping
            // the stream position is a replication requirement, not an optimisation.
            var pool = ArmourState.FromUnitPool(
                integrity: 100f, maxIntegrity: 100f, physicalRating: 25f, energyRating: 0f, reliability: 0f);
            var rng = new ScriptedRng(true, true, true, true);

            _calculator.ResolveDamage(incomingDamage: 100f, armourIntegrityDamage: 0f, armour: pool, rng: rng);

            Assert.AreEqual(0, rng.ChanceCallCount, "A zero quality must not advance the combat RNG.");
        }
    }
}
