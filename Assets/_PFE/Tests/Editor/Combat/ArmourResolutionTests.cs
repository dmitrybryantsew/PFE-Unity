using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the armour resolution rules to their AS3 oracle.
    ///
    /// <para>Armour had <b>no tests at all</b> before this fixture, which is why four divergences
    /// survived unnoticed. Every case below cites the AS3 line it is pinning, so a future refactor
    /// that "simplifies" the rules fails here rather than in play.</para>
    ///
    /// <para>Oracle: <c>Unit.damage()</c> (<c>Unit.as:3505-3678</c>) and <c>Armor.setArmor()</c> /
    /// <c>Armor.damage()</c> (<c>Armor.as:297-355</c>).</para>
    /// </summary>
    [TestFixture]
    public class ArmourResolutionTests
    {
        private DamageCalculator _calculator;

        [SetUp]
        public void Setup()
        {
            _calculator = new DamageCalculator(new CombatCalculator());
        }

        // === Helpers ===

        /// <summary>
        /// An RNG whose <see cref="Chance"/> answers are scripted in order, so a test can say
        /// "the reliability roll passes, then the crit roll fails" without depending on a seed.
        /// </summary>
        private sealed class ScriptedRng : IRngService
        {
            private readonly Queue<bool> _chanceResults;

            public ScriptedRng(params bool[] chanceResults)
            {
                _chanceResults = new Queue<bool>(chanceResults);
            }

            /// <summary>How many <see cref="Chance"/> calls were made — proves a roll happened (or didn't).</summary>
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

        /// <summary>An RNG that never passes a roll — for the "no armour applied" cases.</summary>
        private static ScriptedRng NeverRolls() => new ScriptedRng();

        /// <summary>An RNG that passes every roll.</summary>
        private static ScriptedRng AlwaysRolls() => new ScriptedRng(true, true, true, true);

        private static ArmourState Armour(
            float integrity = 100f,
            float maxIntegrity = 100f,
            float physical = 20f,
            float energy = 10f,
            float reliability = 1f)
            => ArmourState.FromItem(integrity, maxIntegrity, physical, energy, reliability);

        // === Condition factor — Armor.setArmor() (Armor.as:302-306) ===

        [Test]
        public void ConditionFactor_AtFullIntegrity_IsOne()
        {
            var armour = Armour(integrity: 100f, maxIntegrity: 100f);

            Assert.AreEqual(1f, armour.ConditionFactor, 1e-6f,
                "AS3: `_loc1_ = 1` unless `hp < maxhp / 2`.");
        }

        [Test]
        public void ConditionFactor_AtExactlyHalfIntegrity_IsOne()
        {
            var armour = Armour(integrity: 50f, maxIntegrity: 100f);

            Assert.AreEqual(1f, armour.ConditionFactor, 1e-6f,
                "AS3's guard is `hp < maxhp / 2` — strictly less. At exactly half, the factor is " +
                "still 1, which is what makes the curve continuous.");
        }

        [Test]
        public void ConditionFactor_AtQuarterIntegrity_IsThreeQuarters()
        {
            var armour = Armour(integrity: 25f, maxIntegrity: 100f);

            // 0.5 + 25/100 = 0.75
            Assert.AreEqual(0.75f, armour.ConditionFactor, 1e-6f,
                "AS3: `_loc1_ = 0.5 + hp / maxhp` below half integrity.");
        }

        [Test]
        public void ConditionFactor_AtZeroIntegrity_IsHalf_NotZero()
        {
            var armour = Armour(integrity: 0f, maxIntegrity: 100f);

            Assert.AreEqual(0.5f, armour.ConditionFactor, 1e-6f,
                "Armour never degrades to zero by condition — it degrades to HALF and then stops by " +
                "breaking. This is the single most misremembered part of the model.");
        }

        [Test]
        public void ConditionFactor_IsContinuousAtTheMidpoint()
        {
            var justAbove = Armour(integrity: 50.001f, maxIntegrity: 100f);
            var justBelow = Armour(integrity: 49.999f, maxIntegrity: 100f);

            Assert.AreEqual(1f, justAbove.ConditionFactor, 1e-4f);
            Assert.AreEqual(1f, justBelow.ConditionFactor, 1e-3f,
                "The two branches meet at the midpoint — no step discontinuity.");
        }

        [Test]
        public void ConditionFactor_WithNoArmour_IsOne()
        {
            Assert.AreEqual(1f, ArmourState.None.ConditionFactor, 1e-6f,
                "No armour means no degradation term at all.");
        }

        // === The projection scales all three ratings — Armor.setArmor() (Armor.as:307-309) ===

        [Test]
        public void Condition_ScalesAllThreeRatings()
        {
            var armour = Armour(integrity: 25f, maxIntegrity: 100f, physical: 40f, energy: 20f, reliability: 0.8f);

            Assert.AreEqual(30f, armour.EffectivePhysicalRating, 1e-4f, "40 * 0.75");
            Assert.AreEqual(15f, armour.EffectiveEnergyRating, 1e-4f, "20 * 0.75");
            Assert.AreEqual(0.6f, armour.EffectiveReliability, 1e-4f,
                "AS3 scales armor_qual by the same factor — a battered plate also applies less often.");
        }

        // === The resolution chain ===

        [Test]
        public void NoArmour_DamagePassesThroughUnchanged()
        {
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 0f,
                armour: ArmourState.None,
                rng: AlwaysRolls());

            Assert.AreEqual(50f, outcome.HpDamage, 1e-4f);
            Assert.IsFalse(outcome.ArmourReduced);
            Assert.IsFalse(outcome.ArmourBroke);
            Assert.AreEqual(0f, outcome.ArmourIntegrityDamage, 1e-4f);
        }

        [Test]
        public void ReliabilityRollFails_NoRatingIsSubtracted()
        {
            // AS3: `if (armor_qual > 0 && isrnd(armor_qual)) _loc8_ += armor;` — a failed roll adds nothing.
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 1f,
                armour: Armour(physical: 20f),
                rng: NeverRolls());

            Assert.AreEqual(50f, outcome.HpDamage, 1e-4f);
            Assert.IsFalse(outcome.ArmourReduced, "The roll failed, so the rating must not apply.");
        }

        [Test]
        public void ReliabilityRollPasses_RatingIsSubtracted()
        {
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 1f,
                armour: Armour(physical: 20f),
                rng: AlwaysRolls());

            Assert.AreEqual(30f, outcome.HpDamage, 1e-4f, "50 - 20");
            Assert.IsTrue(outcome.ArmourReduced);
        }

        [Test]
        public void EnergyDamage_ReadsTheEnergyRating()
        {
            // AS3 has two branches: physical types read `armor`, energy types read `marmor`.
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 1f,
                armour: Armour(physical: 20f, energy: 8f),
                rng: AlwaysRolls(),
                damageType: DamageType.Plasma);

            Assert.AreEqual(42f, outcome.HpDamage, 1e-4f, "50 - 8 (marmor), not 50 - 20 (armor)");
        }

        [Test]
        public void SkinResistance_AlwaysApplies_EvenWhenTheRollFails()
        {
            // AS3: `_loc8_ = skin;` runs before the roll, so skin is unconditional.
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 1f,
                armour: Armour(physical: 20f),
                rng: NeverRolls(),
                skinResistance: 5f);

            Assert.AreEqual(45f, outcome.HpDamage, 1e-4f);
            Assert.IsFalse(outcome.ArmourReduced, "Skin is not the armour rating — the flag stays false.");
        }

        [Test]
        public void Piercing_ReducesTheReduction()
        {
            // AS3: `_loc8_ -= param3.pier`
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 1f,
                armour: Armour(physical: 20f),
                rng: AlwaysRolls(),
                piercing: 8f);

            Assert.AreEqual(38f, outcome.HpDamage, 1e-4f, "50 - (20 - 8)");
        }

        [Test]
        public void ArmourMultiplier_ScalesTheReduction_NotTheDamage()
        {
            // AS3: `_loc8_ *= param3.armorMult` — higher means the armour is MORE effective.
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 1f,
                armour: Armour(physical: 20f),
                rng: AlwaysRolls(),
                armourMultiplier: 1.5f);

            Assert.AreEqual(20f, outcome.HpDamage, 1e-4f, "50 - (20 * 1.5)");
        }

        [Test]
        public void PiercingBeyondTheReduction_DoesNotBecomeABonus()
        {
            // AS3 guards with `if (_loc8_ > 0)`, so negative reductions never ADD damage.
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 1f,
                armour: Armour(physical: 20f),
                rng: AlwaysRolls(),
                piercing: 100f);

            Assert.AreEqual(50f, outcome.HpDamage, 1e-4f,
                "Over-piercing must not increase damage past the incoming amount.");
        }

        // === The breaking hit — both paths agree, by different routes ===

        [Test]
        public void TheHitThatBreaksTheArmour_GetsNoReduction()
        {
            // The unit pool zeroes `armor_qual` BEFORE the reduction reads it (Unit.as:3597-3601 then
            // :3613-3627), so the breaking hit is unmitigated. The equipped path lands in the same
            // place: Armor.damage() calls changeArmor("off") (Armor.as:334), which runs
            // Pers.setParameters() and zeroes gg.armor/gg.marmor (Pers.as:876-877) before
            // super.damage() reads them.
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 10f,
                armour: Armour(integrity: 5f, maxIntegrity: 100f, physical: 20f),
                rng: AlwaysRolls());

            Assert.IsTrue(outcome.ArmourBroke);
            Assert.IsFalse(outcome.ArmourReduced, "The hit that breaks the armour gets no reduction.");
            Assert.AreEqual(50f, outcome.HpDamage, 1e-4f);
        }

        [Test]
        public void ANonBreakingHit_StillGetsItsReduction()
        {
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 10f,
                armour: Armour(integrity: 50f, maxIntegrity: 100f, physical: 20f),
                rng: AlwaysRolls());

            Assert.IsFalse(outcome.ArmourBroke);
            Assert.IsTrue(outcome.ArmourReduced);
            Assert.AreEqual(32f, outcome.HpDamage, 1e-4f,
                "50 - 20 * 0.9 — the wear lands first, so the projection read by the reduction is " +
                "already the post-hit one (40/100 -> 0.5 + 0.4).");
        }

        [Test]
        public void TheConditionFactorIsPostHit_NotPreHit()
        {
            // The decisive case. Integrity sits *exactly* on the half-way line, so the pre-hit
            // projection would be 1.0 while the post-hit one is 0.9 — the two readings disagree by
            // 2 damage. AS3 wears the armour and refreshes the projection
            // (Armor.damage() -> setArmor(), reached from UnitPlayer.as:3297) *before* the reduction
            // reads it (super.damage(), :3324), so crossing the line degrades the very hit that
            // crossed it.
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 10f,
                armour: Armour(integrity: 50f, maxIntegrity: 100f, physical: 20f),
                rng: AlwaysRolls());

            Assert.AreEqual(32f, outcome.HpDamage, 1e-4f,
                "Pre-hit would give 30 (20 * 1.0); post-hit gives 32 (20 * 0.9).");
        }

        [Test]
        public void TheOrderingDoesNotBite_WhileTheArmourStaysAboveHalf()
        {
            // Above the half-way line both readings agree, so the ordering only matters near the
            // break. This is the guard against "the fix changed everything".
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 1f,
                armour: Armour(integrity: 100f, maxIntegrity: 100f, physical: 20f),
                rng: AlwaysRolls());

            Assert.AreEqual(30f, outcome.HpDamage, 1e-4f, "99/100 is still at or above half.");
        }

        [Test]
        public void AlreadyBrokenArmour_IsInert()
        {
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 10f,
                armour: Armour(integrity: 0f, maxIntegrity: 100f, physical: 20f),
                rng: AlwaysRolls());

            Assert.IsFalse(outcome.ArmourReduced);
            Assert.AreEqual(50f, outcome.HpDamage, 1e-4f);
            Assert.AreEqual(0f, outcome.ArmourIntegrityDamage, 1e-4f,
                "There is no integrity left to take.");
        }

        [Test]
        public void IntegrityDamage_IsClampedToWhatIsLeft()
        {
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 999f,
                armour: Armour(integrity: 30f, maxIntegrity: 100f, physical: 20f),
                rng: AlwaysRolls());

            Assert.AreEqual(30f, outcome.ArmourIntegrityDamage, 1e-4f,
                "Report the integrity actually removed, not the requested amount — the caller " +
                "writes this back to the item.");
            Assert.IsTrue(outcome.ArmourBroke);
        }

        // === Crit and durability ===

        [Test]
        public void Crit_MultipliesAfterArmour()
        {
            // AS3 applies crit to `param1` AFTER the reduction has been subtracted (:3652-3656).
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 1f,
                armour: Armour(physical: 20f),
                rng: AlwaysRolls(),
                critChance: 0.5f,
                critMultiplier: 2f);

            Assert.IsTrue(outcome.IsCritical);
            Assert.AreEqual(60f, outcome.HpDamage, 1e-4f, "(50 - 20) * 2");
        }

        [Test]
        public void DurabilityMultiplier_AppliesLast()
        {
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 1f,
                armour: Armour(physical: 20f),
                rng: AlwaysRolls(),
                durabilityMultiplier: 0.7f);

            Assert.AreEqual(21f, outcome.HpDamage, 1e-4f, "(50 - 20) * 0.7");
        }

        [Test]
        public void ZeroIncomingDamage_IsEmpty()
        {
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 0f,
                armourIntegrityDamage: 10f,
                armour: Armour(),
                rng: AlwaysRolls());

            Assert.IsTrue(outcome.IsEmpty);
        }

        [Test]
        public void FullAbsorption_IsReported()
        {
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 15f,
                armourIntegrityDamage: 5f,
                armour: Armour(physical: 20f),
                rng: AlwaysRolls());

            Assert.AreEqual(0f, outcome.HpDamage, 1e-4f);
            Assert.IsTrue(outcome.WasFullyAbsorbed);
        }

        // === The state machine ===

        [Test]
        public void TakeIntegrityDamage_ReturnsTrueOnlyOnTheBreakingHit()
        {
            var armour = Armour(integrity: 10f, maxIntegrity: 100f);

            Assert.IsFalse(armour.TakeIntegrityDamage(4f), "Still 6 left — not broken yet.");
            Assert.IsTrue(armour.TakeIntegrityDamage(6f), "This is the hit that empties it.");
            Assert.IsFalse(armour.TakeIntegrityDamage(1f), "Already broken — no second break event.");
        }

        [Test]
        public void Repair_RestoresAndReRaisesTheEffectiveRatings()
        {
            // AS3 Armor.repair() also calls setArmor(), so the projection recovers.
            var armour = Armour(integrity: 10f, maxIntegrity: 100f, physical: 40f);

            Assert.AreEqual(24f, armour.EffectivePhysicalRating, 1e-4f, "40 * (0.5 + 0.1)");

            armour.Repair(1000f);

            Assert.AreEqual(100f, armour.integrity, 1e-4f, "Repair clamps to max.");
            Assert.AreEqual(40f, armour.EffectivePhysicalRating, 1e-4f, "And the projection is back to full.");
        }

        [Test]
        public void Repair_DoesNothingWithoutArmour()
        {
            var armour = ArmourState.None;
            armour.Repair(50f);

            Assert.IsFalse(armour.IsEquipped);
        }

        // === UnitStats integration ===

        [Test]
        public void UnitStats_ApplyDamage_DepletesArmourBeforeHealth()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            stats.EquipArmour(Armour(integrity: 50f, maxIntegrity: 100f, physical: 20f));

            bool broke = stats.ApplyDamage(new DamageOutcome(
                hpDamage: 10f,
                armourIntegrityDamage: 5f));

            Assert.IsFalse(broke);
            Assert.AreEqual(45f, stats.armour.integrity, 1e-4f);
            Assert.AreEqual(90f, stats.CurrentHp.Value, 1e-4f);
        }

        [Test]
        public void UnitStats_BreakingTheArmour_UnequipsIt()
        {
            // AS3 Armor.damage() calls changeArmor("off") on break (Armor.as:334), and that path runs
            // Pers.setParameters(), which zeroes gg.armor / gg.marmor (Pers.as:876-877) — so the
            // projection is cleared there too, and the breaking hit gets no reduction from it.
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            stats.EquipArmour(Armour(integrity: 5f, maxIntegrity: 100f, physical: 20f));

            bool broke = stats.ApplyDamage(new DamageOutcome(
                hpDamage: 10f,
                armourIntegrityDamage: 5f));

            Assert.IsTrue(broke);
            Assert.IsFalse(stats.armour.IsEquipped,
                "A broken armour is unequipped, not left inert.");
            Assert.AreEqual(0f, stats.armour.EffectivePhysicalRating, 1e-4f);
            Assert.AreEqual(90f, stats.CurrentHp.Value, 1e-4f, "The health damage still lands.");
        }

        [Test]
        public void UnitStats_UnequipArmour_ClearsTheProjection()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            stats.EquipArmour(Armour(physical: 40f));
            Assert.AreEqual(40f, stats.armour.EffectivePhysicalRating, 1e-4f);

            stats.UnequipArmour();

            Assert.IsFalse(stats.armour.IsEquipped);
            Assert.AreEqual(0f, stats.armour.EffectivePhysicalRating, 1e-4f);
        }

        // === The reduction channel gate — Unit.as:3610-3628 ===

        [Test]
        public void PinkDamage_BypassesArmourAndSkin()
        {
            // Pink (19) matches neither of AS3's two branches, so `_loc8_` stays 0. Not just the
            // armour rating — the skin line sits inside the branches too, so skin is skipped as well.
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 5f,
                armour: Armour(physical: 20f),
                rng: AlwaysRolls(),
                damageType: DamageType.Pink,
                skinResistance: 9f);

            Assert.AreEqual(50f, outcome.HpDamage, 1e-4f,
                "Neither the 20 rating nor the 9 skin may apply to pink.");
            Assert.IsFalse(outcome.ArmourReduced);
        }

        [Test]
        public void PoisonDamage_BypassesArmourAndSkin()
        {
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 5f,
                armour: Armour(physical: 20f),
                rng: AlwaysRolls(),
                damageType: DamageType.Poison,
                skinResistance: 9f);

            Assert.AreEqual(50f, outcome.HpDamage, 1e-4f);
        }

        [Test]
        public void IgnoreArmour_SkipsTheWholeBlock()
        {
            // AS3's fourth parameter to Unit.damage(). DoT sources pass true
            // (Effect.as:417-438, UnitPlayer.as:1375) so a burning player is not saved by a plate.
            var outcome = _calculator.ResolveDamage(
                incomingDamage: 50f,
                armourIntegrityDamage: 5f,
                armour: Armour(physical: 20f),
                rng: AlwaysRolls(),
                damageType: DamageType.Fire,
                ignoreArmour: true,
                skinResistance: 9f);

            Assert.AreEqual(50f, outcome.HpDamage, 1e-4f);
            Assert.IsFalse(outcome.ArmourReduced);
        }

        [Test]
        public void PhysicalAndEnergyChannels_MatchTheAs3Branches()
        {
            foreach (var t in new[]
            {
                DamageType.PhysicalBullet, DamageType.Blade, DamageType.PhysicalMelee,
                DamageType.Explosive, DamageType.Fang, DamageType.Acid,
            })
                Assert.AreEqual(ArmourChannel.Physical, ArmourWear.ChannelFor(t), t.ToString());

            foreach (var t in new[]
            {
                DamageType.Fire, DamageType.Laser, DamageType.Plasma,
                DamageType.Spark, DamageType.Cryo, DamageType.Astral,
            })
                Assert.AreEqual(ArmourChannel.Energy, ArmourWear.ChannelFor(t), t.ToString());

            foreach (var t in new[]
            {
                DamageType.Venom, DamageType.Poison, DamageType.Bleed, DamageType.Necrotic,
                DamageType.Pink, DamageType.Balefire, DamageType.Psionic, DamageType.EMP,
                DamageType.Internal, DamageType.FriendlyFire,
            })
                Assert.AreEqual(ArmourChannel.None, ArmourWear.ChannelFor(t), t.ToString());
        }

        // === The wear table — Armor.damage() and Unit.damage() do not agree ===

        [Test]
        public void ItemWear_AcidIsDouble_AndPinkIsTriple()
        {
            Assert.AreEqual(20f, ArmourWear.ItemIntegrityDamage(DamageType.Acid, 10f), 1e-4f);
            Assert.AreEqual(30f, ArmourWear.ItemIntegrityDamage(DamageType.Pink, 10f), 1e-4f);
            Assert.AreEqual(10f, ArmourWear.ItemIntegrityDamage(DamageType.Fire, 10f), 1e-4f,
                "Only acid and pink are scaled on the item path.");
        }

        [Test]
        public void ItemWear_ResistAppliesBeforeTheTypeMultiplier()
        {
            // AS3 order: `param1 *= 1 - resist[type]` (Armor.as:321) then the pink x3 (:328).
            // So resist -0.5 gives 10 * 1.5 = 15, then * 3 = 45 — not 10 * 3 * 1.5 read the other way.
            Assert.AreEqual(45f,
                ArmourWear.ItemIntegrityDamage(DamageType.Pink, 10f, resist: -0.5f), 1e-4f);
        }

        [Test]
        public void ItemWear_SkipsTheFiveImmuneTypes()
        {
            // Armor.as:319 — the wear block is skipped entirely for these.
            foreach (var t in new[]
            {
                DamageType.Venom, DamageType.EMP, DamageType.Poison,
                DamageType.Bleed, DamageType.Internal,
            })
                Assert.AreEqual(0f, ArmourWear.ItemIntegrityDamage(t, 10f), 1e-4f, t.ToString());

            Assert.AreEqual(10f, ArmourWear.ItemIntegrityDamage(DamageType.Necrotic, 10f), 1e-4f,
                "Necrotic gets no reduction but still wears the plate.");
        }

        [Test]
        public void ItemWear_IndestructibleTakesNothing()
        {
            Assert.AreEqual(0f,
                ArmourWear.ItemIntegrityDamage(DamageType.PhysicalBullet, 10f, indestructible: true),
                1e-4f);
        }

        [Test]
        public void PoolWear_AcidIsQuadruple_AndExplosiveIsDouble()
        {
            Assert.AreEqual(40f, ArmourWear.PoolIntegrityDamage(DamageType.Acid, 10f), 1e-4f);
            Assert.AreEqual(20f, ArmourWear.PoolIntegrityDamage(DamageType.Explosive, 10f), 1e-4f);
            Assert.AreEqual(10f, ArmourWear.PoolIntegrityDamage(DamageType.Fire, 10f), 1e-4f);
        }

        [Test]
        public void PoolWear_DividesByTheArmourMultiplier()
        {
            // Unit.as:3585-3588 — a DIVISION, and only when the multiplier exceeds 1.
            Assert.AreEqual(5f,
                ArmourWear.PoolIntegrityDamage(DamageType.PhysicalBullet, 10f, armourMultiplier: 2f),
                1e-4f);
            Assert.AreEqual(10f,
                ArmourWear.PoolIntegrityDamage(DamageType.PhysicalBullet, 10f, armourMultiplier: 0.5f),
                1e-4f, "Below 1 the division does not run at all — the guard is `> 1`.");
        }

        [Test]
        public void PoolWear_ExcludesPinkButIncludesAstral()
        {
            // The AS3 condition is a numeric range, `type <= D_BALE ... || type == D_ASTRO`.
            // Pink is 19, past the range, and is not astral — so it does not wear the pool.
            // Astral is 18, also past the range, but named explicitly in the tail.
            Assert.AreEqual(0f, ArmourWear.PoolIntegrityDamage(DamageType.Pink, 10f), 1e-4f);
            Assert.AreEqual(10f, ArmourWear.PoolIntegrityDamage(DamageType.Astral, 10f), 1e-4f);
        }

        [Test]
        public void PoolWear_ExcludesEmpPoisonAndBleed()
        {
            foreach (var t in new[] { DamageType.EMP, DamageType.Poison, DamageType.Bleed })
                Assert.AreEqual(0f, ArmourWear.PoolIntegrityDamage(t, 10f), 1e-4f, t.ToString());
        }

        [Test]
        public void PoolWearGate_MatchesTheAs3NumericRange_ForEveryType()
        {
            // AS3 writes the pool gate as a numeric range —
            //   `param2 <= D_BALE && param2 != D_EMP && param2 != D_POISON && param2 != D_BLEED || param2 == D_ASTRO`
            // The port spells the members out so a reorder of DamageType cannot silently move the
            // boundary. This asserts the two spellings agree on all 21 values — otherwise the
            // "clearer" version is just a different rule.
            foreach (DamageType t in System.Enum.GetValues(typeof(DamageType)))
            {
                bool as3Range = (((int)t <= (int)DamageType.Balefire
                                  && t != DamageType.EMP
                                  && t != DamageType.Poison
                                  && t != DamageType.Bleed)
                                 || t == DamageType.Astral);

                Assert.AreEqual(as3Range, ArmourWear.WearsUnitPool(t),
                    $"The spelled-out gate and AS3's numeric range disagree for {t}.");
            }
        }

        [Test]
        public void PoolWear_SubtractsTheSpellShieldFirst()
        {
            // Unit.as:3581-3584 — `if (shithp > 0) _loc9_ -= shitArmor;` runs before the multipliers.
            Assert.AreEqual(10f,
                ArmourWear.PoolIntegrityDamage(DamageType.Explosive, 20f, spellShieldAbsorb: 15f),
                1e-4f, "(20 - 15) * 2");
        }
    }
}
