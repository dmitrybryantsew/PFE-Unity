using UnityEngine;
using PFE.Systems.Map.TileQuery;

namespace PFE.Entities.Units
{
    /// <summary>What a unit's horizontal step actually did.</summary>
    public readonly struct UnitWallResolution
    {
        /// <summary>
        /// How far the unit may move horizontally this step, in <b>world pixels</b>. Less than the
        /// requested delta when a wall stopped it part-way.
        /// </summary>
        public readonly float AppliedDeltaXPx;

        /// <summary>
        /// True when a wall stopped the unit. AS3's response to that is
        /// <c>dx = Math.abs(dx) * this.elast</c>, and <c>elast</c> is <c>0</c> for every shipped unit,
        /// so the caller zeroes the horizontal velocity rather than bouncing it.
        /// </summary>
        public readonly bool Blocked;

        public UnitWallResolution(float appliedDeltaXPx, bool blocked)
        {
            AppliedDeltaXPx = appliedDeltaXPx;
            Blocked = blocked;
        }
    }

    /// <summary>
    /// Resolves a unit's horizontal motion against the room's tiles — the column half of AS3's
    /// <c>Unit.run()</c> (<c>unit/Unit.as:2110-2240</c>).
    ///
    /// <para><b>Why this exists.</b> The port gave units no horizontal collision at all.
    /// <c>UnitController.Move</c> writes <c>_rb.MovePosition(...)</c>, and
    /// <c>MovePosition</c> on a <b>Kinematic</b> body is not blocked by static geometry — the port's
    /// own comment at that call site says so, and the body is set Kinematic in <c>Awake</c>. Ground is
    /// resolved, but only ever <i>downward</i> and only as a probe
    /// (<see cref="UnitGroundProbe"/>). So a unit walked, slid and was knocked straight through walls;
    /// the play-test report — "bullets can push back target through wall" — is that, and it was only
    /// half the report. The other half was the missing px/frame → units/s conversion in
    /// <c>DamageSystem.ApplyKnockback</c>.</para>
    ///
    /// <para><b>Why not port <c>run()</c>.</b> AS3 resolves one column at a time inside the same loop
    /// that applies <c>X += dx</c>, with the wall damage, the destructible-tile branch, the shelf
    /// exception and the step-up all interleaved (<c>:2119-2238</c> left, <c>:2196-2238</c> right).
    /// The port already has a sub-stepped swept resolver that does the same job and is already tested
    /// — <see cref="ITileQueryService.ResolveMove"/> — so this is a thin adapter onto it rather than a
    /// second implementation. It had <b>no callers at all</b> before this.</para>
    ///
    /// <para><b>Horizontal only, deliberately.</b> The delta passed here is <c>(dx, 0)</c>, so
    /// <c>ResolveMove</c>'s vertical pass is a no-op and the existing gravity / groundedness design
    /// (<c>ResolveGroundState</c> + <c>ApplyGravity</c>) is untouched. Vertical motion is not the
    /// reported fault, and changing it here would move every unit's fall at once.</para>
    ///
    /// <para><b>Step-up is not implemented, and the reason is not laziness.</b> AS3's gate is
    /// <c>Y2 - _loc2_.phY1 &lt;= (stay ? porog : porog_jump)</c> — the unit's feet may be at most
    /// <c>porog</c> (10 px, <c>TileQueryConstants.PorogGrounded</c>) below the obstacle's top, and
    /// <c>4</c> px airborne. That fires on <i>sub-tile</i> surfaces. The port's tile geometry has none:
    /// <c>TileCollisionMath.CheckTileCollisionAt</c> — the function <c>ResolveMove</c> steps with —
    /// classifies by grid coordinate and treats every tile as a full 40 px cell, so an obstacle's top is
    /// always a multiple of 40 and the smallest possible step is 39 px. The gate could never fire, and a
    /// branch that cannot fire is worse than no branch: it reads as live. It becomes live the moment the
    /// resolver honours <c>TileData.heightLevel</c>, which shifts a tile's bounds in 10 px steps —
    /// <c>HasSolidOverlapAt</c> already does, and <c>CheckTileCollisionAt</c> does not, which is the
    /// inconsistency to fix first.</para>
    /// </summary>
    public static class UnitWallMotion
    {
        /// <summary>
        /// How far the move box's bottom is lifted before the query — see
        /// <see cref="ToMoveBoxPixels"/>.
        /// </summary>
        public const float BottomInsetPixels = 1f;

