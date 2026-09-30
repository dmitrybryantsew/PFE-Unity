using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Tests.Editor.Map.Rendering
{
    /// <summary>
    /// Pins the producer the unit layer was missing: <c>room.units</c> → live GameObjects.
    ///
    /// <para>Before <see cref="RoomUnitSpawner"/>, nothing in the repo instantiated a
    /// <c>UnitController</c> at all — every reference was a <c>GetComponent&lt;UnitController&gt;()</c> in
    /// a projectile or presenter. So authored enemies were data that was never drawn, which is why the
    /// camp's training dummies stayed invisible even after they were correctly classified as units.</para>
    ///
    /// <para>The tests below are grouped by the four things the spawner has to get right: how many
    /// objects exist, which controller they get, where they land, and what the dummy does once it is
    /// alive. Each has a complement, so a rule that over-applies fails here.</para>
    /// </summary>
    [TestFixture]
    public class RoomUnitSpawnerTests
    {
        GameObject _root;
        Transform _parent;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("RoomUnitSpawnerTests");
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
            }
        }

        // ── fixtures ─────────────────────────────────────────────────────────────────────────────

        /// <summary>An in-memory unit table, so the tests never depend on what is in <c>Resources</c>.</summary>
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

        private static UnitDefinition MakeDefinition(string id, int health, float width = 0.55f, float height = 0.70f)
        {
            var definition = ScriptableObject.CreateInstance<UnitDefinition>();
            definition.id = id;
            definition.health = health;
            definition.width = width;
            definition.height = height;
            return definition;
        }

        private static RoomInstance MakeRoom(string id = "spawner_room")
        {
            var room = new RoomInstance
            {
                id = id,
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
            };
            room.InitializeTiles();
            return room;
        }

        private static UnitInstance MakeUnit(
            string unitId,
            Vector2 position,
            string controllerId = "",
            int facingDirection = 1,
            params (string key, string value)[] attributes)
        {
            var unit = new UnitInstance
            {
                unitId = unitId,
                unitType = unitId,
                entityId = unitId,
                position = position,
                controllerId = controllerId,
                facingDirection = facingDirection,
                maxHealth = 100f,
                currentHealth = 100f
            };

            for (int i = 0; i < attributes.Length; i++)
            {
                unit.attributes.Add(new MapObjectAttributeData
                {
                    key = attributes[i].key,
                    value = attributes[i].value
                });
            }

            return unit;
        }

        /// <summary>The one dummy definition the Camp's test ground actually needs.</summary>
        private static FakeUnitDefinitions TrainingDummyProvider(int health = 500)
        {
            return new FakeUnitDefinitions().Add(MakeDefinition("training", health, 2f, 2f));
        }

        // ── how many objects exist ───────────────────────────────────────────────────────────────

        [Test]
        public void RefreshAll_SpawnsOneGameObjectPerUnit()
        {
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f)));
            room.units.Add(MakeUnit("training", new Vector2(300f, 200f)));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            Assert.AreEqual(2, spawner.SpawnedCount);
            Assert.AreEqual(2, _parent.childCount);
        }

        [Test]
        public void RefreshAll_IsIdempotent_SoARefreshDoesNotDuplicateTheRoom()
        {
            // RefreshAll is called from UpdateVisuals and RefreshSprites, i.e. often. If it appended
            // instead of syncing, every repaint would double the room's enemies.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f)));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();
            spawner.RefreshAll();
            spawner.RefreshAll();

            Assert.AreEqual(1, spawner.SpawnedCount);
            Assert.AreEqual(1, _parent.childCount);
        }

        [Test]
        public void RefreshAll_DestroysTheGameObject_WhenTheUnitLeavesTheRoom()
        {
            // The complement of the spawn test: a room's unit list is mutable (units die, save data is
            // restored), so a sync that only ever adds would leave corpses behind.
            RoomInstance room = MakeRoom();
            UnitInstance survivor = MakeUnit("training", new Vector2(100f, 200f));
            UnitInstance removed = MakeUnit("training", new Vector2(300f, 200f));
            room.units.Add(survivor);
            room.units.Add(removed);

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();
            Assert.AreEqual(2, spawner.SpawnedCount);

            room.units.Remove(removed);
            spawner.RefreshAll();

            Assert.AreEqual(1, spawner.SpawnedCount, "The removed unit must be despawned.");
            Assert.AreEqual(1, _parent.childCount, "Its GameObject must be gone, not just forgotten.");
        }

        [Test]
        public void DestroyAll_RemovesEverySpawnedObject_AndLeavesNothingBehind()
        {
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f)));
            room.units.Add(MakeUnit("training", new Vector2(300f, 200f)));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();
            spawner.DestroyAll();

            Assert.AreEqual(0, spawner.SpawnedCount);
            Assert.AreEqual(0, _parent.childCount);
        }

        // ── which controller they get ────────────────────────────────────────────────────────────

        [Test]
        public void Spawn_UsesThePlacementsControllerId_SoTheDummyGetsItsOwnController()
        {
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f), controllerId: "UnitTrain"));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            // GetComponent<UnitController>() returns the most-derived type, so asserting the concrete
            // subclass is what proves the controllerId was honoured rather than a base controller added.
            Assert.IsNotNull(_parent.GetChild(0).GetComponent<TrainingDummyController>(),
                "controllerId 'UnitTrain' must resolve to the dummy controller.");
        }

        [Test]
        public void Spawn_WithNoControllerId_GetsTheBaseController_NotTheDummy()
        {
            // The complement: units placed by the random-enemy pass have no authored `cl`, so they must
            // NOT silently become training dummies. Without this, a resolver that ignored the id and
            // always returned the first registered type would still pass the test above.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f), controllerId: string.Empty));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            Assert.IsNotNull(_parent.GetChild(0).GetComponent<UnitController>());
            Assert.IsNull(_parent.GetChild(0).GetComponent<TrainingDummyController>(),
                "A unit with no `cl` must not be a dummy.");
        }

        [Test]
        public void Spawn_WithAnUnknownNamedController_StillSpawns_AndFallsBackToTheBase()
        {
            // 44 of AS3's ~45 unit controller classes are unported. A named-but-unknown controller is a
            // porting gap: the unit must still appear (so the gap is visible in play) on the base brain.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("raider", new Vector2(100f, 200f), controllerId: "UnitRaider"));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            Assert.AreEqual(1, spawner.SpawnedCount, "An unported controller must not drop the unit.");
            Assert.IsNotNull(_parent.GetChild(0).GetComponent<UnitController>());
            Assert.IsNull(_parent.GetChild(0).GetComponent<TrainingDummyController>());
        }

        // ── where they land ──────────────────────────────────────────────────────────────────────

        [Test]
        public void Spawn_PlacesTheUnitAtItsAuthoredPixelPosition()
        {
            // 100 pixels = 1 Unity unit (WorldCoordinates.PixelToUnity), so these are literal
            // expectations, not the conversion recomputed in the test body.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(400f, 200f)));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            Vector3 localPosition = _parent.GetChild(0).localPosition;
            Assert.AreEqual(4f, localPosition.x, 0.001f);
            Assert.AreEqual(2f, localPosition.y, 0.001f);
        }

        [Test]
        public void Spawn_SizesTheColliderFromTheDefinition_NotFromTheColliderDefault()
        {
            // This is the assertion that proves Initialize() ran AFTER Awake(). AddComponent runs Awake
            // with _stats == null, so the collider sizing there is a no-op; if Initialize did not repeat
            // it, the 2x2 dummy would keep the BoxCollider2D default of 1x1 and be half its authored size.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f)));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            var collider = _parent.GetChild(0).GetComponent<BoxCollider2D>();
            Assert.IsNotNull(collider);
            Assert.AreEqual(2f, collider.size.x, 0.001f);
            Assert.AreEqual(2f, collider.size.y, 0.001f);
        }

        [Test]
        public void Spawn_PutsTheUnitOnThePhysicalObjectSortingLayer_OrderedByDepth()
        {
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 100f))); // lower on screen
            room.units.Add(MakeUnit("training", new Vector2(100f, 300f))); // higher on screen

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            var lower = _parent.GetChild(0).GetComponent<SpriteRenderer>();
            var higher = _parent.GetChild(1).GetComponent<SpriteRenderer>();

            Assert.AreEqual(MapSortingLayers.BackgroundPhysicalObjects, lower.sortingLayerName);
            Assert.Greater(lower.sortingOrder, higher.sortingOrder,
                "The unit lower on screen must draw in front of the one above it.");
        }

        [Test]
        public void Spawn_AppliesThePlacementsFacing()
        {
            // The facing is resolved by RoomPopulator, not here (see RoomUnitSpawner's ctor comment).
            // This asserts the spawner consumes it, including the mirrored transform.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f), facingDirection: -1));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            var controller = _parent.GetChild(0).GetComponent<TrainingDummyController>();
            Assert.AreEqual(-1, controller.FacingDirection);
            Assert.AreEqual(-1f, _parent.GetChild(0).localScale.x, 0.001f);
        }

        [Test]
        public void Spawn_WithNoDefinition_StillSpawnsAndLandsWhereAuthored()
        {
            // A missing definition must not move the unit to the origin: an enemy that is really there
            // but drawn at (0,0) is far harder to diagnose than one that is visibly at its post.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("not_a_real_unit_id", new Vector2(400f, 200f)));

            var spawner = new RoomUnitSpawner(room, _parent, new FakeUnitDefinitions());
            spawner.RefreshAll();

            Assert.AreEqual(1, spawner.SpawnedCount);
            Vector3 localPosition = _parent.GetChild(0).localPosition;
            Assert.AreEqual(4f, localPosition.x, 0.001f);
            Assert.AreEqual(2f, localPosition.y, 0.001f);
        }

        // ── what the dummy does once it is alive ─────────────────────────────────────────────────

        [Test]
        public void Dummy_Tr1_SelectsTheArmoredVariant()
        {
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f), controllerId: "UnitTrain",
                attributes: new[] { ("tr", "1") }));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            var controller = _parent.GetChild(0).GetComponent<TrainingDummyController>();
            Assert.IsTrue(controller.IsArmored);
            Assert.AreEqual(TrainingDummyController.ArmoredVisualClassName, controller.VisualClassName);
            Assert.AreEqual(TrainingDummyController.ArmoredSkin, controller.SkinResistance, 1e-4f,
                "UnitTrain.as:44 raises skin to 20 for the armoured variant. Asserted on " +
                "SkinResistance — the IDamageable member DamageSystem reads — not on UnitStats, " +
                "because the whole point of the A7b shape is that the value has to survive the trip " +
                "from the controller to the resolver.");
        }

        [Test]
        public void Dummy_WithNoTrAttribute_IsThePlainVariant()
        {
            // The complement: `tr` absent means unarmoured (AS3 compares @tr == 1), so a rule that
            // defaulted to armoured would still pass the test above while making 3 of the camp's 5
            // dummies armoured when only 2 are.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f), controllerId: "UnitTrain"));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            var controller = _parent.GetChild(0).GetComponent<TrainingDummyController>();
            Assert.IsFalse(controller.IsArmored);
            Assert.AreEqual(TrainingDummyController.PlainVisualClassName, controller.VisualClassName);
            Assert.AreEqual(0f, controller.SkinResistance, 1e-4f,
                "The plain dummy has no skin: AllData.as declares <comb hp='500' armor='0' " +
                "marmor='0'/> with no skin attribute, and only the tr='1' branch raises it. " +
                "Without this the two variants could differ by 20 and a test on the armoured one " +
                "alone would not notice.");
        }

        [Test]
        public void Dummy_HealthComesFromTheUnitDefinition_NotFromAGuessTable()
        {
            // The definition says 500. The deleted CalculateUnitHealth had no "training" case and fell
            // through to 50, so this is the assertion that pins the fix.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f), controllerId: "UnitTrain"));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider(health: 500));
            spawner.RefreshAll();

            var controller = _parent.GetChild(0).GetComponent<TrainingDummyController>();
            Assert.AreEqual(500f, controller.MaxHealth, 0.001f);
            Assert.AreEqual(500f, controller.CurrentHealth, 0.001f);
        }

        [Test]
        public void Dummy_SurvivesALethalHit_BecauseOnDeathRestoresIt()
        {
            // UnitTrain.as:65-68 overrides die() to hp = maxhp, so the dummy cannot die by construction.
            // The port reaches death through UnitController.OnDeath, and TakeDamage calls it
            // synchronously — so this runs in EditMode without needing FixedUpdate.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f), controllerId: "UnitTrain"));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider(health: 500));
            spawner.RefreshAll();

            var controller = _parent.GetChild(0).GetComponent<TrainingDummyController>();
            controller.TakeDamage(99999f);

            Assert.IsTrue(controller.IsAlive, "A training dummy must not be killable.");
            Assert.AreEqual(500f, controller.CurrentHealth, 0.001f);
        }

        [Test]
        public void BaseUnit_WithTheSameLethalHit_DoesDie()
        {
            // The complement. Without it, a UnitController whose OnDeath was a no-op for everyone —
            // or a TakeDamage that never reduced health — would still pass the dummy test above.
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f), controllerId: string.Empty));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider(health: 500));
            spawner.RefreshAll();

            var controller = _parent.GetChild(0).GetComponent<UnitController>();
            controller.TakeDamage(99999f);

            Assert.IsFalse(controller.IsAlive, "A plain unit with no die() override must actually die.");
        }
    }
}
