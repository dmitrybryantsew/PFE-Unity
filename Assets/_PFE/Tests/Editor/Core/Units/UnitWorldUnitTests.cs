using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PFE.Core;
using PFE.Core.Ids;
using PFE.Entities.Units;
using PFE.Sim.State;
using PFE.Sim.Units;
using PFE.Systems.Replication;
using UnityEngine;
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Tests.Editor.Core.Units
{
    /// <summary>
    /// Thread J, Phase 3 — the fourth <see cref="UnitWorld"/> domain: the replicated unit record
    /// (<see cref="SimUnitState"/>), the store half first and then the forwarding facade
    /// <see cref="UnitController"/> became when its <c>_sim</c> field moved into the controller's
    /// world slot (shared with the stealth and corpse blocks — one handle, three domains), plus
    /// the <see cref="UnitRecordSyncSystem"/> stamping pass that keeps the records' view-derived
    /// fields fresh for the snapshot projector.
    ///
    /// <para><b>The contract under test.</b> Exactly the stealth, corpse and motor migrations':
    /// through the public surface the world slot and the local fallback are indistinguishable —
    /// same calls, same numbers, only where the bytes sit differs. The seam the facade reads is
    /// the SAME <c>_stealthHandle</c> the stealth facade tests read: the unit record shares the
    /// controller's slot rather than naming a second one, so the projection cannot double-count a
    /// unit through two slots.</para>
    ///
    /// <para><b>Why the writes go through the public unit API.</b> The entries into the record
    /// that never touch room geometry are <c>SetVelocityX/Y</c> (the motor-less velocity path),
    /// <c>AssignEntityId</c> / <c>AssignDefinitionId</c> (the spawner stamps) and the
    /// <c>ApplyUnitState</c> restore seam — they exercise every forwarder the way a tick, a spawn
    /// and a host correction actually reach them. <c>SyncUnitRecordForProjection</c> is driven
    /// directly: the transform read needs no room, and a unit with no tile query legitimately
    /// stamps room key 0 ("in no room").</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitWorldUnitTests
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

        // ── The store: the unit-record array ─────────────────────────────────────

        [Test]
        public void Acquire_StartsTheUnitRecordAtDefault()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();

            ref SimUnitState data = ref world.AccessUnit(handle);
            Assert.That(data.FacingDirection, Is.EqualTo(1), "the old _facingDirection = 1 initializer");
            Assert.That(data.OverhangLeft, Is.EqualTo(1f), "the old NoSupportOverhang initializer");
            Assert.That(data.OverhangRight, Is.EqualTo(1f), "the old NoSupportOverhang initializer");
            Assert.That(data.ControllerSlot, Is.EqualTo(SimUnitState.HostAiControllerSlot),
                "a fresh record must claim host AI, not player 0");
            Assert.That(data.EntityHash, Is.EqualTo(0UL));
        }

        [Test]
        public void AccessUnit_ThrowsOnAReleasedHandle()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.Release(ref handle);

            Assert.Throws<InvalidOperationException>(() => ReadVelocityX(world, handle));
        }

        [Test]
        public void TryGetUnit_FalseOnAReleasedHandle()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.Release(ref handle);

            Assert.IsFalse(world.TryGetUnit(handle, out _));
        }

        [Test]
        public void ReleasedSlot_ReacquiresWithADefaultUnitRecord()
        {
            var world = new UnitWorld();
            UnitHandle first = world.Acquire();
            UnitHandle stale = first;
            world.AccessUnit(first).Vx = 7f;
            world.AccessUnit(first).ControllerSlot = 3;
            world.Release(ref first);

            UnitHandle second = world.Acquire();

            Assert.IsFalse(world.TryGetUnit(stale, out _),
                "the reissued slot carries a bumped generation, so the old handle stays stale");
            Assert.That(world.AccessUnit(second).Vx, Is.EqualTo(0f));
            Assert.That(world.AccessUnit(second).ControllerSlot, Is.EqualTo(SimUnitState.HostAiControllerSlot),
                "a reissued slot starts clean — the previous tenant's unit record must not leak");
        }

        [Test]
        public void UnitRecords_AreIndependentAcrossSlots()
        {
            var world = new UnitWorld();
            UnitHandle a = world.Acquire();
            UnitHandle b = world.Acquire();

            world.AccessUnit(a).Health = 55f;

            Assert.That(world.AccessUnit(a).Health, Is.EqualTo(55f));
            Assert.That(world.AccessUnit(b).Health, Is.EqualTo(0f),
                "each slot owns its record; the two tenants cannot see each other's unit state");
        }

        [Test]
        public void ControllerSlot_HoldsAllThreeDomains_UnderOneHandle()
        {
            var world = new UnitWorld();
            UnitHandle controllerSlot = world.Acquire();

            world.Access(controllerSlot).Noise = 9000;
            world.AccessCorpse(controllerSlot).DissolveFrames = 12f;
            world.AccessUnit(controllerSlot).Vx = -3f;

            Assert.That(world.Access(controllerSlot).Noise, Is.EqualTo(9000));
            Assert.That(world.AccessCorpse(controllerSlot).DissolveFrames, Is.EqualTo(12f));
            Assert.That(world.AccessUnit(controllerSlot).Vx, Is.EqualTo(-3f),
                "stealth, corpse and the unit record share the slot but never each other's bytes");
        }

        // ── The batch surface: live list + room keys ─────────────────────────────

        [Test]
        public void LiveSlotList_TracksAcquireAndRelease()
        {
            var world = new UnitWorld();
            UnitHandle a = world.Acquire();
            UnitHandle b = world.Acquire();
            UnitHandle c = world.Acquire();

            Assert.That(world.LiveCount, Is.EqualTo(3));
            Assert.That(world.LiveSlotAt(2), Is.EqualTo(c.Slot));

            world.Release(ref b);

            Assert.That(world.LiveCount, Is.EqualTo(2));
            Assert.That(world.LiveSlotAt(0), Is.EqualTo(a.Slot));
            Assert.That(world.LiveSlotAt(1), Is.EqualTo(c.Slot),
                "swap-remove keeps the surviving slots reachable through the dense list");
            Assert.Throws<ArgumentOutOfRangeException>(() => world.LiveSlotAt(2));
        }

        [Test]
        public void SetRoomKey_RoundsTripsThroughTheBatchSurface()
        {
            var world = new UnitWorld();
            UnitHandle a = world.Acquire();
            UnitHandle b = world.Acquire();

            world.SetRoomKey(a, 0xABCDEFUL);

            int slotA = -1, slotB = -1;
            for (int i = 0; i < world.LiveCount; i++)
            {
                int slot = world.LiveSlotAt(i);
                if (slot == a.Slot) slotA = slot;
                if (slot == b.Slot) slotB = slot;
            }

            Assert.That(slotA, Is.GreaterThanOrEqualTo(0));
            Assert.That(world.RoomKeyAt(slotA), Is.EqualTo(0xABCDEFUL));
            Assert.That(world.RoomKeyAt(slotB), Is.EqualTo(0UL),
                "keys are per-slot; stamping one unit's room never touches another");
        }

        [Test]
        public void SetRoomKey_ThrowsOnAReleasedHandle()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.Release(ref handle);

            Assert.Throws<InvalidOperationException>(() => world.SetRoomKey(handle, 1UL));
        }

        [Test]
        public void ReleasedSlot_DropsTheRoomKeyForTheNextTenant()
        {
            var world = new UnitWorld();
            UnitHandle first = world.Acquire();
            world.SetRoomKey(first, 0xABCDEFUL);
            world.Release(ref first);

            UnitHandle second = world.Acquire();
            Assert.That(second.Slot, Is.EqualTo(first.Slot), "precondition: the slot was reissued");

            int slot = world.LiveSlotAt(0);
            Assert.That(world.RoomKeyAt(slot), Is.EqualTo(0UL),
                "release clears the key — a reissued slot must not inherit the previous unit's room");
        }

        [Test]
        public void Clear_ResetsTheLiveList()
        {
            var world = new UnitWorld();
            world.Acquire();
            world.Acquire();

            world.Clear();

            Assert.That(world.LiveCount, Is.EqualTo(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.LiveSlotAt(0));
        }

        private static float ReadVelocityX(UnitWorld world, in UnitHandle handle) =>
            world.AccessUnit(handle).Vx;

        // ── The facade: UnitController forwards ──────────────────────────────────

        private static SimClock MakeClock(out SimLoop loop)
        {
            var clock = new SimClock(30);
            loop = new SimLoop(clock, null);
            return clock;
        }

        private UnitController MakeUnit(SimClock clock, SimLoop loop)
        {
            var go = new GameObject("Unit");
            _spawned.Add(go);
            UnitController unit = go.AddComponent<UnitController>();
            unit.AttachSimulation(clock, loop);
            return unit;
        }

        private static UnitHandle UnitHandleOf(UnitController unit)
        {
            FieldInfo field = typeof(UnitController).GetField(
                "_stealthHandle", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "the shared controller-slot handle is the seam this test reads");
            return (UnitHandle)field.GetValue(unit);
        }

        private static SimUnitState LocalSimOf(UnitController unit)
        {
            FieldInfo field = typeof(UnitController).GetField(
                "_localSim", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "the local fallback copy is the seam the no-world path is read through");
            return (SimUnitState)field.GetValue(unit);
        }

        [Test]
        public void WorldPath_RegisterAcquiresASlot_AndVelocityLandsInIt()
        {
            MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            UnitController unit = MakeUnit(loop.Clock, loop);

            UnitHandle handle = UnitHandleOf(unit);
            Assert.IsTrue(handle.IsValid, "registration acquired the slot before the first tick");

            unit.SetVelocityX(3f);
            unit.SetVelocityY(-2f);

            Assert.IsTrue(world.TryGetUnit(handle, out SimUnitState data));
            Assert.That(data.Vx, Is.EqualTo(3f));
            Assert.That(data.Vy, Is.EqualTo(-2f));
        }

        [Test]
        public void WorldPath_AssignEntityId_StampsIdentityInTheSlot()
        {
            MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            UnitController unit = MakeUnit(loop.Clock, loop);

            unit.AssignEntityId(EntityId.CreateForPlayer(2), new EntityLifetime(1, 1));

            UnitHandle handle = UnitHandleOf(unit);
            Assert.IsTrue(world.TryGetUnit(handle, out SimUnitState data));
            Assert.That(data.EntityHash, Is.EqualTo(EntityId.CreateForPlayer(2).Hash));
            Assert.That(data.Generation, Is.EqualTo(1));
            Assert.That(data.LifecycleSequence, Is.EqualTo(1));
            Assert.That(data.ControllerSlot, Is.EqualTo(SimUnitState.HostAiControllerSlot),
                "identity stamping must not disturb the record's other defaults");
        }

        [Test]
        public void WorldPath_SyncStampsPositionAndTheNoRoomKey()
        {
            MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            UnitController unit = MakeUnit(loop.Clock, loop);
            unit.AssignEntityId(EntityId.CreateForPlayer(1), new EntityLifetime(1, 1));

            // Any transform works for the stamp — the unit needs no room for the position half,
            // and with no tile query Room is null, which the sync must express as key 0.
            unit.transform.position = new Vector3(0.05f, -0.10f, 0f);
            unit.SyncUnitRecordForProjection();

            UnitHandle handle = UnitHandleOf(unit);
            Assert.IsTrue(world.TryGetUnit(handle, out SimUnitState data));
            Vector2 expected = PFE.Systems.Map.WorldCoordinates.UnityToPixel(unit.transform.position);
            Assert.That(data.Px, Is.EqualTo(expected.x));
            Assert.That(data.Py, Is.EqualTo(expected.y));

            int slot = world.LiveSlotAt(0);
            Assert.That(world.RoomKeyAt(slot), Is.EqualTo(0UL),
                "a unit with no room stamps key 0 — the projector reads that as unattributed");
        }

        [Test]
        public void WorldPath_ApplyUnitState_ReplacesTheWholeRecord()
        {
            MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            UnitController unit = MakeUnit(loop.Clock, loop);
            unit.AssignEntityId(EntityId.CreateForPlayer(1), new EntityLifetime(1, 1));
            UnitHandle handle = UnitHandleOf(unit);

            var state = new SimUnitState
            {
                EntityHash = 123UL,
                Vx = 9f,
                Vy = -1f,
                Px = 100f,
                Py = 50f,
                FacingDirection = -1,
                IsGrounded = true,
                Health = 42f,
            };

            using (SimAuthorityContext.EnterSnapshotApplication())
            {
                unit.ApplyUnitState(in state);
            }

            Assert.IsTrue(world.TryGetUnit(handle, out SimUnitState data));
            Assert.That(data.Vx, Is.EqualTo(9f));
            Assert.That(data.Health, Is.EqualTo(42f),
                "the restore seam replaces the live record, not just the fields it knows about");

            // Identity re-assert: the controller's mirrors win over the restored record.
            Assert.That(data.EntityHash, Is.EqualTo(EntityId.CreateForPlayer(1).Hash),
                "a restored record's foreign hash must not blank the unit's identity");
        }

        [Test]
        public void Capture_ReturnsTheSyncedRecord()
        {
            MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            UnitController unit = MakeUnit(loop.Clock, loop);
            unit.AssignEntityId(EntityId.CreateForPlayer(1), new EntityLifetime(1, 1));

            unit.transform.position = new Vector3(0.07f, 0.03f, 0f);
            SimUnitState captured = unit.CaptureSimState();

            Assert.That(captured.Px, Is.EqualTo(
                PFE.Systems.Map.WorldCoordinates.UnityToPixel(unit.transform.position).x),
                "capture runs the same sync the projector's pass runs, so the adapter's spawn "
                + "position comes from the current rig, not a stale record");
            Assert.That(captured.EntityHash, Is.EqualTo(EntityId.CreateForPlayer(1).Hash));
        }

        [Test]
        public void LocalPath_LoopWithoutWorld_KeepsTheRecordInTheFallback()
        {
            MakeClock(out SimLoop loop);
            UnitController unit = MakeUnit(loop.Clock, loop);

            unit.SetVelocityX(3f);
            unit.SetVelocityY(-2f);

            Assert.IsFalse(UnitHandleOf(unit).IsValid,
                "no world pushed — the fallback copy is in use and no slot must exist");

            SimUnitState local = LocalSimOf(unit);
            Assert.That(local.Vx, Is.EqualTo(3f));
            Assert.That(local.Vy, Is.EqualTo(-2f));
            Assert.That(unit.UnitState, Is.EqualTo(local));
        }

        [Test]
        public void BothPaths_AreNumericallyIdentical_ForTheSameWrites()
        {
            MakeClock(out SimLoop worldLoop);
            worldLoop.UnitWorld = new UnitWorld();
            UnitController inWorld = MakeUnit(worldLoop.Clock, worldLoop);

            MakeClock(out SimLoop localLoop);
            UnitController local = MakeUnit(localLoop.Clock, localLoop);

            inWorld.SetVelocityX(4f);
            inWorld.SetVelocityY(-7f);
            local.SetVelocityX(4f);
            local.SetVelocityY(-7f);

            Assert.That(inWorld.UnitState, Is.EqualTo(local.UnitState),
                "the public surface returns the same record through both storages");

            Assert.IsTrue(worldLoop.UnitWorld.TryGetUnit(UnitHandleOf(inWorld), out SimUnitState slot));
            SimUnitState fallback = LocalSimOf(local);
            Assert.That(slot, Is.EqualTo(fallback),
                "the record is identical between the world slot and the fallback copy");
        }

        // ── The sync system: the stamping pass ───────────────────────────────────

        [Test]
        public void SyncAll_SkipsAnonymousUnits_AndStampsIdentifiedOnes()
        {
            MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            var registry = new UnitViewRegistry();

            UnitController ided = MakeUnit(loop.Clock, loop);
            ided.AttachViewRegistries(registry, null);
            ided.AssignEntityId(EntityId.CreateForPlayer(1), new EntityLifetime(1, 1));

            UnitController anonymous = MakeUnit(loop.Clock, loop);
            anonymous.AttachViewRegistries(registry, null);
            // A distinct position proves the skip: if the pass touched this unit, its slot would
            // carry the stamped position; it must stay at the acquire-time default.
            anonymous.transform.position = new Vector3(0.11f, 0.22f, 0f);

            var sync = new UnitRecordSyncSystem(registry);
            sync.SyncAll();

            Assert.That(sync.LastSyncedCount, Is.EqualTo(1),
                "only the id-carrying unit is stamped; the anonymous one must wait for its id");

            UnitHandle idedHandle = UnitHandleOf(ided);
            Assert.IsTrue(world.TryGetUnit(idedHandle, out SimUnitState data));
            Assert.That(data.EntityHash, Is.EqualTo(EntityId.CreateForPlayer(1).Hash));
            Assert.That(data.Px, Is.EqualTo(
                PFE.Systems.Map.WorldCoordinates.UnityToPixel(ided.transform.position).x),
                "the identified unit's position landed in its slot");

            UnitHandle anonHandle = UnitHandleOf(anonymous);
            Assert.IsTrue(world.TryGetUnit(anonHandle, out SimUnitState anonData));
            Assert.That(anonData.EntityHash, Is.EqualTo(0UL),
                "the anonymous unit's slot stays off the projection — the projector's hash filter "
                + "must never see it stamped");
            Assert.That(anonData.Px, Is.EqualTo(0f),
                "the pass skipped the anonymous unit entirely; its record is the acquire-time "
                + "default, not a stamped position");
        }

        [Test]
        public void SyncAll_WithoutARegistry_StampsNothing()
        {
            var sync = new UnitRecordSyncSystem(null);

            sync.SyncAll();

            Assert.That(sync.LastSyncedCount, Is.EqualTo(0),
                "a fixture without a registry gets the same answer an empty room gives");
        }
    }
}
