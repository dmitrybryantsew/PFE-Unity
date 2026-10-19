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
    /// <para><b>The per-tile <c>grav</c> multiplier is modelled, and it was not always.</b> This class
    /// used to carry a note saying <c>TileData</c> had no <c>grav</c> field, so the reduced
    /// <c>dy += World.ddy</c> form was "the only case the port can currently represent". That was true
    /// when it was written and stopped being true when <c>TileData.gravityMultiplier</c> landed: the
    /// field exists, <c>Area.setLift</c> writes it, and <see cref="FallSpeed"/> takes it as
    /// <c>tileGravity</c>. The remaining gate that is still not modelled here is <c>Unit.grav</c>
    /// per unit, which is <c>1</c> for every unit this path serves.</para>
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
        /// Fall speed for one step: accelerate by <see cref="GravityUnitsPerSecondSquared"/> times the
        /// tile's gravity multiplier, clamped at <see cref="TerminalFallSpeed"/> times the same
        /// multiplier.
        ///
        /// <para>The clamp is not cosmetic. AS3's gate (<c>dy &lt; World.maxdy * grav</c>) means a long
        /// fall stops accelerating at 20 px/frame; the port had no clamp on this path, so a unit
        /// falling more than about a second kept speeding up without bound — the same omission
        /// <c>RoomObjectPhysicsLayer</c> had, found and fixed by the same census.</para>
        ///
        /// <para><b><paramref name="tileGravity"/> is AS3's <c>Tile.grav</c>, and it scales both the
        /// acceleration and the terminal speed.</b> That is one multiply, not two independent tuning
        /// knobs: the oracle's gate is <c>dy &lt; World.maxdy * _loc1_.grav</c>, so a
        /// <c>grav="0.2"</c> tile falls at a fifth of the acceleration <i>and</i> tops out at a fifth
        /// of the speed. The default of 1 is the ordinary case and keeps every existing caller's
        /// arithmetic identical.</para>
        ///
        /// <para><b>A non-positive value is not this function's business.</b> <c>grav &lt; 0</c> is the
        /// lift shaft (a unit pushed <i>up</i>) and <c>grav == 0</c> is weightlessness; both take
        /// neither arm of the oracle's <c>if</c>, so neither accelerates at all. The negative arm is
        /// implemented by the driver that owns the velocity (it needs the sign flip and its own
        /// terminal), and this function answers "no change" for anything at or below zero rather than
        /// inventing a downward pull from a value that means the opposite.</para>
        /// </summary>
        public static float FallSpeed(float velocityY, float deltaTime, float tileGravity = 1f)
        {
            if (tileGravity <= 0f)
            {
                return velocityY;
            }

            float terminal = TerminalFallSpeed * tileGravity;

            if (velocityY <= -terminal)
            {
                return velocityY;
            }

            return Mathf.Max(
                -terminal, velocityY - GravityUnitsPerSecondSquared * tileGravity * deltaTime);
        }

        /// <summary>
        /// The lift arm of the same branch: a unit on a <b>negative</b> gravity tile rises, and this is
        /// its speed for one step — the mirror of <see cref="FallSpeed"/>.
        ///
        /// <para><b>Why it cannot call <see cref="FallSpeed"/> with a negative multiplier.</b> Three
        /// differences, and each is visible in play. It returns a <i>rising</i> speed, so the terminal
        /// is <c>+TerminalFallSpeed * |grav|</c> rather than a negative floor. It applies <b>while
        /// grounded</b> — the caller decides that, because a lift has to push off the floor. And it
        /// stops accelerating once the rise reaches <c>World.maxdy * |grav|</c>
        /// (<c>Unit.as:1964</c>: <c>dy &gt; maxdy * grav</c> with <c>grav &lt; 0</c>), which for the
        /// <c>-0.5</c> shafts is 10 px/frame — half the motor's clamp.</para>
        /// </summary>
        public static float LiftSpeed(float velocityY, float deltaTime, float tileGravity)
        {
            if (tileGravity >= 0f)
            {
                return velocityY;
            }

            float magnitude = -tileGravity;
            float terminal = TerminalFallSpeed * magnitude;

            if (velocityY >= terminal)
            {
                return velocityY;
            }

            return Mathf.Min(
                terminal, velocityY + GravityUnitsPerSecondSquared * magnitude * deltaTime);
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
