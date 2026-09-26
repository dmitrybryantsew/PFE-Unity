using NUnit.Framework;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Pins the AS3 prop-physics constants and the room heartbeat's step rate.
    ///
    /// <para><b>Why these exist.</b> <c>RoomObjectPhysicsLayer</c> had a gravity value 2× too strong
    /// (1800 vs AS3's 900 px/s²), a substep limit of 8 against AS3's 9, no terminal-velocity clamp at
    /// all, an invented air-drag force AS3 does not have, and — the real defect — it was stepped once
    /// per <i>rendered frame</i> with a hardcoded 1/60, so simulated time advanced by (fps / 60) per
    /// real second: correct only at exactly 60 fps, 2.4× fast at 144 fps and 2× <i>slow</i> at 30 fps.
    /// None of those numbers was asserted anywhere, which is exactly how they
    /// coexisted. Every assertion names the wrong value it guards against.</para>
    ///
    /// <para>AS3 authority: <c>fe/loc/Box.as</c> (props) and <c>fe/World.as</c> (constants).</para>
    /// </summary>
    [TestFixture]
    public class RoomObjectPhysicsLayerTests
    {
        private const float Tolerance = 1e-3f;

        /// <summary>One AS3 frame. The step that makes velocity and displacement match AS3.</summary>
        private static readonly float As3Frame = 1f / SimClock.CanonicalTicksPerSecond;

        // ── The constants, against their AS3 lines ───────────────────────────────────────────

        [Test]
        public void Gravity_IsWorldDdyScaledToPixelsPerSecond()
        {
            // World.ddy = 1 px/frame² (World.as:46), applied once per 30 Hz frame (Box.as:895).
            Assert.AreEqual(900f, RoomObjectPhysicsLayer.GravityPixelsPerSecond, Tolerance,
                "1 px/frame² × 30² = 900 px/s².");

            Assert.That(RoomObjectPhysicsLayer.GravityPixelsPerSecond, Is.Not.EqualTo(1800f).Within(Tolerance),
                "1800 was the shipped value: exactly 2× too strong, the same doubling as the divergent " +
                "ddy literals reconciled in the projectile census.");
        }

        [Test]
        public void TerminalFallSpeed_IsWorldMaxdyScaledToPixelsPerSecond()
        {
            // Box.as:893 gates the integrator on `dy < World.maxdy`; World.maxdy = 20 px/frame.
            Assert.AreEqual(600f, RoomObjectPhysicsLayer.MaxFallSpeedPixelsPerSecond, Tolerance,
                "20 px/frame × 30 = 600 px/s.");

            Assert.That(RoomObjectPhysicsLayer.MaxFallSpeedPixelsPerSecond,
                Is.Not.EqualTo(float.PositiveInfinity).Within(Tolerance),
                "The port had no clamp at all, so a long fall exceeded AS3's terminal velocity.");
        }

        [Test]
        public void GroundFriction_MatchesTheAs3LandingRule()
        {
            // Box.as:1078-1085: `dx *= 0.92`, and `dx = 0` when |dx| < 5 px/frame.
            Assert.AreEqual(0.92f, RoomObjectPhysicsLayer.GroundFriction, Tolerance,
                "AS3 damps horizontal speed by 8% per frame on landing.");
            Assert.AreEqual(5f, RoomObjectPhysicsLayer.GroundStopPixelsPerFrame, Tolerance);
            Assert.AreEqual(150f, RoomObjectPhysicsLayer.GroundStopPixelsPerSecond, Tolerance,
                "5 px/frame × 30 = 150 px/s.");

            Assert.That(RoomObjectPhysicsLayer.GroundFriction, Is.Not.EqualTo(0.5f).Within(Tolerance),
                "The old GroundDrag/AirDrag model damped far harder and had no AS3 counterpart.");
        }

        [Test]
        public void SubstepDistance_IsWorldMaxdelta()
        {
            // World.as:52 — maxdelta = 9. The port used 8.
            Assert.AreEqual(9f, RoomObjectPhysicsLayer.SubstepDistancePixels, Tolerance);

            Assert.That(RoomObjectPhysicsLayer.SubstepDistancePixels, Is.Not.EqualTo(8f).Within(Tolerance),
                "8 was the shipped value; AS3's World.maxdelta is 9.");
        }

        [Test]
        public void LegacyPerFrameDeltaTime_IsTheSixtiethStep_NotTheSimStep()
        {
            // The two drivers must not be confused: the legacy per-frame path keeps 1/60, while the
            // sim-driven heartbeat steps by SimClock.SimDt (1/30).
            Assert.AreEqual(1f / 60f, RoomObjectPhysicsLayer.LegacyPerFrameDeltaTime, 1e-6f);
            Assert.That(RoomObjectPhysicsLayer.LegacyPerFrameDeltaTime,
                Is.Not.EqualTo(As3Frame).Within(1e-6f),
                "If these ever coincide the rate fix has been silently reverted.");
        }

        // ── SubstepCount: Box.as:610-620 ─────────────────────────────────────────────────────

        [Test]
        public void SubstepCount_MatchesAs3FloorPlusOne()
        {
            // AS3: floor(max / maxdelta) + 1. Note it already yields 1 below the limit, which is why
            // AS3 needs no separate small-move branch.
            Assert.AreEqual(1, RoomObjectPhysicsLayer.SubstepCount(0f));
            Assert.AreEqual(1, RoomObjectPhysicsLayer.SubstepCount(8.9f));
            Assert.AreEqual(2, RoomObjectPhysicsLayer.SubstepCount(9f));
            Assert.AreEqual(2, RoomObjectPhysicsLayer.SubstepCount(17f));
            Assert.AreEqual(3, RoomObjectPhysicsLayer.SubstepCount(18f));
            Assert.AreEqual(4, RoomObjectPhysicsLayer.SubstepCount(27f));
        }

        [Test]
        public void SubstepCount_DiffersFromTheOldCeilOverEight_InTheBandWhereItMattered()
        {
            // The old form was ceil(max / 8) with a floor of 1. It agrees at small distances and
            // diverges in bands; 17 px is the smallest discriminating case (old 3, AS3 2). Asserting
            // the difference keeps the formula from being "simplified" back to the old one.
            Assert.AreEqual(2, RoomObjectPhysicsLayer.SubstepCount(17f),
                "ceil(17/8) = 3, so a regression to the old formula shows up here.");
            Assert.AreEqual(3, RoomObjectPhysicsLayer.SubstepCount(25f),
                "ceil(25/8) = 4, AS3 gives 3.");
        }

        // ── IntegrateFallSpeed: Box.as:893-899 ───────────────────────────────────────────────

        [Test]
        public void IntegrateFallSpeed_OneAs3Frame_IsOnePixelPerFrame()
        {
            // 900 px/s² × 1/30 s = 30 px/s, which is exactly 1 px/frame — the AS3 `dy += 1`.
            Assert.AreEqual(-30f, RoomObjectPhysicsLayer.IntegrateFallSpeed(0f, As3Frame), Tolerance);

            Assert.That(RoomObjectPhysicsLayer.IntegrateFallSpeed(0f, As3Frame),
                Is.Not.EqualTo(-60f).Within(Tolerance),
                "-60 is what the old 1800 px/s² produced: 2 px/frame.");
        }

        [Test]
        public void IntegrateFallSpeed_ClampsAtTerminalVelocity_AndStaysThere()
        {
            float vy = 0f;
            for (int i = 0; i < 40; i++)
            {
                vy = RoomObjectPhysicsLayer.IntegrateFallSpeed(vy, As3Frame);
            }

            Assert.AreEqual(-600f, vy, Tolerance, "20 px/frame is AS3's ceiling (World.maxdy).");

            // Already at the ceiling: must not accelerate further.
            Assert.AreEqual(-600f, RoomObjectPhysicsLayer.IntegrateFallSpeed(vy, As3Frame), Tolerance);
        }

        [Test]
        public void IntegrateFallSpeed_VelocityIsStepSizeIndependent()
        {
            // Same wall time, two step sizes: velocity must agree. (Displacement does NOT agree — this
            // is semi-implicit Euler, and matching AS3's displacement is the whole reason the sim
            // path uses 1/30 rather than 1/60.)
            float oneFrame = RoomObjectPhysicsLayer.IntegrateFallSpeed(0f, As3Frame);
            float twoHalves = RoomObjectPhysicsLayer.IntegrateFallSpeed(
                RoomObjectPhysicsLayer.IntegrateFallSpeed(0f, As3Frame * 0.5f), As3Frame * 0.5f);

            Assert.AreEqual(oneFrame, twoHalves, Tolerance);
        }

        // ── ApplyGroundFriction: Box.as:1078-1085 ────────────────────────────────────────────

        [Test]
        public void ApplyGroundFriction_StopsOutrightBelowTheThreshold()
        {
            Assert.AreEqual(0f, RoomObjectPhysicsLayer.ApplyGroundFriction(0f, As3Frame), Tolerance);
            Assert.AreEqual(0f, RoomObjectPhysicsLayer.ApplyGroundFriction(149f, As3Frame), Tolerance);

            // Exactly at the threshold is NOT below it, so it is damped rather than zeroed.
            Assert.AreEqual(150f * 0.92f, RoomObjectPhysicsLayer.ApplyGroundFriction(150f, As3Frame),
                Tolerance);
        }

        [Test]
        public void ApplyGroundFriction_DampsEightPercentPerAs3Frame()
        {
            Assert.AreEqual(184f, RoomObjectPhysicsLayer.ApplyGroundFriction(200f, As3Frame), Tolerance);
            Assert.AreEqual(-184f, RoomObjectPhysicsLayer.ApplyGroundFriction(-200f, As3Frame), Tolerance,
                "Friction is symmetric — it must not push a left-moving prop right.");
        }

        [Test]
        public void ApplyGroundFriction_IsRateIndependentOverOneAs3Frame()
        {
            float oneFrame = RoomObjectPhysicsLayer.ApplyGroundFriction(200f, As3Frame);
            float twoHalves = RoomObjectPhysicsLayer.ApplyGroundFriction(
                RoomObjectPhysicsLayer.ApplyGroundFriction(200f, As3Frame * 0.5f), As3Frame * 0.5f);

            Assert.AreEqual(oneFrame, twoHalves, Tolerance,
                "The rate is expressed as Pow(0.92, dt × 30) precisely so both drivers decay alike.");
        }
    }

    /// <summary>
    /// The room heartbeat's rate, asserted through the real <see cref="RoomInstance"/> the layer is
    /// stepped from — the pure tests above cannot catch the layer being handed the wrong delta.
    /// </summary>
    [TestFixture]
    public class RoomPropStepRateTests
    {
        private const float Tolerance = 1e-3f;
        private static readonly float As3Frame = 1f / SimClock.CanonicalTicksPerSecond;

        /// <summary>
        /// A room with one dynamic prop in mid-air. Mirrors the setup in <c>RoomInstanceTests</c>; kept
        /// local so this fixture reads on its own.
        /// </summary>
        private static RoomInstance CreateRoomWithProp(out ObjectInstance prop, Vector2 position)
        {
            RoomInstance room = new RoomInstance
            {
                id = "test_room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                landPosition = new Vector3Int(0, 0, 0)
            };
            room.InitializeTiles();

            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "woodbox";
            definition.size = 1;
            definition.width = 1;
            definition.physicalCapability = MapObjectPhysicalCapability.DynamicTelekinetic;

            prop = new ObjectInstance
            {
                objectId = "woodbox",
                objectType = "box",
                definition = definition,
                definitionId = "woodbox",
                position = position,
                runtimeState = new MapObjectRuntimeStateData()
            };
            prop.runtimeState.dynamicState.velocity = Vector2.zero;
            room.objects.Add(prop);
            room.Activate();
            return room;
        }

        [Test]
        public void Update_ExplicitAs3Step_AdvancesExactlyOneAs3Frame()
        {
            RoomInstance room = CreateRoomWithProp(out ObjectInstance prop, new Vector2(120f, 120f));

            room.Update(As3Frame);

            Assert.AreEqual(-30f, prop.runtimeState.dynamicState.velocity.y, Tolerance,
                "One AS3 frame of gravity is 1 px/frame, i.e. 30 px/s.");
            Assert.AreEqual(119f, prop.position.y, Tolerance,
                "Semi-implicit Euler moves 1 px in the first frame, matching AS3 exactly.");
        }

        [Test]
        public void Update_DefaultStep_IsStillTheLegacySixtieth()
        {
            RoomInstance room = CreateRoomWithProp(out ObjectInstance prop, new Vector2(120f, 120f));

            room.Update();

            Assert.AreEqual(-15f, prop.runtimeState.dynamicState.velocity.y, Tolerance,
                "The no-argument overload keeps the historical 1/60 step so the per-frame driver's " +
                "behaviour is unchanged; only the sim-driven path passes 1/30.");
        }

        [Test]
        public void Update_VelocityAfterOneAs3Frame_IsStepSizeIndependent()
        {
            RoomInstance oneFrame = CreateRoomWithProp(out ObjectInstance a, new Vector2(120f, 120f));
            RoomInstance twoHalves = CreateRoomWithProp(out ObjectInstance b, new Vector2(120f, 120f));

            oneFrame.Update(As3Frame);
            twoHalves.Update(As3Frame * 0.5f);
            twoHalves.Update(As3Frame * 0.5f);

            Assert.AreEqual(a.runtimeState.dynamicState.velocity.y,
                b.runtimeState.dynamicState.velocity.y, Tolerance,
                "Same wall time must give the same velocity whichever driver ran — this is the " +
                "property the per-frame 1/60 step violated.");
        }

        [Test]
        public void Update_GroundedProp_AppliesAs3GroundFriction()
        {
            RoomInstance room = CreateRoomWithProp(out ObjectInstance prop, new Vector2(120f, 120f));

            // Force contact, then give it horizontal speed above the stop threshold.
            prop.runtimeState.dynamicState.isGrounded = true;
            prop.runtimeState.dynamicState.velocity = new Vector2(200f, 0f);

            room.Update(As3Frame);

            Assert.AreEqual(184f, prop.runtimeState.dynamicState.velocity.x, Tolerance,
                "200 px/s × 0.92 = 184 px/s after one AS3 frame in contact.");
        }
    }

    /// <summary>
    /// The heartbeat's position in the tick order. Cheap to assert and load-bearing: the room must be
    /// stepped before the player motor, or the motor collides against a room that has not moved yet.
    /// </summary>
    [TestFixture]
    public class RoomHeartbeatTickOrderTests
    {
        [Test]
        public void GameLoopManager_Heartbeat_RunsBeforeThePlayerMotor()
        {
            // Only TickOrder is touched, so the null dependencies are never dereferenced.
            var heartbeat = new GameLoopManager(null, null, null, null, null, null, null);

            Assert.AreEqual(SimTickOrder.RoomState, heartbeat.TickOrder);
            Assert.Less(heartbeat.TickOrder, SimTickOrder.PlayerMotor,
                "Room state must be established before anything moves inside it.");
        }
    }
}
