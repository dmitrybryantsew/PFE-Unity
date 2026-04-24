using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Unit tests for TileData class.
    /// Tests tile physics, bounds, slopes, and destruction.
    /// </summary>
    [TestFixture]
    public class TileDataTests
    {
        [Test]
        public void TileData_DefaultValues_AreCorrect()
        {
            TileData tile = new TileData();
            Assert.AreEqual(TilePhysicsType.Air, tile.physicsType);
            Assert.AreEqual(0, tile.visualId);
            Assert.AreEqual(0, tile.visualId2);
            Assert.AreEqual(0, tile.heightLevel);
            Assert.AreEqual(0, tile.slopeType);
            Assert.IsFalse(tile.indestructible);
            Assert.AreEqual(1000, tile.hitPoints);
            Assert.AreEqual(MaterialType.Default, tile.material);
            Assert.IsTrue(tile.canPlaceObjects);
        }

        [Test]
        public void GetBounds_ReturnsCorrectRect()
        {
            TileData tile = new TileData
            {
                gridPosition = new Vector2Int(5, 10),
                heightLevel = 0
            };

            Rect bounds = tile.GetBounds();
            Assert.AreEqual(200f, bounds.xMin, 0.001f); // 5 * 40
            Assert.AreEqual(240f, bounds.xMax, 0.001f); // (5 + 1) * 40
            Assert.AreEqual(400f, bounds.yMin, 0.001f); // 10 * 40
            Assert.AreEqual(440f, bounds.yMax, 0.001f); // (10 + 1) * 40
            Assert.AreEqual(40f, bounds.width, 0.001f);
            Assert.AreEqual(40f, bounds.height, 0.001f);
        }

        [Test]
        public void GetBounds_WithHeightLevel_AdjustsY()
        {
            TileData tile = new TileData
            {
                gridPosition = new Vector2Int(0, 10),
                heightLevel = 1
            };

            Rect bounds = tile.GetBounds();
            // Y position should be raised by 1/4 of tile height
            float expectedY = 10 * 40f + 10f; // Y * 40 + heightLevel * 10
            Assert.AreEqual(expectedY, bounds.yMin, 0.001f);
        }

        [Test]
        public void GetGroundHeight_Flat_ReturnsYMin()
        {
            TileData tile = new TileData
            {
                gridPosition = new Vector2Int(0, 0),
                slopeType = 0
            };

            float height = tile.GetGroundHeight(100);
            Assert.AreEqual(0f, height, 0.001f);
        }

        [Test]
        public void GetGroundHeight_SlopeUp_CalculatesCorrectly()
        {
            TileData tile = new TileData
            {
                gridPosition = new Vector2Int(0, 0),
                slopeType = 1 // Slope / (low left to high right)
            };

            Rect bounds = tile.GetBounds();

            // At left edge, should be at bottom
            float height = tile.GetGroundHeight(bounds.xMin);
            Assert.AreEqual(bounds.yMax, height, 0.001f);

            // At right edge, should be at top
            height = tile.GetGroundHeight(bounds.xMax);
            Assert.AreEqual(bounds.yMin, height, 0.001f);

            // At middle, should be halfway
            height = tile.GetGroundHeight(bounds.center.x);
            Assert.AreEqual(bounds.center.y, height, 0.001f);
        }

        [Test]
        public void GetGroundHeight_SlopeDown_CalculatesCorrectly()
        {
            TileData tile = new TileData
            {
                gridPosition = new Vector2Int(0, 0),
                slopeType = -1 // Slope \ (high left to low right)
            };

            Rect bounds = tile.GetBounds();

            // At left edge, should be at top
            float height = tile.GetGroundHeight(bounds.xMin);
            Assert.AreEqual(bounds.yMin, height, 0.001f);

            // At right edge, should be at bottom
            height = tile.GetGroundHeight(bounds.xMax);
            Assert.AreEqual(bounds.yMax, height, 0.001f);

            // At middle, should be halfway
            height = tile.GetGroundHeight(bounds.center.x);
            Assert.AreEqual(bounds.center.y, height, 0.001f);
        }

        [Test]
        public void IsSolid_Wall_ReturnsTrue()
        {
            TileData tile = new TileData
            {
                physicsType = TilePhysicsType.Wall
            };
            Assert.IsTrue(tile.IsSolid());
        }

        [Test]
        public void IsSolid_Air_ReturnsFalse()
        {
            TileData tile = new TileData
            {
                physicsType = TilePhysicsType.Air
            };
            Assert.IsFalse(tile.IsSolid());
        }

        [Test]
        public void IsPlatform_Platform_ReturnsTrue()
        {
            TileData tile = new TileData
            {
                physicsType = TilePhysicsType.Platform
            };
            Assert.IsTrue(tile.IsPlatform());
        }

        [Test]
        public void IsPlatform_Wall_ReturnsFalse()
        {
            TileData tile = new TileData
            {
                physicsType = TilePhysicsType.Wall
            };
            Assert.IsFalse(tile.IsPlatform());
        }

        [Test]
        public void IsStair_StairType_ReturnsTrue()
        {
            TileData tile = new TileData
            {
                physicsType = TilePhysicsType.Stair
            };
            Assert.IsTrue(tile.IsStair());
        }

        [Test]
        public void IsStair_SlopeType_ReturnsTrue()
        {
            TileData tile = new TileData
            {
                physicsType = TilePhysicsType.Air,
                slopeType = 1
            };
            Assert.IsTrue(tile.IsStair());
        }

        [Test]
        public void TakeDamage_DestructibleTile_ReducesHP()
        {
            TileData tile = new TileData
            {
                hitPoints = 1000,
                indestructible = false,
                damageThreshold = 0
            };

            bool applied = tile.TakeDamage(100);
            Assert.IsTrue(applied);
            Assert.AreEqual(900, tile.hitPoints);
        }

        [Test]
        public void TakeDamage_IndestructibleTile_DoesNotReduceHP()
        {
            TileData tile = new TileData
            {
                hitPoints = 1000,
                indestructible = true,
                damageThreshold = 0
            };

            bool applied = tile.TakeDamage(100);
            Assert.IsFalse(applied);
            Assert.AreEqual(1000, tile.hitPoints);
        }

        [Test]
        public void TakeDamage_BelowThreshold_DoesNotReduceHP()
        {
            TileData tile = new TileData
            {
                hitPoints = 1000,
                indestructible = false,
                damageThreshold = 50
            };

            bool applied = tile.TakeDamage(30); // Below threshold
            Assert.IsFalse(applied);
            Assert.AreEqual(1000, tile.hitPoints);

            applied = tile.TakeDamage(60); // Above threshold
            Assert.IsTrue(applied);
            Assert.AreEqual(940, tile.hitPoints);
        }

        [Test]
        public void IsDestroyed_ZeroHP_ReturnsTrue()
        {
            TileData tile = new TileData
            {
                hitPoints = 0,
                indestructible = false
            };
            Assert.IsTrue(tile.IsDestroyed());
        }

        [Test]
        public void IsDestroyed_Indestructible_ReturnsFalse()
        {
            TileData tile = new TileData
            {
                hitPoints = 0,
                indestructible = true
            };
            Assert.IsFalse(tile.IsDestroyed());
        }

        [Test]
        public void Destroy_SetsTileToAir()
        {
            TileData tile = new TileData
            {
                physicsType = TilePhysicsType.Wall,
                visualId = 5,
                opacity = 1f
            };

            tile.Destroy();

            Assert.AreEqual(TilePhysicsType.Air, tile.physicsType);
            Assert.AreEqual(0f, tile.opacity);
            Assert.AreEqual(0, tile.visualId);
            Assert.AreEqual(0, tile.visualId2);
            Assert.AreEqual(0, tile.hitPoints);
        }

        [Test]
        public void Reset_RestoresHPForDestructible()
        {
            TileData tile = new TileData
            {
                hitPoints = 100,
                indestructible = false
            };

            tile.Reset();
            Assert.AreEqual(1000, tile.hitPoints);
        }

        [Test]
        public void Reset_DoesNotRestoreHPForIndestructible()
        {
            TileData tile = new TileData
            {
                hitPoints = 100,
                indestructible = true
            };

            tile.Reset();
            Assert.AreEqual(100, tile.hitPoints); // Should not change
        }

        [Test]
        public void GraphicGettersAndSetters_WorkCorrectly()
        {
            TileData tile = new TileData();

            tile.SetFrontGraphic("tile_wall_01");
            Assert.AreEqual("tile_wall_01", tile.GetFrontGraphic());

            tile.SetBackGraphic("tile_back_01");
            Assert.AreEqual("tile_back_01", tile.GetBackGraphic());
        }

        [Test]
        public void HeightLevel_AffectsGroundPosition()
        {
            TileData tile1 = new TileData
            {
                gridPosition = new Vector2Int(0, 10),
                heightLevel = 0
            };

            TileData tile2 = new TileData
            {
                gridPosition = new Vector2Int(0, 10),
                heightLevel = 2
            };

            Rect bounds1 = tile1.GetBounds();
            Rect bounds2 = tile2.GetBounds();

            // heightLevel 2 should be 20 pixels higher
            Assert.AreEqual(20f, bounds2.yMin - bounds1.yMin, 0.001f);
        }

        [Test]
        public void AllPhysicsTypes_AreHandled()
        {
            TileData tile = new TileData();

            // Test all enum values
            tile.physicsType = TilePhysicsType.Air;
            Assert.IsFalse(tile.IsSolid());

            tile.physicsType = TilePhysicsType.Wall;
            Assert.IsTrue(tile.IsSolid());

            tile.physicsType = TilePhysicsType.Platform;
            Assert.IsTrue(tile.IsSolid());
            Assert.IsTrue(tile.IsPlatform());

            tile.physicsType = TilePhysicsType.Stair;
            Assert.IsTrue(tile.IsSolid());
            Assert.IsTrue(tile.IsStair());
        }

        [Test]
        public void SlopeGroundHeight_OutOfBounds_ClampsCorrectly()
        {
            TileData tile = new TileData
            {
                gridPosition = new Vector2Int(0, 0),
                slopeType = 1
            };

            Rect bounds = tile.GetBounds();

            // Far left of tile
            float height = tile.GetGroundHeight(bounds.xMin - 100);
            Assert.AreEqual(bounds.yMax, height, 0.001f);

            // Far right of tile
            height = tile.GetGroundHeight(bounds.xMax + 100);
            Assert.AreEqual(bounds.yMin, height, 0.001f);
        }
    }
}
