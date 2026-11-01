using System;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// A unit's collision box in <b>AS3 room-local pixels, Y growing down</b> — the space
    /// <c>Unit.collisionAll</c> works in.
    ///
    /// <para><b>Feet-anchored, not centred.</b> AS3 writes <c>X1 = X - scX/2</c>, <c>X2 = X + scX/2</c>,
    /// <c>Y1 = Y - scY</c>, <c>Y2 = Y</c> (<c>Unit.as:2504-2507</c>), so <c>Y2</c> is the feet and the box
    /// stands entirely <i>above</i> them. The port's units are built the same way — the
    /// <c>BoxCollider2D</c> offset is <c>(0, height/2)</c>, which makes <c>transform.position</c> the
    /// feet (<c>UnitController.cs:757-760</c>). A box centred on the feet is half a body low, which
    /// reads as a pet that collides with the floor under it and nothing above it.</para>
    /// </summary>
    public readonly struct CollisionBody
    {
        /// <summary>Left edge, <c>X1</c>.</summary>
        public readonly float X1;

        /// <summary>Right edge, <c>X2</c>.</summary>
        public readonly float X2;

        /// <summary>Top edge, <c>Y1</c> (the smaller number — Y grows down).</summary>
        public readonly float Y1;

        /// <summary>Bottom edge, <c>Y2</c> — the feet.</summary>
        public readonly float Y2;

        /// <summary>AS3 <c>transT</c> — <c>phis == 3</c> tiles are not solid to this body.</summary>
        public readonly bool TransparentToGhostWalls;

        /// <summary>
        /// The <c>throu || t_throw &gt; 0 || levit || isFly || diagon != 0</c> disjunction of
        /// <c>Unit.as:2578</c>, collapsed into one flag: any of them lets the body pass a shelf.
        /// </summary>
        public readonly bool IgnoresShelves;

        /// <summary>AS3 <c>stay</c> — selects <c>porog</c> rather than <c>porog_jump</c>.</summary>
        public readonly bool Stays;

        /// <summary>AS3 <c>porog</c> — the step allowance used when <see cref="Stays"/>.</summary>
        public readonly float StepAllowance;

        /// <summary>AS3 <c>porog_jump</c> — the step allowance used otherwise.</summary>
        public readonly float JumpStepAllowance;

        public CollisionBody(
            float x1, float x2, float y1, float y2,
            bool transparentToGhostWalls = false,
            bool ignoresShelves = false,
            bool stays = false,
            float stepAllowance = 0f,
            float jumpStepAllowance = 0f)
        {
            X1 = x1;
            X2 = x2;
            Y1 = y1;
            Y2 = y2;
            TransparentToGhostWalls = transparentToGhostWalls;
            IgnoresShelves = ignoresShelves;
            Stays = stays;
            StepAllowance = stepAllowance;
            JumpStepAllowance = jumpStepAllowance;
        }

        /// <summary>
        /// The box a body of this size occupies when its <b>feet</b> are at
        /// (<paramref name="feetX"/>, <paramref name="feetY"/>) — the oracle's four assignments in one
        /// place, so no caller can centre the box by accident.
        /// </summary>
        public static CollisionBody FromFeet(
            float feetX, float feetY, float halfWidth, float height,
            bool transparentToGhostWalls = false,
            bool ignoresShelves = false,
            bool stays = false,
            float stepAllowance = 0f,
            float jumpStepAllowance = 0f)
        {
            return new CollisionBody(
                feetX - halfWidth, feetX + halfWidth,
                feetY - height, feetY,
                transparentToGhostWalls, ignoresShelves, stays, stepAllowance, jumpStepAllowance);
        }

        /// <summary>The step allowance this body's <c>stay</c> flag selects.</summary>
        public float EffectiveStepAllowance => Stays ? StepAllowance : JumpStepAllowance;
    }

    /// <summary>
    /// One tile as <c>Unit.collisionTile</c> sees it — <b>its physics box</b>, not its cell.
    ///
    /// <para><c>phX1..phY2</c> are not the cell's corners: <c>Tile.setZForm</c> writes
    /// <c>phY1 = (Y + zForm/4) * tileY</c> (<c>loc/Tile.as:281-297</c>), so a raised tile is solid only
    /// in the lower <c>1 - zForm/4</c> of its cell. A rule that tests "is this cell solid" reports a
    /// collision for the empty top of a quarter tile, which is the defect the motor stopped making on
    /// 2026-10-30 (<c>docs/Physics/HalfAndQuarterTiles_2026-10-30.md</c>).</para>
    /// </summary>
    public readonly struct CollisionTile
    {
        /// <summary>Left edge, <c>phX1</c>.</summary>
        public readonly float X1;

        /// <summary>Right edge, <c>phX2</c>.</summary>
        public readonly float X2;

        /// <summary>Top edge, <c>phY1</c> — lowered by <c>zForm</c>.</summary>
        public readonly float Y1;

        /// <summary>Bottom edge, <c>phY2</c> — never written by <c>setZForm</c>.</summary>
        public readonly float Y2;

        /// <summary>AS3 <c>phis != 0</c>. <c>phis == 0</c> is air.</summary>
        public readonly bool Solid;

        /// <summary>
        /// AS3 <c>phis == 3</c> — a runtime ghost wall (<c>Spell.as:446</c>). A body with
        /// <see cref="CollisionBody.TransparentToGhostWalls"/> passes it, which is the whole point of
        /// the <c>transT</c> flag; the port's importer collapses 3 into <c>Wall</c> and nothing creates
        /// one at runtime yet, so the adapter passes <c>false</c>.
        /// </summary>
        public readonly bool GhostWall;

        /// <summary>AS3 <c>shelf</c> — a one-way platform.</summary>
        public readonly bool Shelf;

        public CollisionTile(float x1, float x2, float y1, float y2,
            bool solid, bool ghostWall = false, bool shelf = false)
        {
            X1 = x1;
            X2 = x2;
            Y1 = y1;
            Y2 = y2;
            Solid = solid;
            GhostWall = ghostWall;
            Shelf = shelf;
        }

        /// <summary>
        /// Build a tile from the port's <b>room-local, y-up</b> rect — <c>TileData.GetBounds()</c> —
        /// mirroring it into AS3's y-down space.
        ///
        /// <para><b>The two edges swap.</b> A y-up rect's <c>yMax</c> is its top, which is AS3's
        /// <c>phY1</c> (the smaller number); its <c>yMin</c> is AS3's <c>phY2</c>. Writing
        /// <c>Y1 = H - yMin</c> puts the tile's box <i>below</i> the cell it belongs to — a quarter-tile
        /// then reads as solid where it is empty and empty where it is solid, which is exactly the
        /// half/quarter-tile defect in its second form.</para>
        /// </summary>
        public static CollisionTile FromPortBounds(
            float x1, float x2, float yMinUp, float yMaxUp, float roomHeightPixels,
            bool solid, bool ghostWall = false, bool shelf = false)
        {
            return new CollisionTile(
                x1, x2,
                y1: roomHeightPixels - yMaxUp,
                y2: roomHeightPixels - yMinUp,
                solid, ghostWall, shelf);
        }
    }

    /// <summary>
    /// The tile grid <see cref="UnitCollisionAllMath"/> walks — column and <b>AS3 row</b> (top-down),
    /// with each tile's physics box. An interface rather than a <c>RoomInstance</c> so the rule can be
    /// executed by an offline fixture against a hand-built grid.
    /// </summary>
    public interface ICollisionTileGrid
    {
        /// <summary>AS3 <c>loc.sky</c> — an open-sky location has no tile collision at all.</summary>
        bool OpenSky { get; }

        /// <summary>AS3 <c>loc.spaceX</c>.</summary>
        int Columns { get; }

        /// <summary>AS3 <c>loc.spaceY</c>.</summary>
        int Rows { get; }

        /// <summary><c>Tile.tileX</c> — 40 in every shipped room.</summary>
        float TileSize { get; }

        /// <summary>
        /// The tile at an <b>AS3</b> row. False means "no tile data here", which the rule treats as
        /// empty rather than solid — the oracle dereferences <c>loc.space</c> unguarded, so a missing
        /// cell is a port-side condition and must not manufacture a collision.
        /// </summary>
        bool TryGetTile(int column, int as3Row, out CollisionTile tile);
    }

    /// <summary>
    /// <c>Unit.collisionAll()</c> (<c>Unit.as:2541-2566</c>) and <c>collisionTile()</c>
    /// (<c>:2568-2583</c>) — "does this body overlap anything solid right now".
    ///
    /// <para><b>Why this exists as a rule.</b> The pet's <c>mater</c> latch (<c>UnitPet.as:586-596</c>)
    /// turns world collision on only once the pet is already overlapping something, and the port had a
    /// private approximation that asked "is the cell solid" — a different question from the oracle's
    /// AABB against <c>phX1..phY2</c>. The two agree only while every tile is a full 40 px cell and the
    /// body is nowhere near an edge, which is exactly the assumption the half/quarter-tile work
    /// invalidated.</para>
    ///
    /// <para><b>The oracle, arm for arm.</b></para>
    /// <code>
    /// if(loc.sky) return false;                                             // :2544
    /// for each cell column/row in floor(X1/tile)..floor(X2/tile) …
    ///    if(!(col &lt; 0 || col &gt;= spaceX || row &lt; 0 || row &gt;= spaceY)) // :2554
    ///       if(collisionTile(space[col][row])) return true;                // :2556
    ///
    /// collisionTile(t):                                                     // :2568
    ///   if(!t || (t.phis == 0 || transT &amp;&amp; t.phis == 3) &amp;&amp; !t.shelf) return 0;
    ///   if(X2 &lt;= t.phX1 || X1 &gt;= t.phX2 || Y2 &lt;= t.phY1 || Y1 &gt;= t.phY2) return 0;
    ///   if((t.phis == 0 || transT &amp;&amp; t.phis == 3) &amp;&amp; t.shelf
    ///      &amp;&amp; (Y2 - (stay ? porog : porog_jump) &gt; t.phY1
    ///          || throu || t_throw &gt; 0 || levit || isFly || diagon != 0)) return 0;
    ///   return 1;
    /// </code>
    ///
    /// <para><b>The boundaries are strict, and that is load-bearing.</b> A body whose bottom edge is
    /// exactly on a shelf's top does <b>not</b> collide (<c>Y2 &gt;= phY1</c> is the reject), which is
    /// why a unit can stand on one; a body whose right edge is exactly on a cell seam does not collide
    /// with the next column. Flooring the extents to cells loses both facts.</para>
    ///
    /// <para><b>Unity-free.</b> Only <see cref="System.Math"/>, so an offline fixture can execute every
    /// arm against a hand-built grid.</para>
    /// </summary>
    public static class UnitCollisionAllMath
    {
        /// <summary>
        /// <c>collisionAll()</c> over a whole grid. Returns true when <b>any</b> overlapped tile is
        /// solid to this body.
        /// </summary>
        public static bool Any(in CollisionBody body, ICollisionTileGrid grid)
        {
            if (grid == null) return false;

            // `if(loc.sky) return false;` (:2544) — an open-sky room skips tile collision entirely.
            if (grid.OpenSky) return false;

            float tileSize = grid.TileSize;
            if (tileSize <= 0f) return false;

            int left = (int)Math.Floor(body.X1 / tileSize);
            int right = (int)Math.Floor(body.X2 / tileSize);
            int top = (int)Math.Floor(body.Y1 / tileSize);
            int bottom = (int)Math.Floor(body.Y2 / tileSize);

            for (int column = left; column <= right; column++)
            {
                for (int row = top; row <= bottom; row++)
                {
                    // `if(!(_loc3_ < 0 || _loc3_ >= loc.spaceX || …))` (:2554) — out of bounds is
                    // SKIPPED, not solid: the room edge is not a wall.
                    if (column < 0 || column >= grid.Columns) continue;
                    if (row < 0 || row >= grid.Rows) continue;

                    if (!grid.TryGetTile(column, row, out CollisionTile tile)) continue;

                    if (CollidesWithTile(body, tile)) return true;
                }
            }

            return false;
        }

        /// <summary><c>collisionTile(tile)</c> for one tile — the three arms of <c>:2568-2583</c>.</summary>
        public static bool CollidesWithTile(in CollisionBody body, in CollisionTile tile)
        {
            // Arm 1 (:2570). `phis == 0` is air; `phis == 3` is a ghost wall that only a `transT`
            // body passes. Either way a non-shelf tile is not an obstacle.
            bool passableForm = !tile.Solid || (body.TransparentToGhostWalls && tile.GhostWall);
            if (passableForm && !tile.Shelf) return false;

            // Arm 2 (:2574). Strict: touching an edge is not overlapping it.
            if (!Overlaps(body, tile)) return false;

            // Arm 3 (:2578). Only a shelf in a passable form can be dropped here, and only from below:
            // `Y2 - allowance > phY1` means the feet are more than a step below the shelf's top.
            if (passableForm && tile.Shelf)
            {
                if (body.IgnoresShelves) return false;
                if (body.Y2 - body.EffectiveStepAllowance > tile.Y1) return false;
            }

            return true;
        }

        /// <summary>
        /// The oracle's AABB test, verbatim — <c>!(X2 &lt;= phX1 || X1 &gt;= phX2 || Y2 &lt;= phY1 ||
        /// Y1 &gt;= phY2)</c>. Edge contact is <b>not</b> overlap.
        /// </summary>
        public static bool Overlaps(in CollisionBody body, in CollisionTile tile)
        {
            return !(body.X2 <= tile.X1
                     || body.X1 >= tile.X2
                     || body.Y2 <= tile.Y1
                     || body.Y1 >= tile.Y2);
        }
    }
}
