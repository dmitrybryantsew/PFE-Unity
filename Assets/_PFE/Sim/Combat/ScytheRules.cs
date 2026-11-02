using System;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The whole of AS3 <c>UnitScythe</c>'s behaviour, as pure functions —
    /// <c>fe/unit/UnitScythe.as:1-140</c>.
    ///
    /// <para><b>Why the blade gets its own rules class instead of living in
    /// <see cref="BossAlicornRules"/>.</b> The oracle splits them the same way:
    /// <c>UnitBossAlicorn.emit()</c> (<c>:294-307</c>) decides <i>where</i> a blade appears and how
    /// hard it hits, and <c>UnitScythe.run()/control()</c> decides what it does once it exists. The
    /// two never read each other's state, and the blade is spawned by a <i>different</i> unit — so
    /// folding its orbit arithmetic into the boss's decision table would put a second unit's physics
    /// in the boss's rules file. The emit-side constants stay in <see cref="BossAlicornRules"/>
    /// (<c>ScytheSpawnPixels</c>, <c>BoundScytheHealthMultiplier</c>, <c>ScytheDamageScale</c>); the
    /// blade's own numbers are here.</para>
    ///
    /// <para><b>Unity-free on purpose</b> — the same reason every other <c>Sim/Combat/*Rules</c>
    /// class is: a <c>MonoBehaviour</c> cannot be constructed in the offline harness, so a fixture
    /// that drove <c>ScytheController</c> could not run there. Everything here is arithmetic and
    /// gates; <c>ScytheController</c> reads these and performs the side effects.</para>
    ///
    /// <para><b>Y is mirrored.</b> AS3's Y grows down from the ceiling; every pixel value this class
    /// returns is in the port's y-up room-local space. The orbit is the one place the mirror changes a
    /// <i>sign</i> rather than a coordinate, and <see cref="OrbitPixels"/> says so.</para>
    /// </summary>
    public static class ScytheRules
    {
        // ── constructor literals (UnitScythe.as:9-45) ─────────────────────────────────────────────

        /// <summary>
        /// <c>napr = -1</c> (<c>:9</c>) — the "no angle latched yet" sentinel.
        /// </summary>
        /// <remarks>
        /// <b>It is a sentinel, not a direction.</b> <c>getNapr()</c> latches the aim on its first
        /// call and never re-aims (<c>:89-92</c>: <c>if(this.napr == -1) this.napr = _loc1_;</c>), so a
        /// free blade flies in a <i>straight line</i> from wherever it was released toward wherever the
        /// player stood at that instant. Re-aiming every tick would make it a homing missile, which is
        /// a different (and much harder) attack.
        /// </remarks>
        public const float NoAngle = -1f;

        /// <summary><c>skor = 1.5</c> (<c>:15</c>) — the release speed, in pixels per frame.</summary>
        public const float InitialSpeed = 1.5f;

        /// <summary>
        /// <c>this.skor *= 1.05</c> (<c>:126</c>) — the per-tick acceleration. Compounded every tick
        /// with no cap, so a blade that survives 60 ticks is travelling at
        /// <c>1.5 * 1.05^60 ≈ 28</c> px/frame — faster than anything else in the game.
        /// </summary>
        public const float SpeedGrowth = 1.05f;

        /// <summary>
        /// <c>if(this.t &gt; 60) attKorp(cel)</c> (<c>:134-137</c>) — <b>a blade cannot hurt anything
        /// for its first sixty ticks</b> (two seconds at 30 Hz).
        /// </summary>
        /// <remarks>
        /// This is the single most surprising line in the class, and it is deliberate: the blade is a
        /// <i>sweeping hazard</i>, not a projectile. It is released, spirals outward gaining speed, and
        /// only becomes dangerous once it is fast. A port that damaged on contact from tick 0 would
        /// turn a dodgeable sweep into an unavoidable hit at the boss's feet.
        /// </remarks>
        public const int ContactDelayTicks = 60;

        /// <summary><c>bindRad = 200</c> (<c>:25</c>) — the orbit radius, in pixels.</summary>
        public const float BindRadius = 200f;

        /// <summary><c>bindKoef = 0.05</c> (<c>:27</c>) — radians of orbit per tick, i.e. a full
        /// revolution every ~126 ticks (4.2 s at 30 Hz).</summary>
        public const float BindKoef = 0.05f;

        /// <summary>
        /// <c>while(_loc2_ &lt; 6) emit(_loc2_++)</c> (<c>UnitBossAlicorn.as:727-732</c>) — the ring has
        /// six slots, and <c>bindN</c> is the slot index.
        /// </summary>
        public const int BindCount = 6;

        /// <summary><c>if(this.dr &lt; 30) this.dr += 0.5</c> (<c>:66-69</c>) — the visual spin-up, in
        /// degrees per frame.</summary>
        public const float SpinAcceleration = 0.5f;

        /// <summary>See <see cref="SpinAcceleration"/> — the terminal spin rate.</summary>
        public const float SpinMax = 30f;

        /// <summary><c>if(vis.osn.alpha &lt; 1) vis.osn.alpha += 0.05</c> (<c>:72-75</c>) — the blade
        /// fades in over 20 frames.</summary>
        public const float BladeFadeStep = 0.05f;

        /// <summary><c>if(vis.vzz.alpha &lt; 1) vis.vzz.alpha += 0.02</c> (<c>:76-79</c>) — the trail
        /// fades in over 50.</summary>
        public const float TrailFadeStep = 0.02f;

        // ── the orbit (UnitScythe.as:95-115) ──────────────────────────────────────────────────────

        /// <summary>
        /// <c>t * bindKoef + PI * 2 * bindN / 6</c> (<c>:99-100</c>) — the blade's phase on the ring.
        /// </summary>
        /// <remarks>
        /// <c>bindN</c> spaces the six blades evenly around the circle; <c>t * bindKoef</c> spins the
        /// whole ring. Both terms are in the oracle's expression, and the <c>/ 6</c> is the literal
        /// slot count rather than <see cref="BindCount"/> in AS3 — the two are the same number, and
        /// keeping the constant named means a fixture can assert it.
        /// </remarks>
        public static double OrbitPhase(int bindN, int t)
        {
            return t * BindKoef + Math.PI * 2.0 * bindN / BindCount;
        }

        /// <summary>
        /// <c>X = bind.X - sin(phase) * bindRad; Y = bind.Y - bind.scY / 2 - cos(phase) * bindRad</c>
        /// (<c>:99-100</c>) — the blade's position on its orbit, in <b>room-local y-up</b> pixels.
        /// </summary>
        /// <param name="bossCentreX">The boss's horizontal centre, room-local pixels.</param>
        /// <param name="bossCentreY">
        /// The boss's vertical centre, room-local pixels — <b>already</b> <c>bind.Y - bind.scY/2</c> in
        /// AS3 terms. AS3 stores a unit's <c>Y</c> at its <b>feet</b> and measures down, so
        /// <c>Y - scY/2</c> moves <i>up</i> by half a body; the port stores the feet at the bottom and
        /// measures up, so the same point is <c>feetY + scY/2</c>. Taking the caller's centre rather
        /// than the feet is what keeps this function from having to know which engine it is in.
        /// </param>
        /// <param name="bindN">The ring slot, 0..5.</param>
        /// <param name="t">The blade's tick counter, <b>already</b> incremented for this frame.</param>
        /// <remarks>
        /// <para><b>The X term carries across unchanged and the Y term does not.</b> X is not mirrored
        /// between the engines, so <c>bind.X - sin(φ)·R</c> is the same expression in both. Y is
        /// mirrored, and the mirror flips the sign of the offset: AS3's
        /// <c>Y_centre_as3 - cos(φ)·R</c> becomes <c>Y_centre_port + cos(φ)·R</c> once
        /// <c>y_port = H - y_as3</c> is substituted. Copying the minus sign across would put the ring
        /// a half-period out of phase with the oracle — the blades would still form a circle, so the
        /// screenshot looks right and only a fixture can catch it.</para>
        /// </remarks>
        public static BossAlicornRules.Vector2Pixels OrbitPixels(
            float bossCentreX, float bossCentreY, int bindN, int t)
        {
            double phase = OrbitPhase(bindN, t);
            return new BossAlicornRules.Vector2Pixels(
                bossCentreX - (float)Math.Sin(phase) * BindRadius,
                bossCentreY + (float)Math.Cos(phase) * BindRadius);
        }

        // ── the free flight (UnitScythe.as:82-93, :117-138) ───────────────────────────────────────

        /// <summary>
        /// <c>Math.atan2(cel.X - X, cel.Y - Y)</c> (<c>:88</c>) — the angle the blade latches at
        /// release.
        /// </summary>
        /// <remarks>
        /// <b>The arguments are swapped relative to the usual reading.</b> AS3's
        /// <c>Math.atan2(y, x)</c> is given <c>ΔX</c> as its <c>y</c> and <c>ΔY</c> as its <c>x</c>, so
        /// the resulting angle satisfies <c>sin(θ) = ΔX/r</c> and <c>cos(θ) = ΔY/r</c> — which is
        /// exactly what <see cref="VelocityFromAngle"/> then reconstructs. Treating it as
        /// <c>atan2(ΔY, ΔX)</c> swaps the axes and sends every blade out at a right angle to the
        /// player.
        /// </remarks>
        public static float AngleToTarget(float deltaX, float deltaY)
        {
            return (float)Math.Atan2(deltaX, deltaY);
        }

        /// <summary><c>dx = sin(napr) * skor; dy = cos(napr) * skor</c> (<c>:127-128</c>) — the
        /// velocity, in pixels per frame, <b>y-up</b>.</summary>
        /// <remarks>
        /// No mirror is needed here and that is not an oversight: the angle was built from the port's
        /// own deltas by <see cref="AngleToTarget"/>, so <c>cos</c> already carries the port's sign.
        /// The mirror lives in <see cref="AngleToTarget"/>'s <i>inputs</i>, not its output.
        /// </remarks>
        public static BossAlicornRules.Vector2Pixels VelocityFromAngle(float napr, float speed)
        {
            return new BossAlicornRules.Vector2Pixels(
                (float)Math.Sin(napr) * speed,
                (float)Math.Cos(napr) * speed);
        }

        /// <summary><c>this.skor *= 1.05</c> (<c>:126</c>) — one tick of the blade's acceleration.</summary>
        public static float AdvanceSpeed(float speed) => speed * SpeedGrowth;

        /// <summary><c>if(this.napr == -1)</c> (<c>:89</c>) — whether the aim is still unlatched.</summary>
        public static bool NeedsAngle(float napr) => napr == NoAngle;

        /// <summary>
        /// <c>if(t &gt; 60) attKorp(cel)</c> (<c>:134</c>) — whether the blade may damage this tick.
        /// </summary>
        public static bool CanDamage(int t) => t > ContactDelayTicks;

        /// <summary>
        /// <c>if(X &gt;= loc.limX || X &lt;= 0 || Y &gt;= loc.limY || Y &lt;= 0) die()</c>
        /// (<c>:106-109</c>) — a free blade dies on leaving the room.
        /// </summary>
        /// <remarks>
        /// <b>The same expression works in both engines, which is why it is not mirrored.</b> Each
        /// room edge is <c>0</c> and the opposite edge is the limit, in both spaces; only the
        /// <i>names</i> of the edges differ (AS3's <c>0</c> is the ceiling, the port's is the floor).
        /// <para>Note this is a <i>room-bounds</i> test, not a wall test: the blade passes through
        /// walls. AS3's <c>UnitScythe.run()</c> overrides the base <c>run()</c> entirely, so the
        /// oracle's wall resolution and its friction never run for a blade at all.</para>
        /// </remarks>
        public static bool OutOfBounds(float x, float y, float limitX, float limitY)
        {
            return x >= limitX || x <= 0f || y >= limitY || y <= 0f;
        }

        /// <summary><c>if(bind &amp;&amp; (bind as Unit).sost &gt;= 3) die()</c> (<c>:130-133</c>) — a
        /// bound blade dies with its host.</summary>
        public static bool HostIsDead(bool hasHost, bool hostAlive) => hasHost && !hostAlive;

        // ── the visual spin (UnitScythe.as:64-80) ─────────────────────────────────────────────────

        /// <summary><c>if(this.dr &lt; 30) this.dr += 0.5</c> (<c>:66-69</c>).</summary>
        public static float SpinStep(float dr) => dr < SpinMax ? dr + SpinAcceleration : SpinMax;

        /// <summary><c>if(alpha &lt; 1) alpha += step</c> (<c>:72-79</c>), clamped at one so the blade
        /// does not overshoot into a wraparound.</summary>
        public static float FadeStep(float alpha, float step)
        {
            if (alpha >= 1f) return 1f;
            float next = alpha + step;
            return next > 1f ? 1f : next;
        }
    }
}
