using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// The flying half of a unit's integration — AS3 <c>Unit.as:1934-1952</c>, the <c>isFly</c> arm of
    /// <c>forces()</c> — as pure functions of their arguments.
    ///
    /// <para><b>Why this class had to exist before the Calamity port could work.</b> The port had no
    /// flight at all: <c>UnitController.IsFlying</c> existed and was read by exactly one consumer (the
    /// spike-trap rule), and <b>neither gravity path consulted it</b>. So a <c>fly="1"</c> placement —
    /// <c>surfCalam</c> in the surf land, the camp's <c>observer</c>, the whole bloat family — was
    /// spawned, told to fly by its own script, and pulled straight back down by the same gravity every
    /// walking unit obeys. The oracle's <c>forces()</c> is a two-arm branch and the arms are
    /// <i>mutually exclusive</i>: a flyer takes the <c>isFly</c> arm and never reaches the
    /// <c>dy += World.ddy</c> line.</para>
    ///
    /// <para><b>Oracle.</b> <c>fe/unit/Unit.as</c> <c>forces()</c>:</para>
    /// <code>
    /// else if(this.isFly) {
    ///    if(this.t_throw &lt;= 0) {
    ///       if(dx * dx + dy * dy &gt; this.maxSpeed * this.maxSpeed) { dx *= 0.7; dy *= 0.7; }
    ///       if(dx &gt; -this.brake &amp;&amp; dx &lt; this.brake) { dx = 0; }
    ///       if(dy &gt; -this.brake &amp;&amp; dy &lt; this.brake) { dy = 0; }
    ///    }
    /// }
    /// </code>
    /// <para>with <c>maxSpeed = 10</c> px/frame (<c>Unit.as:216</c>, overwritten from
    /// <c>&lt;move speed=…&gt;</c> at <c>:1068</c>) and <c>brake = 1</c> px/frame
    /// (<c>Unit.as:230</c>, overwritten at <c>:1080</c>).</para>
    ///
    /// <para><b>Both halves matter and neither is cosmetic.</b> The 0.7 cap is the flyer's terminal
    /// speed; without it the per-frame thrust from <c>UnitNPC.control()</c> accumulates without bound,
    /// because the ground brake that would otherwise bleed it off is the <i>other</i> arm. The deadzone
    /// is what lets the approach in <c>UnitNPC.control()</c> settle: it removes the previous frame's
    /// residual <i>before</i> this frame's thrust is added, so a unit that has arrived stops instead of
    /// jittering around its target at one thrust per frame.</para>
    ///
    /// <para><b>Why the deadzone cannot be applied after the thrust.</b> AS3's order is
    /// <c>forces()</c> → <c>control()</c> → <c>run()</c> (<c>Unit.as:1806-1809</c>), and the hover bob is
    /// added in <c>control()</c> (<c>UnitNPC.as:521-527</c>: <c>floatY = cos(t_float) * 0.5</c>). The
    /// position advances in <c>run()</c>, which does <c>Y += dy</c> on the <i>persistent</i> velocity —
    /// <c>run()</c> never clears it (<c>Unit.as:2029-2040</c>). So the bob is visible in the position,
    /// and what the deadzone does at the top of the next frame is stop the bob's <i>residual</i> from
    /// accumulating into a climb. With AS3's <c>brake = 1</c> the whole bob (|0.5| &lt; 1) is absorbed,
    /// so the flyer bobs and does not drift. Applying the deadzone after the thrust instead would zero
    /// the bob itself, and the flyer would hang in perfect stillness.</para>
    ///
    /// <para><b>That absorption depends on the brake being wider than the bob, which is why the brake
    /// value matters.</b> The comparison is strict, so a brake of exactly <c>0.5</c> would leave a bob
    /// of <c>0.5</c> alive and let the residual accumulate — the flyer would bob <i>and</i> climb. The
    /// importer writes AS3's <c>1</c> for a silent <c>&lt;move&gt;</c> row (see
    /// <c>UnitDataImporter.ParseMovement</c>'s brake read), and <c>&lt;unit id='npc'&gt;</c> is one of
    /// those rows, so the live path gets the absorbing brake.</para>
    ///
    /// <para>The two drivers therefore call this from where the oracle calls <c>forces()</c>: the
    /// motor-less path from <c>UnitController.ApplyGravity</c>, the motor from
    /// <c>TilePhysicsController.ApplyGravity</c> — both above the unit's own <c>TickControl</c>.</para>
    /// </summary>
    public static class UnitFlyPhysics
    {
        /// <summary>
        /// AS3 <c>dx *= 0.7</c> / <c>dy *= 0.7</c> (<c>Unit.as:1940-1941</c>) — the per-frame decay a
        /// flyer takes once it is over its speed cap.
        /// </summary>
        public const float SpeedCapDecayPerFrame = 0.7f;

        /// <summary>
        /// AS3 <c>Unit.maxSpeed</c>'s own default (<c>Unit.as:216</c>), for a unit whose definition
        /// carries no value. <b>Not</b> the port's <c>UnitDefinition.moveSpeed</c> default — see
        /// <see cref="ResolveMaxSpeed"/>.
        /// </summary>
        public const float As3DefaultMaxSpeed = 10f;

        /// <summary>AS3 <c>Unit.brake</c>'s own default (<c>Unit.as:230</c>).</summary>
        public const float As3DefaultBrake = 1f;

        /// <summary>
        /// The speed cap to hand <see cref="ApplyBrake"/>, given a unit definition's value.
        ///
        /// <para><b>A named gap, not a silent substitution.</b> AS3's <c>maxSpeed</c> and this port's
        /// <c>UnitDefinition.moveSpeed</c> are the same field read from the same <c>&lt;move
        /// speed=…&gt;</c> attribute, but their <i>defaults</i> disagree: the oracle's is <c>10</c> and the
        /// port's field initialiser is <c>4</c> (<c>UnitDefinition.cs:164</c>). <c>UnitDataImporter</c>
        /// writes the attribute only when it is authored, so for the 27 of 133 <c>&lt;move&gt;</c> rows
        /// that omit it — <c>&lt;unit id='npc'&gt;</c> among them — the port reads 4 where the oracle reads
        /// 10. This returns the port's value so nothing changes silently, and the divergence is recorded
        /// here rather than fixed in place, because changing the shared default would move 27 units'
        /// movement in one commit with no fixture able to say which of them regressed.</para>
        /// </summary>
        public static float ResolveMaxSpeed(float definitionMoveSpeed)
            => definitionMoveSpeed > 0f ? definitionMoveSpeed : As3DefaultMaxSpeed;

        /// <summary>The same treatment for <c>UnitDefinition.braking</c> (AS3 <c>brake</c>).</summary>
        public static float ResolveBrake(float definitionBraking)
            => definitionBraking > 0f ? definitionBraking : As3DefaultBrake;

        /// <summary>
        /// One step of AS3's <c>isFly</c> arm, on a velocity in AS3's own units (pixels per 30 Hz
        /// frame).
        /// </summary>
        /// <param name="velocityPixelsPerFrame">AS3 <c>dx</c>/<c>dy</c> before the step.</param>
        /// <param name="maxSpeed">AS3 <c>maxSpeed</c> — see <see cref="ResolveMaxSpeed"/>.</param>
        /// <param name="brake">AS3 <c>brake</c> — see <see cref="ResolveBrake"/>.</param>
        /// <param name="rateFrames">
        /// How many canonical 30 Hz frames this step covers. <c>1</c> on the sim clock (the live path —
        /// <c>SimClock</c> runs at <c>World.fps</c>), and <c>TilePhysicsStepMath.RateFramesPerStep</c> on
        /// the legacy fixed clock, so a 50 Hz step decays by <c>0.7^0.6</c> rather than by a whole
        /// <c>0.7</c>. The deadzone is a threshold on the <i>value</i>, not a rate, so it is applied once
        /// per step regardless.
        /// </param>
        /// <remarks>
        /// The <c>t_throw</c> gate (<c>Unit.as:1936</c>) — "a thrown unit keeps its momentum" — is not
        /// modelled, because the port has no throw timer on a unit; a thrown unit here is a
        /// <c>RoomObjectPhysicsLayer</c> prop, not a <c>Unit</c>. Recorded rather than invented.
        /// </remarks>
        public static Vector2 ApplyBrake(Vector2 velocityPixelsPerFrame, float maxSpeed, float brake,
            float rateFrames)
        {
            float dx = velocityPixelsPerFrame.x;
            float dy = velocityPixelsPerFrame.y;

            if (dx * dx + dy * dy > maxSpeed * maxSpeed)
            {
                float factor = Mathf.Pow(SpeedCapDecayPerFrame, Mathf.Max(0f, rateFrames));
                dx *= factor;
                dy *= factor;
            }

            if (dx > -brake && dx < brake) dx = 0f;
            if (dy > -brake && dy < brake) dy = 0f;

            return new Vector2(dx, dy);
        }
    }
}
