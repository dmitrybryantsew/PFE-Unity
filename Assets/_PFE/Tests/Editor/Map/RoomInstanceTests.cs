using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Unit tests for RoomInstance class.
    /// </summary>
    [TestFixture]
    public class RoomInstanceTests
    {
        private RoomInstance CreateTestRoom()
        {
            RoomInstance room = new RoomInstance
            {
                id = "test_room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                landPosition = new Vector3Int(0, 0, 0)
            };
            room.InitializeTiles();
            return room;
        }

        [Test]
        public void RoomInstance_Initialization_CreatesTileGrid()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNotNull(room.tiles);
            Assert.AreEqual(WorldConstants.ROOM_WIDTH, room.width);
            Assert.AreEqual(WorldConstants.ROOM_HEIGHT, room.height);
            Assert.AreEqual(WorldConstants.ROOM_WIDTH * WorldConstants.ROOM_HEIGHT, room.tiles.Length);
        }

        [Test]
        public void GetTileAt_ValidPosition_ReturnsTile()
        {
            RoomInstance room = CreateTestRoom();

            TileData tile = room.GetTileAtCoord(new Vector2Int(10, 15));

            Assert.IsNotNull(tile);
            Assert.AreEqual(10, tile.gridPosition.x);
            Assert.AreEqual(15, tile.gridPosition.y);
        }

        [Test]
        public void GetTileAt_OutOfBounds_ReturnsNull()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNull(room.GetTileAtCoord(new Vector2Int(-1, 0)));
            Assert.IsNull(room.GetTileAtCoord(new Vector2Int(0, -1)));
            Assert.IsNull(room.GetTileAtCoord(new Vector2Int(48, 0)));
            Assert.IsNull(room.GetTileAtCoord(new Vector2Int(0, 27)));
        }

        [Test]
        public void GetTileAt_PixelPosition_ReturnsCorrectTile()
        {
            RoomInstance room = CreateTestRoom();

            // Pixel (100,150) should map to tile (2,3)
            TileData tile = room.GetTileAt(new Vector2(100, 150));

            Assert.IsNotNull(tile);
            Assert.AreEqual(2, tile.gridPosition.x);
            Assert.AreEqual(3, tile.gridPosition.y);
        }

        [Test]
        public void CheckCollision_NoCollision_ReturnsFalse()
        {
            RoomInstance room = CreateTestRoom();
            // All tiles are air by default

            bool collision = room.CheckCollision(new Vector2(100, 100), new Vector2(20, 40));

            Assert.IsFalse(collision);
        }

        [Test]
        public void CheckCollision_WithWall_ReturnsTrue()
        {
            RoomInstance room = CreateTestRoom();
            // Set a tile as wall
            room.tiles[5, 5].physicsType = TilePhysicsType.Wall;

            // Position that overlaps with tile (5,5)
            Vector2 pos = WorldCoordinates.TileToPixel(new Vector2Int(5, 5));
            bool collision = room.CheckCollision(pos, new Vector2(10, 10));

            Assert.IsTrue(collision);
        }

        [Test]
        public void Activate_SetsActiveAndVisited()
        {
            RoomInstance room = CreateTestRoom();

            room.Activate();

            Assert.IsTrue(room.isActive);
            Assert.IsTrue(room.isVisited);
        }

        [Test]
        public void Deactivate_SetsInactive()
        {
            RoomInstance room = CreateTestRoom();
            room.Activate();

            room.Deactivate();

            Assert.IsFalse(room.isActive);
            Assert.IsTrue(room.isVisited); // Should remain true
        }

        [Test]
        public void GetPlayerSpawnPoint_NoSpawnPoints_ReturnsCenter()
        {
            RoomInstance room = CreateTestRoom();

            Vector2 spawn = room.GetPlayerSpawnPoint();

            float expectedX = room.width * WorldConstants.TILE_SIZE / 2;
            float expectedY = room.height * WorldConstants.TILE_SIZE / 2;
            Assert.AreEqual(expectedX, spawn.x, 0.001f);
            Assert.AreEqual(expectedY, spawn.y, 0.001f);
        }

        [Test]
        public void GetPlayerSpawnPoint_WithSpawnPoints_ReturnsFirstPlayerSpawn()
        {
            RoomInstance room = CreateTestRoom();
            room.spawnPoints.Add(new SpawnPoint
            {
                tileCoord = new Vector2Int(10, 10),
                type = SpawnType.Player
            });

            Vector2 spawn = room.GetPlayerSpawnPoint();

            Vector2 expected = WorldCoordinates.TileToPixel(new Vector2Int(10, 10));
            Assert.AreEqual(expected.x, spawn.x, 0.001f);
            Assert.AreEqual(expected.y, spawn.y, 0.001f);
        }

        [Test]
        public void GetGroundHeight_FlatTile_ReturnsTileY()
        {
            RoomInstance room = CreateTestRoom();

            float height = room.GetGroundHeight(new Vector2(100, 100));

            // Should be at the bottom of the tile
            Assert.AreEqual(80f, height, 0.001f); // Tile y=2, so yMin = 2*40 = 80
        }

        [Test]
        public void GetGroundHeight_SlopedTile_CalculatesCorrectly()
        {
            RoomInstance room = CreateTestRoom();
            // Create a sloped tile (slope type 1 = / low-left to high-right)
            room.tiles[5, 5].slopeType = 1;

            Rect bounds = room.tiles[5, 5].GetBounds();

            // slopeType = 1 is '/' — low-left, high-right. In Unity Y-up that means the left
            // edge sits at the tile's yMin (the bottom), NOT yMax.
            // Need to pass a Y position within the tile so GetTileAt finds tile (5,5)
            float height = room.GetGroundHeight(new Vector2(bounds.xMin, bounds.center.y));
            Assert.AreEqual(bounds.yMin, height, 0.001f);
        }

        [Test]
        public void RoomInstance_Difficulty_HasDefaultValues()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNotNull(room.difficulty);
            Assert.AreEqual(0f, room.difficulty.baseDifficulty);
            Assert.AreEqual(0f, room.difficulty.enemyLevel);
            Assert.IsNotNull(room.difficulty.enemyCounts);
        }

        [Test]
        public void RoomInstance_Environment_HasDefaultValues()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNotNull(room.environment);
            Assert.AreEqual("", room.environment.musicTrack);
            Assert.AreEqual(0, room.environment.waterType);
            Assert.IsFalse(room.environment.HasWater());
        }

        [Test]
        public void RoomInstance_DoorsList_Initialized()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNotNull(room.doors);
            Assert.AreEqual(0, room.doors.Count);
        }

        [Test]
        public void RoomInstance_SpawnPointsList_Initialized()
        {
            RoomInstance room = CreateTestRoom();

            Assert.IsNotNull(room.spawnPoints);
            Assert.AreEqual(0, room.spawnPoints.Count);
        }

        [Test]
        public void SetEnemyCount_WorksCorrectly()
        {
            RoomInstance room = CreateTestRoom();

            room.difficulty.SetEnemyCount(1, 2, 5);

            int count = room.difficulty.GetEnemyCount(1);
            Assert.GreaterOrEqual(count, 2);
            Assert.LessOrEqual(count, 5);
        }

        [Test]
        public void GetTotalEnemyCount_SumsAllTypes()
        {
            RoomInstance room = CreateTestRoom();
            room.difficulty.enemyCounts[1] = 3;
            room.difficulty.enemyCounts[2] = 2;
            room.difficulty.enemyCounts[3] = 1;

            int total = room.difficulty.GetTotalEnemyCount();

            Assert.AreEqual(6, total);
        }

        [Test]
        public void RoomWithAllWallTiles_HasCollision()
        {
            RoomInstance room = CreateTestRoom();

            // Fill room with walls
            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    room.tiles[x, y].physicsType = TilePhysicsType.Wall;
                }
            }

            bool collision = room.CheckCollision(new Vector2(100, 100), new Vector2(10, 10));
            Assert.IsTrue(collision);
        }

        [Test]
        public void PlatformTile_IsSolidButNotWall()
        {
            RoomInstance room = CreateTestRoom();
            room.tiles[5, 5].physicsType = TilePhysicsType.Platform;

            Assert.IsTrue(room.tiles[5, 5].IsSolid());
            Assert.IsTrue(room.tiles[5, 5].IsPlatform());
        }

        [Test]
        public void Update_InactiveRoom_DoesNothing()
        {
            RoomInstance room = CreateTestRoom();
            room.Deactivate();

            // Should not throw exception
            room.Update();
        }

        [Test]
        public void RebuildRuntimeLayers_TracksOnlyDynamicProps()
        {
            RoomInstance room = CreateTestRoom();

            MapObjectDefinition dynamicDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            dynamicDefinition.objectId = "woodbox";
            dynamicDefinition.physicalCapability = MapObjectPhysicalCapability.DynamicTelekinetic;

            MapObjectDefinition staticDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            staticDefinition.objectId = "wallcab";
            staticDefinition.physicalCapability = MapObjectPhysicalCapability.Static;

            room.objects.Add(new ObjectInstance
            {
                objectId = "woodbox",
                objectType = "box",
                definition = dynamicDefinition,
                definitionId = "woodbox",
                position = new Vector2(120f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            });

            room.objects.Add(new ObjectInstance
            {
                objectId = "wallcab",
                objectType = "box",
                definition = staticDefinition,
                definitionId = "wallcab",
                position = new Vector2(160f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            });

            room.RebuildRuntimeLayers();

            Assert.AreEqual(1, room.ObjectPhysicsLayer.DynamicObjectCount);
            Assert.AreSame(room.objects[0], room.ObjectPhysicsLayer.DynamicObjects[0]);
        }

        [Test]
        public void TryFindNearestTelekineticObject_ReturnsClosestEligibleProp()
        {
            RoomInstance room = CreateTestRoom();

            MapObjectDefinition telekineticDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            telekineticDefinition.objectId = "woodbox";
            telekineticDefinition.size = 1;
            telekineticDefinition.width = 1;
            telekineticDefinition.physicalCapability = MapObjectPhysicalCapability.DynamicTelekinetic;

            MapObjectDefinition throwableDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            throwableDefinition.objectId = "mcrate4";
            throwableDefinition.size = 2;
            throwableDefinition.width = 2;
            throwableDefinition.physicalCapability = MapObjectPhysicalCapability.DynamicThrowable;

            ObjectInstance farTelekinetic = new ObjectInstance
            {
                objectId = "woodbox",
                objectType = "box",
                definition = telekineticDefinition,
                definitionId = "woodbox",
                position = new Vector2(240f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            };

            ObjectInstance nearTelekinetic = new ObjectInstance
            {
                objectId = "woodbox",
                objectType = "box",
                definition = telekineticDefinition,
                definitionId = "woodbox",
                position = new Vector2(120f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            };

            ObjectInstance nonTelekinetic = new ObjectInstance
            {
                objectId = "mcrate4",
                objectType = "box",
                definition = throwableDefinition,
                definitionId = "mcrate4",
                position = new Vector2(100f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            };

            room.AddObject(farTelekinetic);
            room.AddObject(nearTelekinetic);
            room.AddObject(nonTelekinetic);

            bool found = room.TryFindNearestTelekineticObject(new Vector2(100f, 100f), 200f, out ObjectInstance foundObject);

            Assert.IsTrue(found);
            Assert.AreSame(nearTelekinetic, foundObject);
        }

        [Test]
        public void TryApplyObjectImpulse_EnablesThrownStateForThrowableProp()
        {
            RoomInstance room = CreateTestRoom();

            MapObjectDefinition throwableDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            throwableDefinition.objectId = "mcrate4";
            throwableDefinition.size = 2;
            throwableDefinition.width = 2;
            throwableDefinition.physicalCapability = MapObjectPhysicalCapability.DynamicThrowable;

            ObjectInstance prop = new ObjectInstance
            {
                objectId = "mcrate4",
                objectType = "box",
                definition = throwableDefinition,
                definitionId = "mcrate4",
                position = new Vector2(160f, 160f),
                runtimeState = new MapObjectRuntimeStateData()
            };

            room.AddObject(prop);

            bool applied = room.TryApplyObjectImpulse(prop, new Vector2(120f, -50f), true);

            Assert.IsTrue(applied);
            Assert.IsTrue(prop.runtimeState.dynamicState.isThrown);
            Assert.Greater(prop.runtimeState.dynamicState.throwGraceTime, 0f);
            Assert.AreEqual(new Vector2(120f, -50f), prop.runtimeState.dynamicState.velocity);
        }

        [Test]
        public void Update_ActiveRoom_StepsDynamicPropPhysics()
        {
            RoomInstance room = CreateTestRoom();

            MapObjectDefinition dynamicDefinition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            dynamicDefinition.objectId = "woodbox";
            dynamicDefinition.size = 1;
            dynamicDefinition.width = 1;
            dynamicDefinition.physicalCapability = MapObjectPhysicalCapability.DynamicTelekinetic;

            ObjectInstance dynamicObject = new ObjectInstance
            {
                objectId = "woodbox",
                objectType = "box",
                definition = dynamicDefinition,
                definitionId = "woodbox",
                position = new Vector2(120f, 120f),
                runtimeState = new MapObjectRuntimeStateData()
            };
            dynamicObject.runtimeState.dynamicState.velocity = Vector2.zero;
            room.objects.Add(dynamicObject);

            room.Activate();
            float initialY = dynamicObject.position.y;

            room.Update();

            // Gravity pulls DOWN. This asserted `Greater` because RoomObjectPhysicsLayer applied
            // gravity as `velocity.y += GravityPixelsPerSecond * deltaTime` — an inverted sign that
            // made props rise. With the sign corrected, one Update() at the layer's legacy 1/60 step
            // moves a zero-velocity prop down by 900 px/s² * (1/60)² = 0.25 px (it was 0.5 px while
            // gravity was still 2x too strong). See docs/Roadmap/LLP2D_STAGE_A_RESULTS.md §5.
            // The step rate, the constants and the grounded-friction rule are pinned in
            // RoomObjectPhysicsLayerTests.
            Assert.Less(dynamicObject.position.y, initialY);
            Assert.AreEqual(1, room.ObjectPhysicsLayer.DynamicObjectCount);
        }

        // ── HasWallUnderFeet: AS3 UnitZombie.setPos's bury test ─────────────────────────────────
        //
        // Three independent things have to be right for the ambush to work at all, and each of them
        // fails SILENTLY — the observable symptom of all three is "no zombie ever buries", which is
        // also what a tier-0 roll looks like. So they are pinned separately:
        //
        //   1. the 10 px DROP below the feet (`Y + 10`), which is what makes the probe read the floor
        //      rather than the tile the unit is standing in;
        //   2. the ±10 px FEET, which is what makes it two tiles rather than one;
        //   3. the MIRROR — AS3's Y runs down, this room's runs up, so "below" is `y - 10`.
        //
        // The arithmetic the tests use: `RoomPopulator.ResolveLegacyBottomAnchorPixels` seats a unit at
        // `(height - border - as3Row - 1) * TILE + 1`, and port row = `height - 1 - as3Row`. A unit
        // standing on the bottom row of the room therefore occupies port row 1 with its feet at
        // `1 * 40 + 1 = 41`, and the tiles it stands on are port row 0.

        [Test]
        public void HasWallUnderFeet_BothFootTilesWall_IsTrue()
        {
            RoomInstance room = CreateTestRoom();
            room.tiles[10, 0].physicsType = TilePhysicsType.Wall;
            room.tiles[11, 0].physicsType = TilePhysicsType.Wall;

            // 2 tiles wide, centred on the 10/11 boundary: feet at x 430 and x 450, both inside a
            // different column. Both are wall, so the ambush is possible.
            Assert.IsTrue(room.HasWallUnderFeet(new Vector2(440f, 41f)));
        }

        [Test]
        public void HasWallUnderFeet_OneFootOverAir_IsFalse()
        {
            RoomInstance room = CreateTestRoom();
            room.tiles[10, 0].physicsType = TilePhysicsType.Wall;
            room.tiles[11, 0].physicsType = TilePhysicsType.Air;

            // AS3 is `kop1.phis > 0 && kop2.phis > 0` — a conjunction, so one foot over a hole refuses.
            Assert.IsFalse(room.HasWallUnderFeet(new Vector2(440f, 41f)));
        }

        [Test]
        public void HasWallUnderFeet_SingleTileUnit_ProbesTheSameColumnTwice()
        {
            RoomInstance room = CreateTestRoom();
            room.tiles[10, 0].physicsType = TilePhysicsType.Wall;

            // A 1-tile unit sits at `(col + 0.5) * 40 = 420`, so both feet land in column 10.
            Assert.IsTrue(room.HasWallUnderFeet(new Vector2(420f, 41f)));
        }

        [Test]
        public void HasWallUnderFeet_PlatformUnderFeet_IsFalse()
        {
            // THE test that distinguishes this predicate from `TileData.IsSolid()`.
            //
            // AS3's test is `phis > 0`, and a shelf form carries `phis == 0` with `shelf == true`
            // (Tile.as:182-186) — the port turns that into `Platform` (TileDecoder.cs:223-227).
            // `IsSolid()` is `physicsType >= Wall`, so it would answer TRUE here and bury a zombie under
            // a catwalk that the oracle leaves standing as an ordinary ghoul. The assertion on
            // `IsSolid()` is not decoration: it is the evidence that the two predicates really do
            // disagree, so the `IsFalse` above is testing a choice rather than a tautology.
            RoomInstance room = CreateTestRoom();
            room.tiles[10, 0].physicsType = TilePhysicsType.Platform;
            room.tiles[11, 0].physicsType = TilePhysicsType.Platform;

            Assert.IsFalse(room.HasWallUnderFeet(new Vector2(440f, 41f)));
            Assert.IsTrue(room.tiles[10, 0].IsSolid(), "IsSolid() must disagree — that is the whole point");
        }

        [Test]
        public void HasWallUnderFeet_StairUnderFeet_IsFalse()
        {
            // Same argument as Platform: a stair overlay is `phis == 0` in AS3.
            RoomInstance room = CreateTestRoom();
            room.tiles[10, 0].physicsType = TilePhysicsType.Stair;
            room.tiles[11, 0].physicsType = TilePhysicsType.Stair;

            Assert.IsFalse(room.HasWallUnderFeet(new Vector2(440f, 41f)));
        }

        [Test]
        public void HasWallUnderFeet_ProbesBelowTheFeet_NotTheRowTheUnitStandsIn()
        {
            // The `Y + 10` half, isolated. A unit in port row 1 has its feet at y = 41, which is INSIDE
            // port row 1 — so a probe that dropped the offset would read row 1. Here row 1 is solid and
            // row 0 is air, so dropping the offset flips this assertion.
            RoomInstance room = CreateTestRoom();
            room.tiles[10, 1].physicsType = TilePhysicsType.Wall;
            room.tiles[11, 1].physicsType = TilePhysicsType.Wall;
            room.tiles[10, 0].physicsType = TilePhysicsType.Air;
            room.tiles[11, 0].physicsType = TilePhysicsType.Air;

            Assert.IsFalse(room.HasWallUnderFeet(new Vector2(440f, 41f)));
        }

        [Test]
        public void HasWallUnderFeet_FeetMatchThePlacementSeat()
        {
            // Pins the mirror against the formula that actually places units rather than against a
            // literal, so the two cannot drift apart independently. If the seat or the probe moves,
            // this fails and names which relationship broke.
            RoomInstance room = CreateTestRoom();
            int as3RowOfUnit = room.height - 2;   // port row 1
            float feetY = (room.height - room.borderOffset - as3RowOfUnit - 1) * WorldConstants.TILE_SIZE + 1f;

            Assert.AreEqual(41f, feetY);

            room.tiles[10, 0].physicsType = TilePhysicsType.Wall;
            room.tiles[11, 0].physicsType = TilePhysicsType.Wall;

            Assert.IsTrue(room.HasWallUnderFeet(new Vector2(440f, feetY)));
        }

        [Test]
        public void HasWallUnderFeet_BelowTheRoom_IsFalse()
        {
            // A unit standing on the room's floor row (port row 0) has its feet at y = 1, so the probe
            // lands at row -1 — outside the grid. `GetTileAtCoord` answers null there, and "no tile" has
            // to mean "no floor": a destroyed bottom row must not read as a wall.
            RoomInstance room = CreateTestRoom();
            room.tiles[10, 0].physicsType = TilePhysicsType.Wall;
            room.tiles[11, 0].physicsType = TilePhysicsType.Wall;

            Assert.IsFalse(room.HasWallUnderFeet(new Vector2(440f, 1f)));
        }

        // ── Relight request — the port of AS3 `Location.isRelight` ───────────────────────────────
        //
        // `Location.step():3398` ORs `isRelight` and `isRebuild` into the condition that runs the full
        // `lighting()` pass, and `:3407` clears `isRelight` unconditionally every frame. So the flag is
        // a one-shot "relight once", and the two writers are a door opening (Box.as:690) and a solid
        // tile changing (Location.as:2527, :2586). Nothing set the port's equivalent, which is why a
        // door opened while standing still left the room dark — see RoomBackdropRenderer's gate.

        [Test]
        public void ConsumeRelightRequest_WithNoRequest_ReturnsFalse()
        {
            // The negative control for the three tests below: a fresh room has NOT been asked to
            // relight. Without this, `ConsumeRelightRequest` returning true for any reason would look
            // like the writers working.
            RoomInstance room = CreateTestRoom();

            Assert.IsFalse(room.ConsumeRelightRequest());
        }

        [Test]
        public void RequestRelight_IsOneShot_ConsumeReturnsTrueThenFalse()
        {
            // AS3 clears the flag every frame (Location.as:3407) and re-arms it from the event, so one
            // request buys exactly one full pass. A sticky flag would re-run the expensive pass forever.
            RoomInstance room = CreateTestRoom();
            room.RequestRelight();

            Assert.IsTrue(room.ConsumeRelightRequest(), "the request must be delivered once");
            Assert.IsFalse(room.ConsumeRelightRequest(), "…and must not survive its own consumption");
        }

        [Test]
        public void NotifyTilesMutated_RequestsARelight()
        {
            // AS3's *second* relight writer: `isRebuild` is set when a solid tile changes
            // (Location.as:2525, :2586) and the same gate ORs it in (:3398). The port raises this
            // notification from the destruction and damage paths, so routing it into the request is
            // what makes "shoot a wall away and the room re-lights" work.
            RoomInstance room = CreateTestRoom();

            room.NotifyTilesMutated(new RectInt(10, 10, 1, 1));

            Assert.IsTrue(room.ConsumeRelightRequest(),
                "a tile mutation is AS3's `isRebuild` — it must force a full light pass");
        }

        [Test]
        public void NotifyTilesMutated_StillRaisesTheEvent()
        {
            // Positive control for the change above: RequestRelight() was added *inside*
            // NotifyTilesMutated, so a slip that replaced the event raise instead of preceding it would
            // leave the physics mirror silently un-subscribed from every tile destruction. Assert the
            // event still arrives, with the region intact.
            RoomInstance room = CreateTestRoom();
            RoomInstance seenRoom = null;
            RectInt seenRegion = default;
            int raised = 0;
            room.TilesMutated += (r, region) => { seenRoom = r; seenRegion = region; raised++; };

            RectInt region = new RectInt(9, 9, 3, 3);
            room.NotifyTilesMutated(region);

            Assert.AreEqual(1, raised, "the mutation event must still be raised exactly once");
            Assert.AreSame(room, seenRoom);
            Assert.AreEqual(region, seenRegion, "the region must reach listeners unchanged");
        }
    }
}
