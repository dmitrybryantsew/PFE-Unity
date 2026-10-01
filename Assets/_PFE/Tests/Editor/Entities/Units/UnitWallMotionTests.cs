using NUnit.Framework;
using UnityEngine;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="UnitWallMotion"/> — the port of the column half of AS3's <c>Unit.run()</c>,
    /// <c>unit/Unit.as:2110-2240</c>.
    ///
    /// <para><b>What was wrong.</b> Units had <i>no</i> horizontal collision: <c>MovePosition</c> on a
    /// Kinematic body is not blocked by static geometry, and the only tile query the motor made was a
    /// ground probe below the feet. The play-test report — "bullets can push back target through wall" —
    /// is that.</para>
    ///
    /// <para><b>The test that matters most is the one that asserts NOT blocked.</b> The tile resolver
    /// decides which rows to test from the <i>feet</i> (<c>Mathf.FloorToInt(feetY / 40)</c>), so a unit
    /// whose feet sit exactly on a surface boundary tests the floor's own row and is then blocked by the
    /// ground it is standing on. The port seats units 1 px above the surface, so it is one pixel away
    /// from that, and <see cref="UnitWallMotion.ToMoveBoxPixels"/> insets the box bottom to buy the
    /// margin back. <see cref="ABoxWhoseBottomSitsOnTheSurfaceBoundary_IsBlockedByTheFloorItStandsOn"/>
    /// is the control that proves the hazard is real and not a story in a comment.</para>
    /// </summary>
    public class UnitWallMotionTests
    {
        const float TileSize = 40f;

        // ── Fixture ──────────────────────────────────────────────────────────

        /// <summary>
        /// 16 x 5. Row 0 of the ASCII is the TOP, so the bottom row is y = 0 — a solid ground slab — and
        /// the walls at x = 0 and x = 15 span the full height. Rows 1..3 are open air.
        /// </summary>
        private static ITileQueryService Room()
            => new UnifiedTileQueryService(SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..............#",
                "################"
            }));

        /// <summary>Feet 1 px above the ground's top edge — the seat <c>UnitGroundProbe</c> describes.</summary>
        const float FeetY = TileSize + 1f;

        const float UnitHeight = 30f;
        const float UnitHalfWidth = 13f;

        /// <summary>A box placed directly, so the arithmetic under test is the resolver's.</summary>
        private static TileBox BoxAt(float centreX, float bottom = FeetY, float height = UnitHeight)
            => TileBox.FromMinMax(centreX - UnitHalfWidth, bottom,
                                  centreX + UnitHalfWidth, bottom + height);

        // ── The box ──────────────────────────────────────────────────────────

        [Test]
        public void ToMoveBoxPixels_CarriesTheColliderThroughAndLiftsTheBottom()
        {
            // A 26 x 30 px collider whose feet are at 41 px: centre (200, 56), half-size (13, 15).
            var bounds = new Bounds(
                new Vector3(2.00f, 0.56f, 0f),
                new Vector3(0.26f, 0.30f, 0f));

            TileBox box = UnitWallMotion.ToMoveBoxPixels(bounds, TileQueryConstants.PixelToUnit);

            Assert.AreEqual(187f, box.Left, 1e-3f);
            Assert.AreEqual(213f, box.Right, 1e-3f);
            Assert.AreEqual(FeetY + UnitWallMotion.BottomInsetPixels, box.Bottom, 1e-3f,
                "the bottom is lifted off the surface boundary — see the class remarks");
            Assert.AreEqual(71f, box.Top, 1e-3f);
        }

        [Test]
        public void ToMoveBoxPixels_SurvivesADegenerateCollider()
        {
            // A zero-HEIGHT box makes ResolveMove pass `height - 1 = -1` as the box's height, which
            // inverts the row range and makes the cell loop test nothing — so a malformed collider would
            // read as "walls do not exist" rather than "the collider is wrong". The fallback keeps the
            // box usable. Width needs no such guard: the cell range is derived from the left and right
            // edges, and a zero-width box still tests the one column it sits in.
            var bounds = new Bounds(Vector3.zero, Vector3.zero);

            TileBox box = UnitWallMotion.ToMoveBoxPixels(bounds, TileQueryConstants.PixelToUnit);

            Assert.Greater(box.Height, 0f);
        }

        // ── The negative control ─────────────────────────────────────────────

        [Test]
        public void AUnitStandingOnOpenGround_WalksFreely()
        {
            // The behaviour this whole fix must not break: horizontal collision must not be satisfied by
            // the floor the unit is standing on. A unit at tile x = 5 is nowhere near either wall.
            ITileQueryService room = Room();

            UnitWallResolution result = UnitWallMotion.Resolve(room, BoxAt(200f), deltaXPx: 50f);

            Assert.IsFalse(result.Blocked, "open ground is not an obstacle");
            Assert.AreEqual(50f, result.AppliedDeltaXPx, 1e-3f, "the full delta is applied");
        }

        [Test]
        public void ABoxWhoseBottomSitsOnTheSurfaceBoundary_IsBlockedByTheFloorItStandsOn()
        {
            // The control for the inset, and it is a two-pixel cliff rather than a one-pixel one:
            // `ResolveMove` derives its row range from the FEET with Mathf.FloorToInt, so a bottom of 41
            // (the seat) or 40 (the boundary) both floor into row 1 — the air above the slab — while a
            // bottom of 39 floors into row 0, which is the ground itself. The resolver then reports a
            // block, and the unit cannot walk. Nothing about the unit changed except its bottom pixel;
            // this is why ToMoveBoxPixels lifts it.
            ITileQueryService room = Room();

            UnitWallResolution sunk = UnitWallMotion.Resolve(
                room, BoxAt(200f, bottom: TileSize - 1f), deltaXPx: 50f);

            Assert.IsTrue(sunk.Blocked,
                "a box bottomed inside the ground's own row is blocked by the floor it stands on — the " +
                "hazard the inset exists to avoid");
            Assert.Less(sunk.AppliedDeltaXPx, 50f, "and it cannot move");
        }

        [Test]
        public void ABoxBottomedOnTheBoundaryItself_StillWalks()
        {
            // The complement of the test above, and the reason the inset is 1 px rather than 0: a bottom
            // at exactly the tile top still floors into the row above, so the floor is not tested. This
            // pins where the cliff actually is, so a future change to the seat cannot move it silently.
            ITileQueryService room = Room();

            UnitWallResolution onBoundary = UnitWallMotion.Resolve(
                room, BoxAt(200f, bottom: TileSize), deltaXPx: 50f);

            Assert.IsFalse(onBoundary.Blocked);
            Assert.AreEqual(50f, onBoundary.AppliedDeltaXPx, 1e-3f);
        }

        // ── The walls ────────────────────────────────────────────────────────

        [Test]
        public void WalkingRightIntoTheRightWall_IsBlocked_AndClampedFlushToTheFace()
        {
            ITileQueryService room = Room();

            // The right wall occupies x = 600..640. A 26 px box centred at 585 spans 572..598, so it can
            // advance 2 px before its leading edge reaches the wall face at 600.
            UnitWallResolution result = UnitWallMotion.Resolve(room, BoxAt(585f), deltaXPx: 50f);

            Assert.IsTrue(result.Blocked, "the wall must stop it");
            Assert.Greater(result.AppliedDeltaXPx, 0f, "it advances up to the face rather than snapping back");
            Assert.Less(result.AppliedDeltaXPx, 50f);

            float finalRight = 585f + result.AppliedDeltaXPx + UnitHalfWidth;
            Assert.LessOrEqual(finalRight, 600f + 1f,
                "the box must not end up inside the wall column");
        }

        [Test]
        public void WalkingLeftIntoTheLeftWall_IsBlocked()
        {
            ITileQueryService room = Room();

            // The left wall occupies x = 0..40. A box centred at 80 spans 67..93.
            UnitWallResolution result = UnitWallMotion.Resolve(room, BoxAt(80f), deltaXPx: -100f);

            Assert.IsTrue(result.Blocked);
            Assert.Greater(result.AppliedDeltaXPx, -100f, "it must not tunnel through");

            float finalLeft = 80f + result.AppliedDeltaXPx - UnitHalfWidth;
            Assert.GreaterOrEqual(finalLeft, 40f - 1f);
        }

        [Test]
        public void AUnitFlushAgainstTheWall_CanWalkAwayFromIt()
        {
            // The trailing-side rule. `ResolveMove` reports the side the step moves toward, so a unit
            // leaving a wall is never reported as blocked by it — which is what stops a unit that was
            // knocked flush from being pinned there forever.
            ITileQueryService room = Room();

            UnitWallResolution result = UnitWallMotion.Resolve(room, BoxAt(53f), deltaXPx: 50f);

            Assert.IsFalse(result.Blocked, "moving away from a wall is not a collision with it");
            Assert.AreEqual(50f, result.AppliedDeltaXPx, 1e-3f);
        }

        // ── Degenerate inputs ────────────────────────────────────────────────

        [Test]
        public void WithNoRoom_TheUnitIsNotFrozen()
        {
            // A unit spawned outside any room has no walls to hit. Refusing the move would be a much
            // louder failure than letting it walk.
            UnitWallResolution result = UnitWallMotion.Resolve(null, BoxAt(200f), deltaXPx: 50f);

            Assert.IsFalse(result.Blocked);
            Assert.AreEqual(50f, result.AppliedDeltaXPx, 1e-3f);
        }

        [Test]
        public void WithNoHorizontalMotion_TheResolverIsNotConsulted()
        {
            // The common case for a unit that is standing still or only falling. It must be a no-op, and
            // in particular must not report a block — which would zero a velocity that is already zero
            // and hide a real one.
            UnitWallResolution result = UnitWallMotion.Resolve(Room(), BoxAt(585f), deltaXPx: 0f);

            Assert.IsFalse(result.Blocked);
            Assert.AreEqual(0f, result.AppliedDeltaXPx);
        }
    }
}
