using System;

namespace PFE.Systems.Telekinesis
{
    /// <summary>
    /// The telekinesis ability's arithmetic — AS3's <c>UnitPlayer.actTele</c> / hold tick /
    /// <c>throwTele</c>, ported as pure functions.
    ///
    /// <para><b>Player-only.</b> The oracle has two telekinesis implementations. This is
    /// <c>UnitPlayer</c>'s. <c>UnitAlicorn</c> (<c>:1038-1075</c>) is an <b>enemy</b> that grabs the
    /// <i>player</i>; it is AI-driven, never consults <c>maxTeleMassa</c>, <c>teleDist</c>,
    /// <c>telePorog</c> or <c>stay</c>, and runs on different constants (teleSpeed 20, teleAccel 5).
    /// Do not model anything from it.</para>
    ///
    /// <para><b>Scalar-only and Unity-free, deliberately.</b> Every method takes and returns
    /// <c>float</c>, so this class can be linked as <i>source</i> into a throwaway console project and
    /// its guards actually executed rather than merely modelled. AS3 is per-axis anyway — X and Y are
    /// independent branches with no cross term — so the shape matches the oracle too.</para>
    ///
    /// <para><b>The constants are hard-coded per class, not skill-driven.</b> The <c>tele</c> skill
    /// authors <c>teleSpeed</c>/<c>teleAccel</c> (<c>AllData.as:5214-5215</c>), but
    /// <c>Pers.as</c> declares <b>neither</b> and no call site reads <c>pers.teleSpeed</c>. The player's
    /// level-0 values happen to equal the skill's, which is why this looks skill-driven on a first read.
    /// It never scales.</para>
    /// </summary>
    public static class TelekinesisMath
    {
        /// <summary>
        /// The deadzone around the cursor, in pixels — <c>UnitPlayer.as:1231</c>, <c>var _loc1_:* = 15</c>.
        /// Inside it neither branch fires, so the object coasts on damping alone. A literal in AS3, not a
        /// field, and identical across all three telekinesis classes.
        /// </summary>
        public const float Deadzone = 15f;

        /// <summary><c>UnitPlayer.as:57</c> — <c>public var teleSpeed:Number = 8</c>. Per-axis velocity cap.</summary>
        public const float PlayerTeleSpeed = 8f;

        /// <summary><c>UnitPlayer.as:59</c> — <c>public var teleAccel:Number = 1</c>. Added per tick.</summary>
        public const float PlayerTeleAccel = 1f;

        /// <summary>
        /// <c>Box.as:906-910</c> — <c>else if(levit) { dy *= 0.8; dx *= 0.8; }</c>. Applied every tick in
        /// <c>forces()</c> while the held flag is set. This is the <i>only</i> thing that stops the object
        /// once it reaches the cursor: there is no braking term in the controller.
        /// </summary>
        public const float LevitDamping = 0.8f;

        /// <summary>
        /// <c>Pers.as:219</c> — the unskilled default. The skill ladder is
        /// <c>v0=0.6 v1=1.6 v2=3 v3=6 v4=12 v5=25</c> (<c>AllData.as:5212</c>), and perks multiply it
        /// (<c>:5894</c>, <c>:6158</c>, <c>:6195</c>).
        /// </summary>
        public const float DefaultMaxTeleMassa = 1f;

        /// <summary>
        /// <c>Pers.as:235</c> — <c>teleDist = 360000</c>. <b>Squared</b>: it is compared against
        /// <c>celDist</c>, which is <c>dx*dx + dy*dy</c> (<c>UnitPlayer.as:1760</c>), so the real range is
        /// <c>sqrt(360000) = 600</c> px. The skill's second rank is <c>640000</c> = 800 px
        /// (<c>AllData.as:5342</c>).
        /// </summary>
        public const float DefaultTeleDistSquared = 360000f;

        /// <summary>
        /// <c>UnitPlayer.as:1754</c> — <c>if(mana &lt; 200) return;</c>. A hard refusal, distinct from the
        /// throw, which scales down instead of refusing.
        /// </summary>
        public const float MinManaToGrab = 200f;

