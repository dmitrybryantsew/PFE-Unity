using System;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// The oracle's swim arithmetic, extracted so it can be pinned without a room or an engine.
    ///
    /// <para><b>AS3 swims by adding to <c>dy</c> every frame, never by setting it.</b>
    /// <c>UnitPlayer.as:2804-2807</c> is <c>if(isPlav &amp;&amp; keyBeUp &amp;&amp; !keyJump) dy -= plavdy *
    /// pers.speedPlavMult</c>, with <c>plavdy = accel * 0.5</c> set at <c>:360</c>; <c>:2700-2703</c>
    /// applies the same impulse for <c>keyJump</c>, and <c>:2871-2874</c> adds <c>plavdy * mult / 2</c>
    /// downward for <c>keySit</c>. The water then damps it: <c>dy *= 0.8</c> every frame while
    /// <c>isPlav</c> (<c>Unit.as:1931</c>). Nothing in the oracle ever writes a swim velocity, which
    /// is why the impulse's <i>accumulation</i> is part of the mechanic rather than an implementation
    /// detail — the terminal speed, and the momentum that carries a unit out of the water when the
    /// surface sample goes dry, both come from it.</para>
    ///
    /// <para><b>Why the port had to change.</b> It assigned <c>walkSpeed * 0.5</c> (3.5 px/frame) as a
    /// <i>desired</i> vertical speed. The motor's water drag then took that to 2.8, and — because an
    /// assignment replaces <c>dy</c> instead of adding to it — the unit carried no momentum at all at
    /// the moment the 75% water sample went dry. It rose 11 px above that line and sank back, which
    /// is the reported "sometimes I can't get out of the water by jumping". The same assignment also
    /// threw away any knockback velocity on a unit that landed in water.</para>
    ///
    /// <para><b>Units.</b> Everything here is <b>pixels per 30 Hz frame</b>, the oracle's unit, which
    /// is also the unit of <c>TilePhysicsController.dx/dy</c>. No delta time appears anywhere in this
    /// file on purpose: AS3's water arithmetic is per frame, and the motor is the only place that
    /// converts a frame count into a step.</para>
    /// </summary>
    public static class SwimMath
    {
        /// <summary>
        /// AS3 <c>Unit.as:1931</c> — <c>dy *= 0.8</c> every frame while fully submerged. This is the
        /// motor's own water drag (<c>TilePhysicsController</c>, the <c>isFullySubmerged</c> branch)
        /// and the single definition of it: the terminal-speed derivation below is only correct if
        /// the two agree, so the motor reads this constant rather than repeating <c>0.8f</c>.
        /// </summary>
        public const float WaterDragPerFrame = 0.8f;

        /// <summary>
        /// AS3 <c>plavdy = accel * 0.5</c> (<c>UnitPlayer.as:360</c>) for the player, in px/frame.
        ///
        /// <para><b>It is derived from the walk speed here, not from
        /// <see cref="PFE.Data.Definitions.UnitDefinition.Acceleration"/>, and that is deliberate.</b>
        /// <c>AllData.as:22</c> authors littlepip as <c>&lt;move speed='7' accel='3.5'/&gt;</c>, so
        /// <c>plavdy = 1.75</c>; the port's <c>UnitDef_Littlepip.asset</c> carries
        /// <c>acceleration: 2</c>, which does not match that row and would give 1.0 — a <i>slower</i>
        /// swim than the 3.5 the port already had. <c>speed / 4</c> reproduces 1.75 from the value the
        /// asset does agree with (7), and stays correct if the acceleration field is ever corrected to
        /// 3.5, because <c>accel == speed / 2</c> in that row. The stale field is reported, not
        /// silently compensated for: changing an <c>.asset</c> is the owner's call.</para>
        /// </summary>
        public static float UpImpulsePixelsPerFrame(float walkSpeedPixelsPerFrame)
        {
            return walkSpeedPixelsPerFrame * 0.25f;
        }

        /// <summary>
        /// AS3 <c>dy += plavdy * mult / 2</c> for <c>keySit</c> (<c>UnitPlayer.as:2873</c>) — the
        /// descent is exactly half the ascent.
        /// </summary>
        public static float DownImpulsePixelsPerFrame(float walkSpeedPixelsPerFrame)
        {
            return UpImpulsePixelsPerFrame(walkSpeedPixelsPerFrame) * 0.5f;
        }

        /// <summary>
        /// The speed an impulse converges on under the motor's water drag, given the per-frame
        /// gravity that is also applied inside the same damped loop:
        /// <c>v' = drag * (v + impulse - gravity)</c> ⇒ <c>v = drag * (impulse - gravity) / (1 - drag)</c>.
        ///
        /// <para><b>The port's ordering matters and is why this is not the oracle's own number.</b>
        /// AS3 applies its impulse <i>after</i> <c>forces()</c> has damped, so the drag never touches
        /// the impulse itself: <c>v' = drag * (v + ddy * ddyPlav) - plavdy</c> ⇒ <c>v = 4 - 5 *
        /// plavdy</c> = <b>4.75 px/frame</b> for littlepip. The motor here integrates an added impulse
        /// through the damped loop, so with the port's <c>swimGravityScale</c> of 0.35 the terminal is
        /// <c>4 * (1.75 - 0.35)</c> = <b>5.6 px/frame</b>. Both are recorded so the difference is a
        /// number rather than a shrug: 5.6 against the shipped build's effective <b>2.8</b>
        /// (<c>walkSpeed * 0.5</c> assigned, then damped).</para>
        /// </summary>
        public static float TerminalSpeedPixelsPerFrame(float impulsePerFrame, float gravityPerFrame)
        {
            return WaterDragPerFrame * (impulsePerFrame - gravityPerFrame) / (1f - WaterDragPerFrame);
        }

        // ── The splash ───────────────────────────────────────────────────────────────────────────
        //
        // AS3 plays the water splash on the FRAME THE `inWater` FLAG CHANGES, and only if the unit was
        // moving fast enough through the surface:
        //
        //   if(pla != this.inWater && dy > 5)                  -> fall_water0/1/2/fall_item_water
        //   if(pla != this.inWater && dy < -5 && massa > 0.4)  -> fall_water2
        //                                                         (Unit.as:2671-2686)
        //
        // with the volume `|dy| / 10` in both cases, and `pla` the previous frame's `inWater`. Two
        // things are load-bearing and the port had neither:
        //
        //   1. **The ±5 threshold.** It is not a spam guard bolted on top — it is what keeps a
        //      swimmer QUIET at the surface. Swimming up, the unit leaves the 75% sample line at this
        //      motor's swim terminal (5.6 px/frame, see TerminalSpeedPixelsPerFrame), coasts across
        //      the 25 px band between the two water samples under `swimGravityScale` 0.35, and
        //      crosses the 25% line at sqrt(5.6² - 2·0.35·25) = 3.72 px/frame. That is below 5, so
        //      the bob that a swimmer makes while holding Up is silent in AS3 — while a fall into
        //      water, which crosses at 10+ px/frame, is not. Remove the threshold and every stroke
        //      of a swim at the surface splashes twice (exit + re-entry): the reported glitch.
        //   2. **The volume.** `dy/10` makes a gentle entry quiet and a plunge loud; a flat 1.0 makes
        //      every crossing a full splash. The port played everything at 1.0.
        //
        // `Math.Clamp` on the volume is the port's addition: AS3 hands Flash a number above 1 on a
        // fast fall and Flash clamps it, so clamping here reproduces the audible result rather than
        // depending on an engine detail.

        /// <summary>
        /// AS3 <c>Unit.as:2671</c>/<c>:2684</c> — the splash only plays when the unit crosses the water
        /// line faster than this, in px per 30 Hz frame. See the block above for why the value is not
        /// arbitrary: a swimmer's surface bob crosses at 3.72 and must stay silent.
        /// </summary>
        public const float SplashMinVerticalSpeedPixelsPerFrame = 5f;

        /// <summary>AS3 <c>Unit.as:2675</c>/<c>:2686</c> — <c>this.sound(..., 0, dy / 10)</c>.</summary>
        public const float SplashVolumeDivisor = 10f;

        /// <summary>
        /// Entering the water (falling). <b>The sign is flipped from the oracle's</b>: AS3's Y runs down
        /// so a fall is <c>dy &gt; 5</c>; this motor's <c>dy</c> is Unity's up-positive, so a fall is
        /// negative. Comparing a Unity velocity against the oracle's literal would make every plunge
        /// silent and every breach splash — the mirror is the whole content of this function.
        /// </summary>
        public static bool ShouldSplashEnteringWater(float verticalVelocityPixelsPerFrame)
        {
            return verticalVelocityPixelsPerFrame < -SplashMinVerticalSpeedPixelsPerFrame;
        }

        /// <summary>
        /// Leaving the water (rising) — AS3 <c>dy &lt; -5</c>, i.e. up in the oracle's down-positive Y,
        /// i.e. positive here.
        /// </summary>
        public static bool ShouldSplashLeavingWater(float verticalVelocityPixelsPerFrame)
        {
            return verticalVelocityPixelsPerFrame > SplashMinVerticalSpeedPixelsPerFrame;
        }

        /// <summary>
        /// The splash volume, <c>|dy| / 10</c> clamped to the playable range.
        /// </summary>
        public static float SplashVolume(float verticalVelocityPixelsPerFrame)
        {
            return Math.Clamp(Math.Abs(verticalVelocityPixelsPerFrame) / SplashVolumeDivisor, 0f, 1f);
        }

        /// <summary>
        /// The speed left after coasting <paramref name="distancePixels"/> against
        /// <paramref name="gravityPerFrame"/> — <c>v² = v₀² - 2·g·d</c>, floored at zero so a unit that
        /// stops before covering the distance reports 0 rather than a NaN.
        ///
        /// <para><b>Why this is here and not in a test.</b> It is the bridge between two numbers that
        /// live in different places: the swim terminal (<see cref="TerminalSpeedPixelsPerFrame"/>, this
        /// file) and the height of the band between the 75% and 25% water samples (<c>CheckWater</c>,
        /// the motor's). The splash threshold only makes sense as a comparison between them, so the
        /// comparison is derivable — <see cref="SplashMinVerticalSpeedPixelsPerFrame"/> is above the
        /// speed a surface bob crosses at, and a fixture pins exactly that.</para>
        /// </summary>
        public static float CoastSpeedAfterPixels(float startSpeedPixelsPerFrame, float gravityPerFrame, float distancePixels)
        {
            float squared = startSpeedPixelsPerFrame * startSpeedPixelsPerFrame
                            - 2f * gravityPerFrame * distancePixels;
            return squared <= 0f ? 0f : (float)Math.Sqrt(squared);
        }
    }
}
