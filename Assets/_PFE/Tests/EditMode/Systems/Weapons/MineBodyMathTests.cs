using NUnit.Framework;
using UnityEngine;
using PFE.Entities.Units;
using PFE.Entities.Weapons;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="MineBodyMath"/> — the mine's body — against AS3 <c>Unit.forces()</c> and
    /// <c>Unit.run()</c>, the non-flying branch.
    ///
    /// <para><b>Why this fixture exists at all.</b> A mine is a <c>Unit</c> in the oracle
    /// (<c>WThrow.as:142-174</c> pushes it into <c>loc.units</c>), so it FALLS from the hand to the
    /// floor. The port modelled it as a fixed emplacement — a Kinematic body with
    /// <c>gravityScale = 0</c> and no vertical integration — so a placed mine stayed in the air at
    /// hand height forever. Nothing went red, because nothing asserted the fall.</para>
    ///
    /// <para><b>Three specific traps, each of which the obvious implementation gets wrong.</b></para>
    ///
    /// <list type="number">
    /// <item><description><b>The box hangs ABOVE the origin.</b> AS3 <c>Unit.setPos</c>
    /// (<c>Unit.as:1872-1880</c>) is <c>Y1 = Y - scY; Y2 = Y</c> in a Y-<i>down</i> space, so the
    /// origin is the box's <i>bottom centre</i>. A box centred on the origin — the 0.5-unit circle the
    /// prefab used to carry — rests the mine half its own height too high and the art, which is
    /// anchored at the origin, visibly floats. See
    /// <see cref="BoxRect_HangsAboveTheOrigin_NotCentredOnIt"/> and its negative control.</description></item>
    ///
    /// <item><description><b>The landing SNAPS to the tile edge.</b> AS3 does not leave the unit
    /// wherever the step happened to put it: <c>run()</c> assigns <c>Y = _loc5_</c> with
    /// <c>_loc5_ = _loc2_.phY1</c> (<c>Unit.as:2343-2356</c>). At the port's 50 Hz
    /// <c>fixedDeltaTime</c> a settled fall moves ~12 px per step — more than half the mine's own
    /// height — so without the snap the mine stops <i>inside</i> the floor and the sprite sinks with
    /// it. See <see cref="AtProductionStepSize_TheMineLandsOnTheTileEdge_AndDoesNotSinkIntoIt"/>.</description></item>
    ///
    /// <item><description><b><c>fixed</c> gates the MOVE, not the integration.</b> AS3 puts
    /// <c>forces()</c> <i>above</i> <c>if(!this.fixed) run()</c> (<c>Unit.as:1807-1810</c>), so a
    /// pinned mine keeps accumulating <c>dy</c> and simply never applies it. Gating the integration
    /// instead is the tempting "obviously equivalent" refactor and it changes the velocity a pinned
    /// mine reports. See <see cref="APinnedMine_KeepsIntegrating_ButNeverMoves"/>.</description></item>
    /// </list>
    ///
    /// <para><b>The room is synthetic and the query is the shipped one.</b> Everything below drives the
    /// real <see cref="UnifiedTileQueryService"/> over a real <c>RoomInstance</c> built from ASCII, so
    /// the pixel arithmetic under test is the arithmetic the game runs — no scene, no
    /// <c>MonoBehaviour</c>, no physics step. The one thing that is <i>not</i> exercised here is
    /// <c>MineObject</c>'s own wiring of this rule onto a transform; that is a
    /// <c>MonoBehaviour</c> and needs the editor.</para>
    /// </summary>
    [TestFixture]
    public class MineBodyMathTests
    {
        /// <summary><c>WorldConstants.TILE_SIZE</c>. The whole fixture is arithmetic in these units.</summary>
        private const float TileSize = 40f;

        /// <summary>
        /// A 16×5 room: solid border, three rows of air, solid floor at row 0. The floor's top edge is
        /// therefore at world y = <b>40</b>, which every landing assertion below uses as its oracle.
        /// </summary>
        private static readonly string[] RoomRows =
        {
            "################",
            "#..............#",
            "#..............#",
            "#..............#",
            "################",
        };

        /// <summary>World y of the synthetic room's floor — row 0's top edge, <c>(0 + 1) * 40</c>.</summary>
        private const float FloorTopPixels = TileSize;

        /// <summary>The column the mine falls down: clear of the walls, four tiles from the left.</summary>
        private const float FallColumnXPixels = 200f;

        /// <summary>
        /// A room whose origin is <c>(0, 0)</c> — <c>landPosition (0,0)</c> and <c>borderOffset 0</c> —
        /// so room-local and world pixels coincide and every expected value below can be written as a
        /// literal. <see cref="RoomOriginIsAtTheWorldOrigin"/> asserts that, because a room one land
        /// step away would displace every other assertion here by a room width.
        /// </summary>
        private static RoomInstance BuildRoom()
        {
            return SyntheticRoomBuilder.BuildFromAscii(RoomRows);
        }

        // ── Preconditions: the room and the query are what the assertions assume ─────────────

        [Test]
        public void RoomOriginIsAtTheWorldOrigin()
        {
            var query = new UnifiedTileQueryService(BuildRoom());

            Assert.AreEqual(Vector2.zero, query.OriginPixel,
                "every literal in this fixture is room-local; a non-zero origin would invalidate all of them");
        }

        /// <summary>
        /// The positive control for the whole fixture: the floor the mine is supposed to land on is
        /// really there, at y = 40, and the shipped query really reports it. Without this, a room
        /// builder that silently produced an empty grid would make every landing test below pass
        /// vacuously against "no ground".
        /// </summary>
        [Test]
        public void TheSyntheticFloorIsReal_AtTheHeightTheLandingTestsAssume()
        {
            var query = new UnifiedTileQueryService(BuildRoom());

            Assert.AreEqual(FloorTopPixels,
                query.GetGroundHeight(new Vector2(FallColumnXPixels, FloorTopPixels - 1f)),
                0.001f, "row 0's top edge is the floor the mine lands on");

            Assert.IsTrue(
                query.IsOnGround(MineBodyMath.GroundProbeRectPixels(
                    new Vector2(FallColumnXPixels, FloorTopPixels + 1f))),
                "a mine whose box bottom sits just above y=40 must read as grounded");

            Assert.IsFalse(
                query.IsOnGround(MineBodyMath.GroundProbeRectPixels(
                    new Vector2(FallColumnXPixels, FloorTopPixels + 20f))),
                "and one 20 px higher must NOT — otherwise the probe is answering 'yes' to everything");
        }

        // ── 1. The box: origin is the bottom centre, the box hangs above it ───────────────────

        /// <summary>
        /// AS3 <c>Unit.setPos</c>: <c>X1 = X - scX/2; X2 = X + scX/2; Y1 = Y - scY; Y2 = Y</c>
        /// (<c>Unit.as:1872-1880</c>). In Unity's up-positive space that is <c>yMin == origin.y</c>.
        /// </summary>
        [Test]
        public void BoxRect_HangsAboveTheOrigin_NotCentredOnIt()
        {
            var origin = new Vector2(200f, 140f);

            Rect box = MineBodyMath.BoxRectPixels(origin);

            Assert.AreEqual(200f, box.center.x, 0.001f, "X is the box's horizontal centre (X1..X2)");
            Assert.AreEqual(origin.y, box.yMin, 0.001f, "Y is the box's BOTTOM (Y2 = Y)");
            Assert.AreEqual(origin.y + MineBodyMath.BoxHeightPixels, box.yMax, 0.001f,
                "the box is entirely above the origin (Y1 = Y - scY)");
        }

        /// <summary>
        /// The negative control for the convention above. The prefab used to carry a 0.5-unit
        /// <c>CircleCollider2D</c>, i.e. a box <i>centred</i> on the origin, which is what makes the
        /// mine rest half its own height too high. Asserted so a future "simplification" back to a
        /// centred box fails here instead of showing up as art that floats.
        /// </summary>
        [Test]
        public void ABoxCentredOnTheOrigin_IsTheMistakeThisConventionAvoids()
        {
            var origin = new Vector2(200f, 140f);

            float centredYMin = origin.y - MineBodyMath.BoxHeightPixels * 0.5f;

            Assert.AreEqual(130f, centredYMin, 0.001f);
            Assert.AreNotEqual(MineBodyMath.BoxRectPixels(origin).yMin, centredYMin,
                "a centred box would put the mine's feet 10 px — half its height — above the origin");
        }

        /// <summary>
        /// <c>sX='30' sY='20'</c> on every mine weapon row (<c>AllData.as</c>). Pinned as values rather
        /// than as "whatever the constants happen to be", because the landing height and the tile
        /// overlap test both move with them.
        /// </summary>
        [Test]
        public void TheBoxIsTheAuthoredThirtyByTwenty()
        {
            Assert.AreEqual(30f, MineBodyMath.BoxWidthPixels, 0.001f, "AS3 sX on every mine row");
            Assert.AreEqual(20f, MineBodyMath.BoxHeightPixels, 0.001f, "AS3 sY on every mine row");
        }

        /// <summary>
        /// The probe rect's <c>yMin</c> is the surface, one <c>SeatPixels</c> below the box bottom —
        /// the same convention <c>UnitGroundProbe</c> owns for every other unit, so the mine asks the
        /// tile grid the identical question a walking unit asks.
        /// </summary>
        [Test]
        public void GroundProbeRect_IsLoweredByTheSeat()
        {
            var origin = new Vector2(200f, 140f);

            Rect probe = MineBodyMath.GroundProbeRectPixels(origin);

            Assert.AreEqual(140f - UnitGroundProbe.SeatPixels, probe.yMin, 0.001f);
            Assert.AreEqual(MineBodyMath.BoxWidthPixels, probe.width, 0.001f);
            Assert.AreEqual(MineBodyMath.BoxHeightPixels, probe.height, 0.001f);
        }

        /// <summary>
        /// <see cref="MineBodyMath.GroundProbePoint"/> must be the point <c>IsOnGround</c> actually
        /// samples — the box bottom, lowered by the seat, lowered again by the query's own 1 px probe
        /// (<c>TileCollisionMath.cs:541</c>). The landing snap asks about this point; if it drifted off
        /// the sampled one the two could disagree and seat the mine on the wrong surface.
        /// </summary>
        [Test]
        public void GroundProbePoint_IsThePointIsOnGroundSamples()
        {
            var origin = new Vector2(200f, 140f);

            Rect probe = MineBodyMath.GroundProbeRectPixels(origin);
            Vector2 point = MineBodyMath.GroundProbePoint(origin);

            Assert.AreEqual(probe.yMin - 1f, point.y, 0.001f,
                "IsOnGround samples boundsPx.yMin - 1f");
            Assert.AreEqual(probe.center.x, point.x, 0.001f);
            Assert.AreEqual(138f, point.y, 0.001f,
                "which for a box bottom at 140 is 140 - 1 seat - 1 probe");
        }

        // ── 2. forces(): the falling branch ───────────────────────────────────────────────────

        /// <summary>
        /// <c>dy += World.ddy * grav</c> (<c>Unit.as:1962-1964</c>), at <c>World.ddy = 1</c> px/frame²
        /// and <c>grav = 1</c>, converted to the seconds-based delta the port uses.
        /// </summary>
        [Test]
        public void InAir_TheMineAcceleratesDownwardAtTheOracleRate()
        {
            Vector2 v = MineBodyMath.StepVelocity(Vector2.zero, grounded: false, deltaTime: 0.02f);

            Assert.AreEqual(-UnitFallPhysics.GravityUnitsPerSecondSquared * 0.02f, v.y, 0.0001f,
                "one step of gravity");
            Assert.AreEqual(-9f * 0.02f, v.y, 0.0001f,
                "World.ddy = 1 px/frame^2 at 30 fps is 9 units/s^2");
            Assert.AreEqual(0f, v.x, 0.0001f, "no horizontal force is applied to a falling mine");
        }

        /// <summary>
        /// The terminal clamp: AS3's <c>dy &lt; World.maxdy * grav</c> gate means a long fall stops
        /// accelerating at 20 px/frame. Without it the port accelerated without bound.
        /// </summary>
        [Test]
        public void InAir_TheFallSpeedIsClampedAtTheOracleTerminal()
        {
            Vector2 v = Vector2.zero;

            for (int i = 0; i < 500; i++)
            {
                v = MineBodyMath.StepVelocity(v, grounded: false, deltaTime: 0.02f);
            }

            Assert.AreEqual(-UnitFallPhysics.TerminalFallSpeed, v.y, 0.0001f);
            Assert.AreEqual(-6f, v.y, 0.0001f, "World.maxdy = 20 px/frame is 6 units/s");
        }

        /// <summary>
        /// While grounded AS3 integrates nothing, so a downward <c>dy</c> is cancelled — that is what
        /// stops the mine sinking. The complement is in
        /// <see cref="WhileGrounded_AnUpwardVelocitySurvives"/>.
        /// </summary>
        [Test]
        public void WhileGrounded_AFallIsCancelled()
        {
            Vector2 v = MineBodyMath.StepVelocity(new Vector2(0f, -5f), grounded: true, deltaTime: 0.02f);

            Assert.AreEqual(0f, v.y, 0.0001f, "isLaz cancels the fall rather than accumulating it");
        }

        /// <summary>
        /// The negative control for the cancellation: it must be one-sided. A blast's knock-up (or a
        /// jump) starts on a grounded tick, so cancelling an <i>upward</i> <c>dy</c> too would swallow
        /// it and nothing would ever leave the floor.
        /// </summary>
        [Test]
        public void WhileGrounded_AnUpwardVelocitySurvives()
        {
            Vector2 v = MineBodyMath.StepVelocity(new Vector2(0f, 5f), grounded: true, deltaTime: 0.02f);

            Assert.AreEqual(5f, v.y, 0.0001f,
                "only a DOWNWARD dy is cancelled; cancelling both swallows a jump or a knock-up");
        }

        /// <summary>
        /// <c>brake</c> = 1 px/frame applied as a subtraction from <c>dx</c> (<c>Unit.as:1970-1990</c>)
        /// — a px/frame² acceleration, so 9 units/s², and it stops at zero rather than overshooting.
        /// </summary>
        [Test]
        public void WhileGrounded_TheMineBrakesToRest_WithoutOvershooting()
        {
            Vector2 moving = MineBodyMath.StepVelocity(new Vector2(5f, 0f), grounded: true, deltaTime: 0.02f);
            Assert.AreEqual(5f - 9f * 0.02f, moving.x, 0.0001f, "9 units/s^2 for one 20 ms step");

            Vector2 almostStopped = MineBodyMath.StepVelocity(new Vector2(0.01f, 0f), grounded: true, deltaTime: 0.02f);
            Assert.AreEqual(0f, almostStopped.x, 0.0001f,
                "a brake larger than the remaining speed must snap to zero, not reverse it");
        }

        // ── 3. The `fixed` gate ───────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>if(!this.fixed) run()</c> (<c>Unit.as:1809</c>) — the gate covers the position write
        /// and nothing else.
        /// </summary>
        [Test]
        public void AppliesMotion_IsTheFixedGate()
        {
            Assert.IsTrue(MineBodyMath.AppliesMotion(pinned: false), "an unpinned mine moves");
            Assert.IsFalse(MineBodyMath.AppliesMotion(pinned: true), "`fixed = true` blocks the write");
        }

        /// <summary>
        /// The trap: <c>forces()</c> sits <b>above</b> the gate, so a pinned mine keeps building
        /// <c>dy</c>. Asserted through <see cref="MineBodyMath.Step"/> because that is where the two
        /// could be conflated — an implementation that early-returned before integrating would report
        /// a velocity of zero here and look entirely reasonable.
        /// </summary>
        [Test]
        public void APinnedMine_KeepsIntegrating_ButNeverMoves()
        {
            var query = new UnifiedTileQueryService(BuildRoom());
            var origin = new Vector2(FallColumnXPixels, 140f);

            MineBodyStep step = MineBodyMath.Step(query, Vector2.zero, origin, pinned: true, deltaTime: 0.02f);

            Assert.AreEqual(-UnitFallPhysics.GravityUnitsPerSecondSquared * 0.02f, step.Velocity.y, 0.0001f,
                "forces() runs above the gate, so the pinned mine still accelerates");
            Assert.IsFalse(step.Moved, "but run() is never called, so it does not move");
            Assert.AreEqual(origin.x, step.OriginPixels.x, 0.0001f, "and its origin is untouched");
            Assert.AreEqual(origin.y, step.OriginPixels.y, 0.0001f);
        }

        // ── 4. run(): the landing snap, over the real query ───────────────────────────────────

        /// <summary>
        /// Steps a falling mine until the step that starts grounded, and reports where it ended.
        /// Returns <c>steps = -1</c> when it never landed, so a test can distinguish "landed at the
        /// wrong height" from "never landed at all".
        /// </summary>
        private static (Vector2 Origin, Vector2 Velocity, int Steps) FallUntilLanded(
            ITileQueryService query, Vector2 startOriginPixels, float deltaTime, int maxSteps)
        {
            Vector2 origin = startOriginPixels;
            Vector2 velocity = Vector2.zero;

            for (int i = 0; i < maxSteps; i++)
            {
                MineBodyStep step = MineBodyMath.Step(query, velocity, origin, pinned: false, deltaTime);

                velocity = step.Velocity;
                if (step.Moved) origin = step.OriginPixels;

                if (step.Grounded) return (origin, velocity, i + 1);
            }

            return (origin, velocity, -1);
        }

        /// <summary>
        /// The core claim: a mine dropped from mid-air comes to rest with its box bottom <b>exactly</b>
        /// on the floor's top edge, because AS3's <c>run()</c> assigns <c>Y = phY1</c>
        /// (<c>Unit.as:2343-2356</c>).
        /// </summary>
        [Test]
        public void AFallingMine_LandsWithItsBoxBottomOnTheTileEdge()
        {
            var query = new UnifiedTileQueryService(BuildRoom());
            var start = new Vector2(FallColumnXPixels, 140f);

            (Vector2 origin, Vector2 velocity, int steps) = FallUntilLanded(query, start, 0.02f, 400);

            Assert.AreNotEqual(-1, steps, "the mine must land; if this fires the probe or the room is wrong");
            Assert.AreEqual(FloorTopPixels, origin.y, 0.001f,
                "AS3 snaps Y to the tile's top edge (phY1), so the box bottom rests on y=40");
            Assert.AreEqual(0f, velocity.y, 0.0001f, "and the fall is cancelled on the landing tick");
        }

        /// <summary>
        /// The positive control for the test above: the mine really travelled. Without it, a
        /// <see cref="MineBodyMath.Step"/> that never moved anything would satisfy "it ended at the
        /// floor" if the start had been on the floor.
        /// </summary>
        [Test]
        public void AFallingMine_ActuallyFallsBeforeItLands()
        {
            var query = new UnifiedTileQueryService(BuildRoom());
            var start = new Vector2(FallColumnXPixels, 140f);

            MineBodyStep first = MineBodyMath.Step(query, Vector2.zero, start, pinned: false, deltaTime: 0.02f);

            Assert.IsFalse(first.Grounded, "the start position is in mid-air");
            Assert.IsTrue(first.Moved, "so the first step must move it");
            Assert.Less(first.OriginPixels.y, start.y, "and downward, since AS3's Y axis is down-positive");
        }

        /// <summary>
        /// The defect the snap exists for, measured at the step size production actually uses.
        ///
        /// <para>At <c>Time.fixedDeltaTime</c> = 0.02 s a settled fall covers 6 units/s × 0.02 s = 0.12
        /// units = <b>12 px</b> per step — more than half the mine's 20 px height. Integration alone
        /// therefore leaves the mine up to a full step <i>inside</i> the floor, and because the art is
        /// anchored at the origin the sprite sinks with it. So: after landing, the origin must be
        /// <b>exactly</b> the tile edge, not merely "within a step of it", and it must not creep on
        /// subsequent ticks.</para>
        /// </summary>
        [Test]
        public void AtProductionStepSize_TheMineLandsOnTheTileEdge_AndDoesNotSinkIntoIt()
        {
            var query = new UnifiedTileQueryService(BuildRoom());
            var start = new Vector2(FallColumnXPixels, 140f);
            const float productionStep = 0.02f;

            (Vector2 landed, Vector2 _, int steps) = FallUntilLanded(query, start, productionStep, 400);
            Assert.AreNotEqual(-1, steps, "precondition: it lands");

            Assert.AreEqual(FloorTopPixels, landed.y, 0.001f,
                "the landing must be snapped to the tile edge, not left wherever the step put it");

            // And it stays there. A mine that settled 12 px deep would also "land"; the difference is
            // that it would then read as grounded at the wrong height forever.
            Vector2 origin = landed;
            Vector2 velocity = Vector2.zero;

            for (int i = 0; i < 50; i++)
            {
                MineBodyStep step = MineBodyMath.Step(query, velocity, origin, pinned: false, productionStep);
                velocity = step.Velocity;
                if (step.Moved) origin = step.OriginPixels;

                Assert.AreEqual(FloorTopPixels, origin.y, 0.001f, "resting height must not drift on tick " + i);
            }
        }

        /// <summary>
        /// A mine already seated is a no-op — no drift, no re-snap, and it reports grounded. This is the
        /// steady state the loop above spends most of its ticks in, asserted directly.
        /// </summary>
        [Test]
        public void ARestingMine_IsAStableNoOp()
        {
            var query = new UnifiedTileQueryService(BuildRoom());
            var resting = new Vector2(FallColumnXPixels, FloorTopPixels);

            MineBodyStep step = MineBodyMath.Step(query, Vector2.zero, resting, pinned: false, deltaTime: 0.02f);

            Assert.IsTrue(step.Grounded, "the resting position must read as grounded");
            Assert.IsFalse(step.Moved, "so nothing changes");
            Assert.AreEqual(resting.x, step.OriginPixels.x, 0.0001f);
            Assert.AreEqual(resting.y, step.OriginPixels.y, 0.0001f);
        }

        /// <summary>
        /// The grounded branch still moves X — AS3's ground resolution pins <c>Y</c> and leaves
        /// <c>X += dx</c> alone. An implementation that "rested" by freezing both axes would pass every
        /// landing test above and silently stop a sliding mine.
        /// </summary>
        [Test]
        public void WhileGrounded_TheHorizontalStepStillApplies()
        {
            var query = new UnifiedTileQueryService(BuildRoom());
            var resting = new Vector2(FallColumnXPixels, FloorTopPixels);

            MineBodyStep step = MineBodyMath.Step(query, new Vector2(5f, 0f), resting, pinned: false, deltaTime: 0.02f);

            // Braked first (9 units/s^2 for one step), then applied: (5 - 0.18) * 0.02 s * 100 px/unit.
            float expectedDx = (5f - UnitFallPhysics.BrakeUnitsPerSecondSquared * 0.02f) * 0.02f * 100f;

            Assert.IsTrue(step.Moved, "a grounded mine with horizontal velocity must slide");
            Assert.AreEqual(resting.x + expectedDx, step.OriginPixels.x, 0.001f,
                "dx is applied on the ground branch; only Y is pinned");
            Assert.AreEqual(FloorTopPixels, step.OriginPixels.y, 0.001f, "and Y is still pinned to the edge");
        }

        /// <summary>
        /// No room ⇒ no motion, and not grounded. A mine with no tile query is the reduced case for a
        /// scene with no map or a bare fixture; inventing a fall towards nothing would be worse than
        /// standing still, and reporting "grounded" would be a lie the caller could act on.
        /// </summary>
        [Test]
        public void WithNoRoom_TheMineNeitherMovesNorClaimsToBeGrounded()
        {
            var origin = new Vector2(FallColumnXPixels, 140f);
            var velocity = new Vector2(3f, -4f);

            MineBodyStep step = MineBodyMath.Step(null, velocity, origin, pinned: false, deltaTime: 0.02f);

            Assert.IsFalse(step.Moved, "nothing can be integrated without a tile grid");
            Assert.IsFalse(step.Grounded, "and there is no ground to stand on");
            Assert.AreEqual(origin, step.OriginPixels, "the origin is handed back untouched");
            Assert.AreEqual(velocity, step.Velocity, "as is the caller's velocity, which this function did not own");
        }

        // ── 5. The placement retry (AS3 WThrow.as:163-172) ────────────────────────────────────

        /// <summary>
        /// <c>if(_loc5_.collisionAll())</c> is false, so neither the retry nor the pin runs. This is the
        /// common case — a mine thrown into open air.
        /// </summary>
        [Test]
        public void Placement_AClearHandPosition_IsKept()
        {
            Assert.AreEqual(MinePlacement.Keep,
                MineBodyMath.ResolvePlacement(collidesAtHand: false, canRetry: true, collidesAfterRetry: false));
            Assert.AreEqual(MinePlacement.Keep,
                MineBodyMath.ResolvePlacement(collidesAtHand: false, canRetry: true, collidesAfterRetry: true),
                "the second test only runs when the first one fired, so its value is irrelevant here");
        }

        /// <summary>
        /// The hand position was inside a tile but the owner's feet are clear, so the mine is re-placed
        /// on the owner and stays a physics object.
        /// </summary>
        [Test]
        public void Placement_AnOwnerPositionThatIsClear_RetriesThere()
        {
            Assert.AreEqual(MinePlacement.RetryAtOwner,
                MineBodyMath.ResolvePlacement(collidesAtHand: true, canRetry: true, collidesAfterRetry: false));
        }

        /// <summary>
        /// Both positions are inside geometry, so <c>fixed = true</c> (<c>WThrow.as:171</c>) — the only
        /// path that produces a mine that never falls.
        /// </summary>
        [Test]
        public void Placement_WhenNeitherPositionIsClear_Pins()
        {
            Assert.AreEqual(MinePlacement.Pin,
                MineBodyMath.ResolvePlacement(collidesAtHand: true, canRetry: true, collidesAfterRetry: true));
        }

        /// <summary>
        /// The sequencing trap. The two tests are <b>sequential</b> in the oracle, and the second asks
        /// about the position the first moved the mine to — so with no owner to fall back to, the
        /// outcome is a pin regardless of <c>collidesAfterRetry</c>. Collapsing the two into one branch
        /// on the spawn position is the obvious simplification and it would produce a falling mine in a
        /// wall.
        /// </summary>
        [Test]
        public void Placement_WithNoOwnerToRetryAt_Pins_WhateverTheSecondTestWouldSay()
        {
            Assert.AreEqual(MinePlacement.Pin,
                MineBodyMath.ResolvePlacement(collidesAtHand: true, canRetry: false, collidesAfterRetry: false),
                "AS3 dereferences `owner` here and would throw; the port's reduced answer is to pin");
            Assert.AreEqual(MinePlacement.Pin,
                MineBodyMath.ResolvePlacement(collidesAtHand: true, canRetry: false, collidesAfterRetry: true));
        }
    }
}
