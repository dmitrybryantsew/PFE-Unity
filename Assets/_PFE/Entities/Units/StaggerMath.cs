using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// AS3 <c>Unit.shok</c> — the unit's <b>stagger</b> timer — as pure functions of its two numbers.
    ///
    /// <para><b>What <c>shok</c> actually is.</b> It is declared <c>public var shok:int = 0</c>
    /// (<c>fe/unit/Unit.as:386</c>) beside <c>public var maxShok:int = 30</c> (<c>:388</c>), it counts
    /// down in <c>Unit.actions()</c> (<c>:3065-3068</c>), and it has exactly <b>two</b> effects in the
    /// whole oracle — both of them on <i>offence</i>:
    /// <list type="bullet">
    /// <item>the zombie's contact hit is scaled to half — <c>attKorp(celUnit, shok &lt;= 0 ? 1 : 0.5)</c>
    /// (<c>UnitZombie.as:940</c>);</item>
    /// <item>the raider is refused every attack path outright — three separate gates of
    /// <c>shok &lt;= 0</c> (<c>UnitRaider.as:1486</c>, <c>:1498</c>, <c>:1521</c>).</item>
    /// </list>
    /// <b>It is not a movement gate.</b> Nothing in <c>UnitZombie.as</c> or <c>UnitRaider.as</c> reads
    /// <c>shok</c> to stop walking; the oracle's "pause before reacting" is a different field entirely,
    /// <c>aiSpok</c> (<c>maxSpok = 30</c>, <c>Unit.as:362</c>; <c>UnitZombie.as:623-675</c>), which is
    /// not modelled in the port. The port's <c>EnemyBrain</c> movement pause borrowed this field's name
    /// and its comment claimed AS3 backing it never had — see the note at that call site.</para>
    ///
    /// <para><b>Why extracted.</b> Same reason as <see cref="ContactInvulnerabilityMath"/>: the state
    /// is a bare <c>int</c> on a <c>MonoBehaviour</c>, so while the arithmetic lived inside the brain it
    /// was unassertable offline, and every way to get it wrong is silent. A <c>shok</c> that is set but
    /// never allowed to reach <c>0</c> makes a zombie permanently weak; a floor of <c>4</c> instead of
    /// <c>5</c> means every glancing touch staggers; and the rounding rule (below) shifts the whole
    /// curve by a tick without ever throwing.</para>
    ///
    /// <para><b>Two writers, two different rules — do not unify them.</b> Taking damage
    /// <i>raises</i> <c>shok</c> and can never lower it (<c>if(this.shok &lt; _loc11_) this.shok =
    /// _loc11_</c>, <c>Unit.as:3709-3712</c>), while the alarm <i>assigns</i> it and therefore can lower
    /// it (<c>shok = Math.floor(Math.random() * 15 + 5)</c>, <c>UnitZombie.as:346</c>). A single
    /// "SetShok" that always assigns would let a light hit cancel a heavy one's stagger; a single
    /// "max" would let a zombie carry a stagger into an alarm it should have cleared. Both are encoded
    /// here as separate methods, <see cref="FromDamage"/> and <see cref="AlarmTicks"/>.</para>
    ///
    /// <para><b>The alarm assignment is gated on being caught unawares — this is the whole behaviour.</b>
    /// <c>UnitZombie.alarma()</c> reaches <c>shok = 5..19</c> only inside
    /// <c>sost == 1 &amp;&amp; (aiState &lt;= 1 || aiState == 5)</c> (<c>UnitZombie.as:334-347</c>) —
    /// i.e. while the zombie is idle or walking (<c>aiState</c> 0/1) or buried (<c>aiState == 5</c>).
    /// A zombie that is <i>already</i> chasing (<c>aiState == 3</c>) gets nothing at all from
    /// <c>alarma()</c>. And because <c>Unit.damage()</c> calls <c>alarma()</c> at its <b>end</b>
    /// (<c>Unit.as:3985-3988</c>), after it has already raised <c>shok</c> from the hit size itself
    /// (<c>:3700-3712</c>), the practical rule is:
    /// <list type="bullet">
    /// <item><b>hit an unaware zombie</b> → the assignment runs last and wins → stagger <b>5..19</b>;</item>
    /// <item><b>hit a chasing zombie</b> → <c>alarma()</c> is a no-op, only the raise ran → stagger by
    /// damage, <b>7..30</b> for a 14-damage hit on 50 hp.</item>
    /// </list>
    /// The two rules are encoded separately here precisely because they fire in different situations.</para>
    ///
    /// <para><b>Wiring status — deliberate staging, not an oversight.</b> <see cref="AlarmTicks"/> is
    /// wired: both of the port's alarm sites call it. <see cref="FromDamage"/> and <see cref="Raise"/>
    /// are <b>not wired yet</b>. They are the <c>Unit.damage()</c> half, and wiring them without the
    /// surprise gate above would make things <i>worse</i>, not better: the port's alarm call is
    /// unconditional, so it would overwrite the damage raise on a chasing zombie — giving it 5..19
    /// where the oracle gives 7..30. Both halves are implemented and pinned here so the follow-up is
    /// arithmetic rather than archaeology; see the open task for the wiring.</para>
    /// </summary>
    public static class StaggerMath
    {
        /// <summary>
        /// AS3 <c>Unit.maxShok:int = 30</c> (<c>fe/unit/Unit.as:388</c>) — the ceiling both the
        /// damage-stagger formula clamps to and, in practice, the longest stagger a unit can carry.
        /// </summary>
        /// <remarks>
        /// <b>One writer deliberately exceeds it.</b> <c>UnitRaider.as:637</c> assigns <c>shok = 45</c>
        /// directly, past the ceiling, because it is a hand-authored dramatic beat rather than a
        /// formula result — so this is a clamp on <see cref="FromDamage"/>, <i>not</i> an invariant of
        /// the field. Do not add a setter that clamps every write; it would silently shorten that beat.
        /// </remarks>
        public const int MaxTicks = 30;

        /// <summary>
        /// AS3 <c>UnitZombie.as:346</c> / <c>UnitRaider.as:493</c> — the floor of the alarm assignment,
        /// <c>Math.floor(Math.random() * 15 + 5)</c>.
        /// </summary>
        public const int AlarmMinTicks = 5;

        /// <summary>
        /// The span of the alarm roll: <c>Math.random() * 15</c>, i.e. <c>Math.floor</c> of it is
        /// <c>0..14</c>. Callers draw an <c>int</c> in <c>[0, AlarmRollRange)</c> and pass it to
        /// <see cref="AlarmTicks"/>, which reproduces the oracle's distribution exactly.
        /// </summary>
        public const int AlarmRollRange = 15;

        /// <summary>
        /// AS3 <c>UnitZombie.as:559</c> — <c>shok = 15</c>, assigned when a zombie levitates. Between
        /// the alarm floor and ceiling, and a plain assignment like the alarm, not a raise.
        /// </summary>
        public const int LevitationTicks = 15;

        /// <summary>
        /// AS3 <c>Unit.as:3705</c> — <c>if(param4 || _loc11_ &lt; 5) { _loc11_ = 0; }</c>. A hit worth
        /// less than five ticks of stagger grants <b>none at all</b>, so a swarm of small taps cannot
        /// keep a unit permanently reeling. This is a floor on the <i>result</i>, not on the formula:
        /// a computed 4 becomes 0, not 5.
        /// </summary>
        public const int MinStaggerFromDamage = 5;

        /// <summary>
        /// AS3's <c>shok = Math.floor(Math.random() * 15 + 5)</c> — the alarm/hesitation assignment,
        /// <b>5..19</b> inclusive (<c>UnitZombie.as:346</c>, <c>UnitRaider.as:493</c>).
        /// </summary>
        /// <param name="roll">
        /// An integer draw in <c>[0, <see cref="AlarmRollRange"/>)</c>. Callers should obtain it as
        /// <c>Random.Range(0, AlarmRollRange)</c>, which is the port's exact equivalent of
        /// <c>Math.floor(Math.random() * 15)</c>. A <see cref="float"/>-range draw
        /// (<c>Random.Range(0f, 15f)</c>) is <i>not</i> equivalent — Unity's float overload is
        /// inclusive of its upper bound, so it can return <c>15</c> and produce <c>20</c>.
        /// </param>
        /// <remarks>
        /// <b>This assigns; it does not raise.</b> The oracle writes the field directly, so an alarm
        /// raised while a 30-tick damage stagger is still running <i>shortens</i> it. That looks like a
        /// bug and is the oracle's behaviour; <see cref="FromDamage"/> is the other half and does the
        /// opposite. The out-of-range guard is clamped rather than trusted because a bad draw would
        /// otherwise produce a 20-tick stagger that nothing else in the system can explain.
        /// </remarks>
        public static int AlarmTicks(int roll)
        {
            return AlarmMinTicks + Mathf.Clamp(roll, 0, AlarmRollRange - 1);
        }

        /// <summary>
        /// AS3 <c>Unit.damage()</c> (<c>Unit.as:3700-3712</c>) — the stagger a landed hit grants,
        /// as the value the field should be <i>raised to</i> (or <c>0</c> for "no stagger"):
        /// <code>
        /// _loc11_ = Math.round((Math.random() * 0.8 + 0.2) * this.maxShok * 4 * param1 / this.maxhp);
        /// if(_loc11_ &gt; this.maxShok) { _loc11_ = this.maxShok; }
        /// if(param4 || _loc11_ &lt; 5) { _loc11_ = 0; }
        /// </code>
        /// </summary>
        /// <param name="damage">AS3 <c>param1</c> — the damage <i>after</i> armour, i.e. the amount
        /// actually subtracted from <c>hp</c>.</param>
        /// <param name="maxHealth">AS3 <c>this.maxhp</c>. Guarded against <c>0</c>: a definition with no
        /// health would otherwise divide by zero and produce a non-finite roll.</param>
        /// <param name="roll01">A draw in <c>[0, 1)</c> — AS3 <c>Math.random()</c>.</param>
        /// <param name="suppress">AS3 <c>param4</c> — the damage call's "no stagger" flag (it also
        /// suppresses armour; <c>Unit.as:3611</c>). <c>true</c> forces <c>0</c>.</param>
        /// <remarks>
        /// <para><b>The rounding rule is load-bearing and is <i>not</i> <c>Mathf.RoundToInt</c>.</b>
        /// AS3 <c>Math.round</c> rounds a half <b>up</b> (<c>Math.round(0.5) == 1</c>); .NET's
        /// <c>Math.Round</c>, and therefore <c>Mathf.RoundToInt</c>, default to <i>banker's</i> rounding
        /// and give <c>0</c>. With this formula a half-exact value is reachable — any damage/maxhp
        /// ratio landing on a <c>.5</c> boundary — and the two rules then differ by a full tick.
        /// <see cref="As3Round"/> exists so the difference is asserted rather than assumed.</para>
        ///
        /// <para><b>The curve, for a sanity check on any tuning.</b> Folding <c>maxShok</c> in, the
        /// expression is <c>(roll*0.8 + 0.2) * 120 * damage / maxhp</c>, so with
        /// <c>r = damage/maxHealth</c> it spans <c>24r</c> at <c>roll = 0</c> up to <c>120r</c> as
        /// <c>roll</c> approaches <c>1</c>. Because <see cref="As3Round"/> rounds half <i>up</i>, "any
        /// stagger at all" needs <c>raw ≥ 4.5</c> and full saturation needs <c>raw ≥ 30.5</c>:
        /// <list type="bullet">
        /// <item><c>r ≥ 3.75 %</c> — can stagger, but only on a high roll;</item>
        /// <item><c>r ≥ 18.75 %</c> — always staggers, on every roll (the low roll lands exactly on
        /// <c>4.5 → 5</c>);</item>
        /// <item><c>r ≥ 25.42 %</c> — saturates at 30 on a high roll;</item>
        /// <item><c>r ≥ 127.08 %</c> — saturates on every roll.</item>
        /// </list>
        /// Worked example, because the thresholds are not obvious from the formula: a zombie has 50 hp,
        /// so a 14-damage hit is <c>r = 28 %</c>. That <i>always</i> staggers, for <b>7 to 30 ticks</b>
        /// depending on the roll — not a fixed number, and never zero. This is the oracle's tuning, not
        /// a port choice; the numbers are written down so a future tuning change can be checked against
        /// the curve instead of against a feeling.</para>
        /// </remarks>
        public static int FromDamage(float damage, float maxHealth, float roll01, bool suppress = false)
        {
            if (suppress || damage <= 0f || maxHealth <= 0f)
            {
                return 0;
            }

            float raw = (roll01 * 0.8f + 0.2f) * MaxTicks * 4f * damage / maxHealth;
            int ticks = As3Round(raw);

            if (ticks > MaxTicks)
            {
                ticks = MaxTicks;
            }

            return ticks < MinStaggerFromDamage ? 0 : ticks;
        }

        /// <summary>
        /// AS3 <c>if(this.shok &lt; _loc11_) { this.shok = _loc11_; }</c> (<c>Unit.as:3709-3712</c>) —
        /// the damage path's rule: a new stagger may only <b>extend</b> the current one, never cut it
        /// short. Pair with <see cref="FromDamage"/>, which already returns <c>0</c> for "no stagger".
        /// </summary>
        public static int Raise(int current, int candidate)
        {
            return candidate > current ? candidate : current;
        }

        /// <summary>
        /// AS3 <c>shok &lt;= 0 ? 1 : 0.5</c> (<c>UnitZombie.as:940</c>) — whether a staggering unit's
        /// contact hit is halved. The same test the raider uses to refuse an attack outright
        /// (<see cref="BlocksAttack"/>).
        /// </summary>
        /// <remarks>
        /// <b>Strict, and the boundary is the point.</b> At <c>0</c> the unit hits for full damage —
        /// the state it spends most of its life in. A gate of <c>&gt;= 0</c> would make every unit
        /// permanently weak, and the symptom would be "zombies do half damage forever", which reads
        /// as a damage-tuning problem rather than a timer bug.
        /// </remarks>
        public static bool HalvesOutgoingDamage(int ticks)
        {
            return ticks > 0;
        }

        /// <summary>
        /// AS3 <c>shok &lt;= 0</c> (<c>UnitRaider.as:1486</c>, <c>:1498</c>, <c>:1521</c>) — a
        /// staggering raider is refused every attack path. Same predicate as
        /// <see cref="HalvesOutgoingDamage"/>; named separately because the consequence differs by
        /// family and a future change may want to split them.
        /// </summary>
        public static bool BlocksAttack(int ticks)
        {
            return ticks > 0;
        }

        /// <summary>
        /// AS3 <c>Unit.actions()</c> (<c>Unit.as:3065-3068</c>) — <c>if(this.shok &gt; 0)
        /// { --this.shok; }</c>, clamped at zero for the same reason
        /// <see cref="ContactInvulnerabilityMath.Tick"/> is.
        /// </summary>
        public static int Tick(int ticks)
        {
            return ticks > 0 ? ticks - 1 : 0;
        }

        /// <summary>
        /// AS3 <c>Math.round</c> for the non-negative values this file produces — round half <b>up</b>,
        /// not to even.
        /// </summary>
        /// <remarks>
        /// <c>Mathf.RoundToInt</c> is the obvious choice and is wrong here: it routes to
        /// <c>Math.Round</c> with the default <c>MidpointRounding.ToEven</c>, so <c>1.5 → 2</c> but
        /// <c>2.5 → 2</c>. AS3 gives <c>3</c> for the second. Only exact halves differ, which is why
        /// this is invisible in play and worth a test rather than a comment.
        /// </remarks>
        public static int As3Round(float value)
        {
            return Mathf.FloorToInt(value + 0.5f);
        }
    }
}
