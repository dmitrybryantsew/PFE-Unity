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
        /// Wraps another stream and counts every draw taken through it, so a test can assert that a
        /// placement spends the <b>same number of draws</b> as before.
        ///
        /// <para><b>Draw count is part of the spawn contract, not an implementation detail.</b> One
        /// stream is shared by the whole room, so a single extra draw silently re-rolls every later
        /// placement: no error, no warning, and a map that quietly differs from the one the oracle
        /// would have generated. That is precisely the failure a value-only assertion cannot see, which
        /// is why this wrapper exists — see <c>PopulateRoom_NonZombiePlacement_SpendsNoDrawOnTheDiggerRoll</c>.</para>
        ///
        /// <para><see cref="Shuffle{T}"/> is deliberately <b>not</b> counted: it draws inside the
        /// wrapped stream, which this wrapper cannot observe. No test here reaches it.</para>
        /// </summary>
        private sealed class CountingRng : IRngService
        {
            private readonly IRngService _inner;
            public CountingRng(IRngService inner) => _inner = inner;

            public int Draws { get; private set; }

            public uint  NextUInt() { Draws++; return _inner.NextUInt(); }
            public float NextFloat() { Draws++; return _inner.NextFloat(); }
            public int   NextInt(int maxExclusive) { Draws++; return _inner.NextInt(maxExclusive); }
            public int   Range(int minInclusive, int maxExclusive) { Draws++; return _inner.Range(minInclusive, maxExclusive); }
            public float Range(float min, float max) { Draws++; return _inner.Range(min, max); }
            public bool  Chance(float probability) { Draws++; return _inner.Chance(probability); }
            public void  Shuffle<T>(IList<T> list) => _inner.Shuffle(list);
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

        private static UnitDefinition MakeTrainingDefinition(int health = 500) =>
            MakeUnitDefinition("training", health);

        private static UnitDefinition MakeUnitDefinition(string id, int health = 500)
        {
            var definition = ScriptableObject.CreateInstance<UnitDefinition>();
            definition.id = id;
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
            return MakeUnitPlacement("training", definition, attributes);
        }

        /// <summary>
        /// A placement of the <b>family template</b> id <c>zombie</c> — the only id AS3's
        /// <c>UnitZombie</c> constructor runs for, and therefore the only one that rolls a
        /// <c>digger</c> tier. <c>UnitVariantResolver</c> turns it into <c>zombie0</c> at difficulty 0,
        /// which is also what makes these tests fail loudly if the variant step is ever dropped: the
        /// family row carries no <c>&lt;vis&gt;</c> art and no stats at all.
        /// </summary>
        private static ObjectSpawnData MakeZombiePlacement(
            MapObjectDefinition definition,
            params (string key, string value)[] attributes)
        {
            return MakeUnitPlacement("zombie", definition, attributes);
        }

        private static ObjectSpawnData MakeUnitPlacement(
            string unitId,
            MapObjectDefinition definition,
            params (string key, string value)[] attributes)
        {
            var spawn = new ObjectSpawnData
            {
                id = unitId,
                type = MapObjectDefinition.GenericPlacementType,
                definition = definition,
                definitionId = unitId,
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

        /// <summary>
        /// Stamp the tile a (4, 23) placement stands on as a wall, so the ambush gate opens.
        ///
        /// <para><b>The coordinate is derived, not guessed.</b> A placement at AS3 row 23 resolves to
        /// room-local feet at <c>(roomHeight - 23 - 1) * TILE_SIZE + 1</c> = 41 px;
        /// <c>RoomInstance.HasWallUnderFeet</c> then probes 10 px <i>below</i> the feet — row
        /// <c>floor(31 / 40)</c> = 0 — at each foot, <c>x = 180 ± 10</c>, which is column 4 on both
        /// sides for a one-tile-wide unit. So row 0, column 4 is the floor this unit stands on, and
        /// <see cref="RoomInstanceTests"/> pins the same geometry from the other end.</para>
        /// </summary>
        private static void MakeFloorSolid(RoomInstance room)
        {
            room.GetTileAtCoord(new Vector2Int(4, 0)).physicsType = TilePhysicsType.Wall;
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

            // Match the FAMILY, not the authored id. RoomPopulator resolves the placement through
            // UnitVariantResolver.ResolveSpawnId, so an authored `raider` spawns `raider1..N` and never
            // the bare `raider` — UnitRaider.as:132-150 composes `id = parentId + tr` with `tr` clamped to
            // at least 1. The roll is non-reproducible here (PopulateRoom is called without a seeded rng),
            // so the prefix is the only stable handle; the sibling fixtures in this file use `"raider1"`
            // only where they seed the stream. The assertion this test is actually about is the anchor
            // below, and a `unitId == "raider"` lookup failed on the id before it ever reached it.
            UnitInstance spawned = room.units.Find(
                unit => unit != null && unit.unitId.StartsWith("raider", System.StringComparison.Ordinal));
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

        // ── the zombie ambush: AS3 UnitZombie.digger ─────────────────────────────────────────────
        //
        // `digger` is rolled per placement and only for the zombie family, and the bury it arms is
        // resolved here rather than at runtime — this is the port's `setPos`, where the tiles are final
        // and the room is not active yet. See RoomPopulator.CreateUnit and RoomInstance.HasWallUnderFeet.

        [Test]
        public void PopulateRoom_ZombieOverSolidFloor_RollsIntoABuriedAmbush()
        {
            RoomInstance room = MakeRoom("legacy_room_ambush");
            MakeFloorSolid(room);
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeUnitDefinition("zombie0"));

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeZombiePlacement(objectDefinition));

            // 0.01 < 0.25 — the difficulty-0 ambush chance (UnitZombie.as:120-127), so this placement
            // buries. The roll is the only thing this test varies against its complement below.
            RoomPopulator.PopulateRoom(room, template, room.difficulty, new FixedRng(0.01f), provider);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "zombie0");
            Assert.NotNull(spawned,
                "The family id must resolve to a variant (zombie -> zombie0). The bare `zombie` row is a " +
                "content-free template with no <vis> art and no stats, which is why it draws the fallback square.");
            Assert.AreEqual(1, spawned.digger);
            Assert.IsTrue(spawned.ambushArmed, "A digger over solid ground must arm the bury.");

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }

        [Test]
        public void PopulateRoom_ZombieOverSolidFloor_MissesTheAmbushRoll_AndStands()
        {
            // The complement of the test above: same room, same solid floor, same placement — only the
            // roll differs. Without it, an implementation that always buried would pass that test.
            RoomInstance room = MakeRoom("legacy_room_ambush_miss");
            MakeFloorSolid(room);
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeUnitDefinition("zombie0"));

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeZombiePlacement(objectDefinition));

            // 0.5 > 0.25 — not an ambusher.
            RoomPopulator.PopulateRoom(room, template, room.difficulty, new FixedRng(0.5f), provider);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "zombie0");
            Assert.NotNull(spawned, "A non-digger is an ordinary ghoul, not a missing unit.");
            Assert.AreEqual(0, spawned.digger);
            Assert.IsFalse(spawned.ambushArmed);

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }

        [Test]
        public void PopulateRoom_Tier1ZombieOverAir_StandsRatherThanBurying()
        {
            // setPos (UnitZombie.as:197-213): no solid floor means `zak = false`. Only tier 2 is
            // exterminated in that branch, so a tier-1 digger over a catwalk is an ordinary ghoul that
            // keeps its tier — a scripted `command()` can still dig it out (UnitZombie.as:357-365).
            RoomInstance room = MakeRoom("legacy_room_ambush_air");
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeUnitDefinition("zombie0"));

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeZombiePlacement(objectDefinition));

            RoomPopulator.PopulateRoom(room, template, room.difficulty, new FixedRng(0.01f), provider);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "zombie0");
            Assert.NotNull(spawned, "Only tier 2 is removed over a drop; tier 1 stands.");
            Assert.AreEqual(1, spawned.digger);
            Assert.IsFalse(spawned.ambushArmed);

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }

        [Test]
        public void PopulateRoom_Tier2ZombieOverAir_IsExterminatedBeforeItIsEverDrawn()
        {
            // UnitZombie.as:206-209 — the `else` arm of the bury test calls exterminate() for tier 2,
            // which is `loc.remObj(this); sost = 4; disabled = true;` (Unit.as:4411-4421). The unit is
            // removed at population, so it never appears at all: not invisible, absent.
            RoomInstance room = MakeRoom("legacy_room_ambush_tier2_air");
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeUnitDefinition("zombie0"));

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeZombiePlacement(objectDefinition, ("dig", "2")));

            // 0.99 would roll a non-digger, so the authored `dig=2` is the only reason this is tier 2.
            RoomPopulator.PopulateRoom(room, template, room.difficulty, new FixedRng(0.99f), provider);

            Assert.IsNull(
                room.units.Find(unit => unit != null && unit.unitId.StartsWith("zombie", System.StringComparison.Ordinal)),
                "A tier-2 digger over a drop is exterminated at population and must not be in the room.");

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }

        [Test]
        public void PopulateRoom_Tier2ZombieOverSolidFloor_IsKept()
        {
            // The positive control for the extermination above: same tier, same authored `dig=2`, same
            // 0.99 roll — only the floor differs. Without this, an implementation that dropped *every*
            // tier-2 unit would pass the test above.
            RoomInstance room = MakeRoom("legacy_room_ambush_tier2_floor");
            MakeFloorSolid(room);
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeUnitDefinition("zombie0"));

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeZombiePlacement(objectDefinition, ("dig", "2")));

            RoomPopulator.PopulateRoom(room, template, room.difficulty, new FixedRng(0.99f), provider);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "zombie0");
            Assert.NotNull(spawned);
            Assert.AreEqual(2, spawned.digger);
            Assert.IsTrue(spawned.ambushArmed);

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }

        [Test]
        public void PopulateRoom_AuthoredDig_WinsOverTheRoll_AndIsNotClamped()
        {
            // UnitZombie.as:120-123 reads the PLACED node first (`param3.@dig`), so an authored value
            // beats the roll — and AS3 stores it verbatim, testing it only for truthiness and against
            // 1/2/3. Clamping would silently turn an authored dig='5' into a permanently inert tier 3,
            // which is a different unit.
            RoomInstance room = MakeRoom("legacy_room_ambush_authored");
            MakeFloorSolid(room);
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeUnitDefinition("zombie0"));

            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.objects.Add(MakeZombiePlacement(objectDefinition, ("dig", "5")));

            // 0.01 would roll a tier-1 ambusher if the roll were consulted at all.
            RoomPopulator.PopulateRoom(room, template, room.difficulty, new FixedRng(0.01f), provider);

            UnitInstance spawned = room.units.Find(unit => unit != null && unit.unitId == "zombie0");
            Assert.NotNull(spawned);
            Assert.AreEqual(5, spawned.digger, "An authored dig= must not be clamped or overwritten by the roll.");
            // A truthy tier that matches no bury branch still buries: `zak` is set for any non-zero
            // digger, and only the *senses* are picked per tier (UnitZombie.as:426-439).
            Assert.IsTrue(spawned.ambushArmed);

            Object.DestroyImmediate(template);
            Object.DestroyImmediate(objectDefinition);
        }

        [Test]
        public void PopulateRoom_NonZombiePlacement_SpendsNoDrawOnTheDiggerRoll()
        {
            // The digger roll belongs to UnitZombie's constructor and to nothing else. Rolling it for a
            // raider would consume one extra draw from the stream the whole room shares, which re-rolls
            // every later placement — no error, no warning, a different map. A value-only assertion
            // cannot see that, so this counts draws.
            //
            // Two otherwise identical rooms, differing only in a `dig` attribute the raider must not be
            // reading. If the family gate were dropped, the authored value would short-circuit the roll
            // and the second room would spend one draw fewer — and would also report digger = 1.
            RoomInstance withoutDig = MakeRoom("legacy_room_raider_no_dig");
            RoomInstance withDig = MakeRoom("legacy_room_raider_dig");
            MapObjectDefinition objectDefinition = MakeTrainingObjectDefinition();
            var provider = new FakeUnitDefinitions().Add(MakeUnitDefinition("raider1"));

            RoomTemplate plain = ScriptableObject.CreateInstance<RoomTemplate>();
            plain.objects.Add(MakeUnitPlacement("raider", objectDefinition));

            RoomTemplate withAttribute = ScriptableObject.CreateInstance<RoomTemplate>();
            withAttribute.objects.Add(MakeUnitPlacement("raider", objectDefinition, ("dig", "1")));

            var plainRng = new CountingRng(new FixedRng(0.01f));
            var digRng = new CountingRng(new FixedRng(0.01f));

            RoomPopulator.PopulateRoom(withoutDig, plain, withoutDig.difficulty, plainRng, provider);
            RoomPopulator.PopulateRoom(withDig, withAttribute, withDig.difficulty, digRng, provider);

            UnitInstance plainUnit = withoutDig.units.Find(unit => unit != null && unit.unitId == "raider1");
            UnitInstance digUnit = withDig.units.Find(unit => unit != null && unit.unitId == "raider1");
            Assert.NotNull(plainUnit);
            Assert.NotNull(digUnit);
            Assert.AreEqual(0, plainUnit.digger, "Only the zombie family rolls a digger tier.");
            Assert.AreEqual(0, digUnit.digger, "A non-zombie must not read a `dig` attribute either.");

            Assert.AreEqual(plainRng.Draws, digRng.Draws,
                "A non-zombie placement must spend the same number of spawn draws whether or not a `dig` " +
                "attribute is present — an extra draw silently shifts every later placement in the room.");

            Object.DestroyImmediate(plain);
            Object.DestroyImmediate(withAttribute);
            Object.DestroyImmediate(objectDefinition);
        }
    }
}
