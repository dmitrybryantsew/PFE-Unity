using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Tests.EditMode.Systems.Map.TileCollision;
using UnityEngine;

namespace PFE.Tests.Editor.Map.TileQuery
{
    /// <summary>
    /// AS3 <c>Unit.throu</c> on a <b>ramp</b> — the diagonal staircase. A unit that has asked to fall
    /// must not be caught by a <c>diagon</c> tile.
    ///
    /// <para><b>The oracle.</b> <c>run()</c>'s downward branch is
    /// <c>if(_loc5_ == 0 &amp;&amp; !this.throu) { _loc5_ = checkDiagon(dy / param1); }</c>
    /// (<c>Unit.as:2334-2337</c>), immediately followed by the same guard around <c>checkShelf</c>
    /// (<c>:2338-2341</c>). The ramp and the catwalk are <b>one flag</b> in the original: the port had
    /// the catwalk half and not the ramp half, so a staircase was the only surface in the game that
    /// could not be dropped through. The player's gesture is the ordinary one —
    /// <c>keyDubSit</c> raises the flag (<c>UnitPlayer.as:2917-2925</c>) and
    /// <c>(keyJump || !stay) &amp;&amp; keySit</c> keeps it raised while airborne (<c>:2640</c>).</para>
    ///
    /// <para><b>Why this is a staircase and not "a slope".</b> The 12 tread forms
    /// (<c>В Г Ж З И Й Л М О П С Т</c>) are <c>diagon = ±1</c> with <c>phis == 0</c>
    /// (<c>Assets/_PFE/Data/tile_forms.json</c>), so a flight of them decodes to
    /// <c>physicsType = Air</c> + <c>slopeType = ±1</c> and is reached through
    /// <c>TileData.IsSlopeSurface()</c>, not through the <c>Platform</c> branch. The synthetic room
    /// spells the same cells with <c>/</c>, which <c>SyntheticRoomBuilder</c> gives
    /// <c>physicsType = Stair</c>; <see cref="Tread"/> is the decoder's shape and every case below runs
    /// against <b>both</b>, because the two differ in a field the ground query does not read and that is
    /// exactly the sort of coincidence that stops being true later.</para>
    ///
    /// <para><b>Offline.</b> <c>RoomInstance</c>, <c>SyntheticRoomBuilder</c> and
    /// <c>TileCollisionMath</c> are plain classes, so this fixture needs no <c>GameObject</c>, no scene
    /// and no editor. The rooms are built at land (0, 0), so world pixels and room-local pixels
    /// coincide. What it deliberately does <i>not</i> cover is the motor walking the fall — that needs a
    /// <c>GameObject</c> and is the owner-run fixture in <c>GoldenTraceTests</c>.</para>
    /// </summary>
    [TestFixture]
    public sealed class SlopeDropThroughTests
    {
        private const float Tile = 40f;

        /// <summary>AS3 <c>porog</c> — <c>TileQueryConstants.PorogGrounded</c>.</summary>
        private const float Porog = 10f;

        private static readonly TileQueryOptions NoFallThrough =
            new TileQueryOptions(canFallThroughPlatforms: false);

        private static readonly TileQueryOptions FallThrough =
            new TileQueryOptions(canFallThroughPlatforms: true);

