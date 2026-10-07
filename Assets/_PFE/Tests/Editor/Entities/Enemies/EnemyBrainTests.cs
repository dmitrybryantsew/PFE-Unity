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

            brain.SimTick(3);

            Assert.AreEqual(EnemyAIState.CombatChase, brain.CurrentState,
                "precondition: the brain stayed in combat chase rather than bailing to Alert.");
            Assert.AreEqual("run", animator.StateName,
                "UnitZombie.as:283-287 — `aiState == 3` is `run` (aiState 3), selected BEFORE the dx>6 " +
                "test, so a chasing zombie at run speed 10 and one at walk speed both draw `run`.");
            Assert.AreEqual(definition.RunSpeed, controller.VelocityPixelsPerFrame.x, 1e-3f,
                "UnitZombie.as:714 — `maxSpeed = runSpeed` while chasing. zombie0's run is 10 " +
                "(AllData.as:788), an absolute px/frame figure, not moveSpeed * runMultiplier = 3.");
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
