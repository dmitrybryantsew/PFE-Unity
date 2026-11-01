using UnityEngine;

namespace PFE.Systems.Telekinesis
{
    /// <summary>
    /// The <b>enemy</b> telekinesis arithmetic — AS3 <c>UnitAlicorn.findBox / upTeleObj / throwTele /
    /// dropTeleObj</c> (<c>fe/unit/UnitAlicorn.as:1140-1246</c>) plus the hold controller inside
    /// <c>control():1038-1084</c>, as pure functions.
    ///
    /// <para><b>Why this is a separate class from <see cref="TelekinesisMath"/>.</b> The oracle has two
    /// independent telekinesis implementations and they are not the same mechanic with different
    /// numbers:</para>
    /// <list type="bullet">
    ///   <item><description><b>Player</b> (<c>UnitPlayer</c>) — cursor-driven, gated on
    ///     <c>levitPoss &amp;&amp; onCursor &amp;&amp; celDist &lt;= teleDist &amp;&amp; massa &lt;=
    ///     maxTeleMassa</c>, holds an object at the <b>cursor</b>, compares the object's vertical
    ///     <i>centre</i> to it, and costs mana.</description></item>
    ///   <item><description><b>Enemy</b> (this) — AI-driven, gated on
    ///     <c>levitPoss &amp;&amp; wall == 0 &amp;&amp; levit == 0 &amp;&amp; massa &gt;= 1</c> plus a
    ///     distance and a line of sight, holds it at a <b>fixed point beside the caster</b>, compares the
    ///     object's <i>bottom</i> to it, and costs nothing. It grabs the <b>player</b> as readily as a
    ///     crate, and throws what it grabbed.</description></item>
    /// </list>
    /// <para><c>TelekinesisMath</c>'s header used to say "do not model anything from <c>UnitAlicorn</c>",
    /// which was correct advice for <i>that</i> file: its constants and its gate are the player's, and
    /// reusing them here would have been the wrong port. This class exists so both can be modelled
    /// without either borrowing the other's rules.</para>
    ///
    /// <para><b>Y is mirrored, and this is the trap.</b> AS3's <c>Y</c> grows <i>down</i>; every
    /// coordinate in this class is the port's y-<i>up</i> room-local pixel space. The oracle's
    /// <c>teleY = Y - 40</c> ("40 px above the caster") is therefore <c>+40</c> here, and its
    /// <c>if(teleObj.Y &lt; teleY - derp) dy += teleAccel</c> ("push it down") becomes "push it up".
    /// Carrying a sign across unchanged drives the held thing into the floor, which reads as "the
    /// alicorn's telekinesis does nothing".</para>
    /// </summary>
    public static class AlicornTelekinesisMath
    {
        /// <summary>
        /// <c>teleX = X + storona * 150</c> / <c>X + storona * 80</c>, <c>teleY = Y - 40</c>
        /// (<c>UnitAlicorn.as:1042-1048</c>) — the point a held thing is dragged to, in room-local
        /// y-up pixels.
        /// </summary>
        /// <param name="facing">AS3 <c>storona</c>, <c>+1</c> east.</param>
        /// <param name="targetIsUnit">
        /// <c>teleObj is Unit</c>. A grabbed <i>unit</i> is held further out (150 px) than a grabbed
        /// prop (80 px) — the player is held at arm's length so the melee never connects.
        /// </param>
        public static Vector2 HoldTarget(float casterX, float casterY, int facing, bool targetIsUnit)
        {
            float offsetX = targetIsUnit
                ? Combat.AlicornVariantRules.UnitHoldOffsetX
                : Combat.AlicornVariantRules.PropHoldOffsetX;

            return new Vector2(
                casterX + (facing >= 0 ? offsetX : -offsetX),
                casterY - Combat.AlicornVariantRules.HoldOffsetY);
        }

        /// <summary>
        /// One tick of the hold — <c>UnitAlicorn.as:1050-1065</c>, per axis:
        /// <code>
        /// if(teleObj.X &lt; teleX - derp &amp;&amp; teleObj.dx &lt;  teleSpeed) teleObj.dx += teleAccel;
        /// if(teleObj.X &gt; teleX + derp &amp;&amp; teleObj.dx &gt; -teleSpeed) teleObj.dx -= teleAccel;
        /// </code>
        /// with Y mirrored (see the class remarks).
        ///
        /// <para><b>Accelerate to a cap, never proportional</b> — the same controller shape as the
        /// player's, and the same reason: there is no braking term, only <c>Box.forces()</c>'s
        /// <c>*= 0.8</c> while <c>levit</c> is set. A held prop therefore settles at
        /// <c>accel * 0.8 / 0.2 = 4 * accel</c> px/frame, i.e. 16 for <c>tr1</c>/<c>tr3</c> and 20 for
        /// <c>tr2</c> — never at <c>teleSpeed</c>, which only binds on the first tick of each nudge.</para>
        /// </summary>
        /// <param name="objectX">The held thing's horizontal centre.</param>
        /// <param name="objectY">
        /// The held thing's <b>bottom</b>, not its centre. <c>UnitAlicorn.as:1058</c> compares
        /// <c>teleObj.Y</c>, and AS3's <c>Y</c> is the bottom edge (<c>Box.as:171</c>: <c>Y1 = Y - scY</c>
        /// is the top). This is the opposite of the player's controller, which compares the centre
        /// (<c>UnitPlayer.as:1247</c>) — do not "unify" them.
        /// </param>
        /// <param name="velocity">The held thing's current velocity, px per frame, y-up.</param>
        /// <returns>
        /// The <b>resulting velocity</b> for that axis, not the change — AS3 assigns to <c>dx</c>
        /// (<c>teleObj.dx += teleAccel</c>), it does not add a separate impulse. A caller that wants a
        /// delta has to subtract the velocity it passed in; adding this value as though it were a delta
        /// compounds the velocity every tick.
        /// </returns>
        public static Vector2 HoldAccelStep(
            float objectX,
            float objectY,
            Vector2 target,
            Vector2 velocity,
            float teleSpeed,
            float teleAccel,
            float deadzone)
        {
            return new Vector2(
                TelekinesisMath.HoldAccelStep(objectX, target.x, velocity.x, teleSpeed, teleAccel, deadzone),
                TelekinesisMath.HoldAccelStep(objectY, target.y, velocity.y, teleSpeed, teleAccel, deadzone));
        }

