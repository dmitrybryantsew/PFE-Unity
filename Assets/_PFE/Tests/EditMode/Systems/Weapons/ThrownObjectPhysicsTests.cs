using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="ThrownObjectPhysics"/> against AS3 <c>PhisBullet.step()</c> /
    /// <c>PhisBullet.run()</c> (<c>weapon/PhisBullet.as:46-347</c>).
    ///
    /// <para>Every expectation here was worked out from the AS3 source, line by line, and several of
    /// them exist because the obvious implementation is wrong in a way that is invisible in play:</para>
    ///
    /// <list type="bullet">
    /// <item><description><c>SubStepCount</c> is <c>floor(d/9) + 1</c>, not <c>ceil(d/9)</c> — the two
    /// differ at exact multiples of 9.</description></item>
    /// <item><description>Resting friction tests <c>|dx| &gt; 1</c> and subtracts the full <c>brake</c>
    /// (2), so a resting <c>dx</c> of 1.5 <i>overshoots to −0.5</i> before the next frame snaps it to
    /// zero.</description></item>
    /// <item><description>A point exactly on a cell's top edge belongs to that cell, because AS3's
    /// <c>getAbsTile</c> floors a <b>Y-down</b> coordinate. Get that wrong and a resting object creeps
    /// downward one pixel per frame forever instead of re-detecting its floor.</description></item>
    /// <item><description>A ceiling bounce leaves <c>dy</c> positive, so AS3's following
    /// <c>if(dy &gt; 0)</c> floor branch runs <i>in the same sub-step</i>.</description></item>
    /// </list>
    ///
    /// <para>The probe is a plain grid, so the whole flight model is exercised without a
    /// <c>RoomInstance</c>, a <c>MonoBehaviour</c> or a running engine.</para>
    /// </summary>
    [TestFixture]
    public class ThrownObjectPhysicsTests
    {
        // AS3 values, in AS3's own units. Thrown objects get their parameters from WThrow, which
        // OVERRIDES PhisBullet's class defaults — see ProjectilePhysicsMath's citations.
        private const float Skok   = ProjectilePhysicsMath.ThrowBounceRetention;   // 0.4, WThrow.as:22
        private const float Tormoz = ProjectilePhysicsMath.ThrowFloorDamping;      // 0.6, WThrow.as:24
        private const float Brake  = ProjectilePhysicsMath.BrakePxPerFrame2;       // 2,   WThrow.as:20

        private const float TilePx = TileQueryConstants.TileSize;                  // 40

        /// <summary>Room size used by every scenario: 4×4 tiles, 160×160 px, origin at (0,0).</summary>
        private const int GridW = 4;
        private const int GridH = 4;

        // ── Constants ────────────────────────────────────────────────────────────────────────

        [Test]
        public void Constants_AreAs3sThrownObjectValues()
        {
            Assert.AreEqual(9f, ThrownObjectPhysics.MaxDeltaPx, 1e-6f,
                "World.as:48 maxdelta = 9 — the sub-step limit IS AS3's CCD for a bullet.");

            Assert.AreEqual(1f, ThrownObjectPhysics.GravityPxPerFrame2, 1e-6f,
                "World.as:46 ddy = 1 px/frame². The port shipped 0.6 for years.");

            Assert.AreEqual(1f, ThrownObjectPhysics.RestThresholdPxPerFrame, 1e-6f,
                "PhisBullet.as:61/65 `if(dx > 1)`.");

            Assert.AreEqual(2f, ThrownObjectPhysics.SettleThresholdPxPerFrame, 1e-6f,
                "PhisBullet.as:330 `if(dy > 2)`.");

            Assert.AreEqual(3, ThrownObjectPhysics.ExplosionLeadInFrames,
                "PhisBullet.as:116 `if(liv == 3)` — the explosion fires 3 frames before liv runs out.");

            Assert.AreEqual(1f, ThrownObjectPhysics.FaceOffsetPx, 1e-6f,
                "PhisBullet.as:252 `X = phX2 + 1`.");

            // The two thresholds are different decisions and must not be collapsed into one.
            Assert.AreNotEqual(ThrownObjectPhysics.RestThresholdPxPerFrame,
                               ThrownObjectPhysics.SettleThresholdPxPerFrame);
        }

        // ── Sub-stepping ─────────────────────────────────────────────────────────────────────

        [Test]
        public void SubStepCount_UsesAs3sFloorPlusOne()
        {
            Assert.AreEqual(1, ThrownObjectPhysics.SubStepCount(0f, 0f));
            Assert.AreEqual(1, ThrownObjectPhysics.SubStepCount(8.9f, -8.9f));

            // At an exact multiple of maxdelta AS3 subdivides; `ceil` would not.
            Assert.AreEqual(2, ThrownObjectPhysics.SubStepCount(9f, 0f));
            Assert.AreEqual(3, ThrownObjectPhysics.SubStepCount(18f, 0f));
            Assert.AreEqual(4, ThrownObjectPhysics.SubStepCount(0f, -27f));
        }

        [Test]
        public void SubStepCount_IsNotCeil_AtExactMultiplesOfMaxDelta()
        {
            // The port's other sub-stepping sites use `max(1, ceil(d/9))`. Copying that idiom here
            // would be wrong at every exact multiple, so pin the difference rather than the value.
            for (int multiple = 1; multiple <= 4; multiple++)
            {
                float d = multiple * ThrownObjectPhysics.MaxDeltaPx;
                int as3 = multiple + 1;
                int ceil = Mathf.CeilToInt(d / ThrownObjectPhysics.MaxDeltaPx);

                Assert.AreEqual(as3, ThrownObjectPhysics.SubStepCount(d, 0f),
                    $"{d} px must subdivide into {as3} steps.");
                Assert.AreNotEqual(ceil, ThrownObjectPhysics.SubStepCount(d, 0f),
                    $"{d} px: ceil gives {ceil}, AS3 gives {as3}.");
            }
        }

        [Test]
        public void SubStepCount_NeverLetsASubStepExceedMaxDelta()
        {
            for (float d = 0f; d < 500f; d += 0.5f)
            {
                int steps = ThrownObjectPhysics.SubStepCount(d, 0f);
                Assert.LessOrEqual(d / steps, ThrownObjectPhysics.MaxDeltaPx + 1e-4f,
                    $"{d} px over {steps} steps exceeds maxdelta.");
            }
        }

        // ── Gravity and free flight ──────────────────────────────────────────────────────────

        [Test]
        public void Step_AppliesGravityOncePerFrame_InPixelsPerFrame()
        {
            GridProbe probe = new GridProbe();
            ThrownObjectState state = At(20f, 150f, 0f, 0f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, detonateOnContact: false);

            Assert.AreEqual(-1f, state.VelocityPxPerFrame.y, 1e-5f,
                "PhisBullet.as:57 `dy += ddy` — one frame of gravity is exactly 1 px/frame.");
            Assert.AreEqual(149f, state.PositionPx.y, 1e-5f);
            Assert.AreEqual(0, state.ContactCount);
            Assert.AreEqual(1, state.SubStepCount);
        }

        [Test]
        public void Step_ClearAir_ReportsNoContactAndAccumulatesFall()
        {
            GridProbe probe = new GridProbe();
            ThrownObjectState state = At(20f, 150f, 0f, 0f);

            for (int i = 0; i < 3; i++)
            {
                ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);
            }

            Assert.AreEqual(-3f, state.VelocityPxPerFrame.y, 1e-5f);
            Assert.AreEqual(150f - 1f - 2f - 3f, state.PositionPx.y, 1e-4f,
                "Y advances by the velocity AFTER gravity is applied.");
            Assert.AreEqual(0, state.ContactCount);
            Assert.IsFalse(state.Stay);
            Assert.IsFalse(state.Detonated);
            Assert.IsFalse(state.Removed);
        }

        // ── Floor ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Run_FloorHit_AboveSettleThreshold_BouncesAndDampsHorizontal()
        {
            // Floor at tile (1,0): port Y ∈ [0, 40], top edge at 40.
            GridProbe probe = new GridProbe();
            probe.SetSolid(1, 0);

            // Start above it, falling at 5 px/frame (6 after this frame's gravity), drifting right.
            ThrownObjectState state = At(60f, 45f, 5f, -5f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.AreEqual(65f, state.PositionPx.x, 1e-4f, "X moved before Y was touched.");
            Assert.AreEqual(41f, state.PositionPx.y, 1e-4f,
                "PhisBullet.as:325 `Y = phY1 - 1` — 1 px clear of the surface, in Y-up 1 px over the cell top.");
            Assert.AreEqual(2.4f, state.VelocityPxPerFrame.y, 1e-4f,
                "PhisBullet.as:332 `dy = -|dy * skok|` with skok = 0.4, reflected upward: 6 * 0.4.");
            Assert.AreEqual(3f, state.VelocityPxPerFrame.x, 1e-4f,
                "PhisBullet.as:333 `dx *= tormoz` with tormoz = 0.6: 5 * 0.6.");
            Assert.IsFalse(state.Stay, "A bounce is not a rest.");
            Assert.AreEqual(1, state.ContactCount);
        }

        [Test]
        public void Run_FloorHit_AtOrBelowSettleThreshold_Settles()
        {
            GridProbe probe = new GridProbe();
            probe.SetSolid(1, 0);

            // Falling at 1 px/frame → 2 after gravity, and AS3's test is `dy > 2`, strictly.
            ThrownObjectState state = At(60f, 41f, 0f, -1f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.AreEqual(41f, state.PositionPx.y, 1e-4f);
            Assert.AreEqual(0f, state.VelocityPxPerFrame.y, 1e-5f,
                "PhisBullet.as:341 `dy = 0` — at exactly the threshold it settles, it does not bounce.");
            Assert.IsTrue(state.Stay, "PhisBullet.as:342 `stay = true`.");
            Assert.AreEqual(1, state.ContactCount);
        }

        [Test]
        public void Run_RestingObject_ReDetectsTheFloor_AndDoesNotCreep()
        {
            // This is the test that pins the cell lookup. AS3's resting object sits at `phY1 - 1`,
            // applies gravity, steps onto `phY1` EXACTLY, and re-detects the floor there — because
            // `getAbsTile` floors a Y-down coordinate, so a point on a cell's top edge belongs to
            // that cell. A Y-up `floor` puts it in the cell above instead, the floor is never found,
            // and the object sinks 1 px per frame forever.
            GridProbe probe = new GridProbe();
            probe.SetSolid(1, 0);

            ThrownObjectState state = At(60f, 41f, 0f, 0f);
            state.Stay = true;

            for (int frame = 0; frame < 5; frame++)
            {
                ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

                Assert.AreEqual(41f, state.PositionPx.y, 1e-4f,
                    $"Frame {frame}: the resting object must be re-snapped to the surface, not sink.");
                Assert.IsTrue(state.Stay, $"Frame {frame}: the floor must be re-detected every frame.");
                Assert.AreEqual(0f, state.VelocityPxPerFrame.y, 1e-5f);
            }

            Assert.AreEqual(5, state.ContactCount, "One contact per frame: the floor is re-found each time.");
        }

        [Test]
        public void Run_RestingFriction_OvershootsThenSnapsToZero()
        {
            GridProbe probe = new GridProbe();
            probe.SetSolid(1, 0);

            ThrownObjectState state = At(60f, 41f, 1.5f, 0f);
            state.Stay = true;

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            // PhisBullet.as:61-72: `if(dx > 1) dx -= brake`, with brake = 2. 1.5 − 2 = −0.5.
            // The port's old shape (`|dx| > brake * dt`) would have left dx at 1.5 — a creeping rest.
            Assert.AreEqual(-0.5f, state.VelocityPxPerFrame.x, 1e-4f,
                "The AS3 brake overshoots through zero; that is the shape, not a rounding artefact.");
            Assert.Less(state.VelocityPxPerFrame.x, 0f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            // PhisBullet.as:69-72: −0.5 is neither > 1 nor < −1, so it snaps to 0.
            Assert.AreEqual(0f, state.VelocityPxPerFrame.x, 1e-5f,
                "The overshoot lasts exactly one frame.");
        }

        // ── Ceiling ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void Run_CeilingHit_ReflectsDownward()
        {
            // Ceiling at tile (1,2): port Y ∈ [80, 120], bottom edge at 80.
            GridProbe probe = new GridProbe();
            probe.SetSolid(1, 2);

            ThrownObjectState state = At(60f, 79f, 0f, 5f);   // rising at 5 → 4 after gravity

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.AreEqual(-1.6f, state.VelocityPxPerFrame.y, 1e-4f,
                "PhisBullet.as:297 `dy = |dy * skok|` is downward in Y-down; here it is negative: 4 * 0.4.");
            Assert.AreEqual(1, state.ContactCount);
        }

        [Test]
        public void Run_CeilingBounce_ReEntersTheFloorBranch_InTheSameSubStep()
        {
            // AS3 writes the vertical branches as two separate `if`s, and a ceiling bounce leaves
            // `dy` POSITIVE (downward in Y-down) — so the very next `if(dy > 0)` runs, advances Y by
            // the new velocity and re-tests for a floor. It is easy to "tidy" these into if/else and
            // silently lose the behaviour.
            GridProbe probe = new GridProbe();
            probe.SetSolid(1, 2);

            ThrownObjectState state = At(60f, 79f, 0f, 5f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.AreEqual(-1.6f, state.VelocityPxPerFrame.y, 1e-4f);
            Assert.AreEqual(79f - 1.6f, state.PositionPx.y, 1e-4f,
                "After the ceiling snap to 79, the floor branch moved Y down by the NEW velocity.");
            Assert.IsFalse(state.Stay, "The floor branch clears `stay` on entry.");
            Assert.AreEqual(1, state.ContactCount, "The second pass found no floor — the point is in air.");
        }

        // ── Walls ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Run_WallHitMovingRight_SnapsLeftOfTheCell()
        {
            // Wall at tile (2,1): port X ∈ [80, 120], left edge at 80.
            GridProbe probe = new GridProbe();
            probe.SetSolid(2, 1);

            ThrownObjectState state = At(75f, 60f, 5f, 0f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.AreEqual(79f, state.PositionPx.x, 1e-4f,
                "PhisBullet.as:273 `X = phX1 - 1` — 1 px clear of the wall's near face.");
            Assert.AreEqual(-2f, state.VelocityPxPerFrame.x, 1e-4f,
                "PhisBullet.as:274 `dx = -|dx * skok|`: 5 * 0.4, reversed.");
            Assert.AreEqual(1, state.ContactCount);
        }

        [Test]
        public void Run_WallHitMovingLeft_SnapsRightOfTheCell()
        {
            // Wall at tile (0,1): port X ∈ [0, 40], right edge at 40.
            GridProbe probe = new GridProbe();
            probe.SetSolid(0, 1);

            ThrownObjectState state = At(44f, 60f, -5f, 0f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.AreEqual(41f, state.PositionPx.x, 1e-4f,
                "PhisBullet.as:252 `X = phX2 + 1`.");
            Assert.AreEqual(2f, state.VelocityPxPerFrame.x, 1e-4f,
                "PhisBullet.as:253 `dx = |dx * skok|`: 5 * 0.4, reversed.");
            Assert.AreEqual(1, state.ContactCount);
        }

        // ── Branch ordering ──────────────────────────────────────────────────────────────────

        [Test]
        public void Run_ResolvesXBeforeY_AndQueriesThePreYPosition()
        {
            // The ordering is observable in the QUERIES, not just the outcome: AS3 moves X and tests
            // it while Y is still at its pre-move value, then moves Y and tests at the post-X X.
            // Asserting the outcome alone cannot distinguish the two orders, so assert the sequence.
            GridProbe probe = new GridProbe();
            probe.SetSolid(2, 1);

            ThrownObjectState state = At(75f, 60f, 5f, 0f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.AreEqual(2, probe.Queries.Count,
                "One query for the X branch, one for the Y branch — nothing else.");

            Assert.AreEqual(new Vector2(80f, 60f), probe.Queries[0],
                "PhisBullet.as:262 tests X at the ORIGINAL Y (60), before the Y move.");
            Assert.AreEqual(new Vector2(79f, 59f), probe.Queries[1],
                "PhisBullet.as:285/314 test Y at the POST-X X (79) and the moved Y (59).");
        }

        [Test]
        public void Run_CornerHit_ResolvesBothAxes()
        {
            // A wall ahead and a floor below: AS3 resolves the wall first, then the floor from the
            // wall-corrected X. Both contacts land, and the floor's damping applies to the
            // already-reflected dx.
            GridProbe probe = new GridProbe();
            probe.SetSolid(2, 1);
            probe.SetSolid(1, 0);

            ThrownObjectState state = At(75f, 42f, 5f, -3f);   // 5 right, 3 down → 4 down after gravity

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.AreEqual(79f, state.PositionPx.x, 1e-4f);
            Assert.AreEqual(41f, state.PositionPx.y, 1e-4f);
            Assert.AreEqual(1.6f, state.VelocityPxPerFrame.y, 1e-4f, "4 * 0.4, upward.");
            Assert.AreEqual(-1.2f, state.VelocityPxPerFrame.x, 1e-4f,
                "−2 from the wall bounce, then × 0.6 from the floor damping: the order shows here.");
            Assert.AreEqual(2, state.ContactCount);
        }

        // ── Room bounds ──────────────────────────────────────────────────────────────────────

        [Test]
        public void Run_LeavingTheRoomHorizontally_RemovesBeforeAnyTileTest()
        {
            GridProbe probe = new GridProbe();
            probe.SetSolid(3, 1);   // would be hit if the tile test ran first

            // 9 px/frame over 2 sub-steps = 4.5 px each; from 158 that clears the 160 px room edge.
            ThrownObjectState state = At(158f, 60f, 9f, 0f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.IsTrue(state.Removed, "PhisBullet.as:234-238 — outside `loc.limX` removes the object.");
            Assert.AreEqual(0, state.ContactCount);
            Assert.AreEqual(0, probe.Queries.Count,
                "The bounds test precedes the tile test, so no tile is ever queried.");
        }

        [Test]
        public void Run_FallingOutOfTheRoomBottom_RemovesTheObject()
        {
            GridProbe probe = new GridProbe();

            ThrownObjectState state = At(60f, 5f, 0f, -9f);   // 10 after gravity, 2 sub-steps of 5

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.IsTrue(state.Removed, "PhisBullet.as:309-313 — `Y >= loc.limY` removes the object.");
            Assert.AreEqual(0, state.ContactCount);
        }

        [Test]
        public void Run_RisingPastTheRoomTop_IsNotRemoved()
        {
            // AS3 has a room-bounds test on the falling branch ONLY. Rising out of the top of a room
            // is left alone — a real asymmetry in the original, and not one to "fix" here.
            GridProbe probe = new GridProbe();

            ThrownObjectState state = At(60f, 155f, 0f, 20f);   // 19 after gravity, 3 sub-steps

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, false);

            Assert.IsFalse(state.Removed);
            Assert.IsFalse(state.Detonated);
            Assert.AreEqual(0, state.ContactCount);
            Assert.AreEqual(174f, state.PositionPx.y, 1e-3f,
                "155 + 3 × (19/3) — the object simply leaves the top of the room.");
        }

        // ── Contact detonation (bumc) ────────────────────────────────────────────────────────

        [Test]
        public void Step_DetonateOnContact_ZerosVelocityAndSettles()
        {
            GridProbe probe = new GridProbe();
            probe.SetSolid(1, 0);

            ThrownObjectState state = At(60f, 45f, 5f, -5f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, detonateOnContact: true);

            Assert.IsTrue(state.Detonated, "PhisBullet.as:323 `popadalo()` when `bumc`.");
            Assert.AreEqual(Vector2.zero, state.VelocityPxPerFrame,
                "PhisBullet.as:193 `dx = dy = 0` — and that is why the bounce below cannot fire.");

            Assert.AreEqual(41f, state.PositionPx.y, 1e-4f, "The snap still happens after the zeroing.");
            Assert.IsTrue(state.Stay,
                "With dy zeroed, `dy > 2` is false, so the floor branch settles instead of bouncing.");
            Assert.AreEqual(1, state.ContactCount);
        }

        [Test]
        public void Step_DetonateOnContact_StopsTheSubStepLoop()
        {
            GridProbe probe = new GridProbe();
            probe.SetSolid(1, 0);

            // 20 px/frame → 21 after gravity → floor(21/9) + 1 = 3 sub-steps. AS3's loop breaks on
            // `babah`, so only the first one runs.
            ThrownObjectState state = At(60f, 45f, 0f, -20f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, detonateOnContact: true);

            Assert.AreEqual(3, state.SubStepCount, "The count was computed from the original deltas.");
            Assert.AreEqual(1, state.ContactCount, "Only the first sub-step ran.");
            Assert.IsTrue(state.Detonated);
        }

        [Test]
        public void Step_WithoutDetonateOnContact_KeepsFlying()
        {
            GridProbe probe = new GridProbe();
            probe.SetSolid(1, 0);

            ThrownObjectState state = At(60f, 45f, 0f, -20f);

            ThrownObjectPhysics.Step(ref state, probe, Skok, Tormoz, Brake, detonateOnContact: false);

            Assert.IsFalse(state.Detonated);
            Assert.IsFalse(state.Removed);
            Assert.GreaterOrEqual(state.ContactCount, 1);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        private static ThrownObjectState At(float xPx, float yPx, float dxPxPerFrame, float dyPxPerFrame)
        {
            return new ThrownObjectState
            {
                PositionPx         = new Vector2(xPx, yPx),
                VelocityPxPerFrame = new Vector2(dxPxPerFrame, dyPxPerFrame),
            };
        }

        /// <summary>
        /// A <see cref="IThrownTileProbe"/> over a <see cref="GridW"/>×<see cref="GridH"/> grid at the
        /// world origin, recording every solidity query so branch ordering can be asserted.
        ///
        /// <para><b>The Y lookup is <c>ceil(y / 40) - 1</c>, not <c>floor(y / 40)</c></b> — the exact
        /// Y-up mirror of AS3's <c>floor(as3Y / 40)</c>, since <c>as3Y = H - y</c> and the row index
        /// flips. <c>floor</c> here would put a point on a cell's top edge into the cell above it and
        /// break floor re-detection; <see cref="Run_RestingObject_ReDetectsTheFloor_AndDoesNotCreep"/>
        /// is what catches that.</para>
        /// </summary>
        private sealed class GridProbe : IThrownTileProbe
        {
            private readonly bool[,] _solid = new bool[GridW, GridH];

            public List<Vector2> Queries { get; } = new List<Vector2>();

            public Rect RoomBoundsPx { get; } =
                new Rect(0f, 0f, GridW * TilePx, GridH * TilePx);

            public void SetSolid(int tileX, int tileY)
            {
                Assert.IsTrue(tileX >= 0 && tileX < GridW && tileY >= 0 && tileY < GridH,
                    $"({tileX},{tileY}) is outside the {GridW}x{GridH} test room.");
                _solid[tileX, tileY] = true;
            }

            public bool IsSolidAt(Vector2 worldPx)
            {
                Queries.Add(worldPx);

                Vector2Int c = CellAt(worldPx);
                if (c.x < 0 || c.y < 0 || c.x >= GridW || c.y >= GridH) return false;

                return _solid[c.x, c.y];
            }

            public Rect CellBoundsAt(Vector2 worldPx)
            {
                Vector2Int c = CellAt(worldPx);
                return new Rect(c.x * TilePx, c.y * TilePx, TilePx, TilePx);
            }

            private static Vector2Int CellAt(Vector2 worldPx)
            {
                return new Vector2Int(
                    Mathf.FloorToInt(worldPx.x / TilePx),
                    Mathf.CeilToInt(worldPx.y / TilePx) - 1);
            }
        }
    }
}
