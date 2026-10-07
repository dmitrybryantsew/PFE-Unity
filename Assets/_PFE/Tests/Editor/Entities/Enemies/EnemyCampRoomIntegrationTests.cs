using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Entities.Enemies;
using PFE.Entities.Enemies.Archetypes;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Enemies
{
    /// <summary>
    /// Integration test verifying the presence and spawning of the zombie test unit
    /// in the camp test room (Rooms/rooms_rbl/room_1_0) alongside the training dummies.
    /// </summary>
    [TestFixture]
    public class EnemyCampRoomIntegrationTests
    {
        private GameObject _root;
        private Transform _parent;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("EnemyCampRoomIntegrationTests_Root");
            var parentGo = new GameObject("SpawnedUnits");
            parentGo.transform.SetParent(_root.transform, false);
            _parent = parentGo.transform;
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
            }
        }

        [Test]
        public void CampRoom_ContainsAndSpawnsZombie_AlongsideTrainingDummies()
        {
            // 1. Verify camp room template exists in Resources and contains both dummies and zombie
            RoomTemplate camp = Resources.Load<RoomTemplate>("Rooms/rooms_rbl/room_1_0");
            Assert.IsNotNull(camp, "The camp room (Rooms/rooms_rbl/room_1_0) must exist in Resources.");

            int dummyCount = 0;
            int zombieCount = 0;
            ObjectSpawnData zombieSpawn = null;

            foreach (ObjectSpawnData obj in camp.objects)
            {
                if (obj == null) continue;
                if (obj.id == "training") dummyCount++;
                if (obj.id == "zombie")
                {
                    zombieCount++;
                    zombieSpawn = obj;
                }
            }

            Assert.AreEqual(5, dummyCount, "The camp room should contain five training dummies.");
            Assert.AreEqual(1, zombieCount, "The camp room should contain one placed zombie test unit.");
            Assert.IsNotNull(zombieSpawn, "Zombie spawn definition must not be null.");
            Assert.AreEqual(new Vector2Int(20, 15), zombieSpawn.tileCoord, "Zombie should be placed at tile coord (20, 15).");

            // 2. Populate RoomInstance through RoomPopulator
            var room = new RoomInstance
            {
                id = "camp_test_instance",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
            };
            room.InitializeTiles();

            RoomPopulator.PopulateRoom(room, camp, room.difficulty);

            // The room authors `id='zombie'`, but the oracle does not spawn what the room authored — it
            // spawns a VARIANT. `Location.createUnit()` (Location.as:1169/1185) calls `randomCid()` and
            // hands the result to `Unit.create()`, whose subclass joins the two
            // (`UnitZombie.as:104 id = "zombie" + tr`). At `locDifLevel < 2` the ladder's last arm is a
            // bare `_loc2_ = 0` (Location.as:1866-1869), so the camp room's zombie is `zombie0`.
            //
            // This assertion used to read `unitId == "zombie"`, i.e. it pinned the bug: the family id
            // resolves to `Resources/Units/zombie.asset`, the content-free template with 1 sprite, no
            // <vis> art and no <comb hp>. That is what the player saw as "the zombie has no animation".
            UnitInstance zombieInstance = room.units.Find(u => u != null && u.unitId == "zombie0");
            Assert.IsNotNull(zombieInstance,
                "RoomPopulator must resolve the authored 'zombie' to a variant id, not spawn the family " +
                "template. Got: " + DescribeUnits(room));
            Assert.AreEqual("UnitZombie", zombieInstance.controllerId, "Zombie controllerId should map to UnitZombie.");

            // 3. Spawn through RoomUnitSpawner
            var spawner = new RoomUnitSpawner(room, _parent);
            spawner.RefreshAll();

            Transform zombieTransform = _parent.Find("Unit_zombie0");
            Assert.IsNotNull(zombieTransform, "RoomUnitSpawner should instantiate a GameObject named Unit_zombie0.");

            var controller = zombieTransform.GetComponent<ZombieController>();
            Assert.IsNotNull(controller, "Spawned zombie must have ZombieController attached.");

            // AS3's F_ZOMBIE is 3 and is declared and never assigned anywhere in the codebase; the
            // zombie family row is `<unit id='zombie' fraction='1'>` (AllData.as:768) — 1, F_MONSTER —
            // and UnitZombie.resurrect() writes `fraction = Unit.F_MONSTER` (:506) even for a zombie
            // killed once already. ZombieController used to override this to FactionType.Zombie, which
            // made the port disagree with both the data and the oracle.
            Assert.AreEqual(FactionType.Monster, controller.Faction,
                "The zombie family row is fraction='1' (F_MONSTER); F_ZOMBIE is declared-only in AS3.");

            // The whole point of the variant step: the spawned definition must be the variant's, not the
            // family template's. 216 cells (24 columns x 9 rows) vs the template's 1 is the observable.
            Assert.IsNotNull(controller.Stats, "The spawned zombie must have its UnitDefinition assigned.");
            Assert.AreEqual(216, controller.Stats.spriteSheet != null ? controller.Stats.spriteSheet.Length : 0,
                "zombie0's imported sheet is 24 x 9 = 216 cells; the family template 'zombie' has 1. A " +
                "count of 1 here means the variant resolution regressed.");
            Assert.AreEqual(10f, controller.Stats.RunSpeed, 1e-3f,
                "zombie0 is <move speed='1.5' run='10'> (AllData.as:784-790). RunSpeed must be the " +
                "oracle's absolute 'run' (10 px/frame), not moveSpeed * runMultiplier (1.5 * 2 = 3).");

            var brain = zombieTransform.GetComponent<ZombieBrain>();
            Assert.IsNotNull(brain, "Spawned zombie must have ZombieBrain attached.");
            Assert.AreEqual(EnemyAIState.Idle, brain.CurrentState, "Zombie should initialize in Idle state.");

            // Verify visual child exists
            Transform visual = zombieTransform.Find("Visual");
            Assert.IsNotNull(visual, "Spawned zombie must have a Visual child.");
            var renderer = visual.GetComponent<SpriteRenderer>();
            Assert.IsNotNull(renderer, "Visual child must have a SpriteRenderer.");

            // The animator is the reader the importer's animation data never had, and the brain is what
            // drives it. Both halves have to be present or a zombie stands frozen however complete the
            // asset is.
            var animator = visual.GetComponent<UnitAnimator>();
            Assert.IsNotNull(animator, "Visual child must have a UnitAnimator — nothing else drives the sheet.");
            Assert.IsTrue(animator.HasAnimation,
                "zombie0 carries animation data (stay/walk/trot/run/jump/die/death/fall); HasAnimation " +
                "false means the animator was handed the wrong definition or no renderer.");
        }

        private static string DescribeUnits(RoomInstance room)
        {
            var ids = new List<string>();
            foreach (UnitInstance unit in room.units)
            {
                ids.Add(unit == null ? "<null>" : unit.unitId);
            }

            return ids.Count == 0 ? "<no units>" : string.Join(", ", ids);
        }
    }
}