        /// <summary>
        /// <c>throwTele()</c> (<c>:1205-1246</c>) — the impulse added to the held thing's velocity, in
        /// px per frame.
        ///
        /// <para><b>Two different directions, and the branch is on the type.</b> A grabbed <i>unit</i> is
        /// always thrown the way the caster faces — <c>{x: 100 * storona, y: -30}</c> (<c>:1219-1222</c>)
        /// — so a player who is lifted is thrown sideways and slightly up, never "at" anything. A prop is
        /// thrown <i>at</i> the current target (<c>:1226-1229</c>), with a <c>|Δx| / 4</c> upward bias so
        /// a short throw arcs instead of skidding.</para>
        ///
        /// <para><b>The magnitude is <c>norma(vec, speed)</c>, not a velocity in the vec's direction.</b>
        /// <c>norma</c> normalises to the requested length, so the impulse is <i>speed</i> px/frame in
        /// that direction — which means the two branches differ only in direction, never in force.</para>
        ///
        /// <para><b>A heavy prop is thrown slower</b> — <c>throwForce / sqrt(massa)</c> for
        /// <c>massa &gt; 1</c> (<c>:1213-1216</c>). See
        /// <see cref="Combat.AlicornVariantRules.ThrowSpeed"/>.</para>
        /// </summary>
        /// <param name="objectCentreY">
        /// The held prop's vertical <b>centre</b> — <c>teleObj.Y - teleObj.scY / 2</c> (<c>:1228</c>).
        /// Note the asymmetry with <see cref="HoldAccelStep"/>, which compares the bottom: the oracle
        /// really does use both.
        /// </param>
        /// <param name="celX">The caster's current target point (AS3 <c>celX</c>), room-local y-up.</param>
        /// <param name="celY">The same for Y — the target's <b>bottom</b>.</param>
        public static void ThrowImpulse(
            bool targetIsUnit,
            int facing,
            float objectX,
            float objectCentreY,
            float celX,
            float celY,
            float throwForce,
            float massa,
            out float impulseX,
            out float impulseY)
        {
            float speed = Combat.AlicornVariantRules.ThrowSpeed(throwForce, massa);

            float dx;
            float dy;

            if (targetIsUnit)
            {
                // AS3 {x: 100 * storona, y: -30}; y-up flips the -30.
                dx = 100f * (facing >= 0 ? 1f : -1f);
                dy = 30f;
            }
            else
            {
                dx = celX - objectX;

                // AS3 `celY - (teleObj.Y - scY/2) - |celX - teleObj.X| / 4`, mirrored: the bias is
                // upward in AS3 and therefore also upward here, i.e. a `+` on the y-up delta.
                dy = celY - objectCentreY + Mathf.Abs(dx) / 4f;
            }

            float length = Mathf.Sqrt(dx * dx + dy * dy);
            if (length < 1e-4f)
            {
                // AS3's `norma` divides by the length, so a target sitting exactly on the held object
                // yields NaN and the NaN is added to dx/dy. Guarded rather than reproduced: a NaN
                // velocity propagates into the position and the prop vanishes.
                impulseX = 0f;
                impulseY = 0f;
                return;
            }

            float k = speed / length;
            impulseX = dx * k;
            impulseY = dy * k;
        }

        /// <summary>
        /// <c>findBox()</c>'s first branch (<c>:1143-1152</c>) — <c>isrnd(0.25)</c> makes the caster go
        /// for the player even when a crate is closer, so an engaged alicorn is a threat rather than a
        /// juggler.
        /// </summary>
        public static bool PrefersUnitTarget(float roll) => roll < Combat.AlicornVariantRules.UnitGrabChance;

        /// <summary>
        /// The prop half of the same scan — <c>isrnd(0.3)</c> per candidate (<c>:1155</c>). Evaluated per
        /// prop rather than once, exactly as the oracle does, so a room with four crates is four
        /// independent 30 % chances in scan order.
        /// </summary>
        public static bool AcceptsProp(float roll) => roll < Combat.AlicornVariantRules.PropGrabChance;

        /// <summary>
        /// <c>getRasst2(obj) &lt;= optDistTele * optDistTele</c> (<c>:1157</c>) — the range gate, on the
        /// <b>squared</b> distance, measured from the caster's origin to the prop's origin.
        /// </summary>
        public static bool InTeleRange(float distanceSquared)
        {
            float range = Combat.AlicornVariantRules.TeleRangePixels;
            return distanceSquared <= range * range;
        }
    }
}
