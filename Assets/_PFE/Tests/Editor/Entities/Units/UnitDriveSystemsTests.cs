using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core;
using PFE.Entities.Enemies;
using PFE.Entities.Units;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Thread J, Phase 2 — the batch drive lanes (<see cref="UnitDriveLanes"/>) that replaced
    /// per-entity <c>SimLoop</c> registration for units, brains and unit-owned motors.
    ///
    /// <para><b>What these tests pin.</b> The refactor's whole contract is behavioural identity with
    /// per-entity dispatch: same order buckets, same step bodies, and a fixture fallback that keeps a
    /// bare <c>new SimLoop</c> working unchanged. So the suite asserts the order values, the
    /// register/unregister semantics (idempotence, destroy cleanup, disable/enable re-registration),
    /// the lane-or-loop fallback both ways, and one end-to-end <c>StepOnce</c> that proves a unit
    /// attached through <c>AttachSimulation</c> is dispatched by the lane, in bucket order, without
    /// touching the loop's own registry.</para>
    ///
    /// <para><b>Why spies and not real archetypes.</b> A real unit's <c>StepUnit</c> resolves ground
    /// state against a room that a bare EditMode GameObject does not have, so the spies override the
    /// step bodies to record the call — the lane's job (calling the right body, in the right order,
    /// for the right population) is fully exercised without standing up a room. EditMode also does
    /// not run <c>Awake</c> for <c>AddComponent</c>, so the spies' uninitialised physics caches are
    /// never touched.</para>
    /// </summary>
    [TestFixture]
    public class UnitDriveSystemsTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<string> _callLog = new List<string>();

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
            _callLog.Clear();
        }

        // ── Spies ────────────────────────────────────────────────────────────

        private sealed class SpyUnit : UnitController
        {
            public string Label;
            public List<string> Log;
            public int StepCount;

            protected override void StepUnit()
            {
                StepCount++;
                Log?.Add(Label);
            }
        }

        private sealed class SpyBrain : EnemyBrain
        {
            public string Label;
            public List<string> Log;

            public override void SimTick(int tickIndex) => Log?.Add(Label);

            protected override void ExecuteAttackAction()
            {
            }
        }

        // ── Construction helpers ─────────────────────────────────────────────

        private static SimLoop MakeLoop() => new SimLoop(new SimClock(30), null);

        private static UnitDriveLanes MakeLanes()
        {
            return new UnitDriveLanes(
                new UnitBrainDriveSystem(),
                new UnitMotorDriveSystem(),
                new UnitStepDriveSystem());
        }

        private GameObject MakeGameObject(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        private SpyUnit MakeSpyUnit(string label)
        {
            SpyUnit unit = MakeGameObject("Unit_" + label).AddComponent<SpyUnit>();
            unit.Label = label;
            unit.Log = _callLog;
            return unit;
        }

        private SpyBrain MakeSpyBrain(SpyUnit controller, string label)
        {
            SpyBrain brain = controller.gameObject.AddComponent<SpyBrain>();
            brain.Label = label;
            brain.Log = _callLog;
            brain.AttachController(controller);
            return brain;
        }

        // ── Order values: the contract is "same buckets as the per-entity tickables" ──

        [Test]
        public void LaneOrderValues_MatchTheFormerPerEntityBuckets()
        {
            Assert.That(new UnitBrainDriveSystem().TickOrder, Is.EqualTo(SimTickOrder.UnitMotor - 1),
                "brains must keep deciding one slot below UnitMotor, or their decisions stop reaching " +
                "the motors that apply them in the same tick");
            Assert.That(new UnitMotorDriveSystem().TickOrder, Is.EqualTo(SimTickOrder.UnitMotor));
            Assert.That(new UnitStepDriveSystem().TickOrder, Is.EqualTo(SimTickOrder.UnitsAndAi));
        }

        // ── Register / unregister semantics ──────────────────────────────────

        [Test]
        public void Register_IsIdempotentAndUnregisterRemoves()
        {
            var lane = new UnitStepDriveSystem();
            SpyUnit unit = MakeSpyUnit("a");

            lane.Register(unit);
            lane.Register(unit);

            Assert.That(lane.Count, Is.EqualTo(1), "the second Register must de-duplicate, as SimLoop.Register did");

            Assert.That(lane.Unregister(unit), Is.True);
            Assert.That(lane.Unregister(unit), Is.False, "a second unregister reports 'was not on the lane'");
            Assert.That(lane.Count, Is.EqualTo(0));
        }

        [Test]
        public void BatchDispatchesEveryRegisteredEntityOnce_InRegistrationOrder()
        {
            var lane = new UnitStepDriveSystem();
            SpyUnit a = MakeSpyUnit("a");
            SpyUnit b = MakeSpyUnit("b");
            SpyUnit c = MakeSpyUnit("c");

            lane.Register(a);
            lane.Register(b);
            lane.Register(c);

            lane.SimTick(7);

            Assert.That(a.StepCount, Is.EqualTo(1));
            Assert.That(b.StepCount, Is.EqualTo(1));
            Assert.That(c.StepCount, Is.EqualTo(1));
            Assert.That(_callLog, Is.EqualTo(new[] { "a", "b", "c" }),
                "population order is spawn order; a batch reorder would be a determinism change for netcode");
        }

        [Test]
        public void DestroyedUnit_LaneSkipsTheDeadEntry_AndUnregisterStillMatchesIt()
        {
            // EditMode never fires OnDestroy for a non-ExecuteAlways script (the same reason Awake
            // does not run — see UnitController's remarks on ResolvePhysicsComponents), so a unit
            // destroyed while registered stays in the lane as a fake-null until Unregister runs.
            // In play mode OnDestroy unregisters and this never comes up; here the lane's null
            // guard is the contract under test.
            SimLoop loop = MakeLoop();
            UnitDriveLanes lanes = MakeLanes();
            loop.UnitDrives = lanes;

            SpyUnit unit = MakeSpyUnit("a");
            unit.AttachSimulation(loop.Clock, loop);
            Assert.That(loop.TickableCount, Is.EqualTo(0), "with lanes present the loop's own registry stays empty");

            Object.DestroyImmediate(unit.gameObject);

            Assert.DoesNotThrow(() => lanes.Steps.SimTick(0));
            Assert.That(unit.StepCount, Is.EqualTo(0), "a destroyed unit must not be stepped");

            Assert.That(lanes.Steps.Unregister(unit), Is.True,
                "Unregister still matches the dead entry by reference, so play-mode OnDestroy and " +
                "pooled-reuse cleanup both actually remove it");
            Assert.That(lanes.Steps.Count, Is.EqualTo(0));
        }

        // ── The lane-or-loop fallback, both directions ───────────────────────

        [Test]
        public void NoLanes_FixtureRegistersDirectlyOnTheLoopUnchanged()
        {
            SimLoop loop = MakeLoop();
            SpyUnit unit = MakeSpyUnit("a");

            unit.AttachSimulation(loop.Clock, loop);

            Assert.That(loop.TickableCount, Is.EqualTo(1),
                "a bare new SimLoop (every fixture, GoldenTraceHarness included) must keep the " +
                "legacy direct-registration path");
            loop.StepOnce();
            Assert.That(unit.StepCount, Is.EqualTo(1));
        }

        [Test]
        public void NoLanes_BrainRegistersDirectlyOnTheLoopUnchanged()
        {
            SimLoop loop = MakeLoop();
            SpyUnit unit = MakeSpyUnit("a");
            SpyBrain brain = MakeSpyBrain(unit, "brain");

            brain.Initialize(null, loop);

            Assert.That(loop.TickableCount, Is.EqualTo(1));
        }

        [Test]
        public void LanesPresent_EndToEndStepOnceDispatchesThroughTheLanesInBucketOrder()
        {
            SimLoop loop = MakeLoop();
            UnitDriveLanes lanes = MakeLanes();
            loop.UnitDrives = lanes;
            // What SimTickRegistrar does in production: the lanes are the loop's tickables.
            loop.Register(lanes.Brains);
            loop.Register(lanes.Motors);
            loop.Register(lanes.Steps);

            SpyUnit unit = MakeSpyUnit("unit");
            SpyBrain brain = MakeSpyBrain(unit, "brain");
            brain.Initialize(null, loop);
            unit.AttachSimulation(loop.Clock, loop);

            Assert.That(loop.TickableCount, Is.EqualTo(3), "only the three lanes are on the loop itself");
            Assert.That(lanes.Brains.Count, Is.EqualTo(1));
            Assert.That(lanes.Steps.Count, Is.EqualTo(1));

            loop.StepOnce();

            Assert.That(_callLog, Is.EqualTo(new[] { "brain", "unit" }),
                "one tick: brains at UnitMotor-1 first, unit steps at UnitsAndAi — the bucket order the " +
                "per-entity registrations produced");
        }

        // ── Motor lane carve-out: unit-owned only ────────────────────────────

        [Test]
        public void UnitOwnedMotor_JoinsTheMotorLane_NotTheLoop()
        {
            SimLoop loop = MakeLoop();
            UnitDriveLanes lanes = MakeLanes();
            loop.UnitDrives = lanes;

            TilePhysicsController motor = MakeGameObject("Motor").AddComponent<TilePhysicsController>();
            motor.MarkUnitOwned();
            motor.AttachSimulation(loop.Clock, loop);

            Assert.That(lanes.Motors.Count, Is.EqualTo(1));
            Assert.That(loop.TickableCount, Is.EqualTo(0), "an NPC motor must not sit on the loop's own registry");
        }

        [Test]
        public void PlayerMotor_KeepsDirectLoopRegistrationEvenWhenLanesExist()
        {
            SimLoop loop = MakeLoop();
            UnitDriveLanes lanes = MakeLanes();
            loop.UnitDrives = lanes;

            TilePhysicsController motor = MakeGameObject("PlayerMotor").AddComponent<TilePhysicsController>();
            motor.AttachSimulation(loop.Clock, loop);

            Assert.That(lanes.Motors.Count, Is.EqualTo(0), "the player motor ticks at PlayerMotor, a slot no lane owns");
            Assert.That(loop.TickableCount, Is.EqualTo(1));
            Assert.That(motor.TickOrder, Is.EqualTo(SimTickOrder.PlayerMotor));
        }
    }
}
