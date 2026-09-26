using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Systems.Map;

namespace PFE.Tests.EditMode.Systems.Physics.LowLevelPhysics2D
{
    /// <summary>
    /// Builds a room's tile silhouette into static <see cref="PhysicsChain"/> shapes.
    ///
    /// <para>Stage A throwaway. Answers guide §5 question 3: what does one room cost as a chain?</para>
    ///
    /// <para><b>Single-sourcing.</b> The point of this class is that it reads tile solidity from
    /// <see cref="RoomInstance"/> / <see cref="TileData"/> only. It must never re-derive slope or
    /// platform interpretation — guide §1.2 point 5 calls out <c>TileCollider.cs:163-175</c> as a
    /// fifth, drifting interpretation of the slope, and a chain built here cannot be allowed to
    /// become a sixth.</para>
    /// </summary>
    public static class RoomChainBuilder
    {
        /// <summary>Result of one room build. Everything the cost report needs.</summary>
        public readonly struct BuildResult
        {
            public BuildResult(int chainCount, int pointCount, long elapsedMilliseconds, long memoryUsedBytes)
            {
                ChainCount = chainCount;
                PointCount = pointCount;
                ElapsedMilliseconds = elapsedMilliseconds;
                MemoryUsedBytes = memoryUsedBytes;
            }

            public int ChainCount { get; }
            public int PointCount { get; }
            public long ElapsedMilliseconds { get; }
            public long MemoryUsedBytes { get; }
        }

        /// <summary>
        /// Emits one static chain per exposed surface run: horizontal runs of "solid tile with air
        /// above" (the surfaces things stand on), plus each exposed vertical face (the surfaces
        /// horizontal projectiles hit).
        ///
        /// <para>Coordinates: room-local pixels, converted to Unity units by
        /// <see cref="Llp2d.PixelToUnit"/>. Tile grid <c>y</c> increases upward, matching
        /// <c>WorldCoordinates.PixelToUnity</c> which does not negate Y.</para>
        /// </summary>
        public static BuildResult Build(RoomInstance room, PhysicsWorld world, out PhysicsBody staticBody)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            staticBody = Llp2d.CreateBody(world, PhysicsBody.BodyType.Static, Vector2.zero, 0f);

            int chainCount = 0;
            int pointCount = 0;

            // ── Horizontal surfaces ────────────────────────────────────────────────────────────
            // A run is a maximal span of adjacent solid tiles in one row whose neighbour on the
            // given side (above / below) is not solid.
            pointCount += BuildHorizontalRuns(room, staticBody, above: true, ref chainCount);
            pointCount += BuildHorizontalRuns(room, staticBody, above: false, ref chainCount);

            // ── Vertical faces ─────────────────────────────────────────────────────────────────
            // Same idea transposed: a maximal span in one column of tiles whose left (or right)
            // neighbour is not solid. Merged into runs rather than emitted per tile because a chain
            // has a four-vertex minimum (see EmitRun).
            pointCount += BuildVerticalRuns(room, staticBody, leftSide: true, ref chainCount);
            pointCount += BuildVerticalRuns(room, staticBody, leftSide: false, ref chainCount);

            stopwatch.Stop();

            return new BuildResult(chainCount, pointCount, stopwatch.ElapsedMilliseconds, world.counters.memoryUsed);
        }

        private static int BuildHorizontalRuns(RoomInstance room, PhysicsBody body, bool above, ref int chainCount)
        {
            int pointCount = 0;

            for (int y = 0; y < room.height; y++)
            {
                int runStart = -1;

                for (int x = 0; x <= room.width; x++)
                {
                    bool solid = x < room.width && IsSolid(room, x, y);
                    bool exposed = solid && !IsSolid(room, x, above ? y + 1 : y - 1);

                    if (exposed && runStart < 0)
                    {
                        runStart = x;
                    }
                    else if (!exposed && runStart >= 0)
                    {
                        // Surface sits at the top of the tile when exposing upward, at the bottom
                        // when exposing downward.
                        float surfaceY = above ? y + 1 : y;
                        AddHorizontal(body, runStart, x, surfaceY, ref chainCount, ref pointCount);
                        runStart = -1;
                    }
                }
            }

            return pointCount;
        }

        private static int BuildVerticalRuns(RoomInstance room, PhysicsBody body, bool leftSide, ref int chainCount)
        {
            int pointCount = 0;

            for (int x = 0; x < room.width; x++)
            {
                int runStart = -1;

                for (int y = 0; y <= room.height; y++)
                {
                    bool solid = y < room.height && IsSolid(room, x, y);
                    bool exposed = solid && !IsSolid(room, leftSide ? x - 1 : x + 1, y);

                    if (exposed && runStart < 0)
                    {
                        runStart = y;
                    }
                    else if (!exposed && runStart >= 0)
                    {
                        float faceX = leftSide ? x : x + 1;
                        AddRun(body, faceX, runStart, faceX, y, ref chainCount, ref pointCount);
                        runStart = -1;
                    }
                }
            }

            return pointCount;
        }

        private static void AddHorizontal(
            PhysicsBody body, int tileX0, int tileX1, float tileY, ref int chainCount, ref int pointCount)
        {
            AddRun(body, tileX0, tileY, tileX1, tileY, ref chainCount, ref pointCount);
        }

        /// <summary>
        /// Emits one chain for a straight run between two tile-grid points.
        ///
        /// <para><b>Four-vertex minimum.</b> <c>ChainGeometry</c> throws
        /// <c>ArgumentOutOfRangeException: Chain Geometry must contain a minimum of 4 vertices</c>
        /// for a two-point polyline, verified by running it. A one- or two-tile ledge is common in
        /// PFE, so short runs are subdivided into four collinear vertices rather than being
        /// downgraded to a segment shape — keeping every room surface a genuine chain, which is the
        /// property that removes ghost collisions.</para>
        /// </summary>
        private static void AddRun(
            PhysicsBody body, float tileX0, float tileY0, float tileX1, float tileY1,
            ref int chainCount, ref int pointCount)
        {
            const int MinimumChainVertices = 4;

            float x0 = tileX0 * Llp2d.TileSizePx * Llp2d.PixelToUnit;
            float y0 = tileY0 * Llp2d.TileSizePx * Llp2d.PixelToUnit;
            float x1 = tileX1 * Llp2d.TileSizePx * Llp2d.PixelToUnit;
            float y1 = tileY1 * Llp2d.TileSizePx * Llp2d.PixelToUnit;

            // One vertex per tile boundary crossed, then pad to the minimum.
            int steps = Mathf.Max(
                Mathf.RoundToInt(Mathf.Max(Mathf.Abs(tileX1 - tileX0), Mathf.Abs(tileY1 - tileY0))),
                MinimumChainVertices - 1);

            var points = new Vector2[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                points[i] = new Vector2(Mathf.Lerp(x0, x1, t), Mathf.Lerp(y0, y1, t));
            }

            Llp2d.CreateChain(body, points);
            chainCount++;
            pointCount += points.Length;
        }

        /// <summary>
        /// The single solidity predicate for this builder. Deliberately delegates to
        /// <see cref="TileData.IsSolid"/> rather than reinterpreting <c>physicsType</c>: a platform
        /// and a wall are both walked on in AS3, and the difference is applied by <c>porog</c> at
        /// contact time, not by the geometry.
        /// </summary>
        private static bool IsSolid(RoomInstance room, int x, int y)
        {
            if (x < 0 || y < 0 || x >= room.width || y >= room.height)
            {
                return false;
            }

            TileData tile = room.tiles[x, y];
            return tile != null && tile.IsSolid();
        }
    }
}
