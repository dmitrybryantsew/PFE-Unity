using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Tests for TileCollisionSystem.
    /// Tests tile-based collision detection, slopes, platforms, and destruction.
    /// Based on AS3 collision behavior from Location.as and Box.as.
    /// </summary>
    [TestFixture]
    public class TileCollisionSystemTests
    {
        private RoomInstance testRoom;
        private TileCollisionSystem collisionSystem;

        [SetUp]
        public void SetUp()
        {
            testRoom = new RoomInstance
            {
                width = 10,
                height = 10,
                tiles = new TileData[10, 10]
            };

            // Initialize all tiles as air
            for (int x = 0; x < 10; x++)
            {
                for (int y = 0; y < 10; y++)
                {
                    testRoom.tiles[x, y] = new TileData
                    {
                        gridPosition = new Vector2Int(x, y),
                        physicsType = TilePhysicsType.Air
                    };
                }
            }

            collisionSystem = new TileCollisionSystem(testRoom);
        }

        [TearDown]
        public void TearDown()
        {
            testRoom = null;
            collisionSystem = null;
        }

        // Helper to create a tile
        private TileData CreateTile(int x, int y, TilePhysicsType physicsType)
        {
            return new TileData
            {
                gridPosition = new Vector2Int(x, y),
                physicsType = physicsType
            };
        }

        // Helper to create a wall tile
        private TileData CreateWall(int x, int y)
        {
            return CreateTile(x, y, TilePhysicsType.Wall);
        }

        // Helper to create a platform tile
        private TileData CreatePlatform(int x, int y)
        {
            return CreateTile(x, y, TilePhysicsType.Platform);
        }

        // Helper to create a stair tile
        private TileData CreateStair(int x, int y)
        {
            return CreateTile(x, y, TilePhysicsType.Stair);
        }

        [Test]
        public void CheckCollision_AirTile_NoCollision()
        {
            // Arrange
            Rect bounds = new Rect(50f, 50f, 20f, 20f);

            // Act
            bool result = collisionSystem.CheckCollision(bounds);

            // Assert
            Assert.IsFalse(result, "Air tile should not collide");
        }

        [Test]
        public void CheckCollision_WallTile_Collides()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreateWall(2, 2);
            Rect bounds = new Rect(90f, 90f, 20f, 20f); // Overlaps with tile at (2,2)

            // Act
            bool result = collisionSystem.CheckCollision(bounds);

            // Assert
            Assert.IsTrue(result, "Wall tile should collide");
        }

        [Test]
        public void CheckCollision_WallTile_NoOverlap_NoCollision()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreateWall(2, 2);
            Rect bounds = new Rect(150f, 150f, 20f, 20f); // Far away

            // Act
            bool result = collisionSystem.CheckCollision(bounds);

            // Assert
            Assert.IsFalse(result, "Wall tile far away should not collide");
        }

        [Test]
        public void CheckCollision_Platform_FromAbove_Collides()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreatePlatform(2, 2);
            // Tile at (2,2) has bounds: x=80-120, y=80-120
            // Entity just above platform: bottom at Y=118 (just 2px below tile top at Y=120)
            // This ensures entity overlaps with tile (2,2) in the grid check
            // Entity bounds: yMin=118, yMax=138 (20px tall)
            Rect bounds = new Rect(90f, 118f, 20f, 20f);

            // Act
            // Falling downward means negative velocityY
            bool result = collisionSystem.CheckCollision(bounds, canFallThroughPlatforms: false, velocityY: -5f);

            // Assert
            Assert.IsTrue(result, "Platform should collide when landing from above");
        }

        [Test]
        public void CheckCollision_Platform_FromBelow_NoCollision()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreatePlatform(2, 2);
            // Entity is below the platform: top at Y=78 (below tile bottom at Y=80)
            // Entity bounds: yMin=58, yMax=78 (20px tall)
            Rect bounds = new Rect(90f, 58f, 20f, 20f);

            // Act
            // Moving upward means positive velocityY
            bool result = collisionSystem.CheckCollision(bounds, canFallThroughPlatforms: false, velocityY: 5f);

            // Assert
            Assert.IsFalse(result, "Platform should not collide when jumping from below");
        }

        [Test]
        public void CheckCollision_Platform_CanFallThrough_NoCollision()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreatePlatform(2, 2);
            Rect bounds = new Rect(90f, 118f, 20f, 20f);

            // Act
            bool result = collisionSystem.CheckCollision(bounds, canFallThroughPlatforms: true);

            // Assert
            Assert.IsFalse(result, "Platform should not collide when can fall through");
        }

        [Test]
        public void CheckCollision_Stair_NormalEntity_Collides()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreateStair(2, 2);
            Rect bounds = new Rect(90f, 90f, 20f, 20f);

            // Act
            bool result = collisionSystem.CheckCollision(bounds, isTransparent: false);

            // Assert
            Assert.IsTrue(result, "Stair should collide with normal entity");
        }

        [Test]
        public void CheckCollision_Stair_TransparentEntity_NoCollision()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreateStair(2, 2);
            Rect bounds = new Rect(90f, 90f, 20f, 20f);

            // Act
            bool result = collisionSystem.CheckCollision(bounds, isTransparent: true);

            // Assert
            Assert.IsFalse(result, "Stair should not collide with transparent entity");
        }

        [Test]
        public void GetGroundHeight_FlatTile_ReturnsTileBottom()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreateWall(2, 2);
            // Position that maps to tile (2,2): x in [80,120), y in [80,120)
            Vector2 position = new Vector2(90f, 90f);

            // Act
            float groundHeight = collisionSystem.GetGroundHeight(position);

            // Assert
            // Tile at (2,2) has yMin = 2 * 40 = 80
            Assert.AreEqual(80f, groundHeight, 0.01f, "Ground height should be at bottom of flat tile");
        }

        [Test]
        public void GetGroundHeight_SlopeForward_ReturnsCorrectHeight()
        {
            // Arrange
            var slopeTile = new TileData
            {
                gridPosition = new Vector2Int(2, 2),
                physicsType = TilePhysicsType.Wall,
                slopeType = 1 // / slope (high at left/yMax, low at right/yMin)
            };
            testRoom.tiles[2, 2] = slopeTile;

            // Test at left edge INSIDE tile (should be near yMax = 120)
            // Position must be in tile (2,2): x in [80,120), y in [80,120)
            float leftHeight = collisionSystem.GetGroundHeight(new Vector2(85f, 90f));

            // Test at right edge INSIDE tile (should be near yMin = 80)
            float rightHeight = collisionSystem.GetGroundHeight(new Vector2(115f, 90f));

            // Assert - verify slope direction (left higher than right)
            Assert.Greater(leftHeight, rightHeight, "Left edge should be higher than right edge on / slope");
            // At X=85 (5px from left edge of 80-120), height should be 120 - 5*(40/40) = 115
            Assert.AreEqual(115f, leftHeight, 1f, "Left edge should be near yMax");
            // At X=115 (35px from left edge), height should be 120 - 35 = 85
            Assert.AreEqual(85f, rightHeight, 1f, "Right edge should be near yMin");
        }

        [Test]
        public void GetGroundHeight_SlopeBackward_ReturnsCorrectHeight()
        {
            // Arrange
            var slopeTile = new TileData
            {
                gridPosition = new Vector2Int(2, 2),
                physicsType = TilePhysicsType.Wall,
                slopeType = -1 // \ slope (low at left/yMin, high at right/yMax)
            };
            testRoom.tiles[2, 2] = slopeTile;

            // Test at left edge INSIDE tile (should be near yMin = 80)
            float leftHeight = collisionSystem.GetGroundHeight(new Vector2(85f, 90f));

            // Test at right edge INSIDE tile (should be near yMax = 120)
            float rightHeight = collisionSystem.GetGroundHeight(new Vector2(115f, 90f));

            // Assert - verify slope direction (left lower than right)
            Assert.Less(leftHeight, rightHeight, "Left edge should be lower than right edge on \\ slope");
            // At X=85 (5px from left edge), height should be 80 + 5*(40/40) = 85
            Assert.AreEqual(85f, leftHeight, 1f, "Left edge should be near yMin");
            // At X=115 (35px from left edge), height should be 80 + 35 = 115
            Assert.AreEqual(115f, rightHeight, 1f, "Right edge should be near yMax");
        }

        [Test]
        public void IsOnGround_OnTile_ReturnsTrue()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreateWall(2, 2);
            // Entity bottom at Y=80 matches tile ground height (yMin)
            Rect bounds = new Rect(90f, 80f, 20f, 20f);

            // Act
            bool onGround = collisionSystem.IsOnGround(bounds);

            // Assert
            Assert.IsTrue(onGround, "Should be on ground when sitting on tile");
        }

        [Test]
        public void IsOnGround_AboveTile_ReturnsFalse()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreateWall(2, 2);
            Rect bounds = new Rect(90f, 50f, 20f, 20f); // Above tile

            // Act
            bool onGround = collisionSystem.IsOnGround(bounds);

            // Assert
            Assert.IsFalse(onGround, "Should not be on ground when above tile");
        }

        [Test]
        public void ApplyDamage_DestructibleTile_Damages()
        {
            // Arrange
            var tile = new TileData
            {
                gridPosition = new Vector2Int(2, 2),
                physicsType = TilePhysicsType.Wall,
                hitPoints = 100,
                indestructible = false
            };
            testRoom.tiles[2, 2] = tile;

            // Act - radiusTiles=0 to only hit the specific tile
            bool damaged = collisionSystem.ApplyDamage(new Vector2(90f, 90f), 50, radiusTiles: 0);

            // Assert
            Assert.IsTrue(damaged, "Should damage destructible tile");
            Assert.AreEqual(50, tile.hitPoints, "Tile should have 50 HP remaining");
        }

        [Test]
        public void ApplyDamage_IndestructibleTile_NoDamage()
        {
            // Arrange
            var tile = new TileData
            {
                gridPosition = new Vector2Int(2, 2),
                physicsType = TilePhysicsType.Wall,
                hitPoints = 100,
                indestructible = true
            };
            testRoom.tiles[2, 2] = tile;

            // Act - radiusTiles=0 to only hit the specific tile
            bool damaged = collisionSystem.ApplyDamage(new Vector2(90f, 90f), 50, radiusTiles: 0);

            // Assert
            Assert.IsFalse(damaged, "Should not damage indestructible tile");
            Assert.AreEqual(100, tile.hitPoints, "Indestructible tile should keep full HP");
        }

        [Test]
        public void ApplyDamage_DamageThresholdTooLow_NoDamage()
        {
            // Arrange
            var tile = new TileData
            {
                gridPosition = new Vector2Int(2, 2),
                physicsType = TilePhysicsType.Wall,
                hitPoints = 100,
                damageThreshold = 60 // Requires 60+ damage
            };
            testRoom.tiles[2, 2] = tile;

            // Act - radiusTiles=0 to only hit the specific tile
            bool damaged = collisionSystem.ApplyDamage(new Vector2(90f, 90f), 50, radiusTiles: 0);

            // Assert
            Assert.IsFalse(damaged, "Should not damage below threshold");
            Assert.AreEqual(100, tile.hitPoints, "Tile should keep full HP");
        }

        [Test]
        public void ApplyDamage_FatalDamage_Destroyes()
        {
            // Arrange
            var tile = new TileData
            {
                gridPosition = new Vector2Int(2, 2),
                physicsType = TilePhysicsType.Wall,
                hitPoints = 50,
                indestructible = false,
                visualId = 42
            };
            testRoom.tiles[2, 2] = tile;

            // Act - radiusTiles=0 to only hit the specific tile
            bool damaged = collisionSystem.ApplyDamage(new Vector2(90f, 90f), 100, radiusTiles: 0);

            // Assert
            Assert.IsTrue(damaged, "Should apply damage");
            Assert.AreEqual(TilePhysicsType.Air, tile.physicsType, "Tile should be destroyed to air");
            Assert.AreEqual(0, tile.hitPoints, "HP should be 0");
            Assert.AreEqual(0, tile.visualId, "Visual should be cleared");
        }

        [Test]
        public void Raycast_ThroughAir_NoHit()
        {
            // Arrange
            Vector2 origin = new Vector2(50f, 50f);
            Vector2 direction = new Vector2(1f, 0f); // Right
            float maxDistance = 200f;

            // Act
            var result = collisionSystem.Raycast(origin, direction, maxDistance);

            // Assert
            Assert.IsNull(result, "Raycast through air should not hit anything");
        }

        [Test]
        public void Raycast_HitWall_Hits()
        {
            // Arrange
            testRoom.tiles[5, 1] = CreateWall(5, 1);
            Vector2 origin = new Vector2(50f, 50f);
            Vector2 direction = new Vector2(1f, 0f); // Right
            float maxDistance = 200f;

            // Act
            var result = collisionSystem.Raycast(origin, direction, maxDistance);

            // Assert
            Assert.IsNotNull(result, "Raycast should hit wall");
            Assert.AreEqual(TilePhysicsType.Wall, result.Value.tile.physicsType, "Should hit wall tile");
        }

        [Test]
        public void CheckTileCollision_NullTile_NoCollision()
        {
            // Arrange
            Rect bounds = new Rect(90f, 90f, 20f, 20f);

            // Act
            bool result = collisionSystem.CheckTileCollision(null, bounds);

            // Assert
            Assert.IsFalse(result, "Null tile should not collide");
        }

        [Test]
        public void CheckTileCollision_NoOverlap_NoCollision()
        {
            // Arrange
            var tile = CreateWall(2, 2);
            Rect bounds = new Rect(150f, 150f, 20f, 20f); // Far away

            // Act
            bool result = collisionSystem.CheckTileCollision(tile, bounds);

            // Assert
            Assert.IsFalse(result, "Tile with no overlap should not collide");
        }

        [Test]
        public void HeightLevel_AffectsBounds()
        {
            // Arrange
            var tile0 = new TileData
            {
                gridPosition = new Vector2Int(2, 2),
                physicsType = TilePhysicsType.Wall,
                heightLevel = 0
            };

            var tile1 = new TileData
            {
                gridPosition = new Vector2Int(2, 2),
                physicsType = TilePhysicsType.Wall,
                heightLevel = 1
            };

            // Act
            Rect bounds0 = tile0.GetBounds();
            Rect bounds1 = tile1.GetBounds();

            // Assert
            Assert.AreEqual(80f, bounds0.yMin, "Height level 0 should start at Y=80");
            Assert.Greater(bounds1.yMin, bounds0.yMin, "Height level 1 should be higher than level 0");
        }

        [Test]
        public void PlatformCollision_Threshold_WorksCorrectly()
        {
            // Arrange
            testRoom.tiles[2, 2] = CreatePlatform(2, 2);
            Rect tileBounds = testRoom.tiles[2, 2].GetBounds(); // y=80-120, so top is yMax=120

            // Far above threshold (entity bottom at Y=135, more than 10px above tile top at Y=120)
            // Position at y=135 means entity spans y=135-155, which overlaps with tile Y=2 (80-120) and Y=3 (120-160)
            Rect farAbove = new Rect(90f, 135f, 20f, 20f);
            bool result1 = collisionSystem.CheckCollision(farAbove, canFallThroughPlatforms: false);

            // Within threshold (entity bottom at Y=118, just 2px below tile top at Y=120, but still overlapping with tile)
            Rect withinThreshold = new Rect(90f, 118f, 20f, 20f);
            bool result2 = collisionSystem.CheckCollision(withinThreshold, canFallThroughPlatforms: false, velocityY: -5f);

            // Assert
            Assert.IsFalse(result1, "Should not collide when far above threshold");
            Assert.IsTrue(result2, "Should collide when within threshold and falling");
        }
    }
}