        /// <summary><c>UnitPlayer.as:1274</c> — the hold is released once the cursor is this far past <c>teleDist</c>.</summary>
        public const float DropDistanceMultiplier = 1.2f;

        /// <summary>
        /// AS3's frame rate. <c>SimClock.CanonicalTicksPerSecond</c> is the same 30, and
        /// <c>RoomInstance.Update(SimClock.SimDt)</c> is documented as "exactly one AS3 frame at
        /// 30 fps" — so every velocity above is <b>pixels per frame</b>, not per second.
        /// </summary>
        public const float As3FramesPerSecond = 30f;

        // ── unit conversion ──────────────────────────────────────────────────────────────────────
        //
        // Everything above is in AS3's own units: px per FRAME. The port integrates as
        // `position += velocity * deltaTime` with velocity in px per SECOND, so an AS3 velocity has to
        // be scaled by the frame rate before it can be used. Getting this wrong is a silent 30x error
        // -- the same mistake that left the knockback impulse 3.33x too strong.

        /// <summary>px/frame → px/second.</summary>
        public static float PerFrameVelocityToPerSecond(float pixelsPerFrame)
            => pixelsPerFrame * As3FramesPerSecond;

        /// <summary>px/frame² → px/second². Two factors of the frame rate, because it is a second derivative.</summary>
        public static float PerFrameAccelToPerSecondSquared(float pixelsPerFrameSquared)
            => pixelsPerFrameSquared * As3FramesPerSecond * As3FramesPerSecond;

        /// <summary>
        /// A per-frame damping factor applied over an arbitrary interval —
        /// <c>damping ^ (deltaTime * framesPerSecond)</c>.
        ///
        /// <para><b>Frame-rate independent by construction.</b> The naive <c>velocity *= 0.8</c> is only
        /// correct when the caller steps at exactly 30 Hz; at 60 Hz it would damp twice as often and the
        /// object would barely move. This exponent is exact at any rate and reduces to <c>0.8</c> when
        /// <paramref name="deltaTime"/> is one AS3 frame.</para>
        /// </summary>
        public static float DampingOverInterval(float perFrameDamping, float deltaTime)
        {
            if (perFrameDamping <= 0f)
            {
                return 0f;
            }

            return (float)Math.Pow(perFrameDamping, Math.Max(0f, deltaTime) * As3FramesPerSecond);
        }

        // ── the hold: accelerate toward the cursor, up to a per-axis cap ───────────────────────────

        /// <summary>
        /// One axis of the hold controller — <c>UnitPlayer.as:1247-1262</c>:
        /// <code>
        /// if(X  &lt; celX - 15 &amp;&amp; dx &lt;  teleSpeed) dx += teleAccel;
        /// if(X  &gt; celX + 15 &amp;&amp; dx &gt; -teleSpeed) dx -= teleAccel;
        /// </code>
        ///
        /// <para><b>Accelerate, never proportional.</b> There is no term involving the distance to the
        /// target — the object is nudged by a fixed <paramref name="accel"/> every tick until it is inside
        /// the deadzone or hits the cap. This is a different controller <i>in shape</i> from the
        /// <c>v = (target - pos) * k</c> the port used, not merely a different constant.</para>
        ///
        /// <para><b>Two ways the nudge is suppressed, and they are not the same.</b> Inside the deadzone
        /// the object is not decelerated at all — it coasts, and only
        /// <see cref="ApplyLevitDamping"/> bleeds its speed off. At the cap the nudge is skipped but the
        /// existing velocity is kept.</para>
        /// </summary>
        /// <param name="objectCoord">The held object's coordinate on this axis.</param>
        /// <param name="cursorCoord">The cursor's world coordinate on this axis.</param>
        /// <param name="velocity">Current velocity on this axis.</param>
        /// <param name="speedCap">Per-axis speed cap (<see cref="PlayerTeleSpeed"/> for the player).</param>
        /// <param name="accel">Per-tick nudge (<see cref="PlayerTeleAccel"/> for the player).</param>
        /// <param name="deadzone">Half-width of the no-nudge band (<see cref="Deadzone"/>).</param>
        public static float HoldAccelStep(float objectCoord, float cursorCoord, float velocity,
                                          float speedCap, float accel, float deadzone)
        {
            if (objectCoord < cursorCoord - deadzone && velocity < speedCap)
            {
                return velocity + accel;
            }

            if (objectCoord > cursorCoord + deadzone && velocity > -speedCap)
            {
                return velocity - accel;
            }

            return velocity;
        }

