using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Particles;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins AS3's unit-hit impact dispatcher — <c>Unit.udarUnit</c>'s tail,
    /// <c>fe/unit/Unit.as:4168-4204</c>.
    ///
    /// <para><b>What this block is.</b> Every unit-vs-unit contact that lands damage ends in a five-way
    /// switch on the <i>attacker's</i> tip damage type, and each branch emits exactly one part and plays
    /// one sound. There is no gate and no probability, which is why the rules type returns a single emit
    /// rather than a list.</para>
    ///
    /// <para><b>Why the anchor needed its own fixture.</b> Unlike the blood block, this one is a function
    /// of <b>two</b> units. Four of the five branches anchor at the midpoint of the two mid-heights and
    /// carry a <c>scale</c>; <c>moln</c> anchors on the <b>defender</b> alone and carries the attacker's
    /// mid-height as <c>celx</c>/<c>cely</c> instead. A reader who assumes one shape gets the electric arc
    /// wrong in three ways at once — position, target and scale — and the result is a plausible-looking
    /// arc in the wrong place, which nothing would report.</para>
    /// </summary>
    [TestFixture]
    public class UnitHitImpactRulesTests
    {
        /// <summary>
        /// A defender at (100, 200) with a 40px sprite and an attacker at (160, 200) with a 60px one.
        /// Feet are at Y, so the mid-heights are 180 and 170 and the midpoint is (130, 175) — all four
        /// numbers distinct, so a transposition between them cannot pass unnoticed.
        /// </summary>
        private static UnitHitImpactContext Hit(DamageType tipDamage,
                                                float attackerDamage = 40f,
                                                float variance = 1f,
                                                float multiplier = 1f)
            => new UnitHitImpactContext(
                tipDamage: tipDamage,
                attackerDamage: attackerDamage,
                damageVariance: variance,
                damageMultiplier: multiplier,
                attackerX: 160f,
                attackerY: 200f,
                attackerSpriteHeight: 60f,
                defenderX: 100f,
                defenderY: 200f,
                defenderSpriteHeight: 40f);

        // === The dispatcher ===

        [Test]
        public void EveryBranchOfTheSwitchEmitsExactlyOnePart()
        {
            // The oracle's if/else-if chain has no fall-through hole: :4177 through :4204 always reach an
            // Emitter.emit. So a caller never has to ask whether a hit produced a visual.
            Assert.AreEqual(UnitHitImpactRules.SparkId,
                UnitHitImpactRules.Plan(Hit(DamageType.Spark)).Id);
            Assert.AreEqual(UnitHitImpactRules.AcidId,
                UnitHitImpactRules.Plan(Hit(DamageType.Acid)).Id);
            Assert.AreEqual(UnitHitImpactRules.NecroId,
                UnitHitImpactRules.Plan(Hit(DamageType.Necrotic)).Id);
            Assert.AreEqual(UnitHitImpactRules.FleshId,
                UnitHitImpactRules.Plan(Hit(DamageType.Fang)).Id);
            Assert.AreEqual(UnitHitImpactRules.FleshId,
                UnitHitImpactRules.Plan(Hit(DamageType.PhysicalBullet)).Id);
        }

        [Test]
        public void TheFourPartIdsAreTheOnesTheOracleNames()
        {
            // Spelled out rather than compared against the constants, so a rename that drifts from the
            // <part> rows in AllData.as is a red test. All four ids exist as rows (verified by census:
            // moln, buma, bumn, bum are all present among the 118).
            Assert.AreEqual("moln", UnitHitImpactRules.SparkId);
            Assert.AreEqual("buma", UnitHitImpactRules.AcidId);
            Assert.AreEqual("bumn", UnitHitImpactRules.NecroId);
            Assert.AreEqual("bum", UnitHitImpactRules.FleshId);
        }

        [Test]
        public void AnUnknownTipDamageFallsThroughToTheFleshBurst()
        {
            // AS3's final `else` (:4200-4204) catches everything — a bullet tip, a fire tip, a damage
            // type that is not in the chain at all. There is no "no visual" branch to reach.
            foreach (DamageType type in new[]
                     {
                         DamageType.PhysicalBullet, DamageType.Blade, DamageType.Fire,
                         DamageType.Explosive, DamageType.Plasma, DamageType.Bleed,
                     })
            {
                Assert.AreEqual(UnitHitImpactRules.FleshId, UnitHitImpactRules.Plan(Hit(type)).Id,
                    $"{type} has no branch of its own, so it must land on the flesh burst.");
            }
        }

        // === The part id and the sound id are NOT the same shape ===

        [Test]
        public void TheSoundHasFiveBranchesWhereThePartHasFour()
        {
            // `D_FANG` and the fall-through emit the SAME part (bum) but different sounds (:4197-4198 vs
            // :4202-4203). Collapsing the two lookups into one would silently give a fang hit the flesh
            // noise, and nothing about the emit would look wrong.
            Assert.AreEqual(UnitHitImpactRules.FleshId,
                UnitHitImpactRules.IdFor(DamageType.Fang));
            Assert.AreEqual(UnitHitImpactRules.FleshId,
                UnitHitImpactRules.IdFor(DamageType.Poison));

            Assert.AreEqual("fang_hit", UnitHitImpactRules.SoundIdFor(DamageType.Fang));
            Assert.AreEqual("hit_flesh", UnitHitImpactRules.SoundIdFor(DamageType.Poison));
            Assert.AreNotEqual(UnitHitImpactRules.SoundIdFor(DamageType.Fang),
                UnitHitImpactRules.SoundIdFor(DamageType.Poison),
                "the fang's own sound is the only thing that distinguishes the two flesh branches");
        }

        [Test]
        public void TheSoundIdsAreTheOnesTheOraclePlays()
        {
            Assert.AreEqual("electro",  UnitHitImpactRules.SoundIdFor(DamageType.Spark));
            Assert.AreEqual("acid",     UnitHitImpactRules.SoundIdFor(DamageType.Acid));
            Assert.AreEqual("hit_necr", UnitHitImpactRules.SoundIdFor(DamageType.Necrotic));
            Assert.AreEqual("fang_hit", UnitHitImpactRules.SoundIdFor(DamageType.Fang));
            Assert.AreEqual("hit_flesh", UnitHitImpactRules.SoundIdFor(DamageType.PhysicalBullet));
        }

        // === The anchor: four branches share one, moln is the exception ===

        [Test]
        public void TheFourSharedBranchesAnchorAtTheMidpointOfTheTwoMidHeights()
        {
            // (X + param1.X) / 2 = (100 + 160) / 2 = 130
            // (Y - scY/2 + param1.Y - param1.scY/2) / 2 = (180 + 170) / 2 = 175
            // …expressed as a delta from the defender's origin (100, 200): (30, -25).
            foreach (DamageType type in new[]
                     {
                         DamageType.Acid, DamageType.Necrotic, DamageType.Fang, DamageType.PhysicalBullet,
                     })
            {
                ParticleEmit emit = UnitHitImpactRules.Plan(Hit(type));

                Assert.AreEqual(30f, emit.OffsetX, 1e-4f, $"{type}: midpoint X is 130, defender X is 100.");
                Assert.AreEqual(-25f, emit.OffsetY, 1e-4f,
                    $"{type}: midpoint Y is 175, defender Y (feet) is 200.");
                Assert.IsNotNull(emit.Spec, $"{type}: the shared branches always carry a spec.");
                Assert.IsNull(emit.Spec.CelX,
                    $"{type}: only moln carries a target — the others are a contact burst.");
            }
        }

        [Test]
        public void TheArcAnchorsOnTheDefenderAndCarriesTheAttackerAsItsTarget()
        {
            // Emitter.emit("moln", loc, X, Y - scY / 2, {celx: param1.X, cely: param1.Y - param1.scY / 2})
            // — the defender's OWN mid-height, and the attacker's carried as the part's target, because
            // the arc is drawn between the two. Also the only branch with no scale.
            ParticleEmit emit = UnitHitImpactRules.Plan(Hit(DamageType.Spark));

            Assert.AreEqual(0f, emit.OffsetX, 1e-4f, "the arc starts at the defender, not the midpoint.");
            Assert.AreEqual(-20f, emit.OffsetY, 1e-4f, "…at the defender's mid-height: -scY/2 = -20.");
            Assert.AreEqual(160f, emit.Spec.CelX, 1e-4f, "celx is the attacker's X.");
            Assert.AreEqual(170f, emit.Spec.CelY, 1e-4f, "cely is the attacker's mid-height: 200 - 60/2.");
            Assert.IsNull(emit.Spec.Scale, "the arc is the one branch AS3 gives no scale to.");
        }

        [Test]
        public void TheArcAndTheBurstDoNotShareAnAnchor()
        {
            // The control for the two tests above: if the two shapes were ever collapsed, both would
            // still pass individually against their own expectation only if the expectations were wrong
            // together. This asserts the two anchors are actually different for the same two units.
            ParticleEmit arc = UnitHitImpactRules.Plan(Hit(DamageType.Spark));
            ParticleEmit burst = UnitHitImpactRules.Plan(Hit(DamageType.Acid));

            Assert.AreNotEqual(arc.OffsetX, burst.OffsetX);
            Assert.AreNotEqual(arc.OffsetY, burst.OffsetY);
        }

        // === The scale ===

        [Test]
        public void TheScaleIsTheAttackersDamageOverTwentyTimesTheVariance()
        {
            // _loc4_ = param1.dam * _loc3_ * param2 / 20 (Unit.as:4168), in range so neither clamp fires.
            Assert.AreEqual(2f, UnitHitImpactRules.ScaleFor(Hit(DamageType.Acid, 40f, 1f, 1f)), 1e-4f,
                "40 * 1 * 1 / 20 = 2");
            Assert.AreEqual(1.6f, UnitHitImpactRules.ScaleFor(Hit(DamageType.Acid, 40f, 0.8f, 1f)), 1e-4f,
                "the variance is a factor, not a jitter: 40 * 0.8 / 20");
            Assert.AreEqual(1f, UnitHitImpactRules.ScaleFor(
                Hit(DamageType.Acid, 40f, 1f, 0.5f)), 1e-4f,
                "the collision multiplier is a factor too: 40 * 1 * 0.5 / 20 = 1. "
                + "Kept in range deliberately — 4 * 1 * 0.5 / 20 = 0.1 would sit below the floor and the "
                + "clamp would hide whether the multiplier was read at all.");
        }

        [Test]
        public void TheScaleIsClampedAtBothEnds()
        {
            // :4169-4172 floors it at 0.5 and :4173-4176 ceilings it at 3. Both clamps matter: the floor
            // keeps a scratch from vanishing, and the ceiling is what stops a boss hit from covering the
            // screen. 2 * 0.8 / 20 = 0.08 would round to nothing.
            Assert.AreEqual(UnitHitImpactRules.ScaleFloor,
                UnitHitImpactRules.ScaleFor(Hit(DamageType.Acid, 2f, 0.8f, 1f)), 1e-4f);
            Assert.AreEqual(UnitHitImpactRules.ScaleCeiling,
                UnitHitImpactRules.ScaleFor(Hit(DamageType.Acid, 200f, 1f, 1f)), 1e-4f);

            Assert.AreEqual(0.5f, UnitHitImpactRules.ScaleFloor, 1e-4f);
            Assert.AreEqual(3f, UnitHitImpactRules.ScaleCeiling, 1e-4f);
            Assert.AreEqual(20f, UnitHitImpactRules.ScaleDamageDivisor, 1e-4f);
        }

        [Test]
        public void TheScaleIsCarriedOnTheBurstAndNotOnTheArc()
        {
            // The scale is computed unconditionally at :4168, BEFORE the switch — so it exists on every
            // path even though only four of the five branches pass it on. This pins that the arc drops it
            // rather than the value being absent.
            Assert.AreEqual(2f,
                UnitHitImpactRules.Plan(Hit(DamageType.Necrotic, 40f, 1f, 1f)).Spec.Scale, 1e-4f);
            Assert.AreEqual(2f, UnitHitImpactRules.ScaleFor(Hit(DamageType.Spark, 40f, 1f, 1f)), 1e-4f,
                "the scale is computed for the spark branch too — it is simply not passed to the emit.");
        }

        [Test]
        public void AZeroDamageAttackerStillProducesTheFloorNotZero()
        {
            // The end of the clamp chain, and the reason it is written as two `if`s rather than a clamp
            // helper that might return the raw value: a 0-damage attacker (a data error, or a tip with no
            // damage) must still draw a visible burst, at 0.5.
            Assert.AreEqual(UnitHitImpactRules.ScaleFloor,
                UnitHitImpactRules.ScaleFor(Hit(DamageType.Acid, 0f, 1f, 1f)), 1e-4f);
            Assert.IsTrue(UnitHitImpactRules.ScaleFor(Hit(DamageType.Acid, 0f, 1f, 1f)) > 0f);
        }
    }
}
