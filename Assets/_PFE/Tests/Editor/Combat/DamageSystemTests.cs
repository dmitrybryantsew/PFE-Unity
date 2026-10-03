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
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Weapons;
using PFE.Tests.Editor.Core;

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

            /// <summary>
            /// Settable so a test can hand the resolver a natural resistance. Defaults to <c>0</c>,
            /// which is both AS3's default and what every unit in the port has today except the
            /// armoured training dummy.
            /// </summary>
            public float SkinResistance { get; set; } = 0f;

            /// <summary>
            /// Settable so a test can give the target real evasion. Defaults to
            /// <see cref="EvasionState.Default"/> — AS3's own field defaults (dexter 1, dodge 0) — and
            /// <b>not</b> all-zeroes, which would mean <c>dexter &lt;= 0</c>, i.e. "hit by everything".
            /// </summary>
            public EvasionState Evasion { get; set; } = EvasionState.Default;

            /// <summary>
            /// Settable so a test can make a target immovable (<c>0</c>) or light (<c>1.5</c>). Defaults
            /// to AS3's field default of <c>1</c>.
            /// </summary>
            public float Knocked { get; set; } = 1f;

            /// <summary>
            /// Settable so a test can change how far a hit throws — already the post-<c>/50</c> value.
            /// Defaults to AS3's field default of <c>1</c>.
            /// </summary>
            public float Mass { get; set; } = 1f;

            /// <summary>
            /// Settable so a test can close the knockback gate. AS3 returns <b>before</b> taking its
            /// random draw, so this also decides whether the combat stream is consumed.
            /// </summary>
            public bool IsInvulnerable { get; set; } = false;

            /// <summary>
            /// Settable so a test can make the target one of AS3's non-living <c>doop</c> units, which
            /// suppresses the stealth crit. Defaults to <c>false</c> — AS3's own field default, and the
            /// living case that the overwhelming majority of units answer.
            /// </summary>
            public bool IsNonLiving { get; set; } = false;

            public int ApplyKnockbackCalls;
            public Vector2 LastKnockbackImpulse;

            public void ApplyKnockback(Vector2 impulse)
            {
                ApplyKnockbackCalls++;
                LastKnockbackImpulse = impulse;
            }

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
            DamageType damageType = DamageType.PhysicalBullet,
            float missChance = 0f,
            float precision = 0f,
            bool isMelee = false,
            float knockback = 0f,
            Vector2 knockbackDir = default)
            => new DamageContext(
                owner: null,
                weapon: null,
                baseDamage: baseDamage,
                explosionDamage: explosionDamage,
                armorMultiplier: armorMultiplier,
                piercing: piercing,
                knockback: knockback,
                knockbackDir: knockbackDir,
                critChance: critChance,
                critMultiplier: critMultiplier,
                damageType: damageType,
                destroyTiles: 0f,
                penetrationChance: 0f,
                dopEffect: null,
                dopDamage: 0f,
                dopChance: 1f,
                missChance: missChance,
                precision: precision,
                isMelee: isMelee);

        /// <summary>
        /// An RNG that returns scripted values and counts the rolls consumed. Needed for the avoidance
        /// tests because the outcome has to be forced, and — for the inert-context test — because the
        /// roll COUNT is itself the assertion.
        /// </summary>
        private sealed class ScriptedRng : IRngService
        {
            private readonly Queue<float> _values;

            public int RollsConsumed { get; private set; }

            public ScriptedRng(params float[] values) => _values = new Queue<float>(values);

            public float NextFloat()
            {
                RollsConsumed++;
                return _values.Count > 0 ? _values.Dequeue() : 0f;
            }

            public IRngService GetStream(RngStream stream, int? salt = null) => this;

            public uint NextUInt() => 0u;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool Chance(float probability) => NextFloat() < probability;
            public void Shuffle<T>(IList<T> list) { }
        }

        /// <summary>
        /// Builds the settings asset with every gate set explicitly. The fields are private and
        /// serialized, so they are set reflectively — the same way Unity's serializer would, and the
        /// same way <c>SimLoopTests</c> does it.
        ///
        /// <para><b><paramref name="testDamage"/> defaults to <c>true</c> here, which is the opposite of
        /// the asset's own default — deliberately.</b> These tests pin the <i>formula chain</i>
        /// (vulnerability, armour, crit, ordering) with exact numbers, and the damage spread multiplies
        /// every one of those numbers by <c>[0.7, 1.3)</c>, so leaving it on would turn every assertion
        /// into a range check and destroy what they were written to prove. The spread itself is pinned
        /// by <c>DamageVarianceTests</c> and by the explicit <c>testDamage: false</c> cases below, and
        /// <c>DamageVarianceTests.ProductionDefault_LeavesTheSpreadOn</c> guards that the asset really
        /// does ship with the spread enabled — so this helper cannot hide a regression in the default.</para>
        ///
        /// <para>Note that <c>testDamage</c> does <b>not</b> remove the random draw — it only discards
        /// its result (<c>Unit.as:4085-4089</c>). So the stream still advances by one per non-blast hit
        /// whether this is on or off, and no test here depends on a fractional roll.</para>
        /// </summary>
        private static PfeDebugSettings MakeSettings(
            bool simTickEnabled, bool simTickDamage, bool applyVulnerabilities = false,
            bool testDamage = true)
        {
            var settings = OfflineScriptableObject.Create<PfeDebugSettings>();

            SetPrivateField(settings, "simTickEnabled", simTickEnabled);
            SetPrivateField(settings, "simTickDamage", simTickDamage);
            SetPrivateField(settings, "applyVulnerabilities", applyVulnerabilities);
            SetPrivateField(settings, "testDamage", testDamage);

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
            bool applyVulnerabilities = false,
            bool testDamage = true)
        {
            var settings = MakeSettings(simTickEnabled, simTickDamage, applyVulnerabilities, testDamage);

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
            bool applyVulnerabilities = false,
            bool testDamage = true)
            => Make(simTickEnabled: true, simTickDamage: true, rng, publisher, applyVulnerabilities, testDamage);

        private static DamageSystem MakeImmediate(
            IRngService rng = null,
            IPublisher<DamageDealtMessage> publisher = null,
            bool applyVulnerabilities = false,
            bool testDamage = true)
            => Make(simTickEnabled: true, simTickDamage: false, rng, publisher, applyVulnerabilities, testDamage);

        /// <summary>
        /// Reports a batch of hits from <b>inside</b> a tick, at the projectile slot — which is where
        /// every real report comes from (<c>SimTickOrder.Projectiles</c> is 40,
        /// <c>SimTickOrder.Damage</c> is 60).
        ///
        /// <para>Why this exists rather than calling <c>Report</c> directly: the avoidance roll is taken
        /// at report time and needs the tick index, which it reads from <c>SimLoop.TickIndex</c>.
        /// <c>StepOnce</c> increments that index before dispatching, so a report made from inside a tick
        /// sees the tick it belongs to. A test that called <c>Report</c> and then <c>SimTick(n)</c> by
        /// hand would be attributing the report to whatever tick last ran — a shape the production loop
        /// cannot produce, and one that would make the stream assertions below vacuous.</para>
        /// </summary>
        private sealed class HitReporter : ISimTickable
        {
            private readonly DamageSystem _system;
            private readonly List<PendingDamage> _hits = new();

            /// <summary>What <see cref="DamageSystem.Report"/> answered for each hit, in order.</summary>
            public readonly List<DamageVerdict> Verdicts = new();

            public HitReporter(DamageSystem system) => _system = system;

            public int TickOrder => SimTickOrder.Projectiles;

            public void Report(in PendingDamage hit) => _hits.Add(hit);

            public void SimTick(int tickIndex)
            {
                for (int i = 0; i < _hits.Count; i++)
                {
                    Verdicts.Add(_system.Report(_hits[i]));
                }

                _hits.Clear();
            }
        }

        /// <summary>
        /// A tick-aligned system wired to a real, steppable <see cref="SimLoop"/>, plus a
        /// <see cref="HitReporter"/> registered ahead of it so hits can be delivered from inside a tick.
        /// </summary>
        private static (DamageSystem System, SimLoop Loop, HitReporter Reporter) MakeOnLoop(
            IRngService rng = null)
        {
            var settings = MakeSettings(simTickEnabled: true, simTickDamage: true);
            var loop     = new SimLoop(new SimClock(), settings);

            var system = new DamageSystem(
                new DamageCalculator(new CombatCalculator()),
                rng ?? new RecordingRng(),
                null,
                settings,
                loop);
            system.Start();

            var reporter = new HitReporter(system);
            loop.Register(reporter);

            return (system, loop, reporter);
        }

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

        // ── Natural resistance (AS3 skin) ────────────────────────────────────

        [Test]
        public void SkinResistance_IsReadFromTheTarget_NotPassedAsZero()
        {
            // AS3 `Unit.damage():3611-3632` opens each reduction branch with `_loc8_ = this.skin`,
            // so it is a flat subtraction that lands before the probabilistic armour roll. The
            // calculator has applied the term since the armour work landed and ArmourResolutionTests
            // exercises it — but DamageSystem, the only production caller, passed a literal 0f, so no
            // target could ever supply one. TrainingDummyController had been writing 20 for its
            // `tr='1'` variant the whole time; the value was computed and then dropped, which made the
            // armoured training dummy resolve identically to the plain one.
            var system = MakeImmediate();

            var bare = new FakeTarget { Health = 100f };
            var thickSkinned = new FakeTarget { Health = 100f, SkinResistance = 20f };

            system.Report(PendingDamage.Direct(Context(baseDamage: 50f), bare, Vector3.zero));
            system.Report(PendingDamage.Direct(Context(baseDamage: 50f), thickSkinned, Vector3.zero));

            Assert.AreEqual(50f, bare.Health, 1e-4f,
                "No skin and no armour: a physical hit lands whole.");
            Assert.AreEqual(70f, thickSkinned.Health, 1e-4f,
                "20 skin is a flat 20 off every physical hit, so 50 becomes 30.");
        }

        [Test]
        public void SkinResistance_DoesNotApply_ToATypeThatReachesNoReductionBranch()
        {
            // The gating is the subtle half and the reason this is not "target.SkinResistance" applied
            // universally: `_loc8_ = this.skin` sits *inside* each of AS3's two branches, so a type
            // that reaches neither gets no skin at all. Ten of the port's twenty-one types are in that
            // position (venom, poison, bleed, necrotic, pink, balefire, psionic, EMP, internal,
            // friendly fire — ArmourWear.ChannelFor maps them to ArmourChannel.None). Subtracting skin
            // for those would be a silent buff against all ten.
            var system = MakeImmediate();
            var target = new FakeTarget { Health = 100f, SkinResistance = 20f };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 50f, damageType: DamageType.Poison), target, Vector3.zero));

            Assert.AreEqual(50f, target.Health, 1e-4f,
                "Poison reaches no reduction branch, so the target's skin must not be subtracted.");
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
            // per roll would hand every roll in the tick an identical stream — and therefore identical
            // crit rolls. One generator per tick is the fix, and this is the guard on it.
            //
            // The rolls are now split across two phases of the tick: the hit-avoidance roll runs at
            // REPORT time (it has to — its answer tells the projectile whether to keep flying) and the
            // spread/armour/crit/knockback rolls at the drain. Both phases must therefore come from the
            // same generator, which is the property this asserts: one request, not two.
            var rng = new RecordingRng();
            var (_, loop, reporter) = MakeOnLoop(rng);
            var a = new FakeTarget { Health = 100f };
            var b = new FakeTarget { Health = 100f };

            reporter.Report(PendingDamage.Direct(Context(baseDamage: 10f), a, Vector3.zero));
            reporter.Report(PendingDamage.Direct(Context(baseDamage: 10f), b, Vector3.zero));

            loop.StepOnce();

            Assert.AreEqual(1, rng.StreamRequests.Count, "One stream per tick, shared by its hits.");
            Assert.AreEqual(RngStream.Combat, rng.StreamRequests[0].Stream);
            Assert.AreEqual(0, rng.StreamRequests[0].Salt,
                "The salt is the tick index, which is what makes a tick's rolls reproducible.");

            // A second tick gets its own generator, salted with its own index — otherwise the memo
            // would be a single long-lived stream in disguise and "reproducible per tick" would be
            // false.
            reporter.Report(PendingDamage.Direct(Context(baseDamage: 10f), a, Vector3.zero));
            loop.StepOnce();

            Assert.AreEqual(2, rng.StreamRequests.Count);
            Assert.AreEqual(1, rng.StreamRequests[1].Salt);
        }

        [Test]
        public void TickMode_AnEvadedHitIsKnownAtReportTime_SoAProjectileCanFlyOn()
        {
            // The regression guard for the play-test report: "miss seems to not penetrate behind unit
            // (unit still consumes it)". `Report` used to answer Queued for EVERY tick-mode hit, and a
            // projectile branches on `verdict == Evaded` to decide whether to keep flying — so with
            // SimTickDamage on, no round could ever pass through a unit it had missed. The verdict is
            // the whole assertion here: the damage outcome is a tick later, but the round's fate is not.
            var rng = new ScriptedRng(0.5f, 0.5f);
            var (system, loop, reporter) = MakeOnLoop(rng);
            var target = new FakeTarget { Health = 100f, Evasion = new EvasionState(100f, 0f, 0f) };

            // accuracy = 320/320 = 1.0, divisor = 100.05 ⇒ a 0.5 roll misses.
            reporter.Report(PendingDamage.Direct(
                Context(baseDamage: 40f, precision: 320f), target, Vector3.zero,
                travelDistancePixels: 320f));

            loop.StepOnce();

            Assert.AreEqual(1, reporter.Verdicts.Count);
            Assert.AreEqual(DamageVerdict.Evaded, reporter.Verdicts[0],
                "the projectile must be told the round was evaded — that bit is what lets it fly on");
            Assert.AreEqual(1, system.MissedCount, "the hit must be counted as evaded");
            Assert.AreEqual(0, system.ResolvedCount, "an evaded hit is never resolved");
            Assert.AreEqual(0, target.ApplyDamageCalls, "a miss must not reach the target at all");
            Assert.AreEqual(100f, target.Health, 1e-4f, "no damage");
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

        // ── Hit avoidance (Unit.udarBullet, Unit.as:4067-4131) ───────────────

        [Test]
        public void EvadedHit_AppliesNothingAtAll_NotEvenArmourWear()
        {
            // AS3 puts the ENTIRE hit — damage, armour wear, crit, knockback — inside the udarBullet
            // conjunction, so a miss must not dent the armour. This is the test that fails if the
            // avoidance roll is moved after the wear computation instead of before every damage term.
            var system = MakeImmediate(new ScriptedRng(0.5f));
            var target = new FakeTarget
            {
                Health  = 100f,
                Armour  = ArmourState.FromItem(
                    integrity: 100f, maxIntegrity: 100f,
                    physicalRating: 50f, energyRating: 50f, reliability: 1f),
                Evasion = new EvasionState(100f, 0f, 0f),
            };

            // accuracy = 320/320 = 1.0, divisor = 100.05 ⇒ a 0.5 roll misses.
            system.Report(PendingDamage.Direct(
                Context(baseDamage: 40f, precision: 320f), target, Vector3.zero,
                travelDistancePixels: 320f));

            Assert.AreEqual(1, system.MissedCount, "the hit must be counted as evaded");
            Assert.AreEqual(0, system.ResolvedCount, "an evaded hit is not resolved");
            Assert.AreEqual(0, target.ApplyDamageCalls, "a miss must not reach the target at all");
            Assert.AreEqual(100f, target.Health, 1e-4f, "no damage");
            Assert.AreEqual(100f, target.Armour.integrity, 1e-4f, "no armour wear");
        }

        [Test]
        public void EvadedHit_PublishesAMiss_SoThePresentationLayerCanShowIt()
        {
            // AS3 draws a "miss" number (Unit.as:4103-4117, txtMiss). The message is the only hook the
            // presentation layer has, so a miss that published nothing would silently lose the feedback.
            var publisher = new FakePublisher();
            var system    = MakeImmediate(new ScriptedRng(0.5f), publisher);
            var target    = new FakeTarget { Health = 100f, Evasion = new EvasionState(100f, 0f, 0f) };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 40f, precision: 320f), target, Vector3.zero,
                travelDistancePixels: 320f));

            Assert.AreEqual(1, publisher.Messages.Count);
            Assert.IsTrue(publisher.Messages[0].isMiss, "the message must be flagged as a miss");
            Assert.AreEqual(0f, publisher.Messages[0].damage, 1e-4f, "a miss deals no damage");
            Assert.IsFalse(publisher.Messages[0].isCritical, "a miss cannot crit");
        }

        [Test]
        public void EvasionDoesNotApplyToABlast()
        {
            // AS3's explosion path (Bullet.explRun) calls unit.damage() directly and never reaches
            // udarBullet, so miss/precision/dodge do not gate AoE. Without this the same high-dexterity
            // target would dodge grenades, which the oracle does not do.
            var system = MakeImmediate(new ScriptedRng(0.5f));
            var target = new FakeTarget { Health = 100f, Evasion = new EvasionState(100f, 0f, 0f) };

            system.Report(PendingDamage.Explosion(
                Context(explosionDamage: 40f, precision: 320f),
                target,
                targetPosition: Vector3.zero,
                explosionCentre: Vector3.zero,
                explosionRadius: 100f));

            Assert.AreEqual(0, system.MissedCount, "a blast is never evaded");
            Assert.AreEqual(1, system.ResolvedCount);
            Assert.AreEqual(60f, target.Health, 1e-4f, "full 40 damage, no falloff at the centre");
        }

        [Test]
        public void InertEvasionTerms_LeaveTheExistingNumbersUntouched()
        {
            // The behaviour-neutrality guard at the system level. A shot with no miss penalty and no
            // precision — which is every shot the port can currently produce — must land exactly as it
            // did before the avoidance path existed, and must not have consumed a roll *for the
            // avoidance conjunction*.
            //
            // `RollsConsumed` is 2, and neither draw belongs to the avoidance conjunction: they are the
            // damage spread (`Unit.as:4085`) and the knockback jitter (`otbros`'s
            // `Math.random() * 0.4 + 0.8`, `:4251`, called from `:4091` after `this.damage()`). Both run
            // after the conjunction. The distinction is what the companion test below pins: an evading
            // target costs one MORE draw than an inert one.
            //
            // This was 1 before the knockback port landed, and the number moved because the oracle really
            // does take that second draw on every landed hit — which is exactly why `KnockbackMath.Roll`
            // is a separate method from `Scale`: the draw happens even when the weapon's `@knock` is 0 or
            // the target's `knocked` is 0, and only the multiply by zero happens after.
            var rng    = new ScriptedRng();
            var system = MakeImmediate(rng);
            var target = new FakeTarget { Health = 100f, Evasion = new EvasionState(100f, 0f, 0f) };

            system.Report(PendingDamage.Direct(Context(baseDamage: 25f), target, Vector3.zero));

            Assert.AreEqual(75f, target.Health, 1e-4f);
            Assert.AreEqual(0, system.MissedCount);
            Assert.AreEqual(2, rng.RollsConsumed,
                "the avoidance conjunction short-circuits without rolling; the two draws are the spread " +
                "and the knockback jitter");
        }

        [Test]
        public void AnEvadingTargetCostsOneMoreDrawThanAnInertOne()
        {
            // The complement of the test above, and the reason `RollsConsumed` there is asserted as a
            // number rather than as "no roll at all". Same shot, same everything, except the target can
            // evade: the conjunction now takes its own draw *before* the spread, so the count goes up by
            // exactly one. Without this pair, "2 draws" would not distinguish "the conjunction is inert"
            // from "the conjunction rolled and something else did not".
            //
            // Both counts include the knockback jitter (`Unit.as:4251`, one draw per landed hit), so the
            // absolute numbers are 2 and 3 — but the DELTA is the assertion the name promises, and it is
            // asserted directly below so a future term that adds a draw to both sides cannot quietly make
            // the name false.
            var inertRng = new ScriptedRng();
            var inert    = MakeImmediate(inertRng);
            inert.Report(PendingDamage.Direct(
                Context(baseDamage: 25f), new FakeTarget { Health = 100f }, Vector3.zero));

            var evasiveRng = new ScriptedRng(0.001f);
            var evasive    = MakeImmediate(evasiveRng);
            evasive.Report(PendingDamage.Direct(
                Context(baseDamage: 25f, precision: 320f),
                new FakeTarget { Health = 100f, Evasion = new EvasionState(100f, 0f, 0f) },
                Vector3.zero,
                travelDistancePixels: 320f));

            Assert.AreEqual(2, inertRng.RollsConsumed, "spread + knockback jitter, no conjunction draw");
            Assert.AreEqual(3, evasiveRng.RollsConsumed,
                "one draw for accuracy-vs-dexterity, then the spread, then the knockback jitter");
            Assert.AreEqual(evasiveRng.RollsConsumed - inertRng.RollsConsumed, 1,
                "an evading target costs exactly one more draw than an inert one — the whole point");
            Assert.AreEqual(0, evasive.MissedCount,
                "a 0.001 roll is below accuracy/divisor (1.0 / 100.05), so it lands");
        }

        // ── Damage spread (Unit.udarBullet, Unit.as:4085) ────────────────────

        [Test]
        public void DamageSpread_ScalesTheIncomingNumber()
        {
            // The production default: `testDamage` off, so the spread is live. A scripted 0.0 gives
            // 0.0 * 0.6 + 0.7 = 0.7, the floor of the oracle's range — 10 damage becomes 7.
            var system = MakeImmediate(new ScriptedRng(0f), testDamage: false);
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Direct(Context(baseDamage: 10f), target, Vector3.zero));

            Assert.AreEqual(93f, target.Health, 1e-4f, "10 * 0.7 = 7");
        }

        [Test]
        public void DamageSpread_IsAppliedBeforeTheArmourReduction_NotAfter()
        {
            // The ordering assertion, and the one a plausible implementation gets wrong. AS3 multiplies
            // at :4085 and only then enters damage(), whose reduction block subtracts at :3644. So a
            // flat subtraction bites into 7, not into 10 — and the difference is invisible unless the
            // spread is below 1, which is why this test uses the 0.7 floor rather than a mid roll.
            //
            //   spread-then-reduce: max(0, 10 * 0.7 - 3) = 4.0    → health 96
            //   reduce-then-spread: max(0, 10 - 3) * 0.7 = 4.9    → health 95.1
            //
            // `skinResistance` is the flat term used rather than an armour rating on purpose: skin is
            // applied unconditionally when the damage type reaches a reduction branch, whereas a rating
            // only lands if the reliability roll succeeds — and a roll would make this test depend on the
            // very stream it is trying to reason about.
            var system = MakeImmediate(new ScriptedRng(0f), testDamage: false);
            var target = new FakeTarget { Health = 100f, SkinResistance = 3f };

            system.Report(PendingDamage.Direct(Context(baseDamage: 10f), target, Vector3.zero));

            Assert.AreEqual(96f, target.Health, 1e-4f,
                "the spread lands first, so the flat reduction subtracts from 7 — not from 10");
        }

        [Test]
        public void DamageSpread_AppliesToABlast()
        {
            // INVERTED. This fixture used to assert the opposite, reasoning that "Bullet.explRun calls
            // unit.damage() directly and never enters udarBullet, so a blast does its falloff value
            // exactly". The premise is true; the conclusion does not follow. AS3 repeats the spread
            // verbatim at `explGas():763` — `_loc5_ = this.damageExpl * (Math.random() * 0.6 + 0.7)`
            // — before the blast calls `damage()`, and the `explTip 1` shape gets a udarBullet spread
            // anyway through the child bullet `explBullet()` spawns. A blast skips udarBullet's COPY of
            // the spread, not the spread.
            //
            // The same ScriptedRng(0f) that used to prove 40 now proves 28: 0.0 * 0.6 + 0.7 = 0.7.
            var system = MakeImmediate(new ScriptedRng(0f), testDamage: false);
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Explosion(
                Context(explosionDamage: 40f),
                target,
                targetPosition: Vector3.zero,
                explosionCentre: Vector3.zero,
                explosionRadius: 100f));

            Assert.AreEqual(72f, target.Health, 1e-4f, "40 * 0.7 — a blast takes the spread");
        }

        [Test]
        public void Explosion_FalloffIsAPlateauNotALine()
        {
            // The defect no existing fixture could see. `Clamp01(1 - d/r)` and the oracle's
            // `d <= r/2 ? 1 : 2 - 2d/r` agree at d = 0 and at d = r — which is where every explosion
            // test sat — and disagree everywhere in between, by up to a factor of two. So the midpoint
            // is the only place that pins the shape, and that is why the old curve could be wrong with
            // a green suite.
            //
            // d = r/2 is the seam: the oracle is still at full strength there (`d > r*0.5` is false),
            // and one step further out it has already started to ramp.
            var system = MakeImmediate(testDamage: true);   // deterministic: the spread reports 1

            // Exactly at the plateau edge — full strength. A linear curve would give 20, not 40.
            var atHalf = new FakeTarget { Health = 100f };
            system.Report(PendingDamage.Explosion(
                Context(explosionDamage: 40f), atHalf,
                targetPosition: new Vector3(5f, 0f, 0f),
                explosionCentre: Vector3.zero,
                explosionRadius: 10f));

            // Three quarters of the way out — half strength. The control: both curves agree here, so a
            // failure at atHalf but not here is unambiguously the plateau.
            var atThreeQuarters = new FakeTarget { Health = 100f };
            system.Report(PendingDamage.Explosion(
                Context(explosionDamage: 40f), atThreeQuarters,
                targetPosition: new Vector3(7.5f, 0f, 0f),
                explosionCentre: Vector3.zero,
                explosionRadius: 10f));

            Assert.AreEqual(60f, atHalf.Health, 1e-4f,
                "d = r/2 is INSIDE the plateau, so full 40. A linear curve gives 20.");
            Assert.AreEqual(80f, atThreeQuarters.Health, 1e-4f,
                "d = 3r/4 gives 2 - 1.5 = 0.5, so 20 — the control both curves satisfy.");
        }

        [Test]
        public void DamageSpread_IsSkippedForAZeroDamageShot_AndTakesNoDraw()
        {
            // AS3's spread sits inside `if (param1.damage > 0)` (:4079), so a zero-damage shot exits at
            // `return 0` before Math.random() is reached. The draw count is the assertion, because the
            // hp number is 0 either way — and an extra draw would shift the shared combat stream for
            // every later hit in the tick.
            var rng    = new ScriptedRng(0f);
            var system = MakeImmediate(rng, testDamage: false);
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Direct(Context(baseDamage: 0f), target, Vector3.zero));

            Assert.AreEqual(100f, target.Health, 1e-4f);
            Assert.AreEqual(0, system.ResolvedCount);
            Assert.AreEqual(0, rng.RollsConsumed, "no damage ⇒ no spread ⇒ no draw");
        }

        [Test]
        public void TestDamageToggle_ChangesTheNumberButNotTheStream()
        {
            // AS3's World.w.testDam discards the rolled value on the line AFTER the roll (:4085-4089),
            // so the draw still happens. This is the guard against "optimising" the draw away when the
            // toggle is on — which would be a replication bug on a shared stream, not a debug nicety.
            //
            // The count is 2: the spread, and the knockback jitter. `testDam` discards the spread's
            // VALUE and nothing else — it does not reach `otbros`, which draws unconditionally at :4251.
            var offRng = new ScriptedRng(0f);
            var off    = MakeImmediate(offRng, testDamage: false);
            var offTarget = new FakeTarget { Health = 100f };
            off.Report(PendingDamage.Direct(Context(baseDamage: 10f), offTarget, Vector3.zero));

            var onRng = new ScriptedRng(0f);
            var on    = MakeImmediate(onRng, testDamage: true);
            var onTarget = new FakeTarget { Health = 100f };
            on.Report(PendingDamage.Direct(Context(baseDamage: 10f), onTarget, Vector3.zero));

            Assert.AreEqual(93f, offTarget.Health, 1e-4f, "spread on: 10 * 0.7");
            Assert.AreEqual(90f, onTarget.Health, 1e-4f, "spread discarded: the listed 10");

            Assert.AreEqual(offRng.RollsConsumed, onRng.RollsConsumed,
                "the toggle must not change how many draws the hit takes");
            Assert.AreEqual(2, onRng.RollsConsumed,
                "the spread and the knockback jitter — the toggle discards the spread's value, not its draw");
        }

        // ── The call path (PendingDamage.ReachedDamageWithoutUdarBullet) ─────
        //
        // AS3 has two damage entry shapes and the difference is not cosmetic. `udarBullet`
        // (Unit.as:4067) owns exactly three rolls — the avoidance conjunction (:4072), the damage
        // spread (:4085), and the `otbros` throw (:4091) — and anything that reaches `damage()`
        // without passing through it owns none of those three. `PendingDamage.Contact` (AS3
        // `Unit.udarBox`, :4237) is that second shape, and the flag is the predicate that names it.
        //
        // Careful: "owns none of udarBullet's three rolls" is NOT the same as "takes no spread".
        // AS3 repeats the spread verbatim at `explGas():763`, so a blast skips udarBullet's copy and
        // takes the other one — and the `explTip 1` shape takes udarBullet's copy anyway, through the
        // child bullet `explBullet()` spawns. That is why the variance gate asks a separate question
        // (`PendingDamage.SkipsDamageVariance`) and why the only path with no spread anywhere is a
        // prop impact. Tests for both facts are below.
        //
        // Each of the three gates has a test below, and each is paired with the `Direct` control for
        // the same hit, because "took no draw" only means something next to "and this is what the draw
        // would have cost". The knockback gate is deliberately tested here rather than in the knockback
        // section: it is the same fact as the other two, not a fourth rule.

        [Test]
        public void Contact_IsNotABlast_AndReadsBaseDamage()
        {
            // The shape of the factory, and the reason the flag had to be a flag rather than a reuse of
            // IsExplosion. A contact hit that claimed to be a blast would also switch its damage source
            // to ExplosionDamage and take distance falloff — so a crate impact would resolve as
            // falloff-scaled zero instead of its vel2 number, which is a much quieter bug than it looks.
            var system = MakeImmediate(testDamage: false);
            var target = new FakeTarget { Health = 100f };

            var hit = PendingDamage.Contact(
                DamageContext.Contact(25f, DamageType.PhysicalMelee), target, Vector3.zero);

            Assert.IsFalse(hit.IsExplosion,
                "a prop impact is not a blast: it reads BaseDamage and takes no falloff");
            Assert.IsTrue(hit.ReachedDamageWithoutUdarBullet,
                "and it is the second entry shape, so it takes none of udarBullet's three rolls");
            Assert.IsTrue(hit.SkipsDamageVariance,
                "udarBox has no spread of its own, so this is the one path that skips the spread too");
            Assert.AreEqual(25f, hit.Context.BaseDamage, 1e-4f);
            Assert.AreEqual(0f, hit.Context.ExplosionDamage, 1e-4f);
            Assert.AreEqual(0f, hit.Context.Knockback, 1e-4f,
                "udarBox never calls otbros, so a contact context carries no knock");

            system.Report(hit);

            Assert.AreEqual(75f, target.Health, 1e-4f, "25 lands whole — no spread, no falloff");
            Assert.AreEqual(1, system.ResolvedCount);
        }

        [Test]
        public void Contact_TakesNoDrawAtAll_NeitherSpreadNorKnockback()
        {
            // Two of the three gates in one assertion. `otbros` has exactly ONE call site in the whole
            // oracle — Unit.as:4091, inside udarBullet, one line after `this.damage()` — so "no
            // udarBullet" and "no throw" are the same fact rather than two rules that happen to agree.
            // Gating the throw on IsExplosion, which is what the code did before a second
            // non-udarBullet caller existed, gave the right answer for blasts and silently drew an extra
            // roll for every crate impact. Nothing visible would have exposed it: the impulse it then
            // produced was zero, so the damage was identical and only the shared stream had moved.
            var contactRng = new ScriptedRng();
            var contact    = MakeImmediate(contactRng, testDamage: false);
            contact.Report(PendingDamage.Contact(
                Context(baseDamage: 40f), new FakeTarget { Health = 100f }, Vector3.zero));

            var directRng = new ScriptedRng();
            var direct    = MakeImmediate(directRng, testDamage: false);
            direct.Report(PendingDamage.Direct(
                Context(baseDamage: 40f), new FakeTarget { Health = 100f }, Vector3.zero));

            Assert.AreEqual(0, contactRng.RollsConsumed,
                "a contact hit draws nothing at all: no spread and no knockback jitter");
            Assert.AreEqual(2, directRng.RollsConsumed,
                "a direct hit draws both — the control that makes the zero above mean something");
        }

        [Test]
        public void Contact_DoesNotApplyTheSpread_ButDirectDoes()
        {
            // The spread's EFFECT, not merely its draw. A scripted 0.0 gives the oracle's range floor,
            // 0.0 * 0.6 + 0.7 = 0.7, so a 40-damage crate impact that leaked into the spread would land
            // 28 — a permanent ±30% swing on every crate, where the oracle is deterministic.
            var contact       = MakeImmediate(new ScriptedRng(0f), testDamage: false);
            var contactTarget = new FakeTarget { Health = 100f };
            contact.Report(PendingDamage.Contact(Context(baseDamage: 40f), contactTarget, Vector3.zero));

            var direct       = MakeImmediate(new ScriptedRng(0f), testDamage: false);
            var directTarget = new FakeTarget { Health = 100f };
            direct.Report(PendingDamage.Direct(Context(baseDamage: 40f), directTarget, Vector3.zero));

            Assert.AreEqual(60f, contactTarget.Health, 1e-4f,
                "40 exactly — udarBox hands its number straight to damage()");
            Assert.AreEqual(72f, directTarget.Health, 1e-4f,
                "40 * 0.7 — the shot path spreads, and this is the difference being asserted");
        }

        [Test]
        public void Contact_TakesNoAvoidanceRoll_SoAnUnderSkilledSwingStillLands()
        {
            // The third gate, and the one the flag is named for. `miss` is the attacker's under-skill
            // penalty, and 0.4 is the largest a real weapon can produce (HitAvoidance.SkillConfidence
            // answers 0.6 for a gap of 2), so this is a reachable context rather than a synthetic one.
            // A 0.3 roll fails `roll > 0.4`, so the identical context through Direct is evaded.
            var contactRng    = new ScriptedRng(0.3f);
            var contact       = MakeImmediate(contactRng, testDamage: false);
            var contactTarget = new FakeTarget { Health = 100f };

            contact.Report(PendingDamage.Contact(
                Context(baseDamage: 40f, missChance: 0.4f), contactTarget, Vector3.zero));

            Assert.AreEqual(0, contact.MissedCount, "the contact path never reaches the conjunction");
            Assert.AreEqual(60f, contactTarget.Health, 1e-4f, "so the hit lands whole");
            Assert.AreEqual(0, contactRng.RollsConsumed, "and takes no roll on the way");

            var directRng    = new ScriptedRng(0.3f);
            var direct       = MakeImmediate(directRng);
            var directTarget = new FakeTarget { Health = 100f };

            direct.Report(PendingDamage.Direct(
                Context(baseDamage: 40f, missChance: 0.4f), directTarget, Vector3.zero));

            Assert.AreEqual(1, direct.MissedCount, "the same context through Direct is evaded");
            Assert.AreEqual(100f, directTarget.Health, 1e-4f, "and deals nothing");
        }

        [Test]
        public void Contact_StillPaysTheArmourTerm_ItIsNotABypassOfTheWholePipeline()
        {
            // The counterweight to the three tests above, and the reason the flag is not called
            // "SkipDamage". The flag removes the three things udarBullet owns; it does NOT skip
            // `damage()`, whose body applies vulnerability, skin, armour and crit. `udarBox` calls
            // `damage(...)` at :4237 exactly as a bullet does, so a crate impact is reduced by a plate —
            // and a "contact hits ignore armour" reading would make crates the best weapon in the game.
            var system = MakeImmediate(testDamage: false);
            var target = new FakeTarget
            {
                Health = 100f,
                Armour = ArmourState.FromItem(100f, 100f, physicalRating: 20f, energyRating: 0f, reliability: 1f),
            };

            system.Report(PendingDamage.Contact(
                DamageContext.Contact(40f, DamageType.PhysicalMelee), target, Vector3.zero));

            Assert.AreEqual(80f, target.Health, 1e-4f, "40 - 20: the plate still bites");
            Assert.IsTrue(target.LastOutcome.ArmourReduced);
        }

        [Test]
        public void Explosion_TakesTheSpreadDrawButNoThrow()
        {
            // The regression guard, retargeted. The flag that means "reached damage() without
            // udarBullet" must keep suppressing the avoidance roll and the otbros throw for a blast —
            // a blast that suddenly took a knockback draw would shift every later roll in the same
            // tick. But it must NOT suppress the spread: the oracle applies that one to blasts too
            // (explGas:763), so exactly ONE draw is consumed, not zero.
            var rng    = new ScriptedRng(0f);
            var system = MakeImmediate(rng, testDamage: false);
            var target = new FakeTarget { Health = 100f };

            system.Report(PendingDamage.Explosion(
                Context(explosionDamage: 40f),
                target,
                targetPosition: Vector3.zero,
                explosionCentre: Vector3.zero,
                explosionRadius: 100f));

            Assert.AreEqual(72f, target.Health, 1e-4f, "40 at the centre, times the 0.7 spread");
            Assert.AreEqual(1, rng.RollsConsumed, "the spread, and only the spread");
            Assert.AreEqual(0, target.ApplyKnockbackCalls, "a blast does not reach otbros");
        }

        [Test]
        public void MeleeSwing_IsADirectHit_SoItStillThrows()
        {
            // The other half of that regression guard, and the one a careless fix for the contact case
            // would break. AS3's melee weapons are bullet-based — WClub.as:353, WPunch.as:49 and
            // WKick.as:48 all stamp `b.otbros` on a spawned bullet — so a melee swing does pass through
            // udarBullet and does throw. "Contact hits do not throw" must not be implemented as "nothing
            // without a projectile throws", which would silently disarm the entire melee branch.
            var rng    = new ScriptedRng(0f);
            var system = MakeImmediate(rng, testDamage: true);
            var target = new FakeTarget { Health = 100f, Knocked = 1f, Mass = 1f };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 10f, knockback: 3f, knockbackDir: Vector2.right, isMelee: true),
                target, Vector3.zero));

            Assert.AreEqual(1, target.ApplyKnockbackCalls,
                "a melee swing reaches otbros through udarBullet, so it still throws");
            Assert.AreEqual(3f * 0.8f * TileQueryConstants.PerFrameVelocityToUnitsPerSecond,
                target.LastKnockbackImpulse.x, 1e-4f);
        }

        // ── Knockback units (Unit.otbros, Unit.as:4242-4258) ─────────────────

        [Test]
        public void KnockbackImpulse_IsConvertedFromPixelsPerFrameToUnitsPerSecond()
        {
            // The play-test report was "bullets can push back target through wall and push really hard
            // (i use minigun)". Half of that was the missing horizontal collision; the other half is
            // this conversion, and this test is the guard that was absent.
            //
            // `DamageContext.Knockback` is AS3's `otbros` — a per-FRAME pixel velocity, because
            // `Unit.otbros()` adds it to `dx`, which `Unit.run()` adds to `X` once per frame. What
            // `ApplyKnockback` hands the unit is `UnitController._velocity`, which is Unity units per
            // SECOND (Move multiplies by Time.fixedDeltaTime). Omitting the factor made every shot
            // 1/0.3 = 3.33× too strong — not a tuning miss but a unit mismatch.
            //
            // A scripted 0.0 makes the oracle's `random * 0.4 + 0.8` come out at exactly 0.8, and a
            // `knocked` of 1 over `massa` of 1 leaves the ratio at 1, so the expected impulse is
            // knock * 0.8 * 0.3 with no hidden arithmetic.
            var rng = new ScriptedRng(0f);
            var system = MakeImmediate(rng, testDamage: true);
            var target = new FakeTarget { Health = 100f, Knocked = 1f, Mass = 1f };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 10f, knockback: 3f, knockbackDir: Vector2.right),
                target, Vector3.zero));

            Assert.AreEqual(1, target.ApplyKnockbackCalls);

            float expected = 3f * 0.8f * TileQueryConstants.PerFrameVelocityToUnitsPerSecond;
            Assert.AreEqual(expected, target.LastKnockbackImpulse.x, 1e-4f,
                "3 px/frame at the minimum roll is 3 * 0.8 * 0.3 = 0.72 units/s, not 2.4");

            Assert.AreEqual(0f, target.LastKnockbackImpulse.y, 1e-6f,
                "the direction is stamped by the caller and carried through unchanged");
        }

        [Test]
        public void KnockbackImpulse_UsesTheVelocityFactor_NotTheAccelerationOne()
        {
            // The two factors are 0.3 and 9 — a factor of 30 apart — and the port has shipped the wrong
            // one for a velocity before (see TileQueryConstants' own remarks). Pinning the magnitude
            // against both constants makes "which one" explicit rather than incidental.
            var rng = new ScriptedRng(0f);
            var system = MakeImmediate(rng, testDamage: true);
            var target = new FakeTarget { Health = 100f, Knocked = 1f, Mass = 1f };

            system.Report(PendingDamage.Direct(
                Context(baseDamage: 10f, knockback: 1f, knockbackDir: Vector2.right),
                target, Vector3.zero));

            float velocityBased     = 1f * 0.8f * TileQueryConstants.PerFrameVelocityToUnitsPerSecond;
            float accelerationBased = 1f * 0.8f * TileQueryConstants.PerFrameAccelerationToUnitsPerSecondSquared;

            float applied = target.LastKnockbackImpulse.x;

            Assert.AreEqual(velocityBased, applied, 1e-4f);
            Assert.Greater(Mathf.Abs(applied - accelerationBased), 1f,
                "a per-frame velocity needs x fps / px-scale (0.3); the x fps^2 factor (9) is 30x too strong");
        }
    }
}