        /// <summary>
        /// The damping the held object gets every tick while <c>levit</c> is set —
        /// <c>Box.as:906-910</c>, <c>dx *= 0.8</c>.
        /// </summary>
        public static float ApplyLevitDamping(float velocity, float damping = LevitDamping)
        {
            return velocity * damping;
        }

        /// <summary>
        /// The speed the controller settles at — the fixed point of
        /// <c>v → (v + accel) * damping</c>, i.e. <c>accel * damping / (1 - damping)</c>.
        ///
        /// <para><b>4 px/tick for the player, not 8.</b> With
        /// <see cref="PlayerTeleAccel"/>=1 and <see cref="LevitDamping"/>=0.8 the object can never
        /// actually cruise at the cap: accelerating costs 1 per tick and damping removes 20%, so the two
        /// balance at 4. The cap of 8 only ever binds on the rare tick where damping has not yet been
        /// applied. Any test that asserts a held object approaches the cursor at
        /// <see cref="PlayerTeleSpeed"/> is asserting a number the oracle does not produce.</para>
        /// </summary>
        public static float TerminalSpeed(float accel, float damping)
        {
            if (damping >= 1f)
            {
                return float.PositiveInfinity;
            }

            return accel * damping / (1f - damping);
        }

        // ── the grab gate ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>UnitPlayer.as:1790</c> plus the two guards that sit outside it:
        /// <code>
        /// if(loc.celObj &amp;&amp; loc.celObj.levitPoss &amp;&amp; loc.celObj.onCursor
        ///    &amp;&amp; loc.celDist &lt;= pers.teleDist &amp;&amp; loc.celObj.massa &lt;= pers.maxTeleMassa)
        /// </code>
        /// with <c>mana &gt;= 200</c> at <c>:1754</c> and a line-of-sight test at <c>:1792</c>.
        ///
        /// <para><b>Every term has to be supplied by the caller; none is invented here.</b>
        /// <c>levitPoss</c> is a per-<i>instance</i> flag (<c>Obj.as:32</c>, default <c>true</c>) that
        /// ~20 subclasses mutate at runtime — not the port's per-definition
        /// <c>SupportsTelekinesis()</c>, which answers a different question from a hardcoded list.
        /// <c>massa</c> must be AS3's, i.e. <c>@massa / 50</c>
        /// (<see cref="PFE.Data.Definitions.MapObjectDefinition.GetAs3Massa"/>), or the last term
        /// compares 15..50000 against a cap of 25 and never passes.</para>
        ///
        /// <para><c>onCursor</c> and <c>celObj</c> are the same fact — the nearest-candidate scan at
        /// <c>:1769-1783</c> sets <c>loc.celObj</c> and flags it <c>onCursor</c> — so the port collapses
        /// both into <paramref name="hasTarget"/>.</para>
        ///
        /// <para><b><c>stay</c> is deliberately not a term here, and it used to be.</b> AS3's action gate
        /// never reads it; only the HUD hint at <c>GUI.as:1326</c> does, and <c>actTele</c> <i>clears</i>
        /// it on grab (<c>UnitPlayer.as:1831</c>), so it is a consequence of picking a prop up rather than
        /// a precondition for it. A port that required it refused exactly the grab AS3 allows — catching a
        /// prop out of the air. Removed 2026-10-02 on the owner's ruling that AS3 could grab a falling
        /// object. <b>Do not reintroduce it</b> from the <c>GUI.as:1326</c> hint; that line is a hint,
        /// not a gate.</para>
        /// </summary>
        public static bool CanGrab(bool hasTarget, bool levitPoss,
                                   float distanceSquared, float teleDistSquared,
                                   float massa, float maxTeleMassa, float mana)
        {
            if (!hasTarget || !levitPoss)
            {
                return false;
            }

            if (distanceSquared > teleDistSquared)
            {
                return false;
            }

            if (massa > maxTeleMassa)
            {
                return false;
            }

            return mana >= MinManaToGrab;
        }

