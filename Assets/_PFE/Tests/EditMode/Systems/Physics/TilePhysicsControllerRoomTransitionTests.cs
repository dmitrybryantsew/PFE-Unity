using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// P0-2 regression guard. See docs/Roadmap/01_P0_CRITICAL_DEFECT_FIXES.md (Defect 2).
    ///
    /// RoomTransitionManager used to assign player.transform.position directly. The motor
    /// keeps its own authoritative posX/posY plus a currentRoom reference, none of which
    /// were updated — so the next tick collided against the OLD room's tile grid at the
    /// NEW room's coordinates. RepositionForRoom is the fix; these tests pin its contract.
    /// </summary>
    [TestFixture]
    public class TilePhysicsControllerRoomTransitionTests
    {
        // Mirrors TilePhysicsController.UNIT_TO_PIX.
        private const float UnitToPix = 100f;
        private const float Epsilon = 0.0001f;

        private GameObject _go;
        private TilePhysicsController _motor;
        private RoomInstance _roomA;
        private RoomInstance _roomB;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("MotorUnderTest");
            _motor = _go.AddComponent<TilePhysicsController>();

            _roomA = MakeRoom(0, 0);
            _roomB = MakeRoom(2, 1);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        private static RoomInstance MakeRoom(int landX, int landY)
        {
            return new RoomInstance
            {
                id = "test_room_" + landX + "_" + landY,
                landPosition = new Vector3Int(landX, landY, 0),
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
            };
        }

        [Test]
        public void RepositionForRoom_SwapsRoomContext()
        {
            _motor.RepositionForRoom(_roomA, Vector3.zero);
            Assert.AreSame(_roomA, _motor.CurrentRoom, "First room was not adopted.");

            _motor.RepositionForRoom(_roomB, new Vector3(5f, 3f, 0f));
            Assert.AreSame(_roomB, _motor.CurrentRoom,
                "Motor still resolves tiles against the previous room. SetRoom must run " +
                "before any position is resolved.");
        }

        [Test]
        public void RepositionForRoom_SyncsPixelPositionFromWorldPosition()
        {
            Vector3 spawn = new Vector3(12.5f, -4.25f, 0f);

            _motor.RepositionForRoom(_roomB, spawn);

            // colliderOffsetPixels defaults to zero, so collider pixels == transform pixels.
            Assert.AreEqual(spawn.x * UnitToPix, _motor.State.Position.x, Epsilon,
                "posX did not follow the spawn world position.");
            Assert.AreEqual(spawn.y * UnitToPix, _motor.State.Position.y, Epsilon,
                "posY did not follow the spawn world position.");

            // The transform must agree with the motor, or they fight every tick.
            Assert.AreEqual(spawn.x, _go.transform.position.x, Epsilon);
            Assert.AreEqual(spawn.y, _go.transform.position.y, Epsilon);
        }

        [Test]
        public void RepositionForRoom_ClearsVelocity()
        {
            _motor.RepositionForRoom(_roomA, Vector3.zero);
            _motor.AddForce(new Vector2(40f, -25f));
            Assert.AreNotEqual(0f, _motor.State.Velocity.x,
                "Precondition failed: AddForce did not seed velocity, so this test proves nothing.");

            _motor.RepositionForRoom(_roomB, new Vector3(1f, 1f, 0f));

            Assert.AreEqual(0f, _motor.State.Velocity.x, Epsilon,
                "dx carried across the door. AS3 rebuilds the unit on a location change, so " +
                "velocity must never survive a transition.");
            Assert.AreEqual(0f, _motor.State.Velocity.y, Epsilon, "dy carried across the door.");
        }

        [Test]
        public void RepositionForRoom_NullRoomDoesNotThrow()
        {
            Assert.DoesNotThrow(() => _motor.RepositionForRoom(null, new Vector3(1f, 2f, 0f)));
            Assert.IsNull(_motor.CurrentRoom);
        }
    }
}
