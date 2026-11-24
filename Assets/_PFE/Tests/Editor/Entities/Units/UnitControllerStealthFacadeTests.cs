using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PFE.Core;
using PFE.Entities.Units;
using PFE.Sim.Units;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Thread J, Phase 2 — the forwarding facade <see cref="UnitController"/>'s
    /// stealth/noise/suspicion surface became when the record moved into
    /// <see cref="UnitWorld"/> (thread J, Phase 2).
    ///
    /// <para><b>The contract under test.</b> Through the public surface the two storage paths are
    /// indistinguishable: a unit on a loop WITH a world writes into its slot (and the slot mirrors
    /// every write — the property the future <c>UnitStealthSystem</c> iterates); a unit on a bare
    /// loop keeps the local fallback copy and answers identically. Same calls, same numbers, the
    /// only difference being where the bytes sit.</para>
    ///
    /// <para><b>Why a spy and reflection.</b> The ops run against a <c>SpyUnit</c> whose step body
    /// is empty, exactly like the drive-lane fixtures — and the one private the test reads is the
    /// handle field, because a facade that forwards to the wrong slot is precisely the bug this
    /// migration could introduce without any public symptom.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitControllerStealthFacadeTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            _spawned.Clear();
        }

        private sealed class SpyUnit : UnitController
        {
            protected override void StepUnit()
            {
            }
        }

        private static SimClock MakeClock(out SimLoop loop)
        {
            var clock = new SimClock(30);
            loop = new SimLoop(clock, null);
            return clock;
        }

        private SpyUnit MakeUnit(SimClock clock, SimLoop loop)
        {
            var go = new GameObject("SpyUnit");
            _spawned.Add(go);
            SpyUnit unit = go.AddComponent<SpyUnit>();
            unit.AttachSimulation(clock, loop);
            return unit;
        }

        private static UnitHandle StealthHandleOf(UnitController unit)
        {
            FieldInfo field = typeof(UnitController).GetField(
                "_stealthHandle", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "the facade's handle field is the seam this test reads");
            return (UnitHandle)field.GetValue(unit);
        }

        [Test]
        public void WorldPath_AttachAcquiresASlot_AndEveryFacadeWriteReachesIt()
        {
            SimClock clock = MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            SpyUnit unit = MakeUnit(clock, loop);

            Assert.That(world.LiveCount, Is.EqualTo(1),
                "attaching to a loop with a world must acquire the stealth slot");

            unit.MakeNoise(300, isEvent: true);
            unit.TickStealth(new Vector2(15f, 0f));
            float suspicion = unit.AddSuspicion(5f);
            unit.RaiseDemask(120f);

            Assert.That(unit.Noise, Is.EqualTo(300));
            Assert.That(unit.Demask, Is.EqualTo(200f), "a 15 px/frame step raises demask to the 200 cap");
            Assert.That(suspicion, Is.EqualTo(5f));

            UnitHandle handle = StealthHandleOf(unit);
            Assert.IsTrue(world.TryGet(handle, out UnitStealthData data),
                "the unit's handle must name its live slot");
            Assert.That(data.Noise, Is.EqualTo(300));
            Assert.That(data.NoiseRippleTicks, Is.EqualTo(NoiseMath.NoiseRippleTicks),
                "an event noise restarts the ripple cooldown");
            Assert.That(data.Suspicion.Value, Is.EqualTo(5f));
            Assert.That(data.Suspicion.HoldTicks, Is.GreaterThan(0));
            Assert.That(data.Stealth.Demask, Is.EqualTo(200f));
        }

        [Test]
        public void WorldPath_TickNoise_LandsEveryPieceInTheSlot()
        {
            SimClock clock = MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            SpyUnit unit = MakeUnit(clock, loop);

            unit.MakeNoise(300);
            unit.TickNoise(grounded: true, velocityPixelsPerFrame: Vector2.zero);
            // Decay alone: 300 - 20. The unit starts grounded, so no landing fires.
            Assert.That(unit.Noise, Is.EqualTo(280));

            unit.TickNoise(grounded: false, velocityPixelsPerFrame: new Vector2(0f, -40f));
            unit.TickNoise(grounded: true, velocityPixelsPerFrame: Vector2.zero);

            UnitHandle handle = StealthHandleOf(unit);
            Assert.IsTrue(world.TryGet(handle, out UnitStealthData data));
            Assert.That(data.NoiseWasGrounded, Is.True, "the landing edge has passed");
            Assert.That(data.NoiseFallSpeedPixelsPerFrame, Is.EqualTo(-40f),
                "the airborne step's downward speed is carried for the landing calc");
            int expected = NoiseMath.MakeNoise(260, NoiseMath.LandingNoise(
                NoiseMath.DefaultNoiseRun, -40f));
            expected = NoiseMath.TickNoise(expected);
            Assert.That(data.Noise, Is.EqualTo(expected),
                "landing event, then decay — the whole AS3 landing chain via the world slot");
        }

        [Test]
        public void LocalPath_LoopWithoutWorld_KeepsAnsweringWithoutASlot()
        {
            SimClock clock = MakeClock(out SimLoop loop);
            SpyUnit unit = MakeUnit(clock, loop);

            unit.MakeNoise(300, isEvent: true);
            unit.TickStealth(new Vector2(15f, 0f));
            unit.AddSuspicion(5f);
            unit.RaiseDemask(120f);
            unit.TickSuspicion();

            Assert.That(unit.Noise, Is.EqualTo(300));
            Assert.That(unit.Demask, Is.EqualTo(200f));
            Assert.That(unit.Suspicion.Value, Is.EqualTo(5f));
            Assert.That(unit.NoiseRippleTicks, Is.EqualTo(NoiseMath.NoiseRippleTicks));
            Assert.IsFalse(StealthHandleOf(unit).IsValid,
                "no world pushed — the fallback copy is in use and no slot must exist");
        }

        [Test]
        public void BothPaths_AreNumericallyIdentical_ForTheSameCallSequence()
        {
            SimClock worldClock = MakeClock(out SimLoop worldLoop);
            worldLoop.UnitWorld = new UnitWorld();
            SpyUnit inWorld = MakeUnit(worldClock, worldLoop);

            SimClock localClock = MakeClock(out SimLoop localLoop);
            SpyUnit local = MakeUnit(localClock, localLoop);

            const int steps = 12;
            for (int i = 0; i < steps; i++)
            {
                bool grounded = i % 3 != 2;
                var velocity = new Vector2(i * 1.5f, grounded ? 0f : -(2f + i));
                inWorld.TickNoise(grounded, velocity);
                local.TickNoise(grounded, velocity);
                inWorld.TickStealth(velocity);
                local.TickStealth(velocity);
                inWorld.TickSuspicion();
                local.TickSuspicion();

                if (i == 4)
                {
                    inWorld.MakeNoise(300, isEvent: true);
                    local.MakeNoise(300, isEvent: true);
                    inWorld.AddSuspicion(3f);
                    local.AddSuspicion(3f);
                    inWorld.RaiseDemask(90f);
                    local.RaiseDemask(90f);
                }

                Assert.That(inWorld.Noise, Is.EqualTo(local.Noise), $"Noise diverged at step {i}");
                Assert.That(inWorld.Suspicion, Is.EqualTo(local.Suspicion), $"Suspicion diverged at step {i}");
                Assert.That(inWorld.Stealth, Is.EqualTo(local.Stealth), $"Stealth diverged at step {i}");
            }
        }

        [Test]
        public void WorldPath_LazyAcquire_IfTheWorldArrivesAfterRegistration()
        {
            SimClock clock = MakeClock(out SimLoop loop);
            SpyUnit unit = MakeUnit(clock, loop);

            var world = new UnitWorld();
            loop.UnitWorld = world;
            unit.MakeNoise(50);

            Assert.That(world.LiveCount, Is.EqualTo(1),
                "the data accessor's defensive acquire must land the unit in the new world");
            Assert.That(unit.Noise, Is.EqualTo(50));
            Assert.IsTrue(world.TryGet(StealthHandleOf(unit), out UnitStealthData data));
            Assert.That(data.Noise, Is.EqualTo(50));
        }
    }
}