        /// <summary>
        /// <c>UnitPlayer.as:1274</c> — the hold is dropped when the cursor strays past
        /// <c>teleDist * 1.2</c>, when mana runs out, or when the object stops being liftable.
        /// </summary>
        public static bool MustDrop(float distanceSquared, float teleDistSquared, float mana, bool levitPoss)
        {
            if (!levitPoss)
            {
                return true;
            }

            if (mana <= 0f)
            {
                return true;
            }

            return distanceSquared > teleDistSquared * DropDistanceMultiplier;
        }

        // ── mana ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The per-tick mana cost of holding — <c>UnitPlayer.as:1307</c>,
        /// <c>dmana -= teleSqrtMassa * pers.teleMult</c>, where <c>teleSqrtMassa</c> is
        /// <c>sqrt(massa)</c> when <c>massa &gt; telePorog</c> and <c>0</c> otherwise
        /// (<c>:1807-1813</c>).
        ///
        /// <para><b><c>telePorog</c> is a free-hold threshold, not a limit.</b> A light object costs
        /// nothing to hold; a heavy one costs the square root of its mass. The ladder is
        /// <c>v0=0.1 … v5=1.2</c> (<c>AllData.as:5213</c>), so at high skill almost everything is free to
        /// hold and only the throw costs anything.</para>
        ///
        /// <para>Returned as a positive magnitude; the caller subtracts it.</para>
        /// </summary>
        public static float HoldManaDrain(float massa, float telePorog, float teleMult)
        {
            if (massa <= telePorog)
            {
                return 0f;
            }

            return (float)Math.Sqrt(Math.Max(0f, massa)) * teleMult;
        }

        /// <summary>
        /// The mana a throw costs — <c>UnitPlayer.as:1852-1854</c>. Zero when <c>throwForce</c> is not
        /// positive, which is also the case in which the throw does no damage
        /// (<c>:1880</c> guards the emitter on the same term).
        /// </summary>
        public static float ThrowCost(float massa, float throwForce, float throwDmagic, float allDManaMult)
        {
            if (throwForce <= 0f)
            {
                return 0f;
            }

            return massa * throwDmagic * allDManaMult;
        }

        /// <summary>
        /// The velocity impulse a throw adds — <c>UnitPlayer.as:1847-1879</c>.
        ///
        /// <para>The direction is
        /// <c>(teleObj.X - X, teleObj.Y - scY/2 - Y + scY/2 - 10)</c> — i.e. from the player's <b>eye</b>
        /// position to the object's <b>centre</b>, minus a 10 px upward bias so a throw does not skim the
        /// floor. It is normalised to <c>throwForce</c>.</para>
        ///
        /// <para><b>A short wallet scales the throw down; it does not refuse it.</b> <c>:1862-1867</c>
        /// uses <c>throwForce * mana / cost</c> and zeroes mana, where the grab at <c>:1754</c> would have
        /// returned early. So a player who cannot afford the full throw still gets a weak one — and the
        /// object is released either way.</para>
        ///
        /// <para><b>One degenerate case is guarded rather than reproduced.</b> AS3's <c>norma</c> divides
        /// by the vector length, so an object sitting exactly on the player's eye position yields
        /// <c>NaN</c>/<c>Infinity</c> — which would then be added to <c>dx</c> and propagate into
        /// position. This returns a zero impulse instead.</para>
        /// </summary>
        public static void ThrowImpulse(float objectX, float objectY, float objectHalfHeight,
                                        float playerX, float playerY, float playerHalfHeight,
                                        float throwForce, float mana, float cost,
                                        out float impulseX, out float impulseY)
        {
            float dx = objectX - playerX;
            float dy = objectY - objectHalfHeight - playerY + playerHalfHeight - 10f;

            float scale = throwForce;
            if (cost > 0f && cost > mana)
            {
                scale = throwForce * (mana / cost);
            }

            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6f)
            {
                impulseX = 0f;
                impulseY = 0f;
                return;
            }

            float k = scale / length;
            impulseX = dx * k;
            impulseY = dy * k;
        }
    }
}
