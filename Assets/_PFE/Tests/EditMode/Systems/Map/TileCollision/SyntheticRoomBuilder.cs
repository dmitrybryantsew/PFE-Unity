using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.EditMode.Systems.Map.TileCollision
{
    /// <summary>
    /// Constructs synthetic <see cref="RoomInstance"/> objects from ASCII art or tile grids
    /// for collision testing and golden trace capture.
    ///
    /// No scene loading, no prefabs, no Tilemap.
    ///
    /// ASCII vocabulary:
    ///   # = Wall
    ///   = = One-way Platform (shelf)
    ///   H = Climbable Ladder (stairType = 1, slopeType = 0)
    ///   / = Slope Up (slopeType = 1, stair)
    ///   \ = Slope Down (slopeType = -1, stair)
    ///   ~ = Water (hasWater = true)
    ///   . or space = Air
    /// </summary>
    public static class SyntheticRoomBuilder
    {
        public static RoomInstance BuildFromAscii(string[] rows, int landX = 0, int landY = 0)
        {
            if (rows == null || rows.Length == 0)
            {
                throw new ArgumentException("Rows cannot be null or empty", nameof(rows));
            }

            int height = rows.Length;
            int width = 0;
            for (int i = 0; i < height; i++)
            {
                if (rows[i].Length > width)
                {
                    width = rows[i].Length;
                }
            }

            RoomInstance room = new RoomInstance
            {
                id = $"synthetic_{width}x{height}",
                width = width,
                height = height,
                landPosition = new Vector3Int(landX, landY, 0),
                borderOffset = 0,
                tiles = new TileData[width, height]
            };

            // ASCII row 0 is top (Y = height - 1), bottom row is Y = 0
            for (int r = 0; r < height; r++)
            {
                int y = height - 1 - r;
                string row = rows[r];

                for (int x = 0; x < width; x++)
                {
                    char c = x < row.Length ? row[x] : '.';
                    room.tiles[x, y] = CreateTileFromChar(c, x, y);
                }
            }

            return room;
        }

        public static RoomInstance BuildEmpty(int width, int height, int landX = 0, int landY = 0)
        {
            RoomInstance room = new RoomInstance
            {
                id = $"empty_{width}x{height}",
                width = width,
                height = height,
                landPosition = new Vector3Int(landX, landY, 0),
                borderOffset = 0,
                tiles = new TileData[width, height]
            };

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    room.tiles[x, y] = new TileData
                    {
                        gridPosition = new Vector2Int(x, y),
                        physicsType = TilePhysicsType.Air
                    };
                }
            }

            return room;
        }

        private static TileData CreateTileFromChar(char c, int x, int y)
        {
            TileData tile = new TileData
            {
                gridPosition = new Vector2Int(x, y),
                physicsType = TilePhysicsType.Air
            };

            switch (c)
            {
                case '#':
                    tile.physicsType = TilePhysicsType.Wall;
                    break;

                case '=':
                    tile.physicsType = TilePhysicsType.Platform;
                    break;

                case 'H':
                    tile.physicsType = TilePhysicsType.Air;
                    tile.stairType = 1;
                    break;

                case '/':
                    tile.physicsType = TilePhysicsType.Stair;
                    tile.slopeType = 1;
                    break;

                case '\\':
                    tile.physicsType = TilePhysicsType.Stair;
                    tile.slopeType = -1;
                    break;

                case '~':
                    tile.physicsType = TilePhysicsType.Air;
                    tile.hasWater = true;
                    break;

                case '.':
                case ' ':
                default:
                    tile.physicsType = TilePhysicsType.Air;
                    break;
            }

            return tile;
        }
    }
}
