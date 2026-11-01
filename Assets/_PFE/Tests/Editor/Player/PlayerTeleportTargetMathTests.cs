using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Tests.EditMode.Systems.Map.TileCollision;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Player
{
    /// <summary>
    /// Fixtures for <see cref="PlayerTeleportTargetMath"/> — the destination snap behind
    /// <c>UnitPlayer.actPort</c> (<c>fe/unit/UnitPlayer.as:1641-1642</c>) and the destination square
    /// (<c>GUI.as:1531-1552</c>).
    ///
    /// <para><b>What these pin, and why they are not cosmetic.</b> The oracle's <c>_loc2_</c> is a
    /// y-down expression. The port copied it verbatim into a y-up motor, which put every landing point
    /// one whole cell <i>above</i> the cursor — inside the ceiling of any roofed room. That is the
    /// reported "teleporting does not work in a room", so the mirror itself is the regression under
    /// test here, not the arithmetic.</para>
    /// </summary>
    [TestFixture]
    public class PlayerTeleportTargetMathTests
    {
        private const float Tile = 40f;

        // ──────────────────────────────────────────────────────────────────────────────
        // The mirror
        // ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The oracle's <c>Math.round(celY / tileY + 1) * tileY - 1</c>, evaluated in AS3's own y-down
        /// space. Used as the reference the port is mirrored against.
        /// </summary>
        private static float As3FeetY(float celY, float tile)
            => Mathf.Floor((celY / tile) + 0.5f + 1f) * tile - 1f;

        /// <summary>
        /// For a sweep of cursor positions across a three-tile-tall room, the port's landing point must
        /// be the mirror image of the oracle's: same cell, same seam, flipped through the room.
        ///
        /// <para><b>Exact cell centres are skipped, deliberately.</b> AS3 rounds half <i>up</i> in a
        /// y-down space; mirrored into y-up that tie lands on the cell below, while rounding half up in
        /// the port's own space lands on the cell itself. The two agree everywhere except a cursor
        /// sitting on the exact half-pixel line, where the oracle's own answer is an artifact of its
        /// rounding direction rather than a rule. <see cref="Resolve_HalfPixelCursor_RoundsUp"/> pins
        /// what the port does there instead of pretending the ambiguity away.</para>
        /// </summary>
        [Test]
        public void Resolve_MatchesTheMirroredOracle_AcrossASweep()
        {
            const float roomHeightPixels = 120f;   // three tiles
            int compared = 0;

            for (float y = 1f; y < roomHeightPixels; y += 1f)
            {
                if (Mathf.Approximately(Mathf.Repeat(y, Tile), Tile * 0.5f))
                {
                    continue;   // exact cell centre — see the summary
                }

                PlayerTeleportTargetMath.Target target =
                    PlayerTeleportTargetMath.Resolve(new Vector2(0f, y), Tile);

                float celY = roomHeightPixels - y;
                float expectedFeetY = roomHeightPixels - As3FeetY(celY, Tile);

                Assert.AreEqual(expectedFeetY, target.FeetPixelY, 0.001f,
                    $"cursor y={y}: the port's feet must mirror the oracle's, not copy its sign.");
                compared++;
            }

            Assert.Greater(compared, 100, "the sweep must actually have compared something");
        }

        /// <summary>
        /// The feet stand one pixel above the destination cell's <b>bottom</b> seam — AS3's <c>- 1</c>,
        /// mirrored. This is the assertion the old expression failed by 78 px (2 * tile - 2).
        /// </summary>
        [Test]
        public void Resolve_FeetSitOnePixelAboveTheCellBottom()
        {
            // Cursor in the middle of the cell [80, 120): cell 2, bottom seam 80.
            PlayerTeleportTargetMath.Target target =
                PlayerTeleportTargetMath.Resolve(new Vector2(100f, 100f), Tile);

            Assert.AreEqual(2, target.CellY);
            Assert.AreEqual(80f, target.CellY * Tile, 0.001f);
            Assert.AreEqual(81f, target.FeetPixelY, 0.001f,
                "feet belong one pixel above the destination cell's bottom seam");
        }

        /// <summary>
        /// The regression in one line: a cursor inside a room must land <i>in</i> the cell it is over,
        /// not a cell above it. The pre-fix expression put the feet 78 px higher, which is inside the
        /// ceiling of a two-tile-tall interior.
        /// </summary>
        [Test]
        public void Resolve_IndoorCursor_StaysInsideTheCursorsCell()
        {
            PlayerTeleportTargetMath.Target target =
                PlayerTeleportTargetMath.Resolve(new Vector2(210f, 100f), Tile);

            Assert.AreEqual(5, target.CellX);
            Assert.AreEqual(2, target.CellY);

            Assert.GreaterOrEqual(target.FeetPixelY, target.CellY * Tile,
                "the feet must not be below the destination cell");
            Assert.LessOrEqual(target.FeetPixelY, (target.CellY + 1) * Tile,
                "the feet must not be above the destination cell — that is the bug that put the " +
                "player in the ceiling");

            // The pre-fix expression, kept as the absent control. At this cursor it evaluates to
            // Mathf.Round(3.5) * 40 - 1 = 159 — the TOP of the cell, 78 px (2 * tile - 2) above the
            // correct 81, which is the ceiling of any two-tile interior.
            float preFixFeetY = Mathf.Round(100f / Tile + 1f) * Tile - 1f;
            Assert.AreEqual(159f, preFixFeetY, 0.001f, "control: this is what the old snap produced");
            Assert.AreEqual(78f, preFixFeetY - target.FeetPixelY, 0.001f);
        }

        /// <summary>
        /// AS3's <c>Math.round</c> is <c>floor(x + 0.5)</c>; <c>Mathf.RoundToInt</c> is banker's. A
        /// cursor on an exact half-pixel must round up, as the oracle does.
        /// </summary>
        [Test]
        public void Resolve_HalfPixelCursor_RoundsUp()
        {
            // 100 px = 2.5 tiles. AS3 rounds to 3, so the destination cell is 3 - 1 = 2.
            Assert.AreEqual(2, PlayerTeleportTargetMath.Resolve(new Vector2(0f, 100f), Tile).CellY);
            Assert.AreEqual(3, PlayerTeleportTargetMath.Resolve(new Vector2(100f, 0f), Tile).CellX);

            // Negative halves round up too (toward +inf), which banker's rounding does not.
            Assert.AreEqual(0, PlayerTeleportTargetMath.As3Round(-0.5f));
            Assert.AreEqual(-2, PlayerTeleportTargetMath.As3Round(-2.5f));
        }

        // ──────────────────────────────────────────────────────────────────────────────
        // The probe height (the second half of the "teleport does not work indoors" bug)
        // ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The <b>mechanism</b> of the second bug, proved with the real collision predicate: a
        /// destination with a full body height of headroom is clear, and the same destination tested
        /// with a doubled height is not.
        ///
        /// <para><c>TilePhysicsController.CanTeleportTo</c> built its rect as
        /// <c>(x - hw, y, 2hw, 2hh)</c> — twice the body height — so the probe reached the ceiling over
        /// any indoor landing spot and the teleport was refused. This test does not call the motor
        /// (that needs a GameObject and therefore the editor); it pins the geometry that the motor's
        /// one-line fix restores, using <see cref="TileCollisionMath.CheckCollision"/> itself, so a
        /// future "restore the 2f" would be caught by
        /// <c>GoldenTraceTests.X7_CanTeleportTo_ProbesTheBodyNotTwiceTheBody</c>.</para>
        /// </summary>
        [Test]
        public void TeleportProbe_OneBodyHeightClearsTheCeiling_TwoDoesNot()
        {
            // Four tiles tall: floor y 0-40, open y 40-120, ceiling y 120-160.
            string[] ascii = {
                "########",
                "........",
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Feet at y=40 (standing on the floor), body 50 tall: reaches 90, clears the ceiling at 120.
            var body = new Rect(60f - 15f, 40f, 30f, 50f);
            Assert.IsFalse(Collides(room, body),
                "one body height of headroom must be enough to teleport into");

            // The old rect: same feet, twice the height — 40..140, straight into the ceiling.
            var doubled = new Rect(60f - 15f, 40f, 30f, 100f);
            Assert.IsTrue(Collides(room, doubled),
                "the doubled probe hit the ceiling — this is the defect, reproduced");
        }

        private static bool Collides(RoomInstance room, Rect boundsPixels)
            => TileCollisionMath.CheckCollision(
                room, boundsPixels, 0f, 0f,
                platformThreshold: TileQueryConstants.PorogGrounded,
                isTransparent: false,
                canFallThroughPlatforms: true,
                velocityY: 0f);

        /// <summary>
        /// The <b>call site</b>: <c>CanTeleportTo</c> must pass the body height to its rect, not twice
        /// it. A source lint rather than a behavioural test because the motor is a <c>MonoBehaviour</c>
        /// — outside the editor <c>new GameObject()</c> throws <c>SecurityException: ECall methods must
        /// be packaged into a system module</c> before an assertion is reached, so
        /// <c>GoldenTraceTests.X7</c> can only run inside Unity.
        ///
        /// <para><c>SourceLint</c> strips comments first, which matters here: the method's own doc
        /// comment quotes <c>halfHeight * 2f</c> as the defect, and a lint over raw text would match
        /// that quotation and fail on the fixed file.</para>
        /// </summary>
        [Test]
        public void CanTeleportTo_ProbesTheBodyHeight_NotTwiceTheBodyHeight()
        {
            string source = SourceLint.ReadStripped("Systems/Physics/TilePhysicsController.cs");

            // Positive control: the method is still here under this name. Without it, both assertions
            // below would pass against a file that no longer contains the code they are about.
            Assert.That(source, Does.Contain("public bool CanTeleportTo("),
                "CanTeleportTo moved or was renamed; move this guard with it.");

            Assert.That(source, Does.Not.Contain("halfHeight * 2f"),
                "CanTeleportTo is doubling the probe height again: the box then reaches twice the body " +
                "height above the feet, which is the ceiling of any indoor destination.");

            Assert.That(source, Does.Contain("halfHeight);"),
                "the destination rect's height must be the body height itself.");
        }

        // ──────────────────────────────────────────────────────────────────────────────
        // The destination square
        // ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>GUI.as:1977</c> shows the clip only from the 15th frame of a 25-frame charge. The port
        /// thresholds on the same <i>share</i> of the charge rather than on frames, because its
        /// serialized charge duration is not the oracle's (0.42 s vs 0.833 s).
        /// </summary>
        [Test]
        public void ShouldShowMarker_StartsAtSixtyPercentOfTheCharge()
        {
            const float charge = 0.42f;

            Assert.IsFalse(PlayerTeleportTargetMath.ShouldShowMarker(0f, charge),
                "no charge, no square");
            Assert.IsFalse(PlayerTeleportTargetMath.ShouldShowMarker(charge * 0.59f, charge),
                "the oracle hides the square for the first 15 of 25 frames");
            Assert.IsTrue(PlayerTeleportTargetMath.ShouldShowMarker(charge * 0.6f, charge),
                "frame 15 of 25 is exactly where it appears");
            Assert.IsTrue(PlayerTeleportTargetMath.ShouldShowMarker(charge, charge));

            Assert.IsFalse(PlayerTeleportTargetMath.ShouldShowMarker(0.1f, 0f),
                "a zero-length charge must not divide by zero into 'visible'");
        }

        /// <summary>
        /// <c>GUI.as:1536-1551</c>: refused wins over charging, ready wins over charging.
        /// </summary>
        [Test]
        public void ResolveMarkerFrame_BlockedBeatsCharging()
        {
            Assert.AreEqual(TeleportFrameCharging,
                PlayerTeleportTargetMath.ResolveMarkerFrame(destinationAllowed: true, chargeComplete: false));
            Assert.AreEqual(TeleportFrameReady,
                PlayerTeleportTargetMath.ResolveMarkerFrame(destinationAllowed: true, chargeComplete: true));
            Assert.AreEqual(TeleportFrameBlocked,
                PlayerTeleportTargetMath.ResolveMarkerFrame(destinationAllowed: false, chargeComplete: false));
            Assert.AreEqual(TeleportFrameBlocked,
                PlayerTeleportTargetMath.ResolveMarkerFrame(destinationAllowed: false, chargeComplete: true));
        }

        /// <summary>The three <c>gotoAndStop</c> indices, spelled out so a renumbering cannot pass silently.</summary>
        private const int TeleportFrameCharging = 1;
        private const int TeleportFrameReady = 2;
        private const int TeleportFrameBlocked = 3;

        /// <summary>
        /// The marked cell must be the cell the feet land in, and the marker's rect must be exactly one
        /// tile — a 71 px square on a 40 px grid would spill into the cell below and misreport where the
        /// player lands.
        /// </summary>
        [Test]
        public void Resolve_MarkedCell_ContainsTheFeetAndIsOneTile()
        {
            PlayerTeleportTargetMath.Target target =
                PlayerTeleportTargetMath.Resolve(new Vector2(210f, 100f), Tile);
            Rect cell = target.CellRectPixels(Tile);

            Assert.AreEqual(Tile, cell.width, 0.001f);
            Assert.AreEqual(Tile, cell.height, 0.001f);
            Assert.AreEqual(5 * Tile, cell.x, 0.001f);
            Assert.AreEqual(2 * Tile, cell.y, 0.001f);

            Assert.IsTrue(cell.Contains(new Vector2(target.FeetPixelX, target.FeetPixelY)),
                "the feet must be inside the marked cell");

            Vector2 centre = target.CellCentrePixels(Tile);
            Assert.AreEqual(cell.x + Tile * 0.5f, centre.x, 0.001f);
            Assert.AreEqual(cell.y + Tile * 0.5f, centre.y, 0.001f);
        }
    }
}
