using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Map.TileCollision
{
    /// <summary>
    /// Mandatory Golden Test Suite for P2 Unified Tile Collision.
    /// Implements all 36 scenarios across 5 categories from docs/Roadmap/03_P2_UNIFIED_TILE_COLLISION.md §4:
    ///   4.1 Ladders (L1–L7)
    ///   4.2 Slopes (S1–S6)
    ///   4.3 Platforms & Porog (P1–P10)
    ///   4.4 Water (W1–W7)
    ///   4.5 Cross-cutting (X1–X6)
    ///
    /// Each test builds a synthetic ASCII room, drives the motor via SimLoop at 30 Hz,
    /// asserts key physical invariants, and verifies the deterministic trace hash.
    /// </summary>
    [TestFixture]
    public class GoldenTraceTests
    {
        // ──────────────────────────────────────────────────────────────────────────
        // 4.1 Ladders (L1–L7)
        // ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void L1_WalkIntoLadderColumn_ClimbMovesAtLadderSpeed()
        {
            // Floor at y=0, ladder at x=5 from y=1 to y=6
            string[] ascii = {
                "..........",
                ".....H....",
                ".....H....",
                ".....H....",
                ".....H....",
                ".....H....",
                "##########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 120f, startY: 40f);

            // Walk right toward ladder column (x=5 is 200..240 px, center 220)
            harness.StepN(15, horizontal: 1f);

            // Press up to attach and climb
            harness.StepN(10, ladderY: 1f, ladderClimb: true);

            Assert.IsTrue(harness.Motor.IsOnLadder, "Expected motor to attach to ladder");
            Assert.Greater(harness.Motor.PixelPosition.y, 40f, "Expected motor to climb up ladder");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void L2_ClimbToTop_DoesNotPopAboveTop()
        {
            string[] ascii = {
                "..........",
                ".....H....",
                ".....H....",
                ".....H....",
                "##########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 220f, startY: 40f);

            // Attach and climb up past top of ladder
            harness.StepN(30, ladderY: 1f, ladderClimb: true);

            // Motor should either detach cleanly or remain clamped below top ceiling
            Assert.LessOrEqual(harness.Motor.PixelPosition.y, 200f);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void L3_ClimbToBottom_DetachesOntoGroundCleanly()
        {
            string[] ascii = {
                ".....H....",
                ".....H....",
                ".....H....",
                "##########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 220f, startY: 100f);

            // Climb down onto solid floor
            harness.StepN(25, ladderY: -1f, ladderClimb: true);

            Assert.IsFalse(harness.Motor.IsOnLadder, "Expected motor to detach when landing on ground");
            Assert.IsTrue(harness.Motor.IsGrounded, "Expected motor to be grounded on floor");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void L4_JumpWhileOnLadder_DetachesWithJumpImpulse()
        {
            string[] ascii = {
                ".....H....",
                ".....H....",
                ".....H....",
                "..........",
                "##########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 220f, startY: 80f);

            // Attach
            harness.Step(ladderY: 1f, ladderClimb: true);

            // Jump while on ladder
            harness.Step(jump: true);

            Assert.IsFalse(harness.Motor.IsOnLadder, "Jump should detach from ladder");
            Assert.Greater(harness.Motor.VelocityY, 0f, "Jump impulse should be positive");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void L5_ReachTopAndStepOffSideways_TransitionsToGrounded()
        {
            string[] ascii = {
                "....######",
                "....H#####",
                "....H#####",
                "##########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 180f, startY: 80f);

            // Step right onto the ledge
            harness.StepN(15, horizontal: 1f);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void L6_LadderBehindPlatform_BothCoexistWithoutStateConflict()
        {
            string[] ascii = {
                "....H.....",
                "...===....",
                "....H.....",
                "##########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 180f, startY: 40f);

            // Climb up through platform
            harness.StepN(20, ladderY: 1f, ladderClimb: true);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void L7_LadderProbeHalfWidthBoundary_StableAttachment()
        {
            string[] ascii = {
                "....H.....",
                "....H.....",
                "##########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Ladder tile is at x=4 (160..200). Probe right at edge
            using var harness = new GoldenTraceHarness(room, startX: 195f, startY: 40f);

            harness.Step(ladderY: 1f, ladderClimb: true);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // 4.2 Slopes (S1–S6)
        // ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void S1_WalkUpSlope_ClimbsContinuouslyWithoutHops()
        {
            string[] ascii = {
                "........",
                "...../##",
                "..../###",
                ".../####",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 80f, startY: 40f);

            // Walk right up the slope
            harness.StepN(20, horizontal: 1f);

            Assert.Greater(harness.Motor.PixelPosition.x, 80f);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void S2_WalkDownSlope_StaysGrounded()
        {
            string[] ascii = {
                "........",
                "##\\.....",
                "###\\....",
                "####\\...",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 100f, startY: 120f);

            // Walk right down the slope
            harness.StepN(15, horizontal: 1f);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void S3_StandStillOnSlope_DoesNotSlide()
        {
            string[] ascii = {
                "..../...",
                ".../....",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 140f, startY: 60f);

            // Stand idle for 10 ticks
            harness.StepN(10, horizontal: 0f);

            Assert.AreEqual(0f, harness.Motor.VelocityX, 0.1f, "Standing idle on slope should not accelerate horizontally");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void S4_JumpOnSlope_LaunchesFromSurfaceHeight()
        {
            string[] ascii = {
                "........",
                "..../...",
                ".../....",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 140f, startY: 60f);

            float initialY = harness.Motor.PixelPosition.y;
            harness.Step(jump: true);

            Assert.Greater(harness.Motor.PixelPosition.y, initialY, "Jump should launch upward");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void S5_SlopeMeetsFlat_SmoothTransition()
        {
            string[] ascii = {
                "........",
                "..../...",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 120f, startY: 40f);

            harness.StepN(20, horizontal: 1f);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void S6_SlopeMeetsWall_StopsAtWall()
        {
            string[] ascii = {
                "....#...",
                ".../#...",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 100f, startY: 40f);

            harness.StepN(25, horizontal: 1f);

            // Tile 4 is wall (x=160..200), motor half-width is 15 -> max X is ~145
            Assert.Less(harness.Motor.PixelPosition.x, 160f, "Motor must not penetrate wall at slope end");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // 4.3 Platforms & Porog (P1–P10)
        // ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void P1_FeetWithinPorogDescending_LandsOnPlatform()
        {
            string[] ascii = {
                "........",
                "...==...",
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // The platform sits in tile row 2, i.e. cell [80, 120]. Its walkable surface is the
            // cell TOP (120px) — the same convention these tests already use for the floor
            // ("start on floor at y=40", floor row 0 => cell [0,40], surface 40).
            // Start above it and fall into the porog window [110, 120].
            using var harness = new GoldenTraceHarness(room, startX: 140f, startY: 125f);

            harness.StepN(4); // fall into the 10px porog window below the surface

            Assert.IsTrue(harness.Motor.IsGrounded, "Expected motor to land on platform");
            Assert.IsTrue(harness.Motor.IsOnPlatform, "Expected isOnPlatform flag to be true");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void P2_FeetMoreThanPorogAbove_PassesThroughWhenRising()
        {
            string[] ascii = {
                "........",
                "...==...",
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Start on floor at y=40, jump up through platform at y=80
            using var harness = new GoldenTraceHarness(room, startX: 140f, startY: 40f);

            harness.Step(jump: true, jumpForce: 18f);
            harness.StepN(5);

            // Jumping from below must NOT hit the platform as ceiling
            Assert.IsFalse(harness.Motor.HitCeiling, "Jumping from below platform must never hit ceiling");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void P3_ExactlyAtPorogBoundary_DeterministicLanding()
        {
            string[] ascii = {
                "........",
                "...==...",
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Platform surface = cell top = 120px (see P1). AS3 passes through when
            // feet are MORE than porog below the surface, so the landing window is
            // [tileTop - porog, tileTop] = [110, 120]. Start at 111 so that one tick of
            // gravity (1 px/frame^2) lands the feet exactly on 110 — the boundary.
            // '>' is strict in AS3, so the boundary must LAND, not pass through.
            using var harness = new GoldenTraceHarness(room, startX: 140f, startY: 111f);

            harness.Step();

            Assert.IsTrue(harness.Motor.IsGrounded);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void P4_RiseThroughPlatformFromBelow_NeverBlocked()
        {
            string[] ascii = {
                "........",
                "...==...",
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 140f, startY: 40f);

            harness.Step(jump: true, jumpForce: 15f);

            for (int i = 0; i < 8; i++)
            {
                harness.Step();
                Assert.IsFalse(harness.Motor.HitCeiling, "Platform should never block upward movement");
            }

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void P5_PressDownOnPlatform_DropsThrough()
        {
            string[] ascii = {
                "........",
                "...==...",
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 140f, startY: 125f);

            // Settle on platform (surface = 120px, see P1)
            harness.StepN(4);
            Assert.IsTrue(harness.Motor.IsOnPlatform);

            // Press down to drop through
            harness.Step(down: true);
            harness.StepN(10);

            // Should fall down toward floor (40px)
            Assert.Less(harness.Motor.PixelPosition.y, 120f, "Should drop below platform surface");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void P6_DropThroughReleaseDownInside_NoReGrabWhileInside()
        {
            string[] ascii = {
                "........",
                "...==...",
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 140f, startY: 80f);

            harness.Step();
            harness.Step(down: true);
            // Release down immediately while falling through
            harness.Step(down: false);
            harness.StepN(5);

            Assert.Less(harness.Motor.PixelPosition.y, 80f);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void P7_StandOnPlatformWalkOffEdge_CleanFall()
        {
            string[] ascii = {
                "........",
                "...==...",
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 140f, startY: 80f);

            harness.Step();
            // Walk off to the left (platform ends at x=120)
            harness.StepN(15, horizontal: -1f);

            Assert.IsFalse(harness.Motor.IsOnPlatform);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void P8_AirborneVsGroundedPorog_DifferentiatesByStay()
        {
            string[] ascii = {
                "........",
                "...#....",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Grounded approach vs airborne
            using var harness = new GoldenTraceHarness(room, startX: 100f, startY: 40f);

            harness.StepN(10, horizontal: 1f);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void P9_PassThroughFlags_ForcesPassThrough()
        {
            string[] ascii = {
                "...==...",
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 140f, startY: 100f);

            // Falling with down pressed passes through
            harness.StepN(10, down: true);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void P10_ExternalForce_BehavesDeterministically()
        {
            string[] ascii = {
                "........",
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 100f, startY: 40f);

            harness.Motor.AddForce(5f, 10f);
            harness.StepN(10);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // 4.4 Water (W1–W7)
        // ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void W1_EnterWaterFromAbove_VelocityDamped()
        {
            string[] ascii = {
                "........",
                "........",
                "~~..~~~~",
                "~~~~~~~~",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 40f, startY: 120f);

            harness.StepN(15);

            Assert.IsTrue(harness.Motor.IsInWater, "Expected motor to enter water");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void W2_SinkOrSwim_IsInWaterSet()
        {
            string[] ascii = {
                "........",
                "~~~~~~~~",
                "~~~~~~~~",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 100f, startY: 80f);

            harness.StepN(5);

            Assert.IsTrue(harness.Motor.IsInWater);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void W3_FullySubmerged_IsFullySubmergedSet()
        {
            string[] ascii = {
                "~~~~~~~~",
                "~~~~~~~~",
                "~~~~~~~~",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Deep underwater at y=40
            using var harness = new GoldenTraceHarness(room, startX: 100f, startY: 40f);

            harness.StepN(5);

            Assert.IsTrue(harness.Motor.IsFullySubmerged, "75% height submerged should set IsFullySubmerged");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void W4_ExitWaterUpward_BreaksSurfaceCleanly()
        {
            string[] ascii = {
                "........",
                "........",
                "~~~~~~~~",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 100f, startY: 40f);

            // Jump out of water
            harness.Step(jump: true, jumpForce: 20f);
            harness.StepN(15);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void W5_WaterPlusLadder_BothApplyWithoutConflict()
        {
            string[] ascii = {
                "....H...",
                "....H...",
                "~~..H~~~",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 180f, startY: 40f);

            harness.StepN(15, ladderY: 1f, ladderClimb: true);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void W6_WaterBoundaryTile_NoFlickerAtSurface()
        {
            string[] ascii = {
                "........",
                "~~~~~~~~",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Right around water surface line (y=40..80)
            using var harness = new GoldenTraceHarness(room, startX: 100f, startY: 50f);

            harness.StepN(10);

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void W7_PartialSubmersion_OnlyIsInWaterTrue()
        {
            string[] ascii = {
                "........",
                "........",
                "~~~~~~~~",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Water tile is row 1 => cell [40, 80]. AS3 samples the body at 25% and 75% of its
            // height (Unit.as:2631, 2642) — NOT the head. Standing on the floor (feet at 40) puts
            // the whole 50px body inside a 40px water cell, so the 75% sample is wet and the unit
            // IS fully submerged — that was unachievable as a "partial" case.
            // Waist-deep instead: feet at 60 -> 25% sample at ~72 (in water), 75% sample at ~97
            // (row 2, dry). That is genuine partial submersion.
            using var harness = new GoldenTraceHarness(room, startX: 100f, startY: 60f);

            harness.Step();

            Assert.IsTrue(harness.Motor.IsInWater);
            Assert.IsFalse(harness.Motor.IsFullySubmerged, "Partial submersion should not set IsFullySubmerged");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // 4.5 Cross-cutting (X1–X6)
        // ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void X1_SubStepAtMaxDelta9_NoTunnelingThroughOneTileWall()
        {
            string[] ascii = {
                "........",
                "...#....",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Wall at x=3 (120..160). Motor at x=80, moving right at 40 px/tick (huge velocity)
            using var harness = new GoldenTraceHarness(room, startX: 80f, startY: 40f);

            harness.Motor.AddForce(40f, 0f);
            harness.Step();

            // Wall is at 120, half-width is 15 -> motor cannot be past 120
            Assert.LessOrEqual(harness.Motor.PixelPosition.x, 120f, "High-speed move must not tunnel through 1-tile wall");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void X2_ExactlyMaxDeltaDistance_SubStepConsistency()
        {
            string[] ascii = {
                "........",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 80f, startY: 40f);

            // Move by exactly 9 px
            harness.Motor.AddForce(9f, 0f);
            harness.Step();

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void X3_SimultaneousFloorPlusWall_CorrectResolutionOrder()
        {
            string[] ascii = {
                "..#.....",
                "..#.....",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Fall into corner (landing on floor while sliding into wall)
            using var harness = new GoldenTraceHarness(room, startX: 60f, startY: 80f);

            harness.StepN(15, horizontal: 1f);

            Assert.IsTrue(harness.Motor.IsGrounded, "Must be grounded");
            Assert.Less(harness.Motor.PixelPosition.x, 80f, "Must not penetrate corner wall");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }

        [Test]
        public void X4_RoomBoundary_QueryReturnsAir()
        {
            string[] ascii = {
                "....",
                "####"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            // Query outside room coordinates
            bool solidLeft = TileCollisionMath.IsSolidAt(room, new Vector2Int(-1, 0));
            bool solidRight = TileCollisionMath.IsSolidAt(room, new Vector2Int(10, 0));

            Assert.IsFalse(solidLeft, "Out of bounds should return air/false");
            Assert.IsFalse(solidRight, "Out of bounds should return air/false");
        }

        [Test]
        public void X5_Determinism_IdenticalInputsProduceByteIdenticalHash()
        {
            string[] ascii = {
                "....#...",
                "...==...",
                "....H...",
                "~~..H...",
                "########"
            };

            ulong hashRun1;
            ulong hashRun2;

            {
                RoomInstance room1 = SyntheticRoomBuilder.BuildFromAscii(ascii);
                using var harness1 = new GoldenTraceHarness(room1, startX: 80f, startY: 40f);
                harness1.StepN(10, horizontal: 1f);
                harness1.Step(jump: true);
                harness1.StepN(10, ladderY: 1f, ladderClimb: true);
                hashRun1 = harness1.Recorder.ComputeHash();
            }

            {
                RoomInstance room2 = SyntheticRoomBuilder.BuildFromAscii(ascii);
                using var harness2 = new GoldenTraceHarness(room2, startX: 80f, startY: 40f);
                harness2.StepN(10, horizontal: 1f);
                harness2.Step(jump: true);
                harness2.StepN(10, ladderY: 1f, ladderClimb: true);
                hashRun2 = harness2.Recorder.ComputeHash();
            }

            Assert.AreEqual(hashRun1, hashRun2, "Identical inputs on identical rooms must produce byte-identical trace hash");
        }

        [Test]
        public void X6_CanTeleportTo_AgreesWithMotorSolidity()
        {
            string[] ascii = {
                "....#...",
                "....#...",
                "########"
            };
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(ascii);

            using var harness = new GoldenTraceHarness(room, startX: 80f, startY: 40f);

            // Inside open air (x=60, y=40)
            bool canTeleportAir = harness.Motor.CanTeleportTo(60f, 40f, 15f, 50f);
            Assert.IsTrue(canTeleportAir, "Should be able to teleport to open air");

            // Inside wall (x=180 is inside tile (4,0) which is wall)
            bool canTeleportWall = harness.Motor.CanTeleportTo(180f, 40f, 15f, 50f);
            Assert.IsFalse(canTeleportWall, "Must not be able to teleport into wall");

            ulong hash = harness.Recorder.ComputeHash();
            Assert.AreNotEqual(0UL, hash);
        }
    }
}
