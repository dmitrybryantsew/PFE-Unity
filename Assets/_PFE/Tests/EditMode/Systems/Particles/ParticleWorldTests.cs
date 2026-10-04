using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core;
using PFE.Core.Rng;
using PFE.Systems.Particles;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins <see cref="ParticleWorld"/> — the live population: the two budgets as seen from the world's
    /// side, the unknown-id report, the water query, and the once-per-tick step.
    ///
    /// <para><b>Why this fixture can exist.</b> <see cref="ParticleWorld"/> is deliberately
    /// <i>Unity-free</i>: every collaborator it takes is an <c>int</c>-or-<c>float</c> seam
    /// (<see cref="IParticleSpriteFrames"/>, <see cref="IParticleTileWater"/>), a pure static
    /// (<see cref="ParticleRules"/>), or a plain interface (<see cref="IRngService"/>). So the whole thing
    /// is constructible from a bare <c>new</c> in a plain host, with no <c>MonoBehaviour</c>, no editor,
    /// and no <c>SimLoop</c>. That is the entire reason the seams are shaped the way they are — the
    /// fixture is the payoff, not an afterthought.</para>
    ///
    /// <para><b>Every claim is paired with a control.</b> The interesting failures in this project have
    /// all been "a test passed because it never reached the code it meant to test", so a test that asserts
    /// a refusal is always written next to one that asserts the same call succeeds under a condition that
    /// should not refuse. An assertion of the form "X did not happen" with no positive partner is treated
    /// as unfalsifiable here.</para>
    /// </summary>
    [TestFixture]
    public class ParticleWorldTests
    {
        private const float Tolerance = 1e-4f;

        // ── Doubles ───────────────────────────────────────────────────────────

        /// <summary>
        /// A constant <see cref="IRngService"/>. A constant rather than a scripted queue on purpose: these
        /// tests are about the world's bookkeeping, and a scripted sequence would make each expectation
        /// depend on the exact roll order inside <c>SpawnOne</c>, which is <see cref="ParticleRulesTests"/>'
        /// subject and not this fixture's. Anything that depends on the roll is read back off the spawned
        /// state instead of predicted.
        /// </summary>
        private sealed class ConstantRng : IRngService
        {
            private readonly float _value;

            public ConstantRng(float value = 0.5f) => _value = value;

            public int RollsConsumed { get; private set; }

            public float NextFloat()
            {
                RollsConsumed++;
                return _value;
            }

            public IRngService GetStream(RngStream stream, int? salt = null) => this;
            public uint NextUInt() => 0u;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool Chance(float probability) => _value < probability;
            public void Shuffle<T>(IList<T> list) { }
        }

        /// <summary>
        /// Records every water query, and answers with a fixed value. The call <b>count</b> is the point:
        /// the world is documented to ask only when the row's <c>water=</c> is non-zero, and that is a
        /// behaviour claim — 112 of the 118 shipped rows have <c>water=0</c>, so an implementation that
        /// queried unconditionally would turn every particle into a tile lookup for nothing.
        /// </summary>
        private sealed class CountingTileWater : IParticleTileWater
        {
            private readonly int _value;

            public CountingTileWater(int value = 0) => _value = value;

            public int Calls { get; private set; }

            public int WaterAt(float x, float y)
            {
                Calls++;
                return _value;
            }
        }

        /// <summary>Frame counts, plus a record of which definitions were asked about.</summary>
        private sealed class RecordingFrames : IParticleSpriteFrames
        {
            public int Count = 4;
            public readonly List<string> Asked = new List<string>();

            public int FrameCount(ParticleDefinition definition)
            {
                Asked.Add(definition == null ? null : definition.Id);
                return Count;
            }
        }

        // ── Builders ──────────────────────────────────────────────────────────

        private static ParticleDefinition Def(string id, Action<ParticleDefinition> configure = null)
        {
            var def = new ParticleDefinition { Id = id, Vis = "visual_" + id };
            configure?.Invoke(def);
            return def;
        }

        private static ParticleWorld BuildWorld(
            IReadOnlyList<ParticleDefinition> definitions,
            IParticleSpriteFrames frames = null,
            IParticleTileWater tiles = null,
            IRngService rng = null) =>
            new ParticleWorld(
                new ParticleDefinitionTable(definitions),
                frames,
                tiles,
                rng ?? new ConstantRng());

        // ── Resolution and the unknown-id report ──────────────────────────────

        [Test]
        public void Has_IsTrueForARowAndFalseForAnythingElse()
        {
            ParticleWorld world = BuildWorld(new[] { Def("smoke") });

            Assert.That(world.Has("smoke"), Is.True);
            Assert.That(world.Has("smoke_"), Is.False, "no prefix matching");
            Assert.That(world.Has("Smoke"), Is.False, "the oracle's id lookup is case-sensitive");
            Assert.That(world.Has(""), Is.False);
            Assert.That(world.Has(null), Is.False);
        }

        [Test]
        public void Emit_KnownId_SpawnsAndReportsTrue()
        {
            ParticleWorld world = BuildWorld(new[] { Def("smoke") });

            Assert.That(world.Emit("smoke", 100f, 200f), Is.True);
            Assert.That(world.LiveCount, Is.EqualTo(1));
            Assert.That(world.SpawnedTotal, Is.EqualTo(1));
            Assert.That(world.Live[0].X, Is.EqualTo(100f).Within(Tolerance));
            Assert.That(world.Live[0].Y, Is.EqualTo(200f).Within(Tolerance));
        }

        [Test]
        public void Emit_UnknownId_RecordsTheIdAndReturnsFalse()
        {
            ParticleWorld world = BuildWorld(new[] { Def("smoke") });

            Assert.That(world.Emit("typo", 0f, 0f), Is.False, "the oracle traces and carries on");
            Assert.That(world.LiveCount, Is.EqualTo(0));
            Assert.That(world.SpawnedTotal, Is.EqualTo(0), "a miss must not be counted as a spawn");
            Assert.That(world.UnknownEmitTotal, Is.EqualTo(1));
            Assert.That(world.UnknownIds, Is.EquivalentTo(new[] { "typo" }));
        }

        [Test]
        public void UnknownIds_AreDeduplicatedAndKeptInFirstSeenOrder()
        {
            ParticleWorld world = BuildWorld(Array.Empty<ParticleDefinition>());

            world.Emit("b", 0f, 0f);
            world.Emit("a", 0f, 0f);
            world.Emit("b", 0f, 0f);

            Assert.That(world.UnknownIds, Is.EqualTo(new[] { "b", "a" }),
                "the report is a set for reading, not a log to scan");
            Assert.That(world.UnknownEmitTotal, Is.EqualTo(3),
                "the tally counts every miss, including repeats");
        }

        [Test]
        public void Emit_EmptyOrNullId_IsRejectedWithoutPollutingTheUnknownReport()
        {
            ParticleWorld world = BuildWorld(new[] { Def("smoke") });

            Assert.That(world.Emit("", 0f, 0f), Is.False);
            Assert.That(world.Emit(null, 0f, 0f), Is.False);

            // A null id is a caller bug, not an unmapped art id. Mixing the two would bury the real
            // finding — "this id has no art" — under a stream of "this call site passed nothing".
            Assert.That(world.UnknownIds, Is.Empty);
            Assert.That(world.UnknownEmitTotal, Is.EqualTo(0));
        }

        [Test]
        public void Emit_ResolvesTheDefinitionBeforeAskingForArt()
        {
            var frames = new RecordingFrames();
            ParticleWorld world = BuildWorld(new[] { Def("smoke") }, frames);

            world.Emit("smoke", 0f, 0f);

            Assert.That(frames.Asked, Is.EqualTo(new[] { "smoke" }),
                "the frame count must come from the resolved row, not from the id string");
        }

        [Test]
        public void Emit_UnknownId_NeverAsksForArt()
        {
            var frames = new RecordingFrames();
            ParticleWorld world = BuildWorld(new[] { Def("smoke") }, frames);

            world.Emit("typo", 0f, 0f);

            Assert.That(frames.Asked, Is.Empty, "a miss must not reach the sprite seam");
        }

        // ── The two budgets, and telling a refusal from a miss ────────────────

        [Test]
        public void Emit_BeyondTheTypeCap_IsRefusedAndIsNotRecordedAsAnUnknownId()
        {
            // maxkol=1 is the acid-spray shape: a hard cap of 12 live particles of this type.
            ParticleWorld world = BuildWorld(new[] { Def("spray", d => d.MaxKol = 1) });

            for (int i = 0; i < ParticleBudget.TypeConcurrencyCap; i++)
            {
                Assert.That(world.Emit("spray", 0f, 0f), Is.True, $"particle {i + 1} is within the cap");
            }

            Assert.That(world.Budget.LiveOfType(1), Is.EqualTo(ParticleBudget.TypeConcurrencyCap));

            // The control for the refusal below: this is the same call, one particle too late.
            Assert.That(world.Emit("spray", 0f, 0f), Is.False);

            Assert.That(world.Budget.DroppedByTypeBudget, Is.EqualTo(1));
            Assert.That(world.Budget.DroppedByGlobalBudget, Is.EqualTo(0),
                "the two counters are separate, so the refusal names its own cause");
            Assert.That(world.UnknownIds, Is.Empty,
                "a budget refusal is not a missing definition — conflating them is the failure mode this split exists to prevent");
            Assert.That(world.UnknownEmitTotal, Is.EqualTo(0));
        }

        [Test]
        public void Emit_PastTheGlobalCeiling_IsRefused_UnlessTheRowIsImp()
        {
            ParticleWorld world = BuildWorld(new[] { Def("smoke"), Def("boom", d => d.Imp = 1) });

            // The global gate reads kol2, which is the PREVIOUS tick's stepped count. One spawn plus two
            // ticks is the shortest way to put a non-zero value there.
            world.Emit("smoke", 0f, 0f);
            world.SimTick(0);
            world.SimTick(1);
            Assert.That(world.Budget.Kol2, Is.GreaterThan(0), "arrange: the ceiling now has something to compare against");

            world.Budget.MaxParts = 0;

            Assert.That(world.Emit("smoke", 0f, 0f), Is.False, "kol2 > maxParts refuses the cast outright");
            Assert.That(world.Budget.DroppedByGlobalBudget, Is.EqualTo(1));

            // The control: `imp != 0` is the oracle's exemption (Emitter.as:167), so the same ceiling
            // must not stop this row. Without this assertion the test would pass on an implementation
            // that refused everything.
            Assert.That(world.Emit("boom", 0f, 0f), Is.True, "imp=1 bypasses the global ceiling");
        }

        [Test]
        public void Emit_WithKol_SpawnsTheBatchAndReportsEveryParticle()
        {
            ParticleWorld world = BuildWorld(new[] { Def("spark") });

            var spec = new ParticleSpec { Kol = 5 };
            Assert.That(world.Emit("spark", 0f, 0f, spec), Is.True);

            Assert.That(world.LiveCount, Is.EqualTo(5));
            Assert.That(world.SpawnedTotal, Is.EqualTo(5), "the total counts particles, not calls");
            Assert.That(spec.Kol, Is.EqualTo(5), "the caller's spec must not be mutated");
        }

        // ── Stepping ──────────────────────────────────────────────────────────

        [Test]
        public void Step_AdvancesAParticleByExactlyItsOwnVelocity()
        {
            ParticleWorld world = BuildWorld(new[] { Def("smoke", d => { d.MinV = 10f; d.RV = 0f; }) });
            world.Emit("smoke", 100f, 200f);

            ParticleState before = world.Live[0];
            Assert.That(before.IsMove, Is.True, "arrange: the row must actually have a velocity");

            world.SimTick(0);

            ParticleState after = world.Live[0];
            Assert.That(after.X, Is.EqualTo(before.X + before.DX).Within(Tolerance));
            Assert.That(after.Y, Is.EqualTo(before.Y + before.DY).Within(Tolerance));
            Assert.That(after.Liv, Is.EqualTo(before.Liv - 1), "and it aged by exactly one tick");
        }

        [Test]
        public void Step_RemovesOnlyTheExpired_AndLeavesTheSurvivorsInSpawnOrder()
        {
            // MinLiv is exact here because the life roll is `floor(rand * rliv) + minliv` and rliv is 0.
            ParticleWorld world = BuildWorld(new[]
            {
                Def("short", d => d.MinLiv = 1),
                Def("mid"),
                Def("long"),
            });

            world.Emit("short", 1f, 0f);
            world.Emit("mid", 2f, 0f);
            world.Emit("long", 3f, 0f);
            Assert.That(world.LiveCount, Is.EqualTo(3));

            world.SimTick(0);

            Assert.That(world.LiveCount, Is.EqualTo(2));
            Assert.That(world.Live[0].DefinitionIndex, Is.EqualTo(1), "mid is now first");
            Assert.That(world.Live[1].DefinitionIndex, Is.EqualTo(2), "long still follows mid");

            // The control for the removal: one tick later the same two are still there, so the step
            // removed exactly the one that expired rather than clearing the list.
            world.SimTick(1);
            Assert.That(world.LiveCount, Is.EqualTo(2));
            Assert.That(world.Live[0].DefinitionIndex, Is.EqualTo(1));
        }

        [Test]
        public void Step_FreesTheTypeSlotWhenAParticleExpires()
        {
            ParticleWorld world = BuildWorld(new[] { Def("spray", d => { d.MaxKol = 1; d.MinLiv = 1; }) });

            world.Emit("spray", 0f, 0f);
            Assert.That(world.Budget.LiveOfType(1), Is.EqualTo(1), "the spawn claimed the slot");

            world.SimTick(0);

            Assert.That(world.LiveCount, Is.EqualTo(0));
            Assert.That(world.Budget.LiveOfType(1), Is.EqualTo(0),
                "Part.setNull releases the slot — otherwise a cap of 12 would be a lifetime total, not a concurrency limit");
        }

        // ── The water query ───────────────────────────────────────────────────

        [Test]
        public void Step_AsksForTileWaterOnlyWhenTheRowCarriesAWaterValue()
        {
            var tiles = new CountingTileWater(0);
            ParticleWorld world = BuildWorld(
                new[] { Def("dry"), Def("splash", d => d.Water = 2) }, tiles: tiles);

            world.Emit("dry", 0f, 0f);
            world.SimTick(0);
            Assert.That(tiles.Calls, Is.EqualTo(0), "water=0 rows are 112 of the 118 — they must not cost a tile lookup");

            world.Emit("splash", 0f, 0f);
            world.SimTick(1);
            Assert.That(tiles.Calls, Is.EqualTo(1), "the water=2 row does query");
        }

        [Test]
        public void Step_WaterOnlyParticleOnDryGround_DiesImmediately()
        {
            // The dry stub is not a silent no-op: `water=2` means "only in water", so on a dry tile the
            // oracle sets liv to 1 and the same tick's decrement kills it (Part.as:192-199).
            ParticleWorld world = BuildWorld(new[] { Def("splash", d => d.Water = 2) });
            world.Emit("splash", 0f, 0f);

            world.SimTick(0);

            Assert.That(world.LiveCount, Is.EqualTo(0), "a water=2 particle cannot survive out of water");
        }

        [Test]
        public void Step_DryOnlyParticleOnDryGround_SurvivesItsFullLife()
        {
            // The control for the test above, and the reason the stub is honest rather than arbitrary:
            // `water=1` is the opposite rule, so the same dry ground must let it live.
            ParticleWorld world = BuildWorld(new[] { Def("dust", d => d.Water = 1) });
            world.Emit("dust", 0f, 0f);

            for (int i = 0; i < 5; i++) world.SimTick(i);

            Assert.That(world.LiveCount, Is.EqualTo(1), "water=1 lives out of water");
            Assert.That(world.Live[0].Liv, Is.EqualTo(20 - 5), "and it aged normally");
        }

        // ── The tick shape ────────────────────────────────────────────────────

        [Test]
        public void TickOrder_SitsBeforeEveryBandThatEmits()
        {
            ParticleWorld world = BuildWorld(Array.Empty<ParticleDefinition>());

            Assert.That(world.TickOrder, Is.EqualTo(SimTickOrder.PreTick));

            // The whole reason for the band. AS3's Part is a MovieClip whose ENTER_FRAME fires the next
            // frame, so a part cast during a tick is not stepped until the following one. Emitters live
            // in these bands, so the step has to precede all of them or every fresh particle moves a tick
            // early. Pinning the relation rather than the number means a future renumber cannot silently
            // move the population after the emitters.
            Assert.That(world.TickOrder, Is.GreaterThan(SimTickOrder.Input));
            Assert.That(world.TickOrder, Is.LessThan(SimTickOrder.UnitsAndAi), "Effect.visEff / stepEffect");
            Assert.That(world.TickOrder, Is.LessThan(SimTickOrder.Projectiles), "Bullet.explVis / Projectile.Detonate");
            Assert.That(world.TickOrder, Is.LessThan(SimTickOrder.Damage), "Unit.damage blood");
        }

        [Test]
        public void SimTick_RollsTheBudgetRegisterBeforeItSteps()
        {
            ParticleWorld world = BuildWorld(new[] { Def("smoke") });
            world.Emit("smoke", 0f, 0f);

            // A particle emitted in this tick has not been stepped by it — the emitters run after this
            // world's slot, so nothing has counted yet.
            Assert.That(world.Budget.Kol1, Is.EqualTo(0));

            world.SimTick(0);

            // If the two halves of SimTick were in the wrong order the numbers below would be swapped:
            // Step-then-BeginTick would leave kol1 at 0 and kol2 at 1. This is the assertion that pins
            // `kol2 = kol1; kol1 = 0;` (World.as:1266-1267) ahead of the step.
            Assert.That(world.Budget.Kol1, Is.EqualTo(1), "this tick stepped one particle");
            Assert.That(world.Budget.Kol2, Is.EqualTo(0), "the roll read the previous tick, which was empty");

            world.SimTick(1);

            Assert.That(world.Budget.Kol1, Is.EqualTo(1));
            Assert.That(world.Budget.Kol2, Is.EqualTo(1), "the previous tick's count was carried forward");
        }

        [Test]
        public void Live_IsTheWorldsOwnList_NotACopyPerTick()
        {
            // Documented as a contract so a renderer knows it must not retain the reference. Asserted
            // because "returns a fresh list" looks harmless and would quietly allocate once per tick,
            // once per frame, for the rest of the project's life.
            ParticleWorld world = BuildWorld(new[] { Def("smoke") });
            world.Emit("smoke", 0f, 0f);

            IReadOnlyList<ParticleState> first = world.Live;
            world.SimTick(0);

            Assert.That(ReferenceEquals(first, world.Live), Is.True);
        }

        // ── Reset vs ClearUnknownIds ──────────────────────────────────────────

        [Test]
        public void Reset_ClearsThePopulationAndCounters_ButKeepsTheUnknownReport()
        {
            ParticleWorld world = BuildWorld(new[] { Def("smoke") });
            world.Emit("smoke", 0f, 0f);
            world.Emit("typo", 0f, 0f);

            Assert.That(world.LiveCount, Is.EqualTo(1));
            Assert.That(world.UnknownIds, Is.Not.Empty);

            world.Reset();

            Assert.That(world.LiveCount, Is.EqualTo(0), "a room change takes its particles with it");
            Assert.That(world.SpawnedTotal, Is.EqualTo(0));
            Assert.That(world.Budget.Kol1, Is.EqualTo(0));
            Assert.That(world.Budget.Kol2, Is.EqualTo(0));
            Assert.That(world.Budget.DroppedByGlobalBudget, Is.EqualTo(0));

            // The distinction that makes Reset and ClearUnknownIds two methods: a typo'd id reported once
            // must stay reported, otherwise the report resets on every transition and stops meaning
            // "this id has never resolved".
            Assert.That(world.UnknownIds, Is.EquivalentTo(new[] { "typo" }));
            Assert.That(world.UnknownEmitTotal, Is.EqualTo(0), "the session tally does restart");
        }

        [Test]
        public void ClearUnknownIds_ForgetsTheReport()
        {
            ParticleWorld world = BuildWorld(Array.Empty<ParticleDefinition>());
            world.Emit("typo", 0f, 0f);

            world.ClearUnknownIds();

            Assert.That(world.UnknownIds, Is.Empty);
            Assert.That(world.UnknownEmitTotal, Is.EqualTo(0));

            // Control: the report is still live afterwards, so this forgot the past rather than turned
            // the recording off.
            world.Emit("another", 0f, 0f);
            Assert.That(world.UnknownIds, Is.EquivalentTo(new[] { "another" }));
        }

        // ── Construction ──────────────────────────────────────────────────────

        [Test]
        public void Constructor_NullCollaborators_FallBackToEmptyDryAndNoArt()
        {
            var world = new ParticleWorld(null, null, null, new ConstantRng());

            Assert.That(world.Definitions.Count, Is.EqualTo(0));
            Assert.That(world.Emit("anything", 0f, 0f), Is.False);
            Assert.That(world.UnknownIds, Is.EquivalentTo(new[] { "anything" }),
                "an empty table must still report the miss rather than swallow it");
            Assert.That(world.LiveCount, Is.EqualTo(0));

            // The dry fallback is exercised rather than merely installed.
            world.SimTick(0);
            Assert.That(world.LiveCount, Is.EqualTo(0));
        }

        [Test]
        public void Constructor_NullRng_Throws()
        {
            // Deliberately not defaulted: a missing RNG is a container wiring bug, and a world that
            // silently stopped drawing jitter would present as a data problem.
            Assert.Throws<ArgumentNullException>(
                () => new ParticleWorld(null, null, null, null));
        }

        [Test]
        public void Constructor_UsesThePresentationRngStream()
        {
            // FX jitter must not be able to shift a combat roll. The world is documented to salt onto
            // Presentation; if it ever drew from the default stream this double would be asked for none.
            var rng = new StreamRecordingRng();
            var world = new ParticleWorld(new ParticleDefinitionTable(new[] { Def("smoke") }), null, null, rng);
            world.Emit("smoke", 0f, 0f);

            Assert.That(rng.RequestedStreams, Is.EqualTo(new[] { RngStream.Presentation }));
        }

        private sealed class StreamRecordingRng : IRngService
        {
            public readonly List<RngStream> RequestedStreams = new List<RngStream>();

            public IRngService GetStream(RngStream stream, int? salt = null)
            {
                RequestedStreams.Add(stream);
                return this;
            }

            public float NextFloat() => 0.5f;
            public uint NextUInt() => 0u;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool Chance(float probability) => false;
            public void Shuffle<T>(IList<T> list) { }
        }
    }
}
