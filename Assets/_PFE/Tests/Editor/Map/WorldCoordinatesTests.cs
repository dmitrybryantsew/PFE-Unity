using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Unit tests for WorldCoordinates conversion utilities.
    /// Tests all coordinate system conversions with AS3 reference values.
    /// </summary>
    [TestFixture]
    public class WorldCoordinatesTests
    {
        [Test]
        public void LandToWorld_ConvertsCorrectly()
        {
            // Test: Land (0,0,0) + local (0,0) = world (0,0)
            Vector2 result = WorldCoordinates.LandToWorld(new Vector3Int(0, 0, 0), new Vector2(0, 0));
            Assert.AreEqual(0f, result.x, 0.001f);
            Assert.AreEqual(0f, result.y, 0.001f);

            // Test: Land (1,0,0) + local (0,0) = world (1920,0) - one room to the right
            result = WorldCoordinates.LandToWorld(new Vector3Int(1, 0, 0), new Vector2(0, 0));
            Assert.AreEqual(1920f, result.x, 0.001f);
            Assert.AreEqual(0f, result.y, 0.001f);

            // Test: Land (0,1,0) + local (0,0) = world (0,1080) - one room down
            result = WorldCoordinates.LandToWorld(new Vector3Int(0, 1, 0), new Vector2(0, 0));
            Assert.AreEqual(0f, result.x, 0.001f);
            Assert.AreEqual(1080f, result.y, 0.001f);

            // Test: Land (1,1,0) + local (100,50) = world (2020,1130)
            result = WorldCoordinates.LandToWorld(new Vector3Int(1, 1, 0), new Vector2(100, 50));
            Assert.AreEqual(2020f, result.x, 0.001f);
            Assert.AreEqual(1130f, result.y, 0.001f);
        }

        [Test]
        public void WorldToLand_ConvertsCorrectly()
        {
            // Test: World (0,0) -> Land (0,0,0)
            Vector3Int result = WorldCoordinates.WorldToLand(new Vector2(0, 0));
            Assert.AreEqual(0, result.x);
            Assert.AreEqual(0, result.y);
            Assert.AreEqual(0, result.z);

            // Test: World (1000,500) -> Land (0,0,0) - still in first room
            result = WorldCoordinates.WorldToLand(new Vector2(1000, 500));
            Assert.AreEqual(0, result.x);
            Assert.AreEqual(0, result.y);

            // Test: World (1920,0) -> Land (1,0,0) - at edge of first room
            result = WorldCoordinates.WorldToLand(new Vector2(1920, 0));
            Assert.AreEqual(1, result.x);
            Assert.AreEqual(0, result.y);

            // Test: World (2000,1200) -> Land (1,1,0)
            result = WorldCoordinates.WorldToLand(new Vector2(2000, 1200));
            Assert.AreEqual(1, result.x);
            Assert.AreEqual(1, result.y);
        }

        [Test]
        public void WorldToLocal_ConvertsCorrectly()
        {
            // Test: World (100,50) -> Local (100,50)
            Vector2 result = WorldCoordinates.WorldToLocal(new Vector2(100, 50));
            Assert.AreEqual(100f, result.x, 0.001f);
            Assert.AreEqual(50f, result.y, 0.001f);

            // Test: World (2000,100) -> Local (80,100) - wrapped around room width
            result = WorldCoordinates.WorldToLocal(new Vector2(2000, 100));
            Assert.AreEqual(80f, result.x, 0.001f);
            Assert.AreEqual(100f, result.y, 0.001f);
        }

        [Test]
        public void PixelToTile_ConvertsCorrectly()
        {
            // Test: Pixel (0,0) -> Tile (0,0)
            Vector2Int result = WorldCoordinates.PixelToTile(new Vector2(0, 0));
            Assert.AreEqual(0, result.x);
            Assert.AreEqual(0, result.y);

            // Test: Pixel (40,0) -> Tile (1,0) - exactly one tile over
            result = WorldCoordinates.PixelToTile(new Vector2(40, 0));
            Assert.AreEqual(1, result.x);
            Assert.AreEqual(0, result.y);

            // Test: Pixel (39,39) -> Tile (0,0) - still in first tile
            result = WorldCoordinates.PixelToTile(new Vector2(39, 39));
            Assert.AreEqual(0, result.x);
            Assert.AreEqual(0, result.y);

            // Test: Pixel (100,150) -> Tile (2,3)
            result = WorldCoordinates.PixelToTile(new Vector2(100, 150));
            Assert.AreEqual(2, result.x);
            Assert.AreEqual(3, result.y);
        }

        [Test]
        public void TileToPixel_ConvertsCorrectly()
        {
            // Test: Tile (0,0) -> Pixel (0,0)
            Vector2 result = WorldCoordinates.TileToPixel(new Vector2Int(0, 0));
            Assert.AreEqual(0f, result.x, 0.001f);
            Assert.AreEqual(0f, result.y, 0.001f);

            // Test: Tile (1,0) -> Pixel (40,0)
            result = WorldCoordinates.TileToPixel(new Vector2Int(1, 0));
            Assert.AreEqual(40f, result.x, 0.001f);
            Assert.AreEqual(0f, result.y, 0.001f);

            // Test: Tile (5,10) -> Pixel (200,400)
            result = WorldCoordinates.TileToPixel(new Vector2Int(5, 10));
            Assert.AreEqual(200f, result.x, 0.001f);
            Assert.AreEqual(400f, result.y, 0.001f);
        }

        [Test]
        public void TileToRect_ReturnsCorrectBounds()
        {
            // Test: Tile (0,0) -> Rect (0,0,40,40)
            Rect result = WorldCoordinates.TileToRect(new Vector2Int(0, 0));
            Assert.AreEqual(0f, result.xMin, 0.001f);
            Assert.AreEqual(0f, result.yMin, 0.001f);
            Assert.AreEqual(40f, result.width, 0.001f);
            Assert.AreEqual(40f, result.height, 0.001f);

            // Test: Tile (5,10) -> Rect (200,400,40,40)
            result = WorldCoordinates.TileToRect(new Vector2Int(5, 10));
            Assert.AreEqual(200f, result.xMin, 0.001f);
            Assert.AreEqual(400f, result.yMin, 0.001f);
        }

        [Test]
        public void UnityToPixel_ConvertsCorrectly()
        {
            // Test: Unity (1,1) -> Pixel (100,100) - 100 pixels per meter
            Vector2 result = WorldCoordinates.UnityToPixel(new Vector3(1, 1, 0));
            Assert.AreEqual(100f, result.x, 0.001f);
            Assert.AreEqual(100f, result.y, 0.001f);

            // Test: Unity (0.5,2) -> Pixel (50,200)
            result = WorldCoordinates.UnityToPixel(new Vector3(0.5f, 2f, 0));
            Assert.AreEqual(50f, result.x, 0.001f);
            Assert.AreEqual(200f, result.y, 0.001f);
        }

        [Test]
        public void PixelToUnity_ConvertsCorrectly()
        {
            // Test: Pixel (100,100) -> Unity (1,1)
            Vector3 result = WorldCoordinates.PixelToUnity(new Vector2(100, 100));
            Assert.AreEqual(1f, result.x, 0.001f);
            Assert.AreEqual(1f, result.y, 0.001f);

            // Test: Pixel (50,200) -> Unity (0.5,2)
            result = WorldCoordinates.PixelToUnity(new Vector2(50, 200));
            Assert.AreEqual(0.5f, result.x, 0.001f);
            Assert.AreEqual(2f, result.y, 0.001f);
        }

        [Test]
        public void TileToUnity_ConvertsCorrectly()
        {
            // Test: Tile (0,0) -> Unity (0,0,0)
            Vector3 result = WorldCoordinates.TileToUnity(new Vector2Int(0, 0));
            Assert.AreEqual(0f, result.x, 0.001f);
            Assert.AreEqual(0f, result.y, 0.001f);

            // Test: Tile (10,10) -> Unity (4,4,0) - 10*40/100 = 4
            result = WorldCoordinates.TileToUnity(new Vector2Int(10, 10));
            Assert.AreEqual(4f, result.x, 0.001f);
            Assert.AreEqual(4f, result.y, 0.001f);
        }

        [Test]
        public void UnityToTile_ConvertsCorrectly()
        {
            // Test: Unity (0,0,0) -> Tile (0,0)
            Vector2Int result = WorldCoordinates.UnityToTile(new Vector3(0, 0, 0));
            Assert.AreEqual(0, result.x);
            Assert.AreEqual(0, result.y);

            // Test: Unity (4,4,0) -> Tile (10,10)
            result = WorldCoordinates.UnityToTile(new Vector3(4, 4, 0));
            Assert.AreEqual(10, result.x);
            Assert.AreEqual(10, result.y);
        }

        [Test]
        public void IsTileInBounds_ValidatesCorrectly()
        {
            // Test: Within bounds
            Assert.IsTrue(WorldCoordinates.IsTileInBounds(new Vector2Int(0, 0)));
            Assert.IsTrue(WorldCoordinates.IsTileInBounds(new Vector2Int(47, 26)));
            Assert.IsTrue(WorldCoordinates.IsTileInBounds(new Vector2Int(24, 13)));

            // Test: Out of bounds
            Assert.IsFalse(WorldCoordinates.IsTileInBounds(new Vector2Int(-1, 0)));
            Assert.IsFalse(WorldCoordinates.IsTileInBounds(new Vector2Int(0, -1)));
            Assert.IsFalse(WorldCoordinates.IsTileInBounds(new Vector2Int(48, 0)));
            Assert.IsFalse(WorldCoordinates.IsTileInBounds(new Vector2Int(0, 27)));
            Assert.IsFalse(WorldCoordinates.IsTileInBounds(new Vector2Int(100, 100)));
        }

        [Test]
        public void ClampTileToBounds_ClampsCorrectly()
        {
            // Test: Already in bounds - no change
            Vector2Int result = WorldCoordinates.ClampTileToBounds(new Vector2Int(10, 10));
            Assert.AreEqual(10, result.x);
            Assert.AreEqual(10, result.y);

            // Test: X too high
            result = WorldCoordinates.ClampTileToBounds(new Vector2Int(50, 10));
            Assert.AreEqual(47, result.x);
            Assert.AreEqual(10, result.y);

            // Test: Y too high
            result = WorldCoordinates.ClampTileToBounds(new Vector2Int(10, 30));
            Assert.AreEqual(10, result.x);
            Assert.AreEqual(26, result.y);

            // Test: Both negative
            result = WorldCoordinates.ClampTileToBounds(new Vector2Int(-1, -1));
            Assert.AreEqual(0, result.x);
            Assert.AreEqual(0, result.y);
        }

        [Test]
        public void RoundTrip_ConversionsAreConsistent()
        {
            // Test round-trip: Land -> World -> Land
            Vector3Int originalLand = new Vector3Int(2, 3, 0);
            Vector2 localPos = new Vector2(100, 200);
            Vector2 worldPos = WorldCoordinates.LandToWorld(originalLand, localPos);
            Vector3Int convertedLand = WorldCoordinates.WorldToLand(worldPos);
            Assert.AreEqual(originalLand.x, convertedLand.x);
            Assert.AreEqual(originalLand.y, convertedLand.y);

            // Test round-trip: Tile -> Pixel -> Tile
            Vector2Int originalTile = new Vector2Int(15, 20);
            Vector2 pixelPos = WorldCoordinates.TileToPixel(originalTile);
            Vector2Int convertedTile = WorldCoordinates.PixelToTile(pixelPos);
            Assert.AreEqual(originalTile.x, convertedTile.x);
            Assert.AreEqual(originalTile.y, convertedTile.y);

            // Test round-trip: Unity -> Pixel -> Unity
            Vector3 originalUnity = new Vector3(5.5f, 3.2f, 0);
            Vector2 pixel = WorldCoordinates.UnityToPixel(originalUnity);
            Vector3 convertedUnity = WorldCoordinates.PixelToUnity(pixel);
            Assert.AreEqual(originalUnity.x, convertedUnity.x, 0.01f);
            Assert.AreEqual(originalUnity.y, convertedUnity.y, 0.01f);
        }
    }
}
