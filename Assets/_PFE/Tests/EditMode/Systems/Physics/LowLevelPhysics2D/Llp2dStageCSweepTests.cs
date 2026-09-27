using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.EditMode.Systems.Physics.LowLevelPhysics2D
{
    /// <summary>
    /// Stage C: the swept tile-contact seam (<see cref="IPhysicsWorldService.TrySweepTiles"/>) over the
    /// chain mirror.
    ///
    /// <para><b>What this adds over B1.</b> B1 asked whether the chain mirror <i>overlaps</i> the same
    /// box the classic tile query collides, and found the two agree on first contact. That was enough
    /// to trust the geometry, but it is not the question a projectile asks. A chain is a
    /// <b>zero-thickness</b> surface, so overlap is only ever true while the projectile straddles the
    /// line — a window one projectile wide. B1 sidestepped that by running 4 px/frame constant-velocity
    /// scenarios and saying so in its own comments. The seam under test here does not need that
    /// handicap, and these tests are written to prove it rather than to inherit it.</para>
    ///
    /// <para><b>The three things that must be true.</b> (1) The seam finds the geometry B1 already
    /// proved is there, at the same five contact situations. (2) It finds nothing in open air — the
    /// control that makes (1) mean anything. (3) It still finds the wall when the whole move crosses
    /// it in one step, where an end-of-step overlap test provably sees nothing. Case (3) is the one
    /// that would silently pass a naive implementation, so it also <i>measures</i> the naive
    /// alternative rather than asserting it away.</para>
    ///
    /// <para><b>Room and coordinates.</b> The same 16x5 room B1 uses, for direct comparability, and
    /// its ASCII, tile->pixel mapping and free-space description are not repeated here. Rooms are at
    /// the origin except in the one test whose whole point is that the mirror is in <i>world</i> space.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class Llp2dStageCSweepTests
    {
        // Identical to Llp2dStageB1DualRunTests.RoomWithShelf, deliberately: the scenarios below are
        // B1's, so the surfaces they aim at must be B1's surfaces.
        //   row 0 -> y=4  ceiling, px y in [160,200]
        //   row 2 -> y=2  a four-tile shelf at columns 5..8, px x in [200,360], y in [80,120]
        //   row 4 -> y=0  floor, px y in [0,40]
        private static readonly string[] RoomWithShelf =
        {
            "################",
            "#..............#",
            "#....####......#",
            "#..............#",
            "################"
        };

        // The surfaces a projectile must NOT be stopped by, one room each so each test's reasoning is
        // local, plus one room for the complement (a solid tile that must still stop it). All use the
        // 8x8 size class rather than the 93 px needle: the question is whether the surface blocks at
        // all, and a 93 px needle cannot be positioned between two 40 px tile rows without overlapping
        // one of them, which would make the fixture — not the rule — decide the answer.
        //
        // Rows are ASCII rows; ASCII row r is tile y = height - 1 - r, so for these 6-row rooms:
        //
        //   RoomWithCatwalk         catwalk row y=3, columns 5..8:      px x 200..360, y 120..160
        //   RoomWithSlope           slope tile y=1, column 10:          px x 400..440, y  40..80
        //   RoomWithLadder          ladder tile y=3, column 5:          px x 200..240, y 120..160
        //   RoomWithCatwalkOnSolid  catwalk y=3 over solid y=2, col 5:  solid px x 200..240, y 80..120
        private static readonly string[] RoomWithCatwalk =
        {
            "################",
            "#..............#",
            "#....====......#",
            "#..............#",
            "#..............#",
            "################"
        };

        private static readonly string[] RoomWithSlope =
        {
            "################",
            "#..............#",
            "#..............#",
            "#..............#",
            "#........./....#",
            "################"
        };

        private static readonly string[] RoomWithLadder =
        {
            "################",
            "#..............#",
            "#....H.........#",
            "#..............#",
            "#..............#",
            "################"
        };

        private static readonly string[] RoomWithCatwalkOnSolid =
        {
            "################",
            "#..............#",
            "#....=.........#",
            "#....#.........#",
            "#..............#",
            "################"
        };

        // The five surfaces the B1 contact scenarios aim at, in world pixels. Named so a failure says
        // which surface moved rather than which float was off.
        private const float FloorTopPx = 40f;
        private const float ShelfUndersidePx = 80f;
        private const float ShelfTopPx = 120f;
        private const float CeilingUndersidePx = 160f;
        private const float RightWallInnerFacePx = 600f;

        /// <summary>
        /// The shipped <c>projectile.prefab</c> collider: <c>m_Size {x: 0.93, y: 0.06}</c>,
        /// <c>m_Direction: 1</c> (horizontal axis). So the real hitbox is a <b>93 px needle</b>, not a
        /// dot, and its leading tip sits ~46 px ahead of the transform centre. Read off the prefab,
        /// not assumed.
        /// </summary>
        private static readonly Vector2 NeedleSizePx = new Vector2(93f, 6f);

        /// <summary>
        /// The 8x8 size class B1's scenarios were authored against. Its length equals its thickness,
        /// so a capsule's straight section collapses to exactly zero and the shape is really a
        /// <b>circle</b> — which is what keeps these scenarios a like-for-like replacement for B1's
        /// overlap predicate, free of any dependence on the needle's orientation.
        ///
        /// <para>That degenerate case is not free: the engine <i>rejects</i> a capsule with coincident
        /// centres, so the seam has to build a circle instead.
        /// <see cref="SquareSize_IsSweptAsACircle_NotAsADegenerateCapsule"/> pins that directly,
        /// because the scenarios below only exercise it as a side effect.</para>
        /// </summary>
        private static readonly Vector2 BoxSizePx = new Vector2(8f, 8f);

        /// <summary>
        /// The seam reports the tangency point, which for a circle or a capsule cap touching a chain
        /// lies on the chain line itself. A hair over 1 px absorbs Box2D's iterative GJK without
        /// letting a genuinely wrong surface pass.
        /// </summary>
        private const float ContactTolerancePx = 1.5f;

        private const int NoContact = -1;

        // ── (1) The B1 contact scenarios, asked through the sweep ───────────────────────────────

        /// <summary>
        /// Each of B1's five contact situations must produce a sweep contact <b>on the surface B1's
        /// classic backend collided with</b>. Asserting the contact coordinate and not merely
        /// "something was hit" is the point: a hit-everything query, a mis-scaled capsule or a
        /// mirrored sign would all pass a bare <c>IsTrue</c>.
        /// </summary>
        [TestCaseSource(nameof(B1ContactScenarios))]
        public void B1ContactScenario_TheSweepReportsTheSameSurface(
            string name, Vector2 startPx, Vector2 velocityPxPerFrame, int ticks, Axis axis, float expectedSurfacePx)
        {
            using var service = new PhysicsWorldService();
            BuildOriginRoom(service);

            SweepContact contact = RunSweepTrajectory(
                service, startPx, velocityPxPerFrame, BoxSizePx, ticks);

            Assert.IsTrue(contact.Touched,
                $"[{name}] the sweep never reported contact, so it missed geometry the B1 suite already " +
                $"proved is there (classic backend hits it). Trace:\n" +
                Trace(service, startPx, velocityPxPerFrame, BoxSizePx, ticks));

            float actual = axis == Axis.X ? contact.PointPx.x : contact.PointPx.y;
            string axisName = axis == Axis.X ? "x" : "y";

            Assert.That(actual, Is.EqualTo(expectedSurfacePx).Within(ContactTolerancePx),
                $"[{name}] the sweep hit something, but not the surface B1 collides with. Expected " +
                $"{axisName}={expectedSurfacePx} px, got {axisName}={actual:F2} px " +
                $"({actual - expectedSurfacePx:+0.0;-0.0} px off), at {contact.PointPx} px on tick " +
                $"{contact.Tick}. Trace:\n" +
                Trace(service, startPx, velocityPxPerFrame, BoxSizePx, ticks));

            AssertUnitNormal(contact, name);
        }

        /// <summary>
        /// The control, and the one scenario a false-positive bug fails. B1 has the same case; here it
        /// guards the new predicate rather than the old one.
        /// </summary>
        [Test]
        public void FreeSpaceScenario_TheSweepReportsNoContact()
        {
            using var service = new PhysicsWorldService();
            BuildOriginRoom(service);

            SweepContact contact = RunSweepTrajectory(
                service, new Vector2(60f, 140f), new Vector2(2f, 0f), BoxSizePx, ticks: 10);

            Assert.IsFalse(contact.Touched,
                $"[free_space] the sweep reported a tile contact in open air on tick {contact.Tick} at " +
                $"{contact.PointPx} px. A hit-everything filter, a capsule built in the wrong units, or " +
                "an AABB that is far too fat would all look exactly like this.");
        }

        // ── (1b) The square size class: a capsule with no straight section ──────────────────────

        /// <summary>
        /// The 8x8 size class the B1 scenarios above use has length == thickness, so a capsule's
        /// straight section collapses to exactly zero and the shape <b>is</b> a circle.
        ///
        /// <para>This gets its own test because the engine does not tolerate that degenerate capsule:
        /// <c>PhysicsShape.ShapeProxy</c> throws <c>ArgumentException "Capsule Geometry is not
        /// valid"</c> when the two centres coincide, so the seam must build a <c>CircleGeometry</c>
        /// instead. Without that, <b>every</b> scenario in this fixture that uses
        /// <see cref="BoxSizePx"/> threw instead of sweeping — which is how the defect was actually
        /// found, incidentally, as seven unrelated-looking failures. This test makes the case
        /// deliberate so a regression names itself instead.</para>
        ///
        /// <para>The expected surface is the floor's top, the same one the equivalent B1 scenario
        /// expects: a radius-4 circle reaching the floor reports the contact <i>on the floor</i>, not
        /// at the circle's own centre 4 px above it.</para>
        /// </summary>
        [Test]
        public void SquareSize_IsSweptAsACircle_NotAsADegenerateCapsule()
        {
            using var service = new PhysicsWorldService();
            BuildOriginRoom(service);

            Assert.That(BoxSizePx.x, Is.EqualTo(BoxSizePx.y),
                "Precondition: this test is specifically about the length == thickness case, so the " +
                $"size class must actually be square. Got {BoxSizePx}.");

            var from = new Vector2(450f, 100f);
            var delta = new Vector2(0f, -60f);

            Assert.IsTrue(
                service.TrySweepTiles(from, delta, BoxSizePx, new Vector2(0f, -1f),
                                      out Vector2 point, out Vector2 normal),
                "A square projectile must sweep. The engine rejects a capsule whose two centres " +
                "coincide ('Capsule Geometry is not valid'), so a failure here means the seam built a " +
                "degenerate capsule rather than a circle.");

            Assert.That(point.y, Is.EqualTo(FloorTopPx).Within(ContactTolerancePx),
                $"The square projectile must report the contact on the floor's top surface " +
                $"(y={FloorTopPx} px), not at its own centre. Got y={point.y:F2} px.");

            AssertUnitNormal(new SweepContact(0, point, normal), "square_size_into_floor");
        }

        /// <summary>
        /// The square size class through the <b>overlap-first</b> branch. The capsule version of this
        /// is <see cref="Sweep_StartingStraddlingASurface_ReportsContactInsteadOfAMiss"/>; this is the
        /// same case for a circle, and it exists because the circle takes a different overload
        /// (<c>TestOverlapGeometry(CircleGeometry, …)</c>) on a path that would otherwise never run.
        ///
        /// <para>It is worth pinning rather than trusting the symmetry with the capsule overload: the
        /// whole reason this fixture grew the tests above is that a shape assumption went unchecked.
        /// If <c>CircleGeometry.center</c> were not honoured the way the capsule's centres are, the
        /// overlap would be tested at the world origin instead of at the projectile, and a square
        /// projectile spawned inside a wall would fly straight through it.</para>
        /// </summary>
        [Test]
        public void SquareSize_StartingStraddlingASurface_ReportsContact()
        {
            using var service = new PhysicsWorldService();
            BuildOriginRoom(service);

            // Centred ON the floor's top surface: a radius-4 circle spans y 36..44 px, so it is
            // through the zero-thickness chain at y=40 before the step even begins.
            var from = new Vector2(450f, FloorTopPx);
            var delta = new Vector2(0f, -60f);

            Assert.IsTrue(
                service.TrySweepTiles(from, delta, BoxSizePx, new Vector2(0f, -1f),
                                      out Vector2 point, out Vector2 normal),
                "A square projectile already straddling the floor's surface must report contact. A " +
                "false here means the circle's overlap branch did not run — most likely because " +
                "CircleGeometry.center is not being honoured — and this bullet would pass through the " +
                "floor.");

            Assert.That(point.x, Is.EqualTo(from.x),
                $"An initial overlap has no sweep fraction, so the seam reports the start position. " +
                $"Expected x={from.x}, got x={point.x}.");
            Assert.That(point.y, Is.EqualTo(from.y),
                $"An initial overlap has no sweep fraction, so the seam reports the start position. " +
                $"Expected y={from.y}, got y={point.y}.");

            Assert.That(normal.x, Is.EqualTo(0f),
                "CastResult documents the normal as zero (degenerate) in the initial-overlap case.");
            Assert.That(normal.y, Is.EqualTo(0f),
                "CastResult documents the normal as zero (degenerate) in the initial-overlap case.");
        }

        // ── (2) The needle, and the step that would tunnel ──────────────────────────────────────

        /// <summary>
        /// <b>The test this whole seam exists for.</b> A 93 px needle crosses the room in a single
        /// 500 px step — AS3's fastest bullets are in this range — and the wall's inner face is a
        /// zero-thickness chain.
        ///
        /// <para>The scenario is chosen so the end pose is <i>260 px past the wall</i>. That matters:
        /// with a shorter step the end pose would happen to straddle the wall anyway, and the test
        /// would pass against an implementation that only tested the end pose. So the naive
        /// alternative is <b>measured</b> here, not assumed — and if it ever starts reporting a hit,
        /// this scenario has stopped being able to tell a sweep from an overlap, and the test says so
        /// instead of passing quietly.</para>
        /// </summary>
        [Test]
        public void FastSweep_CrossingTheWallInOneStep_StillHitsIt()
        {
            using var service = new PhysicsWorldService();
            BuildOriginRoom(service);

            var from = new Vector2(400f, 140f);
            var delta = new Vector2(500f, 0f);
            var facing = new Vector2(1f, 0f);

            var endPose = new Vector2(from.x + delta.x, from.y);

            // Precondition, measured first: an end-of-step test must see nothing here.
            Assert.IsFalse(EndPoseOverlapsAChain(service, endPose, NeedleSizePx),
                $"Precondition failed: the END pose {endPose} px already overlaps a chain, so this " +
                "scenario cannot distinguish a swept query from a naive end-of-step overlap. Move the " +
                "start further from the wall.");

            bool hit = service.TrySweepTiles(from, delta, NeedleSizePx, facing,
                                             out Vector2 point, out Vector2 normal);

            Assert.IsTrue(hit,
                $"The sweep did not hit the right wall's inner face (x={RightWallInnerFacePx} px) even " +
                $"though the needle's leading tip crossed it: the capsule starts with its tip at " +
                $"{from.x + NeedleSizePx.x * 0.5f} px and ends with it at " +
                $"{endPose.x + NeedleSizePx.x * 0.5f} px. A query that only tests the end pose would " +
                "report exactly this, and that is tunnelling through a zero-thickness surface.");

            Assert.That(point.x, Is.EqualTo(RightWallInnerFacePx).Within(ContactTolerancePx),
                $"The sweep must report the contact at the wall's inner face, x={RightWallInnerFacePx} px, " +
                $"not somewhere along the 500 px of travel. Got {point} px.");

            AssertUnitNormal(new SweepContact(0, point, normal), "fast_sweep_into_wall");
        }

        /// <summary>
        /// The needle's <b>length</b> is load-bearing, and this pins it. Facing down, the tip is
        /// 46.5 px ahead of the centre, so the centre is still above the floor when the tip lands on
        /// it. A seam that swept a small box at the projectile's centre would report this same contact
        /// 46 px lower — half a tile into the floor — which is the failure mode the capsule shape
        /// exists to prevent.
        /// </summary>
        [Test]
        public void NeedleSweep_IntoTheFloor_ContactsAtTheTipNotTheCentre()
        {
            using var service = new PhysicsWorldService();
            BuildOriginRoom(service);

            var from = new Vector2(450f, 100f);
            var delta = new Vector2(0f, -60f);
            var facing = new Vector2(0f, -1f);

            Assert.IsTrue(
                service.TrySweepTiles(from, delta, NeedleSizePx, facing, out Vector2 point, out Vector2 normal),
                $"The needle's leading tip travels from {from.y - NeedleSizePx.x * 0.5f} px down through " +
                $"the floor's top surface at y={FloorTopPx} px, so the sweep must hit.");

            Assert.That(point.y, Is.EqualTo(FloorTopPx).Within(ContactTolerancePx),
                $"The contact must lie on the floor's top surface (y={FloorTopPx} px). Got y={point.y:F2} " +
                $"px, which is {point.y - FloorTopPx:+0.0;-0.0} px off. A large negative value here is " +
                "the signature of sweeping the projectile's centre instead of its leading tip.");

            AssertUnitNormal(new SweepContact(0, point, normal), "needle_into_floor");
        }

        /// <summary>
        /// The case the shipped docs single out: <i>"Initially touching shapes are treated as a miss.
        /// You should check for overlap first if initial overlap is required."</i>
        ///
        /// <para>A projectile whose muzzle is inside a wall spawns already straddling the surface.
        /// Without the seam's overlap-first branch, <c>CastShape</c> would classify that as a miss and
        /// the bullet would fly out through the floor. This test is what keeps that branch honest —
        /// and it asserts the seam's documented contract for the branch, not just its boolean: the
        /// contact point is the start position (no sweep fraction exists) and the normal is zero,
        /// which is exactly what <c>CastResult</c> documents for an initial overlap.</para>
        /// </summary>
        [Test]
        public void Sweep_StartingStraddlingASurface_ReportsContactInsteadOfAMiss()
        {
            using var service = new PhysicsWorldService();
            BuildOriginRoom(service);

            // Centred ON the floor's top surface, facing down: the capsule spans ~46.5 px either side
            // of y=40, so it is through the surface before the step begins.
            var from = new Vector2(450f, FloorTopPx);
            var delta = new Vector2(0f, -60f);
            var facing = new Vector2(0f, -1f);

            Assert.IsTrue(
                service.TrySweepTiles(from, delta, NeedleSizePx, facing, out Vector2 point, out Vector2 normal),
                "A capsule already straddling the floor's surface must report contact. The shipped docs " +
                "for CastShape are explicit that it treats an initially-touching shape as a MISS, so a " +
                "false here means the overlap-first branch is not running and this bullet would pass " +
                "straight through the floor.");

            Assert.That(point.x, Is.EqualTo(from.x),
                "An initial overlap has no sweep fraction, so the seam documents the contact point as " +
                $"the start position. Expected x={from.x}, got x={point.x}.");
            Assert.That(point.y, Is.EqualTo(from.y),
                "An initial overlap has no sweep fraction, so the seam documents the contact point as " +
                $"the start position. Expected y={from.y}, got y={point.y}.");

            Assert.That(normal.x, Is.EqualTo(0f),
                "CastResult documents the normal as zero (degenerate) in the initial-overlap case.");
            Assert.That(normal.y, Is.EqualTo(0f),
                "CastResult documents the normal as zero (degenerate) in the initial-overlap case.");
        }

        /// <summary>
        /// The seam must report nothing when there is nothing to report — the needle version of the
        /// free-space control, positioned so the <i>narrow phase</i> runs rather than the AABB reject
        /// short-circuiting it. y=140 sits between the shelf's top (120) and the ceiling's underside
        /// (160), and the capsule's 6 px thickness does not reach either, so the swept AABB admits the
        /// ceiling chain as a candidate and the cast is what has to decline it.
        /// </summary>
        [Test]
        public void NeedleSweep_ThroughOpenAir_ReportsNoContact()
        {
            using var service = new PhysicsWorldService();
            BuildOriginRoom(service);

            var from = new Vector2(100f, 140f);
            var delta = new Vector2(160f, 0f);

            Assert.IsFalse(
                service.TrySweepTiles(from, delta, NeedleSizePx, Vector2.right, out Vector2 point, out _),
                $"The needle's tip stays {RightWallInnerFacePx - (from.x + delta.x + NeedleSizePx.x * 0.5f)} px " +
                "short of the right wall and clears the shelf and the ceiling, so there is nothing to " +
                $"hit. Reported a contact at {point} px.");

            // The horizontal move never approaches the ceiling vertically, so the ceiling chain is a
            // candidate the narrow phase must reject. Asserted separately so a future change that
            // widens the AABB cannot quietly turn this into an AABB-only test.
            Assert.IsFalse(EndPoseOverlapsAChain(service, new Vector2(from.x + delta.x, from.y), NeedleSizePx),
                "Sanity: the end pose itself is clear, so the false above came from the cast and not " +
                "from a fixture that is accidentally inside a wall.");
        }

        // ── (3) World space, not room space ─────────────────────────────────────────────────────

        /// <summary>
        /// The defect this guards is the one that survived the entire B0/B1 suite: geometry emitted in
        /// room-local space instead of world space. A room at land position (0,0) cannot see it,
        /// because there the two coincide — which is precisely why every other test in this file is
        /// blind to it and why this one deliberately is not.
        ///
        /// <para>The offset is <b>read from the query service</b> rather than recomputed, so this test
        /// cannot become a second definition of where a room is, and it keeps passing if the room-size
        /// convention ever changes.</para>
        /// </summary>
        [Test]
        public void Sweep_AwayFromTheOrigin_IsInWorldSpace()
        {
            using var service = new PhysicsWorldService();

            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf, landX: 1, landY: 1);
            service.BuildRoomGeometry(room);

            var query = new UnifiedTileQueryService(room);
            Vector2 origin = query.OriginPixel;

            Assert.That(origin, Is.Not.EqualTo(Vector2.zero),
                "Precondition failed: this fixture must actually be displaced from the origin, or the " +
                "test cannot observe a missing origin step at all. Got OriginPixel == (0,0).");

            var displacedFrom = new Vector2(origin.x + 450f, origin.y + 100f);

            Assert.IsTrue(
                service.TrySweepTiles(displacedFrom, new Vector2(0f, -60f), NeedleSizePx,
                                      new Vector2(0f, -1f), out Vector2 point, out _),
                $"The sweep found no floor at the displaced room's own coordinates ({displacedFrom} px, " +
                $"origin {origin}). The chain mirror is not in world space — see the origin step in " +
                "RoomChainGeometry.EmitChain.");

            Assert.That(point.y, Is.EqualTo(origin.y + FloorTopPx).Within(ContactTolerancePx),
                $"The contact must be at the DISPLACED surface height. Expected y=" +
                $"{origin.y + FloorTopPx} px (origin.y {origin.y} + floor {FloorTopPx}), got y={point.y:F2} px.");

            // And nothing may be found at the room-local coordinates. Without this, a mirror emitted
            // twice — once at the origin and once displaced — would satisfy the assertions above.
            Assert.IsFalse(
                service.TrySweepTiles(new Vector2(450f, 100f), new Vector2(0f, -60f), NeedleSizePx,
                                      new Vector2(0f, -1f), out _, out _),
                "Geometry was also found at the room-local coordinates, so the room is mirrored twice " +
                "or the origin step is not applied at all.");
        }

        // ── (4) What must NOT block a bullet ────────────────────────────────────────────────────

        /// <summary>
        /// A catwalk (AS3 <c>shelf</c>) must not stop a projectile.
        ///
        /// <para>AS3's bullet test reads <c>phis</c> and nothing else (<c>weapon/Bullet.as:476</c>:
        /// <c>_loc3_.phis == 1 || _loc3_.phis == 2</c> against the cell the bullet occupies), and a
        /// <c>shelf</c> form carries <c>phis = 0</c>. A shelf is a <b>one-way platform</b> — its
        /// behaviour lives in <c>Box.as:1270</c> / <c>Unit.as:2578</c>, which gate it on the moving
        /// body's own bottom edge. Emitting it as a chain surface made bullets stop in mid-air on
        /// every catwalk.</para>
        ///
        /// <para>The control at the end is the point: the same column swept further must still find
        /// the floor, so the <c>false</c> above cannot be "this room has no geometry".</para>
        /// </summary>
        [Test]
        public void Sweep_IntoACatwalk_PassesThrough_ButStillFindsTheFloor()
        {
            using var service = new PhysicsWorldService();
            service.BuildRoomGeometry(SyntheticRoomBuilder.BuildFromAscii(RoomWithCatwalk));

            // The catwalk's top would sit at y=160 and the floor's at y=40, so a step of 60 px from
            // y=190 crosses the catwalk and stops well short of the floor.
            var from = new Vector2(280f, 190f);

            Assert.IsFalse(
                service.TrySweepTiles(from, new Vector2(0f, -60f), BoxSizePx,
                                      new Vector2(0f, -1f), out _, out _),
                "The sweep reported a contact on a catwalk. AS3 shelves are phis=0, so a bullet passes " +
                "straight through them.");

            Assert.IsTrue(
                service.TrySweepTiles(from, new Vector2(0f, -200f), BoxSizePx,
                                      new Vector2(0f, -1f), out Vector2 point, out _),
                "Sanity: the same column must still find the floor, or the assertion above holds " +
                "because the room produced no geometry at all.");

            Assert.That(point.y, Is.EqualTo(FloorTopPx).Within(ContactTolerancePx),
                $"The floor contact must be at y={FloorTopPx} px, got y={point.y:F2} px.");
        }

        /// <summary>
        /// A slope (AS3 <c>diagon</c>) must not stop a projectile either — and this is the answer to
        /// the question the implementation guide's Stage C checklist raises, "one new trace covers
        /// projectiles vs. a slope": the trace must assert <b>no contact</b>.
        ///
        /// <para>A <c>diagon</c> form carries <c>phis = 0</c> with <c>diagon = ±1</c>. The flag
        /// describes the <i>walkable ramp height</i> — <c>Tile.getSurface</c> interpolates
        /// <c>phY1</c> across the cell — not an occluder. Note this is why "emit the slope diagonal so
        /// the projectile hits it correctly" was the wrong fix: there is nothing to hit.</para>
        /// </summary>
        [Test]
        public void Sweep_IntoASlope_PassesThrough_ButStillFindsTheFloor()
        {
            using var service = new PhysicsWorldService();
            service.BuildRoomGeometry(SyntheticRoomBuilder.BuildFromAscii(RoomWithSlope));

            // The slope tile's top would sit at y=80, the floor's at y=40. A step of 120 px from
            // y=190 crosses the slope and stops 30 px short of the floor.
            var from = new Vector2(420f, 190f);

            Assert.IsFalse(
                service.TrySweepTiles(from, new Vector2(0f, -120f), BoxSizePx,
                                      new Vector2(0f, -1f), out _, out _),
                "The sweep reported a contact on a slope. AS3 diagons are phis=0 — a slope is a " +
                "walkable ramp, not a wall — so a bullet passes through.");

            Assert.IsTrue(
                service.TrySweepTiles(from, new Vector2(0f, -200f), BoxSizePx,
                                      new Vector2(0f, -1f), out Vector2 point, out _),
                "Sanity: the same column must still find the floor.");

            Assert.That(point.y, Is.EqualTo(FloorTopPx).Within(ContactTolerancePx),
                $"The floor contact must be at y={FloorTopPx} px, got y={point.y:F2} px.");
        }

        /// <summary>
        /// A ladder (AS3 <c>stair</c>) must not stop a projectile either — the third of the three
        /// forms the class doc names, and the one the port calls a "ladder" (<c>TileQueryFlags.Ladder</c>,
        /// <c>TileData.IsClimbableLadder()</c>) even though AS3 calls the form <c>stair</c>.
        ///
        /// <para>This one was never actually broken on the flipped path — <c>Ladder</c> was not in the
        /// old <c>Solid | Platform | Slope</c> predicate — so the test pins the rule rather than a
        /// regression. It is here because the legacy per-tile path <i>is</i> broken for ladders: a real
        /// ladder decodes to <c>TilePhysicsType.Stair</c>, which gets a collider. Note the fixture
        /// writes <c>H</c>, which <c>SyntheticRoomBuilder</c> builds as <c>Air</c> plus
        /// <c>stairType</c>; the seam sees the same <c>Ladder</c> flag either way, so the assertion is
        /// faithful for the mirror, but <c>H</c> would not reproduce the legacy bug.</para>
        /// </summary>
        [Test]
        public void Sweep_IntoALadder_PassesThrough_ButStillFindsTheFloor()
        {
            using var service = new PhysicsWorldService();
            service.BuildRoomGeometry(SyntheticRoomBuilder.BuildFromAscii(RoomWithLadder));

            // The ladder occupies the cell y 120..160, so its top would sit at y=160; the floor's is
            // at y=40. A step of 60 px from y=190 crosses the ladder and stops well short of the floor.
            var from = new Vector2(220f, 190f);

            Assert.IsFalse(
                service.TrySweepTiles(from, new Vector2(0f, -60f), BoxSizePx,
                                      new Vector2(0f, -1f), out _, out _),
                "The sweep reported a contact on a ladder. AS3 stair forms are phis=0, so a bullet " +
                "passes through them.");

            Assert.IsTrue(
                service.TrySweepTiles(from, new Vector2(0f, -200f), BoxSizePx,
                                      new Vector2(0f, -1f), out Vector2 point, out _),
                "Sanity: the same column must still find the floor.");

            Assert.That(point.y, Is.EqualTo(FloorTopPx).Within(ContactTolerancePx),
                $"The floor contact must be at y={FloorTopPx} px, got y={point.y:F2} px.");
        }

        /// <summary>
        /// The other half of the rule, and the half a naive fix gets wrong: dropping catwalks and
        /// slopes from the geometry must not open a hole in the <b>solid</b> tile beside them.
        ///
        /// <para>Here a catwalk sits directly on top of a solid block. If the exposure test still
        /// counted the catwalk as solid, the block's top face would not be emitted and a bullet could
        /// enter the block from above without ever being stopped. The block's top face must therefore
        /// be present at y=120 — and the contact must be there, <i>not</i> at the catwalk's y=160,
        /// which is what distinguishes this from the two tests above.</para>
        /// </summary>
        [Test]
        public void Sweep_IntoASolidTileUnderACatwalk_StillStops_AtTheSolidTilesTop()
        {
            using var service = new PhysicsWorldService();
            service.BuildRoomGeometry(SyntheticRoomBuilder.BuildFromAscii(RoomWithCatwalkOnSolid));

            var from = new Vector2(220f, 190f);

            Assert.IsTrue(
                service.TrySweepTiles(from, new Vector2(0f, -100f), BoxSizePx,
                                      new Vector2(0f, -1f), out Vector2 point, out _),
                "The sweep passed through a solid tile because a catwalk sat on top of it. The " +
                "exposure test must use the same solid-only predicate as the geometry, or removing " +
                "the catwalk surface opens a hole in the solid tile behind it.");

            Assert.That(point.y, Is.EqualTo(120f).Within(ContactTolerancePx),
                $"The contact must be the solid tile's top face at y=120 px (its cell is y 80..120). " +
                $"A contact at y=160 would mean the catwalk still blocked; y={point.y:F2} px.");
        }

        // ── The captured evidence ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Prints what the seam actually reported. The implementation guide is blunt that the B1
        /// evidence was skipped last time ("<c>tileQueryLogDivergence</c> shipped defaulting to
        /// <c>0</c> and no one ever turned it on; do not repeat that"), so the numbers live in the
        /// test log rather than only in an assertion that passes.
        ///
        /// <para>Asserts only that the sweep did something; the value of this test is its output.</para>
        /// </summary>
        [Test]
        public void AllSweepScenarios_ReportTheCapturedContacts()
        {
            using var service = new PhysicsWorldService();
            BuildOriginRoom(service);

            var sb = new StringBuilder();
            sb.Append("[LLP2D C] swept tile contact over the chain mirror\n");
            sb.Append("  room ").Append(RoomWithShelf[0].Length).Append('x').Append(RoomWithShelf.Length)
              .Append(" tiles; B1-size projectile ").Append(BoxSizePx.x).Append('x').Append(BoxSizePx.y)
              .Append(" px; needle ").Append(NeedleSizePx.x).Append('x').Append(NeedleSizePx.y).Append(" px\n");

            sb.Append("  -- B1 contact scenarios, asked through the sweep --\n");
            int contacts = 0;
            foreach (SweepSpec spec in ContactSpecs())
            {
                SweepContact contact = RunSweepTrajectory(
                    service, spec.StartPx, spec.VelocityPxPerFrame, BoxSizePx, spec.Ticks);

                if (contact.Touched)
                {
                    contacts++;
                }

                string axisName = spec.Axis == Axis.X ? "x" : "y";
                float actual = spec.Axis == Axis.X ? contact.PointPx.x : contact.PointPx.y;

                sb.Append("  ").Append(spec.Name.PadRight(26))
                  .Append(contact.Touched
                      ? $"tick {contact.Tick,3}  contact=({contact.PointPx.x,7:F2},{contact.PointPx.y,7:F2})px"
                      : "never touched              ")
                  .Append(contact.Touched
                      ? $"  {axisName}={actual,7:F2} (expected {spec.ExpectedSurfacePx,3:F0})  n=({contact.Normal.x,6:F3},{contact.Normal.y,6:F3})"
                      : string.Empty)
                  .Append('\n');
            }

            SweepContact freeSpace = RunSweepTrajectory(
                service, new Vector2(60f, 140f), new Vector2(2f, 0f), BoxSizePx, ticks: 10);

            sb.Append("  ").Append("free_space".PadRight(26))
              .Append(freeSpace.Touched ? $"TOUCHED on tick {freeSpace.Tick}" : "never touched")
              .Append("   (control: must be 'never touched')\n");

            sb.Append("  -- the needle: the shape production actually fires --\n");
            AppendNeedleRow(sb, service, "into_floor", new Vector2(450f, 100f), new Vector2(0f, -60f),
                new Vector2(0f, -1f));
            AppendNeedleRow(sb, service, "into_right_wall_1step", new Vector2(400f, 140f), new Vector2(500f, 0f),
                new Vector2(1f, 0f));
            AppendNeedleRow(sb, service, "open_air", new Vector2(100f, 140f), new Vector2(160f, 0f),
                Vector2.right);
            AppendNeedleRow(sb, service, "starts_straddling_floor", new Vector2(450f, FloorTopPx),
                new Vector2(0f, -60f), new Vector2(0f, -1f));

            sb.Append("  ").Append(contacts).Append(" of ").Append(CountContactSpecs())
              .Append(" B1 contact scenarios produced a sweep contact.\n");

            Debug.Log(sb.ToString());

            Assert.Greater(contacts, 0, "No B1 contact scenario produced a contact, so nothing was compared.");
        }

        // ── Scenario plumbing ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Which coordinate of a contact point identifies the surface that was hit.
        ///
        /// <para><b>Must be public.</b> It appears in the parameter list of a <c>[TestCaseSource]</c> test
        /// method, and NUnit requires test methods to be public — so CS0051 ("inconsistent
        /// accessibility") fires the moment this is anything less accessible. The accessibility has to
        /// match the method, not the reflection NUnit does.</para>
        /// </summary>
        public enum Axis
        {
            /// <summary>A vertical surface (a wall face): the contact's x is the surface.</summary>
            X,

            /// <summary>A horizontal surface (a floor, a shelf, a ceiling): the contact's y is it.</summary>
            Y
        }

        private readonly struct SweepSpec
        {
            public SweepSpec(
                string name, Vector2 startPx, Vector2 velocityPxPerFrame, int ticks,
                Axis axis, float expectedSurfacePx)
            {
                Name = name;
                StartPx = startPx;
                VelocityPxPerFrame = velocityPxPerFrame;
                Ticks = ticks;
                Axis = axis;
                ExpectedSurfacePx = expectedSurfacePx;
            }

            public string Name { get; }
            public Vector2 StartPx { get; }
            public Vector2 VelocityPxPerFrame { get; }
            public int Ticks { get; }

            /// <summary>Which coordinate of the reported contact identifies the expected surface.</summary>
            public Axis Axis { get; }

            /// <summary>The surface's coordinate in world px, on the same axis.</summary>
            public float ExpectedSurfacePx { get; }
        }

        /// <summary>Where the sweep said contact happened, or <see cref="NoContact"/>.</summary>
        private readonly struct SweepContact
        {
            public SweepContact(int tick, Vector2 pointPx, Vector2 normal)
            {
                Tick = tick;
                PointPx = pointPx;
                Normal = normal;
            }

            public int Tick { get; }
            public Vector2 PointPx { get; }
            public Vector2 Normal { get; }
            public bool Touched => Tick != NoContact;
        }

        /// <summary>
        /// B1's <c>ContactSpecs</c>, same names, same start positions, same velocities, same tick
        /// budgets — only the expected surface is added, because the seam reports <i>where</i> and
        /// B1's predicate only reported <i>whether</i>.
        ///
        /// <para>Constant velocity for the same reason B1 used it: it keeps the scenario a fixed
        /// quantity that a failure can be read off. The sweep does not need B1's slow-speed handicap
        /// — <see cref="FastSweep_CrossingTheWallInOneStep_StillHitsIt"/> is the test that shows so —
        /// but matching B1's numbers is what makes the two suites comparable.</para>
        /// </summary>
        private static IEnumerable<SweepSpec> ContactSpecs()
        {
            // Falls 100 -> 40 px onto the floor, clear of the shelf in x (450 px is tile 11; the
            // shelf occupies tiles 5..8).
            yield return new SweepSpec("fall_onto_floor", new Vector2(450f, 100f), new Vector2(0f, -4f),
                ticks: 30, Axis.Y, FloorTopPx);

            // Falls 150 -> 120 px onto the shelf top, directly above tiles 5..8.
            yield return new SweepSpec("fall_onto_shelf", new Vector2(280f, 150f), new Vector2(0f, -4f),
                ticks: 30, Axis.Y, ShelfTopPx);

            // Rises 50 -> 80 px into the shelf's underside.
            yield return new SweepSpec("rise_into_shelf_underside", new Vector2(280f, 50f), new Vector2(0f, 4f),
                ticks: 20, Axis.Y, ShelfUndersidePx);

            // Rises 60 -> 160 px into the ceiling's underside.
            yield return new SweepSpec("rise_into_ceiling", new Vector2(450f, 60f), new Vector2(0f, 4f),
                ticks: 40, Axis.Y, CeilingUndersidePx);

            // Travels 500 -> 600 px into the right wall's inner face.
            yield return new SweepSpec("travel_into_right_wall", new Vector2(500f, 140f), new Vector2(4f, 0f),
                ticks: 40, Axis.X, RightWallInnerFacePx);
        }

        private static int CountContactSpecs()
        {
            int count = 0;
            foreach (SweepSpec _ in ContactSpecs())
            {
                count++;
            }

            return count;
        }

        private static IEnumerable<TestCaseData> B1ContactScenarios()
        {
            foreach (SweepSpec spec in ContactSpecs())
            {
                yield return new TestCaseData(
                        spec.Name, spec.StartPx, spec.VelocityPxPerFrame, spec.Ticks, spec.Axis,
                        spec.ExpectedSurfacePx)
                    .SetName($"Sweep_{spec.Name}");
            }
        }

        /// <summary>
        /// Integrates a constant-velocity trajectory and asks the seam once per tick, stopping at the
        /// first contact.
        ///
        /// <para>The conversion is left expanded rather than folded into a constant:
        /// <c>VelocityScale * dt / PixelToUnit == 1</c>, so one tick advances exactly the authored
        /// px/frame. Collapsing it would hide the 30x velocity-vs-acceleration confusion that
        /// <c>DualRunProjectileHarness</c> warns about.</para>
        ///
        /// <para>Facing follows the velocity, which is what the projectile does when it rotates to
        /// face its travel direction. For the 8x8 scenarios this is irrelevant — an 8x8 capsule is a
        /// circle — and it only becomes load-bearing for the needle.</para>
        /// </summary>
        private static SweepContact RunSweepTrajectory(
            PhysicsWorldService service, Vector2 startPx, Vector2 velocityPxPerFrame, Vector2 sizePx, int ticks)
        {
            float dt = 1f / PFE.Core.SimClock.CanonicalTicksPerSecond;

            Vector2 deltaPerTickPx =
                velocityPxPerFrame * ProjectilePhysicsMath.VelocityScale * dt / Llp2d.PixelToUnit;

            Vector2 facing = velocityPxPerFrame.sqrMagnitude > 0f
                ? velocityPxPerFrame.normalized
                : Vector2.right;

            Vector2 centrePx = startPx;

            for (int tick = 0; tick < ticks; tick++)
            {
                if (service.TrySweepTiles(centrePx, deltaPerTickPx, sizePx, facing,
                                          out Vector2 point, out Vector2 normal))
                {
                    return new SweepContact(tick, point, normal);
                }

                centrePx += deltaPerTickPx;
            }

            return new SweepContact(NoContact, Vector2.zero, Vector2.zero);
        }

        private static List<string> Trace(
            PhysicsWorldService service, Vector2 startPx, Vector2 velocityPxPerFrame, Vector2 sizePx, int ticks)
        {
            float dt = 1f / PFE.Core.SimClock.CanonicalTicksPerSecond;

            Vector2 deltaPerTickPx =
                velocityPxPerFrame * ProjectilePhysicsMath.VelocityScale * dt / Llp2d.PixelToUnit;

            Vector2 facing = velocityPxPerFrame.sqrMagnitude > 0f
                ? velocityPxPerFrame.normalized
                : Vector2.right;

            Vector2 centrePx = startPx;
            var lines = new List<string>();

            for (int tick = 0; tick < ticks; tick++)
            {
                bool hit = service.TrySweepTiles(centrePx, deltaPerTickPx, sizePx, facing,
                                                 out Vector2 point, out Vector2 normal);

                lines.Add(
                    $"    tick {tick,4}  from=({centrePx.x,8:F2},{centrePx.y,8:F2})px  " +
                    (hit ? $"HIT at ({point.x,7:F2},{point.y,7:F2})px" : "----"));

                if (hit)
                {
                    break;
                }

                centrePx += deltaPerTickPx;
            }

            return lines;
        }

        private static void AppendNeedleRow(
            StringBuilder sb, PhysicsWorldService service, string name,
            Vector2 fromPx, Vector2 deltaPx, Vector2 facing)
        {
            bool hit = service.TrySweepTiles(fromPx, deltaPx, NeedleSizePx, facing,
                                             out Vector2 point, out Vector2 normal);

            sb.Append("  ").Append(name.PadRight(26))
              .Append(hit
                  ? $"contact=({point.x,7:F2},{point.y,7:F2})px  n=({normal.x,6:F3},{normal.y,6:F3})"
                  : "no contact                    ")
              .Append('\n');
        }

        /// <summary>
        /// A unit-length normal is how the docs describe every non-overlapped <c>CastShape</c> result
        /// ("In all non-overlapped cases, this will be a unit-normal"). A zero or scaled normal means
        /// the hit came from somewhere other than a real sweep, so it is worth asserting rather than
        /// only printing.
        /// </summary>
        private static void AssertUnitNormal(SweepContact contact, string name)
        {
            Assert.That(contact.Normal.magnitude, Is.EqualTo(1f).Within(0.001f),
                $"[{name}] a genuine sweep hit must report a unit normal (CastResult: 'In all " +
                $"non-overlapped cases, this will be a unit-normal'). Got {contact.Normal} " +
                $"(|n|={contact.Normal.magnitude:F4}), which is the signature of an initial-overlap " +
                "result rather than the sweep this test is aiming at.");
        }

        /// <summary>
        /// The naive alternative, measured: place the projectile's AABB at the END of the step and ask
        /// whether it overlaps any chain. This is what a non-swept implementation would ask, and the
        /// point of <see cref="FastSweep_CrossingTheWallInOneStep_StillHitsIt"/> is that it comes back
        /// false where the sweep comes back true.
        ///
        /// <para>A box is used rather than the capsule on purpose: 93x6 has the same axial reach
        /// (46.5 px) as the 93x6 capsule and covers slightly more area at the corners, so "the box
        /// sees nothing" implies "the capsule sees nothing". For a precondition that is the
        /// conservative direction.</para>
        /// </summary>
        private static bool EndPoseOverlapsAChain(PhysicsWorldService service, Vector2 centrePx, Vector2 sizePx)
        {
            Vector2 sizeUnits = sizePx * Llp2d.PixelToUnit;

            PolygonGeometry box = PolygonGeometry.CreateBox(
                sizeUnits,
                0f,
                new PhysicsTransform(centrePx * Llp2d.PixelToUnit, PhysicsRotate.identity),
                false);

            // new QueryFilter(), not default: the parameterless ctor is the one documented to hit
            // everything — see DualRunProjectileHarness for the same note.
            return service.World.TestOverlapGeometry(box, new PhysicsQuery.QueryFilter());
        }

        /// <summary>
        /// Builds the shared fixture room at the origin.
        ///
        /// <para>Six of this fixture's eight tests compare a contact coordinate against a surface
        /// constant written in <b>world</b> pixels (<see cref="FloorTopPx"/>, <see cref="ShelfTopPx"/>,
        /// …). Those numbers are only the room's own coordinates while the room sits at the origin, so
        /// that precondition is asserted rather than assumed — otherwise a change to the builder's
        /// default land position would surface as six unrelated "wrong surface" failures.
        /// <see cref="Sweep_AwayFromTheOrigin_IsInWorldSpace"/> deliberately does not come through
        /// here, because being displaced is that test's entire subject.</para>
        /// </summary>
        private static void BuildOriginRoom(PhysicsWorldService service)
        {
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf);
            DualRunProjectileHarness.RequireOriginAtZero(room);
            service.BuildRoomGeometry(room);
        }
    }
}
