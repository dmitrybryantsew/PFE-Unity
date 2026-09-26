using System;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Core;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.EditMode.Systems.Physics.LowLevelPhysics2D
{
    /// <summary>
    /// Stage A spike for <c>UnityEngine.LowLevelPhysics2D</c>.
    ///
    /// <para>One test per question in <c>docs/Roadmap/LLP2D_IMPLEMENTATION_GUIDE.md</c> §5. This is
    /// investigation, not coverage: the assertions exist to make a negative answer loud, and the
    /// numbers go to the console so they can be copied into the write-up.</para>
    ///
    /// <para>THROWAWAY. No production consumer. Delete or promote in Stage B.</para>
    /// </summary>
    [TestFixture]
    public sealed class Llp2dSpikeTests
    {
        private const int CanonicalTickRate = SimClock.CanonicalTicksPerSecond;
        private const float StepSeconds = 1f / CanonicalTickRate;

        // ── Q1 ───────────────────────────────────────────────────────────────────────────────
        // Can a SimulationType.Script world be stepped inside an EditMode test, driven by
        // SimLoop.StepOnce()? If this fails the world cannot live in the Sim layer at all.
        [Test]
        public void Q1_ScriptWorld_StepsOnlyFromSimLoop()
        {
            PhysicsWorld world = Llp2d.CreateScriptWorld();
            try
            {
                Assert.AreEqual(
                    PhysicsWorld.SimulationType.Script,
                    world.simulationType,
                    "World must be manually stepped; an auto-stepping world cannot live in the Sim layer.");

                const float startY = 10f;
                PhysicsBody body = Llp2d.CreateBody(world, PhysicsBody.BodyType.Dynamic, new Vector2(0f, startY));
                Llp2d.CreateCircle(body, 0.25f);

                // A scripted world must not integrate on its own. Nothing has stepped yet.
                Assert.AreEqual(startY, body.position.y, 1e-5f, "World integrated before it was stepped.");

                SimClock clock = new SimClock(CanonicalTickRate);
                SimLoop loop = new SimLoop(clock, null);
                loop.Register(new Llp2d.WorldStepper(world, StepSeconds));

                const int ticks = CanonicalTickRate; // one second of simulation
                for (int i = 0; i < ticks; i++)
                {
                    loop.StepOnce();
                }

                float fallen = startY - body.position.y;

                // Analytic free fall is ½gt² = 4.5 units. A symplectic integrator overshoots by
                // ½g·dt·t = 0.15, so anything in [4.0, 5.2] is the expected answer. A 60 Hz or
                // render-driven step would land far outside it.
                Assert.Greater(fallen, 4.0f, "Too little fall — the step size is not one 30 Hz tick.");
                Assert.Less(fallen, 5.2f, "Too much fall — the step size is not one 30 Hz tick.");
                Assert.AreEqual(ticks - 1, loop.TickIndex, "SimLoop did not tick once per call.");

                Debug.Log($"[LLP2D Q1] OK. Fell {fallen:F4} units in {ticks} ticks " +
                          $"(analytic 4.5). simulationType={world.simulationType}, " +
                          $"workers={world.simulationWorkers}.");
            }
            finally
            {
                Llp2d.DestroyWorld(world);
            }
        }

        // ── Q2 ───────────────────────────────────────────────────────────────────────────────
        // Does it coexist with the classic Physics2D scene while TileCollider still uses
        // BoxCollider2D / PlatformEffector2D? The migration depends on running both at once, and
        // the roadmap defers collider demotion to P4 on purpose.
        [Test]
        public void Q2_CoexistsWithClassicPhysics2D()
        {
            const float classicGroundTop = -0.5f;
            const float llpGroundTop = 2.0f;

            GameObject classicGround = new GameObject("ClassicGround");
            GameObject classicBody = new GameObject("ClassicBody");
            try
            {
                BoxCollider2D groundCollider = classicGround.AddComponent<BoxCollider2D>();
                groundCollider.size = new Vector2(20f, 1f);
                classicGround.transform.position = new Vector3(0f, classicGroundTop - 0.5f, 0f);

                BoxCollider2D bodyCollider = classicBody.AddComponent<BoxCollider2D>();
                bodyCollider.size = new Vector2(1f, 1f);
                Rigidbody2D rb = classicBody.AddComponent<Rigidbody2D>();
                rb.bodyType = RigidbodyType2D.Dynamic;
                classicBody.transform.position = new Vector3(0f, 6f, 0f);

                // Physics2D.Simulate() refuses to run unless the classic scene is in Script mode.
                // Restored afterwards so this test does not change project behaviour for whatever
                // runs next in the same editor session.
                SimulationMode2D restoreMode = Physics2D.simulationMode;
                Physics2D.simulationMode = SimulationMode2D.Script;

                PhysicsWorld world = Llp2d.CreateScriptWorld();
                try
                {
                    // Full sizes — see Llp2d.CreateBox. Ground is 1 unit tall, so its centre sits
                    // half a unit below the surface we want.
                    PhysicsBody llpGround = Llp2d.CreateBody(
                        world, PhysicsBody.BodyType.Static, new Vector2(0f, llpGroundTop - 0.5f));
                    Llp2d.CreateBox(llpGround, new Vector2(20f, 1f));

                    PhysicsBody llpBody = Llp2d.CreateBody(
                        world, PhysicsBody.BodyType.Dynamic, new Vector2(0f, 6f));
                    Llp2d.CreateBox(llpBody, new Vector2(1f, 1f));

                    // Step both, interleaved, at the same cadence.
                    for (int i = 0; i < 240; i++)
                    {
                        Physics2D.Simulate(StepSeconds);
                        world.Simulate(StepSeconds);
                    }

                    float classicRest = classicBody.transform.position.y;
                    float llpRest = llpBody.position.y;

                    // Each body must settle on ITS OWN ground. If the two engines shared state both
                    // would end up on the same surface.
                    Assert.AreEqual(classicGroundTop + 0.5f, classicRest, 0.05f,
                        "Classic body did not settle on the classic ground.");
                    Assert.AreEqual(llpGroundTop + 0.5f, llpRest, 0.05f,
                        "LowLevel body did not settle on the LowLevel ground — the worlds are not isolated.");

                    // The LowLevel world must not have absorbed the classic bodies.
                    Assert.AreEqual(2, world.counters.bodyCount,
                        "LowLevel world counted bodies it does not own.");

                    // Classic queries still work with a LowLevel world alive.
                    Assert.IsNotNull(Physics2D.Raycast(new Vector2(0f, 6f), Vector2.down, 100f),
                        "Classic Physics2D raycast broke with a LowLevel world present.");

                    Debug.Log($"[LLP2D Q2] OK. Classic body rested at y={classicRest:F3} " +
                              $"(ground {classicGroundTop + 0.5f:F2}); LowLevel body at y={llpRest:F3} " +
                              $"(ground {llpGroundTop + 0.5f:F2}). worldCount={PhysicsWorld.worldCount}, " +
                              $"counters.bodyCount={world.counters.bodyCount}.");
                }
                finally
                {
                    Llp2d.DestroyWorld(world);
                    Physics2D.simulationMode = restoreMode;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(classicBody);
                UnityEngine.Object.DestroyImmediate(classicGround);
            }
        }

        // ── Q3 ───────────────────────────────────────────────────────────────────────────────
        // Cost of one room as a chain. Extrapolate to ~557 shipped rooms before designing anything
        // about lifetime or streaming.
        [Test]
        public void Q3_OneRoomAsChain_Cost()
        {
            RoomInstance room = BuildRepresentativeRoom();

            PhysicsWorld world = Llp2d.CreateScriptWorld();
            try
            {
                long before = world.counters.memoryUsed;
                RoomChainBuilder.BuildResult result =
                    RoomChainBuilder.Build(room, world, out PhysicsBody staticBody);
                long after = world.counters.memoryUsed;

                Assert.Greater(result.ChainCount, 0, "No chains were built.");
                Assert.IsTrue(staticBody.isValid, "Static room body is invalid.");

                long delta = after - before;
                const int shippedRooms = 557;
                double mb = delta / (1024.0 * 1024.0);
                double allRoomsMb = mb * shippedRooms;

                var sb = new StringBuilder();
                sb.Append("[LLP2D Q3] room ").Append(room.width).Append('x').Append(room.height)
                  .Append(" -> chains=").Append(result.ChainCount)
                  .Append(" points=").Append(result.PointCount)
                  .Append(" buildMs=").Append(result.ElapsedMilliseconds)
                  .Append(" worldMemoryBytes=").Append(after)
                  .Append(" deltaBytes=").Append(delta)
                  .Append(" (").Append(mb.ToString("F3")).Append(" MB)")
                  .Append(" | x").Append(shippedRooms).Append(" rooms = ")
                  .Append(allRoomsMb.ToString("F1")).Append(" MB")
                  .Append(" | counters: bodies=").Append(world.counters.bodyCount)
                  .Append(" shapes=").Append(world.counters.shapeCount)
                  .Append(" contacts=").Append(world.counters.contactCount);
                Debug.Log(sb.ToString());

                // A negative answer is "a room is too expensive to keep resident". Name the bound
                // so the number has meaning: 8 MB for every shipped room at once is not viable.
                Assert.Less(result.ElapsedMilliseconds, 2000L, "Building one room's chains took over 2s.");
                Assert.Less(allRoomsMb, 2048.0, "All shipped rooms as resident chains would exceed 2 GB.");
            }
            finally
            {
                Llp2d.DestroyWorld(world);
            }
        }

        // ── Q4 ───────────────────────────────────────────────────────────────────────────────
        // Is AS3's `shelf` expressible as a PreSolveEvent? Implements Unit.as:2578 and confirms
        // it survives. Feeds open decision L3.
        [Test]
        public void Q4_ShelfPassThrough_ViaPreSolve()
        {
            // CreateBox takes full size, so these are full dimensions; halves are derived.
            const float shelfSizeY = 0.1f;
            const float shelfHalfHeight = shelfSizeY * 0.5f;
            const float shelfTopY = 0.05f;
            const float bodySizeY = 0.4f;
            const float bodyHalfHeight = bodySizeY * 0.5f;

            // ── Calibration ──────────────────────────────────────────────────────────────────
            // The docs never say which return value disables the contact, and guessing wrong
            // inverts every result below. So measure it: whichever value lets the body fall
            // through is the disabling one.
            float forceTrueBottom = RunShelfScenario(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.ForceTrue,
                rule: ShelfPassThroughSolver.Rule.ApproachDirection, dropThrough: false,
                shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight, shelfHalfHeight: shelfHalfHeight,
                disableValue: false, out int callbacksTrue);

            float forceFalseBottom = RunShelfScenario(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.ForceFalse,
                rule: ShelfPassThroughSolver.Rule.ApproachDirection, dropThrough: false,
                shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight, shelfHalfHeight: shelfHalfHeight,
                disableValue: false, out int callbacksFalse);

            Assert.Greater(callbacksTrue, 0, "Pre-solve never fired for ForceTrue.");
            Assert.Greater(callbacksFalse, 0, "Pre-solve never fired for ForceFalse.");

            bool landedWithTrue = Mathf.Abs(forceTrueBottom - shelfTopY) < 0.06f;
            bool landedWithFalse = Mathf.Abs(forceFalseBottom - shelfTopY) < 0.06f;

            Assert.IsTrue(landedWithTrue ^ landedWithFalse,
                $"Both return values behaved identically (true->bottom {forceTrueBottom:F3}, " +
                $"false->bottom {forceFalseBottom:F3}); the callback is not gating the contact.");

            bool disableValue = !landedWithTrue;

            // ── (a) Direction rule, falling from above: must land on the shelf ─────────────────
            float landingBottom = RunShelfScenario(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.As3Rule,
                rule: ShelfPassThroughSolver.Rule.ApproachDirection,
                dropThrough: false,
                shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight, shelfHalfHeight: shelfHalfHeight,
                disableValue: disableValue, out int landingCallbacks);

            Assert.Greater(landingCallbacks, 0, "Pre-solve never fired in the landing case.");
            Assert.AreEqual(shelfTopY, landingBottom, 0.06f,
                "A body falling onto a shelf from above did not rest on its top surface.");

            // ── (b) Direction rule, rising from below: must pass through ───────────────────────
            // Gravity off so the result is purely the rule, not the trajectory.
            float blockedBottom = RunShelfScenario(
                startY: -3f, initialVelocityY: 6f, ticks: 30, gravityY: 0f,
                mode: null, rule: ShelfPassThroughSolver.Rule.ApproachDirection,
                dropThrough: false,
                shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight, shelfHalfHeight: shelfHalfHeight,
                disableValue: disableValue, out _);

            float passedBottom = RunShelfScenario(
                startY: -3f, initialVelocityY: 6f, ticks: 30, gravityY: 0f,
                mode: ShelfPassThroughSolver.Mode.As3Rule,
                rule: ShelfPassThroughSolver.Rule.ApproachDirection,
                dropThrough: false,
                shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight, shelfHalfHeight: shelfHalfHeight,
                disableValue: disableValue, out _);

            Assert.Less(blockedBottom, 0f, "Control case: body should have been blocked below the shelf.");
            Assert.Greater(passedBottom, shelfTopY,
                "A body rising from below did not pass through the shelf.");

            // ── (c) AS3 `throu` — explicit drop-through overrides the rule ─────────────────────
            float droppedBottom = RunShelfScenario(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.As3Rule,
                rule: ShelfPassThroughSolver.Rule.ApproachDirection,
                dropThrough: true,
                shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight, shelfHalfHeight: shelfHalfHeight,
                disableValue: disableValue, out _);

            Assert.Less(droppedBottom, -1f, "Explicit drop-through did not let the body fall past the shelf.");

            // ── (d) Observation only: AS3's literal positional test ────────────────────────────
            // Not asserted — the point of the spike is to measure it, and the measurement is the
            // answer. Asserting either outcome would bake in a guess about solver timing.
            float positionalBottom = RunShelfScenario(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.As3Rule,
                rule: ShelfPassThroughSolver.Rule.As3Position,
                dropThrough: false,
                shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight, shelfHalfHeight: shelfHalfHeight,
                disableValue: disableValue, out int positionalCallbacks);

            bool positionalLanded = Mathf.Abs(positionalBottom - shelfTopY) < 0.06f;

            Debug.Log($"[LLP2D Q4] disableValue={disableValue} " +
                      $"(true->bottom {forceTrueBottom:F3}, false->bottom {forceFalseBottom:F3}). " +
                      $"DIRECTION rule: landingBottom={landingBottom:F4} (shelf top {shelfTopY:F2}), " +
                      $"rising blocked={blockedBottom:F4} vs passed={passedBottom:F4}, " +
                      $"droppedBottom={droppedBottom:F4}. " +
                      $"AS3 POSITION rule: bottom={positionalBottom:F4} landed={positionalLanded} " +
                      $"(callbacks={positionalCallbacks}).");
        }

        // ── L3 follow-up ─────────────────────────────────────────────────────────────────────
        // Q4 concluded "AS3's shelf is expressible via approach direction, but not via AS3's own
        // positional porog test", and L3 defaulted to "keep classic colliders". Two rescue attempts
        // were never measured, and both are cheap:
        //
        //   (A) CCD. Q4 ran with continuousAllowed on (CreateScriptWorld defaults to true), so this
        //       variant mostly documents that CCD did not rescue it — and the FirstCallbackBottomY
        //       datum says why: if the callback sees an already-penetrating body, the TOI never
        //       matters because we disable the contact before the solver can use it.
        //   (B) Previous-tick position. We drive stepping from SimLoop, so the pre-integration
        //       position is free, and that is the state AS3's Unit.as:2578 actually tests.
        //
        // Observation-only, following the precedent of Q4 part (d): asserting an outcome here would
        // bake in a guess about solver timing. The two sanity assertions below only prove the
        // harness itself is sound.
        [Test]
        public void L3_ShelfPositionalRule_RescueAttempts()
        {
            // Same geometry as Q4 so the numbers are comparable.
            const float shelfSizeY = 0.1f;
            const float shelfHalfHeight = shelfSizeY * 0.5f;
            const float shelfTopY = 0.05f;
            const float bodySizeY = 0.4f;
            const float bodyHalfHeight = bodySizeY * 0.5f;

            // Measured by Q4's calibration probe, re-asserted below: false DISABLES the contact.
            const bool disableValue = false;

            // ── Sanity: the shelf and the callback gate really are load-bearing ────────────────
            ShelfRunResult keep = RunShelfScenarioEx(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.ForceTrue, rule: ShelfPassThroughSolver.Rule.ApproachDirection,
                dropThrough: false, shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight,
                shelfHalfHeight: shelfHalfHeight, disableValue: disableValue,
                continuous: true, trackPrevTick: false, fastBody: false);

            ShelfRunResult disable = RunShelfScenarioEx(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.ForceFalse, rule: ShelfPassThroughSolver.Rule.ApproachDirection,
                dropThrough: false, shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight,
                shelfHalfHeight: shelfHalfHeight, disableValue: disableValue,
                continuous: true, trackPrevTick: false, fastBody: false);

            Assert.AreEqual(shelfTopY, keep.BottomY, 0.06f,
                "Harness sanity failed: a contact that is always kept must land the body on the shelf.");
            Assert.Less(disable.BottomY, -1f,
                "Harness sanity failed: a contact that is always disabled must let the body fall past.");

            // ── (A) AS3 positional rule, CCD on and off ───────────────────────────────────────
            ShelfRunResult posCcd = RunShelfScenarioEx(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.As3Rule, rule: ShelfPassThroughSolver.Rule.As3Position,
                dropThrough: false, shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight,
                shelfHalfHeight: shelfHalfHeight, disableValue: disableValue,
                continuous: true, trackPrevTick: false, fastBody: false);

            ShelfRunResult posNoCcd = RunShelfScenarioEx(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.As3Rule, rule: ShelfPassThroughSolver.Rule.As3Position,
                dropThrough: false, shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight,
                shelfHalfHeight: shelfHalfHeight, disableValue: disableValue,
                continuous: false, trackPrevTick: false, fastBody: false);

            // A "high speed" body does CCD against dynamic and kinematic bodies; worth measuring
            // whether it changes what the callback sees against a static shelf.
            ShelfRunResult posFastBody = RunShelfScenarioEx(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.As3Rule, rule: ShelfPassThroughSolver.Rule.As3Position,
                dropThrough: false, shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight,
                shelfHalfHeight: shelfHalfHeight, disableValue: disableValue,
                continuous: true, trackPrevTick: false, fastBody: true);

            // ── (B) AS3 positional rule against the PREVIOUS tick's position ───────────────────
            ShelfRunResult prevCcd = RunShelfScenarioEx(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.As3Rule, rule: ShelfPassThroughSolver.Rule.As3PositionPrevTick,
                dropThrough: false, shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight,
                shelfHalfHeight: shelfHalfHeight, disableValue: disableValue,
                continuous: true, trackPrevTick: true, fastBody: false);

            ShelfRunResult prevNoCcd = RunShelfScenarioEx(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.As3Rule, rule: ShelfPassThroughSolver.Rule.As3PositionPrevTick,
                dropThrough: false, shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight,
                shelfHalfHeight: shelfHalfHeight, disableValue: disableValue,
                continuous: false, trackPrevTick: true, fastBody: false);

            // Rising from below. Gravity off so the result is the rule, not the trajectory.
            //
            // Read this one carefully: the solver has a single PorogUnits (grounded porog = 10 px),
            // but AS3 uses porog_jump = 4 px (Unit.as:279) when the unit is airborne. A body that
            // starts a tick within 10 px below the surface therefore BLOCKS here, which is AS3's
            // step-up band, not a bug — AS3 lifts a unit that close onto the surface. A production
            // rule must select porog vs porog_jump by the AS3 `stay` flag. Observation only.
            ShelfRunResult prevRising = RunShelfScenarioEx(
                startY: -3f, initialVelocityY: 6f, ticks: 30, gravityY: 0f,
                mode: ShelfPassThroughSolver.Mode.As3Rule, rule: ShelfPassThroughSolver.Rule.As3PositionPrevTick,
                dropThrough: false, shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight,
                shelfHalfHeight: shelfHalfHeight, disableValue: disableValue,
                continuous: true, trackPrevTick: true, fastBody: false);

            // AS3 `throu`: explicit drop-through must still win.
            ShelfRunResult prevDropThrough = RunShelfScenarioEx(
                startY: 3f, initialVelocityY: 0f, ticks: 200, gravityY: Llp2d.GravityUnitsPerSecondSquared,
                mode: ShelfPassThroughSolver.Mode.As3Rule, rule: ShelfPassThroughSolver.Rule.As3PositionPrevTick,
                dropThrough: true, shelfTopY: shelfTopY, bodyHalfHeight: bodyHalfHeight,
                shelfHalfHeight: shelfHalfHeight, disableValue: disableValue,
                continuous: true, trackPrevTick: true, fastBody: false);

            // A contact must have been created at all, or the run measured nothing.
            Assert.Greater(prevCcd.CallbackCount, 0,
                "Pre-solve never fired for the previous-tick rule; the run measured nothing.");

            Debug.Log(
                $"[LLP2D L3] shelfTop={shelfTopY:F3} porog={TileQueryConstants.PorogGrounded * Llp2d.PixelToUnit:F3} " +
                $"bodyHalfHeight={bodyHalfHeight:F3}\n" +
                $"  A ccd        bottom={posCcd.BottomY:F4} landed={posCcd.Landed(shelfTopY)} " +
                $"firstCbBottom={posCcd.FirstCallbackBottomY:F4}@tick{posCcd.FirstCallbackTick} cb={posCcd.CallbackCount}\n" +
                $"  A no-ccd     bottom={posNoCcd.BottomY:F4} landed={posNoCcd.Landed(shelfTopY)} " +
                $"firstCbBottom={posNoCcd.FirstCallbackBottomY:F4}@tick{posNoCcd.FirstCallbackTick} cb={posNoCcd.CallbackCount}\n" +
                $"  A fastBody   bottom={posFastBody.BottomY:F4} landed={posFastBody.Landed(shelfTopY)} " +
                $"firstCbBottom={posFastBody.FirstCallbackBottomY:F4}@tick{posFastBody.FirstCallbackTick} cb={posFastBody.CallbackCount}\n" +
                $"  B prev+ccd   bottom={prevCcd.BottomY:F4} landed={prevCcd.Landed(shelfTopY)} " +
                $"firstCbBottom={prevCcd.FirstCallbackBottomY:F4}@tick{prevCcd.FirstCallbackTick} cb={prevCcd.CallbackCount}\n" +
                $"  B prev-noCcd bottom={prevNoCcd.BottomY:F4} landed={prevNoCcd.Landed(shelfTopY)} " +
                $"firstCbBottom={prevNoCcd.FirstCallbackBottomY:F4}@tick{prevNoCcd.FirstCallbackTick} cb={prevNoCcd.CallbackCount}\n" +
                $"  B rising     bottom={prevRising.BottomY:F4} passedThrough={prevRising.BottomY > shelfTopY} cb={prevRising.CallbackCount}\n" +
                $"  B throu      bottom={prevDropThrough.BottomY:F4} cb={prevDropThrough.CallbackCount}");
        }

        // ── Q5 ───────────────────────────────────────────────────────────────────────────────
        // Determinism probe: workers = 1, fixed inputs, 1000 ticks, hash body transforms, repeat
        // and compare. If it is not reproducible it can never enter a golden trace.
        [Test]
        public void Q5_Determinism_ThousandTicks()
        {
            const int ticks = 1000;

            uint a = RunHash(ticks, seedOffset: 0f);
            uint b = RunHash(ticks, seedOffset: 0f);
            uint different = RunHash(ticks, seedOffset: 0.01f);

            // Guard against a hash that is constant by accident.
            Assert.AreNotEqual(a, different,
                "The hash did not change when the initial state changed — the probe is not measuring anything.");

            Assert.AreEqual(a, b,
                $"Two identical {ticks}-tick runs diverged ({a:X8} vs {b:X8}). " +
                "The world cannot enter a golden trace.");

            Debug.Log($"[LLP2D Q5] OK. {ticks} ticks, identical runs hash 0x{a:X8}; " +
                      $"perturbed run 0x{different:X8}.");
        }

        // ── Q6 ───────────────────────────────────────────────────────────────────────────────
        // CCD check: does a projectile at AS3 speeds stop reliably with continuousAllowed?
        [Test]
        public void Q6_ContinuousCollision_FastProjectile()
        {
            const float wallX = 5f;
            const float wallHalfWidth = Llp2d.TileSizeUnits * 0.5f; // one tile thick
            const float projectileRadius = 0.05f;

            // AS3 bullets are px-per-30Hz-frame; CombatCalculator.PixelSpeedToUnitySpeed is
            // px/100 * 30. 2000 px/frame is the "instant hit" tier (ProjectileController.cs:133).
            const float pxPerFrame = 2000f;
            float unitsPerSecond = pxPerFrame / 100f * Llp2d.SimTicksPerSecond;
            float unitsPerTick = unitsPerSecond * StepSeconds;

            float continuousX = RunProjectile(wallX, wallHalfWidth, projectileRadius, unitsPerSecond, continuous: true);
            float discreteX = RunProjectile(wallX, wallHalfWidth, projectileRadius, unitsPerSecond, continuous: false);

            float nearFace = wallX - wallHalfWidth;      // wall occupies [nearFace, farFace]
            float farFace = wallX + wallHalfWidth;
            float firstTouchX = nearFace - projectileRadius;

            // The question is "does it stop", not "does it stop flush". Report the penetration
            // rather than asserting zero: a solver is allowed a slop, and the number is the answer.
            float penetration = continuousX - firstTouchX;

            Assert.Less(continuousX, farFace,
                $"CCD failed: a projectile moving {unitsPerTick:F1} units/tick passed clean through a " +
                $"{wallHalfWidth * 2f:F2}-unit wall (ended at x={continuousX:F3}).");

            Assert.Greater(discreteX, farFace,
                "Control case: the discrete run should have tunnelled through the wall.");

            Debug.Log($"[LLP2D Q6] OK. {pxPerFrame} px/frame = {unitsPerSecond:F1} units/s = " +
                      $"{unitsPerTick:F2} units/tick vs a {wallHalfWidth * 2f:F2}-unit wall " +
                      $"[{nearFace:F2}..{farFace:F2}]. continuous stopped at x={continuousX:F3} " +
                      $"(first touch {firstTouchX:F3}, penetration {penetration:F3} units = " +
                      $"{penetration / Llp2d.PixelToUnit:F1} px); discrete reached x={discreteX:F3}.");
        }

        // ── Scenario runners ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Outcome of one shelf scenario, carrying the diagnostics the L3 measurement needs: not
        /// just where the body ended, but what the pre-solve callback saw the first time it ran.
        /// </summary>
        private struct ShelfRunResult
        {
            public float BottomY;
            public float FirstCallbackBottomY;
            public int FirstCallbackTick;
            public int CallbackCount;

            public bool Landed(float shelfTopY)
            {
                return Mathf.Abs(BottomY - shelfTopY) < 0.06f;
            }
        }

        /// <summary>
        /// One body, one shelf, gravity on. Returns the body's bottom Y after <paramref name="ticks"/>.
        /// </summary>
        private static float RunShelfScenario(
            float startY,
            float initialVelocityY,
            int ticks,
            float gravityY,
            ShelfPassThroughSolver.Mode? mode,
            ShelfPassThroughSolver.Rule rule,
            bool dropThrough,
            float shelfTopY,
            float bodyHalfHeight,
            float shelfHalfHeight,
            bool disableValue,
            out int callbackCount)
        {
            PhysicsWorld world = Llp2d.CreateScriptWorld(gravityY: -gravityY);
            try
            {
                world.sleepingAllowed = false;

                PhysicsBody shelf = Llp2d.CreateBody(
                    world, PhysicsBody.BodyType.Static, new Vector2(0f, shelfTopY - shelfHalfHeight));
                PhysicsShape shelfShape = Llp2d.CreateBox(shelf, new Vector2(4f, shelfHalfHeight * 2f));

                ShelfPassThroughSolver solver = null;
                if (mode.HasValue)
                {
                    world.preSolveCallbacks = true;
                    shelfShape.preSolveCallbacks = true;

                    solver = ScriptableObject.CreateInstance<ShelfPassThroughSolver>();
                    solver.ShelfTopY = shelfTopY;
                    solver.BodyHalfHeight = bodyHalfHeight;
                    solver.SolverMode = mode.Value;
                    solver.RuleMode = rule;
                    solver.DisableReturnValue = disableValue;
                    solver.DropThroughRequested = dropThrough;
                    shelfShape.callbackTarget = solver;
                }

                PhysicsBody body = Llp2d.CreateBody(
                    world, PhysicsBody.BodyType.Dynamic, new Vector2(0f, startY));
                Llp2d.CreateBox(body, new Vector2(bodyHalfHeight * 2f, bodyHalfHeight * 2f));
                body.linearVelocity = new Vector2(0f, initialVelocityY);

                for (int i = 0; i < ticks; i++)
                {
                    world.Simulate(StepSeconds);
                }

                callbackCount = solver != null ? solver.InvocationCount : 0;

                if (solver != null)
                {
                    UnityEngine.Object.DestroyImmediate(solver);
                }

                return body.position.y - bodyHalfHeight;
            }
            finally
            {
                Llp2d.DestroyWorld(world);
            }
        }

        /// <summary>
        /// Shelf scenario with the knobs the L3 measurement needs: continuous collision on/off,
        /// the "high speed body" flag, and pre-tick position snapshotting.
        ///
        /// <para>Separate from <see cref="RunShelfScenario"/> so Q4's seven call sites keep the exact
        /// behaviour they were measured with — the point is that L3's numbers stay comparable to
        /// Q4's.</para>
        /// </summary>
        private static ShelfRunResult RunShelfScenarioEx(
            float startY,
            float initialVelocityY,
            int ticks,
            float gravityY,
            ShelfPassThroughSolver.Mode mode,
            ShelfPassThroughSolver.Rule rule,
            bool dropThrough,
            float shelfTopY,
            float bodyHalfHeight,
            float shelfHalfHeight,
            bool disableValue,
            bool continuous,
            bool trackPrevTick,
            bool fastBody)
        {
            ShelfRunResult result = default;

            PhysicsWorld world = Llp2d.CreateScriptWorld(gravityY: -gravityY, continuousAllowed: continuous);
            try
            {
                world.sleepingAllowed = false;

                PhysicsBody shelf = Llp2d.CreateBody(
                    world, PhysicsBody.BodyType.Static, new Vector2(0f, shelfTopY - shelfHalfHeight));
                PhysicsShape shelfShape = Llp2d.CreateBox(shelf, new Vector2(4f, shelfHalfHeight * 2f));

                world.preSolveCallbacks = true;
                shelfShape.preSolveCallbacks = true;

                ShelfPassThroughSolver solver = ScriptableObject.CreateInstance<ShelfPassThroughSolver>();
                solver.ShelfTopY = shelfTopY;
                solver.BodyHalfHeight = bodyHalfHeight;
                solver.SolverMode = mode;
                solver.RuleMode = rule;
                solver.DisableReturnValue = disableValue;
                solver.DropThroughRequested = dropThrough;
                shelfShape.callbackTarget = solver;

                PhysicsBody body = Llp2d.CreateBody(
                    world, PhysicsBody.BodyType.Dynamic, new Vector2(0f, startY));
                Llp2d.CreateBox(body, new Vector2(bodyHalfHeight * 2f, bodyHalfHeight * 2f));
                body.linearVelocity = new Vector2(0f, initialVelocityY);
                if (fastBody)
                {
                    body.fastCollisionsAllowed = true;
                }

                for (int i = 0; i < ticks; i++)
                {
                    solver.TickIndex = i;

                    // Snapshot BEFORE the step. This is the state AS3's Unit.as:2578 tests against,
                    // and it is free in production because SimLoop owns stepping.
                    if (trackPrevTick)
                    {
                        solver.PreviousBottomY = body.position.y - bodyHalfHeight;
                    }

                    world.Simulate(StepSeconds);
                }

                result.BottomY = body.position.y - bodyHalfHeight;
                result.FirstCallbackBottomY = solver.FirstCallbackBottomY;
                result.FirstCallbackTick = solver.FirstCallbackTick;
                result.CallbackCount = solver.InvocationCount;

                UnityEngine.Object.DestroyImmediate(solver);
                return result;
            }
            finally
            {
                Llp2d.DestroyWorld(world);
            }
        }

        /// <summary>
        /// A projectile fired at a wall. Returns its final X. Gravity is off so the result isolates
        /// collision detection from trajectory.
        /// </summary>
        private static float RunProjectile(
            float wallX, float wallHalfWidth, float radius, float speedUnitsPerSecond, bool continuous)
        {
            PhysicsWorld world = Llp2d.CreateScriptWorld(gravityY: 0f, continuousAllowed: continuous);
            try
            {
                PhysicsBody wall = Llp2d.CreateBody(
                    world, PhysicsBody.BodyType.Static, new Vector2(wallX, 0f));
                Llp2d.CreateBox(wall, new Vector2(wallHalfWidth * 2f, 4f));

                PhysicsBody projectile = Llp2d.CreateBody(
                    world, PhysicsBody.BodyType.Dynamic, new Vector2(0f, 0f));
                Llp2d.CreateCircle(projectile, radius);
                projectile.linearVelocity = new Vector2(speedUnitsPerSecond, 0f);

                for (int i = 0; i < 30; i++)
                {
                    world.Simulate(StepSeconds);
                }

                return projectile.position.x;
            }
            finally
            {
                Llp2d.DestroyWorld(world);
            }
        }

        /// <summary>Ten stacked bodies, one second of identical input, hashed.</summary>
        private static uint RunHash(int ticks, float seedOffset)
            // Delegates to the shared probe in Llp2d so the cross-process golden recorded by
            // Llp2dStageB0Tests and this in-process comparison cannot drift apart.
            => Llp2d.RunDeterminismProbe(ticks, seedOffset, StepSeconds);

        /// <summary>
        /// A 48x25 room with the surfaces a generated room actually has: floor, ceiling, side walls,
        /// a couple of platforms and a block. Not a template — just enough to make the cost honest.
        /// </summary>
        private static RoomInstance BuildRepresentativeRoom()
        {
            RoomInstance room = SyntheticRoomBuilder.BuildEmpty(
                PFE.Systems.Map.WorldConstants.ROOM_WIDTH, PFE.Systems.Map.WorldConstants.ROOM_HEIGHT);

            for (int x = 0; x < room.width; x++)
            {
                SetWall(room, x, 0);
                SetWall(room, x, room.height - 1);
            }

            for (int y = 0; y < room.height; y++)
            {
                SetWall(room, 0, y);
                SetWall(room, room.width - 1, y);
            }

            // Two shelves, the pervasive one-way surface.
            for (int x = 6; x < 14; x++) SetPlatform(room, x, 8);
            for (int x = 26; x < 34; x++) SetPlatform(room, x, 14);

            // A solid block: exercises interior culling (its shared edges must not become chains).
            for (int x = 18; x < 24; x++)
            {
                for (int y = 1; y < 5; y++)
                {
                    SetWall(room, x, y);
                }
            }

            return room;
        }

        private static void SetWall(RoomInstance room, int x, int y)
        {
            room.tiles[x, y].physicsType = TilePhysicsType.Wall;
        }

        private static void SetPlatform(RoomInstance room, int x, int y)
        {
            room.tiles[x, y].physicsType = TilePhysicsType.Platform;
        }
    }
}
