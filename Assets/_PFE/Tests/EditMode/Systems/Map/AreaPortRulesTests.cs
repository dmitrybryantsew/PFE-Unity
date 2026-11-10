using NUnit.Framework;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Map
{
    /// <summary>
    /// Offline fixtures for <see cref="AreaPortRules"/> — the teleport half of an AS3 <c>Area</c>
    /// (<c>Area.as:289-434</c>), which is what makes the <c>bossnecr1</c> fight work.
    ///
    /// <para><b>What was broken.</b> The eight areas in <c>bossnecr1</c> were imported with their
    /// <c>port</c>, <c>tip</c>, <c>w</c> and <c>h</c> attributes intact, and <b>nothing in the port read
    /// <c>port</c></b> — so Mchavi never teleported and the arena's wall portals did not exist. The
    /// defect was not a wrong number; it was an unread attribute, which no fixture of the wrong kind
    /// would have caught either.</para>
    ///
    /// <para><b>These are pure functions</b>: no scene, no <c>GameObject</c>, no rendering. The one
    /// Unity type involved is <see cref="WorldCoordinates"/>, and only to prove that the destination
    /// conversion here is the <i>same</i> conversion the importer already uses rather than a second
    /// copy of it that can drift.</para>
    /// </summary>
    [TestFixture]
    public class AreaPortRulesTests
    {
        const int Tile = (int)WorldConstants.TILE_SIZE;   // 40

        // ── @port parsing (Area.as:212-224) ───────────────────────────────────────────────────────

        /// <summary>
        /// <c>"x:y"</c>, both in AS3 tiles. The values below are the real ones from
        /// <c>bossnecr1.asset</c>, so a change in the parse that breaks the shipped map fails here.
        /// </summary>
        [TestCase("42:22", 42, 22)]
        [TestCase("4:5", 4, 5)]
        [TestCase("42:15", 42, 15)]
        [TestCase("4:10", 4, 10)]
        [TestCase(" 42 : 22 ", 42, 22)]
        [TestCase("42:22:1", 42, 22)]           // the oracle takes the first two parts
        public void TryParsePort_ReadsTheAuthoredPair(string raw, int x, int y)
        {
            Assert.IsTrue(AreaPortRules.TryParsePort(raw, out int px, out int py), raw);
            Assert.AreEqual(x, px, raw);
            Assert.AreEqual(y, py, raw);
        }

        /// <summary>
        /// A malformed value is a <b>refusal</b>, not a teleport to nowhere. The oracle coerces through
        /// E4X and would produce <c>NaN</c>; "no port" is a state the caller already handles and
        /// "teleport to NaN" is not, so the two are not made equivalent.
        /// </summary>
        [TestCase("")]
        [TestCase(null)]
        [TestCase("   ")]
        [TestCase("42")]
        [TestCase("x:y")]
        [TestCase("42:")]
        [TestCase(":22")]
        public void TryParsePort_RefusesAnythingThatIsNotTwoIntegers(string raw)
        {
            Assert.IsFalse(AreaPortRules.TryParsePort(raw, out int px, out int py), raw ?? "<null>");
            Assert.AreEqual(-1, px);
            Assert.AreEqual(-1, py);
        }

        // ── who activates it (Area.as:290-331) ────────────────────────────────────────────────────

        /// <summary>
        /// <c>Area.step()</c> scans <c>loc.units</c> for a body whose <c>areaTestTip</c> equals the
        /// area's <c>tip</c> (<c>:316-331</c>). The match is <b>exact and non-empty</b> in both
        /// directions, and the player's own tip is excluded because the player path is a different
        /// mechanism (<c>Area.as:300-315</c>).
        /// </summary>
        [Test]
        public void IsUnitActivator_IsAnExactNonEmptyMatch_AndExcludesThePlayer()
        {
            Assert.IsTrue(AreaPortRules.IsUnitActivator("en", "en"), "the necromancer's own tip");
            Assert.IsTrue(AreaPortRules.IsUnitActivator("zebra", "zebra"), "any other authored tip");

            Assert.IsFalse(AreaPortRules.IsUnitActivator("gg", "gg"),
                "tip='gg' is the player and is not this path's business");
            Assert.IsFalse(AreaPortRules.IsUnitActivator("gg", "en"));
            Assert.IsFalse(AreaPortRules.IsUnitActivator("en", "gg"));
            Assert.IsFalse(AreaPortRules.IsUnitActivator("en", "EN"), "ordinal, not case-insensitive");
            Assert.IsFalse(AreaPortRules.IsUnitActivator("en", "zebra"));
            Assert.IsFalse(AreaPortRules.IsUnitActivator("en", "en "), "no trimming: the ids are exact");
        }

        /// <summary>
        /// An empty tip activates for <b>nobody</b>, in either direction.
        ///
        /// <para><c>Area.step()</c> returns before the scan when its own <c>tip</c> is empty
        /// (<c>:290-293</c>), and 31 authored areas rely on that. A blank <i>unit</i> tip must not match
        /// either — every randomly placed enemy has an empty <c>areaTestTip</c>, so a permissive reading
        /// would fire any <c>tip="en"</c> portal for the first wandering NPC that crossed it.</para>
        /// </summary>
        [Test]
        public void AnEmptyTip_ActivatesForNobody()
        {
            Assert.IsFalse(AreaPortRules.IsUnitActivator("", "en"));
            Assert.IsFalse(AreaPortRules.IsUnitActivator(null, "en"));
            Assert.IsFalse(AreaPortRules.IsUnitActivator("en", ""));
            Assert.IsFalse(AreaPortRules.IsUnitActivator("en", null));
            Assert.IsFalse(AreaPortRules.IsUnitActivator("", ""), "the empty tip would otherwise match itself");
        }

        // ── the destination (Area.as:426) ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>param1.teleport((portX + 1) * World.tileX, (portY + 1) * World.tileY - 1)</c>, mirrored
        /// into the port's up-positive space. The two real values from <c>bossnecr1</c> at the room's
        /// own height of 25 rows.
        /// </summary>
        [Test]
        public void PortDestination_TranscribesTheOraclesPixels()
        {
            // (portX+1)*40 = 43*40 = 1720; (25 - 0 - 22 - 1)*40 + 1 = 81.
            AreaPortRules.PortDestinationPixels(42, 22, Tile, 25, 0, out float x, out float y);
            Assert.AreEqual(1720f, x, 0.001f, "x is (42+1)*40 — the boundary, not the centre");
            Assert.AreEqual(81f, y, 0.001f, "y is the mirror of (22+1)*40-1, plus the oracle's 1px");

            // (portX+1)*40 = 5*40 = 200; (25 - 0 - 5 - 1)*40 + 1 = 761.
            AreaPortRules.PortDestinationPixels(4, 5, Tile, 25, 0, out x, out y);
            Assert.AreEqual(200f, x, 0.001f);
            Assert.AreEqual(761f, y, 0.001f);
        }

        /// <summary>
        /// The <c>-1</c> in <c>(portY+1)*tile - 1</c> is a <b>one-pixel lift</b>, and the mirror turns it
        /// into <c>+1</c>. Dropping it would put the unit exactly on the tile boundary; adding a whole
        /// tile would put her one row up. Both are invisible until the motor's next gravity step
        /// resolves her against the floor, which is why it is pinned as a number rather than as a
        /// comment.
        /// </summary>
        [Test]
        public void PortDestination_KeepsTheOnePixelLift()
        {
            AreaPortRules.PortDestinationPixels(10, 10, Tile, 25, 0, out _, out float y);
            Assert.AreEqual((25 - 10 - 1) * 40 + 1, y, 0.001f);
            Assert.AreNotEqual((25 - 10 - 1) * 40, y, "the 1px is the oracle's, not rounding");
            Assert.AreNotEqual((25 - 10) * 40 + 1, y, "off by a whole row would be a different tile");
        }

        /// <summary>
        /// The border offset is subtracted on <b>both</b> axes, exactly as the placement flip does it
        /// (<c>RoomPopulator.ResolveLegacyPlacementTileCoord</c>: <c>x + borderOffset</c>,
        /// <c>height - borderOffset - 1 - y</c>).
        ///
        /// <para><b>Why this is not cosmetic.</b> A room with a one-tile border and a port at row 22 of
        /// 25 lands at <b>41 px</b> with the border applied and <b>81 px</b> without — two different
        /// rows. The bug would be a boss standing one tile into the floor on half the maps and fine on
        /// the other half, which is the kind of defect that survives a screen check on the one map
        /// somebody happened to test.</para>
        /// </summary>
        [Test]
        public void PortDestination_AppliesTheBorderOffsetOnBothAxes()
        {
            AreaPortRules.PortDestinationPixels(42, 22, Tile, 25, 1, out float x, out float y);
            Assert.AreEqual(1760f, x, 0.001f, "(42+1+1)*40");
            Assert.AreEqual(41f, y, 0.001f, "(25-1-22-1)*40 + 1");

            AreaPortRules.PortDestinationPixels(4, 5, Tile, 25, 1, out x, out y);
            Assert.AreEqual(240f, x, 0.001f, "(4+1+1)*40");
            Assert.AreEqual(721f, y, 0.001f, "(25-1-5-1)*40 + 1");
        }

        /// <summary>
        /// A negative border is clamped to zero rather than flipping the whole conversion — a room
        /// asset with <c>borderOffset: -1</c> must not mirror the destination off the top of the map.
        /// </summary>
        [Test]
        public void PortDestination_ClampsANegativeBorderToZero()
        {
            AreaPortRules.PortDestinationPixels(42, 22, Tile, 25, -3, out float x, out float y);
            AreaPortRules.PortDestinationPixels(42, 22, Tile, 25, 0, out float expectedX, out float expectedY);
            Assert.AreEqual(expectedX, x, 0.001f);
            Assert.AreEqual(expectedY, y, 0.001f);
        }

        /// <summary>
        /// <b>The consistency guard:</b> this conversion and
        /// <see cref="WorldCoordinates.As3BottomCentreAnchorPixels"/> must be the same conversion.
        ///
        /// <para>The oracle's <c>x</c> is <c>(portX + 1) * tile</c>, a tile <b>boundary</b>; the shared
        /// helper takes a <c>sizeTiles</c> footprint and places its bottom <i>centre</i>, so a zero-width
        /// footprint at column <c>portX + 1</c> is the same point. Both halves then reduce to the same
        /// expression, and this test is what stops one of them being edited alone — the failure mode the
        /// alicorn boss already paid for once, when a mirrored comparison pointed the wrong way.</para>
        /// </summary>
        [TestCase(42, 22, 25, 0)]
        [TestCase(4, 5, 25, 0)]
        [TestCase(42, 22, 25, 1)]
        [TestCase(4, 5, 24, 2)]
        [TestCase(1, 0, 25, 0)]
        [TestCase(0, 24, 25, 0)]
        public void PortDestination_IsTheProjectsOwnAs3Conversion(int portX, int portY, int height, int border)
        {
            AreaPortRules.PortDestinationPixels(portX, portY, Tile, height, border, out float x, out float y);

            Vector2 shared = WorldCoordinates.As3BottomCentreAnchorPixels(
                new Vector2Int(portX + 1, portY), 0f, height, border);

            Assert.AreEqual(shared.x, x, 0.001f,
                $"x disagrees with As3BottomCentreAnchorPixels for port {portX}:{portY}");
            Assert.AreEqual(shared.y, y, 0.001f,
                $"y disagrees with As3BottomCentreAnchorPixels for port {portX}:{portY}");
        }

        // ── the rising edge (Area.as:341-344) ─────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(active &amp;&amp; !preactive &amp;&amp; onPort)</c> — <b>rising edge only</b>.
        ///
        /// <para>A level poll is the tempting reading and it is wrong twice: the unit is still inside the
        /// rectangle on the next frame, so it would be teleported again, and the destination of one of
        /// <c>bossnecr1</c>'s eight portals can be inside another of them, which makes a level poll a
        /// two-frame ping-pong that ends with the boss somewhere neither portal names.</para>
        /// </summary>
        [Test]
        public void ShouldTeleport_FiresOnTheRisingEdgeOnly()
        {
            Assert.IsTrue(AreaPortRules.ShouldTeleport(false, true), "entered — the only firing case");
            Assert.IsFalse(AreaPortRules.ShouldTeleport(true, true), "still inside — must not fire again");
            Assert.IsFalse(AreaPortRules.ShouldTeleport(true, false), "left — nothing to do");
            Assert.IsFalse(AreaPortRules.ShouldTeleport(false, false), "empty — nothing to do");
        }

        // ── the overlap test (Obj.as:287-294) ────────────────────────────────────────────────────

        /// <summary>
        /// <c>areaTest</c> is <b>half-open</b>: <c>!(a.X1 &gt;= b.X2 || a.X2 &lt;= b.X1 || …)</c>. Two
        /// rectangles that merely share an edge do <b>not</b> overlap, so a unit standing exactly on a
        /// portal's boundary has not entered it.
        ///
        /// <para>This matters for the eight wall strips in <c>bossnecr1</c>: they are 1 tile wide and
        /// abut the arena floor, so a closed-interval reading would make the portal fire from one pixel
        /// outside the wall.</para>
        /// </summary>
        [Test]
        public void Overlaps_IsHalfOpen_SoASharedEdgeIsNotAnOverlap()
        {
            // Area 0..40 x 0..280 (1 tile wide, 7 tall) — the shape of a bossnecr1 portal.
            const float aMinX = 0f, aMaxX = 40f, aMinY = 0f, aMaxY = 280f;

            Assert.IsTrue(AreaPortRules.Overlaps(aMinX, aMaxX, aMinY, aMaxY, 20f, 60f, 100f, 200f),
                "a body inside");
            Assert.IsTrue(AreaPortRules.Overlaps(aMinX, aMaxX, aMinY, aMaxY, 39f, 79f, 100f, 200f),
                "one pixel of overlap is an overlap");

            Assert.IsFalse(AreaPortRules.Overlaps(aMinX, aMaxX, aMinY, aMaxY, 40f, 80f, 100f, 200f),
                "touching the right edge is not entering");
            Assert.IsFalse(AreaPortRules.Overlaps(aMinX, aMaxX, aMinY, aMaxY, -40f, 0f, 100f, 200f),
                "touching the left edge is not entering");
            Assert.IsFalse(AreaPortRules.Overlaps(aMinX, aMaxX, aMinY, aMaxY, 10f, 30f, 280f, 320f),
                "standing on the top edge is not entering");
            Assert.IsFalse(AreaPortRules.Overlaps(aMinX, aMaxX, aMinY, aMaxY, 10f, 30f, -40f, 0f),
                "standing on the bottom edge is not entering");
        }
    }
}
