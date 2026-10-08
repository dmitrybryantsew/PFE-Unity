using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// AS3 <c>UnitZombie.jump()</c> and its <c>checkJump()</c> headroom probe — the arithmetic behind the
    /// zombie's hop, as pure functions of its numbers.
    ///
    /// <para><b>Why extracted.</b> Same reason as <see cref="StaggerMath"/> and
    /// <see cref="UnitDropThroughMath"/>: the state is a bare <c>int</c> on a <c>MonoBehaviour</c> and the
    /// thresholds are bare literals in the oracle, so while the arithmetic lived inside the brain it was
    /// unassertable offline — and <b>every way to get it wrong is silent</b>. A <c>&gt;</c> written as
    /// <c>&gt;=</c>, a probe one tile out, a cooldown off by one: none of them throws, none of them is
    /// covered by an existing test, and all three read as "the zombie feels a bit off".</para>
    ///
    /// <para><b>The sign, which is the one thing that silently inverts.</b> AS3 writes
    /// <c>dy = -jumpdy</c> because AS3's Y grows <i>downward</i>; the port's grows <i>upward</i>, so the
    /// same impulse is <b>positive</b> here. The negation lives in the axis and not in the caller — see
    /// <see cref="ShouldJump"/> for the same mirror applied to the trigger, and
    /// <c>UnitDropThroughMath.ShouldDropThrough</c> for its exact counterpart. The two are mirrors of one
    /// measurement (<c>celDY</c>) and are pinned against each other by a test rather than by a comment.</para>
    /// </summary>
    public static class UnitJumpMath
    {
        /// <summary>
        /// AS3 <c>UnitZombie.as:683</c> — <c>else if(celDY &lt; -40) { aiVNapr = -1; }</c>, the band that
        /// means "the target is above me" and the only thing that arms the jump
        /// (<c>:839</c> <c>aiVNapr &lt; 0</c>).
        ///
        /// <para><c>celDY = celY - Y + scY</c> (<c>:678</c>) reduces to <i>the target's centre minus this
        /// unit's top</i>, because <c>Y1 = Y - scY</c> is the top and <c>celY</c> is the target's centre.
        /// AS3's Y runs down, so a <i>negative</i> <c>celDY</c> means the target is <b>higher</b>.</para>
        /// </summary>
        public const float TargetAboveJumpPixels = 40f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:467/475</c> — the lower of the two probe heights,
        /// <c>loc.getAbsTile(X, Y - 85)</c>. Measured from the unit's <b>feet</b>, and AS3's Y runs down, so
        /// this is <b>85 px above the feet</b> — about two tile-heights.
        /// </summary>
        public const float HeadroomRisePixels = 85f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:471/479</c> — the upper probe height,
        /// <c>loc.getAbsTile(X, Y - 125)</c>: <b>125 px above the feet</b>, just over three tiles. The pair
        /// is what gives the zombie room to reach its apex (~171 px for <c>jumpdy</c> 18) without clipping.
        /// </summary>
        public const float HeadroomHighRisePixels = 125f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:475/479</c> — <c>X + 40 * storona</c>: the second probe column is one tile
        /// toward the unit's facing (<c>storona</c>), so the zombie does not launch itself into a wall it is
        /// about to walk into.
        /// </summary>
        public const float HeadroomForwardPixels = 40f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:393</c> — <c>aiJump = Math.floor(30 + Math.random() * 50)</c>: the floor of
        /// the self-set jump cooldown.
        /// </summary>
        public const int CooldownMinTicks = 30;

        /// <summary>
        /// The span of the cooldown roll: <c>Math.random() * 50</c>, i.e. <c>Math.floor</c> of it is
        /// <c>0..49</c>. Callers draw an <c>int</c> in <c>[0, <see cref="CooldownRollRange"/>)</c> and pass
        /// it to <see cref="CooldownTicks"/>, which reproduces the oracle's distribution exactly.
        /// </summary>
        public const int CooldownRollRange = 50;

        /// <summary>
        /// AS3 <c>aiVNapr &lt; 0</c> (<c>UnitZombie.as:839</c>) — should the zombie leave the ground because
        /// its target is overhead?
        ///
        /// <para><b>Both arguments are world pixels and both are the oracle's own two quantities</b>, so no
        /// caller re-derives "how far above" from a third definition: <paramref name="unitTopPixelY"/> is
        /// <c>Y1 = Y - scY</c> and <paramref name="targetCentrePixelY"/> is <c>celY</c>. The oracle compares
        /// them in AS3's Y-down space (<c>celDY &lt; -40</c>); flipping the axis negates the difference, so
        /// the port's form is <c>target − top &gt; 40</c>.</para>
        ///
        /// <para><b>Strictly greater, and the boundary is the point.</b> AS3's band is
        /// <c>celDY &gt; 40</c> / <c>celDY &lt; -40</c> with a dead zone between, so exactly 40 px above is
        /// <i>not</i> a jump — it is <c>aiVNapr = 0</c>, "level enough". A <c>&gt;=</c> here would make the
        /// zombie hop at a player it is already standing next to, and the symptom would read as a jump
        /// tuned too eagerly rather than as an off-by-one.</para>
        /// </summary>
        /// <param name="unitTopPixelY">The unit's <b>top</b> in world pixels — its centre plus half its
        /// height. Not its feet: AS3 measures from <c>Y1</c>.</param>
        /// <param name="targetCentrePixelY">The target's <b>centre</b> in world pixels — AS3's <c>celY</c>.
        /// </param>
        public static bool ShouldJump(float unitTopPixelY, float targetCentrePixelY)
        {
            return targetCentrePixelY - unitTopPixelY > TargetAboveJumpPixels;
        }

        /// <summary>
        /// AS3 <c>loc.getAbsTile(X + 40 * storona, …)</c> — the x of the forward probe column, one tile
        /// toward the unit's facing.
        /// </summary>
        /// <param name="feetPixelX">The unit's x in <b>room-local</b> pixels — the same space AS3's
        /// <c>X</c> is in, i.e. world pixels with the room origin already subtracted.</param>
        /// <param name="facing">AS3 <c>storona</c>, <c>+1</c> or <c>-1</c>. Zero is treated as
        /// <c>+1</c> rather than as "no column": a unit with no facing still has to probe somewhere, and
        /// probing its own column twice would silently halve the check.</param>
        public static float HeadroomAheadPixelX(float feetPixelX, int facing)
        {
            return feetPixelX + (facing < 0 ? -HeadroomForwardPixels : HeadroomForwardPixels);
        }

        /// <summary>AS3 <c>Y - 85</c> — the lower probe height, in the port's Y-up room-local pixels.</summary>
        public static float HeadroomLowPixelY(float feetPixelY)
        {
            return feetPixelY + HeadroomRisePixels;
        }

        /// <summary>AS3 <c>Y - 125</c> — the upper probe height, in the port's Y-up room-local pixels.</summary>
        public static float HeadroomHighPixelY(float feetPixelY)
        {
            return feetPixelY + HeadroomHighRisePixels;
        }

        /// <summary>
        /// AS3's <c>aiJump = Math.floor(30 + Math.random() * 50)</c> — the cooldown a fresh jump arms,
        /// <b>30..79</b> ticks inclusive (<c>UnitZombie.as:393</c>), i.e. 1.0–2.63 s at the oracle's 30 Hz.
        /// </summary>
        /// <param name="roll">
        /// An integer draw in <c>[0, <see cref="CooldownRollRange"/>)</c>. Callers should obtain it as
        /// <c>Random.Range(0, CooldownRollRange)</c>, which is the port's exact equivalent of
        /// <c>Math.floor(Math.random() * 50)</c>. A <see cref="float"/>-range draw
        /// (<c>Random.Range(0f, 50f)</c>) is <i>not</i> equivalent — Unity's float overload is inclusive of
        /// its upper bound, so it can return <c>50</c> and produce <c>80</c>, a tick longer than the oracle
        /// can ever give. <see cref="StaggerMath.AlarmTicks"/> documents the same trap.
        /// </param>
        /// <remarks>
        /// The out-of-range guard clamps rather than trusting the draw, because a bad roll would otherwise
        /// produce a cooldown nothing else in the system can explain — and it would present as "the zombie
        /// stopped jumping", which reads as a broken feature rather than as a bad random number.
        /// </remarks>
        public static int CooldownTicks(int roll)
        {
            return CooldownMinTicks + Mathf.Clamp(roll, 0, CooldownRollRange - 1);
        }

        /// <summary>
        /// AS3 <c>UnitZombie.as:308</c> — the number of cells the <c>jump</c> row's pose is divided into,
        /// from <c>anims["jump"].setStab((dy * 0.6 + 8) / 16)</c>.
        ///
        /// <para><b>It is the oracle's literal, not the row's <c>len</c>.</b> Every <c>stab</c> row in
        /// <c>AllData.as</c> is a <c>jump</c> and they are <c>len='14'</c> once and <c>len='16'</c> seven
        /// times, while AS3 divides by a hardcoded <c>16</c> either way — so deriving this from
        /// <see cref="PFE.Data.Definitions.AnimationFrame.length"/> would "fix" the one row that is 14 and
        /// disagree with the game on it. <c>setStab</c>'s own <c>f = maxf * progress</c> is the only place
        /// <c>len</c> enters, and it enters as the <i>multiplier</i>.</para>
        /// </summary>
        public const float JumpPoseCells = 16f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:308</c> — the <c>0.6</c> in <c>(dy * 0.6 + 8) / 16</c>: how many cells of
        /// the row one px/frame of vertical speed is worth. The row therefore sweeps about 13 px/frame of
        /// falling per half of its 16 cells, and <c>jumpdy</c> 18 saturates it — which is why the clamp
        /// inside <see cref="PFE.Data.Definitions.AnimationFrame.FrameAtProgress"/> is reached on every
        /// real jump rather than being a safety net.
        /// </summary>
        public const float JumpPoseRisePerCell = 0.6f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:308</c> — the <c>+ 8</c>: the pose at zero vertical speed, i.e. the apex.
        /// Half of <see cref="JumpPoseCells"/>, so the row reads as launch at cell 0, spread at cell 8 and
        /// landing at cell 15.
        /// </summary>
        public const float JumpPoseMidpointCells = 8f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:308</c> — <c>anims["jump"].setStab((dy * 0.6 + 8) / 16)</c>: the
        /// <c>jump</c> row's pose as a function of <b>vertical velocity</b>, not of elapsed time.
        ///
        /// <para><b>Why velocity and not a timer.</b> The row is one launch-to-landing arc of 16 cells, and
        /// selecting the cell from <c>dy</c> makes the pose agree with the physics for free: the zombie
        /// reaches the spread cell exactly at its apex and the landing cell exactly when it is falling
        /// fastest. A port that played the row on a 16-frame timer would look right at 30 Hz on flat ground
        /// and wrong the moment gravity, a platform or a ceiling changed the arc — which is most jumps.</para>
        ///
        /// <para><b>The sign is the one thing that silently inverts, and the property name invites it.</b>
        /// AS3's <c>dy</c> is <i>positive while falling</i> because AS3's Y grows downward, so the oracle's
        /// rising pose (cell 0) is <c>dy &lt; 0</c>. This port's Y grows <b>upward</b>, so
        /// <paramref name="velocityPixelsPerFrameY"/> is positive while <i>rising</i> and the two are
        /// mirrors — hence the negation below. Getting it wrong does not throw and does not stall: the row
        /// simply plays backwards, showing the landing pose on the way up, which reads as "the jump
        /// animation looks off" rather than as a sign error. Note that
        /// <c>UnitController.VelocityPixelsPerFrame</c> — despite its name — carries <i>this</i> port's
        /// Y-up sign on both of its backing paths (<c>TilePhysicsController.dy</c> has gravity subtracted
        /// from it, <c>TilePhysicsController.cs:1045</c>), so it is the correct argument unmodified.</para>
        /// </summary>
        /// <param name="velocityPixelsPerFrameY">The unit's vertical velocity in px/frame, <b>positive while
        /// rising</b> — i.e. <c>UnitController.VelocityPixelsPerFrame.y</c>. AS3's <c>dy</c> is this value
        /// negated.</param>
        /// <returns>A pose in <c>0..1</c> for <see cref="PFE.Data.Definitions.AnimationFrame.FrameAtProgress"/>.
        /// Deliberately not clamped here: the oracle passes the raw expression and lets <c>setStab</c> clamp,
        /// so clamping twice would be one place for the two to disagree.</returns>
        public static float JumpPoseProgress(float velocityPixelsPerFrameY)
        {
            float dyAs3 = -velocityPixelsPerFrameY;
            return (dyAs3 * JumpPoseRisePerCell + JumpPoseMidpointCells) / JumpPoseCells;
        }

        /// <summary>
        /// AS3 <c>UnitZombie.as:582-585</c> — <c>if(this.aiJump &gt; 0) { --this.aiJump; }</c>, clamped at
        /// zero for the same reason <see cref="StaggerMath.Tick"/> is: the oracle's guard already makes a
        /// negative unreachable, so clamping here asserts the invariant instead of repeating the guard.
        /// </summary>
        public static int Tick(int ticks)
        {
            return ticks > 0 ? ticks - 1 : 0;
        }
    }
}