        /// <summary>
        /// The box to sweep, in <b>world pixels</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the bottom is lifted by <see cref="BottomInsetPixels"/>.</b>
        /// <c>ResolveMove</c> decides which rows to test from the <i>feet</i>:
        /// <c>Mathf.FloorToInt((bottom - originY) / TILE_SIZE)</c>. A unit is seated 1 px above the
        /// surface (<c>UnitGroundProbe.SeatPixels</c>), so a box bottomed at the feet floors into the
        /// row <i>above</i> the floor slab and the floor is never tested — which is what makes horizontal
        /// movement possible at all. The cliff is two pixels wide: a bottom of 40 (the boundary itself)
        /// still floors upward, but a bottom of 39 floors into the floor's own row and the unit is then
        /// blocked by the ground it is standing on. A unit that has been shoved a few pixels down — by
        /// knockback, or by a spawn anchored short — would land there and be frozen. The inset moves the
        /// whole cliff down with it, and costs nothing: the bottom pixel of a box is not what stops a
        /// unit at a wall.</para>
        ///
        /// <para>Built from <c>Collider2D.bounds</c> rather than re-derived from grid coordinates, for
        /// the same reason <see cref="UnitGroundProbe"/> is — so there is no convention to get wrong.</para>
        /// </remarks>
        public static TileBox ToMoveBoxPixels(Bounds worldBounds, float pixelToUnit)
        {
            if (pixelToUnit <= 0f)
            {
                pixelToUnit = TileQueryConstants.PixelToUnit;
            }

            float unitsToPixels = 1f / pixelToUnit;

            float halfWidth = worldBounds.extents.x * unitsToPixels;
            float bottom    = worldBounds.min.y * unitsToPixels + BottomInsetPixels;
            float top       = worldBounds.max.y * unitsToPixels;

            float height = top - bottom;
            if (height <= 0f)
            {
                // A degenerate collider still has to produce a usable box: a zero-height box makes
                // CheckTileCollisionAt's width/height guard reject every query, which would read as
                // "walls do not exist" rather than "the collider is malformed".
                height = TileQueryConstants.PixelToUnit;
            }

            return TileBox.FromMinMax(
                worldBounds.center.x * unitsToPixels - halfWidth, bottom,
                worldBounds.center.x * unitsToPixels + halfWidth, bottom + height);
        }

        /// <summary>
        /// Sweeps <paramref name="boxPx"/> by <paramref name="deltaXPx"/> and reports how far it may
        /// actually go.
        /// </summary>
        /// <param name="tileQuery">The room's tile service. A null one resolves the move unblocked —
        /// a unit outside any room has no walls to hit, and refusing to move it would be a worse
        /// failure than letting it walk.</param>
        /// <param name="boxPx">The unit's box, world pixels. See <see cref="ToMoveBoxPixels"/>.</param>
        /// <param name="deltaXPx">Requested horizontal movement, world pixels.</param>
        public static UnitWallResolution Resolve(ITileQueryService tileQuery, in TileBox boxPx, float deltaXPx)
        {
            if (tileQuery == null || deltaXPx == 0f)
            {
                return new UnitWallResolution(deltaXPx, blocked: false);
            }

            // Solid only. Horizontal blocking in the tile model is Wall-only by construction
            // (`TileCollisionMath.CheckTileCollisionAt`), so asking for Platform or Ladder would change
            // nothing here — and the vertical pass is a no-op because delta.y is 0, which is what keeps
            // this from also re-deciding where the unit stands.
            TileMoveResult moved = tileQuery.ResolveMove(
                boxPx, new Vector2(deltaXPx, 0f), TileQueryFlags.Solid);

            // `ResolveMove` reports only the side the step was moving toward — it sets `HitLeft` from a
            // negative sub-step and `HitRight` from a positive one — so this is exact rather than
            // defensive, and a unit backing away from a wall it was flush against is not reported as
            // blocked by it. AS3 has the same shape: it branches on the SIGN of `dx` and resolves only
            // the leading edge (`Unit.as:2119` leftward, `:2196` rightward).
            bool blocked = moved.HitLeft || moved.HitRight;

            // ResolveMove's Position is the box CENTRE, so the applied delta is the difference from
            // where the box started. When blocked it has already been clamped flush to the wall face,
            // which is AS3's `X = _loc2_.phX2 + scX / 2` (`:2142`).
            return new UnitWallResolution(moved.Position.x - boxPx.Center.x, blocked);
        }
    }
}
