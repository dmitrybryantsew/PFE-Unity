namespace PFE.Tests.Editor.Core
{
    using System.Collections.Generic;
    using System.Reflection;
    using NUnit.Framework;
    using PFE.Core;
    using UnityEngine;

    /// <summary>
    /// Covers the fixed-step accumulator, the catch-up budget and consumer ordering.
    ///
    /// <para>Every test drives <see cref="SimLoop.Advance"/> with an explicit delta rather than
    /// <see cref="SimLoop.Tick"/>, so none of this depends on the engine clock or a running player
    /// loop. <see cref="SimLoop.StepOnce"/> and <see cref="SimLoop.Advance"/> touch no Unity time.</para>
    /// </summary>
    [TestFixture]
    public class SimLoopTests
    {
        private const float Display60 = 1f / 60f;
        private const float Display144 = 1f / 144f;

        // ── The load-bearing invariant ───────────────────────────────────────

        [Test]
        public void OneSecondOfWallTime_AdvancesThirtyCanonicalFrames_AtEveryTickRate()
        {
            // This is the whole point of P1. Constants are canonical px-per-30Hz-frame, so one second
            // of wall time must advance exactly 30 canonical frames of simulation no matter what tick
            // rate is configured. If this fails, game speed depends on the tick rate (or the monitor)
            // and every physics constant becomes meaningless.
            foreach (int rate in SimClock.SupportedTicksPerSecond)
            {
                SimLoop loop = MakeLoop(rate);
                var clock = loop.Clock;

                for (int frame = 0; frame < 60; frame++)
                {
                    loop.Advance(Display60);
                }

                int ticks = loop.TickIndex + 1;
                float canonicalFramesAdvanced = ticks * clock.StepScale;

                Assert.That(canonicalFramesAdvanced, Is.EqualTo(30f).Within(1f),
                    $"at {rate} Hz, one second of wall time advanced {canonicalFramesAdvanced} " +
                    "canonical frames; it must always be 30");
            }
        }

        [Test]
        public void TickCount_TracksConfiguredRate_OverOneSecond()
        {
            foreach (int rate in SimClock.SupportedTicksPerSecond)
            {
                SimLoop loop = MakeLoop(rate);

                for (int frame = 0; frame < 60; frame++)
                {
                    loop.Advance(Display60);
                }

                Assert.That(loop.TickIndex + 1, Is.EqualTo(rate).Within(1),
                    $"a {rate} Hz clock should have run about {rate} ticks in one second");
            }
        }

        // ── Accumulator behaviour ────────────────────────────────────────────

        [Test]
        public void StepOnce_FirstTickIsIndexZero()
        {
            SimLoop loop = MakeLoop(30);

            Assert.That(loop.TickIndex, Is.EqualTo(-1), "no tick has run before the first step");
            Assert.That(loop.StepOnce(), Is.EqualTo(0));
            Assert.That(loop.StepOnce(), Is.EqualTo(1));
        }

        [Test]
        public void CanonicalClock_RunsExactlyOneTickPerCanonicalStep()
        {
            SimLoop loop = MakeLoop(30);

            for (int i = 0; i < 10; i++)
            {
                loop.Advance(1f / 30f);
                Assert.That(loop.Metrics.TicksThisFrame, Is.EqualTo(1),
                    "one canonical step of wall time is exactly one tick");
            }

            Assert.That(loop.TickIndex, Is.EqualTo(9));
        }

        [Test]
        public void CanonicalClock_OnSixtyHzDisplay_IdlesOnRoughlyHalfTheFrames()
        {
            // The display rate is decoupled from the sim rate: at a 60 Hz display and a 30 Hz sim,
            // about half the rendered frames run no tick at all. A sim that ticks on every rendered
            // frame is exactly the bug P1 exists to fix, so this asserts the idle frames happen.
            // Counted over a window rather than per-frame, because whether the second 1/60 s lands
            // exactly on a 1/30 s boundary is a float-rounding coin flip.
            SimLoop loop = MakeLoop(30);
            int framesWithTicks = 0;

            for (int frame = 0; frame < 60; frame++)
            {
                loop.Advance(Display60);
                if (loop.Metrics.TicksThisFrame > 0)
                {
                    framesWithTicks++;
                }
            }

            Assert.That(loop.TickIndex + 1, Is.EqualTo(30).Within(1));
            Assert.That(framesWithTicks, Is.EqualTo(30).Within(1),
                "a 30 Hz sim on a 60 Hz display must idle on about half the frames");
        }

        [Test]
        public void HighRefreshDisplay_DoesNotSpeedUpTheSimulation()
        {
            // 144 Hz display, 30 Hz sim: 144 frames of 1/144 s is still one second, so still 30 ticks.
            SimLoop loop = MakeLoop(30);

            for (int frame = 0; frame < 144; frame++)
            {
                loop.Advance(Display144);
            }

            Assert.That(loop.TickIndex + 1, Is.EqualTo(30).Within(1),
                "a 144 Hz monitor must not make the game run ~4.8x fast");
        }

        // ── Catch-up budget and spiral guard ─────────────────────────────────

        [Test]
        public void SingleLongHitch_IsCappedAtMaxCatchupTicks()
        {
            SimLoop loop = MakeLoop(30);

            loop.Advance(1f); // one whole second in a single frame

            Assert.That(loop.Metrics.TicksThisFrame, Is.EqualTo(SimClock.MaxCatchupTicks),
                "a hitch must not queue an unbounded burst of ticks");
        }

        [Test]
        public void AdversarialInput_NeverExceedsBudget_AndNeverDropsTicks()
        {
            // The input clamp alone is sufficient: accumulate at most one budget, drain up to one
            // budget, so the residue can never exceed the budget. That is why the second-half drop
            // guard in SimLoop.Advance is unreachable, and why dropped must stay 0 here.
            SimLoop loop = MakeLoop(30);
            float budget = loop.Clock.SimDt * SimClock.MaxCatchupTicks;

            for (int i = 0; i < 200; i++)
            {
                loop.Advance(i % 2 == 0 ? 10f : 0.0001f);

                Assert.That(loop.Metrics.Accumulator, Is.LessThanOrEqualTo(budget + 1e-4f),
                    "the accumulator must never hold more than the catch-up budget");
            }

            Assert.That(loop.Metrics.DroppedTicks, Is.EqualTo(0),
                "the input clamp should make dropping unreachable; a non-zero count means a real overload");
        }

        [Test]
        public void ZeroAndNegativeDeltas_DoNotTick()
        {
            SimLoop loop = MakeLoop(30);

            loop.Advance(0f);
            loop.Advance(-1f);

            Assert.That(loop.TickIndex, Is.EqualTo(-1));
            Assert.That(loop.Metrics.Accumulator, Is.EqualTo(0f).Within(1e-6f));
        }

        // ── Consumer ordering ────────────────────────────────────────────────

        [Test]
        public void Consumers_RunInAscendingTickOrder_RegardlessOfRegistrationOrder()
        {
            SimLoop loop = MakeLoop(30);
            var log = new List<string>();

            loop.Register(new OrderRecorder(log, "triggers", SimTickOrder.Triggers));
            loop.Register(new OrderRecorder(log, "input", SimTickOrder.Input));
            loop.Register(new OrderRecorder(log, "motor", SimTickOrder.PlayerMotor));

            loop.StepOnce();

            Assert.That(log, Is.EqualTo(new[] { "input", "motor", "triggers" }),
                "a tick must run input before movement and movement before triggers");
        }

        [Test]
        public void EqualTickOrder_FallsBackToRegistrationOrder()
        {
            // List.Sort is not stable, so the tie-break is explicit. Without it the order would be
            // unspecified and a tick could produce different results between runs.
            SimLoop loop = MakeLoop(30);
            var log = new List<string>();

            loop.Register(new OrderRecorder(log, "first", SimTickOrder.PlayerMotor));
            loop.Register(new OrderRecorder(log, "second", SimTickOrder.PlayerMotor));
            loop.Register(new OrderRecorder(log, "third", SimTickOrder.PlayerMotor));

            loop.StepOnce();

            Assert.That(log, Is.EqualTo(new[] { "first", "second", "third" }));
        }

        [Test]
        public void Consumers_ReceiveTheSameTickIndexWithinATick()
        {
            SimLoop loop = MakeLoop(30);
            var seen = new List<int>();

            loop.Register(new IndexRecorder(seen, SimTickOrder.Input));
            loop.Register(new IndexRecorder(seen, SimTickOrder.SpawnDespawn));

            loop.StepOnce();
            loop.StepOnce();

            Assert.That(seen, Is.EqualTo(new[] { 0, 0, 1, 1 }),
                "every consumer in a tick sees the same index, and it advances between ticks");
        }

        [Test]
        public void Register_IsIdempotentAndNullSafe()
        {
            SimLoop loop = MakeLoop(30);

            loop.Register(null);
            Assert.That(loop.TickableCount, Is.EqualTo(0), "a null consumer must be ignored");

            var consumer = new OrderRecorder(new List<string>(), "once", SimTickOrder.Input);
            loop.Register(consumer);
            loop.Register(consumer);

            Assert.That(loop.TickableCount, Is.EqualTo(1), "double registration must not double-tick");
        }

        [Test]
        public void Unregister_StopsDelivery()
        {
            SimLoop loop = MakeLoop(30);
            var log = new List<string>();
            var consumer = new OrderRecorder(log, "a", SimTickOrder.Input);

            loop.Register(consumer);
            loop.StepOnce();
            Assert.That(loop.Unregister(consumer), Is.True);

            loop.StepOnce();

            Assert.That(log, Is.EqualTo(new[] { "a" }), "an unregistered consumer must not tick again");
            Assert.That(loop.Unregister(consumer), Is.False);
        }

        // ── Dispatch gate and interpolation ──────────────────────────────────

        [Test]
        public void DispatchDisabled_DoesNotAdvanceTickIndex_OrAccumulate()
        {
            SimLoop loop = MakeLoop(30, simTickEnabled: false);

            for (int i = 0; i < 60; i++)
            {
                loop.Advance(Display60);
            }

            Assert.That(loop.TickIndex, Is.EqualTo(-1), "the gate must stop ticks entirely");
            Assert.That(loop.Metrics.Accumulator, Is.EqualTo(0f).Within(1e-6f),
                "a disabled sim must not bank a backlog it would burst on re-enable");
            Assert.That(loop.Metrics.DroppedTicks, Is.EqualTo(0),
                "a disabled sim must not report phantom drops");
        }

        [Test]
        public void Alpha_StaysInUnitRange_AndSweepsWithPartialAccumulation()
        {
            SimLoop loop = MakeLoop(30);

            loop.Advance(0f);
            Assert.That(loop.Alpha, Is.EqualTo(0f).Within(1e-5f));

            // Half a canonical step leaves the accumulator at half a step, so alpha is ~0.5.
            loop.Advance(1f / 60f);
            Assert.That(loop.Alpha, Is.EqualTo(0.5f).Within(1e-3f));

            // A full step drains to zero.
            loop.Advance(1f / 60f);
            Assert.That(loop.Alpha, Is.EqualTo(0f).Within(1e-3f));

            for (int i = 0; i < 300; i++)
            {
                loop.Advance(Display144);
                Assert.That(loop.Alpha, Is.InRange(0f, 1f));
            }
        }

        [Test]
        public void TickIndex_IsMonotonicAndContiguous()
        {
            // 120 frames of 1/60 s is two seconds of wall time, so a 60 Hz clock must have run
            // 120 ticks by the end. Over two seconds rather than one purely so there are more
            // indices to check contiguity across.
            SimLoop loop = MakeLoop(60);
            var seen = new List<int>();
            loop.Register(new IndexRecorder(seen, SimTickOrder.Input));

            for (int frame = 0; frame < 120; frame++)
            {
                loop.Advance(Display60);
            }

            Assert.That(seen.Count, Is.EqualTo(120).Within(1), "a 60 Hz clock over two seconds");

            for (int i = 0; i < seen.Count; i++)
            {
                Assert.That(seen[i], Is.EqualTo(i),
                    "tick indices must be contiguous and start at 0; a gap means a tick was skipped");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static SimLoop MakeLoop(int ticksPerSecond, bool simTickEnabled = true)
        {
            return new SimLoop(new SimClock(ticksPerSecond), MakeSettings(simTickEnabled));
        }

        /// <summary>
        /// Builds a settings asset with the sim gate set explicitly. The field is private and
        /// serialized, so it is set reflectively — the same way Unity's serializer would.
        /// </summary>
        private static PfeDebugSettings MakeSettings(bool simTickEnabled)
        {
            var settings = ScriptableObject.CreateInstance<PfeDebugSettings>();

            FieldInfo field = typeof(PfeDebugSettings).GetField(
                "simTickEnabled", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(field, Is.Not.Null,
                "PfeDebugSettings.simTickEnabled was renamed; update this helper");

            field.SetValue(settings, simTickEnabled);
            return settings;
        }

        private sealed class OrderRecorder : ISimTickable
        {
            private readonly List<string> _log;
            private readonly string _name;

            public OrderRecorder(List<string> log, string name, int tickOrder)
            {
                _log = log;
                _name = name;
                TickOrder = tickOrder;
            }

            public int TickOrder { get; }

            public void SimTick(int tickIndex)
            {
                _log.Add(_name);
            }
        }

        private sealed class IndexRecorder : ISimTickable
        {
            private readonly List<int> _seen;

            public IndexRecorder(List<int> seen, int tickOrder)
            {
                _seen = seen;
                TickOrder = tickOrder;
            }

            public int TickOrder { get; }

            public void SimTick(int tickIndex)
            {
                _seen.Add(tickIndex);
            }
        }
    }
}
