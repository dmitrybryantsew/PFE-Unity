using PFE.Data.Definitions;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Who may damage whom, from AS3's <c>fraction</c>.
    ///
    /// <para><b>Why this exists.</b> The port had no notion of friendly fire at all: every damage
    /// path — <c>Projectile.HandleImpact</c>, <c>Projectile.Detonate</c>,
    /// <c>Projectile.ApplyHoming</c>, <c>ThrownObject.Detonate</c> — applied damage to any
    /// <c>IDamageable</c> it overlapped, and <c>DamageContext.Owner</c> was <c>null</c> on every path
    /// (all six weapon controllers passed <c>null</c>, and the spawner never set it). AS3 filters in
    /// two places, so the port both let the player shoot allies and let enemies shoot each other.
    /// This is the single answer, mirroring <c>ProjectileOcclusionRule</c>'s shape: a pure function of
    /// its arguments, so it can be asserted with no GameObject, no collider and no running engine —
    /// which is the same move that made <c>ProjectilePhysicsMath</c> and <c>UnitFallPhysics</c>
    /// testable, and the reason those values stopped drifting.</para>
    ///
    /// <para><b>The oracle.</b> <c>fe/unit/Unit.as:78-86</c> declares the factions
    /// (<c>F_PLAYER = 100</c>, <c>F_MONSTER = 1</c>, <c>F_RAIDER = 2</c>, <c>F_ZOMBIE = 3</c>,
    /// <c>F_ROBOT = 4</c>) and <c>Unit.as:454</c> defaults <c>fraction = 0</c>. See
    /// <see cref="FactionType"/> for why the port now uses AS3's numbering.</para>
    ///
    /// <para><b>Where it is applied, and where it is deliberately not.</b> The AS3 census is
    /// <c>grep -n "fraction\s*[!=]=" fe/**/*.as</c> — 38 hits, of which only five are damage filters.
    /// Four of them are covered here: <c>Bullet.as:515</c> (direct hit, via
    /// <see cref="CanHitDirectly"/>), <c>Bullet.as:764</c> + <c>:817</c> (the two explosion variants,
    /// via <see cref="ExplosionMultiplier"/>), and <c>PhisBullet.as:151</c> (thrown contact, the same
    /// direct-hit predicate). The fifth is <c>Spell.as:386</c>, which has no port counterpart.</para>
    ///
    /// <para><b>Two paths must NOT be filtered, and the oracle says so:</b></para>
    /// <list type="bullet">
    /// <item><b>Melee.</b> <c>Unit.udarUnit</c> (<c>Unit.as:4125-4167</c>) applies damage with no
    /// faction test at all, and neither does its caller <c>Unit.udar</c> (<c>:3277</c>). Melee is
    /// faction-blind in AS3, so <c>MeleeHitVolume</c> stays unfiltered. Adding a filter there would be
    /// a silent gameplay change, not a port fix.</item>
    /// <item><b>Mines and unit self-destructs.</b> <c>Unit.explosion</c> (<c>Unit.as:3328-3349</c>)
    /// builds its <c>Bullet</c> with <c>weap = null</c> (<c>new Bullet(this, X, Y - 3, null, …)</c>),
    /// and both explosion gates are guarded by <c>this.weap &amp;&amp; …</c> — so <b>the scaling never
    /// runs</b> and a mine does full damage to its own faction and to the player. <c>MineObject</c>
    /// must therefore keep passing no multiplier.</item>
    /// </list>
    /// </summary>
    public static class FactionRule
    {
        /// <summary>
        /// Damage a unit takes from an explosion of its <i>own</i> faction, when that faction is not
        /// the player's. AS3 <c>weapon/Bullet.as:766</c>:
        /// <code>
        /// if(this.weap &amp;&amp; this.weap.owner.fraction == _loc1_.fraction &amp;&amp; _loc1_.fraction != Unit.F_PLAYER)
        ///    _loc5_ *= 0.25;
        /// </code>
        /// <para>Reduced, not zero — AS3 keeps friendly explosions dangerous. The same constant is
        /// used by <c>explBlast</c> via the per-unit <c>Unit.friendlyExpl</c> (<c>Unit.as:182</c>,
        /// default <c>0.25</c>), a path the port does not have; see
        /// <see cref="ExplosionMultiplier"/>.</para>
        /// </summary>
        public const float FriendlyExplosionMultiplier = 0.25f;

        /// <summary>
        /// The player's own-explosion multiplier, AS3 <c>Pers.as:213</c> <c>autoExpl = 1</c>.
        ///
        /// <para><b>Deliberately 1, and deliberately not a skill yet.</b> AS3's value is driven by the
        /// skill <c>&lt;sk id='autoExpl' v0='1' v1='0.25'/&gt;</c> (<c>AllData.as:5573</c>), which
        /// reduces a levelled player's self-damage to 25%. The port's skill system does not apply that
        /// skill, so the base value is the faithful one — full self-explosion damage is AS3's
        /// <i>default</i>, not a port bug. Wiring the skill is separate work, and this constant is the
        /// seam it will use.</para>
        /// </summary>
        public const float DefaultAutoExplosionMultiplier = 1f;

        /// <summary>
        /// Whether a direct (non-explosion) hit on <paramref name="target"/> is allowed at all.
        ///
        /// <para>AS3 <c>weapon/Bullet.as:515</c>, the bullet's only unit test:</para>
        /// <code>
        /// if((this.targetObj || _loc2_.fraction != this.owner.fraction) &amp;&amp; X &gt;= _loc2_.X1 &amp;&amp; …)
        /// </code>
        /// <para>So a unit is hit when the bullet has an explicit guided target, <i>or</i> when the
        /// target's faction differs from the owner's. A same-faction unit is <b>immune</b> — which is
        /// how AS3 stops a bullet hitting its own shooter. It has to: <c>Unit.as:3290-3291</c> sets
        /// <c>weaponX = X</c>, <c>weaponY = Y - scY * 0.5</c>, so the muzzle <i>is</i> the unit's own
        /// centre and every bullet is spawned inside its owner's body.</para>
        /// </summary>
        /// <param name="attacker">Faction of the unit that fired.</param>
        /// <param name="target">Faction of the unit being tested.</param>
        /// <param name="hasGuidedTarget">
        /// True when this projectile was given an explicit target (AS3 <c>targetObj</c>, set by the
        /// homing path). Such a projectile hits regardless of faction.
        /// </param>
        public static bool CanHitDirectly(
            FactionType attacker, FactionType target, bool hasGuidedTarget = false)
        {
            if (hasGuidedTarget) return true;
            return target != attacker;
        }

        /// <summary>
        /// Damage multiplier for an explosion from <paramref name="attacker"/> hitting
        /// <paramref name="target"/>. AS3 <c>weapon/Bullet.as:764-786</c>.
        ///
        /// <para>Two independent gates, in AS3's order:</para>
        /// <code>
        /// if(weap.owner.fraction == target.fraction &amp;&amp; target.fraction != F_PLAYER)
        ///    damage *= 0.25;                                  // same faction, not the player's
        /// if(weap.owner.fraction == F_PLAYER &amp;&amp; target.player)
        ///    target.damage(damage * World.w.pers.autoExpl, …) // the player's own explosion
        /// else
        ///    target.damage(damage, …)
        /// </code>
        ///
        /// <para><b>AS3 does not make the player immune to their own explosions</b>, and the
        /// <c>!= F_PLAYER</c> guard is what makes that explicit: the player's faction is excluded from
        /// the <c>×0.25</c> reduction and gets <c>autoExpl</c> instead — which defaults to <b>1</b>.
        /// So the correct port behaviour is <i>full</i> self-explosion damage, reduced only once the
        /// skill is wired. Note also that the player's explosion does full damage to a Player-faction
        /// ally, because the first gate excludes the player's faction entirely; that asymmetry is
        /// AS3's, not an oversight here.</para>
        /// </summary>
        /// <param name="attacker">Faction of the weapon's owner.</param>
        /// <param name="target">Faction of the unit being damaged.</param>
        /// <param name="targetIsPlayer">AS3 <c>target.player</c>.</param>
        /// <param name="autoExpl">
        /// AS3 <c>World.w.pers.autoExpl</c>. Defaults to
        /// <see cref="DefaultAutoExplosionMultiplier"/> — pass a value once the skill is wired.
        /// </param>
        public static float ExplosionMultiplier(
            FactionType attacker,
            FactionType target,
            bool targetIsPlayer,
            float autoExpl = DefaultAutoExplosionMultiplier)
        {
            float multiplier = 1f;

            if (target == attacker && target != FactionType.Player)
            {
                multiplier *= FriendlyExplosionMultiplier;
            }

            if (attacker == FactionType.Player && targetIsPlayer)
            {
                multiplier *= autoExpl;
            }

            return multiplier;
        }

    }
}
