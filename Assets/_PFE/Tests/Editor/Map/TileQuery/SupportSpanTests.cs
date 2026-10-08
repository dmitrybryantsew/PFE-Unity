using NUnit.Framework;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Tests.EditMode.Systems.Map.TileCollision;
using UnityEngine;

namespace PFE.Tests.Editor.Map.TileQuery
{
    /// <summary>
    /// Pins the room's half of AS3 <c>Unit.shX1</c>/<c>shX2</c>:
    /// <c>TileCollisionMath.TryGetSupportSpan</c>, reached through
    /// <c>ITileQueryService.TryGetSupportSpan</c>.
    ///
    /// <para><b>Why this fixture exists.</b> The oracle's reduction is a <c>min</c> over the tiles under
    /// the feet (<c>Unit.as:2301</c>/<c>:2305</c>), and because <c>-(X1 - phX1)</c> is smallest when
    /// <c>phX1</c> is smallest, the pair of fractions collapses to one <b>span</b>: the leftmost
    /// supporting tile's left edge and the rightmost supporting tile's right edge. That identity is the
    /// whole design — and it is exactly the kind of claim that is easy to state and wrong to implement,
    /// so it is asserted here rather than trusted.</para>
    ///
    /// <para><b>The named agreement invariant.</b>
    /// <c>SupportSpan_IsFoundExactlyWhenIsOnGroundSaysSo</c> is the control that makes the deliberate
    /// duplication of <c>CheckGroundCollisionAt</c>'s three branches safe. <c>IsOnGround</c> is the port's
    /// <c>isLaz</c> and stops at the first wall; the span needs <i>every</i> supporting tile, so it cannot
    /// share that loop. The failure this test is built to catch is one copy being edited and the other
    /// not — which would otherwise show up only as a zombie that hops at a ledge it is not on.</para>
    ///
    /// <para><b>Offline.</b> <c>SyntheticRoomBuilder</c> and <c>RoomInstance</c> are plain classes, so this
    /// fixture runs without a <c>GameObject</c>, a scene or the editor. The rooms are built at land
    /// (0, 0), so world pixels and room-local pixels coincide — the same convention
    /// <c>TileQueryServiceTests</c> uses.</para>
    /// </summary>
    [TestFixture]
    public sealed class SupportSpanTests
    {
        private const float Tile = 40f;

        private static readonly TileQueryOptions NoFallThrough =
            new TileQueryOptions(canFallThroughPlatforms: false);

        private static readonly TileQueryOptions FallThrough =
            new TileQueryOptions(canFallThroughPlatforms: true);

        // ── Arrange helpers ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The rect <c>UnitController.ResolveGroundState</c> actually hands the seam.
        /// <c>UnitGroundProbe.ToProbeRectPixels</c> places <c>yMin</c> on the tile <b>surface</b> — the
        /// feet minus the 1 px seat — because <c>IsOnGround</c> then samples <c>yMin - 1</c> and needs that
        /// point to land <i>inside</i> the ground row, not on its boundary. Height is carried through but
        /// read by neither query; only <c>yMin</c>, <c>width</c> and <c>center.x</c> matter.
        /// </summary>
        private static Rect OnSurface(float centreX, float surfaceY, float widthPx)
            => new Rect(centreX - widthPx * 0.5f, surfaceY, widthPx, 70f);

        /// <summary>A body standing on a floor whose top surface is one tile up (40 px).</summary>
        private static Rect OnFloor(float centreX, float widthPx = Tile) => OnSurface(centreX, Tile, widthPx);

        /// <summary>A body standing on a platform whose top surface is two tiles up (80 px).</summary>
        private static Rect OnCatwalk(float centreX, float widthPx = Tile) => OnSurface(centreX, 2f * Tile, widthPx);

        private static UnifiedTileQueryService Service(RoomInstance room) => new UnifiedTileQueryService(room);

        // ── Rooms ───────────────────────────────────────────────────────────────────────────

