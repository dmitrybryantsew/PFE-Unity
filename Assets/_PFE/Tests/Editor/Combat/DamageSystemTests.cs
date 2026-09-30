using System;
using System.Collections.Generic;
using System.Reflection;
using MessagePipe;
using NUnit.Framework;
using UnityEngine;
using PFE.Core;
using PFE.Core.Messages;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="DamageSystem"/> — the queue, the two timings, and the edge cases that only exist
    /// because resolution is deferred.
    ///
    /// <para>Deferring damage to a tick introduces failure modes that the old inline
    /// <c>DamageResolver</c> could not have: a target that dies earlier in the same drain, a hit
    /// reported *during* a drain, and a queue that exists with nothing to drain it. Each has a test
    /// here, because each is silent when it goes wrong — the numbers just come out slightly different.</para>
    /// </summary>
    [TestFixture]
    public class DamageSystemTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Records every <c>GetStream</c> request and delegates the actual rolls to a real seeded PCG.
        /// The requests are the point: the salt and the call count are both load-bearing (see the
        /// class remarks on <see cref="DamageSystem"/>).
        /// </summary>
        private sealed class RecordingRng : IRngService
        {
            private readonly IRngService _inner = new PcgRngService(0xC0FFEEUL);

            public readonly List<(RngStream Stream, int? Salt)> StreamRequests = new();

            public IRngService GetStream(RngStream stream, int? salt = null)
            {
                StreamRequests.Add((stream, salt));
                return _inner.GetStream(stream, salt);
            }

            public uint NextUInt() => _inner.NextUInt();
            public float NextFloat() => _inner.NextFloat();
            public int NextInt(int maxExclusive) => _inner.NextInt(maxExclusive);
            public int Range(int minInclusive, int maxExclusive) => _inner.Range(minInclusive, maxExclusive);
            public float Range(float min, float max) => _inner.Range(min, max);
            public bool Chance(float probability) => _inner.Chance(probability);
            public void Shuffle<T>(IList<T> list) => _inner.Shuffle(list);
        }

        private sealed class FakePublisher : IPublisher<DamageDealtMessage>
        {
            public readonly List<DamageDealtMessage> Messages = new();

            public void Publish(DamageDealtMessage message) => Messages.Add(message);
        }

        private sealed class FakeTarget : IDamageable
        {
            public float Health = 100f;

            /// <summary>Properties, not fields: a field does not satisfy an interface member.</summary>
            public float MaxHealth { get; set; } = 100f;

            public ArmourState Armour { get; set; } = ArmourState.None;

            /// <summary>
            /// Settable so a test can give the target a real table. Defaults to AS3's baseline — which
            /// carries <c>emp = 0</c> — so a test that does not set it sees what a unit with no
            /// <c>&lt;vulner&gt;</c> element actually has, rather than a convenient identity.
            /// </summary>
            public VulnerabilityData Vulnerabilities { get; set; } = VulnerabilityData.Neutral;

            public int ApplyDamageCalls;
            public DamageOutcome LastOutcome;

            /// <summary>Runs inside <see cref="ApplyDamage"/>, for the reentrancy test.</summary>
            public Action OnApply;

            public void TakeDamage(float damage) => Health -= damage;

            public bool ApplyDamage(in DamageOutcome outcome)
            {
                ApplyDamageCalls++;
                LastOutcome = outcome;
                Health -= outcome.HpDamage;
                OnApply?.Invoke();
                return outcome.ArmourBroke;
            }

            public float CurrentHealth => Health;
            public bool IsAlive => Health > 0f;
        }

        private static DamageContext Context(
            float baseDamage = 10f,
            float explosionDamage = 0f,
            float armorMultiplier = 1f,
            float piercing = 0f,
            float critChance = 0f,
            float critMultiplier = 1f,
            DamageType damageType = DamageType.PhysicalBullet)
            => new DamageContext(
                owner: null,
                weapon: null,
                baseDamage: baseDamage,
                explosionDamage: explosionDamage,
                armorMultiplier: armorMultiplier,
                piercing: piercing,
                knockback: 0f,
                knockbackDir: Vector2.zero,
                critChance: critChance,
                critMultiplier: critMultiplier,
                damageType: damageType,
                destroyTiles: 0f,
                penetrationChance: 0f,
                dopEffect: null,
                dopDamage: 0f,
                dopChance: 1f);

        /// <summary>
        /// Builds the settings asset with both gates set explicitly. The fields are private and
        /// serialized, so they are set reflectively — the same way Unity's serializer would, and the
        /// same way <c>SimLoopTests</c> does it.
        /// </summary>
        private static PfeDebugSettings MakeSettings(
            bool simTickEnabled, bool simTickDamage, bool applyVulnerabilities = false)
        {
            var settings = ScriptableObject.CreateInstance<PfeDebugSettings>();

            SetPrivateField(settings, "simTickEnabled", simTickEnabled);
            SetPrivateField(settings, "simTickDamage", simTickDamage);
            SetPrivateField(settings, "applyVulnerabilities", applyVulnerabilities);

            return settings;
        }

        private static void SetPrivateField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(field, Is.Not.Null,
                $"PfeDebugSettings.{name} was renamed; update this helper");

            field.SetValue(target, value);
        }

        private static DamageSystem Make(
            bool simTickEnabled,
            bool simTickDamage,
            IRngService rng = null,
            IPublisher<DamageDealtMessage> publisher = null,
            bool applyVulnerabilities = false)
        {
            var settings = MakeSettings(simTickEnabled, simTickDamage, applyVulnerabilities);

            var system = new DamageSystem(
                new DamageCalculator(new CombatCalculator()),
                rng ?? new RecordingRng(),
                publisher,
                settings,
                new SimLoop(new SimClock(), settings));

            // IsTickAligned also requires that this system actually reached the loop; Start() is what
            // does that, and it is the same call VContainer's IStartable makes.
            system.Start();

            return system;
        }

        private static DamageSystem MakeTickAligned(
            IRngService rng = null,
            IPublisher<DamageDealtMessage> publisher = null,
            bool applyVulnerabilities = false)
            => Make(simTickEnabled: true, simTickDamage: true, rng, publisher, applyVulnerabilities);

        private static DamageSystem MakeImmediate(
            IRngService rng = null,
            IPublisher<DamageDealtMessage> publisher = null,
            bool applyVulnerabilities = false)
            => Make(simTickEnabled: true, simTickDamage: false, rng, publisher, applyVulnerabilities);

        // ── Ordering ─────────────────────────────────────────────────────────

        [Test]
        public void TickOrder_IsTheDamageSlot()
        {
            Assert.AreEqual(SimTickOrder.Damage, MakeTickAligned().TickOrder,
                "Damage must resolve after every source has moved, and before triggers/destruction.");
        }

        // ── The two timings ──────────────────────────────────────────────────

        [Test]
        public void TickMode_QueuesTheHit_AndAppliesItOnlyOnSimTick()
        {
            var system = MakeTickAligned();
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Direct(Context(baseDamage: 20f), target, Vector3.zero));

            Assert.AreEqual(1, system.PendingCount, "Report must only queue in tick mode.");
            Assert.AreEqual(0, target.ApplyDamageCalls, "Nothing may land before the tick.");
            Assert.AreEqual(100f, target.Health, 1e-4f);

            system.SimTick(7);

            Assert.AreEqual(0, system.PendingCount);
            Assert.AreEqual(1, target.ApplyDamageCalls);
            Assert.AreEqual(80f, target.Health, 1e-4f);
            Assert.AreEqual(1, system.ResolvedCount);
        }

        [Test]
        public void ImmediateMode_ResolvesAtReportTime_AndNeverQueues()
        {
            var system = MakeImmediate();
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Direct(Context(baseDamage: 20f), target, Vector3.zero));

            Assert.AreEqual(0, system.PendingCount, "Immediate mode must not accumulate.");
            Assert.AreEqual(1, target.ApplyDamageCalls);
            Assert.AreEqual(80f, target.Health, 1e-4f);
        }

        [Test]
        public void SimTickDamageWithoutSimTickEnabled_ResolvesImmediately()
        {
            // The hazard this pins: SimLoop gates dispatch on SimTickEnabled, so with it off a queued
            // hit would never be drained and damage would silently stop working entirely.
            var system = Make(simTickEnabled: false, simTickDamage: true);
            var target = new FakeTarget { Health = 100f };

            Assert.IsFalse(system.IsTickAligned);

            system.Report(PendingDamage.Direct(Context(baseDamage: 20f), target, Vector3.zero));

            Assert.AreEqual(0, system.PendingCount);
            Assert.AreEqual(1, target.ApplyDamageCalls);
            Assert.AreEqual(80f, target.Health, 1e-4f);
        }

        // ── Deferred-liveness edge cases ─────────────────────────────────────

        [Test]
        public void TargetThatDiesDuringTheDrain_IsSkippedNotApplied()
        {
            var system = MakeTickAligned();
            var target = new FakeTarget { Health = 10f };

            // Both queued in the same tick; the first is lethal.
            system.Report(PendingDamage.Direct(Context(baseDamage: 50f), target, Vector3.zero));
            system.Report(PendingDamage.Direct(Context(baseDamage: 50f), target, Vector3.zero));

            system.SimTick(1);

            Assert.AreEqual(1, target.ApplyDamageCalls, "The second hit must not land on a corpse.");
            Assert.AreEqual(1, system.ResolvedCount);
            Assert.AreEqual(1, system.SkippedCount);
            Assert.AreEqual(-40f, target.Health, 1e-4f);
        }

        [Test]
        public void AHitReportedDuringTheDrain_LandsOnTheNextTick()
        {
            // Pins the double buffer. Without the swap, a hit reported from inside ApplyDamage would
            // either be dropped or mutate the list under iteration.
            var system = MakeTickAligned();
            var second = new FakeTarget { Health = 100f };
            var first = new FakeTarget { Health = 100f };

            first.OnApply = () => system.Report(
                PendingDamage.Direct(Context(baseDamage: 5f), second, Vector3.zero));

            system.Report(PendingDamage.Direct(Context(baseDamage: 20f), first, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(0, second.ApplyDamageCalls,
                "A hit reported mid-drain must not resolve inside the same drain.");
            Assert.AreEqual(1, system.PendingCount);

            system.SimTick(2);

            Assert.AreEqual(1, second.ApplyDamageCalls);
            Assert.AreEqual(95f, second.Health, 1e-4f);
        }

        [Test]
        public void ANullTarget_IsIgnoredAtReportTime()
        {
            var system = MakeTickAligned();

            system.Report(PendingDamage.Direct(Context(), null, Vector3.zero));

            Assert.AreEqual(0, system.PendingCount);
        }

        [Test]
        public void ZeroDamage_IsNotApplied()
        {
            var system = MakeTickAligned();
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Direct(Context(baseDamage: 0f), target, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(0, target.ApplyDamageCalls);
            Assert.AreEqual(0, system.ResolvedCount);
        }

        // ── RNG plumbing ─────────────────────────────────────────────────────

        [Test]
        public void TickMode_SaltsTheCombatStreamWithTheTickIndex_AndFetchesItOncePerTick()
        {
            // `PcgRngService.GetStream(stream, salt)` constructs a NEW generator per call, so fetching
            // per hit would hand every hit in the tick an identical stream — and therefore identical
            // crit rolls. One fetch per tick is the fix, and this is the guard on it.
            var rng = new RecordingRng();
            var system = MakeTickAligned(rng);
            var a = new FakeTarget { Health = 100f };
            var b = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Direct(Context(baseDamage: 10f), a, Vector3.zero));
            system.Report(PendingDamage.Direct(Context(baseDamage: 10f), b, Vector3.zero));
            system.SimTick(42);

            Assert.AreEqual(1, rng.StreamRequests.Count, "One stream per tick, shared by its hits.");
            Assert.AreEqual(RngStream.Combat, rng.StreamRequests[0].Stream);
            Assert.AreEqual(42, rng.StreamRequests[0].Salt,
                "The salt is the tick index, which is what makes a tick's rolls reproducible.");
        }

        [Test]
        public void ImmediateMode_UsesTheUnsaltedCombatStream_AndReusesIt()
        {
            var rng = new RecordingRng();
            var system = MakeImmediate(rng);
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Direct(Context(baseDamage: 1f), target, Vector3.zero));
            system.Report(PendingDamage.Direct(Context(baseDamage: 1f), target, Vector3.zero));

            Assert.AreEqual(1, rng.StreamRequests.Count,
                "The stream must be created once and reused, not restarted per hit.");
            Assert.IsNull(rng.StreamRequests[0].Salt,
                "Immediate mode has no tick index to salt with.");
        }

        [Test]
        public void SimTick_WithAnEmptyQueue_DoesNotTouchTheRng()
        {
            // Salting constructs a generator, so an unconditional fetch would allocate 30 times a
            // second for nothing.
            var rng = new RecordingRng();
            var system = MakeTickAligned(rng);

            system.SimTick(1);
            system.SimTick(2);

            Assert.AreEqual(0, rng.StreamRequests.Count);
        }

        // ── The formula the system owns ──────────────────────────────────────

        [Test]
        public void EquippedArmour_IsReadFromTheTarget_NotHardcodedToZero()
        {
            // The regression guard for the deleted DamageResolver, which carried
            // `float armour = 0f; // TODO: target.GetArmour(...)` — so armour never applied at all.
            var system = MakeTickAligned();
            var target = new FakeTarget
            {
                Health = 100f,
                Armour = ArmourState.FromItem(100f, 100f, physicalRating: 20f, energyRating: 0f, reliability: 1f),
            };

            system.Report(PendingDamage.Direct(Context(baseDamage: 50f), target, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(70f, target.Health, 1e-4f, "50 - 20.");
            Assert.IsTrue(target.LastOutcome.ArmourReduced);
        }

        [Test]
        public void Crit_IsRolledOnTheCombatStream_AndMultiplies()
        {
            var system = MakeTickAligned();
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 10f, critChance: 1f, critMultiplier: 3f), target, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(70f, target.Health, 1e-4f, "10 * 3.");
            Assert.IsTrue(target.LastOutcome.IsCritical);
        }

        [Test]
        public void Piercing_IsSubtractedFlatly_NotAsAFraction()
        {
            // The other DamageResolver fault: it did `armour * armorMult * (1 - clamp01(piercing))`,
            // treating piercing as a 0..1 fraction. AS3 subtracts it flatly from the reduction.
            var system = MakeTickAligned();
            var target = new FakeTarget
            {
                Health = 100f,
                Armour = ArmourState.FromItem(100f, 100f, 20f, 0f, 1f),
            };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 50f, piercing: 8f), target, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(62f, target.Health, 1e-4f, "50 - (20 - 8), not 50 - 20 * (1 - 0.08).");
        }

        // ── Explosions ───────────────────────────────────────────────────────

        [Test]
        public void Explosion_AppliesFalloffAndTheFactionMultiplier()
        {
            var system = MakeTickAligned();
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Explosion(
                Context(baseDamage: 0f, explosionDamage: 40f),
                target,
                targetPosition: Vector3.zero,
                explosionCentre: Vector3.zero,
                explosionRadius: 10f,
                factionMultiplier: 0.25f));

            system.SimTick(1);

            Assert.AreEqual(90f, target.Health, 1e-4f, "40 * falloff 1 * 0.25 = 10.");
        }

        [Test]
        public void Explosion_AtTheRadiusEdge_DoesNoDamage()
        {
            var system = MakeTickAligned();
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Explosion(
                Context(explosionDamage: 40f),
                target,
                targetPosition: new Vector3(10f, 0f, 0f),
                explosionCentre: Vector3.zero,
                explosionRadius: 10f));

            system.SimTick(1);

            Assert.AreEqual(100f, target.Health, 1e-4f, "Falloff is exactly 0 at the edge.");
            Assert.AreEqual(0, target.ApplyDamageCalls, "Zero damage is not applied at all.");
        }

        // ── The published message ────────────────────────────────────────────

        [Test]
        public void ResolvingPublishesTheDamageThatLanded_NotTheDamageRequested()
        {
            var publisher = new FakePublisher();
            var system = MakeTickAligned(publisher: publisher);
            var target = new FakeTarget
            {
                Health = 100f,
                Armour = ArmourState.FromItem(100f, 100f, 20f, 0f, 1f),
            };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 50f), target, new Vector3(3f, 4f, 0f)));
            system.SimTick(1);

            Assert.AreEqual(1, publisher.Messages.Count);
            Assert.AreEqual(30f, publisher.Messages[0].damage, 1e-4f,
                "The floating number must show the post-armour damage.");
            Assert.AreEqual(new Vector3(3f, 4f, 0f), publisher.Messages[0].position);
        }

        // ── The vulnerability term (AS3 Unit.damage():3527-3530) ─────────────

        /// <summary>
        /// A table that is <c>1</c> everywhere except the slots a test names. Built from the all-ones
        /// constructor rather than from <see cref="VulnerabilityData.Neutral"/> on purpose: a test that
        /// wants to assert the <c>emp</c> baseline must say so, and one that does not must not inherit
        /// it by accident.
        /// </summary>
        private static VulnerabilityData Table(float bullet = 1f, float emp = 1f, float laser = 1f)
        {
            var table = new VulnerabilityData(1f);
            table.bullet = bullet;
            table.emp = emp;
            table.laser = laser;
            return table;
        }

        [Test]
        public void VulnerabilityTerm_Off_LeavesTheNumbersExactlyAsTheyWere()
        {
            var system = MakeTickAligned();                     // the flag defaults off
            var target = new FakeTarget
            {
                Health = 100f,
                Vulnerabilities = Table(bullet: 0.5f),
            };

            system.Report(PendingDamage.Direct(Context(baseDamage: 10f), target, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(90f, target.Health, 1e-4f,
                "With the term off a 0.5 multiplier must not be consulted at all — this is the " +
                "regression guard for the whole pre-A7 behaviour.");
        }

        [Test]
        public void VulnerabilityTerm_On_ScalesDamageByTheTargetsMultiplierForItsType()
        {
            var system = MakeTickAligned(applyVulnerabilities: true);
            var target = new FakeTarget
            {
                Health = 100f,
                Vulnerabilities = Table(bullet: 0.5f),
            };

            system.Report(PendingDamage.Direct(Context(baseDamage: 10f), target, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(95f, target.Health, 1e-4f, "10 * 0.5 = 5.");
        }

        [Test]
        public void VulnerabilityTerm_On_IsPerType_SoAnUntouchedSlotStaysNeutral()
        {
            var system = MakeTickAligned(applyVulnerabilities: true);
            var target = new FakeTarget
            {
                Health = 100f,
                Vulnerabilities = Table(bullet: 0.5f),          // laser is left at 1
            };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 10f, damageType: DamageType.Laser), target, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(90f, target.Health, 1e-4f,
                "The multiplier is looked up by the hit's own damage type, not applied globally.");
        }

        [Test]
        public void VulnerabilityTerm_ScalesTheArmourWearToo_BecauseItIsAppliedFirst()
        {
            // The ordering assertion — the one a plausible implementation gets wrong. AS3 multiplies at
            // :3529 and only then reads param1 for the pool block at :3580 (`_loc9_ = param1`), so a
            // resistant target's armour also lasts longer. Applying the term *after* the wear passes
            // every test above and still fails here.
            //
            // reliability is 0 so the flat rating never applies (`isrnd(0)` is never true). That keeps
            // the hp number deterministic without depending on the seeded PCG's rolls — the wear is
            // computed before the roll and does not depend on it at all.
            var system = MakeTickAligned(applyVulnerabilities: true);
            var target = new FakeTarget
            {
                Health = 100f,
                Armour = ArmourState.FromItem(100f, 100f, 20f, 0f, 0f),
                Vulnerabilities = Table(bullet: 0.5f),
            };

            system.Report(PendingDamage.Direct(Context(baseDamage: 50f), target, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(25f, target.LastOutcome.ArmourIntegrityDamage, 1e-4f,
                "The plate wears by the post-vulnerability 25, not the raw 50.");
            Assert.AreEqual(75f, target.Health, 1e-4f);
        }

        [Test]
        public void VulnerabilityTerm_ZeroedHitDoesNothingAtAll_NotZeroDamage()
        {
            // AS3 :3563 `if(param1 == 0) return 0`. The realistic trigger is the one the real data has:
            // AS3's baseline carries emp = 0, so every unit is EMP-immune until a <vulner> element
            // grants it back — 25 of the 94 elements do. This is the gameplay effect of the flag.
            var publisher = new FakePublisher();
            var system = MakeTickAligned(publisher: publisher, applyVulnerabilities: true);
            var target = new FakeTarget
            {
                Health = 100f,
                Armour = ArmourState.FromItem(100f, 100f, 20f, 0f, 1f),
                Vulnerabilities = VulnerabilityData.Neutral,
            };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 40f, damageType: DamageType.EMP), target, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(100f, target.Health, 1e-4f, "emp = 0 nullifies the hit.");
            Assert.AreEqual(0, target.ApplyDamageCalls,
                "Not even a zero-damage outcome reaches the target.");
            Assert.AreEqual(0, system.ResolvedCount, "A nullified hit is not a resolved hit.");
            Assert.AreEqual(0, publisher.Messages.Count,
                "And no floating number — AS3 returns before one is emitted.");
        }

        [Test]
        public void VulnerabilityTerm_On_AnEmpGrantingTableLetsTheHitThrough()
        {
            // The complement of the test above: same hit, same damage type, but a table that grants emp
            // back. Without this, an implementation that nullified *every* EMP hit would satisfy the
            // zero case, so the pair is what pins the behaviour rather than the constant.
            var system = MakeTickAligned(applyVulnerabilities: true);
            var target = new FakeTarget
            {
                Health = 100f,
                Vulnerabilities = Table(emp: 0.25f),
            };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 40f, damageType: DamageType.EMP), target, Vector3.zero));
            system.SimTick(1);

            Assert.AreEqual(90f, target.Health, 1e-4f, "40 * 0.25 = 10 — AllData.as:513's shape.");
        }

        [Test]
        public void VulnerabilityTerm_AppliesInImmediateModeToo_NotOnlyOnTheTick()
        {
            // The timing flag and this one are independent: the formula is the same in both modes, which
            // is what makes the timing flag safe to flip.
            var system = MakeImmediate(applyVulnerabilities: true);
            var target = new FakeTarget
            {
                Health = 100f,
                Vulnerabilities = Table(bullet: 0.25f),
            };

            system.Report(PendingDamage.Direct(Context(baseDamage: 40f), target, Vector3.zero));

            Assert.AreEqual(90f, target.Health, 1e-4f, "40 * 0.25 = 10, resolved inline.");
        }
    }
}
