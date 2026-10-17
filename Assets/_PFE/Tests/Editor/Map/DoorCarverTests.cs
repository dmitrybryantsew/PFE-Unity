using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    [TestFixture]
    public class DoorCarverTests
    {
        [Test]
        public void ApplyBorder_UsesExistingRoomBounds()
        {
            RoomInstance room = CreateAirRoom();

            DoorCarver.ApplyBorder(room, 1);

            Assert.AreEqual(WorldConstants.ROOM_WIDTH, room.width);
            Assert.AreEqual(WorldConstants.ROOM_HEIGHT, room.height);
            Assert.AreEqual(0, room.borderOffset);
        }

        [Test]
        public void ApplyBorder_AssignsComposableBorderMaterial()
        {
            RoomInstance room = CreateAirRoom();

            DoorCarver.ApplyBorder(room, 1);

            TileData bottomLeft = room.tiles[0, 0];
            Assert.AreEqual(TilePhysicsType.Wall, bottomLeft.physicsType);
            Assert.AreEqual("A", bottomLeft.GetFrontGraphic());
            Assert.AreEqual("A", bottomLeft.GetBackGraphic());
            Assert.IsTrue(bottomLeft.indestructible);
        }

        [Test]
        public void CarveDoor_CarvesExistingEdgeCells()
        {
            RoomInstance room = CreateAirRoom();
            DoorCarver.ApplyBorder(room, 1);

            DoorCarver.CarveDoor(room, 0, (int)DoorQuality.Narrow);

            int rightCol = WorldConstants.ROOM_WIDTH - 1;
            // AS3 Location.setDoor() for right index 0 carves TOP-DOWN rows 3 and 3 - 1, i.e.
            // Unity rows ROOM_HEIGHT - 1 - 3 = 21 and 22. (These read 23/24 while ROOM_HEIGHT was 27.)
            int row0 = WorldConstants.ROOM_HEIGHT - 1 - 3;
            int row1 = row0 + 1;
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[rightCol, row0].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[rightCol, row1].physicsType);
            Assert.AreEqual(string.Empty, room.tiles[rightCol, row0].GetFrontGraphic());
        }

        [Test]
        public void CarveDoor_ConvertsAs3BottomRowToUnityBottomRow()
        {
            RoomInstance room = CreateAirRoom();
            DoorCarver.ApplyBorder(room, 1);

            DoorCarver.CarveDoor(room, 6, (int)DoorQuality.Narrow);

            Assert.AreEqual(TilePhysicsType.Air, room.tiles[5, 0].physicsType);
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[6, 0].physicsType);
            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[5, WorldConstants.ROOM_HEIGHT - 1].physicsType);
            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[6, WorldConstants.ROOM_HEIGHT - 1].physicsType);
        }

        [Test]
        public void ApplyBorder_MainFramesExistingSolidEdges_WhenRamkaIsZero()
        {
            RoomInstance room = CreateAirRoom();
            room.tiles[0, 10].physicsType = TilePhysicsType.Wall;

            DoorCarver.ApplyBorder(room, 0);

            TileData edgeTile = room.tiles[0, 10];
            Assert.AreEqual(TilePhysicsType.Wall, edgeTile.physicsType);
            Assert.AreEqual("A", edgeTile.GetFrontGraphic());
            Assert.AreEqual("A", edgeTile.GetBackGraphic());
            Assert.IsTrue(edgeTile.indestructible);
        }

        [Test]
        public void ApplyBorder_ConvertsAs3BottomRamkaToUnityBottomRow()
        {
            RoomInstance room = CreateAirRoom();

            DoorCarver.ApplyBorder(room, 3);

            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[12, 0].physicsType);
            Assert.AreEqual("A", room.tiles[12, 0].GetFrontGraphic());
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[12, WorldConstants.ROOM_HEIGHT - 1].physicsType);
        }

        [Test]
        public void FinalizeSpecificRoom_DoesNotCarveTemplateDoors()
        {
            RoomInstance room = CreateAirRoom();
            int rightCol = WorldConstants.ROOM_WIDTH - 1;
            int doorRow0 = WorldConstants.ROOM_HEIGHT - 1 - 3; // right door 0 -> AS3 row 3
            int doorRow1 = doorRow0 + 1;                       // AS3 row 2
            room.tiles[rightCol, doorRow0].physicsType = TilePhysicsType.Wall;
            room.tiles[rightCol, doorRow1].physicsType = TilePhysicsType.Wall;
            room.doors.Add(new DoorInstance
            {
                doorIndex = 0,
                side = DoorSide.Right,
                quality = DoorQuality.Narrow,
                isActive = true
            });

            var template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.environment.waterLevel = WorldConstants.ROOM_HEIGHT;

            RoomSetup.FinalizeSpecificRoom(room, template);

            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[rightCol, doorRow0].physicsType);
            Assert.AreEqual(TilePhysicsType.Wall, room.tiles[rightCol, doorRow1].physicsType);

            Object.DestroyImmediate(template);
        }

        // ── The doorway's `place = false` marking (AS3 Location.setNoObj) ───────────────────────────
        //
        // `canPlaceObjects` is the port's `Tile.place`. Its ONLY writer in the oracle is `setNoObj`,
        // reached only from `setDoor`, and its only reader is the placement guard
        // (RoomPopulator.SkipsAuthoredPlacement). The port used to mark the whole two-tile border ring
        // from RoomSetup instead; because that ran BEFORE PopulateRoom it deleted 1515 authored
        // placements across 335 of the 550 shipped rooms, `rooms_plant/яма` among them (38 of 44
        // objects — every unit, every trap, most of the loot). These tests pin the oracle's span and,
        // just as importantly, pin what must stay placeable.

        [Test]
        public void CarveDoor_BottomDoor_MarksTheOracleSpanAndLeavesTheRestOfTheRowPlaceable()
        {
            RoomInstance room = CreateAirRoom();
            DoorCarver.ApplyBorder(room, 1);

            // Bottom door 6: AS3 `_loc3_ = (6-6)*9 + 4 = 4`, so the opening is columns 5 and 6 and
            // `setNoObj(_loc3_+1, spaceY-1, 0, -2)` marks AS3 rows spaceY-1 .. spaceY-3 at each.
            DoorCarver.CarveDoor(room, 6, (int)DoorQuality.Narrow);

            int bottomRow = 0;                              // AS3 row height-1
            int innerRow = 1;                               // AS3 row height-2
            int deepRow = 2;                                // AS3 row height-3

            Assert.IsFalse(room.tiles[5, bottomRow].canPlaceObjects, "column 5, bottom row");
            Assert.IsFalse(room.tiles[6, bottomRow].canPlaceObjects, "column 6, bottom row");
            Assert.IsFalse(room.tiles[5, innerRow].canPlaceObjects, "column 5, inner row");
            Assert.IsFalse(room.tiles[6, innerRow].canPlaceObjects, "column 6, inner row");
            Assert.IsFalse(room.tiles[5, deepRow].canPlaceObjects, "column 5, third row");
            Assert.IsFalse(room.tiles[6, deepRow].canPlaceObjects, "column 6, third row");

            // The regression controls. `innerRow` is exactly where `яма` stands its garrison
            // (AS3 row 23 of 25); it must be placeable everywhere except the two door columns.
            Assert.IsTrue(room.tiles[20, innerRow].canPlaceObjects, "far column on the door's own row");
            Assert.IsTrue(room.tiles[5, 4].canPlaceObjects, "same column, above the marked span");
            Assert.IsTrue(room.tiles[20, bottomRow].canPlaceObjects, "far column on the bottom row");
        }

        [Test]
        public void CarveDoor_LeftDoor_MarksTheWallColumnPlusFiveInward()
        {
            RoomInstance room = CreateAirRoom();
            DoorCarver.ApplyBorder(room, 1);

            // Left door 11: AS3 `_loc3_ = (11-11)*4 + 3 = 3`, and `setNoObj(0, _loc3_, 5, 0)` spans
            // six columns (0..5 inclusive) on each of the two carved rows.
            DoorCarver.CarveDoor(room, 11, (int)DoorQuality.Narrow);

            int row0 = WorldConstants.ROOM_HEIGHT - 1 - 3;  // AS3 row 3
            int row1 = row0 + 1;                            // AS3 row 2

            for (int x = 0; x <= 5; x++)
            {
                Assert.IsFalse(room.tiles[x, row0].canPlaceObjects, $"column {x} on AS3 row 3");
                Assert.IsFalse(room.tiles[x, row1].canPlaceObjects, $"column {x} on AS3 row 2");
            }

            Assert.IsTrue(room.tiles[6, row0].canPlaceObjects, "one column past the span");
            Assert.IsTrue(room.tiles[0, WorldConstants.ROOM_HEIGHT - 1].canPlaceObjects,
                "the far wall, well outside the span");
        }

        [Test]
        public void CarveDoor_RightDoor_MarksTheWallColumnPlusFiveInward()
        {
            RoomInstance room = CreateAirRoom();
            DoorCarver.ApplyBorder(room, 1);

            // Right door 0: AS3 `_loc3_ = 0*4 + 3 = 3`, and `setNoObj(spaceX-1, _loc3_, -5, 0)` walks
            // six columns BACK from the wall (a negative span still includes the anchor).
            DoorCarver.CarveDoor(room, 0, (int)DoorQuality.Narrow);

            int rightCol = WorldConstants.ROOM_WIDTH - 1;
            int row0 = WorldConstants.ROOM_HEIGHT - 1 - 3;
            int row1 = row0 + 1;

            for (int x = rightCol - 5; x <= rightCol; x++)
            {
                Assert.IsFalse(room.tiles[x, row0].canPlaceObjects, $"column {x} on AS3 row 3");
                Assert.IsFalse(room.tiles[x, row1].canPlaceObjects, $"column {x} on AS3 row 2");
            }

            Assert.IsTrue(room.tiles[rightCol - 6, row0].canPlaceObjects, "one column past the span");
        }

        [Test]
        public void CarveDoor_TopDoor_MarksThreeRowsInwardFromTheBorder()
        {
            RoomInstance room = CreateAirRoom();
            DoorCarver.ApplyBorder(room, 1);

            // Top door 17: `_loc3_ = (17-17)*9 + 4 = 4`, `setNoObj(_loc3_+1, 0, 0, 2)` marks AS3 rows
            // 0..2 — the border row and two rows in.
            DoorCarver.CarveDoor(room, 17, (int)DoorQuality.Narrow);

            int as3Row0 = WorldConstants.ROOM_HEIGHT - 1;
            for (int y = as3Row0; y >= as3Row0 - 2; y--)
            {
                Assert.IsFalse(room.tiles[5, y].canPlaceObjects, $"column 5, AS3 row {as3Row0 - y}");
                Assert.IsFalse(room.tiles[6, y].canPlaceObjects, $"column 6, AS3 row {as3Row0 - y}");
            }

            Assert.IsTrue(room.tiles[5, as3Row0 - 3].canPlaceObjects, "one row past the span");
            Assert.IsTrue(room.tiles[20, as3Row0].canPlaceObjects, "far column on the border row");
        }

        [Test]
        public void CarveDoor_WideDoor_ExtendsTheSpanOnlyOnTheHorizontalSides()
        {
            RoomInstance room = CreateAirRoom();
            DoorCarver.ApplyBorder(room, 1);

            // AS3 adds `setNoObj(_loc3_, …)` / `(_loc3_+3, …)` in the `param2 > 2` branch of the TOP and
            // BOTTOM arms only; the left and right wide branches hole a third row and mark nothing.
            DoorCarver.CarveDoor(room, 6, (int)DoorQuality.Normal);
            DoorCarver.CarveDoor(room, 11, (int)DoorQuality.Normal);

            Assert.IsFalse(room.tiles[4, 0].canPlaceObjects, "bottom wide: column _loc3_");
            Assert.IsFalse(room.tiles[7, 0].canPlaceObjects, "bottom wide: column _loc3_+3");

            int leftRow0 = WorldConstants.ROOM_HEIGHT - 1 - 3;
            Assert.IsTrue(room.tiles[6, leftRow0].canPlaceObjects,
                "left wide: the extra column must NOT be marked");
        }

        [Test]
        public void CarveDoor_BelowTheCarveThreshold_MarksNothing()
        {
            RoomInstance room = CreateAirRoom();
            DoorCarver.ApplyBorder(room, 1);

            // AS3 `if(param2 < 2) return;` — a blocked door is not an opening and has no slot.
            DoorCarver.CarveDoor(room, 6, (int)DoorQuality.Blocked);

            for (int x = 0; x < WorldConstants.ROOM_WIDTH; x++)
            {
                for (int y = 0; y < WorldConstants.ROOM_HEIGHT; y++)
                {
                    Assert.IsTrue(room.tiles[x, y].canPlaceObjects, $"({x},{y}) must be untouched");
                }
            }
        }

        private static RoomInstance CreateAirRoom()
        {
            RoomInstance room = new RoomInstance
            {
                id = "door_carver_test",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };
            room.InitializeTiles();
            return room;
        }
    }
}