        // ── Rooms ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 16 x 5 with one tread at tile (5, 1). It spans x 200..240 and y 40..80, and a <c>/</c> slope
        /// rises left-to-right, so its surface at the tile's centre (x = 220) is y = 60.
        /// </summary>
        private static RoomInstance StaircaseRoom()
            => SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..../.........#",
                "#..............#"
            });

        /// <summary>
        /// The same room with the tread rewritten to the shape <c>TileDecoder</c> actually produces for
        /// a <c>diagon</c> form: <c>phis == 0</c> is <see cref="TilePhysicsType.Air"/>, and the slope
        /// lives only in <c>slopeType</c>.
        /// </summary>
        private static RoomInstance Tread()
        {
            RoomInstance room = StaircaseRoom();
            room.tiles[5, 1].physicsType = TilePhysicsType.Air;
            room.tiles[5, 1].slopeType = 1;
            return room;
        }

        /// <summary>A solid slab at row 0 with side walls — the control that must keep stopping a fall.</summary>
        private static RoomInstance FloorRoom()
            => SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..............#",
                "################"
            });

        /// <summary>A catwalk at tile row 1 (tiles 4..7), top edge y = 80, over open air.</summary>
        private static RoomInstance CatwalkRoom()
            => SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#...====.......#",
                "#..............#"
            });

        // ── Arrange helpers ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The rect <c>UnitController.ResolveGroundState</c> hands the seam: <c>yMin</c> on the surface
        /// plus the 1 px seat, because <c>IsOnGround</c> samples <c>yMin - 1</c> and that point must land
        /// inside the ground row rather than on its boundary.
        /// </summary>
        private static Rect OnSurface(float centreX, float surfaceY, float widthPx)
            => new Rect(centreX - widthPx * 0.5f, surfaceY, widthPx, 70f);

        /// <summary>The feet exactly on the tread's surface at its centre — y = 60.</summary>
        private static Rect OnTread(float centreX = 220f) => OnSurface(centreX, 60f + 1f, Tile);

        private static UnifiedTileQueryService Service(RoomInstance room) => new UnifiedTileQueryService(room);

        // ── The rule ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The tread is ground until the unit asks to fall, and then it is not. This is the whole
        /// report: before the ramp gate, the second assertion was the one that failed, and a player on
        /// a staircase could not drop through it however hard they pressed Down.
        /// </summary>
        [TestCase(false, TestName = "synthetic / Stair physics")]
        [TestCase(true, TestName = "decoded / Air + slopeType")]
        public void Tread_IsGround_UntilTheUnitAsksToFall(bool decoderShape)
        {
            RoomInstance room = decoderShape ? Tread() : StaircaseRoom();
            var service = Service(room);

            Assert.That(service.IsOnGround(OnTread(), NoFallThrough), Is.True,
                "The tread holds the unit — a staircase you cannot stand on would be a different bug.");

            Assert.That(service.IsOnGround(OnTread(), FallThrough), Is.False,
                "`throu` removes the ramp from the ground search (Unit.as:2334). A diagonal staircase is " +
                "made of these tiles, so with this true the flight can be dropped through, which is what " +
                "the original does.");
        }

        /// <summary>
        /// The motor-level half of the same rule, on the function the motor actually calls: the report
        /// also carries <c>hitPlatform</c>, which is the port's <c>stayPhis == 2</c>.
        /// </summary>
        /// <remarks>
        /// <c>checkDiagon</c> sets <c>stayPhis = 2</c> (<c>Unit.as:2795-2800</c>) — a ramp is the same
        /// class of support as a catwalk, not a solid top. The flag has two readers that both need it:
        /// <c>StartPlatformDropThrough</c>'s 1 px nudge (without it a drop from a standstill waits a tick
        /// for gravity) and the oracle's sideways ladder-descent probes, which are gated on
        /// <c>stayPhis == 2</c> (<c>UnitPlayer.as:2847,2853</c>).
        /// </remarks>
        [TestCase(false, TestName = "synthetic / Stair physics")]
        [TestCase(true, TestName = "decoded / Air + slopeType")]
        public void Tread_ReportsOneWaySupport_AndStopsDoingSoWhenFalling(bool decoderShape)
        {
            RoomInstance room = decoderShape ? Tread() : StaircaseRoom();

            bool ground = TileCollisionMath.CheckGroundCollisionAt(
                room, 220f, 60f, Tile * 0.5f,
                0f, 0f, Porog, canFallThrough: false, out bool hitPlatform);

            Assert.That(ground, Is.True, "feet on the tread's surface at its centre (y = 60)");
            Assert.That(hitPlatform, Is.True,
                "a ramp is `stayPhis == 2` (checkDiagon, Unit.as:2795-2800) — one-way support, not a " +
                "solid top, which is what lets the drop nudge break contact with it");

            ground = TileCollisionMath.CheckGroundCollisionAt(
                room, 220f, 60f, Tile * 0.5f,
                0f, 0f, Porog, canFallThrough: true, out hitPlatform);

            Assert.That(ground, Is.False, "`throu` — the unit is falling through its own staircase");
            Assert.That(hitPlatform, Is.False,
                "and it is not standing on a one-way surface either, or the nudge would re-seat it");
        }

        // ── Controls: what the flag must NOT change ─────────────────────────────────────────

        /// <summary>
        /// <b>A wall still stops the fall.</b> AS3's <c>collisionTile</c> returns 1 for
        /// <c>phis &gt;= 1</c> before it ever looks at the flag (<c>Unit.as:2570</c>), which is the reason
        /// <c>throu</c> can be held for as long as the trigger holds: a unit that keeps it raised falls
        /// through every catwalk and every staircase and is still stopped by the floor.
        /// </summary>
        [Test]
        public void SolidFloor_StopsAFallEvenWhenTheUnitAsksToFall()
        {
            var service = Service(FloorRoom());

            // yMin on the slab's top edge, so IsOnGround's `yMin - 1` sample lands inside row 0.
            Assert.That(service.IsOnGround(OnSurface(200f, Tile, Tile), FallThrough), Is.True,
                "`throu` must never punch through a wall — the player would fall out of the room on every " +
                "double-tap.");
        }

        /// <summary>
        /// <b>The catwalk half still works.</b> The ramp gate is new; the platform gate is not, and a
        /// change that made one of them stop working would look identical from the outside.
        /// </summary>
        [Test]
        public void Catwalk_StillDropsThroughAsBefore()
        {
            var service = Service(CatwalkRoom());
            Rect onTheCatwalk = OnSurface(220f, 2f * Tile, Tile);

            Assert.That(service.IsOnGround(onTheCatwalk, NoFallThrough), Is.True);
            Assert.That(service.IsOnGround(onTheCatwalk, FallThrough), Is.False);
        }

        /// <summary>
        /// <b>The two copies agree.</b> <c>TryGetSupportSpan</c> re-implements the ground query's three
        /// branches on purpose (it needs <i>every</i> supporting tile, the ground probe stops at the first
        /// wall), so the ramp gate exists twice and this is the control that catches one being edited and
        /// the other not. <c>SupportSpanTests.SupportSpan_IsFoundExactlyWhenIsOnGroundSaysSo</c> pins the
        /// same invariant over a wider case set; this one pins the ramp, with the flag on and off.
        /// </summary>
        [TestCase(false, TestName = "throu off")]
        [TestCase(true, TestName = "throu on")]
        public void SupportSpan_AgreesWithIsOnGround_OnATread(bool falling)
        {
            var service = Service(Tread());
            TileQueryOptions options = falling ? FallThrough : NoFallThrough;
            Rect probe = OnTread();

            bool onGround = service.IsOnGround(probe, options);
            bool spanFound = service.TryGetSupportSpan(probe, options, out float left, out float right);

            Assert.That(spanFound, Is.EqualTo(onGround),
                "the ramp branch exists in both CheckGroundCollisionAt and IsSupportingTile; a disagreement " +
                "means only one of them was given the `throu` gate.");

            if (onGround)
            {
                Assert.That(left, Is.EqualTo(200f), "tile 5's left edge");
                Assert.That(right, Is.EqualTo(240f), "tile 5's right edge");
            }
        }

        /// <summary>
        /// <b>Off the tread is still off it.</b> The gate must not turn the ramp into ground for a unit
        /// that is merely near it — the row below the surface has no tile in this room, so the answer is
        /// "airborne" both ways and the fall is not secretly re-seated one row down.
        /// </summary>
        [Test]
        public void Tread_DoesNotSupportABodyBelowItsSurface()
        {
            var service = Service(Tread());
            Rect below = OnSurface(220f, 20f, Tile);

            Assert.That(service.IsOnGround(below, NoFallThrough), Is.False,
                "the sample at y = 19 is in row 0, which is air in this room");
            Assert.That(service.IsOnGround(below, FallThrough), Is.False);
        }
    }
}
