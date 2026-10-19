namespace PFE.Systems.Weapons
{
    /// <summary>
    /// AS3's <b>melee weapon position</b> — <c>WClub.actions()</c>'s spring (<c>WClub.as:232-335</c>)
    /// and the per-sub-type aim-cell rules that feed it (<c>:254-293</c>) — as pure arithmetic.
    ///
    /// <para><b>What the oracle does, and what the port did instead.</b> A melee weapon in PFE is not
    /// held in the hand: it is a <i>levitating</i> object that floats at the aim point. Every frame
    /// <c>actions()</c> moves its own <c>X/Y</c> a <b>half step</b> toward
    /// <c>hold + norma(cel − hold, meleeR)</c> — a critically-overdamped spring — and the blade tip is
    /// then <c>X + cos(rot)·dlina</c>, i.e. the damage lands where the weapon <i>is</i>. <c>X</c> is
    /// <b>persistent</b>: nothing snaps it back to the hand when an attack ends, because for the
    /// player <c>weaponKrep == 0</c> (<c>UnitPlayer.as:368</c>) and the spring branch is the only one
    /// that ever runs. So the reach you see is <c>meleeR</c> — a <i>skill</i> (<c>&lt;sk id='meleeR'
    /// v0='100' vd='30'/&gt;</c>, <c>AllData.as:5226</c>) — plus the blade.</para>
    ///
    /// <para>The port instead snapped <c>State.X/Y</c> to the hold point whenever <c>t_attack &lt;= 0</c>
    /// and chased the cursor clamped to <c>dlina</c> during a swing. That is the reported symptom:
    /// the weapon "launches at the target and is then set back", and its reach is <c>2·dlina</c>
    /// rather than <c>meleeR + dlina</c> — so a melee weapon does not grow with the melee skill at
    /// all.</para>
    ///
    /// <para><b>Why this is a separate class.</b> <c>MeleeWeaponController</c> uses
    /// <c>UnityEngine.Vector2</c>, so every fixture that touches it dies with
    /// <c>SecurityException: ECall methods must be packaged into a system module</c> in the offline
    /// wall (measured: <c>MeleeHitWindowTests</c> + <c>MeleeOwnerStatTests</c> are 0-passed/26-failed
    /// there). The arithmetic that decides <i>where a melee weapon is and therefore what it can hit</i>
    /// must not live in a class no offline test can reach — same reasoning as
    /// <see cref="MeleeChargeMath"/> next door.</para>
    ///
    /// <para><b>Y is mirrored.</b> AS3's Y runs down, the port's up. Every displacement below is
    /// expressed in the <b>port's</b> frame, so the oracle's <c>+dlina*0.3</c> (below the cursor)
    /// appears as <c>−dlina*0.3</c>, and a rotation (degrees, about Z) is negated:
    /// <c>blumR_port = −blumR_as3</c>. Magnitude clamps are sign-independent and port unchanged.</para>
    /// </summary>
    public static class MeleeSpringMath
    {
        // ── Constants (AS3 literals and Pers declaration defaults) ────────────

        /// <summary>
        /// <c>Pers.meleeS</c> (<c>Pers.as:173</c>, <b>40</b>) — the distance in front of the body that
        /// the melee spring is anchored to. <b>A constant, not a stat:</b> nothing in the oracle ever
        /// writes it, which is why the port can hold it as a literal. AS3 also writes it as the
        /// throw-away <c>_loc1_ = 40 * owner.storona</c> at the top of <c>actions()</c>
        /// (<c>WClub.as:241</c>) before replacing it with <c>pers.meleeS * owner.storona</c>
        /// (<c>:263</c>, <c>:292</c>) — the same number, which is why one constant serves both.
        /// </summary>
        public const float MeleeSPx = 40f;

        /// <summary><c>Pers.meleeR</c> (<c>Pers.as:171</c>, <b>100</b>) — the reach of the melee
        /// spring, in Flash px. A <i>skill</i> stat (<c>AllData.as:5226</c>: <c>v0='100' vd='30'</c>),
        /// so it runs 100 at tier 0 to 250 at tier 5, and the alicorn effect multiplies it by 1.5
        /// (<c>AllData.as:5317</c>).</summary>
        public const float MeleeRDefaultPx = 100f;

        /// <summary><c>Pers.meleeRun</c> (<c>Pers.as:175</c>, <b>10</b>) — the melee "levitation run"
        /// speed in px per flash frame, i.e. the <b>maximum step</b> the spring may take toward its
        /// target. Raised by 3 per melee tier (<c>AllData.as:5227</c>) and quadrupled in the sky
        /// (<c>WClub.as:249-252</c>).</summary>
        public const float MeleeRunDefaultPx = 10f;

        /// <summary>
        /// The divisor AS3 applies to <c>&lt;phis massa&gt;</c> to get the weapon's mass
        /// (<c>Weapon.as:715-721</c>: <c>massa = param1.@massa / 50</c>), inverted again at the clamp
        /// site (<c>WClub.as:330</c>: <c>max(levitRun, 1/massa)</c>). Net effect:
        /// <b><c>50 / @massa</c></b> px per frame. A <c>massa</c> of 1 (a bat) gives 50 px/frame — the
        /// spring is effectively unclamped; 15 (a hammer) gives 3.3, so <c>meleeRun</c> wins there.
        /// </summary>
        public const float MassaScale = 50f;

        /// <summary><c>WClub.as:256</c> — for a horizontal swing the aim cell is pulled back toward
        /// the body by <c>dlina * 0.8</c> so that the blade's <i>tip</i>, not its pivot, lands on the
        /// cursor.</summary>
        public const float SwingAimBackFactor = 0.8f;

        /// <summary><c>WClub.as:257</c> — and dropped by <c>dlina * 0.3</c> (AS3 <c>+</c>, down; the
        /// port's frame is mirrored, see the class note).</summary>
        public const float SwingAimDownFactor = 0.3f;

        /// <summary><c>WClub.as:259</c> — the horizontal swing keeps the weapon's facing until the
        /// cursor is this far <i>behind</i> the body, so a swing that sweeps past the character does
        /// not flip it mid-arc.</summary>
        public const float TurnHysteresisPx = 100f;

        /// <summary><c>WClub.as:325</c> — <c>ready = (cel − X)² + (cel − Y)² &lt; 100</c>, i.e. the
        /// weapon counts as settled within 10 px of the aim cell. Read only by
        /// <c>Weapon.attack(true)</c> (S.A.T.S.), <c>Weapon.as:1282</c>; carried here so the value is
        /// not silently invented at the call site.</summary>
        public const float ReadyDistancePx = 10f;

        // ── Small helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Obj.norma(obj, len)</c> (<c>Obj.as:248-257</c>) — <b>clamp</b> a vector to at most
        /// <paramref name="maxLength"/>. It never lengthens: a vector already inside the limit is left
        /// exactly as it is. That asymmetry is load-bearing here — the spring's target is
        /// <c>hold + clamp(cel − hold, meleeR)</c>, so a cursor <i>inside</i> the melee radius is
        /// reached exactly, not pushed out to the radius.
        ///
        /// <para>A non-positive or non-finite <paramref name="maxLength"/> leaves the vector alone,
        /// which is the oracle's own result for a weapon with no <c>&lt;phis massa&gt;</c>
        /// (<c>massa = 0</c> → <c>1/0 = Infinity</c> → nothing is ever longer than it).</para>
        /// </summary>
        public static void ClampTo(ref float x, ref float y, float maxLength)
        {
            if (!(maxLength > 0f)) return;                       // also rejects NaN
            if (float.IsPositiveInfinity(maxLength)) return;

            float lengthSquared = x * x + y * y;
            if (lengthSquared <= maxLength * maxLength) return;

            float scale = maxLength / (float)System.Math.Sqrt(lengthSquared);
            x *= scale;
            y *= scale;
        }

        /// <summary>
        /// The per-frame step limit — <c>max(levitRun, 1/massa)</c> (<c>WClub.as:330</c>), in px per
        /// flash frame, with AS3's <c>massa = @massa/50</c> folded in as <see cref="MassaScale"/>.
        ///
        /// <para><b><paramref name="massaAttr"/> &lt;= 0 means "the row authored no massa"</b>, which
        /// in the oracle makes <c>1/massa</c> infinite and the step unclamped. That is a real state —
        /// it is what a <c>&lt;phis&gt;</c> with no <c>massa</c> produces — so it is reproduced rather
        /// than defaulted.</para>
        /// </summary>
        public static float StepClampPx(float meleeRun, float massaAttr)
        {
            if (!(massaAttr > 0f)) return float.PositiveInfinity;
            return (float)System.Math.Max(meleeRun, MassaScale / massaAttr);
        }

        // ── Aim cell, per sub-type ────────────────────────────────────────────

        /// <summary>
        /// mtip 0 (swing): the aim cell is the cursor pulled <b>back</b> along the facing by
        /// <c>dlina*0.8</c> and <b>down</b> by <c>dlina*0.3</c> (<c>WClub.as:256-257</c>). The blade
        /// sweeps an arc about the weapon's own position, so the pivot must sit short of the cursor
        /// for the tip to reach it.
        /// </summary>
        public static void SwingAimCell(float aimX, float aimY, float dlina, float storona,
                                        out float celX, out float celY)
        {
            celX = aimX - dlina * SwingAimBackFactor * storona;
            celY = aimY - dlina * SwingAimDownFactor;            // AS3 `+` in a y-down frame
        }

        /// <summary>
        /// mtip 2 (overhead): the aim cell is pulled <b>back toward the hold point</b> by
        /// <c>norma(cel − hold, (dlina + mindlina)/2)</c> — i.e. by the mean blade length, and by the
        /// whole distance when the cursor is nearer than that (<c>WClub.as:280-287</c>).
        ///
        /// <para><b>The literal is odd and is kept anyway.</b> When the cursor is inside the mean blade
        /// length the cell collapses onto the hold point, and the chop then runs along
        /// <c>atan2(cel − body centre)</c> — an almost vertical overhead rather than a lunge at the
        /// cursor. That is what the oracle does; "fixing" it here would be inventing a second rule.</para>
        /// </summary>
        public static void OverheadPullBack(ref float celX, ref float celY,
                                            float anchorX, float anchorY,
                                            float dlina, float minDlina)
        {
            float dx = celX - anchorX;
            float dy = celY - anchorY;
            ClampTo(ref dx, ref dy, 0.5f * (dlina + minDlina));
            celX -= dx;
            celY -= dy;
        }

        /// <summary>
        /// The facing a swing uses — <c>storona = owner.celX &gt; owner.X − 100*owner.storona ? 1 : −1</c>
        /// (<c>WClub.as:259</c>). Note the hysteresis is measured against the <b>body's</b> facing
        /// (<c>owner.storona</c>), not the weapon's.
        /// </summary>
        /// <param name="hysteresis">
        /// The 100 px of <c>:259</c> <b>in the caller's units</b> — 1.0 for a caller working in Unity
        /// units. Passed in rather than baked in because this class is otherwise unit-agnostic, and a
        /// px constant compared against unit coordinates is a 100× error that still returns a
        /// plausible ±1.
        /// </param>
        public static float SwingFacing(float cursorX, float bodyX, float bodyFacingSign,
                                        float hysteresis)
            => cursorX > bodyX - hysteresis * bodyFacingSign ? 1f : -1f;

        /// <summary>
        /// The facing a thrust or an overhead uses — <c>storona = celRX &gt; owner.X ? 1 : −1</c>
        /// (<c>WClub.as:288</c>). <b>No hysteresis</b>, unlike <see cref="SwingFacing"/>: the two
        /// sub-types genuinely differ here, and since a thrust's cell is frozen for the duration of
        /// the attack (<c>:267-279</c>) so is its facing.
        /// </summary>
        public static float AimFacing(float celX, float bodyX) => celX > bodyX ? 1f : -1f;

        // ── The spring itself ─────────────────────────────────────────────────

        /// <summary>One frame of the spring: the new position, the step it took, and the visual tilt.</summary>
        public struct SpringResult
        {
            /// <summary>The weapon's new <c>X</c> (port frame, Unity units).</summary>
            public float X;

            /// <summary>The weapon's new <c>Y</c>.</summary>
            public float Y;

            /// <summary>The step actually taken this frame, before the position write — AS3's
            /// <c>del</c> after both <c>norma</c> calls.</summary>
            public float StepX;
            public float StepY;

            /// <summary>
            /// AS3 <c>blumR</c> in <b>degrees</b>, added to the drawn rotation only
            /// (<c>WClub.as:332</c>, applied at <c>:747</c>). Never touches <c>rot</c>, so it cannot
            /// move the hit tip — a "motion blur" lean, not an aim change.
            /// </summary>
            public float BlumRDeg;

            /// <summary>AS3 <c>ready</c> — settled within <see cref="ReadyDistancePx"/> of the aim cell.</summary>
            public bool Ready;
        }

        /// <summary>
        /// One frame of <c>WClub.actions()</c>'s position block (<c>WClub.as:310-335</c>), for the
        /// <b>player</b> branch (<c>weaponKrep == 0</c>, <c>:328-331</c> included):
        ///
        /// <code>
        /// del      = norma((celX, celY) − (anchorX, anchorY), meleeR)
        /// ready    = (celX − X)² + (celY − Y)² &lt; 100
        /// del      = ((anchorX + del.x) − X) / 2, ((anchorY + del.y) − Y) / 2
        /// del      = norma(del, max(levitRun, 1/massa))
        /// blumR    = (del.x * storona + del.y) / 2
        /// X += del.x;  Y += del.y
        /// </code>
        ///
        /// <para><b>Two clamps, and they are not the same clamp.</b> The first limits how far the
        /// <i>target</i> may sit from the hold point (the melee radius); the second limits how far the
        /// weapon may <i>travel</i> in one frame (its levitation speed). Dropping either produces a
        /// plausible-looking weapon: without the first the reach is unbounded, without the second the
        /// weapon teleports to the cursor.</para>
        ///
        /// <para><b>The half step is not damping — it is the spring.</b> <c>/2</c> is AS3's literal,
        /// and it is what makes the weapon converge geometrically instead of arriving in one frame even
        /// when the step clamp is infinite.</para>
        /// </summary>
        /// <param name="celX">The aim cell (port frame). For mtip 1/2 this is <c>celRX</c>, the
        /// possibly-frozen and possibly-pulled-back cell — not the raw cursor.</param>
        /// <param name="anchorX">The hold point's x — <c>owner.X + meleeS*owner.storona</c>.</param>
        /// <param name="anchorY">The hold point's y — AS3 <c>owner.weaponY</c>.</param>
        /// <param name="meleeR">The melee radius, in Unity units (already divided by 100).</param>
        /// <param name="stepClamp">The per-frame travel limit in Unity units; may be
        /// <see cref="float.PositiveInfinity"/> for an unclamped (no <c>massa</c>, or non-player)
        /// spring.</param>
        /// <param name="readyDistance">
        /// <see cref="ReadyDistancePx"/> <b>in the caller's units</b> — 0.1 for a caller working in
        /// Unity units. Passed in for the same reason as <c>SwingFacing</c>'s hysteresis: a px
        /// constant compared against unit coordinates would make <c>ready</c> permanently true.
        /// </param>
        public static SpringResult Resolve(float celX, float celY,
                                           float anchorX, float anchorY,
                                           float x, float y,
                                           float meleeR, float stepClamp, float storona,
                                           float readyDistance)
        {
            // `norma(del, meleeR)` — the TARGET offset from the hold point.
            float targetDx = celX - anchorX;
            float targetDy = celY - anchorY;
            ClampTo(ref targetDx, ref targetDy, meleeR);

            // `ready` measures against the CELL, not the clamped target (:322-325).
            float readyDx = celX - x;
            float readyDy = celY - y;

            // The half step toward the clamped target.
            float stepX = (anchorX + targetDx - x) * 0.5f;
            float stepY = (anchorY + targetDy - y) * 0.5f;

            // `norma(del, max(levitRun, 1/massa))` — the per-frame travel limit.
            ClampTo(ref stepX, ref stepY, stepClamp);

            return new SpringResult
            {
                X         = x + stepX,
                Y         = y + stepY,
                StepX     = stepX,
                StepY     = stepY,
                // (del.x*storona + del.y)/2 in a y-down frame; both the y term and the whole angle are
                // negated in the port's (see the class note).
                BlumRDeg  = (stepY - stepX * storona) * 0.5f,
                Ready     = readyDx * readyDx + readyDy * readyDy < readyDistance * readyDistance,
            };
        }
    }
}
