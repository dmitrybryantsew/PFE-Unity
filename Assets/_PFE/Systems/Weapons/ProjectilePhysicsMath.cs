using UnityEngine;
using PFE.Systems.Map.TileQuery;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Pure arithmetic converting AS3 projectile physics — authored in <b>pixels per 30 Hz frame</b> —
    /// into the per-second quantities Unity integrates.
    ///
    /// <para><b>Why this exists.</b> The port has two conversion idioms and mixing them up is its most
    /// common bug. A per-frame <i>velocity</i> scales by <c>fps / PPU</c> (= ×0.3); a per-frame²
    /// <i>acceleration</i> scales by <c>fps² / PPU</c> (= ×9). The same AS3 file uses both, sometimes
    /// a few lines apart, and the wrong one is never a compile error — it is a weapon that arcs
    /// wrongly. Three sites had drifted by 2×, 3.33× and 30× before anyone noticed, precisely because
    /// nothing pinned the numbers.</para>
    ///
    /// <para>Extracted from <see cref="PFE.Entities.Weapons.Projectile"/> and
    /// <see cref="PFE.Entities.Weapons.ThrownObject"/> for the same reason
    /// <c>TilePhysicsStepMath</c> was extracted from the motor: these are pure functions of their
    /// arguments, so they can be tested without a GameObject, a pool or a running engine. The entity
    /// types own the <i>state</i>; the arithmetic lives here.</para>
    ///
    /// <para>Every constant below cites the AS3 line it comes from. If a number here disagrees with
    /// <c>C:\Users\User\Documents\rustProjects\pfeToUnity\pfe\scripts</c>, AS3 wins.</para>
    /// </summary>
    public static class ProjectilePhysicsMath
    {
        // ── Conversion factors ───────────────────────────────────────────────────────────────

        /// <summary>
        /// px/frame² → units/s². Equal to <c>PixelToUnit × fps²</c> = 9.
        /// Use for anything AS3 writes as <c>ddy</c> / <c>ddx</c> / <c>brake</c>.
        /// </summary>
        public const float AccelerationScale = TileQueryConstants.PerFrameAccelerationToUnitsPerSecondSquared;

        /// <summary>
        /// px/frame → units/s. Equal to <c>PixelToUnit × fps</c> = 0.3.
        /// Use for anything AS3 writes as <c>dx</c> / <c>dy</c> / <c>vel</c> — including thresholds
        /// compared against them.
        ///
        /// <para>An alias for the canonical constant rather than a second copy of the expression.
        /// The two were identical, which is the state that precedes a drift: the unit path's gravity
        /// census found a hand-written <c>100f</c> and <c>0.01f</c> that had already diverged from
        /// the canonical pair in other files. One expression, one place.</para>
        /// </summary>
        public const float VelocityScale = TileQueryConstants.PerFrameVelocityToUnitsPerSecond;

        // ── AS3 source values (px, never hand-converted) ─────────────────────────────────────

        /// <summary>
        /// <c>Weapon.as:1545</c> <c>b.ddy += -0.8 - Math.random() * 0.2</c> — flame==1 lift,
        /// px/frame². This is the <b>minimum</b> of AS3's range; see the jitter note below.
        /// </summary>
        public const float FlameLiftStrongPxPerFrame2 = 0.8f;

        /// <summary>
        /// <c>Weapon.as:1551</c> <c>b.ddy += -0.2 - Math.random() * 0.2</c> — flame==2 lift,
        /// px/frame² (minimum of the range).
        /// </summary>
        public const float FlameLiftWeakPxPerFrame2 = 0.2f;

        // Jitter note: AS3 rolls each flame bullet's lift as `-0.8 - random() * 0.2`, i.e. a
        // per-bullet random 0.8–1.0 px/frame². The port uses the deterministic minimum instead,
        // matching how it treats AS3's other spreads (Weapon.as:1499's `random() * 0.4 + 0.8`
        // speed spread is dropped too). Recorded so the omission reads as a decision, not a miss.

        /// <summary>
        /// <c>Trasser.as:45</c> <c>brake = 2</c>; <c>WThrow.as:20</c> <c>brake = 2</c>.
        /// Applied at <c>Trasser.as:78/82</c> as <c>dx -= brake</c> once per frame, so it is a
        /// px/frame² quantity, <i>not</i> a velocity.
        /// </summary>
        public const float BrakePxPerFrame2 = 2f;

        /// <summary>
        /// <c>Trasser.as:204</c> <c>else if(this.dy &gt; 2)</c> — a floor impact bounces only while
        /// the vertical speed exceeds this; below it the object settles.
        ///
        /// <para>This is compared against <c>dy</c>, which is a <b>velocity</b>, so it converts with
        /// <see cref="VelocityScale"/> (×0.3 → 0.6 units/s) and <b>not</b> with
        /// <see cref="AccelerationScale"/>. Using the acceleration idiom here would be a 30× error.</para>
        /// </summary>
        public const float SettleThresholdPxPerFrame = 2f;

        /// <summary>
        /// <c>WThrow.as:22</c> <c>skok = 0.4</c> — fraction of speed retained on a bounce.
        ///
        /// <para>Assigned onto the bullet at <c>WThrow.as:188</c>, so it <b>overrides</b>
        /// <c>PhisBullet.as:23</c>'s own <c>skok = 0.5</c> default. The port shipped 0.5, making every
        /// bounce 25% more energetic than AS3.</para>
        /// </summary>
        public const float ThrowBounceRetention = 0.4f;

        /// <summary>
        /// <c>WThrow.as:24</c> <c>tormoz = 0.6</c> — horizontal damping applied on a floor bounce
        /// (<c>Trasser.as:203</c> / <c>PhisBullet.as:333</c>).
        ///
        /// <para>Assigned at <c>WThrow.as:189</c>, overriding <c>PhisBullet.as:25</c>'s 0.7 default.
        /// The port shipped 0.7, damping 17% harder than AS3.</para>
        ///
        /// <para><b>Known gap:</b> AS3 additionally lets skills scale this via
        /// <c>&lt;sk id='tormoz' ref='mult' v1='…'/&gt;</c> (e.g. <c>AllData.as:5919</c>). The port's
        /// skill system does not apply that, so this is the unmodified base value.</para>
        /// </summary>
        public const float ThrowFloorDamping = 0.6f;

        // ── Derived values ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Vertical speed below which a floor impact settles instead of bouncing, in units/s.
        /// <c>Trasser.as:204</c>'s 2 px/frame → 0.6.
        /// </summary>
        public static float SettleThresholdVelocity => SettleThresholdPxPerFrame * VelocityScale;

        /// <summary>
        /// Sliding friction while resting, in units/s². <c>Trasser.as:78/82</c> applies
        /// <c>dx -= brake</c> once per frame, so this is a per-frame² acceleration: 2 px/frame² → 18.
        /// </summary>
        public static float SlidingFriction(float brakePxPerFrame2)
        {
            return brakePxPerFrame2 * AccelerationScale;
        }

        /// <summary>
        /// Total per-second acceleration for a bullet, as <c>(ddx, ddy)</c> in units/s².
        ///
        /// <para>Mirrors the accumulation block of AS3 <c>Weapon.shoot()</c>, which zeroes
        /// <c>ddx</c>/<c>ddy</c> at <c>Weapon.as:1524</c> and then <b>adds</b> to them in three
        /// separate blocks. All three are additions, so their order does not matter — but they must
        /// <i>accumulate</i>. A port that assigns instead of adding silently drops whichever term it
        /// overwrites; here that would mean a flame weapon losing gravity.</para>
        ///
        /// <list type="bullet">
        /// <item><c>Weapon.as:1536-1537</c> — forward thrust along the aim direction, px/frame².</item>
        /// <item><c>Weapon.as:1545/1551</c> — flame lift, px/frame², upward.</item>
        /// <item><c>Weapon.as:1558</c> — <c>b.ddy += World.ddy * grav</c>, downward.</item>
        /// </list>
        /// </summary>
        /// <param name="direction">Normalised aim direction (the port's equivalent of AS3 <c>b.rot</c>).</param>
        /// <param name="gravityScale">AS3 <c>phis.@grav</c> — a <b>multiplier</b> on <c>World.ddy</c>, not a <c>ddy</c> itself.</param>
        /// <param name="accelPxPerFrame2">AS3 <c>phis.@accel</c>, px/frame². Pass it raw; the conversion happens here.</param>
        /// <param name="flame">AS3 <c>phis.@flame</c>: 0 = none, 1 = strong up arc, 2 = weak up arc.</param>
        public static Vector2 BulletAcceleration(Vector2 direction, float gravityScale,
                                                 float accelPxPerFrame2, int flame)
        {
            // Weapon.as:1536-1537 — `b.ddx += cos(rot) * accel`, `b.ddy += sin(rot) * accel`.
            float ddx = direction.x * accelPxPerFrame2 * AccelerationScale;
            float ddy = direction.y * accelPxPerFrame2 * AccelerationScale;

            // Weapon.as:1545/1551 — lift. AS3's `+= -0.8` is upward in Y-down space, so in Unity's
            // Y-up it is positive. Deliberately `+=`, not `=`: AS3 accumulates.
            if (flame == 1)
            {
                ddy += FlameLiftStrongPxPerFrame2 * AccelerationScale;
            }
            else if (flame == 2)
            {
                ddy += FlameLiftWeakPxPerFrame2 * AccelerationScale;
            }

            // Weapon.as:1558 — `b.ddy += World.ddy * this.grav`. Downward, so negative in Y-up.
            ddy -= gravityScale * TileQueryConstants.GravityUnitsPerSecondSquared;

            return new Vector2(ddx, ddy);
        }
    }
}
