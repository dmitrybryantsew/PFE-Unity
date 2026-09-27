using UnityEngine;
using PFE.Systems.Map.TileQuery;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// The falling half of a unit's integration, as pure functions of their arguments.
    ///
    /// <para><b>Why this exists.</b> The gravity census closed every site except one, and the reason
    /// that one survived is the reason all of them did: while the arithmetic is computed inline
    /// inside a <c>MonoBehaviour</c>, pinning it needs a GameObject, a collider and a running engine,
    /// so the test never gets written and the value drifts. Extracting it to a plain function of
    /// <c>(velocity, deltaTime)</c> is the same move that made <c>ProjectilePhysicsMath</c> and
    /// <c>TilePhysicsStepMath</c> assertable.</para>
    ///
    /// <para><b>AS3 oracle.</b> <c>fe/unit/Unit.as</c> <c>forces()</c>, the ordinary (not flying, not
    /// swimming) branch:</para>
    /// <code>
    /// if(!levit &amp;&amp; this.isLaz == 0) {
    ///    _loc1_ = loc.getAbsTile(X, Y - scY / 4);
    ///    if(_loc1_.grav &gt; 0 &amp;&amp; dy &lt; World.maxdy * _loc1_.grav
    ///       || _loc1_.grav &lt; 0 &amp;&amp; dy &gt; World.maxdy * _loc1_.grav) {
    ///       dy += World.ddy * _loc1_.grav * this.grav;      // Unit.as:1962-1965
    ///    }
    /// }
    /// </code>
    /// <para>with <c>World.ddy = 1</c> px/frame² (<c>World.as:46</c>), <c>World.maxdy = 20</c>
    /// px/frame (<c>World.as:48</c>), <c>Unit.grav = 1</c> (<c>Unit.as:256</c>) and
    /// <c>World.fps = 30</c> (<c>World.as:44</c>). So a normal unit falls at exactly
    /// <b>1 px/frame²</b>, clamped at <b>20 px/frame</b> — the same <c>World.ddy</c> every other
    /// falling thing in this port uses.</para>
    ///
    /// <para><b>Two AS3 gates are deliberately not modelled here, and both are recorded rather than
    /// silently dropped.</b> (1) The per-tile <c>grav</c> multiplier: AS3 gates the acceleration on
    /// the tile under the unit's feet, and this port's <c>TileData</c> has no <c>grav</c> field at
    /// all, so there is no value to read — adding one is a data-model change, not a constant.
    /// (2) <c>Unit.grav</c> per unit, which is <c>1</c> for every unit this path serves. Until (1)
    /// lands, <c>dy += World.ddy</c> is the correct reduced form for a tile whose <c>grav</c> is
    /// 1 — which is the only case the port can currently represent.</para>
    /// </summary>
    public static class UnitFallPhysics
    {
        /// <summary>
        /// Fall acceleration: <c>World.ddy</c> = 1 px/frame² (<c>World.as:46</c>), converted to the
        /// units a seconds-based delta needs. Kept in its AS3 form as well so the conversion is
        /// visible rather than implied by a bare <c>9f</c>.
        /// </summary>
        public const float GravityPixelsPerFrameSquared = TileQueryConstants.Gravity;

        /// <inheritdoc cref="GravityPixelsPerFrameSquared"/>
        public const float GravityUnitsPerSecondSquared = TileQueryConstants.GravityUnitsPerSecondSquared;

        /// <summary>
        /// Terminal fall speed: <c>World.maxdy</c> = 20 px/frame (<c>World.as:48</c>) — a
        /// <i>velocity</i>, so it converts by the per-frame velocity factor (×0.3), not the
        /// acceleration one. 20 px/frame → <b>6 units/s</b>, which is the same clamp
        /// <c>RoomObjectPhysicsLayer.MaxFallSpeedPixelsPerSecond</c> applies to props (600 px/s).
        /// </summary>
        public const float TerminalFallSpeed =
            TileQueryConstants.MaxDy * TileQueryConstants.PerFrameVelocityToUnitsPerSecond;

        /// <summary>
        /// Ground braking: AS3 <c>brake</c> = 1 px/frame (<c>Unit.as:230</c>), applied as a
        /// <i>subtraction</i> from <c>dx</c> once per frame (<c>Unit.as:1970-1990</c>) — so it is a
        /// px/frame² acceleration and converts by the squared factor → <b>9 units/s²</b>.
        ///
        /// <para>Not to be confused with <c>tormoz</c> = 1 (<c>Unit.as:260</c>), which AS3 applies as
        /// a per-frame <i>multiplier</i> on <c>dx</c> while <c>stay</c>.</para>
        /// </summary>
        public const float BrakePixelsPerFrameSquared = 1.0f;

        /// <inheritdoc cref="BrakePixelsPerFrameSquared"/>
        public const float BrakeUnitsPerSecondSquared =
            BrakePixelsPerFrameSquared * TileQueryConstants.PerFrameAccelerationToUnitsPerSecondSquared;

        /// <summary>
        /// Fall speed for one step: accelerate by <see cref="GravityUnitsPerSecondSquared"/>, clamped
        /// at <see cref="TerminalFallSpeed"/>.
        ///
        /// <para>The clamp is not cosmetic. AS3's gate (<c>dy &lt; World.maxdy</c>) means a long fall
        /// stops accelerating at 20 px/frame; the port had no clamp on this path, so a unit falling
        /// more than about a second kept speeding up without bound — the same omission
        /// <c>RoomObjectPhysicsLayer</c> had, found and fixed by the same census.</para>
        /// </summary>
        public static float FallSpeed(float velocityY, float deltaTime)
        {
            if (velocityY <= -TerminalFallSpeed)
            {
                return velocityY;
            }

            return Mathf.Max(
                -TerminalFallSpeed, velocityY - GravityUnitsPerSecondSquared * deltaTime);
        }

        /// <summary>
        /// Horizontal braking toward rest for one step: a constant deceleration of
        /// <see cref="BrakeUnitsPerSecondSquared"/> that stops at zero rather than overshooting.
        ///
        /// <para><b>Known divergence, recorded not hidden.</b> AS3 applies this only while
        /// <c>stay</c> (grounded) and branches on the walk input (<c>Unit.as:1970-1990</c>): it
        /// brakes toward a walking unit's <c>maxSpeed</c> rather than toward zero, and snaps to zero
        /// when <c>|dx| &lt; brake</c>. This path has no walk input — it serves units with no motor —
        /// so the reduced "brake toward rest" form is what it can express. The <i>magnitude</i> is
        /// the part the census owned and the part that was wrong by 6.67×; the branch structure is a
        /// separate piece of work.</para>
        /// </summary>
        public static float GroundBrake(float velocityX, float deltaTime)
        {
            float reduction = BrakeUnitsPerSecondSquared * deltaTime;

            if (velocityX > reduction) return velocityX - reduction;
            if (velocityX < -reduction) return velocityX + reduction;
            return 0f;
        }
    }
}
