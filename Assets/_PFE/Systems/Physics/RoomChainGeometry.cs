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
        private readonly PhysicsWorld _world;
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

        /// <summary>
        /// World-unit AABB of each chain, parallel to <see cref="_chains"/> by index.
        ///
        /// <para>This exists to keep <see cref="TrySweep"/> affordable. <c>PhysicsChain.CastShape</c>
        /// is narrow-phase only — it has no broadphase of its own — so testing every chain of the
        /// room on every projectile tick would be O(chains × segments) per projectile. A projectile
        /// sweep touches one or two runs, so the AABB rejects the rest in a couple of comparisons.
        /// The bounds are recorded at emission time from the actual emitted points (including the
        /// lead-in/lead-out), so they cannot drift from the geometry.</para>
        /// </summary>
        private readonly List<Rect> _chainBoundsUnits = new List<Rect>();

        /// <summary>Number of chains emitted by the last <see cref="Build"/>.</summary>
        public int ChainCount { get; private set; }

        /// <summary>Total vertices across all chains emitted by the last <see cref="Build"/>.</summary>
        public int PointCount { get; private set; }

        public RoomChainGeometry(ITileQueryService query, PhysicsWorld world)
        {
            _query = query ?? throw new System.ArgumentNullException(nameof(query));
            if (!world.isValid)
                throw new System.ArgumentException("World must be valid", nameof(world));

            _world = world;

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
            _chainBoundsUnits.Clear();
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

            // AABB of the points actually handed to Box2D — lead-in and lead-out included, so the
            // bounds can never be narrower than the geometry they describe. TrySweep rejects
            // non-candidate chains with this before paying for a narrow-phase cast.
            float minX = points[0].x, maxX = points[0].x;
            float minY = points[0].y, maxY = points[0].y;
            for (int i = 1; i < points.Length; i++)
            {
                Vector2 p = points[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }

            _chains.Add(chain);
            _chainBoundsUnits.Add(Rect.MinMaxRect(minX, minY, maxX, maxY));
            ChainCount++;
            PointCount += points.Length;
        }

        // ── Swept query (Stage C) ────────────────────────────────────────────────────────────

        /// <summary>
        /// Sweeps the projectile's shape through this room's chains and reports the nearest contact.
        ///
        /// <para><b>Why a sweep and not an overlap.</b> A chain is a <i>zero-thickness</i> surface, so
        /// a projectile overlaps it only while its shape straddles the line. AS3 bullets move up to
        /// 500 px/frame, far wider than that window, so an end-of-step overlap test would tunnel
        /// straight through walls. The sweep is the projectile's CCD, and it is the whole reason this
        /// query exists rather than reusing <c>TestOverlapGeometry</c> the way the B1 harness does.</para>
        ///
        /// <para><b>Why a capsule and not a box.</b> The shipped projectile prefab's collider is a
        /// <c>CapsuleCollider2D</c> of 0.93 x 0.06 units with a horizontal axis, and the projectile
        /// rotates to face its travel direction. So the hitbox is a <i>needle pointing where it
        /// flies</i>, whose leading tip is ~46 px ahead of the transform centre — not a dot. Treating
        /// it as a small box at the centre would let every bullet bury itself half a tile into a wall
        /// before registering the hit. Passing <paramref name="facing"/> is what keeps the leading tip
        /// leading.</para>
        ///
        /// <para><b>A square size is a circle, and must be built as one.</b> A capsule's straight
        /// section is its length less the two hemispherical caps, so a size whose length equals its
        /// thickness collapses that section to zero — and the engine <i>rejects</i> the resulting
        /// degenerate capsule rather than tolerating it. <c>PhysicsShape.ShapeProxy</c> throws
        /// <c>ArgumentException "Capsule Geometry is not valid"</c> for coincident centres. The shape
        /// is therefore built as a <c>CircleGeometry</c> in exactly that case. This is not
        /// hypothetical: <c>Llp2dStageCSweepTests</c> caught it, because the B1 size class it reuses
        /// (8x8 px) makes the section exactly zero, so every one of those scenarios threw instead of
        /// sweeping — and a square-ish projectile would have done the same in production.</para>
        ///
        /// <para><b>Initially-touching is handled explicitly.</b> The shipped docs for
        /// <c>PhysicsChain.CastShape</c> say <i>"Initially touching shapes are treated as a miss. You
        /// should check for overlap first if initial overlap is required."</i> Without that check a
        /// projectile spawned with its capsule already straddling a surface — muzzle inside a wall —
        /// would sweep clean through it.</para>
        ///
        /// <para><b>Cost.</b> Narrow-phase only, so the per-chain AABB recorded at emission time
        /// rejects non-candidates first. A typical sweep pays for one or two casts, not one per
        /// chain in the room.</para>
        /// </summary>
        /// <param name="fromPx">Shape centre at the start of the move, world pixels.</param>
        /// <param name="deltaPx">Translation over this step, world pixels.</param>
        /// <param name="sizePx">Shape (length, thickness) in pixels — length along
        /// <paramref name="facing"/>, thickness across it. Mirrors the prefab collider's size. A
        /// thickness equal to the length yields a circle; see the note above. Zero thickness has no
        /// geometry to sweep and is refused.</param>
        /// <param name="facing">Unit direction of the shape's long axis, normally the travel
        /// direction. Falls back to +X if degenerate, which only affects a zero-velocity query.</param>
        /// <param name="contactPointPx">Contact point in world pixels. Equals <paramref name="fromPx"/>
        /// when the report comes from an initial overlap, since no sweep fraction exists then.</param>
        /// <param name="contactNormal">Surface normal, unit-length, or zero for an initial overlap —
        /// <c>CastResult</c> documents the normal as degenerate in exactly that case.</param>
        /// <returns>True if the shape is already touching, or would touch during the move.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">If <paramref name="sizePx"/> has zero
        /// thickness, which cannot be swept.</exception>
        public bool TrySweep(Vector2 fromPx, Vector2 deltaPx, Vector2 sizePx, Vector2 facing,
                             out Vector2 contactPointPx, out Vector2 contactNormal)
        {
            contactPointPx = default;
            contactNormal  = default;

            if (_chains.Count == 0) return false;

            Vector2 fromUnits  = fromPx * PixelToUnit;
            Vector2 deltaUnits = deltaPx * PixelToUnit;
            Vector2 axis       = facing.sqrMagnitude > 0f ? facing.normalized : Vector2.right;

            float lengthUnits = sizePx.x * PixelToUnit;
            float radiusUnits = sizePx.y * PixelToUnit * 0.5f;

            // A shape with no thickness has no volume to sweep, and both the capsule and the circle
            // below would be rejected by the engine anyway — so this guard does not add a failure
            // mode, it only replaces a cryptic engine ArgumentException with one that names the cause.
            // Throwing is deliberate: reporting "no contact" would make bullets pass through walls,
            // the exact silent failure this seam exists to prevent.
            if (radiusUnits <= 0f)
            {
                throw new System.ArgumentOutOfRangeException(
                    nameof(sizePx),
                    $"Sweep size {sizePx} px has zero thickness, so it has no geometry to sweep.");
            }

            // A capsule's straight section is its total length less the two hemispherical caps.
            float halfSegment = Mathf.Max(0f, (lengthUnits - radiusUnits * 2f) * 0.5f);

            // When that section collapses to nothing the shape IS a circle, and the degenerate
            // capsule is REJECTED by the engine, not tolerated: PhysicsShape.ShapeProxy throws
            // "Capsule Geometry is not valid" for coincident centres. Built as a circle instead.
            // Both branches are chosen together so the overlap test and the cast cannot end up
            // holding different shapes.
            //
            // This also covers thickness > length, where the (length, thickness) pair is not
            // well-formed and Mathf.Max clamps the section to zero: the shape is then swept as a
            // circle of radius thickness/2. That is the larger reading of the two, so it errs toward
            // registering a contact rather than tunnelling through one.
            bool isCircle = halfSegment <= 0f;

            CapsuleGeometry capsule = default;
            CircleGeometry circle = default;

            if (isCircle)
            {
                circle = CircleGeometry.Create(radiusUnits, fromUnits);
            }
            else
            {
                capsule = CapsuleGeometry.Create(
                    fromUnits - axis * halfSegment,
                    fromUnits + axis * halfSegment,
                    radiusUnits);
            }

            // How far the shape reaches from its centre along any axis. Expanding both end poses
            // by this gives a conservative swept AABB — it may admit a few extra candidates, which
            // the narrow-phase cast then rejects. Cheap and, crucially, never too small. For the
            // circle case halfSegment is zero, so this correctly reduces to just the radius.
            float reach = halfSegment + radiusUnits;

            var filter = new PhysicsQuery.QueryFilter();

            // Scoped deliberately: TestOverlapGeometry answers for the whole world, and one world
            // serves the whole active room set (decision L1). Requiring the shape to be near THIS
            // room's own chains first keeps a projectile from being stopped by a neighbouring room's
            // geometry that merely happens to share the boundary.
            bool overlapsOwnGeometry = isCircle
                ? _world.TestOverlapGeometry(circle, filter)
                : _world.TestOverlapGeometry(capsule, filter);

            if (NearOwnGeometry(fromUnits, reach) && overlapsOwnGeometry)
            {
                contactPointPx = fromPx;
                contactNormal  = Vector2.zero;
                return true;
            }

            if (deltaUnits == Vector2.zero) return false;

            Vector2 toUnits = fromUnits + deltaUnits;
            var swept = Rect.MinMaxRect(
                Mathf.Min(fromUnits.x, toUnits.x) - reach,
                Mathf.Min(fromUnits.y, toUnits.y) - reach,
                Mathf.Max(fromUnits.x, toUnits.x) + reach,
                Mathf.Max(fromUnits.y, toUnits.y) + reach);

            var castInput = isCircle
                ? new PhysicsQuery.CastShapeInput(circle, deltaUnits)
                : new PhysicsQuery.CastShapeInput(capsule, deltaUnits);

            // Set explicitly rather than inherited from the constructor's default. The shipped docs
            // describe this field only as "typically 1", and this suite already carries a live
            // example of an unverified default being wrong (PhysicsQuery.QueryFilter: a
            // default-initialised one matches nothing, where `new QueryFilter()` hits everything).
            // A zero here would make every cast report a miss — silently, and only at runtime.
            castInput.maxFraction = 1f;

            // NOTE: CastShapeInput.canEncroach is the shipped flag for "an initially-touching shape
            // should count". It is deliberately NOT used: its initial-overlap output is documented
            // only as "normal zero, fraction zero", which does not say what `point` holds, and the
            // explicit overlap branch above is built on TestOverlapGeometry — already exercised by
            // the B1 suite. Revisit if the extra world query ever shows up in a profile.

            bool found = false;
            float bestFraction = float.MaxValue;

            for (int i = 0; i < _chains.Count; i++)
            {
                if (!_chainBoundsUnits[i].Overlaps(swept)) continue;

                PhysicsChain chain = _chains[i];
                if (!chain.isValid) continue;

                // The out parameter is the chain segment that was found; the projectile does not need
                // its identity (the contact point is what resolves the tile), so it is discarded.
                PhysicsQuery.CastResult cast = chain.CastShape(castInput, out PhysicsShape _);
                if (!cast.hit) continue;
                if (cast.fraction > bestFraction) continue;

                bestFraction   = cast.fraction;
                contactPointPx = cast.point * (1f / PixelToUnit);
                contactNormal  = cast.normal;
                found          = true;
            }

            return found;
        }

        /// <summary>True if any chain's recorded AABB lies within <paramref name="reach"/> of a point.</summary>
        private bool NearOwnGeometry(Vector2 centreUnits, float reach)
        {
            var probe = Rect.MinMaxRect(
                centreUnits.x - reach, centreUnits.y - reach,
                centreUnits.x + reach, centreUnits.y + reach);

            for (int i = 0; i < _chainBoundsUnits.Count; i++)
            {
                if (_chainBoundsUnits[i].Overlaps(probe)) return true;
            }
            return false;
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
