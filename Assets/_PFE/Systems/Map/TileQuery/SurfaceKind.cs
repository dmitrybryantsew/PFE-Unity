namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// The AS3 <i>surface identity</i> of a tile cell — which of the four things a cell can be to
    /// something moving through it.
    ///
    /// <para><b>Why this is a separate enum and not more bits on <see cref="TileQueryFlags"/>.</b>
    /// The flags answer "what properties does this cell have" and are read with <c>&amp;</c> tests
    /// that predate this concept, so adding bits to that mask would silently give every existing
    /// <c>&amp; TileQueryFlags.Solid</c> a new meaning. A cell has exactly ONE surface kind, so a
    /// single value is the honest type; a consumer that needs a <i>set</i> (a sweep that collides with
    /// walls and catwalks but not ramps) ORs the values it wants, which is why this is <c>[Flags]</c>
    /// rather than a plain enum.</para>
    ///
    /// <para><b>It is the AS3 identity, not the port's decode.</b> AS3 distinguishes these by
    /// <c>phis</c> plus the <c>shelf</c>/<c>stair</c>/<c>diagon</c> flags (<c>loc/Box.as:1260-1275</c>),
    /// and the port's <see cref="TilePhysicsType"/> does <b>not</b> map one-to-one onto them: a
    /// <c>diagon</c> form carries <c>phis = 0</c> and decodes to <c>Air</c> plus a <c>slopeType</c>, and
    /// a <c>shelf</c> decodes to <c>Platform</c> while AS3's <c>phis == 2</c> also decodes to
    /// <c>Wall</c>. Naming the kind here keeps each consumer's predicate readable in AS3's terms
    /// instead of re-deriving it from that mapping at every call site.</para>
    ///
    /// <para><b>What each kind means to each consumer</b> — the whole point of one classification with
    /// a predicate per consumer (guide §7.2 consequence 2):</para>
    /// <list type="table">
    /// <item><term>projectile</term><description><see cref="Solid"/> only —
    /// <c>weapon/Bullet.as:476</c> reads <c>phis == 1 || phis == 2</c> and nothing else, so it passes
    /// catwalks, ramps and ladders.</description></item>
    /// <item><term>dynamic prop</term><description><see cref="Solid"/> blocks;
    /// <see cref="Shelf"/> blocks from above only; <see cref="Diagon"/> and <see cref="Stair"/> pass —
    /// <c>loc/Box.as:1260-1275</c>.</description></item>
    /// </list>
    /// </summary>
    [System.Flags]
    public enum SurfaceKind
    {
        /// <summary>Nothing to collide with — air, water, or a destroyed tile.</summary>
        None = 0,

        /// <summary>AS3 <c>phis == 1 || phis == 2</c> — a wall. Blocks every consumer.</summary>
        Solid = 1 << 0,

        /// <summary>AS3 <c>phis == 0</c> + <c>shelf</c> — a one-way catwalk. Blocks only from above.</summary>
        Shelf = 1 << 1,

        /// <summary>AS3 <c>phis == 0 || phis == 3</c> + <c>stair</c> — a climbable ladder/stair. Blocks nothing.</summary>
        Stair = 1 << 2,

        /// <summary>AS3 <c>phis == 0</c> + <c>diagon</c> — a walkable ramp. Blocks nothing.</summary>
        Diagon = 1 << 3,
    }

    /// <summary>
    /// The single answer to "what surface kind is this tile?" — the AS3 identity, derived in one place.
    ///
    /// <para>Deliberately beside <see cref="ProjectileOcclusionRule"/>: that rule is the bullet's
    /// predicate, this is the classification every per-consumer predicate reads. Both are pure
    /// functions of <see cref="TileData"/>, both are asserted directly so they cannot drift, and both
    /// are reached through <see cref="ITileQueryService"/> by a consumer that holds a query service
    /// (see <see cref="ITileQueryService.ClassifySurface"/>).</para>
    ///
    /// <para><b>Precedence follows the port's own decode.</b> <c>physicsType</c> decides first; then a
    /// slope is read from <c>slopeType</c> before a ladder from <c>stairType</c>, because
    /// <c>TileData.IsClimbableLadder()</c> already requires <c>slopeType == 0</c> — so the two can
    /// never both be true, and the order only documents which wins if the data is ever inconsistent.
    /// The final <c>Stair</c> branch catches an AS3 <c>phis == 3</c> form that carries no climb
    /// metadata of its own: still a stair kind (it passes, per <c>Box.as:1262</c>), just not
    /// climbable.</para>
    /// </summary>
    public static class SurfaceKindRule
    {
        /// <summary>
        /// The kind of <paramref name="tile"/>. A null tile — an unbuilt cell, or a collider carrying no
        /// <see cref="TileData"/> — is <see cref="SurfaceKind.None"/>, never a surface: assuming a
        /// surface where the semantics are unknown is the louder failure of the two, exactly as in
        /// <see cref="ProjectileOcclusionRule.BlocksProjectile"/>.
        /// </summary>
        public static SurfaceKind Of(TileData tile)
        {
            if (tile == null) return SurfaceKind.None;

            if (tile.physicsType == TilePhysicsType.Wall) return SurfaceKind.Solid;
            if (tile.physicsType == TilePhysicsType.Platform) return SurfaceKind.Shelf;

            // `slopeType != 0` (TileData.IsSlopeSurface) — an AS3 `diagon`, which the port decodes to
            // Air rather than to a physicsType of its own. Read before the ladder test because
            // IsClimbableLadder already excludes a non-zero slopeType.
            if (tile.IsSlopeSurface()) return SurfaceKind.Diagon;
            if (tile.IsClimbableLadder()) return SurfaceKind.Stair;

            // AS3 `phis == 3` with no climb metadata — still a stair kind; see the class note.
            if (tile.physicsType == TilePhysicsType.Stair) return SurfaceKind.Stair;

            return SurfaceKind.None;
        }
    }
}
