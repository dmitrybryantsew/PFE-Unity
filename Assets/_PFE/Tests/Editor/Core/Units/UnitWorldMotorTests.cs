using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PFE.Core;
using PFE.Sim.State;
using PFE.Sim.Units;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Tests.Editor.Core.Units
{
    /// <summary>
    /// Thread J, Phase 2 — the third <see cref="UnitWorld"/> domain: the movement motor record
    /// (<see cref="SimMotorState"/>), the store half first and then the forwarding facade
    /// <see cref="TilePhysicsController"/> became when its <c>_motor</c> field moved into a world
    /// slot beside the stealth/corpse records.
    ///
    /// <para><b>The contract under test.</b> Exactly the stealth and corpse migrations': through the
    /// public surface the world slot and the local fallback are indistinguishable — same calls, same
    /// numbers, only where the bytes sit differs. The seam the facade reads is its own
    /// <c>_motorHandle</c>: unlike stealth/corpse, whose handle the unit controller owns, the motor
    /// is a separate component with its own drive lifecycle (it registers at
    /// <c>SimTickOrder.PlayerMotor</c> or <c>UnitMotor</c>, never at the unit's
    /// <c>UnitsAndAi</c>), so it acquires and releases its own slot.</para>
    ///
    /// <para><b>Why the writes go through the public motor API.</b> The only entries into the record
    /// that never touch room geometry are <c>VelocityPixelsPerFrame</c>, <c>SetGravityScale</c> and
    /// the <c>ApplyMotorState</c> restore seam — they exercise every forwarder the way a tick and a
    /// host correction actually reach them, without needing a room's tile grid the way
    /// <c>StepMotor</c> would.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitWorldMotorTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            _spawned.Clear();
        }

        // ── The store: the motor array ───────────────────────────────────────────

        [Test]
        public void Acquire_StartsTheMotorRecordAtDefault()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();

            ref SimMotorState data = ref world.AccessMotor(handle);
            Assert.That(data.FacingDirection, Is.EqualTo(1), "the old facingDirection = 1 initializer");
            Assert.That(data.GravityScale, Is.EqualTo(1f), "the old gravityScale = 1f initializer");
            Assert.That(data.Vx, Is.EqualTo(0f));
            Assert.That(data.Vy, Is.EqualTo(0f));
            Assert.That(data.IsGrounded, Is.False);
            Assert.That(data.Px, Is.EqualTo(0f));
        }

        [Test]
        public void AccessMotor_ThrowsOnAReleasedHandle()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.Release(ref handle);

            Assert.Throws<InvalidOperationException>(() => ReadVelocityX(world, handle));
        }

        [Test]
        public void TryGetMotor_FalseOnAReleasedHandle()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.Release(ref handle);

            Assert.IsFalse(world.TryGetMotor(handle, out _));
        }

        [Test]
        public void ReleasedSlot_ReacquiresWithADefaultMotorRecord()
        {
            var world = new UnitWorld();
            UnitHandle first = world.Acquire();
            UnitHandle stale = first;
            world.AccessMotor(first).Vx = 7f;
            world.AccessMotor(first).FacingDirection = -1;
            world.Release(ref first);

            UnitHandle second = world.Acquire();

            Assert.IsFalse(world.TryGetMotor(stale, out _),
                "the reissued slot carries a bumped generation, so the old handle stays stale");
            Assert.That(world.AccessMotor(second).Vx, Is.EqualTo(0f));
            Assert.That(world.AccessMotor(second).FacingDirection, Is.EqualTo(1),
                "a reissued slot starts clean — the previous tenant's motor record must not leak");
        }

        [Test]
        public void MotorRecords_AreIndependentAcrossSlots()
        {
            var world = new UnitWorld();
            UnitHandle a = world.Acquire();
            UnitHandle b = world.Acquire();

            world.AccessMotor(a).DashTimer = 0.5f;

            Assert.That(world.AccessMotor(a).DashTimer, Is.EqualTo(0.5f));
            Assert.That(world.AccessMotor(b).DashTimer, Is.EqualTo(0f),
                "each slot owns its record; the two tenants cannot see each other's motor state");
        }

        [Test]
        public void ControllerSlot_AndMotorSlot_DoNotShareMotorRecords()
        {
            var world = new UnitWorld();
            UnitHandle controllerSlot = world.Acquire(); // stands in for a UnitController's slot
            UnitHandle motorSlot = world.Acquire();      // stands in for a TilePhysicsController's

            world.AccessMotor(motorSlot).Vy = -4f;

            Assert.That(world.AccessMotor(controllerSlot).Vy, Is.EqualTo(0f),
                "the two components acquire separate slots, so a unit's stealth/corpse slot keeps " +
                "the default motor record and only the motor's own slot carries the truth");
        }

        [Test]
        public void Clear_InvalidatesEveryMotorRecord()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.AccessMotor(handle).Vx = 3f;

            world.Clear();

            Assert.IsFalse(world.TryGetMotor(handle, out _));
        }

        private static float ReadVelocityX(UnitWorld world, in UnitHandle handle) =>
            world.AccessMotor(handle).Vx;

        // ── The facade: TilePhysicsController forwards ───────────────────────────

        private static SimClock MakeClock(out SimLoop loop)
        {
            var clock = new SimClock(30);
            loop = new SimLoop(clock, null);
            return clock;
        }

        private TilePhysicsController MakeMotor(SimClock clock, SimLoop loop)
        {
            var go = new GameObject("Motor");
            _spawned.Add(go);
            TilePhysicsController motor = go.AddComponent<TilePhysicsController>();
            motor.AttachSimulation(clock, loop);
            return motor;
        }

        private static UnitHandle MotorHandleOf(TilePhysicsController motor)
        {
            FieldInfo field = typeof(TilePhysicsController).GetField(
                "_motorHandle", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "the facade's handle field is the seam this test reads");
            return (UnitHandle)field.GetValue(motor);
        }

        private static SimMotorState LocalMotorOf(TilePhysicsController motor)
        {
            FieldInfo field = typeof(TilePhysicsController).GetField(
                "_localMotor", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "the local fallback copy is the seam the no-world path is read through");
            return (SimMotorState)field.GetValue(motor);
        }

        [Test]
        public void WorldPath_RegisterAcquiresASlot_AndVelocityLandsInIt()
        {
            MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            TilePhysicsController motor = MakeMotor(loop.Clock, loop);

            UnitHandle handle = MotorHandleOf(motor);
            Assert.IsTrue(handle.IsValid, "registration acquired the slot before the first tick");

            motor.VelocityPixelsPerFrame = new Vector2(3f, -2f);

            Assert.IsTrue(world.TryGetMotor(handle, out SimMotorState data));
            Assert.That(data.Vx, Is.EqualTo(3f));
            Assert.That(data.Vy, Is.EqualTo(-2f));
        }

        [Test]
        public void WorldPath_SetGravityScale_LandsInTheSlot()
        {
            MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            TilePhysicsController motor = MakeMotor(loop.Clock, loop);

            motor.SetGravityScale(2.5f);

            UnitHandle handle = MotorHandleOf(motor);
            Assert.IsTrue(world.TryGetMotor(handle, out SimMotorState data));
            Assert.That(data.GravityScale, Is.EqualTo(2.5f));
            Assert.That(data.FacingDirection, Is.EqualTo(1), "the untouched default survives");
        }

        [Test]
        public void WorldPath_ApplyMotorState_ReplacesTheWholeRecord()
        {
            MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            TilePhysicsController motor = MakeMotor(loop.Clock, loop);

            var state = new SimMotorState
            {
                Vx = 9f,
                Vy = -1f,
                Px = 100f,
                Py = 50f,
                FacingDirection = -1,
                IsGrounded = true,
                DashTimer = 0.25f,
            };
            motor.ApplyMotorState(in state);

            Assert.That(motor.MotorState, Is.EqualTo(state),
                "the restore seam replaces the live record, not just the fields it knows about");
            UnitHandle handle = MotorHandleOf(motor);
            Assert.IsTrue(world.TryGetMotor(handle, out SimMotorState data));
            Assert.That(data, Is.EqualTo(state), "the slot mirrors the restored record");
        }

        /// <summary>
        /// The release is driven through reflection for the same reason the corpse fixtures drive
        /// <c>BeginDissolve</c>: the real entry (play-mode <c>OnDestroy</c>) is unreachable in
        /// EditMode, which never fires destroy messages for a non-ExecuteAlways script — see
        /// <c>UnitDriveSystemsTests.DestroyedUnit_LaneSkipsTheDeadEntry</c>'s remarks.
        /// </summary>
        private static readonly MethodInfo UnregisterSimDriveMethod =
            typeof(TilePhysicsController).GetMethod(
                "UnregisterSimDrive", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void UnregisterSimDrive(TilePhysicsController motor)
        {
            Assert.IsNotNull(UnregisterSimDriveMethod,
                "the private unregister entry point is the seam play-mode OnDestroy reaches");
            UnregisterSimDriveMethod.Invoke(motor, null);
        }

        [Test]
        public void WorldPath_DriveUnregister_ReleasesTheSlot()
        {
            MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            TilePhysicsController motor = MakeMotor(loop.Clock, loop);
            UnitHandle handle = MotorHandleOf(motor);
            Assert.That(world.LiveCount, Is.EqualTo(1));
            Assert.That(loop.TickableCount, Is.EqualTo(1), "precondition: the motor sits on the loop");

            UnregisterSimDrive(motor);

            Assert.IsFalse(MotorHandleOf(motor).IsValid,
                "the release clears the handle, so a second unregister is a documented no-op");
            Assert.IsFalse(world.TryGetMotor(handle, out _),
                "drive unregistration releases the motor record's slot, exactly as the unit " +
                "controller's unregistration releases the stealth/corpse slot");
            Assert.That(world.LiveCount, Is.EqualTo(0));
            Assert.That(loop.TickableCount, Is.EqualTo(0));
        }

        [Test]
        public void LocalPath_LoopWithoutWorld_KeepsTheRecordInTheFallback()
        {
            MakeClock(out SimLoop loop);
            TilePhysicsController motor = MakeMotor(loop.Clock, loop);

            motor.VelocityPixelsPerFrame = new Vector2(3f, -2f);
            motor.SetGravityScale(0.5f);

            Assert.IsFalse(MotorHandleOf(motor).IsValid,
                "no world pushed — the fallback copy is in use and no slot must exist");

            SimMotorState local = LocalMotorOf(motor);
            Assert.That(local.Vx, Is.EqualTo(3f));
            Assert.That(local.Vy, Is.EqualTo(-2f));
            Assert.That(local.GravityScale, Is.EqualTo(0.5f));
            Assert.That(motor.MotorState, Is.EqualTo(local));
        }

        [Test]
        public void BothPaths_AreNumericallyIdentical_ForTheSameWrites()
        {
            MakeClock(out SimLoop worldLoop);
            worldLoop.UnitWorld = new UnitWorld();
            TilePhysicsController inWorld = MakeMotor(worldLoop.Clock, worldLoop);

            MakeClock(out SimLoop localLoop);
            TilePhysicsController local = MakeMotor(localLoop.Clock, localLoop);

            inWorld.VelocityPixelsPerFrame = new Vector2(4f, -7f);
            local.VelocityPixelsPerFrame = new Vector2(4f, -7f);
            inWorld.SetGravityScale(1.75f);
            local.SetGravityScale(1.75f);

            Assert.That(inWorld.MotorState, Is.EqualTo(local.MotorState),
                "the public surface returns the same record through both storages");

            Assert.IsTrue(
                worldLoop.UnitWorld.TryGetMotor(MotorHandleOf(inWorld), out SimMotorState slot));
            SimMotorState fallback = LocalMotorOf(local);
            Assert.That(slot, Is.EqualTo(fallback),
                "the record is byte-identical between the world slot and the fallback copy");
        }
    }
}
