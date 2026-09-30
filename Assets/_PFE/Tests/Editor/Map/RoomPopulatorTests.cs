using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    [TestFixture]
    public class RoomPopulatorTests
    {
        /// <summary>Every roll reports <paramref name="value"/>, so the spawn coin flip is decided by the test.</summary>
        private sealed class FixedRng : IRngService
        {
            private readonly float _value;
            public FixedRng(float value) => _value = value;

            public uint  NextUInt() => 0u;
            public float NextFloat() => _value;
            public int   NextInt(int maxExclusive) => 0;
            public int   Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool  Chance(float probability) => _value < probability;
            public void  Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }

        /// <summary>
        /// An in-memory unit table, so these tests never depend on what happens to be in
        /// <c>Resources/Units</c>. Without it, <c>ResolveUnitHealth</c> would silently read the real
        /// <c>training.asset</c> and the test would pass or fail with the data on disk.
        /// </summary>
        private sealed class FakeUnitDefinitions : IUnitDefinitionProvider
        {
            private readonly Dictionary<string, UnitDefinition> _byId =
                new Dictionary<string, UnitDefinition>(System.StringComparer.OrdinalIgnoreCase);

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

        private static UnitDefinition MakeTrainingDefinition(int health = 500)
        {
            var definition = ScriptableObject.CreateInstance<UnitDefinition>();
            definition.id = "training";
            definition.health = health;
            return definition;
        }

        /// <summary>
        /// A definition that also carries the legacy <c>&lt;obj&gt;</c> attributes, because that is the
        /// row AS3 reads the controller class from (<c>Unit.as:702</c> reassigns `node` to
        /// <c>AllData.d.obj.(@id == id)[0]</c>, and <c>:708</c> then reads <c>node.@cl</c>).
        /// </summary>
        private static MapObjectDefinition MakeTrainingObjectDefinition(string controllerClass = "UnitTrain")
        {
            var definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "training";
            definition.family = MapObjectFamily.Unit;
            definition.defaultPlacementType = MapObjectDefinition.GenericPlacementType;
            definition.legacyAttributes.Add(new MapObjectAttributeData { key = "cl", value = controllerClass });
            return definition;
        }

        private static RoomInstance MakeRoom(string id)
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

        private static ObjectSpawnData MakeTrainingPlacement(
            MapObjectDefinition definition,
            params (string key, string value)[] attributes)
        {
            var spawn = new ObjectSpawnData
            {
                id = "training",
                type = MapObjectDefinition.GenericPlacementType,
                definition = definition,
                definitionId = "training",
                tileCoord = new Vector2Int(4, 23)
            };

            for (int i = 0; i < attributes.Length; i++)
            {
                spawn.attributes.Add(new MapObjectAttributeData
                {
                    key = attributes[i].key,
                    value = attributes[i].value
                });
            }

            return spawn;
        }

        [Test]
        public void PopulateRoom_ConvertsLegacyAs3ObjectCoordinatesIntoUnityRoomSpace()
        {
            RoomInstance room = new RoomInstance
            {
                id = "legacy_room",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
            };
            room.InitializeTiles();

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(new ObjectSpawnData
            {
                id = "woodbox",
                type = "box",
                tileCoord = new Vector2Int(4, 23),
                code = "legacy_box"
            });

            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            ObjectInstance spawned = room.objects.Find(obj => obj != null && obj.code == "legacy_box");
            Assert.NotNull(spawned);
            Assert.AreEqual(180f, spawned.position.x, 0.01f);

            // AS3 Location.createObj(): y = (ny + 1) * Tile.tileY - 1 with the TOP-DOWN row ny = 23
            // => 959 px below the room's top edge => 41 px above the bottom of a 1000 px room.
            // (This used to read 121: the same formula against the old, wrong 27-row room height.)
            float as3Y = (23 + 1) * WorldConstants.TILE_SIZE - 1;
            float expectedY = room.height * WorldConstants.TILE_SIZE - as3Y;
            Assert.AreEqual(41f, expectedY, 0.01f);
            Assert.AreEqual(expectedY, spawned.position.y, 0.01f);

            Object.DestroyImmediate(template);
        }

        [Test]
        public void PopulateRoom_PlacesUnitsOnTheAs3BottomAnchor()
        {
            RoomInstance room = new RoomInstance
            {
                id = "legacy_room_units",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
            };
            room.InitializeTiles();

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(new ObjectSpawnData
            {
                id = "raider",
                type = "unit",
                tileCoord = new Vector2Int(4, 23)
            });

            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "raider");
            Assert.NotNull(spawned);
            Assert.AreEqual(180f, spawned.position.x, 0.01f);

            // AS3 Location.createUnit() (Location.as:1211) uses the SAME bottom anchor as
            // createObj(): putLoc(this, (nx + 0.5 * size) * tileX, (ny + 1) * tileY - 1). There is no
            // extra tile of lift for units, so a unit and a box authored on the same tile must land
            // on the same Y. The old expectation (161) placed units one tile higher than AS3 does.
            Assert.AreEqual(41f, spawned.position.y, 0.01f);

            Object.DestroyImmediate(template);
        }

        [Test]
        public void PopulateRoom_StaleGenericBucketOnAUnitDefinition_StillSpawnsAUnit()
        {
            RoomInstance room = new RoomInstance
            {
                id = "legacy_room_stale_bucket",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
            };
            room.InitializeTiles();

            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "training";
            definition.family = MapObjectFamily.Unit;

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(new ObjectSpawnData
            {
                id = "training",
                // Camp/room_1_0 really does serialize this, because the classifier had no tip='unit'
                // branch when the room was imported: `- type: obj` then `id: training`.
                type = MapObjectDefinition.GenericPlacementType,
                definition = definition,
                definitionId = "training",
                tileCoord = new Vector2Int(4, 23)
            });

            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            // Scoped to this object id on purpose. `room.objects` is never empty after PopulateRoom:
            // Phase 3 (PlaceXpBonuses) always seeds up to 5 bonus objects into a room whose tiles are
            // air, so a bare `room.objects.Count == 0` fails for a reason that has nothing to do with
            // units. The claim being made is that *the dummy* is not also a prop.
            Assert.IsNull(room.objects.Find(obj => obj != null && obj.objectId == "training"),
                "A unit must not also be created as a static prop.");
            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "training");
            Assert.NotNull(spawned);

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(definition);
        }

        [Test]
        public void PopulateRoom_GenericDefinition_DoesNotDowngradeASpecificStoredBucket()
        {
            RoomInstance room = new RoomInstance
            {
                id = "legacy_room_mapping_rescue",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
            };
            room.InitializeTiles();

            // tarakan's definition is exactly this stale: family GenericObject, bucket "obj". It is a
            // real enemy only because DefaultAS3ObjectMapping supplies "unit" for it by objectId, and
            // the importer writes that into ObjectSpawnData.type.
            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "tarakan";
            definition.family = MapObjectFamily.GenericObject;
            definition.defaultPlacementType = MapObjectDefinition.GenericPlacementType;

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(new ObjectSpawnData
            {
                id = "tarakan",
                type = "unit",
                definition = definition,
                definitionId = "tarakan",
                tileCoord = new Vector2Int(4, 23)
            });

            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            // Without this complement, a rule that always trusted the definition would still pass the
            // stale-bucket test above, while silently turning every mapping-rescued enemy into a prop.
            // Scoped by object id: the 5 XP bonuses PlaceXpBonuses seeds are not this test's business.
            Assert.IsNull(room.objects.Find(obj => obj != null && obj.objectId == "tarakan"));
            Assert.NotNull(room.units.Find(unit => unit != null && unit.unitId == "tarakan"));

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(definition);
        }

        // ── the placement seam the spawner consumes ──────────────────────────────────────────────
        //
        // RoomPopulator is where a unit's placement is translated into the record RoomUnitSpawner
        // renders. Everything the spawner needs must already be on the record, because the spawner is
        // pure: it has no spawn stream and no definition lookup of its own.

        [Test]
        public void PopulateRoom_UnitPlacement_CarriesTheControllerIdFromTheDefinition()
        {
            RoomInstance room = MakeRoom("legacy_room_controller_id");
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition(controllerClass: "UnitTrain");
            var provider = new FakeUnitDefinitions().Add(MakeTrainingDefinition());

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeTrainingPlacement(objectDefinition));

            RoomPopulator.PopulateRoom(room, template, room.difficulty, null, provider);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "training");
            Assert.NotNull(spawned);
            Assert.AreEqual("UnitTrain", spawned.controllerId,
                "The controller class travels from the definition, which is where AS3 reads it.");

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }

        [TestCase("1", 1)]
        [TestCase("-1", -1)]
        [TestCase("5", 1)]
        [TestCase("-4", -1)]
        public void PopulateRoom_AuthoredTurn_DecidesFacing_WithoutRolling(string turn, int expected)
        {
            RoomInstance room = MakeRoom("legacy_room_turn_" + turn.Replace("-", "neg"));
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeTrainingDefinition());

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeTrainingPlacement(objectDefinition, ("turn", turn)));

            // The RNG would flip to the opposite facing if it were consulted, so a rule that rolled
            // regardless of `turn` fails here rather than passing by luck.
            var rng = new FixedRng(expected > 0 ? 0.99f : 0.01f);
            RoomPopulator.PopulateRoom(room, template, room.difficulty, rng, provider);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "training");
            Assert.NotNull(spawned);
            Assert.AreEqual(expected, spawned.facingDirection);

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }

        [Test]
        public void PopulateRoom_NoAuthoredTurn_ResolvesFacingFromTheSpawnStream()
        {
            // AS3's absent-attribute case is a coin flip (Unit.as:609-613). It has to happen here, at
            // population time, because this is the layer holding the spawn stream — the presenter has
            // none, and a presenter that rolled its own dice would make the room irreproducible.
            RoomInstance room = MakeRoom("legacy_room_no_turn");
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeTrainingDefinition());

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeTrainingPlacement(objectDefinition));

            RoomPopulator.PopulateRoom(room, template, room.difficulty, new FixedRng(0.99f), provider);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "training");
            Assert.NotNull(spawned);
            Assert.AreEqual(-1, spawned.facingDirection, "A roll below 0.5 must face left.");

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }

        [Test]
        public void PopulateRoom_UnitPlacement_KeepsItsAttributesForTheController()
        {
            // The dummy's armoured variant is selected by `tr` on the PLACEMENT (UnitTrain.as:31-34),
            // not on the definition — one definition, two variants. So the attributes must survive the
            // trip, or the two armoured dummies in the camp's test ground become five plain ones.
            RoomInstance room = MakeRoom("legacy_room_attributes");
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeTrainingDefinition());

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeTrainingPlacement(objectDefinition, ("tr", "1"), ("fix", "1")));

            RoomPopulator.PopulateRoom(room, template, room.difficulty, null, provider);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "training");
            Assert.NotNull(spawned);
            Assert.AreEqual("1", spawned.GetAttribute("tr"));
            Assert.AreEqual("1", spawned.GetAttribute("fix"));
            // The complement: a missing key must not read back as "1" or the armoured test above would
            // pass for a unit that has no `tr` at all.
            Assert.AreEqual(string.Empty, spawned.GetAttribute("nope"));

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }

        [Test]
        public void PopulateRoom_UnitHealth_ComesFromTheUnitDefinition()
        {
            RoomInstance room = MakeRoom("legacy_room_health");
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeTrainingDefinition(health: 500));

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeTrainingPlacement(objectDefinition));

            RoomPopulator.PopulateRoom(room, template, room.difficulty, null, provider);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "training");
            Assert.NotNull(spawned);
            // The deleted CalculateUnitHealth had no "training" case and reported 50 here.
            Assert.AreEqual(500f, spawned.maxHealth, 0.001f);

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }
    }
}
