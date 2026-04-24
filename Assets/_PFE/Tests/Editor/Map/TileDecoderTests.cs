using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    [TestFixture]
    public class TileDecoderTests
    {
        [Test]
        public void ParseRoom_StairTileWithDifferentTileAbove_PromotesShelfVariant()
        {
            TileFormDatabase database = CreateTestDatabase();

            TileData[,] tiles = TileDecoder.ParseRoom(
                new[] { "_", "AА", "AА" },
                database,
                mirror: false,
                roomWidth: 1,
                roomHeight: 3);

            TileData capTile = tiles[0, 1];
            TileData lowerTile = tiles[0, 0];

            Assert.AreEqual(TilePhysicsType.Stair, capTile.physicsType);
            Assert.AreEqual(1, capTile.stairType);
            Assert.IsTrue(capTile.isLedge);
            Assert.AreEqual(4, capTile.visualId);
            Assert.IsFalse(lowerTile.isLedge);
            Assert.AreEqual(3, lowerTile.visualId);
        }

        [Test]
        public void ParseRoom_StairTileWithMatchingTileAbove_DoesNotPromoteShelfVariant()
        {
            TileFormDatabase database = CreateTestDatabase();

            TileData[,] tiles = TileDecoder.ParseRoom(
                new[] { "AА", "AА", "_" },
                database,
                mirror: false,
                roomWidth: 1,
                roomHeight: 3);

            TileData middleTile = tiles[0, 1];

            Assert.AreEqual(TilePhysicsType.Stair, middleTile.physicsType);
            Assert.AreEqual(1, middleTile.stairType);
            Assert.IsFalse(middleTile.isLedge);
            Assert.AreEqual(3, middleTile.visualId);
        }

        private static TileFormDatabase CreateTestDatabase()
        {
            TileFormDatabase database = ScriptableObject.CreateInstance<TileFormDatabase>();
            database.AddFForm(new TileForm
            {
                id = "A",
                ed = 1
            });
            database.AddOForm(new TileForm
            {
                id = "А",
                ed = 3,
                vid = 3,
                stair = 1
            });
            database.AddOForm(new TileForm
            {
                id = "Б",
                ed = 3,
                vid = 1,
                stair = -1
            });
            database.Initialize();
            return database;
        }
    }
}
