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

        // ── `shelf` is a FLAG, not a physics assignment ──────────────────────────────────
        //
        // AS3 assigns `shelf` independently of `phis` (Tile.as:186-189) and its only consumer reads
        // it when `phis == 0 || phis == 3` (Box.as:1262, 1270). The port used to write
        // `physicsType = Platform` unconditionally, so a beam overlay on a wall demoted the wall:
        // it stopped blocking light and stopped blocking the sight ray. `Decode_ShelfOverlayOnAWall`
        // is the regression; the other two are the controls that stop the guard from over-reaching.

        [Test]
        public void Decode_ShelfOverlayOnAWall_DoesNotDemoteTheWall()
        {
            TileFormDatabase database = CreateShelfDatabase();

            // 'C' is an fForm with phis=1 (a wall); 'Д' is an oForm with shelf=true, phis=0.
            TileData tile = TileDecoder.Decode("C\u0414", 0, 0, database);

            Assert.AreEqual(TilePhysicsType.Wall, tile.physicsType,
                "A shelf flag on a wall is inert in AS3 (Box.as:1262 gates it on phis == 0 || phis == 3), " +
                "so the wall must survive — it still blocks light and line of sight.");
            Assert.IsTrue(tile.isLedge, "The flag itself is still recorded for rendering.");
        }

        [Test]
        public void Decode_ShelfOverlayOnOpenAir_IsStillAPlatform()
        {
            // Control 1: the flag keeps its real scope. If the guard had been written as "ignore
            // shelf entirely", this fails.
            TileFormDatabase database = CreateShelfDatabase();

            TileData tile = TileDecoder.Decode("_\u0414", 0, 0, database);

            Assert.AreEqual(TilePhysicsType.Platform, tile.physicsType);
            Assert.IsTrue(tile.isLedge);
        }

        [Test]
        public void Decode_ShelfOverlayOnALadder_IsStillAPlatform()
        {
            // Control 2: a ladder carries `phis == 0`, so the shelf flag IS meaningful on it — the
            // guard is `!= Wall`, not `== Air`. `stairType` survives, so the tile stays climbable:
            // in AS3 it is `phis 0` + `shelf` + `stair`, i.e. a one-way platform you can also climb.
            TileFormDatabase database = CreateShelfDatabase();

            TileData tile = TileDecoder.Decode("_\u0410\u0414", 0, 0, database);

            Assert.AreEqual(TilePhysicsType.Platform, tile.physicsType,
                "The ladder's `Stair` may be overwritten by the shelf — both are `phis == 0`.");
            Assert.AreEqual(1, tile.stairType, "…but the climb metadata must survive the overwrite.");
            Assert.IsTrue(tile.IsClimbableLadder());
            Assert.IsTrue(tile.isLedge);
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

        /// <summary>
        /// A wall fForm (<c>phis = 1</c>) plus a shelf overlay (<c>shelf = true, phis = 0</c>) and a
        /// ladder overlay (<c>stair = 1, phis = 0</c>).
        ///
        /// <para>Kept separate from <see cref="CreateTestDatabase"/> on purpose: that fixture's 'A'
        /// has no <c>phis</c> at all, i.e. it is <c>phis == 0</c>, which is exactly why its ladder
        /// decodes to <c>Stair</c>. Giving 'A' a <c>phis</c> here would silently change those two
        /// tests.</para>
        /// </summary>
        private static TileFormDatabase CreateShelfDatabase()
        {
            TileFormDatabase database = ScriptableObject.CreateInstance<TileFormDatabase>();
            database.AddFForm(new TileForm
            {
                id = "C",
                ed = 1,
                phis = 1
            });
            database.AddOForm(new TileForm
            {
                id = "\u0410",
                ed = 3,
                vid = 3,
                stair = 1
            });
            database.AddOForm(new TileForm
            {
                id = "\u0414",
                ed = 4,
                vid = 2,
                shelf = true
            });
            database.Initialize();
            return database;
        }
    }
}
