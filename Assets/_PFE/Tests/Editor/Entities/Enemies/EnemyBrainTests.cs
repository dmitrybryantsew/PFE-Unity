using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Entities.Enemies;
using PFE.Entities.Enemies.Archetypes;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;
using PFE.Tests.EditMode.Systems.Map.TileCollision;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Enemies
{
    [TestFixture]
    public class EnemyBrainTests
    {
        /// <summary>A tile cell — <c>WorldConstants.TILE_SIZE</c>.</summary>
        const float TileSize = 40f;

        private List<GameObject> _spawnedObjects;

        [SetUp]
        public void SetUp()
        {
            _spawnedObjects = new List<GameObject>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_spawnedObjects != null)
            {
                foreach (var obj in _spawnedObjects)
                {
                    if (obj != null)
                    {
                        Object.DestroyImmediate(obj);
                    }
                }
                _spawnedObjects.Clear();
            }
        }

        private GameObject CreateGameObject(string name = "TestObject")
        {
            var go = new GameObject(name);
            _spawnedObjects.Add(go);
            return go;
        }

        // ── Fixtures for the zombie half ─────────────────────────────────────

        /// <summary>
        /// The <b>real</b> <c>zombie0</c> definition, not a hand-built one.
        ///
        /// <para><b>Why the real asset matters.</b> Half of what these tests pin is that the zombie reads
        /// its numbers from its data — <c>&lt;comb damage='14'&gt;</c>, <c>&lt;move speed='1.5' run='10'&gt;</c>
        /// (<c>AllData.as:784-790</c>). A synthetic <c>ScriptableObject</c> with those values typed in by
        /// hand would pass whether or not the importer, the asset and the reader agree, which is exactly
        /// the gap that let the zombie spawn as the content-free family template.</para>
        /// </summary>
        private static UnitDefinition Zombie0()
        {
            bool found = ResourcesUnitDefinitionProvider.Shared.TryGetUnit("zombie0", out UnitDefinition definition);
            Assert.IsTrue(found && definition != null,
                "Resources/Units/zombie0 must exist — these tests are about its data, and a missing asset " +
                "would make every assertion below vacuous.");
            return definition;
        }

        /// <summary>
        /// Builds a zombie the way <c>RoomUnitSpawner</c> does: collider first (the controller's
        /// <c>Awake</c> reads it), then the <c>Visual</c> child with its renderer and animator, then the
        /// brain, then the controller.
        /// </summary>
        private GameObject CreateZombie(
            UnitDefinition definition,
            Vector3 position,
            out ZombieController controller,
            out ZombieBrain brain,
            out UnitAnimator animator)
        {
            var go = CreateGameObject("Zombie");
            go.transform.position = position;

            var body = go.AddComponent<Rigidbody2D>();
            body.bodyType = RigidbodyType2D.Kinematic;

            var collider = go.AddComponent<BoxCollider2D>();
            if (definition != null)
            {
                // AS3 Unit.as:1875-1876 — `Y1 = Y - scY; Y2 = Y`, so the box sits entirely ABOVE the
                // origin because the origin is the unit's feet. Same construction RoomUnitSpawner uses,
                // and the ground probe and the AABB hit test both depend on it.
                collider.size = new Vector2(definition.Width, definition.Height);
                collider.offset = new Vector2(0f, definition.Height * 0.5f);
            }

            var visual = new GameObject("Visual");
            visual.transform.SetParent(go.transform, false);
            var renderer = visual.AddComponent<SpriteRenderer>();

            animator = visual.AddComponent<UnitAnimator>();
            animator.Initialize(definition, renderer);

            brain = go.AddComponent<ZombieBrain>();
            controller = go.AddComponent<ZombieController>();

            controller.Initialize(
                definition,
                definition != null ? new UnitStats(definition.health, 100f) : null);

            return go;
        }

        /// <summary>
        /// A damage system that resolves <b>inline</b>, which is AS3's shape.
        ///
        /// <para><b>Why <c>Start()</c> is deliberately not called.</b> <c>IsTickAligned</c> requires the
        /// system to be on a <c>SimLoop</c>; leaving it off means <c>Report</c> applies the hit at report
        /// time, so a test needs no loop, no tick index and no debug settings. A settings asset is not
        /// needed either: every read of it is null-guarded, and the two it would change
        /// (<c>ApplyVulnerabilities</c>, <c>TestDamage</c>) are not what these tests assert.</para>
        /// </summary>
        private static DamageSystem MakeImmediateDamageSystem()
        {
            return new DamageSystem(
                new DamageCalculator(new CombatCalculator()),
                new PcgRngService(0xC0FFEEUL),
                publisher: null,
                debugSettings: null,
                simLoop: null);
        }

        /// <summary>
        /// A 16 x 5 room with a solid ground slab at row 0 and walls at x = 0 / x = 15. Row 0 of the
        /// ASCII is the TOP, so the slab's upper surface is at y = 40 px — the surface
        /// <see cref="UnitGroundProbe"/> expects a unit to be seated 1 px above.
        /// </summary>
        private static ITileQueryService GroundedRoom()
            => new UnifiedTileQueryService(SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..............#",
                "################"
            }));

        /// <summary>Feet 1 px above the slab's surface, in world units. See <c>UnitGroundProbe.SeatPixels</c>.</summary>
        private static Vector3 OnTheGround(float pixelX)
            => new Vector3(pixelX / 100f, (TileSize + 1f) / 100f, 0f);

        // ── Sensors (unchanged) ──────────────────────────────────────────────

        [Test]
        public void Sensors_VisionAngle_DetectsForward_AndRejectsBehind()
        {
            var sensors = new EnemySensors
            {
                VisionRangePixels = 480f,
                CloseProximityPixels = 40f
            };

            Vector2 eyePos = new Vector2(100f, 100f);

            // Facing Right (+1)
            Assert.IsTrue(sensors.CheckVisionAngle(eyePos, facing: 1, new Vector2(200f, 100f)),
                "Target in front (to the right) should be seen.");

            Assert.IsFalse(sensors.CheckVisionAngle(eyePos, facing: 1, new Vector2(0f, 100f)),
                "Target far behind (to the left) should not be seen.");

            Assert.IsTrue(sensors.CheckVisionAngle(eyePos, facing: 1, new Vector2(80f, 100f)),
                "Target within 40px close proximity bubble behind unit should be detected.");

            // Facing Left (-1)
            Assert.IsTrue(sensors.CheckVisionAngle(eyePos, facing: -1, new Vector2(0f, 100f)),
                "Target in front (to the left) should be seen.");

            Assert.IsFalse(sensors.CheckVisionAngle(eyePos, facing: -1, new Vector2(200f, 100f)),
                "Target far behind (to the right) should not be seen.");
        }

        /// <summary>
        /// The sensor's hearing wrapper. The arithmetic is pinned in <c>NoiseMathTests</c>; this checks
        /// the two things the wrapper adds — that it reads the source's <c>noise</c> as a radius, and
        /// that the listener's <c>ear</c> scales it.
        ///
        /// <para><b>The test this replaces asserted the bug.</b> It built a sensor with
        /// <c>HearingRangePixels = 300f</c> and a multiplier, then asked a bool whether a point was
        /// inside — which is the constant-radius model the diagnosis rejected, and it passed for as long
        /// as the bug lived. A fixture that encodes the wrong rule is worse than no fixture, because it
        /// turns "someone changed the radius" into a red build instead of a fix.</para>
        /// </summary>
        [Test]
        public void Sensors_Hearing_IsTheNoiseTimesEarProduct_NotAConstant()
        {
            var sensors = new EnemySensors { Ear = 1f, EarMultiplier = 1f };

            Vector2 listener = new Vector2(100f, 100f);

            Assert.Greater(sensors.HearIntensity(listener, new Vector2(250f, 100f), 300), 0f,
                "A 300-noise source 150 px away is audible (r = 300 x ear 1 x 1).");

            Assert.AreEqual(0f, sensors.HearIntensity(listener, new Vector2(401f, 100f), 300),
                "The same source 301 px away is inaudible — the radius is a product, not a constant.");

            Assert.Greater(sensors.HearIntensity(listener, new Vector2(450f, 100f), 600), 0f,
                "A 600-noise source 350 px away is audible, because twice the noise is twice the radius.");

            Assert.AreEqual(0f, sensors.HearIntensity(listener, listener, 0),
                "Noise 0 is inaudible at ANY distance including point blank — the reported bug, " +
                "as one assertion.");
        }

        [Test]
        public void Sensors_Hearing_EarScalesTheRadius_InBothDirections()
        {
            Vector2 listener = new Vector2(0f, 0f);
            Vector2 at500Px = new Vector2(500f, 0f);

            var sharpEared = new EnemySensors { Ear = 2f };
            var deaf = new EnemySensors { Ear = 0f };

            Assert.Greater(sharpEared.HearIntensity(listener, at500Px, 300), 0f,
                "Ear 2 doubles a 300-noise source's radius to 600 px, so 500 px is audible.");

            Assert.AreEqual(0f, deaf.HearIntensity(listener, at500Px, 300),
                "Ear 0 — the value every small robot in the roster authors — is deaf at any noise.");
        }

        [Test]
        public void Sensors_Hearing_IntensityIsGradedAndCappedAtFour()
        {
            var sensors = new EnemySensors { Ear = 1f, EarMultiplier = 1f };
            Vector2 listener = new Vector2(0f, 0f);

            // r = 400 px. Half way in: (1 - 0.25) x 4 = 3.
            float halfWay = sensors.HearIntensity(listener, new Vector2(200f, 0f), 400);

            Assert.That(halfWay, Is.EqualTo(3f).Within(0.001f),
                "Intensity is (1 - d^2/r^2) x 4, not a bool.");

            float nearRim = sensors.HearIntensity(listener, new Vector2(390f, 0f), 400);
            float nearCentre = sensors.HearIntensity(listener, new Vector2(10f, 0f), 400);

            Assert.That(nearCentre, Is.GreaterThan(nearRim),
                "A nearer source must be more audible than one at the rim.");
            Assert.That(nearCentre, Is.LessThanOrEqualTo(NoiseMath.MaxHearingIntensity));
        }

        [Test]
        public void Controller_TakeDamage_TriggersAlertState_InBrain()
        {
            var go = CreateGameObject("Zombie");
            go.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            go.AddComponent<BoxCollider2D>();

            var brain = go.AddComponent<ZombieBrain>();
            var controller = go.AddComponent<ZombieController>();

            var stats = new UnitStats(100f, 100f);
            controller.Initialize(null, stats);
            brain.Initialize(null, null);

            Assert.AreEqual(EnemyAIState.Idle, brain.CurrentState);

            controller.TakeDamage(20f);

            Assert.AreEqual(EnemyAIState.Alert, brain.CurrentState,
                "Taking damage while Idle should alert the brain.");
            Assert.IsTrue(brain.Blackboard.AlertTimerTicks > 0,
                "Taking damage should set an alert cooldown timer.");
        }

        [Test]
        public void Controller_Death_TransitionsBrainToDeadState()
        {
            var go = CreateGameObject("Zombie");
            go.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            go.AddComponent<BoxCollider2D>();

            var brain = go.AddComponent<ZombieBrain>();
            var controller = go.AddComponent<ZombieController>();

            var stats = new UnitStats(50f, 100f);
            controller.Initialize(null, stats);
            brain.Initialize(null, null);

            controller.TakeDamage(60f); // Lethal damage

            Assert.IsFalse(controller.IsAlive);
            Assert.AreEqual(EnemyAIState.Dead, brain.CurrentState,
                "Lethal damage must transition the brain to the Dead state.");
        }

        // ── The contact attack goes through the damage pipeline ──────────────

        /// <summary>
        /// AS3's contact-attack pair — <c>Unit.attKorp()</c> (<c>Unit.as:3267-3277</c>) then the target's
        /// <c>udarUnit()</c> (<c>:4125-4166</c>).
        ///
        /// <para><b>What this pins, and why the assertion is on the <i>system</i> and not only on the
        /// HP.</b> <c>UnitController.TakeDamage</c> is the pipeline's <i>receiving</i> end — it is what
        /// <c>DamageSystem</c> calls once the number is computed. A zombie that clawed its target through
        /// it would drop HP just the same, so an HP-only assertion passes for both the right and the
        /// wrong implementation. <c>ResolvedCount</c> is the observable that separates them: it only
        /// moves when a hit actually went through the resolver, which is where the target's vulnerability
        /// table, <c>skin</c> resistance, armour pool and death check live.</para>
        /// </summary>
        [Test]
        public void Zombie_ContactAttack_DealsDamageThroughTheDamagePipeline()
        {
            UnitDefinition definition = Zombie0();
            GameObject zombieGo = CreateZombie(
                definition, Vector3.zero, out ZombieController zombieController,
                out ZombieBrain brain, out _);
            brain.Initialize(null, null);

            // A target whose box overlaps the zombie's. Both boxes are 55 x 70 px and sit above their
            // origin, so co-locating the two origins makes them overlap completely.
            var targetGo = CreateGameObject("TargetPlayer");
            targetGo.transform.position = Vector3.zero;
            targetGo.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            var targetCollider = targetGo.AddComponent<BoxCollider2D>();
            targetCollider.size = new Vector2(definition.Width, definition.Height);
            targetCollider.offset = new Vector2(0f, definition.Height * 0.5f);

            var target = targetGo.AddComponent<UnitController>();
            target.Initialize(null, new UnitStats(100f, 100f));

            DamageSystem damageSystem = MakeImmediateDamageSystem();
            zombieController.SetDamageSystem(damageSystem);

            float initialHp = target.CurrentHealth;
            Assert.AreEqual(0, damageSystem.ResolvedCount, "precondition: nothing has been resolved yet");

            bool hit = zombieController.TryContactAttack(target);

            Assert.IsTrue(hit, "The boxes overlap and the target's window is clear, so the hit must land.");
            Assert.AreEqual(1, damageSystem.ResolvedCount,
                "The hit must reach the resolver, not be applied as a raw HP subtraction.");
            Assert.Less(target.CurrentHealth, initialHp,
                "zombie0's <comb damage='14'> should have taken HP off the target.");
        }

        /// <summary>
        /// The rate limiter is the <b>target's</b> window, not the attacker's cooldown: <c>attKorp</c>
        /// refuses a target whose <c>neujaz</c> is running (<c>Unit.as:3273</c>) and <c>udarUnit</c> then
        /// grants it (<c>:4135</c>). So two ticks in a row must land exactly one hit — and a zombie that
        /// swung every tick would be a melee DPS multiplier with no error message anywhere.
        /// </summary>
        [Test]
        public void ContactAttack_IsRefused_WhileTheTargetsContactWindowIsRunning()
        {
            UnitDefinition definition = Zombie0();
            GameObject zombieGo = CreateZombie(
                definition, Vector3.zero, out ZombieController zombieController, out _, out _);

            var targetGo = CreateGameObject("TargetPlayer");
            targetGo.transform.position = Vector3.zero;
            targetGo.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            var targetCollider = targetGo.AddComponent<BoxCollider2D>();
            targetCollider.size = new Vector2(definition.Width, definition.Height);
            targetCollider.offset = new Vector2(0f, definition.Height * 0.5f);

            var target = targetGo.AddComponent<UnitController>();
            target.Initialize(null, new UnitStats(1000f, 100f));

            DamageSystem damageSystem = MakeImmediateDamageSystem();
            zombieController.SetDamageSystem(damageSystem);

            Assert.IsTrue(zombieController.TryContactAttack(target), "the first hit lands");
            Assert.IsTrue(target.IsContactInvulnerable,
                "udarUnit grants neujaz on the TARGET (Unit.as:4135), before the damage is applied.");

            Assert.IsFalse(zombieController.TryContactAttack(target),
                "attKorp refuses while the target's neujaz is running (Unit.as:3273).");
            Assert.AreEqual(1, damageSystem.ResolvedCount, "the second attempt must not reach the resolver.");
        }

        // ── Animation is driven by the brain ─────────────────────────────────

        /// <summary>
        /// The oracle's speed ladder and the animation it selects, in one test.
        ///
        /// <para><b>Why a synthetic room instead of a bare spawn.</b> <c>UnitZombie.animate()</c> tests
        /// <c>stay</c> (groundedness) <i>before</i> it looks at <c>dx</c> — an airborne unit draws
        /// <c>jump</c> whatever its speed is (<c>UnitZombie.as:276-297</c>). A bare
        /// <c>AddComponent</c> spawn has no tile query, so it is never grounded and every assertion here
        /// would read <c>jump</c>. The room gives the ground probe something to answer against, which is
        /// the same reason <c>RoomUnitSpawner</c> calls <c>SetTileQuery</c>.</para>
        ///
        /// <para><b>walk vs run is <c>aiState</c>, not speed.</b> The oracle draws <c>run</c> for
        /// <c>aiState == 3</c> (combat) and <c>trot</c> for <c>aiState == 2</c> (alert) — it only reaches
        /// the <c>dx &gt; 6</c> fallback when neither state is set. So this walks a patrolling zombie
        /// (aiState 1) and then a chasing one (aiState 3), which is the pair that exercises the branch
        /// order rather than just the thresholds.</para>
        /// </summary>
        [Test]
        public void Brain_WalkAndRun_AreSelectedByState_AndPushedIntoTheAnimator()
        {
            UnitDefinition definition = Zombie0();
            ITileQueryService room = GroundedRoom();

            GameObject zombieGo = CreateZombie(
                definition, OnTheGround(200f), out ZombieController controller,
                out ZombieBrain brain, out UnitAnimator animator);

            controller.SetTileQuery(room);
            brain.Initialize(room, null);

            // Resolve groundedness once. `UnitController.SimTick` is what runs the ground probe
            // (`StepUnit` -> `ResolveGroundState`), and the brain does not do it for the controller.
            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded,
                "precondition: the zombie is seated 1 px above the slab, so the probe must land inside it.");

            Assert.AreEqual("stay", animator.StateName,
                "Unit.as:2860 — a fresh unit starts in `stay`, and dx is 0 so the oracle keeps it there.");

            // ── Patrol: aiState 1, dx = walkSpeed = 1.5 px/frame -> `walk` ────────────────────
            brain.SetState(EnemyAIState.Patrol);
            brain.SimTick(2);

            Assert.AreEqual("walk", animator.StateName,
                "UnitZombie.as:276-297 — dx 1.5 is outside (-1, 1), aiState is 1 and dx is under 6, so " +
                "the oracle draws `walk`. `trot` here would mean the aiState-2 test fired on aiState 1.");
            Assert.AreEqual(definition.WalkSpeed, controller.VelocityPixelsPerFrame.x, 1e-3f,
                "the brain commands px/frame; a 0.3x error means the motor/velocity unit schism came back.");

            int walkCell = animator.CurrentCellIndex;
            Assert.GreaterOrEqual(walkCell, 0, "zombie0's `walk` is row 7, len 24 — it must be addressable.");

            // The `walk` row replays, so the drawn cell must actually move. This is the difference
            // between "the animator was told a state" and "the zombie animates".
            animator.AdvanceFrame();
            animator.AdvanceFrame();
            Assert.AreNotEqual(walkCell, animator.CurrentCellIndex,
                "zombie0's `walk` row is 24 cells with rep='1', so two frames must change the cell.");

            // ── CombatChase: aiState 3 -> `run`, whatever the speed ────────────────────────────
            var targetGo = CreateGameObject("TargetPlayer");
            targetGo.transform.position = OnTheGround(300f);
            targetGo.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            var targetCollider = targetGo.AddComponent<BoxCollider2D>();
            targetCollider.size = new Vector2(definition.Width, definition.Height);
            targetCollider.offset = new Vector2(0f, definition.Height * 0.5f);

            var target = targetGo.AddComponent<UnitController>();
            target.Initialize(null, new UnitStats(1000f, 100f));

            brain.SetState(EnemyAIState.CombatChase);
            brain.Blackboard.TargetUnit = target;
            brain.Blackboard.HasLineOfSight = true;
            brain.Blackboard.LastKnownTargetPosition = (Vector2)targetGo.transform.position * 100f;
            brain.Blackboard.AttackCooldownTicks = 0;
            // SetState(CombatChase) rolls a reaction pause from Idle/Patrol/Alert, and TickCombatChase
            // returns early while it runs. The oracle's `shok` is the same hesitation.
            brain.Blackboard.ShockTimerTicks = 0;
            // Arm the awareness budget by hand. `TickCombatChase` now exits on the budget rather than on
            // the sighting, and this fixture sets the chase state directly instead of going through
            // `EnemySensors.Evaluate` (which is where a real sighting arms it — `UnitZombie.as:657`).
            brain.Blackboard.AlertTimerTicks = EnemyAwarenessMath.FullAwarenessTicks;

            brain.SimTick(3);

            Assert.AreEqual(EnemyAIState.CombatChase, brain.CurrentState,
                "precondition: the brain stayed in combat chase rather than bailing to Alert. It bails if " +
                "the awareness budget is not armed (`EnemyAwarenessMath.IsChasing(AlertTimerTicks)`), or " +
                "if a reaction pause is still running.");
            Assert.AreEqual("run", animator.StateName,
                "UnitZombie.as:283-287 — `aiState == 3` is `run` (aiState 3), selected BEFORE the dx>6 " +
                "test, so a chasing zombie at run speed 10 and one at walk speed both draw `run`.");
            Assert.AreEqual(definition.RunSpeed, controller.VelocityPixelsPerFrame.x, 1e-3f,
                "UnitZombie.as:714 — `maxSpeed = runSpeed` while chasing. zombie0's run is 10 " +
                "(AllData.as:788), an absolute px/frame figure, not moveSpeed * runMultiplier = 3.");
        }

        // ── Drop through a one-way platform (AS3 `throu`) ─────────────────────

        /// <summary>World pixel Y of the catwalk's top surface in <see cref="CatwalkRoom"/>.</summary>
        const float CatwalkTopPixels = 160f;

        /// <summary>
        /// The same room as <see cref="GroundedRoom"/>, with a one-way catwalk added at tile row 3.
        ///
        /// <para>ASCII row 0 is the TOP row, so with 8 rows ASCII row <c>r</c> is tile row <c>7 - r</c>
        /// and the catwalk on ASCII row 4 is tile row 3 — top surface <c>(3 + 1) * 40 = 160</c> px. The
        /// floor is ASCII row 7, i.e. tile row 0, top surface 40 px.</para>
        ///
        /// <para><b>The catwalk is 120 px above the floor, and that is deliberate.</b> AS3 forces
        /// <c>throu</c> off within 80 px of the room's floor (<c>UnitZombie.as:934-937</c>), so a catwalk
        /// nearer than that would have the room-floor guard and the drop trigger both in play and neither
        /// of these two tests could say which one answered.</para>
        /// </summary>
        private static ITileQueryService CatwalkRoom()
            => new UnifiedTileQueryService(SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..............#",
                "#..====........#",
                "#..............#",
                "#..............#",
                "################"
            }));

        /// <summary>
        /// Feet 1 px above the catwalk's surface, in world units — the seat
        /// <see cref="UnitGroundProbe.SeatPixels"/> names.
        /// </summary>
        /// <remarks>
        /// 1 px, not 0, and not "just above": <see cref="UnitGroundProbe.ToProbeRectPixels"/> subtracts the
        /// seat before <c>IsOnGround</c> subtracts its own 1 px, so the sample lands 2 px below the feet —
        /// i.e. 1 px <i>inside</i> the catwalk's tile row. A unit placed exactly on the surface would be
        /// sampled on the row boundary, where <c>Mathf.FloorToInt</c> picks the air row above it.
        /// </remarks>
        private static Vector3 OnTheCatwalk(float pixelX)
            => new Vector3(pixelX / 100f, (CatwalkTopPixels + UnitGroundProbe.SeatPixels) / 100f, 0f);

        /// <summary>
        /// Arrange a zombie seated on the catwalk with a live target below it, and
        /// <see cref="EnemyBlackboard.TargetDeltaY"/> set by hand.
        /// </summary>
        private (ZombieController controller, ZombieBrain brain, UnitController target)
            ArrangeZombieOnCatwalkAboveATarget(float targetDeltaY)
            => ArrangeZombieWithTargetDelta(CatwalkRoom(), OnTheCatwalk(180f), targetDeltaY);

        /// <summary>
        /// The shared arrange: a zombie at <paramref name="zombiePosition"/> inside
        /// <paramref name="room"/>, chasing a live target with
        /// <see cref="EnemyBlackboard.TargetDeltaY"/> set by hand.
        /// </summary>
        /// <remarks>
        /// <para><b>The target is placed 2000 px below the zombie and is stationary, on purpose.</b>
        /// <c>EnemySensors.Evaluate</c> writes <c>TargetDeltaY</c> only when a candidate is actually
        /// observed, so a target inside perception range could overwrite the very value under test — and
        /// the fixture would then be measuring the sensor rather than the rule. 2000 px is far outside
        /// <c>VisionRangePixels</c> (480), and a stationary unit has <c>noise == 0</c>, which
        /// <c>NoiseMath.HearingIntensity</c> treats as inaudible at <i>any</i> distance. The hand-set
        /// delta therefore survives the tick — the same reason
        /// <c>Brain_WalkAndRun_AreSelectedByState_AndPushedIntoTheAnimator</c> sets
        /// <c>LastKnownTargetPosition</c> by hand instead of driving the sensors.</para>
        ///
        /// <para><b>Where the target actually is does not matter</b> — only <c>TargetDeltaY</c> is read by
        /// either rule under test. It is placed below rather than above so that the same arrange serves
        /// both directions: the drop-through fixtures set a negative delta and the jump fixtures a
        /// positive one, and neither is a statement about the target's transform.</para>
        ///
        /// <para><b>No motor is added</b>, so <c>EnemyBrain._motor</c> stays null and both the drop flag
        /// and the jump impulse route to the <c>UnitController</c> — which is the live path anyway
        /// (<c>RoomUnitSpawner._useTileMotor</c> is off by default). <c>ZombieBrain</c> is added before
        /// <c>ZombieController</c>, so <c>Awake</c> cannot resolve the controller; <c>Initialize</c> does,
        /// which is the fix documented at <c>EnemyBrain.cs:119-131</c>.</para>
        /// </remarks>
        private (ZombieController controller, ZombieBrain brain, UnitController target)
            ArrangeZombieWithTargetDelta(ITileQueryService room, Vector3 zombiePosition, float targetDeltaY)
        {
            UnitDefinition definition = Zombie0();

            GameObject zombieGo = CreateZombie(
                definition, zombiePosition, out ZombieController controller,
                out ZombieBrain brain, out _);

            controller.SetTileQuery(room);
            brain.Initialize(room, null);

            var targetGo = CreateGameObject("TargetPlayer");
            targetGo.transform.position = zombiePosition + new Vector3(0f, -20f, 0f);
            targetGo.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            var targetCollider = targetGo.AddComponent<BoxCollider2D>();
            targetCollider.size = new Vector2(definition.Width, definition.Height);
            targetCollider.offset = new Vector2(0f, definition.Height * 0.5f);

            var target = targetGo.AddComponent<UnitController>();
            target.Initialize(null, new UnitStats(1000f, 100f));

            brain.SetState(EnemyAIState.CombatChase);
            brain.Blackboard.TargetUnit = target;
            brain.Blackboard.TargetDeltaX = 0f;
            brain.Blackboard.TargetDeltaY = targetDeltaY;
            brain.Blackboard.LastKnownTargetPosition = (Vector2)zombieGo.transform.position * 100f;
            brain.Blackboard.AttackCooldownTicks = 0;
            // SetState rolls a reaction pause coming out of Idle/Patrol/Alert, and TickCombatChase returns
            // early while it runs.
            brain.Blackboard.ShockTimerTicks = 0;
            // A hand-set chase must be ARMED, or TickCombatChase's awareness gate demotes it to Alert on
            // its first tick. The oracle reaches CombatChase only through a sighting / sound / alarm, and
            // each of those arms `aiSpok` (`UnitZombie.as:657/661/453/344`); this fixture deliberately
            // bypasses the sensors (see the remarks above), so it arms by hand — otherwise it would be
            // asserting on a state the brain exits before the rule under test is ever reached.
            brain.Blackboard.AlertTimerTicks = EnemyAwarenessMath.FullAwarenessTicks;

            return (controller, brain, target);
        }

        /// <summary>
        /// The zombie's drop-through wiring, end to end: the brain decides, the controller's ground probe
        /// obeys, and the catwalk stops being a floor.
        ///
        /// <para><b>This is the half the pure fixture cannot reach.</b>
        /// <c>UnitDropThroughMathTests</c> pins the 80 px band, the sign and the room-floor guard as
        /// arithmetic. What it cannot see is whether anything <i>calls</i> them: whether
        /// <c>ZombieBrain</c> runs the trigger in the right states, whether
        /// <c>EnemyBrain.SetDropThroughPlatforms</c> routes to the layer that is actually stepping the
        /// unit, and whether the brain's tick really precedes the unit's. Every one of those is silent
        /// when wrong — the zombie simply keeps standing on the catwalk, which is the reported bug.</para>
        ///
        /// <para><b>OWNER-ONLY — this test has never been executed.</b> It builds <c>GameObject</c>s and
        /// drives <c>MonoBehaviour</c>s, so the offline reflection wall cannot run it; it needs the Unity
        /// Test Runner (EditMode). It was written from the patterns of the passing tests above
        /// (<c>GroundedRoom</c>, <c>CreateZombie</c>, <c>controller.SetTileQuery</c>,
        /// <c>brain.Initialize</c>, <c>controller.SimTick</c>), not verified. If it fails, the message on
        /// each assertion names which link of the chain to check first.</para>
        /// </summary>
        [Test]
        public void Brain_TargetFarBelow_MakesTheCatwalkStopBeingGround()
        {
            (ZombieController controller, ZombieBrain brain, _) =
                ArrangeZombieOnCatwalkAboveATarget(-200f);

            // Precondition. Without this, the assertion at the bottom would pass for the trivial reason
            // that the unit was never standing on the catwalk in the first place.
            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded,
                "precondition: the zombie is seated 1 px above the catwalk, so the ground probe must " +
                "find it. If this fails the ROOM or the seat is wrong, not the drop.");

            // The order is the production order: the brain ticks at SimTickOrder.UnitMotor - 1, i.e.
            // before the unit's own step.
            brain.SimTick(2);
            Assert.AreEqual(EnemyAIState.CombatChase, brain.CurrentState,
                "precondition: the brain must still be chasing, or UpdateDropThroughPlatforms' trigger " +
                "branch (Alert || CombatChase) never ran.");

            controller.SimTick(3);

            Assert.IsFalse(controller.IsGrounded,
                "UnitZombie.as:857-864 — the target is 200 px below, past the 80 px band, so `throu` is " +
                "set and the catwalk stops being ground: the unit is airborne and gravity takes it " +
                "through. A grounded zombie here means the flag never reached the ground probe — check " +
                "EnemyBrain.SetDropThroughPlatforms' routing, ZombieBrain.UpdateDropThroughPlatforms' " +
                "state guard, and the brain-before-unit tick order, in that order.");
        }

        /// <summary>
        /// The control for the test above: the same arrange with one variable changed, and the opposite
        /// answer.
        /// </summary>
        /// <remarks>
        /// A single test asserting "the zombie falls" cannot distinguish the drop rule from a unit that
        /// was never grounded, or from one that falls for some unrelated reason. Two tests that differ
        /// only in <c>TargetDeltaY</c> can: whatever else is true of this fixture, it is true of both.
        /// </remarks>
        [Test]
        public void Brain_TargetOnlySlightlyBelow_LeavesTheCatwalkAsGround()
        {
            (ZombieController controller, ZombieBrain brain, _) =
                ArrangeZombieOnCatwalkAboveATarget(-10f);

            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded,
                "precondition: seated on the catwalk — identical to the test above.");

            brain.SimTick(2);
            Assert.AreEqual(EnemyAIState.CombatChase, brain.CurrentState,
                "precondition: still chasing, so the trigger branch ran and declined.");

            controller.SimTick(3);

            Assert.IsTrue(controller.IsGrounded,
                "UnitZombie.as:861-863 — 10 px below is inside the 80 px band, so `throu` is cleared and " +
                "the catwalk stays a floor. The zombie keeps chasing along it instead of dropping " +
                "through, which is what 'the target is on a step below me' looks like.");
        }

        // ── Jump (AS3 `jumpdy` / `checkJump`) ─────────────────────────────────

        /// <summary>
        /// A 16 x 8 room with a solid floor at tile row 0 and nothing else inside — <b>tall enough that
        /// the headroom probe is clear</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>Eight rows, not <see cref="GroundedRoom"/>'s five, and the reason is the test.</b>
        /// AS3's <c>checkJump</c> probes 85 and 125 px above the unit's feet
        /// (<c>UnitZombie.as:467-482</c>). A unit seated on the floor at 41 px therefore samples 126 and
        /// 166 px — and in a 5-row room (200 px tall) the second of those lands in tile row 4, which is
        /// the ceiling. The zombie would then be refused for the <i>right</i> reason and the test would
        /// read as "the jump is broken". 8 rows put the ceiling at 280 px, clear of both probes.</para>
        /// </remarks>
        private static ITileQueryService JumpRoom()
            => new UnifiedTileQueryService(SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..............#",
                "#..............#",
                "#..............#",
                "#..............#",
                "################"
            }));

        /// <summary>Feet 1 px above <see cref="JumpRoom"/>'s floor, in world units.</summary>
        private static Vector3 OnTheJumpRoomFloor(float pixelX)
            => new Vector3(pixelX / 100f, (TileSize + 1f) / 100f, 0f);

        /// <summary>
        /// The zombie's jump wiring, end to end: the brain decides, and the controller is given an upward
        /// velocity.
        ///
        /// <para><b>This is the half the pure fixture cannot reach.</b> <c>UnitJumpMathTests</c> pins the
        /// 40 px band, the sign, the probe geometry and the cooldown as arithmetic. What it cannot see is
        /// whether anything <i>calls</i> them: whether <c>ZombieBrain</c> runs the trigger in the right
        /// states, whether <c>EnemyBrain.JumpVertical</c> routes to the layer that is actually stepping
        /// the unit, and whether the brain's tick really precedes the unit's. Every one of those is silent
        /// when wrong — the zombie simply never leaves the ground, which is the reported bug.</para>
        ///
        /// <para><b>OWNER-ONLY — this test has never been executed.</b> It builds <c>GameObject</c>s and
        /// drives <c>MonoBehaviour</c>s, so the offline reflection wall cannot run it; it needs the Unity
        /// Test Runner (EditMode). It was written from the patterns of the passing tests above, not
        /// verified. If it fails, the message on each assertion names which link of the chain to check
        /// first.</para>
        /// </summary>
        [Test]
        public void Brain_TargetFarAbove_MakesTheZombieJump()
        {
            (ZombieController controller, ZombieBrain brain, _) =
                ArrangeZombieWithTargetDelta(JumpRoom(), OnTheJumpRoomFloor(180f), +150f);

            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded,
                "precondition: seated on the floor, so AS3's `stay` gate inside jump() is satisfied. " +
                "Without this the test could pass on a unit that was never allowed to jump at all.");
            Assert.That(controller.Velocity.y, Is.EqualTo(0f).Within(0.0001f),
                "precondition: a resting unit has no vertical velocity, so anything below is the jump.");

            // An ODD tick index on purpose: the oracle's `aiTCh % 2 == 1` parity gate is ported as the
            // sim tick's parity, so an even index would decline for a reason that has nothing to do with
            // the jump.
            brain.SimTick(3);

            Assert.AreEqual(EnemyAIState.CombatChase, brain.CurrentState,
                "precondition: still chasing, so UpdateJump's state guard let it through.");

            Assert.Greater(controller.Velocity.y, 0f,
                "UnitZombie.as:839-842 with :388-399 — the target is 150 px above the eye, past the " +
                "40 px band (unit top 111, target centre 226), the headroom is clear and the cooldown is " +
                "spent, so the zombie must have been given an UPWARD velocity. AS3 writes " +
                "`dy = -jumpdy` in a down-positive Y; the port's Y is up-positive, so the impulse is " +
                "positive here — a NEGATIVE value means the oracle's sign was carried across the axis " +
                "and the zombie is being driven into the floor. ZERO means the request never reached " +
                "the controller: check EnemyBrain.JumpVertical's routing, then UpdateJump's state " +
                "guard, then the parity gate, in that order.");
        }

        /// <summary>
        /// The control for the test above: the same arrange with one variable changed, and the opposite
        /// answer.
        /// </summary>
        /// <remarks>
        /// A single test asserting "the zombie jumps" cannot distinguish the 40 px band from a unit that
        /// jumps unconditionally. Two tests that differ only in <c>TargetDeltaY</c> can: whatever else is
        /// true of this fixture — the room, the seat, the facing, the headroom, the state — it is true of
        /// both.
        /// </remarks>
        [Test]
        public void Brain_TargetOnlySlightlyAbove_LeavesTheZombieGrounded()
        {
            (ZombieController controller, ZombieBrain brain, _) =
                ArrangeZombieWithTargetDelta(JumpRoom(), OnTheJumpRoomFloor(180f), +10f);

            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded,
                "precondition: seated on the floor — identical to the test above.");

            brain.SimTick(3);
            Assert.AreEqual(EnemyAIState.CombatChase, brain.CurrentState,
                "precondition: still chasing, so the gate ran and declined.");

            Assert.That(controller.Velocity.y, Is.EqualTo(0f).Within(0.0001f),
                "UnitZombie.as:683 — 10 px above the eye is inside the 40 px band (`aiVNapr == 0`), so " +
                "there is no jump. Note this is the boundary's other side from the DROP: 10 px is well " +
                "inside both bands, so neither vertical reaction fires and the zombie keeps walking. " +
                "The two tests differ only in TargetDeltaY (+150 against +10).");
        }

        /// <summary>
        /// The headroom probe refuses the jump under a ceiling — the half of <c>checkJump()</c> the pure
        /// fixture can only assert as geometry.
        /// </summary>
        /// <remarks>
        /// The pair with <see cref="Brain_TargetFarAbove_MakesTheZombieJump"/> is deliberate: the two use
        /// the <b>same</b> <c>TargetDeltaY</c> (+150) and differ only in the room, so the answer can only
        /// come from the geometry above the unit's head. That is what makes this evidence about the probe
        /// rather than about the trigger.
        /// </remarks>
        [Test]
        public void Brain_CeilingAbove_RefusesTheJump()
        {
            (ZombieController controller, ZombieBrain brain, _) =
                ArrangeZombieWithTargetDelta(CatwalkRoom(), OnTheCatwalk(180f), +150f);

            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded,
                "precondition: seated 1 px above the catwalk, so the `stay` gate is satisfied.");

            brain.SimTick(3);
            Assert.AreEqual(EnemyAIState.CombatChase, brain.CurrentState,
                "precondition: still chasing, so the state guard let it through.");

            Assert.That(controller.Velocity.y, Is.EqualTo(0f).Within(0.0001f),
                "UnitZombie.as:465-484 — the same +150 delta that jumps in JumpRoom must NOT jump here. " +
                "The feet are at 161 px, so the two headroom probes land at 246 and 286; the catwalk " +
                "room is 8 rows (320 px) and tile row 7 is its ceiling, so the 125 px probe is solid " +
                "and checkJump() refuses. A jump here means the headroom probe is not being asked, or " +
                "is asking the wrong height — and the symptom in play would be a zombie clipping into " +
                "ceilings it has no room to clear.");
        }

        /// <summary>
        /// A dead zombie must draw the death row instead of freezing on its last walking frame.
        ///
        /// <para><c>EnemyController.OnDeath</c> used to do nothing but stop movement, so the corpse kept
        /// whatever cell it died on — which is indistinguishable from "the animator is broken". The
        /// oracle selects the death row from <c>sost</c> inside the same <c>animate()</c> that sees the
        /// unit die (<c>UnitZombie.as:234-251</c>), and the port does it from
        /// <c>SetState(Dead)</c> for the same reason: a unit unregistered from the sim, or one whose next
        /// tick is 33 ms away, would otherwise hold the walking frame.</para>
        /// </summary>
        [Test]
        public void Brain_Death_PushesTheDeathRowIntoTheAnimator()
        {
            UnitDefinition definition = Zombie0();
            GameObject zombieGo = CreateZombie(
                definition, Vector3.zero, out ZombieController controller,
                out ZombieBrain brain, out UnitAnimator animator);
            brain.Initialize(null, null);

            Assert.AreEqual("stay", animator.StateName);

            controller.TakeDamage(definition.health + 10f);

            Assert.IsFalse(controller.IsAlive);
            Assert.AreEqual(EnemyAIState.Dead, brain.CurrentState);
            Assert.AreEqual("death", animator.StateName,
                "UnitZombie.as:252-255 — dead and NOT grounded is `death`. Without this the corpse holds " +
                "the frame it died on, which reads as a frozen sprite rather than a death.");
        }

        /// <summary>
        /// The oracle's three-row death sequence, and the branch that decides between them:
        /// <c>die</c> while the body is on the ground, <c>death</c> while it is airborne, and
        /// <c>fall</c> once it lands having already drawn <c>death</c>.
        ///
        /// <para><b>This is a branch-order test, and it is the one that would silently rot.</b>
        /// <c>UnitZombie.as:234-251</c> reads its <i>own</i> <c>animState</c> to choose between
        /// <c>die</c> and <c>fall</c>, so the sequence is stateful: collapsing it to "always
        /// <c>die</c>" looks like a simplification and loses the fall. Step 3 below forces
        /// <c>animState = "death"</c> to stand in for the airborne tick, because a Kinematic
        /// <c>Rigidbody2D</c> in EditMode never actually leaves the ground — the assertion is about the
        /// rule, not about the physics that would trigger it.</para>
        /// </summary>
        [Test]
        public void Brain_DeathOnTheGround_DrawsDie_ThenFallsOnceTheCorpseHasDrawnDeath()
        {
            UnitDefinition definition = Zombie0();
            ITileQueryService room = GroundedRoom();

            GameObject zombieGo = CreateZombie(
                definition, OnTheGround(200f), out ZombieController controller,
                out ZombieBrain brain, out UnitAnimator animator);

            controller.SetTileQuery(room);
            brain.Initialize(room, null);
            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded, "precondition: the zombie is standing on the slab.");

            // 1. Dies on the ground -> `die`, because it has not drawn `death` yet.
            controller.TakeDamage(definition.health + 10f);
            Assert.AreEqual("die", animator.StateName,
                "grounded death with animState != \"death\" is `die` (UnitZombie.as:238-249).");

            // 2. The corpse's airborne tick. Forced, because EditMode physics never lifts it.
            animator.SetState("death");

            // 3. ...and now the same grounded branch must choose `fall`.
            brain.SimTick(2);

            Assert.AreEqual("fall", animator.StateName,
                "UnitZombie.as:242-244 — `if(animState == \"death\") animState = \"fall\"`. Returning " +
                "`die` here means the self-referential branch was flattened.");
        }

        // ── Ledge sense (AS3 `shX1` / `shX2`) ────────────────────────────────

        /// <summary>
        /// A slab covering tiles 0..3 with air beyond — a lip at 160 px, and nothing to land on 80 px
        /// ahead. The zombie is placed with its right half past that lip, which is the state every ledge
        /// site reads.
        /// </summary>
        private static ITileQueryService RightLedgeRoom()
            => new UnifiedTileQueryService(SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..............#",
                "####............"
            }));

        /// <summary>
        /// The same lip with the floor resuming at tile 6 — so the probe 80 px ahead at floor level finds
        /// something to hop onto. The two rooms differ in that one tile and nothing else, which is what
        /// makes the hop/turn pair evidence about the probe rather than about the room.
        /// </summary>
        /// <remarks>
        /// The probe sits 80 px ahead (two tiles) <i>at floor level</i> — AS3's <c>Y + 10</c>, ten pixels
        /// below the feet, which is inside the row the zombie is standing on. So "something to land on" is
        /// a solid or shelf tile in the <b>ground row</b>, not one above it. A crate sitting a tile higher
        /// would not be found, and the zombie would turn.
        /// </remarks>
        private static ITileQueryService LipWithALandingRoom()
            => new UnifiedTileQueryService(SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#..............#",
                "#..............#",
                "####..##########"
            }));

        /// <summary>
        /// Arrange a grounded zombie in <see cref="EnemyAIState.Patrol"/>, facing right, with no target.
        /// </summary>
        /// <remarks>
        /// <para><b>Both facings are set, and that is not redundant.</b> <c>ZombieBrain.HandleLedgeAhead</c>
        /// reads the <i>controller's</i> facing (<c>_controller.FacingDirection</c>) while
        /// <c>EnemyBrain.TickPatrol</c> moves along the <i>blackboard's</i>. In production they are kept in
        /// step because the unit's step derives the controller's facing from the velocity the move
        /// produced; a fixture that ticks only the brain has to set both, or the branch would be looking at
        /// the wrong side of the body.</para>
        ///
        /// <para><b>No motor is added</b>, so the jump routes to the <c>UnitController</c> — the live path
        /// (<c>RoomUnitSpawner._useTileMotor</c> is off by default).</para>
        /// </remarks>
        private (ZombieController controller, ZombieBrain brain)
            ArrangePatrollingZombie(ITileQueryService room, Vector3 position)
        {
            UnitDefinition definition = Zombie0();

            GameObject zombieGo = CreateZombie(
                definition, position, out ZombieController controller,
                out ZombieBrain brain, out _);

            controller.SetTileQuery(room);
            brain.Initialize(room, null);

            controller.SetFacing(1);
            brain.Blackboard.FacingDirection = 1;
            brain.Blackboard.HasHeardNoise = false;
            brain.SetState(EnemyAIState.Patrol);
            // SetState rolls a reaction pause; the ledge branch does not read it, but the chase tests do.
            brain.Blackboard.ShockTimerTicks = 0;

            return (controller, brain);
        }

        /// <summary>
        /// The patrol's hop: at a lip, with something to land on 80 px ahead, the zombie hops onto it.
        /// </summary>
        /// <remarks>
        /// <para><b>OWNER-ONLY — this test has never been executed.</b> It builds <c>GameObject</c>s and
        /// drives <c>MonoBehaviour</c>s, so the offline reflection wall cannot run it; it needs the Unity
        /// Test Runner (EditMode). It was written from the patterns of the passing tests above
        /// (<c>CreateZombie</c>, <c>controller.SetTileQuery</c>, <c>brain.Initialize</c>,
        /// <c>controller.SimTick</c>), not verified.</para>
        ///
        /// <para><b>Why it rolls in a loop rather than asserting one tick.</b> The crate test is gated on
        /// <c>isrnd(0.1)</c> (<c>UnitZombie.as:791</c>) — one roll in ten per tick — so a single tick
        /// cannot assert the hop; nine times in ten the oracle turns around without even probing. The loop
        /// rolls until the ten-in-one passes, and 300 rolls leave a miss probability of
        /// <c>0.9^300 ≈ 1e-14</c>. Each iteration re-asserts the facing because a turn flips it and,
        /// with no controller tick, the body would stay flipped and the branch would then be looking at
        /// the wrong side.</para>
        /// </remarks>
        [Test]
        public void Brain_PatrollingZombieAtALipWithSomethingToLandOn_Hops()
        {
            (ZombieController controller, ZombieBrain brain) =
                ArrangePatrollingZombie(LipWithALandingRoom(), OnTheGround(170f));

            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded,
                "precondition: the zombie is seated on the slab, so AS3's `stay` gate is satisfied. " +
                "Without this the test could pass for the trivial reason that the unit was never grounded.");
            Assert.That(controller.OverhangRight, Is.GreaterThan(UnitOverhangMath.PatrolEdgeThreshold),
                "precondition: the right half is past the patrol's quarter threshold, so the ledge branch " +
                "runs at all. If this fails the ROOM or the seat is wrong, not the hop.");
            Assert.That(controller.Velocity.y, Is.EqualTo(0f).Within(0.0001f),
                "precondition: a resting unit has no vertical velocity, so anything below is the hop.");

            bool hopped = false;
            for (int i = 0; i < 300 && !hopped; i++)
            {
                controller.SetFacing(1);
                brain.Blackboard.FacingDirection = 1;
                brain.SetState(EnemyAIState.Patrol);
                brain.SimTick(1000 + i);
                hopped = controller.Velocity.y > 0f;
            }

            Assert.IsTrue(hopped,
                "UnitZombie.as:789-798 — the zombie is at a lip and the probe 80 px ahead at floor level " +
                "finds the floor resuming, so `this.jump(0.5)` must fire and the controller must be given " +
                "an UPWARD velocity. ZERO after 300 rolls means the request never reached the controller: " +
                "check ZombieBrain.HandleLedgeAhead's state guard, then HasHopSurfaceAhead's probe point " +
                "(its vertical offset is SUBTRACTED — see UnitOverhangMath.AheadProbePoint), then the " +
                "facing the branch reads, in that order. A NEGATIVE value means the oracle's " +
                "down-positive sign was carried across the axis.");
        }

        /// <summary>
        /// The patrol's turn — the control for the hop above, and <b>deterministic</b>: whether or not the
        /// one-in-ten crate roll passes, the probe finds air ahead, so the zombie turns either way.
        /// </summary>
        [Test]
        public void Brain_PatrollingZombieAtALipWithNothingAhead_TurnsAround()
        {
            (ZombieController controller, ZombieBrain brain) =
                ArrangePatrollingZombie(RightLedgeRoom(), OnTheGround(170f));

            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded, "precondition: seated on the slab.");
            Assert.That(controller.OverhangRight, Is.GreaterThan(UnitOverhangMath.PatrolEdgeThreshold),
                "precondition: the right half is past the lip.");
            Assert.That(controller.FacingDirection, Is.EqualTo(1), "precondition: facing the lip.");

            brain.SimTick(3);

            Assert.That(controller.FacingDirection, Is.EqualTo(-1),
                "UnitZombie.as:801-805 and :827-835 — with nothing to hop onto, `turnX` is set and " +
                "`aiNapr = storona = turnX` applies it. The zombie turns away from the lip on the SAME " +
                "tick, before the move, which is what stops it stepping off. An unchanged facing means the " +
                "ledge branch never ran: check the `shX > 0.25` gate and the probe point's sign.");

            Assert.That(controller.Velocity.y, Is.EqualTo(0f).Within(0.0001f),
                "...and it must NOT hop — the crate test failed, so no jump was requested.");
        }

        /// <summary>
        /// The chase's hop: at a lip, with the target level, the chase takes its half hop.
        /// </summary>
        /// <remarks>
        /// <para><b>The threshold is double the patrol's</b> — <c>shX2 &gt; 0.5</c> (<c>UnitZombie.as:890</c>)
        /// against the patrol's <c>0.25</c> — so the same fixture used above would not fire here, and the
        /// precondition asserts the 0.5 gate rather than the 0.25 one.</para>
        ///
        /// <para><b>OWNER-ONLY</b>, as above. The hop is <c>isrnd(0.5)</c>, a coin flip per tick, so the
        /// loop rolls until it lands; 60 flips leave a miss probability of <c>0.5^60 ≈ 1e-18</c>. The loop
        /// is kept short because the chase is bounded by the awareness budget, not because it is short of
        /// <c>Alert</c> on a timer: the arrangement here deliberately never sights the target, so
        /// <c>TickCombatChase</c> runs on the hand-armed budget alone
        /// (<c>EnemyAwarenessMath.FullAwarenessTicks</c> = 400 ticks), and 60 flips stay well inside it.</para>
        /// </remarks>
        [Test]
        public void Brain_ChasingZombieAtALipWithALevelTarget_Hops()
        {
            // The lip is on the right, and TickCombatChase faces the zombie along
            // LastKnownTargetPosition — which the arrange puts at the zombie's own x, so the chase
            // direction is right.
            (ZombieController controller, ZombieBrain brain, _) =
                ArrangeZombieWithTargetDelta(RightLedgeRoom(), OnTheGround(170f), 0f);

            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded, "precondition: seated on the slab.");
            Assert.That(controller.OverhangRight, Is.GreaterThan(UnitOverhangMath.ChaseEdgeThreshold),
                "precondition: MORE THAN HALF the body is past the lip — the chase's threshold is double " +
                "the patrol's, so a quarter-overhang fixture would not fire here.");

            bool hopped = false;
            for (int i = 0; i < 60 && !hopped; i++)
            {
                brain.SimTick(2000 + i);
                hopped = controller.Velocity.y > 0f;
            }

            Assert.IsTrue(hopped,
                "UnitZombie.as:890-895 — at a lip with the target level (`aiVNapr <= 0`, so the " +
                "target-overhead jump declines) the chase takes `_loc2_ = 0.5` on a coin flip. ZERO after " +
                "60 flips means the branch never ran: check the 0.5 gate, then that the state is still " +
                "CombatChase (a shock timer or a lost target sends it to Alert), then the `ShouldJump` " +
                "guard — a target reported ABOVE the unit's top returns early by design.");
        }

        /// <summary>
        /// Well inside a slab the ledge branch does nothing at all — the control that says the hop and the
        /// turn above are about the <b>edge</b> and not about walking.
        /// </summary>
        [Test]
        public void Brain_PatrollingZombieWellInsideASlab_NeitherHopsNorTurns()
        {
            (ZombieController controller, ZombieBrain brain) =
                ArrangePatrollingZombie(GroundedRoom(), OnTheGround(200f));

            controller.SimTick(1);
            Assert.IsTrue(controller.IsGrounded, "precondition: on the slab.");
            Assert.That(controller.OverhangRight, Is.LessThanOrEqualTo(UnitOverhangMath.PatrolEdgeThreshold),
                "precondition: the body is entirely inside the slab, so the ledge branch must not run. " +
                "Without this the assertions below could pass for the wrong reason.");
            Assert.That(controller.FacingDirection, Is.EqualTo(1), "precondition: facing right.");

            // A run of ticks, because the gate is `shX` and not a roll — the answer is the same every tick.
            for (int i = 0; i < 30; i++)
            {
                controller.SetFacing(1);
                brain.Blackboard.FacingDirection = 1;
                brain.SetState(EnemyAIState.Patrol);
                brain.SimTick(3000 + i);
            }

            Assert.That(controller.FacingDirection, Is.EqualTo(1),
                "no edge toward the facing, so `shX2 > 0.25` is false and the turn must not fire. A turn " +
                "here means the overhang is being read from the wrong side or against the wrong " +
                "threshold — in play it would look like a zombie that cannot walk in a straight line.");
            Assert.That(controller.Velocity.y, Is.EqualTo(0f).Within(0.0001f), "...and no hop.");
        }

        [Test]
        public void RoomUnitSpawner_ResolvesZombieController_ByBothIds()
        {
            // Verify RoomUnitSpawner maps both AS3 class names correctly
            var spawner = new RoomUnitSpawner(null, null);

            // Using reflection to inspect private ResolveControllerType
            var method = typeof(RoomUnitSpawner).GetMethod("ResolveControllerType",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            Assert.IsNotNull(method, "ResolveControllerType method should exist on RoomUnitSpawner");

            var typeFromClass = (System.Type)method.Invoke(spawner, new object[] { "UnitZombie" });
            Assert.AreEqual(typeof(ZombieController), typeFromClass,
                "'UnitZombie' must resolve to ZombieController");

            var typeFromAlias = (System.Type)method.Invoke(spawner, new object[] { "zombie" });
            Assert.AreEqual(typeof(ZombieController), typeFromAlias,
                "'zombie' alias must resolve to ZombieController");
        }
    }
}
