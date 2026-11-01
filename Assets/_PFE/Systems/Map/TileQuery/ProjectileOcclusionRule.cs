namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// The single answer to "does this tile stop a projectile?" — asked by the legacy per-tile
    /// collider path, the LowLevelPhysics2D chain mirror, and the thrown-object bounce.
    ///
    /// <para><b>AS3 settles this and it is not a judgement call.</b> A bullet's only tile test is
    /// <c>_loc3_.phis == 1 || _loc3_.phis == 2</c> against the cell it occupies
    /// (<c>weapon/Bullet.as:476</c>), and a physics bullet / grenade uses the same test narrowed to
    /// <c>_loc3_.phis == 1</c> (<c>weapon/PhisBullet.as:242</c>). <c>shelf</c>, <c>diagon</c> and
    /// <c>stair</c> are <i>never</i> consulted by either, and in <c>Data/tile_forms.json</c> every one
    /// of those forms carries <c>phis = 0</c> — only the 20 <c>fForms</c> and one <c>oForm</c> are
    /// <c>phis = 1</c>. Those three flags describe how things <b>move</b>: <c>diagon</c> is the
    /// walkable ramp height, <c>shelf</c> the one-way platform (<c>Box.as:1270</c>,
    /// <c>Unit.as:2578</c>), <c>stair</c> climbable.</para>
    ///
    /// <para>So a projectile passes through catwalks, slopes and ladders. In this port <c>phis</c> 1
    /// and 2 both decode to <see cref="TilePhysicsType.Wall"/>, so the enum alone is <b>not</b> the
    /// whole rule: a <c>phis == 2</c> tile carries <see cref="TileData.levelTwoSolid"/> and must be
    /// let through too. That is the closed-grate case — 195 <c>grate</c>/<c>hgrate</c> placements in
    /// 69 rooms, whose tiles the oracle leaves at <c>phis == 2</c> precisely so bullets cross them.</para>
    ///
    /// <para><b>Do not express this as <c>TileData.IsSolid()</c>.</b> That helper is
    /// <c>physicsType >= Wall</c>, i.e. it includes <c>Platform</c> and <c>Stair</c> — it answers
    /// "does this tile block <i>movement</i>", which is a different question with a different answer.
    /// Using it here is exactly how bullets came to stop in mid-air on every catwalk and ladder.</para>
    ///
    /// <para><b>Why this is a shared function rather than two inlined conditions.</b> The legacy path
    /// (<c>Projectile.OnTriggerEnter2D</c>) and the flipped path (<c>RoomChainGeometry.IsSolidAt</c>)
    /// must agree, or the Stage C rollback flag would change <i>gameplay</i> rather than only the
    /// implementation. Both now resolve to this predicate, and it is directly assertable — the legacy
    /// filter previously had no test at all, because exercising it needed real <c>Collider2D</c>s and
    /// a physics callback that EditMode cannot drive.</para>
    /// </summary>
    public static class ProjectileOcclusionRule
    {
        /// <summary>
        /// True when <paramref name="tile"/> stops a projectile: a <c>phis == 1</c> solid, which this
        /// port decodes to <see cref="TilePhysicsType.Wall"/> — <b>excluding</b> a level-2 solid.
        ///
        /// <para><b>The level-2 exclusion is the oracle's own, not a widening.</b> <c>Bullet.as:476</c>
        /// reads <c>phis == 1 || phis == 2 &amp;&amp; (the bullet is still inside the cell it spawned
        /// in)</c>, and the second clause is unreachable for an ordinary round: <c>tileX</c>/<c>tileY</c>
        /// default to <c>-1</c> (<c>Bullet.as:87-89</c>) and the only writer is <c>WClub.as:627-628</c>,
        /// the club's point-blank shock round. So a bullet fired at a closed grate goes through it, and
        /// <c>PhisBullet.as</c>'s four arms test <c>phis == 1</c> with no second clause at all.</para>
        ///
        /// <para>A null tile — an unbuilt cell, or a collider that carries no <c>TileData</c> — is
        /// <b>not</b> an occluder. Returning true there would stop projectiles on colliders whose
        /// semantics are unknown, which is the louder failure of the two.</para>
        /// </summary>
        public static bool BlocksProjectile(TileData tile)
        {
            if (tile == null) return false;
            if (tile.physicsType != TilePhysicsType.Wall) return false;
            return !tile.levelTwoSolid;
        }
    }
}
