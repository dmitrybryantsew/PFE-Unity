using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Pins Stage C's path-6 re-home: a spawned unit is given a home on the hand-rolled
    /// <see cref="TilePhysicsController"/> motor, behind <c>PfeDebugSettings.UnitMotor</c>.
    ///
    /// <para><b>Why this fixture exists.</b> Path 6 — the legacy Kinematic <c>Rigidbody2D</c> — is the
    /// <i>only</i> integration path any spawned unit has ever had: nothing in the repo added a
    /// <c>TilePhysicsController</c> to one, so the guide's "delete path 6" was unreachable until the
    /// units were given a home. This pins the two things that make the re-home safe, and neither is
    /// visible from a green build.</para>
    ///
    /// <para><b>Hazard 1 — the double step.</b> <c>UnitController</c> decides which driver owns its step
    /// from <c>_hasTilePhysics</c>, which it derives in <c>Awake</c> via
    /// <c>GetComponent&lt;TilePhysicsController&gt;()</c>. <c>Awake</c> runs synchronously inside
    /// <c>AddComponent</c>, so a motor added <i>after</i> the controller is never seen: the predicate
    /// stays false, the unit keeps its own step, and the motor runs too — two integrations per tick,
    /// which presents as a damage-tuning bug rather than as a double tick. The motor must therefore be
    /// added <b>before</b> the controller, and that is asserted directly.</para>
    ///
    /// <para><b>Hazard 2 — two motors in one slot.</b> Every motor used to report
    /// <c>SimTickOrder.PlayerMotor</c>, so "the player moves first" held only by registration luck.
    /// A unit's motor now reports <c>UnitMotor</c>, which is asserted as an ordering relation rather
    /// than as a literal, so renumbering the slots cannot silently break the intent.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitMotorReHomeTests
    {
        GameObject _root;
        Transform _parent;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("UnitMotorReHomeTests");
            var parentObject = new GameObject("Units");
            parentObject.transform.SetParent(_root.transform, false);
            _parent = parentObject.transform;
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                UnityEngine.Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        // ── fixtures ─────────────────────────────────────────────────────────────────────────

        private sealed class FakeUnitDefinitions : IUnitDefinitionProvider
        {
            private readonly Dictionary<string, UnitDefinition> _byId =
                new Dictionary<string, UnitDefinition>(StringComparer.OrdinalIgnoreCase);

            public FakeUnitDefinitions Add(UnitDefinition definition)
            {
                _byId[definition.id] = definition;
                return this;
            }

            public bool TryGetUnit(string unitId, out UnitDefinition definition)
            {
                definition = null;
                return !string.IsNullOrEmpty(unitId) && _byId.TryGetValue(unitId, out definition);
            }
        }

        private static FakeUnitDefinitions Provider(float width = 2f, float height = 3f)
        {
            var definition = ScriptableObject.CreateInstance<UnitDefinition>();
            definition.id = "training";
            definition.health = 500;
            definition.width = width;
            definition.height = height;
            return new FakeUnitDefinitions().Add(definition);
        }

        private static RoomInstance MakeRoomWithOneUnit()
        {
            var room = new RoomInstance { id = "rehome_room", borderOffset = 0 };
            room.InitializeTiles();
            room.units.Add(new UnitInstance
            {
                unitId = "training",
                unitType = "training",
                entityId = "training",
                position = new Vector2(100f, 200f),
                controllerId = string.Empty,
                facingDirection = 1,
                maxHealth = 500f,
                currentHealth = 500f
            });
            return room;
        }

        private GameObject SpawnOne(bool useTileMotor)
        {
            var spawner = new RoomUnitSpawner(
                MakeRoomWithOneUnit(), _parent, Provider(), useTileMotor: useTileMotor);
            spawner.RefreshAll();

            Assert.AreEqual(1, _parent.childCount, "The fixture must produce exactly one unit object.");
            return _parent.GetChild(0).gameObject;
        }

        // ── the ordering relation ────────────────────────────────────────────────────────────

        /// <summary>
        /// The slot a spawned unit's motor reports must sit after the player's motor and before the
        /// other unit systems.
        ///
        /// <para>Asserted as <i>inequalities</i>, not as the literal 25, for the same reason
        /// <c>PhysicsWorldServiceTests.TickOrder_StepsAfterUnitsAndBeforeConsumers</c> is: the intent is
        /// the relation, and a test that pinned the number would have to be edited — silently losing its
        /// meaning — the next time the slots are renumbered.</para>
        /// </summary>
        [Test]
        public void UnitMotor_SlotSitsAfterThePlayerAndBeforeTheOtherUnitSystems()
        {
            Assert.Greater(SimTickOrder.UnitMotor, SimTickOrder.PlayerMotor,
                "The player moves first, so a unit's motor must not share the player's slot.");

            Assert.Greater(SimTickOrder.UnitMotor, SimTickOrder.RoomState,
                "The room's prop step must have run before a unit integrates against it.");

            Assert.Less(SimTickOrder.UnitMotor, SimTickOrder.UnitsAndAi,
                "A unit's brain must be able to decide in a slot at or below its motor, so that the " +
                "motor applies the decision in the SAME tick — AS3 runs control() then run() in one " +
                "pass. A brain above the motor would apply every decision one tick late.");

            Assert.Less(SimTickOrder.UnitMotor, SimTickOrder.PhysicsWorld,
                "The physics world steps after every system that writes a body.");
        }

        /// <summary>
        /// A motor is the player's until it is told otherwise, and the marker is what moves it. Both
        /// directions are asserted, so a motor that reported <c>UnitMotor</c> unconditionally would
        /// fail here rather than by quietly reordering the player against the units.
        /// </summary>
        [Test]
        public void Motor_ReportsThePlayerSlot_UntilItIsMarkedUnitOwned()
        {
            var go = new GameObject("motor");
            go.transform.SetParent(_root.transform, false);
            var motor = go.AddComponent<TilePhysicsController>();

            Assert.AreEqual(SimTickOrder.PlayerMotor, motor.TickOrder,
                "A motor that was never marked owns the player's step, which is what every existing " +
                "motor (Player.prefab's) must keep doing.");

            motor.MarkUnitOwned();

            Assert.AreEqual(SimTickOrder.UnitMotor, motor.TickOrder,
                "Marking the motor as a unit's owner must move it out of the player's slot.");
        }

        // ── the re-home, and the order that makes it safe ────────────────────────────────────

        [Test]
        public void Spawner_WithUnitMotorOn_GivesTheUnitAMotorSizedFromItsDefinition()
        {
            GameObject unit = SpawnOne(useTileMotor: true);

            var motor = unit.GetComponent<TilePhysicsController>();
            Assert.IsNotNull(motor,
                "With UnitMotor on, a spawned unit must get a TilePhysicsController — that is its home " +
                "on the AS3 motor, and without it path 6 has nowhere to migrate to.");

            Assert.AreEqual(SimTickOrder.UnitMotor, motor.TickOrder,
                "A motor built by the spawner drives an NPC, so it must not sit in the player's slot.");

            Assert.AreEqual(2f, motor.CollisionWidth, 0.001f,
                "The motor's collision box must come from the unit's definition (AS3 scX), not from the " +
                "motor's player-shaped serialized default — otherwise it resolves tile collision " +
                "against a box that is not the unit's, which reads as 'it clips into walls'.");

            Assert.AreEqual(3f, motor.CollisionHeight, 0.001f,
                "Same for the height (AS3 scY).");
        }

        /// <summary>
        /// The complement: with the flag off nothing changes, so the old path stays selectable and this
        /// is a one-flag A/B rather than a migration. Without this, "the flag works" would be
        /// indistinguishable from "the motor is always added".
        /// </summary>
        [Test]
        public void Spawner_WithUnitMotorOff_LeavesTheLegacyPathAlone()
        {
            GameObject unit = SpawnOne(useTileMotor: false);

            Assert.IsNull(unit.GetComponent<TilePhysicsController>(),
                "With UnitMotor off the unit must keep the legacy path: no motor, so UnitController's " +
                "own step stays the only driver. This is the Stage C rollback switch.");

            Assert.IsNotNull(unit.GetComponent<Rigidbody2D>(),
                "The legacy Kinematic body is built in BOTH modes — Stage D deletes it, not this flag.");
        }

        /// <summary>
        /// The double-step guard: the motor must be present on the object <i>before</i> the
        /// <c>UnitController</c> is added, because the controller reads
        /// <c>GetComponent&lt;TilePhysicsController&gt;()</c> in <c>Awake</c> and runs that <c>Awake</c>
        /// synchronously inside <c>AddComponent</c>.
        ///
        /// <para>This is the one assertion in the fixture that a passing build cannot give you. If the
        /// order ever flips, the unit's own step and the motor both run every tick, and the symptom is a
        /// doubled step — damage applied twice as often, gravity twice as strong — which reads as a
        /// tuning problem, not as a wiring one.</para>
        /// </summary>
        [Test]
        public void Spawner_AddsTheMotorBeforeTheUnitController()
        {
            GameObject unit = SpawnOne(useTileMotor: true);

            Component[] components = unit.GetComponents<Component>();
            int motorIndex = -1;
            int controllerIndex = -1;

            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] is TilePhysicsController) motorIndex = i;
                if (components[i] is UnitController) controllerIndex = i;
            }

            Assert.Greater(motorIndex, -1, "The unit object must carry a motor when UnitMotor is on.");
            Assert.Greater(controllerIndex, -1, "The unit object must carry a UnitController.");

            Assert.Less(motorIndex, controllerIndex,
                "The motor must be added BEFORE the UnitController. UnitController.Awake derives its " +
                "_hasTilePhysics predicate from GetComponent<TilePhysicsController>(), and Awake runs " +
                "inside AddComponent — so a motor added afterwards is invisible to it, the unit keeps " +
                "its own step, and both integrate every tick.");
        }
    }
}
