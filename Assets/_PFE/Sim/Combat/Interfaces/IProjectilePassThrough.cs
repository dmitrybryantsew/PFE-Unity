namespace PFE.Systems.Combat
{
    /// <summary>
    /// Implemented by a collider whose solidity a bullet must <b>narrow</b> rather than honour — the
    /// collider half of AS3's <c>phis == 2</c>.
    ///
    /// <para><b>Why this exists as well as the tile rule.</b> AS3 decides a bullet's tile hit from
    /// <c>phis</c> alone (<c>weapon/Bullet.as:476</c>, and <c>weapon/PhisBullet.as:242</c> in all four
    /// arms): <c>phis == 1</c> stops the round, <c>phis == 2</c> — a closed <c>grate</c>/<c>hgrate</c>
    /// (<c>AllData.as:4859-4860</c>) — does not. In this port that fact lives in two places, because a
    /// door's cells are <c>_</c> (air) in the shipped room maps: <c>TileVisualManager</c> built them no
    /// collider, so the surface a round actually meets is <c>DoorPropPresenter</c>'s own solid
    /// <c>BoxCollider2D</c>, not a <c>TileCollider</c>. <c>ProjectileOcclusionRule</c> covers the tile
    /// half; this interface covers the collider half, and a door answers it from the same
    /// <c>@phis</c> value that stamps its tiles.</para>
    ///
    /// <para><b>It gates the stop, not the damage.</b> A round that reaches a pass-through surface
    /// returns from the impact path before any effect — no damage, no impact sound, no decal, no
    /// explosion — which is exactly AS3's behaviour for a grate: a bullet does not hit it at all, so
    /// nothing about the hit happens. Melee and blasts still damage the grate, because their paths
    /// never ask this question (<c>Location.hitTile</c> is gated on <c>phis &gt;= 1</c>).</para>
    /// </summary>
    public interface IProjectilePassThrough
    {
        /// <summary>
        /// True while this surface must let a projectile through — a closed grate. False for an
        /// ordinary door, and for a grate that is open or wrecked (both of which are not solid at all,
        /// so the collider is off and the question does not arise).
        /// </summary>
        bool PassesProjectiles { get; }
    }
}
