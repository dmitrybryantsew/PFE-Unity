namespace PFE.Entities.Enemies
{
    /// <summary>
    /// Shared high-level behavioral states for enemy AI brains.
    ///
    /// <para><b>Values 0-3 and 4-6 are AS3's <c>aiState</c> integers and mean what AS3 means by them;
    /// 7 and 9 are the port's, and the split is deliberate.</b> In <c>UnitZombie.as</c>: <c>4</c> is the
    /// super wind-up (<c>pre</c>), <c>5</c> is <b>buried</b>, <c>6</c> is <b>digging out</b>, <c>7</c> is
    /// the <b>super attack</b>. The port keeps 4/5/6 as-is, uses <see cref="Attack"/> (7) for its own
    /// stop-and-swing construct that AS3 does not have, and puts AS3's 7 at <see cref="SuperAttack"/> (9).
    /// "Shock" is the separate <c>shok</c> field, not a state, and death is <c>sost</c>, also not a
    /// state.</para>
    /// </summary>
    public enum EnemyAIState
    {
        /// <summary>
        /// AS3 <c>aiState 0</c>. Standing in place, playing the resting animation.
        /// </summary>
        Idle = 0,

        /// <summary>
        /// AS3 <c>aiState 1</c>. Ambient walking or patrolling back and forth.
        /// </summary>
        Patrol = 1,

        /// <summary>
        /// AS3 <c>aiState 2</c>. Moving toward the last known position or a heard sound.
        /// </summary>
        Alert = 2,

        /// <summary>
        /// AS3 <c>aiState 3</c>. In combat, pursuing and closing distance.
        /// </summary>
        CombatChase = 3,

        /// <summary>
        /// AS3 <c>aiState 4</c> — the super-attack wind-up, which plays the <c>pre</c> row
        /// (<c>UnitZombie.as:256-260</c>) for <c>tPrepSuper</c> ticks. Reached from
        /// <c>ZombieBrain</c>'s <c>aiZlo</c> trigger; the tick that takes the timer to zero calls
        /// <c>superSila()</c> and promotes to <see cref="SuperAttack"/>.
        /// </summary>
        PrepareSuper = 4,

        /// <summary>
        /// AS3 <c>aiState 5</c> — <b>buried</b>. The unit is hidden inside the floor waiting in ambush:
        /// <c>zakop()</c> (<c>UnitZombie.as:414-445</c>) sets <c>invis</c>, <c>fixed</c> and
        /// <c>overLook</c>, and <c>animate()</c> answers <c>aiState == 5</c> by setting
        /// <c>vis.visible = false</c> (<c>:265-268</c>). This member previously claimed to be "shock /
        /// hesitation", which is the unrelated <c>shok</c> field.
        /// </summary>
        Buried = 5,

        /// <summary>
        /// AS3 <c>aiState 6</c> — <b>digging out</b>. <c>animate()</c> answers it with
        /// <c>animState = "dig"</c> (<c>UnitZombie.as:269-272</c>), and after <c>aiTCh</c> expires
        /// <c>control()</c> calls <c>vykop()</c> and lands on <c>aiState 3</c> (<c>:590-594</c>). This
        /// member previously claimed to be "evade", which AS3 does not have at 6.
        /// </summary>
        Digging = 6,

        /// <summary>
        /// The port's stop-and-swing state: halt, swing, resume.
        ///
        /// <para><b>This is a port-only construct and its value 7 collides with AS3's 7, which is the
        /// super attack.</b> The two are not the same thing and must not be conflated — a mapping that
        /// treats this as AS3's 7 would play <c>super</c> for a melee swing. AS3's contact attackers
        /// have no attack state at all; they damage whatever their box overlaps while still running.
        /// An archetype opts in by returning <c>true</c> from <c>StopsToAttack</c>.</para>
        /// </summary>
        Attack = 7,

        /// <summary>
        /// AS3 <c>aiState 7</c> — the <b>super attack</b>, which plays the <c>super</c> row
        /// (<c>UnitZombie.as:261-264</c>).
        ///
        /// <para><b>Why 9 and not 7.</b> 7 is already taken by <see cref="Attack"/>, the port's
        /// stop-and-swing construct, which AS3 does not have at all. The two cannot share a value: a
        /// shared code path that mapped <see cref="Attack"/> to the <c>super</c> row would play the
        /// empowered animation for an ordinary melee swing, and the zombie is exactly the family where
        /// that would show — it is the only one that authors both. Renumbering <see cref="Attack"/>
        /// instead would break every serialized reference to it.</para>
        ///
        /// <para>The state is entered only by <c>ZombieBrain</c> today, from the wind-up
        /// (<see cref="PrepareSuper"/>), and lasts <c>tSuper</c> ticks before returning to
        /// <see cref="CombatChase"/> with a fresh 50..99-tick timer.</para>
        /// </summary>
        SuperAttack = 9,

        /// <summary>
        /// Dying or deceased. Simulation disabled or waiting for despawn/corpse conversion.
        /// </summary>
        Dead = 8
    }
}
