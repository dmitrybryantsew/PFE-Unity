using NUnit.Framework;
using PFE.Core.Ids;
using System.Collections.Generic;

namespace PFE.Tests.Editor.Core
{
    [TestFixture]
    public class EntityIdTests
    {
        [Test]
        public void FNV1a64_DeterministicAndDistinct()
        {
            ulong hash1 = EntityId.ComputeFnv1a64("r_01_01:enemy_grunt:001");
            ulong hash2 = EntityId.ComputeFnv1a64("r_01_01:enemy_grunt:001");
            ulong hash3 = EntityId.ComputeFnv1a64("r_01_01:enemy_grunt:002");

            Assert.AreNotEqual(0UL, hash1);
            Assert.AreEqual(hash1, hash2);
            Assert.AreNotEqual(hash1, hash3);
            Assert.AreEqual(0UL, EntityId.ComputeFnv1a64(null));
            Assert.AreEqual(0UL, EntityId.ComputeFnv1a64(string.Empty));
        }

        [Test]
        public void CreateForRoomSpawn_CorrectFormatting()
        {
            var id = EntityId.CreateForRoomSpawn("r_03_07", "enemy_grunt", 2);

            Assert.IsTrue(id.IsValid);
            Assert.AreEqual("r_03_07:enemy_grunt:002", id.ToString());
            Assert.AreEqual("r_03_07:enemy_grunt:002", id.DebugString);
            Assert.AreEqual(EntityId.ComputeFnv1a64("r_03_07:enemy_grunt:002"), id.Hash);
        }

        [Test]
        public void CreateForPlayer_CorrectFormatting()
        {
            var p0 = EntityId.CreateForPlayer(0);
            var p1 = EntityId.CreateForPlayer(1);

            Assert.IsTrue(p0.IsValid);
            Assert.AreEqual("player:0", p0.ToString());
            Assert.AreEqual("player:1", p1.ToString());
            Assert.AreNotEqual(p0, p1);
        }

        [Test]
        public void CreateRuntime_CorrectFormatting()
        {
            var id = EntityId.CreateRuntime(1420, 1);

            Assert.IsTrue(id.IsValid);
            Assert.AreEqual("runtime:1420:001", id.ToString());
            Assert.AreEqual(EntityId.ComputeFnv1a64("runtime:1420:001"), id.Hash);
        }

        [Test]
        public void FromString_RoundTripsAccurately()
        {
            var original = EntityId.CreateForRoomSpawn("r_02_05", "chest", 3);
            var parsed = EntityId.FromString(original.ToString());

            Assert.AreEqual(original.Hash, parsed.Hash);
            Assert.AreEqual(original.DebugString, parsed.DebugString);
            Assert.AreEqual(original, parsed);

            var empty = EntityId.FromString("");
            Assert.IsFalse(empty.IsValid);
            Assert.AreEqual(EntityId.Empty, empty);
        }

        [Test]
        public void EqualityAndComparisonOperators()
        {
            var idA = EntityId.CreateForRoomSpawn("r1", "box", 1);
            var idB = EntityId.CreateForRoomSpawn("r1", "box", 1);
            var idC = EntityId.CreateForRoomSpawn("r1", "box", 2);

            Assert.IsTrue(idA == idB);
            Assert.IsFalse(idA != idB);
            Assert.IsTrue(idA != idC);
            Assert.IsTrue(idA.Equals(idB));
            Assert.AreEqual(idA.GetHashCode(), idB.GetHashCode());
        }

        [Test]
        public void EntityRegistry_RegisterLookupUnregister()
        {
            IEntityRegistry registry = new EntityRegistry();
            var id1 = EntityId.CreateForPlayer(0);
            var id2 = EntityId.CreateForRoomSpawn("r1", "enemy", 1);
            object dummy1 = "PlayerObject";
            object dummy2 = "EnemyObject";

            registry.Register(id1, dummy1);
            registry.Register(id2, dummy2);

            Assert.AreEqual(2, registry.Count);
            Assert.IsTrue(registry.Contains(id1));
            Assert.IsTrue(registry.Contains(id2));

            Assert.IsTrue(registry.TryGetEntity<string>(id1, out var found1));
            Assert.AreEqual(dummy1, found1);

            Assert.IsTrue(registry.TryGetEntity<string>(id2, out var found2));
            Assert.AreEqual(dummy2, found2);

            Assert.IsTrue(registry.Unregister(id1));
            Assert.IsFalse(registry.Contains(id1));
            Assert.AreEqual(1, registry.Count);

            registry.Clear();
            Assert.AreEqual(0, registry.Count);
        }

        [Test]
        public void SpawnSequence_IsDeterministic()
        {
            List<EntityId> run1 = new List<EntityId>();
            List<EntityId> run2 = new List<EntityId>();

            for (int i = 0; i < 50; i++)
            {
                run1.Add(EntityId.CreateForRoomSpawn("r_test", "item", i));
            }

            for (int i = 0; i < 50; i++)
            {
                run2.Add(EntityId.CreateForRoomSpawn("r_test", "item", i));
            }

            CollectionAssert.AreEqual(run1, run2);
        }
    }
}
