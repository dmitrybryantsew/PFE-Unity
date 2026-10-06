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

        // ── The bullet view's stretch (the laser beam) ────────────────────────────────────────

        /// <summary>
        /// The reference length AS3 divides the stretch by — <c>Bullet.as:215</c> and <c>:221</c> both
        /// end in <c>/ 100</c>. It is the length the bullet/beam art is authored at, so the resulting
        /// scale factor makes the child span the quantity being measured. Not a unit conversion: the
        /// <c>100</c> is art, and it is the same <c>100</c> in both branches.
        /// </summary>
        public const float ViewStretchReferencePx = 100f;

        /// <summary>
        /// How far a bullet's view is stretched along its own X axis — AS3 <c>Bullet.as:213-226</c>, the
        /// block that runs every frame from <c>run()</c>.
        ///
        /// <para><b>Why this is worth a named function.</b> The branch order is the whole behaviour, and
        /// two of the three cases look alike. AS3 writes:</para>
        /// <code>
        /// if(vis.laser &amp;&amp; this.spring &gt;= 2)  vis.laser.scaleX = dist / 100;   // the beam
        /// else if(this.spring == 1 &amp;&amp; this.vel &gt; 100)  vis.scaleX = vel / 100;  // the smear
        /// else                                       vis.scaleX = 1;
        /// </code>
        /// <para>The beam branch is keyed on a <b>child's existence</b> (<c>vis.laser</c>) as well as on
        /// <c>spring</c>, so a prefab without that child silently falls through to the smear or to 1 —
        /// which is why <paramref name="hasBeamChild"/> is a parameter rather than an assumption.</para>
        ///
        /// <para><b>The beam branch measures the ORIGIN, the smear branch the VELOCITY.</b> They are not
        /// interchangeable: the beam is drawn from the muzzle to where the round is now, so it grows as
        /// the round flies and is zero-length at the instant of firing; the smear is a fixed
        /// motion-blur length that does not depend on how far the round has travelled. Passing the
        /// wrong one of the two produces a plausible-looking beam of the wrong length.</para>
        /// </summary>
        /// <param name="hasBeamChild">
        /// Whether the bullet view actually has the <c>laser</c> child (<c>vis.laser</c> in AS3). A
        /// prefab without it cannot show a beam however <paramref name="spring"/> is set.
        /// </param>
        /// <param name="spring">AS3 <c>vis.@spring</c> — imported into <c>WeaponDefinition.springMode</c>.</param>
        /// <param name="velocityPxPerFrame">The round's speed in AS3 units (px per 30 Hz frame).</param>
        /// <param name="distanceFromOriginPx">How far the round has travelled from where it was fired.</param>
        public static float BulletViewScaleX(bool hasBeamChild, int spring,
                                             float velocityPxPerFrame, float distanceFromOriginPx)
        {
            // `if(vis.laser && this.spring >= 2)` — the laser beam, stretched from the origin.
            if (hasBeamChild && spring >= 2)
            {
                return distanceFromOriginPx / ViewStretchReferencePx;
            }

            // `else if(this.spring == 1 && this.vel > 100)` — a fast round smeared along its travel.
            // Note the threshold is on the AS3-space speed, so it must be compared in px/frame.
            if (spring == 1 && velocityPxPerFrame > 100f)
            {
                return velocityPxPerFrame / ViewStretchReferencePx;
            }

            // `else` — drawn at its natural size.
            return 1f;
        }

        /// <summary>
        /// <b>Which</b> transform AS3's view-stretch writes — and whether it writes at all.
        ///
        /// <para><see cref="BulletViewScaleX"/> answers "what value"; this answers "to what, and
        /// whether". They are separate because AS3's three branches do not all write the same object,
        /// and one of them writes nothing: folding that into the value would make "leave the last smear
        /// alone" indistinguishable from "reset to natural size", which are different pictures.</para>
        /// </summary>
        public enum ViewScaleTarget
        {
            /// <summary>Write no scale this frame.</summary>
            None,

            /// <summary>The <c>laser</c> child — the stretched beam (<c>vis.laser.scaleX</c>).</summary>
            BeamChild,

            /// <summary>The whole view — the velocity smear and the natural-size reset (<c>vis.scaleX</c>).</summary>
            View,
        }

        /// <summary>
        /// Transcribes the branch ORDER of <c>Bullet.as:213-227</c>, which is what decides the target:
        /// <code>
        /// if (vis.laser &amp;&amp; spring >= 2)      vis.laser.scaleX = dist / 100;
        /// else if (spring == 1 &amp;&amp; vel > 100)   { if (!babah) vis.scaleX = vel / 100; }
        /// else                                vis.scaleX = 1;
        /// </code>
        ///
        /// <para><b>The one case that writes nothing.</b> A round that has already detonated
        /// (<c>babah</c>) inside the smear arm falls through the inner <c>if</c> and then out of the
        /// whole chain — AS3 leaves the previous smear standing rather than snapping the view back to
        /// its natural size. That is <see cref="ViewScaleTarget.None"/>, and it is deliberately
        /// <i>not</i> the <c>else</c> arm's explicit reset. The two would be the same picture only if
        /// the smear had never applied.</para>
        /// </summary>
        public static ViewScaleTarget BulletViewScaleTarget(bool hasBeamChild, int spring,
                                                           float velocityPxPerFrame, bool hasDetonated)
        {
            // `if(vis.laser && this.spring >= 2)` — the beam, stretched from the origin.
            if (hasBeamChild && spring >= 2) return ViewScaleTarget.BeamChild;

            // `else if(this.spring == 1 && this.vel > 100)` — the smear, and the only silent arm.
            if (spring == 1 && velocityPxPerFrame > 100f)
                return hasDetonated ? ViewScaleTarget.None : ViewScaleTarget.View;

            // `else` — the view is explicitly reset to its natural size.
            return ViewScaleTarget.View;
        }

        // ── The thrown object's spin ──────────────────────────────────────────────────────────

        /// <summary>
        /// How far a thrown object's view rotates this frame, in <b>degrees</b> — AS3
        /// <c>PhisBullet.step()</c>'s <c>vis.rotation += this.dr</c> (<c>PhisBullet.as:100</c>).
        ///
        /// <para><b>Why a named function for one addition.</b> Because <c>dr</c> is written in three
        /// different places in the oracle and the three do <i>not</i> agree, so "rotate by the current
        /// horizontal speed" is wrong twice out of three:</para>
        ///
        /// <list type="number">
        /// <item><c>WThrow.as:192</c> — at spawn, <c>dr = this.throwTip == 2 ? 0 : b.dx</c>. So a
        /// <b>sticky</b> throwable (<c>throwTip == 2</c>: the dynamite and bomb family, which latch to
        /// the first surface they touch) <b>never spins at all</b>, while every other throwable is
        /// given the <i>initial</i> horizontal velocity.</item>
        /// <item><c>PhisBullet.as:73</c> — while resting (<c>stay</c>), <c>dr = dx</c>, i.e. the
        /// <i>current</i>, brake-decayed horizontal velocity. That is what makes a grenade slow its
        /// roll and stop rather than spin forever on the floor.</item>
        /// <item>Nowhere else. In particular AS3 does <b>not</b> refresh <c>dr</c> when a bounce flips
        /// the sign of <c>dx</c>, so a grenade that rebounds off a wall keeps spinning the way it
        /// started. Recomputing from the live velocity every frame would reverse the spin at every
        /// bounce — a visible difference, and the reason branch 2 is spelled out rather than folded
        /// into a single "use the current speed".</item>
        /// </list>
        ///
        /// <para><b>Units are AS3's.</b> <c>vis.rotation</c> is in degrees and <c>dr</c> is a
        /// px/frame velocity used directly as a degree count — there is no conversion, and in
        /// particular <i>not</i> the px/frame → units/s factor. The value is only ever accumulated
        /// into a rotation, so it never needs to be a physical speed.</para>
        /// </summary>
        /// <param name="sticky">AS3 <c>throwTip == 2</c> (the port's <c>ShotPlan.Sticky</c>).</param>
        /// <param name="resting">AS3 <c>stay</c> — the object has settled on a floor.</param>
        /// <param name="dxAtSpawnPxPerFrame">AS3 <c>b.dx</c> as it was at spawn — captured, not read live.</param>
        /// <param name="dxNowPxPerFrame">AS3 <c>dx</c> right now, after gravity, bounces and the resting brake.</param>
        public static float ThrownSpinDeltaDegrees(bool sticky, bool resting,
                                                   float dxAtSpawnPxPerFrame, float dxNowPxPerFrame)
        {
            // WThrow.as:192 — `dr = this.throwTip == 2 ? 0 : b.dx`. A sticky object is never given a
            // spin, and nothing later can start one: branch 2 only runs once `stay` is true, and a
            // latched sticky object never moves far enough to settle.
            if (sticky) return 0f;

            // PhisBullet.as:73 — `this.dr = dx` while resting.
            if (resting) return dxNowPxPerFrame;

            // WThrow.as:192's other arm — the spawn value, held unchanged for the whole flight.
            return dxAtSpawnPxPerFrame;
        }

    }
}