        /// <summary>16 x 5, a solid slab at row 0 and side walls. The plain "standing on the floor" room.</summary>
        private static RoomInstance FloorRoom()
            => SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..............#",
                "################"
            });

        /// <summary>
        /// 16 x 5 with the floor stopping at tile 4: tiles 0..4 are wall and 5..13 are air, so a body
        /// centred on the tile-4/5 boundary has its right half past the lip.
        /// </summary>
        private static RoomInstance LedgeRoom()
            => SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..............#",
                "#####.........##"
            });

        /// <summary>
        /// 16 x 5 with a <b>hole</b> in the floor at tile 5 — support at 0..4 and again at 6..15. A body
        /// wide enough to straddle the hole is supported on both sides of it, which is the case that
        /// separates "independent min/max" from "the contiguous run under the feet".
        /// </summary>
        private static RoomInstance FloorWithAHoleRoom()
            => SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..............#",
                "#####.##########"
            });

        /// <summary>16 x 5 with a one-way platform (a catwalk) at row 1, tiles 4..7, and no floor.</summary>
        private static RoomInstance CatwalkRoom()
            => SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#...====.......#",
                "#..............#"
            });

        /// <summary>16 x 5 with a ladder tile at (5, 1) and nothing else.</summary>
        private static RoomInstance LadderRoom()
            => SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#....H.........#",
                "#..............#"
            });

        /// <summary>16 x 5 with a <c>/</c> slope at (5, 1) — rises from 40 px at its left edge to 80 at its right.</summary>
        private static RoomInstance SlopeRoom()
            => SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..../.........#",
                "#..............#"
            });

        // ── The span on plain ground ────────────────────────────────────────────────────────

        /// <summary>
        /// A body well inside a slab: the span is the two tiles under its feet, and both fractions come
        /// out negative — fully supported on both sides.
        /// </summary>
        [Test]
        public void SupportSpan_OnASolidFloor_IsTheTilesUnderTheFeet()
        {
            var service = Service(FloorRoom());
            Rect probe = OnFloor(200f); // body 180..220 → tiles 4 and 5

            Assert.IsTrue(service.TryGetSupportSpan(probe, NoFallThrough, out float left, out float right),
                "the body is standing on the slab, so a span must be found.");

            Assert.That(left, Is.EqualTo(160f), "tile 4's left edge.");
            Assert.That(right, Is.EqualTo(240f), "tile 5's right edge.");

            // ...and the fractions the unit layer derives from it.
            Assert.That(UnitOverhangMath.OverhangLeft(probe.xMin, left, probe.width),
                Is.LessThan(0f), "the support starts before the body's left edge → supported.");
            Assert.That(UnitOverhangMath.OverhangRight(probe.xMax, right, probe.width),
                Is.LessThan(0f), "...and ends after its right edge → supported.");
        }

        // ── The span at a ledge, which is the whole point ────────────────────────────────────

        /// <summary>
        /// The floor stops at tile 4: the span ends at that tile's right edge, and the fraction the unit
        /// layer derives says the right half of the body is past the lip — exactly the chase threshold.
        /// </summary>
        /// <remarks>
        /// This is the arithmetic the zombie's <c>shX2 &gt; 0.5</c> gate reads, computed end to end from a
        /// room and a rect with no <c>GameObject</c> anywhere. It is the closest a pure fixture can get to
        /// "a zombie at a ledge", and it is what makes the owner-only behavioural tests plausible rather
        /// than hopeful.
        /// </remarks>
        [Test]
        public void SupportSpan_AtALedge_EndsAtTheLastSupportingTile()
        {
            var service = Service(LedgeRoom());
            Rect probe = OnFloor(200f); // body 180..220: tile 4 is floor, tile 5 is air

            Assert.IsTrue(service.TryGetSupportSpan(probe, NoFallThrough, out float left, out float right),
                "half the body is on the floor, so a span must be found — the empty case is a different test.");

            Assert.That(left, Is.EqualTo(160f), "tile 4's left edge.");
            Assert.That(right, Is.EqualTo(200f),
                "tile 4's right edge — NOT tile 5's, because tile 5 is air and does not support. A span " +
                "that ran to 240 would mean the air tile was counted.");

            Assert.That(UnitOverhangMath.OverhangLeft(probe.xMin, left, probe.width),
                Is.EqualTo(-0.5f).Within(0.0001f),
                "-(180-160)/40 = -0.5: the left side is well inside the support.");

            Assert.That(UnitOverhangMath.OverhangRight(probe.xMax, right, probe.width),
                Is.EqualTo(0.5f).Within(0.0001f),
                "(220-200)/40 = +0.5: the right half of the body is past the lip. This is the number the " +
                "chase's `shX2 > 0.5` gate compares — and it is exactly AT the threshold, so the strict " +
                "`>` means this particular lip does not yet fire the chase. A zombie one pixel further " +
                "over would.");

            Assert.IsTrue(UnitOverhangMath.IsAtEdgeToward(1, 0f, 0.5f, UnitOverhangMath.PatrolEdgeThreshold),
                "for the PATROL, however, 0.5 is well past its 0.25 — so the same lip is a patrol edge and " +
                "not yet a chase edge. That asymmetry is the oracle's.");
        }

        // ── The hole: independent min/max, not the contiguous run ───────────────────────────

        /// <summary>
        /// A hole in the floor does not split the span — it <b>widens</b> it, because the min and the max
        /// are taken independently. The span is identical to the same floor with the hole filled.
        /// </summary>
        /// <remarks>
        /// This is the assertion that distinguishes the oracle's two separate <c>min</c> reductions from
        /// the obvious wrong implementation — "the contiguous run of supporting tiles under the feet" —
        /// which would stop at the hole and report a much narrower span, turning the zombie around at a
        /// lip that is not there.
        /// </remarks>
        [Test]
        public void SupportSpan_AcrossAGap_WidensRatherThanSplits()
        {
            var holed = Service(FloorWithAHoleRoom());
            var solid = Service(FloorRoom());

            Rect probe = OnFloor(220f, 120f); // body 160..280 → tiles 4, 5, 6, 7; tile 5 is the hole

            Assert.IsTrue(holed.TryGetSupportSpan(probe, NoFallThrough, out float hLeft, out float hRight),
                "tiles 4, 6 and 7 all support, so a span must be found.");

            Assert.That(hLeft, Is.EqualTo(160f), "the leftmost supporting tile is 4, before the hole.");
            Assert.That(hRight, Is.EqualTo(320f), "the rightmost supporting tile is 7, after it.");

            Assert.IsTrue(solid.TryGetSupportSpan(probe, NoFallThrough, out float sLeft, out float sRight),
                "control: the same body on the floor with no hole.");

            Assert.That(hLeft, Is.EqualTo(sLeft),
                "the hole is invisible to the span: the left edge is the same either way.");
            Assert.That(hRight, Is.EqualTo(sRight),
                "...and so is the right edge. A contiguous-run implementation would have returned " +
                "[160, 200] here — stopping at the hole — which is the divergence this test exists to " +
                "catch.");
        }

        // ── One-way platforms and the `throu` flag ───────────────────────────────────────────

        /// <summary>
        /// A catwalk supports a body standing on it, and stops supporting the moment the unit asks to
        /// fall through — the same flag that makes <c>IsOnGround</c> report airborne.
        /// </summary>
        /// <remarks>
        /// The pair is the point: <c>canFallThrough</c> is AS3's <c>throu</c>, and a zombie that has asked
        /// to drop must not read the catwalk as the ledge it is standing on — otherwise it would hop off
        /// the very platform it is trying to fall through.
        /// </remarks>
        [Test]
        public void SupportSpan_CanFallThrough_SkipsThePlatform()
        {
            var service = Service(CatwalkRoom());
            Rect probe = OnCatwalk(220f); // body 200..240 → platform tiles 5 and 6

            Assert.IsTrue(service.TryGetSupportSpan(probe, NoFallThrough, out float left, out float right),
                "seated on the catwalk, the platform is ground.");

            Assert.That(left, Is.EqualTo(200f), "tile 5's left edge.");
            Assert.That(right, Is.EqualTo(280f), "tile 6's right edge — the span is only the tiles under the feet.");

            Assert.IsFalse(service.TryGetSupportSpan(probe, FallThrough, out _, out _),
                "Unit.as:2568-2582 / UnitZombie.as:857-864 — with `throu` set, a one-way platform is not " +
                "support at all, so the span is empty and the unit is airborne. Walls are unaffected, " +
                "which is why a zombie holding the flag on a solid floor does not sink.");
        }

        /// <summary>
        /// The <c>porog</c> band is what makes a platform ground at all: a body outside the band is not on
        /// it, from either side.
        /// </summary>
        /// <remarks>
        /// The band is <c>y &lt;= tileTop &amp;&amp; y &gt;= tileTop - 10</c> (<c>TileCollisionMath.cs:169-177</c>),
        /// and the sample is <c>yMin - 1</c>, so for a catwalk whose top is at 80 the sample must be in
        /// <c>[70, 80]</c>. Both boundaries are asserted, because the band is the only thing standing
        /// between "a zombie on the catwalk" and "a zombie standing inside it".
        /// </remarks>
        [Test]
        public void SupportSpan_OutsideThePlatformBand_IsNotSupport()
        {
            var service = Service(CatwalkRoom());

            Assert.IsFalse(service.TryGetSupportSpan(OnSurface(220f, 61f, Tile), NoFallThrough, out _, out _),
                "sample y = 60: the body is 19 px below the catwalk's top edge, i.e. below the 10 px band " +
                "while still in the platform's own row, so the platform does not support it. `porog` is a " +
                "step-up allowance, not an invitation to stand inside the platform.");

            Assert.IsFalse(service.TryGetSupportSpan(OnSurface(220f, 90f, Tile), NoFallThrough, out _, out _),
                "sample y = 89: the body hovers a whole row above the catwalk, so the sample does not even " +
                "land on the platform's row.");

            Assert.IsTrue(service.TryGetSupportSpan(OnCatwalk(220f), NoFallThrough, out _, out _),
                "control: seated on it — sample y = 79, inside the band — the same platform IS support, so " +
                "the two assertions above are about the band and not about the room.");
        }

        // ── Things that are not support ─────────────────────────────────────────────────────

        /// <summary>
        /// A ladder is not a floor. Its rungs are for climbing, and a unit "standing" on one is on the
        /// rungs — AS3's <c>phis == 0</c> for a ladder.
        /// </summary>
        [Test]
        public void SupportSpan_LadderUnderTheFeet_IsNotSupport()
        {
            var service = Service(LadderRoom());
            Rect probe = OnCatwalk(220f); // body 200..240 at row 1, where the ladder is (tile 5)

            Assert.IsFalse(service.TryGetSupportSpan(probe, NoFallThrough, out _, out _),
                "the ladder tile is Air with ladder metadata, so it is neither Wall nor Platform nor a " +
                "slope surface — nothing supports. This is why the predicate is not TileData.IsSolid(): " +
                "`IsSolid` is `>= Wall`, which would admit a ladder as a floor.");
        }

        /// <summary>
        /// A slope <b>is</b> support — the third branch of the predicate, and the one a
        /// "Wall || Platform" reading of the code would drop.
        /// </summary>
        /// <remarks>
        /// The oracle's landing test is <c>collisionTile</c>, which admits a diagonal; only the
        /// <i>crate-hop</i> probe is the narrower <c>phis == 1 || shelf</c>. Two questions, two predicates
        /// — conflating them would either make a unit fall through every ramp or make it hop onto one.
        /// </remarks>
        [Test]
        public void SupportSpan_OnASlope_IsSupport()
        {
            var service = Service(SlopeRoom());

            // The `/` slope at tile (5, 1) spans x 200..240 and rises from y = 40 at its left edge to
            // y = 80 at its right. Its height at x = 220 is therefore 60, so a body whose sample sits at
            // 60 is on it. The sample is yMin - 1, so the surface is 61.
            Rect onTheSlope = OnSurface(220f, 61f, Tile);

            Assert.IsTrue(service.TryGetSupportSpan(onTheSlope, NoFallThrough, out float left, out float right),
                "the sample (y = 60) is at or below the slope's ground height at x = 220 (60), so the " +
                "slope supports the body.");

            Assert.That(left, Is.EqualTo(200f), "tile 5's left edge.");
            Assert.That(right, Is.EqualTo(240f), "tile 5's right edge.");

            Assert.IsFalse(service.TryGetSupportSpan(OnSurface(220f, 90f, Tile), NoFallThrough, out _, out _),
                "control: raised to a surface of 90 (sample 89), the body is above the slope's height at " +
                "that x, so nothing supports it — the assertion above is about the slope and not about " +
                "the room.");
        }

        /// <summary>An empty room has no support anywhere — <c>false</c>, which is AS3's answer.</summary>
        [Test]
        public void SupportSpan_NoSupport_ReturnsFalse()
        {
            var service = Service(SyntheticRoomBuilder.BuildEmpty(16, 12));

            Assert.IsFalse(service.TryGetSupportSpan(OnFloor(200f), NoFallThrough, out _, out _),
                "nothing in an empty room supports anything. Note `false` is NOT 'unknown': the caller " +
                "must read it as UnitOverhangMath.NoSupportOverhang, i.e. maximal overhang — a unit over " +
                "a gap is genuinely at the edge.");
        }

        // ── The agreement invariant ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>TryGetSupportSpan</c> finds a span <b>iff</b> <c>IsOnGround</c> is true, over every case in
        /// this file.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is the load-bearing test.</b> The span deliberately re-implements
        /// <c>CheckGroundCollisionAt</c>'s three branches instead of sharing its loop, because that method
        /// returns on the <i>first</i> wall and the span needs <i>every</i> supporting tile. That
        /// duplication is safe only while the two agree, and the failure it is designed to catch is one
        /// copy being edited and the other not — a divergence that would surface only as a zombie that
        /// hops at a ledge it is not standing on.</para>
        ///
        /// <para>The case set is the union of every arrange above, so it cannot drift away from what the
        /// other tests exercise, and the tally at the end forbids a vacuous pass (both false everywhere
        /// would satisfy the equality).</para>
        /// </remarks>
        [Test]
        public void SupportSpan_IsFoundExactlyWhenIsOnGroundSaysSo()
        {
            (string label, RoomInstance room, Rect probe, TileQueryOptions options)[] cases =
            {
                ("floor / on the slab",          FloorRoom(),            OnFloor(200f),            NoFallThrough),
                ("floor / body straddling a wall edge", FloorRoom(),      OnFloor(60f),             NoFallThrough),
                ("ledge / half past the lip",    LedgeRoom(),            OnFloor(200f),            NoFallThrough),
                ("ledge / on the floor part",    LedgeRoom(),            OnFloor(100f),            NoFallThrough),
                ("ledge / fully past the lip",   LedgeRoom(),            OnFloor(300f),            NoFallThrough),
                ("hole / straddling the hole",   FloorWithAHoleRoom(),   OnFloor(220f, 120f),      NoFallThrough),
                ("solid / straddling the same span", FloorRoom(),        OnFloor(220f, 120f),      NoFallThrough),
                ("catwalk / seated",             CatwalkRoom(),          OnCatwalk(220f),          NoFallThrough),
                ("catwalk / seated, falling through", CatwalkRoom(),     OnCatwalk(220f),          FallThrough),
                ("catwalk / below the band",     CatwalkRoom(),          OnSurface(220f, 61f, Tile), NoFallThrough),
                ("catwalk / a row above",        CatwalkRoom(),          OnSurface(220f, 90f, Tile), NoFallThrough),
                ("catwalk / in the band's lower edge", CatwalkRoom(),    OnSurface(220f, 72f, Tile), NoFallThrough),
                ("ladder / on the rungs",        LadderRoom(),           OnCatwalk(220f),          NoFallThrough),
                ("slope / on it",                SlopeRoom(),            OnSurface(220f, 61f, Tile), NoFallThrough),
                ("slope / above it",             SlopeRoom(),            OnSurface(220f, 90f, Tile), NoFallThrough),
                ("empty / nothing",              SyntheticRoomBuilder.BuildEmpty(16, 12), OnFloor(200f), NoFallThrough),
                ("empty / nothing, falling through", SyntheticRoomBuilder.BuildEmpty(16, 12), OnFloor(200f), FallThrough),
            };

            int grounded = 0;
            int airborne = 0;

            foreach ((string label, RoomInstance room, Rect probe, TileQueryOptions options) in cases)
            {
                var service = Service(room);

                bool onGround = service.IsOnGround(probe, options);
                bool spanFound = service.TryGetSupportSpan(probe, options, out float left, out float right);

                Assert.That(spanFound, Is.EqualTo(onGround),
                    $"'{label}': IsOnGround says {onGround} but TryGetSupportSpan says {spanFound}. The two " +
                    "duplicate the same three branches on purpose — the ground probe stops at the first " +
                    "wall, the span needs every supporting tile — and this is the control that makes the " +
                    "duplication safe. A disagreement means one copy was edited and the other not.");

                if (onGround)
                {
                    grounded++;
                    Assert.That(left, Is.LessThan(right),
                        $"'{label}': a found span must be non-empty (left {left}, right {right}).");
                }
                else
                {
                    airborne++;
                }
            }

            Assert.That(grounded, Is.GreaterThan(0),
                "the case set must contain grounded cases, or the agreement above is vacuous.");
            Assert.That(airborne, Is.GreaterThan(0),
                "...and airborne ones, or it would also pass for two functions that both always say true.");
        }
    }
}
