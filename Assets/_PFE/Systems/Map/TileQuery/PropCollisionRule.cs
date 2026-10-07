using UnityEngine;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// The single answer to "does this tile stop a <b>dynamic prop</b>?" — the third tile predicate,
    /// beside <see cref="ProjectileOcclusionRule"/> (the bullet's) and the motor's own step-up rule.
    ///
    /// <para><b>AS3 gives props their own predicate and it is not the bullet's and not a unit's.</b>
    /// <c>loc/Box.collisionTile</c> (<c>loc/Box.as:1260-1275</c>), reached from <c>collisionAll</c>
    /// (<c>:1207</c>):</para>
    ///
    /// <code>
    /// if(!tile || (tile.phis == 0 || tile.phis == 3) &amp;&amp; !tile.shelf) return 0;                        // :1262
    /// if(X2+dx &lt;= tile.phX1 || X1+dx &gt;= tile.phX2 || Y2+dy &lt;= tile.phY1 || Y1+dy &gt;= tile.phY2) return 0;
    /// if((tile.phis == 0 || tile.phis == 3) &amp;&amp; tile.shelf &amp;&amp; (Y2 &gt; tile.phY1 || levit || isThrow)) return 0;  // :1270
    /// return 1;
    /// </code>
    ///
    /// <para>Read in the port's <see cref="SurfaceKind"/> terms (Y is up here, AS3's is down, so the
    /// vertical comparisons mirror — see <see cref="BlocksProp"/>):</para>
    /// <list type="table">
    /// <item><term><see cref="SurfaceKind.Solid"/></term><description>blocks, if the candidate AABB
    /// overlaps the tile.</description></item>
    /// <item><term><see cref="SurfaceKind.Shelf"/></term><description>blocks <b>from above only</b>:
    /// once the prop's bottom is already below the shelf top it is through, and
    /// <c>levit</c>/<c>isThrow</c> bypass it unconditionally.</description></item>
    /// <item><term><see cref="SurfaceKind.Diagon"/> / <see cref="SurfaceKind.Stair"/></term>
    /// <description>pass — a ramp and a ladder are not obstacles to a prop.</description></item>
    /// </list>
    ///
    /// <para><b>Why this exists rather than reusing the shared AABB check.</b> The prop path used to
    /// call <c>RoomInstance.CheckCollision</c> → <c>TileCollisionMath.CheckCollision</c>, which is the
    /// <i>unit's</i> rule. It disagreed with AS3 in three places, and every one of them is a real
    /// divergence: stairs blocked (AS3 passes), ramps blocked (AS3 passes), and the one-way platform
    /// used a <c>porog</c> window with a caller-hardcoded <c>velocityY = 0</c> so the rising guard
    /// could never fire (AS3 uses a half-line with no window). Widening the shared function would have
    /// broken the bullet rule, and reusing <c>isTransparent</c> would have got ramps and ladders right
    /// <i>by accident</i> — which is not a contract.</para>
    /// </summary>
    public static class PropCollisionRule
    {
        /// <summary>
        /// Whether <paramref name="kind"/> blocks a prop whose AABB would be
        /// <paramref name="candidateBoundsPx"/> (this step's position) and currently is
        /// <paramref name="currentBoundsPx"/>. Both are in the same pixel space as
        /// <paramref name="tileBoundsPx"/>; the caller keeps them consistent.
        ///
        /// <para><c>currentBoundsPx</c> is required, not redundant: AS3's shelf clause tests the
        /// prop's <i>current</i> bottom (<c>Y2</c>, with no <c>dy</c>) against the shelf top, which is
        /// exactly the "have I already passed through" half of a one-way platform. Passing only the
        /// candidate would make every shelf permanently solid from above.</para>
        /// </summary>
        public static bool BlocksProp(
            SurfaceKind kind,
            Rect candidateBoundsPx,
            Rect currentBoundsPx,
            Rect tileBoundsPx,
            bool isLevitating,
            bool isThrown)
        {
            // :1262 — a slope or a ladder (phis 0/3 without `shelf`) never blocks, and neither does
            // air. Everything else — a wall, or a shelf — goes on to the overlap test.
            if (kind == SurfaceKind.None ||
                kind == SurfaceKind.Diagon ||
                kind == SurfaceKind.Stair)
            {
                return false;
            }

            // :1264-1265 — no overlap at the candidate position means no block. `Rect.Overlaps` is
            // strict (touching edges do not overlap), which is what AS3's `<=`/`>=` non-overlap test
            // means once the Y axis is mirrored.
            if (!candidateBoundsPx.Overlaps(tileBoundsPx))
            {
                return false;
            }

            // :1270 — a shelf blocks from above only. `Y2 > phY1` (bottom below the tile top, in
            // AS3's Y-down space) becomes `bottom < top` here; `levit` and `isThrow` bypass it
            // outright. Both are real flags the caller used to hardcode to `false`, which is why the
            // one-way behaviour was defeated for props even before this rule existed.
            if (kind == SurfaceKind.Shelf)
            {
                float shelfTopPx = tileBoundsPx.yMax;
                if (currentBoundsPx.yMin < shelfTopPx) return false;
                if (isLevitating || isThrown) return false;
            }

            return true;
        }

        /// <summary>
        /// The AABB form of the rule: does any tile overlapping
        /// <paramref name="candidateBoundsPx"/> block the prop? Iterates the same tile range the tile
        /// query does, in ROOM-LOCAL pixels (the space <c>TileData.GetBounds</c> and
        /// <c>ObjectInstance.GetApproximateBounds</c> both use).
        /// </summary>
        public static bool BlocksMove(
            RoomInstance room,
            Rect candidateBoundsPx,
            Rect currentBoundsPx,
            bool isLevitating,
            bool isThrown)
        {
            if (room == null || room.tiles == null) return false;

            int tileLeft = Mathf.FloorToInt(candidateBoundsPx.xMin / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt(candidateBoundsPx.xMax / WorldConstants.TILE_SIZE);
            int tileBottom = Mathf.FloorToInt(candidateBoundsPx.yMin / WorldConstants.TILE_SIZE);
            int tileTop = Mathf.FloorToInt(candidateBoundsPx.yMax / WorldConstants.TILE_SIZE);

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                for (int ty = tileBottom; ty <= tileTop; ty++)
                {
                    TileData tile = room.GetTileAtCoord(new Vector2Int(tx, ty));
                    if (tile == null) continue;

                    if (BlocksProp(
                        SurfaceKindRule.Of(tile),
                        candidateBoundsPx,
                        currentBoundsPx,
                        tile.GetBounds(),
                        isLevitating,
                        isThrown))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
