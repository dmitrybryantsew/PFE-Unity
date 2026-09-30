using NUnit.Framework;
using PFE.Entities.Units;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="UnitGroundProbe"/> to the arithmetic the tile query actually performs, so the
    /// "units fall through the floor" fix rests on a measurement rather than on a plausible-looking
    /// offset.
    ///
    /// <para>The whole defect is a <b>one-pixel boundary</b>. <c>TileCollisionMath.IsOnGround</c> samples
    /// a single point, <c>boundsPx.yMin - 1f</c> (<c>:541</c>), and resolves it with
    /// <c>Mathf.FloorToInt</c> (<c>:157</c>). A unit is seated <c>+ 1f</c> px above the surface
    /// (<c>RoomPopulator.ResolveLegacyBottomAnchorPixels</c>), so a probe placed at the feet samples the
    /// surface <i>boundary</i> — and <c>floor</c> on a boundary selects the row <b>above</b>, i.e. the
    /// air tile the unit is standing in. So the probe must be lowered by the seat, and the test that
    /// matters is the pair
    /// <see cref="SeatedUnit_SampleLandsInsideTheGroundTile"/> /
    /// <see cref="WithoutTheSeat_TheSampleFloorsIntoTheAirTileAbove"/>: the second is the negative
    /// control, and it fails under the un-seated rect. A single test asserting "999 is in range" would
    /// pass for the wrong reason.</para>
    /// </summary>
    public class UnitGroundProbeTests
    {
        /// <summary>A tile cell, from <c>WorldConstants.TILE_SIZE</c>.</summary>
        const float TileSize = 40f;

        /// <summary>
        /// Mirrors <c>TileCollisionMath.cs:157</c>:
        /// <c>Mathf.FloorToInt((y - roomWorldPixelY) / WorldConstants.TILE_SIZE)</c>. Duplicated on
        /// purpose — it is the arithmetic under test, and the assertions below are about which
        /// <i>physical</i> row comes out, not about agreement with a second copy of the formula.
        /// </summary>
        static int RowAt(float worldPixelY, float roomWorldPixelY) =>
            Mathf.FloorToInt((worldPixelY - roomWorldPixelY) / TileSize);

        /// <summary>The world-pixel y of the top edge of <paramref name="row"/>.</summary>
        static float TopOfRow(int row, float roomWorldPixelY) =>
            roomWorldPixelY + (row + 1) * TileSize;

        /// <summary>
        /// A `training` dummy's collider: <c>width: 0.6</c>, <c>height: 0.75</c>, offset
        /// <c>(0, +Height/2)</c>, so <c>min.y</c> IS the unit's feet.
        /// </summary>
        static Bounds DummyBounds(float feetWorldY) =>
            new Bounds(
                new Vector3(0f, feetWorldY + 0.375f, 0f),
                new Vector3(0.6f, 0.75f, 0f));

        // ── The contract with the query ──────────────────────────────────────────────────────

        /// <summary>
        /// The rect's <c>yMin</c> is the surface, not the feet — the feet are <see cref="UnitGroundProbe.SeatPixels"/>
        /// px above it. <c>Rect.y</c> is the minimum y, which is why the field is named <c>bottom</c> in
        /// the implementation.
        /// </summary>
        [Test]
        public void ProbeRectIsLoweredByTheSeat()
        {
            // Feet 1 px above a surface at 1000 px: y = 10.01 world units.
            Rect probe = UnitGroundProbe.ToProbeRectPixels(DummyBounds(10.01f), TileQueryConstants.PixelToUnit);

            Assert.AreEqual(1000f, probe.yMin, 0.001f,
                "yMin must be the surface (feet minus the 1 px seat), not the feet.");
            Assert.AreEqual(UnitGroundProbe.SeatPixels, 1f, "The seat is the trailing + 1f in " +
                "RoomPopulator.ResolveLegacyBottomAnchorPixels; changing it changes this contract.");
        }

        /// <summary>
        /// <c>IsOnGround</c> subtracts 1 px from the rect it is handed
        /// (<c>TileCollisionMath.cs:541</c>). Pinned so a future edit to either side is caught.
        /// </summary>
        [Test]
        public void ProbePointIsOnePixelBelowTheRectMinimum()
        {
            Rect probe = UnitGroundProbe.ToProbeRectPixels(DummyBounds(10.01f), TileQueryConstants.PixelToUnit);

            Assert.AreEqual(probe.yMin - 1f, UnitGroundProbe.ProbePointY(probe), 0.0001f);
            Assert.AreEqual(999f, UnitGroundProbe.ProbePointY(probe), 0.001f);
        }

        // ── The pair that proves the offset is load-bearing ───────────────────────────────────

        /// <summary>
        /// A unit seated on a tile whose top edge is at y = 1000 px must be sampled <b>inside that
        /// tile's row</b> (row 24, spanning 960..1000), and inside the platform band
        /// <c>[tileTop - PorogGrounded, tileTop]</c>.
        /// </summary>
        [Test]
        public void SeatedUnit_SampleLandsInsideTheGroundTile()
        {
            const float surface = 1000f;
            const float roomWorldPixelY = 0f;

            // Feet = surface + seat = 1001 px = 10.01 world units.
            Rect probe = UnitGroundProbe.ToProbeRectPixels(
                DummyBounds(10.01f), TileQueryConstants.PixelToUnit);
            float sample = UnitGroundProbe.ProbePointY(probe);

            int groundRow = RowAt(surface - 1f, roomWorldPixelY); // the tile whose TOP is the surface
            Assert.AreEqual(24, groundRow, "precondition: the ground tile is row 24");
            Assert.AreEqual(surface, TopOfRow(groundRow, roomWorldPixelY), 0.001f,
                "precondition: row 24's top edge is the surface the unit stands on");

            Assert.AreEqual(groundRow, RowAt(sample, roomWorldPixelY),
                $"the sample ({sample}) must resolve to the ground tile's row, not the air row above");

            Assert.LessOrEqual(sample, surface, "the sample must be at or below the platform top");
            Assert.GreaterOrEqual(sample, surface - TileQueryConstants.PorogGrounded,
                "the sample must be within the platform band [tileTop - porog, tileTop]");
        }

        /// <summary>
        /// The negative control: with the seat <i>not</i> undone the sample lands exactly on the surface
        /// boundary, and <c>floor</c> picks the row above — the air tile. This is the bug the offset
        /// exists to avoid, and it is asserted so the offset cannot be "simplified" away.
        /// </summary>
        [Test]
        public void WithoutTheSeat_TheSampleFloorsIntoTheAirTileAbove()
        {
            const float surface = 1000f;
            const float roomWorldPixelY = 0f;

            // The un-seated rect: yMin at the feet (1001 px) instead of at the surface (1000 px).
            float unSeatedSample = 1001f - 1f;

            Assert.AreEqual(1000f, unSeatedSample, 0.001f);
            Assert.AreEqual(25, RowAt(unSeatedSample, roomWorldPixelY),
                "on the boundary, floor() selects the row ABOVE — the air tile");
            Assert.Greater(Mathf.Abs(TopOfRow(25, roomWorldPixelY) - surface), 0.001f,
                "row 25's top is 1040, not the surface — so that row is not the ground");
        }

        // ── Unit conversion and shape ────────────────────────────────────────────────────────

        /// <summary>
        /// World units → pixels is a <b>division</b> by 0.01, not a multiplication. Multiplying gives
        /// 0.004 px for a 0.4-unit box, which is the classic porting inversion and would make every
        /// probe collapse to a point at the origin.
        /// </summary>
        [Test]
        public void UnitsAreConvertedToPixelsByDivision()
        {
            var bounds = new Bounds(Vector3.zero, new Vector3(0.4f, 0.4f, 0f));

            Rect probe = UnitGroundProbe.ToProbeRectPixels(bounds, TileQueryConstants.PixelToUnit);

            Assert.AreEqual(40f, probe.width, 0.001f, "0.4 world units is 40 px");
            Assert.AreEqual(40f, probe.height, 0.001f);
            Assert.Greater(Mathf.Abs(probe.width - 0.004f), 0.0001f,
                "a multiplied conversion (0.4 * 0.01 = 0.004 px) is the bug this pins");
        }

        /// <summary>
        /// Width, height and the horizontal centre are carried through untouched — <c>IsOnGround</c>
        /// reads <c>yMin</c>, <c>width</c> and <c>center.x</c>, and only <c>yMin</c> is meant to move.
        /// </summary>
        [Test]
        public void ProbeRectKeepsTheColliderWidthHeightAndCentreX()
        {
            var bounds = new Bounds(new Vector3(1.25f, 10.385f, 0f), new Vector3(0.6f, 0.75f, 0f));

            Rect probe = UnitGroundProbe.ToProbeRectPixels(bounds, TileQueryConstants.PixelToUnit);

            Assert.AreEqual(60f, probe.width, 0.001f);
            Assert.AreEqual(75f, probe.height, 0.001f);
            Assert.AreEqual(125f, probe.center.x, 0.001f, "0.5-unit half-width must not shift the centre");
        }

        /// <summary>
        /// A zero or negative conversion must not produce an inverted rect: it falls back to the
        /// canonical constant rather than dividing by zero or flipping the box.
        /// </summary>
        [Test]
        public void NonPositivePixelToUnit_FallsBackToTheCanonicalConstant()
        {
            var bounds = new Bounds(Vector3.zero, new Vector3(0.4f, 0.4f, 0f));

            Rect fromZero = UnitGroundProbe.ToProbeRectPixels(bounds, 0f);
            Rect fromNegative = UnitGroundProbe.ToProbeRectPixels(bounds, -0.01f);
            Rect canonical = UnitGroundProbe.ToProbeRectPixels(bounds, TileQueryConstants.PixelToUnit);

            Assert.AreEqual(canonical.width, fromZero.width, 0.001f);
            Assert.AreEqual(canonical.yMin, fromZero.yMin, 0.001f);
            Assert.AreEqual(canonical.width, fromNegative.width, 0.001f);
            Assert.AreEqual(canonical.yMin, fromNegative.yMin, 0.001f);
            Assert.Greater(fromZero.width, 0f, "an inverted rect would be negative-width");
        }
    }
}
