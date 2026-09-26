using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;

// The project declares its own `PFE.Systems.Physics.PhysicsBody : MonoBehaviour` (PhysicsBody.cs).
// Because this file lives in that same namespace, an unqualified `PhysicsBody` binds to the
// MonoBehaviour, not to the engine's Box2D body struct. Everything below therefore goes through
// this alias. Do not remove it.
using Llp2dBody = UnityEngine.LowLevelPhysics2D.PhysicsBody;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Builds one room's tile silhouette into static <see cref="PhysicsChain"/> shapes in a
    /// <see cref="PhysicsWorld"/>.
    ///
    /// <para><b>Single-sourcing.</b> Reads tile semantics exclusively through
    /// <see cref="ITileQueryService"/> — never re-derives slope or platform interpretation from
    /// <see cref="RoomInstance"/> directly. Guide §1.2 point 5: a chain built here must not become
    /// a sixth drifting interpretation of the slope.</para>
    ///
    /// <para><b>Coordinate space is world, not room-local.</b> Tiles are addressed by room-local tile
    /// index, but the chains are emitted in world units, because <see cref="ITileQueryService"/> is a
    /// world-pixel seam and every consumer of this world will query it with world positions. The
    /// room's <see cref="ITileQueryService.OriginPixel"/> is therefore added to every emitted vertex.
    /// A room at land position (0,0) with no border has a zero origin, so this is invisible to tests
    /// built on one — which is why <c>Build_RoomAwayFromOrigin_ChainsAreInWorldSpace</c> exists.</para>
    ///
    /// <para><b>Geometry is complete, not one-sided.</b> Every exposed surface is emitted in both
    /// directions: up-facing and down-facing horizontal runs, plus both vertical faces. A room
    /// whose ceilings were omitted would not be a mirror of the room, and the omission would only
    /// surface once Stage C attaches consumers. One-way platform behaviour is <i>not</i> expressed
    /// as missing geometry — it is applied at contact time by a pre-solve callback (decision L3),
    /// which requires the surface to be present in the first place.</para>
    ///
    /// <para><b>Stage B scope.</b> Geometry matches the Stage A <c>RoomChainBuilder</c> that was
    /// measured for guide §5 question 3, so Stage B0's re-measurement on a real
    /// <c>RoomTemplate</c> stays comparable. Slope <i>diagonals</i> are deliberately not emitted
    /// yet: <see cref="TileQueryFlags"/> exposes <see cref="TileQueryFlags.Slope"/> but no
    /// orientation, so emitting a diagonal would require reaching around the seam into
    /// <c>TileData.slopeType</c>. A slope tile therefore contributes its tile-top surface, exactly
    /// as in Stage A. Adding orientation to the seam is a Stage C item.</para>
    ///
    /// <para><b>Chain minimum.</b> Box2D v3's <c>ChainGeometry</c> throws for fewer than 4
    /// vertices. Short runs are subdivided to 4 collinear vertices rather than downgraded to
    /// segment shapes — keeping every surface a genuine chain, which is the property that removes
    /// ghost collisions at seams.</para>
    ///
    /// <para><b>Open chains lose their end edges.</b> Box2D gives an open chain no collision on its
    /// first and final edge, so a run subdivided down to the four-vertex minimum would leave its end
    /// tiles inert — a single-tile surface would keep only its middle third, and a projectile would
    /// pass straight through the rest. Every run is therefore emitted with one extra vertex of
    /// lead-in and lead-out, which is where those two discarded edges land. See
    /// <see cref="EmitChain"/>; <c>Build_ShortRun_WholeSurfaceIsCollidableIncludingItsEnds</c> pins
    /// it.</para>
    /// </summary>
    public sealed class RoomChainGeometry
    {
        /// <summary>
        /// <c>ChainGeometry</c> throws <c>ArgumentOutOfRangeException</c> below this. Verified by
        /// running it — see <c>RoomChainBuilder.AddRun</c>.
        /// </summary>
        private const int MinimumChainVertices = 4;

        /// <summary>100 px = 1 Unity unit. AS3 is authored in pixels; a world is in units.</summary>
        private const float PixelToUnit = 0.01f;

        /// <summary>
        /// Tiles are authored in pixels; this is the only tile-size constant. Not multiplied up into
        /// a unit form here: every coordinate in this file is converted through pixels so that the
        /// room-origin step in <see cref="EmitChain"/> cannot be skipped by accident.
        /// </summary>
        private const float TileSizePx = TileQueryConstants.TileSize;

        private readonly ITileQueryService _query;
        private readonly Llp2dBody _staticBody;

        /// <summary>
        /// The chains this builder owns, so a rebuild can destroy exactly what it created.
        ///
        /// <para>Chains cannot be enumerated-and-destroyed through <c>PhysicsBody.GetShapes</c>:
        /// that call returns a <c>NativeArray</c> needing disposal, and
        /// <c>PhysicsShape.Destroy</c> explicitly refuses chain segments ("Shapes of type Chain
        /// cannot be destroyed here, they must be destroyed by their owning chain"). The supported
        /// route is <c>PhysicsChain.Destroy(int)</c>, so the handles are kept here.</para>
        /// </summary>
        private readonly List<PhysicsChain> _chains = new List<PhysicsChain>();

        /// <summary>Number of chains emitted by the last <see cref="Build"/>.</summary>
        public int ChainCount { get; private set; }

        /// <summary>Total vertices across all chains emitted by the last <see cref="Build"/>.</summary>
        public int PointCount { get; private set; }

        public RoomChainGeometry(ITileQueryService query, PhysicsWorld world)
        {
            _query = query ?? throw new System.ArgumentNullException(nameof(query));
            if (!world.isValid)
                throw new System.ArgumentException("World must be valid", nameof(world));

            PhysicsBodyDefinition def = PhysicsBodyDefinition.defaultDefinition;
            def.type = Llp2dBody.BodyType.Static;
            def.position = Vector2.zero;
            def.rotation = PhysicsRotate.identity;
            // Static geometry must not be dragged down by the world's gravity when a consumer
            // later switches the body's type or the world is reconfigured. Explicit, not implied.
            def.gravityScale = 0f;

            _staticBody = world.CreateBody(def);
        }

        /// <summary>The static body that owns all chains for this room.</summary>
        public Llp2dBody StaticBody => _staticBody;

        /// <summary>
        /// Build all chains for the room. Destroys any previously built chains on this body first,
        /// so calling it twice rebuilds rather than accumulates.
        /// </summary>
        public void Build()
        {
            RoomInstance room = _query.Room;
            if (room == null || room.tiles == null) return;

            DestroyExistingChains();

            ChainCount = 0;
            PointCount = 0;

            // Horizontal surfaces, both directions: what things stand on, and what they hit
            // jumping upward.
            BuildHorizontalRuns(room, facingUp: true);
            BuildHorizontalRuns(room, facingUp: false);

            // Vertical faces: exposed left and right sides of solid tiles.
            BuildVerticalRuns(room, leftSide: true);
            BuildVerticalRuns(room, leftSide: false);
        }

        /// <summary>
        /// Releases every chain this builder owns, so a rebuild cannot silently double the room's
        /// shape count. Uses <see cref="PhysicsChain.Destroy"/>, the only route that works for
        /// chain segments.
        /// </summary>
        private void DestroyExistingChains()
        {
            for (int i = 0; i < _chains.Count; i++)
            {
                PhysicsChain chain = _chains[i];
                if (chain.isValid)
                {
                    // 0: the owner key. These chains are never SetOwner'd, so the parameter is
                    // unused — same convention as Llp2d.DestroyWorld.
                    chain.Destroy(0);
                }
            }

            _chains.Clear();
        }

        /// <summary>
        /// Releases this room's chains and its static body. Called on room deactivation.
        /// Idempotent: safe to call twice.
        /// </summary>
        public void Destroy()
        {
            DestroyExistingChains();

            if (_staticBody.isValid)
            {
                // Also releases any attached shapes/joints, per PhysicsBody.Destroy's contract.
                _staticBody.Destroy(0);
            }
        }

        // ── Horizontal runs (up-facing and down-facing) ──────────────────────────────────────

        private void BuildHorizontalRuns(RoomInstance room, bool facingUp)
        {
            for (int y = 0; y < room.height; y++)
            {
                int runStart = -1;

                for (int x = 0; x <= room.width; x++)
                {
                    bool solid = x < room.width && IsSolidAt(x, y);
                    bool exposed = solid && !IsSolidAt(x, facingUp ? y + 1 : y - 1);

                    if (exposed && runStart < 0)
                    {
                        runStart = x;
                    }
                    else if (!exposed && runStart >= 0)
                    {
                        // The surface sits at the tile's top when facing up, at its bottom when
                        // facing down. Row 0 is the TOP row and tile y increases upward, so
                        // "top of tile y" is y+1 in tile space.
                        float surfaceY = facingUp ? y + 1 : y;
                        EmitChain(runStart, surfaceY, x, surfaceY);
                        runStart = -1;
                    }
                }
            }
        }

        // ── Vertical runs (left and right faces) ─────────────────────────────────────────────

        private void BuildVerticalRuns(RoomInstance room, bool leftSide)
        {
            for (int x = 0; x < room.width; x++)
            {
                int runStart = -1;

                for (int y = 0; y <= room.height; y++)
                {
                    bool solid = y < room.height && IsSolidAt(x, y);
                    bool exposed = solid && !IsSolidAt(leftSide ? x - 1 : x + 1, y);

                    if (exposed && runStart < 0)
                    {
                        runStart = y;
                    }
                    else if (!exposed && runStart >= 0)
                    {
                        float faceX = leftSide ? x : x + 1;
                        EmitChain(faceX, runStart, faceX, y);
                        runStart = -1;
                    }
                }
            }
        }

        // ── Emission ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Emits one chain for a straight run between two tile-grid points, subdividing to the
        /// four-vertex minimum that <c>ChainGeometry</c> requires and then extending the run by one
        /// subdivision step at each end.
        ///
        /// <para><b>Why the extension is mandatory, not cosmetic.</b> Box2D discards the end edges of
        /// an open chain: <i>"An open chain has no collision on the first and final edge"</i>
        /// (shipped XML docs, <c>PhysicsChain</c>). Emitting the surface alone therefore makes its two
        /// end tiles inert — a four-vertex run keeps only its middle edge, and a single-tile run
        /// keeps nothing but the middle third of that tile. The lead-in and lead-out emitted here are
        /// exactly the two edges Box2D throws away, so every edge of the <i>intended</i> surface ends
        /// up live. They cost two vertices and add no collision.</para>
        /// </summary>
        private void EmitChain(float tileX0, float tileY0, float tileX1, float tileY1)
        {
            // Room-local tiles -> world pixels -> world units. The origin step is NOT optional:
            // ITileQueryService is a WORLD-PIXEL seam (see its "COORDINATE SPACE" note and
            // OriginPixel), so omitting it displaces the whole mirror of every room except the one
            // at land position (0,0) by landPosition * ROOM_SIZE — one full room width per land
            // step. That is invisible to any test built on an origin room, which is exactly how it
            // survived the whole Stage B0/B1 suite.
            Vector2 originPx = _query.OriginPixel;

            float x0 = (tileX0 * TileSizePx + originPx.x) * PixelToUnit;
            float y0 = (tileY0 * TileSizePx + originPx.y) * PixelToUnit;
            float x1 = (tileX1 * TileSizePx + originPx.x) * PixelToUnit;
            float y1 = (tileY1 * TileSizePx + originPx.y) * PixelToUnit;

            // One vertex per tile boundary crossed, then padded to the minimum. Degenerate runs
            // (a zero-length span, which the run scanner cannot currently produce) collapse to a
            // single vertex; `steps` is forced non-zero so the division below is safe.
            int steps = Mathf.Max(
                Mathf.RoundToInt(Mathf.Max(Mathf.Abs(tileX1 - tileX0), Mathf.Abs(tileY1 - tileY0))),
                MinimumChainVertices - 1);

            // One subdivision step, which doubles as the lead-in/lead-out length. Even the shortest
            // run spaces vertices by 1/3 tile = 0.133 units, far above PhysicsWorld.linearSlop
            // (0.005), which is the only spacing constraint Box2D imposes.
            var step = new Vector2((x1 - x0) / steps, (y1 - y0) / steps);

            // steps + 3 vertices: one lead-in before the surface, the surface's own steps + 1, and
            // one lead-out after it. Index 0 therefore sits one step *before* (x0, y0) and the last
            // index one step *after* (x1, y1) — so the surface spans indices 1..steps+1 and its
            // edges are the interior ones.
            var points = new Vector2[steps + 3];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = new Vector2(x0, y0) + step * (i - 1);
            }

            PhysicsChainDefinition def = PhysicsChainDefinition.defaultDefinition;
            def.isLoop = false;

            ChainGeometry geom = new ChainGeometry(new System.ReadOnlySpan<Vector2>(points));
            PhysicsChain chain = _staticBody.CreateChain(geom, def);

            // A rejected chain (degenerate geometry, world at its shape cap) must not be counted,
            // and must not enter the destroy list — Destroy on an invalid handle is not harmless.
            if (!chain.isValid)
            {
                return;
            }

            _chains.Add(chain);
            ChainCount++;
            PointCount += points.Length;
        }

        // ── Single-sourcing predicate ────────────────────────────────────────────────────────

        /// <summary>
        /// The one solidity predicate for this builder. Goes through
        /// <see cref="ITileQueryService.Classify"/> rather than reinterpreting
        /// <c>TileData.physicsType</c>, because a platform and a wall are both walked on in AS3 —
        /// the difference is applied by <c>porog</c> at contact time, not by the geometry.
        /// </summary>
        private bool IsSolidAt(int x, int y)
        {
            if (x < 0 || y < 0) return false;

            RoomInstance room = _query.Room;
            if (room == null || room.tiles == null) return false;
            if (x >= room.width || y >= room.height) return false;

            TileQueryFlags flags = _query.Classify(new Vector2Int(x, y));
            return (flags & TileQueryFlags.Solid) != 0
                || (flags & TileQueryFlags.Platform) != 0
                || (flags & TileQueryFlags.Slope) != 0;
        }
    }
}
