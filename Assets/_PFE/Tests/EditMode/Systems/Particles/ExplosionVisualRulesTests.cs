using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Systems.Particles;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins <see cref="ExplosionVisualRules"/> — the nine-arm damage-type table and the per-weapon
    /// <c>visexpl</c> override that AS3's <c>Bullet.explVis()</c> dispatches on.
    ///
    /// <para><b>Why this is worth a fixture.</b> The table came out of a private method on a
    /// <c>MonoBehaviour</c>, where only its <c>D_EXPL</c> arm had any coverage at all — so eight of the
    /// nine arms and the whole override were reachable by no test, offline or otherwise. The failure mode
    /// is the project's recurring one: an arm that emits the <i>wrong</i> particle id, or silently
    /// nothing, looks exactly like a missing sprite. Every "it emits nothing" assertion below is
    /// therefore paired with a positive control that the same call emits something on a neighbouring
    /// input, because "nothing happened" is unfalsifiable on its own.</para>
    /// </summary>
    [TestFixture]
    public class ExplosionVisualRulesTests
    {
        private readonly List<ParticleEmit> _emits = new List<ParticleEmit>();
        private string _sound;

        private bool Plan(string visExpl, DamageType type, bool inWater = false, IRngService rng = null)
            => ExplosionVisualRules.Plan(visExpl, type, inWater, rng, _emits, out _sound);

        private List<string> Ids()
        {
            var ids = new List<string>(_emits.Count);
            foreach (ParticleEmit e in _emits) ids.Add(e.Id);
            return ids;
        }

        // ── Fakes ────────────────────────────────────────────────────────────────────────────

        /// <summary>Counts the draws it is asked for, so "the acid arm is the only one that rolls" is testable.</summary>
        private sealed class CountingRng : IRngService
        {
            private readonly IRngService _inner = new PcgRngService(12345UL);
            public int Draws;

            public uint NextUInt() => _inner.NextUInt();
            public float NextFloat() => _inner.NextFloat();
            public int NextInt(int maxExclusive) { Draws++; return _inner.NextInt(maxExclusive); }
            public int Range(int minInclusive, int maxExclusive) { Draws++; return _inner.Range(minInclusive, maxExclusive); }
            public float Range(float min, float max) => _inner.Range(min, max);
            public bool Chance(float probability) => _inner.Chance(probability);
            public void Shuffle<T>(IList<T> list) => _inner.Shuffle(list);
            public IRngService GetStream(RngStream stream, int? salt = null) => _inner.GetStream(stream, salt);
        }

        // ── The damage-type table ────────────────────────────────────────────────────────────

        [Test]
        public void Explosive_Dry_EmitsTheDryBlast()
        {
            Assert.IsTrue(Plan(null, DamageType.Explosive));
            CollectionAssert.AreEqual(new[] { "expl", "flare", "iskr" }, Ids());
            Assert.AreEqual("expl_e", _sound);
            Assert.AreEqual(ExplosionVisualRules.SparkKol, _emits[2].Spec.Kol,
                "the spark burst carries the oracle's {kol:16}");
        }

        [Test]
        public void Explosive_InWater_EmitsTheSplashInstead()
        {
            Assert.IsTrue(Plan(null, DamageType.Explosive, inWater: true));
            CollectionAssert.AreEqual(new[] { "explw", "bubble" }, Ids());
            Assert.AreEqual("expl_uw", _sound);

            ParticleSpec bubble = _emits[1].Spec;
            Assert.IsNotNull(bubble, "bubble carries an explicit kol/rx/ry");
            Assert.AreEqual(ExplosionVisualRules.BubbleKol, bubble.Kol);
            Assert.AreEqual(ExplosionVisualRules.BubbleRadius, bubble.RX);
            Assert.AreEqual(ExplosionVisualRules.BubbleRadius, bubble.RY);
        }

        [Test]
        public void Fire_Dry_EmitsTheFireBlast()
        {
            Assert.IsTrue(Plan(null, DamageType.Fire));
            CollectionAssert.AreEqual(new[] { "fireexpl", "flare", "iskr" }, Ids());
            Assert.AreEqual("fire_e", _sound);
        }

        [Test]
        public void Fire_InWater_IsSilent_AndThatIsDistinguishableFromAMissingArm()
        {
            // The oracle guards the fire arm on `inWater <= 0`; an underwater fire blast does nothing.
            Assert.IsFalse(Plan(null, DamageType.Fire, inWater: true));
            Assert.IsEmpty(_emits);
            Assert.IsNull(_sound);

            // Positive control: the SAME call on dry ground emits. Without this the assertion above
            // would also pass if the Fire arm had simply been deleted.
            Assert.IsTrue(Plan(null, DamageType.Fire, inWater: false));
            Assert.IsNotEmpty(_emits);
        }

        [Test]
        public void Cryo_EmitsTheIceBlast()
        {
            Assert.IsTrue(Plan(null, DamageType.Cryo));
            CollectionAssert.AreEqual(new[] { "iceexpl", "snow" }, Ids());
            Assert.AreEqual("cryo_e", _sound);
            Assert.AreEqual(ExplosionVisualRules.SparkKol, _emits[1].Spec.Kol);
        }

        [Test]
        public void Emp_EmitsTheEmpBlast()
        {
            Assert.IsTrue(Plan(null, DamageType.EMP));
            CollectionAssert.AreEqual(new[] { "impexpl" }, Ids());
            Assert.AreEqual("emp_e", _sound);
        }

        [Test]
        public void Plasma_EmitsThePlasmaBlast()
        {
            Assert.IsTrue(Plan(null, DamageType.Plasma));
            CollectionAssert.AreEqual(new[] { "plaexpl" }, Ids());
            Assert.AreEqual("exppla_e", _sound);
        }

        [Test]
        public void Venom_EmitsGas_AndDoesSoInWaterToo()
        {
            // The oracle's gas arms carry no inWater test, unlike fire and explosive.
            Assert.IsTrue(Plan(null, DamageType.Venom));
            CollectionAssert.AreEqual(new[] { "gas" }, Ids());
            Assert.AreEqual("gas_e", _sound);

            Assert.IsTrue(Plan(null, DamageType.Venom, inWater: true));
            CollectionAssert.AreEqual(new[] { "gas" }, Ids());
        }

        [Test]
        public void Pink_EmitsPinkGas()
        {
            Assert.IsTrue(Plan(null, DamageType.Pink));
            CollectionAssert.AreEqual(new[] { "pinkgas" }, Ids());
            Assert.AreEqual("gas_e", _sound);
        }

        [Test]
        public void Acid_EmitsTheAcidBlastWithARolledDropletCount()
        {
            Assert.IsTrue(Plan(null, DamageType.Acid));
            CollectionAssert.AreEqual(new[] { "acidexpl", "acidkap" }, Ids());
            Assert.AreEqual("acid_e", _sound);
            Assert.IsNotNull(_emits[1].Spec, "acidkap carries an explicit kol");
        }

        [Test]
        public void Acid_KolSpansThirtyToThirtyFourInclusive_NotAConstant()
        {
            var rng = new PcgRngService(2026UL);
            int min = int.MaxValue, max = int.MinValue;

            for (int i = 0; i < 400; i++)
            {
                Assert.IsTrue(Plan(null, DamageType.Acid, rng: rng));
                int kol = _emits[1].Spec.Kol;
                min = Math.Min(min, kol);
                max = Math.Max(max, kol);
            }

            Assert.AreEqual(ExplosionVisualRules.AcidKolMin, min, "the oracle's floor (30) is reachable");
            Assert.AreEqual(ExplosionVisualRules.AcidKolMaxExclusive - 1, max,
                "the oracle's ceiling (34) is reachable — otherwise this is a constant wearing a range's clothes");
        }

        [Test]
        public void Acid_WithNoRng_FallsBackToTheOraclesMinimum()
        {
            // An offline host injects no RNG; the arm must still emit rather than throw.
            Assert.IsTrue(Plan(null, DamageType.Acid, rng: null));
            Assert.AreEqual(ExplosionVisualRules.AcidKolMin, _emits[1].Spec.Kol);
        }

        [Test]
        public void TheAcidRoll_IsDrawnOnlyOnTheAcidArm()
        {
            var rng = new CountingRng();

            Plan(null, DamageType.Explosive, rng: rng);
            Assert.AreEqual(0, rng.Draws,
                "a non-acid blast must not consume a draw the oracle would not have made");

            // Positive control: the acid arm DOES draw, exactly once.
            Plan(null, DamageType.Acid, rng: rng);
            Assert.AreEqual(1, rng.Draws);
        }

        [Test]
        public void Balefire_OffsetsTheBalefireSixtyPixelsUpward()
        {
            Assert.IsTrue(Plan(null, DamageType.Balefire));
            CollectionAssert.AreEqual(new[] { "balefire", "baleblast" }, Ids());
            Assert.AreEqual("bale_e", _sound);

            // AS3 `Y - 60` in a space whose Y runs DOWN, so the offset is negative = upward.
            Assert.AreEqual(ExplosionVisualRules.BalefireOffsetY, _emits[0].OffsetY);
            Assert.AreEqual(0f, _emits[1].OffsetY, "only balefire is offset");
        }

        // ── The per-weapon override ──────────────────────────────────────────────────────────

        [Test]
        public void Override_Sparkle_Dry_SubstitutesSparkleexplForTheTypeArm()
        {
            // `sparkle` is a magic value: the ordinary blast, with sparkleexpl in place of the
            // damage-type arm's second emitter, and the balefire sound.
            Assert.IsTrue(Plan(ExplosionVisualRules.Sparkle, DamageType.Explosive));
            CollectionAssert.AreEqual(new[] { "expl", "sparkleexpl", "iskr" }, Ids());
            Assert.AreEqual("bale_e", _sound);
        }

        [Test]
        public void Override_Sparkle_InWater_EmitsTheSplash()
        {
            Assert.IsTrue(Plan(ExplosionVisualRules.Sparkle, DamageType.Explosive, inWater: true));
            CollectionAssert.AreEqual(new[] { "explw", "bubble" }, Ids());
            Assert.AreEqual("expl_uw", _sound);
        }

        [Test]
        public void Override_ParticleId_EmitsOnlyThatId_AndSuppressesTheWholeTable()
        {
            // `ttexpl` is one of the four shipped override values that name a particle id directly.
            Assert.IsTrue(Plan("ttexpl", DamageType.Explosive));
            CollectionAssert.AreEqual(new[] { "ttexpl" }, Ids());
            Assert.IsNull(_sound, "the id arm plays no sound");

            // Control: the SAME damage type with no override emits three particles, so the override
            // really did suppress the table rather than the table having changed.
            Assert.IsTrue(Plan(null, DamageType.Explosive));
            Assert.AreEqual(3, _emits.Count);
        }

        [Test]
        public void Override_EmptyString_BehavesExactlyLikeNoOverride()
        {
            Assert.IsTrue(Plan("", DamageType.Cryo));
            List<string> withEmpty = Ids();

            Assert.IsTrue(Plan(null, DamageType.Cryo));
            CollectionAssert.AreEqual(withEmpty, Ids());
            Assert.AreEqual("cryo_e", _sound);
        }

        // ── Negative controls ────────────────────────────────────────────────────────────────

        [Test]
        public void ADamageTypeWithNoArm_EmitsNothing_AndThatIsNotAProblemWithTheHelper()
        {
            // `explVis` has no default branch: a plain kinetic round is silent by design.
            Assert.IsFalse(Plan(null, DamageType.PhysicalBullet));
            Assert.IsEmpty(_emits);
            Assert.IsNull(_sound);

            // Positive control: a neighbouring input on the same helper DOES emit, so `false` above is
            // a real decision rather than the helper being broken.
            Assert.IsTrue(Plan(null, DamageType.Explosive));
            Assert.IsNotEmpty(_emits);
        }

        [Test]
        public void Blade_And_Laser_AreAlsoSilent()
        {
            Assert.IsFalse(Plan(null, DamageType.Blade));
            Assert.IsFalse(Plan(null, DamageType.Laser));
            Assert.IsFalse(Plan(null, DamageType.PhysicalMelee));
        }
    }
}
