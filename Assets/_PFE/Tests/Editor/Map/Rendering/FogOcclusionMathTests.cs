using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;

namespace PFE.Tests.Editor.Map.Rendering
{
    /// <summary>
    /// Pins the fog-of-war occlusion rule to AS3's <c>Tile.opac</c>.
    ///
    /// AS3 reads <c>opac</c> in exactly one place, <c>Location.lighting():3214</c>, and it is binary:
    /// <c>if(this.phis &gt; 0) this.opac = 1;</c> (<c>Tile.as:198-201</c>), and <c>setZForm(n &gt; 0)</c>
    /// clears it to 0 (<c>Tile.as:293-296</c>). Water is the room's <c>wopac</c> option applied as a
    /// <c>max</c> (<c>Location.lighting():3217-3220</c>).
    ///
    /// The port previously returned 0.6 for a broad "looks solid" predicate. That only blocked a
    /// one-tile wall because the shadow ray then stepped 20 px (two samples per 40 px wall); once the
    /// step went back to AS3's 40 px, 0.6 would have let light straight through walls. These tests
    /// exist so the constant and the predicate can never drift apart again.
    /// </summary>
    [TestFixture]
    public class FogOcclusionMathTests
    {
        const float Tolerance = 1e-4f;

        [Test]
        public void SolidTile_FullyBlocksLight()
        {
            Assert.AreEqual(1f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(TilePhysicsType.Wall, 0, false, 0f),
                Tolerance,
                "AS3 `if(this.phis > 0) this.opac = 1` (Tile.as:198-201) — one solid tile on a " +
                "40 px step must fully block a 40 px wall.");
        }

        [Test]
        public void AirTile_DoesNotBlockLight()
        {
            Assert.AreEqual(0f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(TilePhysicsType.Air, 0, false, 0f),
                Tolerance);
        }

        [Test]
        public void RaisedTile_DoesNotBlockLight_EvenWhenSolid()
        {
            Assert.AreEqual(0f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(TilePhysicsType.Wall, 2, false, 0f),
                Tolerance,
                "AS3 `setZForm(n > 0) -> this.opac = 0` (Tile.as:293-296) *clears* the value set from " +
                "phis; a partial-height tile has air above it, so light passes over it.");
        }

        [Test]
        public void WaterWithoutRoomOption_DoesNotBlockLight()
        {
            Assert.AreEqual(0f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(TilePhysicsType.Air, 0, true, 0f),
                Tolerance,
                "`opacWater` defaults to 0 (LandAct.as:84) and the room only sets it from `wopac` " +
                "(LandAct.as:225-228).");
        }

        [Test]
        public void WaterWithRoomOption_BlocksLight_UsingTheRawOption()
        {
            Assert.AreEqual(0.7f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(TilePhysicsType.Air, 0, true, 0.7f),
                Tolerance,
                "AS3 uses `opacWater` directly (Location.lighting():3219), it does not halve it.");
        }

        [Test]
        public void WaterOnASolidTile_TakesTheMaximum_NotTheSum()
        {
            Assert.AreEqual(1f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(TilePhysicsType.Wall, 0, true, 0.7f),
                Tolerance,
                "Location.lighting():3217-3220 replaces `_loc18_` only when `opacWater > _loc18_`.");
        }

        [Test]
        public void WaterOpacityAboveOne_IsClamped()
        {
            Assert.AreEqual(1f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(TilePhysicsType.Air, 0, true, 3f),
                Tolerance,
                "A `wopac` above 1 would otherwise let a single tile over-block a ray.");
        }

        [Test]
        public void RaisedTileStillBlocksWhenTheRoomFillsItWithWater()
        {
            Assert.AreEqual(0.5f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(TilePhysicsType.Wall, 3, true, 0.5f),
                Tolerance,
                "The water term is applied after the zForm clear, so a raised flooded tile still " +
                "attenuates by `wopac`.");
        }

        [Test]
        public void NonAirPhysicsTypes_AllBlockLight()
        {
            Assert.AreEqual(1f, FogOcclusionMath.ResolveTileOcclusionOpacity(TilePhysicsType.Platform, 0, false, 0f), Tolerance);
            Assert.AreEqual(1f, FogOcclusionMath.ResolveTileOcclusionOpacity(TilePhysicsType.Stair, 0, false, 0f), Tolerance);
        }
    }
}
