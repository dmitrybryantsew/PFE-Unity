using System;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// The run-wide "are enemies thinking" switch — AS3 <c>World.w.enemyAct</c>
    /// (<c>World.as:170</c>), flipped by the console verb <c>enemy</c> (<c>Consol.as:478-486</c>).
    ///
    /// <para><b>What the oracle actually does with it.</b> The value is read in 63 places across 29 unit
    /// classes, but every read is one of four shapes and every one of them sits <b>inside
    /// <c>control()</c></b>:</para>
    /// <list type="bullet">
    /// <item><description><c>if(World.w.enemyAct &lt;= 0) { …eye position…; return; }</c> — 26 sites, the
    /// freeze.</description></item>
    /// <item><description><c>if(enemyAct &gt; 1 &amp;&amp; aiTCh % 10 == 1 …)</c> — 14 sites.</description></item>
    /// <item><description><c>if(enemyAct &gt;= 2 …)</c> / <c>&gt;= 3 …)</c> / <c>&gt; 2 …)</c> — 10 sites.</description></item>
    /// <item><description><c>if(enemyAct == 3)</c> — 1 site, the console writer itself.</description></item>
    /// </list>
    ///
    /// <para><b>Why this is modelled as a level and not as a boolean.</b> The console is the value's only
    /// writer and it toggles <c>3</c> ↔ <c>0</c> (<c>Consol.as:478-486</c>), so <c>1</c> and <c>2</c> are
    /// unreachable and every threshold above collapses: <c>&gt; 1</c>, <c>&gt;= 2</c>, <c>&gt; 2</c> and
    /// <c>&gt;= 3</c> are all true at 3 and all false at 0. A port that stored a <c>bool</c> would be
    /// right about every state the game can reach — and would silently stop being right the moment a
    /// second writer appeared. Keeping the integer keeps the thresholds expressible at the read sites
    /// (<see cref="RunsCadence"/> is the <c>&gt; 1</c> one, which is the shape 14 of the 26 cadence sites
    /// use), and the class doc below records the collapse so nobody re-derives it.</para>
    ///
    /// <para><b>What a freeze does and does not stop.</b> This is the part that is easy to get wrong,
    /// because the gate is inside <c>control()</c> and the unit's per-frame step calls
    /// <c>control()</c> in the middle of a longer sequence (<c>Unit.as:1806-1840</c>):</para>
    /// <code>
    /// this.forces();      // physics          ─┐
    /// this.control();     // AI — GATED HERE  ─┤ the gate returns from control() only
    /// this.run();         // movement         ─┤
    /// this.checkWater();  //                  ─┤
    /// this.actions();     // weapons          ─┤
    /// this.setVisPos();   //                  ─┤
    /// this.animate();     // drawing          ─┘
    /// </code>
    /// <para>So a frozen enemy <b>still falls, still slides on momentum it already had, still animates
    /// and still draws</b>. What stops is the whole decision half: <c>aiLaz</c>/<c>aiJump</c>/<c>aiTCh</c>
    /// stop decrementing (they are the lines immediately after the gate, <c>UnitRaider.as:686-697</c>),
    /// <c>findCel()</c> never runs so no target is ever acquired (<c>UnitRaider.as:796</c>, i.e. after
    /// the gate), and no state branch fires. The gate does <b>not</b> clear an existing target, so an
    /// enemy that had already seen the player keeps <c>celUnit</c> — it simply stops acting on it and
    /// stops re-checking whether it still should.</para>
    ///
    /// <para><b>The eye position is maintained, not frozen.</b> 20 of the 26 freeze sites do
    /// <c>celY = Y - scY; celX = X + scX * storona * 2;</c> before returning, so the aim point keeps
    /// tracking the unit's facing while the AI is off. Un-freezing therefore resumes with a correct eye
    /// position rather than the one the unit had when it was frozen.</para>
    /// </summary>
    public static class EnemyActivity
    {
        /// <summary>AS3 <c>World.w.enemyAct = 3</c> (<c>World.as:170</c>) — the default, and the value the console writes to un-freeze.</summary>
        public const int Default = 3;

        /// <summary>The frozen value — the console's other write (<c>Consol.as:480-482</c>).</summary>
        public const int Frozen = 0;

        /// <summary>The fully-active value — the console's un-freeze write (<c>Consol.as:484</c>).</summary>
        public const int Full = 3;

        /// <summary>
        /// The threshold 14 of the cadence sites use: <c>enemyAct &gt; 1 &amp;&amp; aiTCh % 10 == 1</c>
        /// (<c>EnemyAwarenessMath</c> records the same shape for <c>aiSpok</c>). True at
        /// <see cref="Full"/>, false at <see cref="Frozen"/>.
        /// </summary>
        public const int CadenceFloor = 1;

        /// <summary>
        /// The highest value any shipped code can write. <c>1</c> and <c>2</c> are unreachable — the
        /// console's only writes are <see cref="Frozen"/> and <see cref="Full"/> — which is why
        /// <see cref="RunsCadence"/> is equivalent to <see cref="IsActive"/> for every reachable state.
        /// </summary>
        public const int HighestReachable = Full;

        private static int _value = Default;

        /// <summary>
        /// The current level. <see cref="Default"/> until something writes it.
        ///
        /// <para><b>Reset per run.</b> AS3's <c>World.w</c> is constructed fresh for every run, so
        /// <c>enemyAct</c> returns to 3; a C# static outlives the run. Call <see cref="Reset"/> from the
        /// world build, or a debug freeze survives into the next session.</para>
        /// </summary>
        public static int Value
        {
            get => _value;
            private set => _value = value;
        }

        /// <summary>AS3's <c>enemyAct &lt;= 0</c> — the 26 freeze sites.</summary>
        public static bool IsFrozen => Value <= 0;

        /// <summary>The complement of <see cref="IsFrozen"/>, named for the reads that test the positive.</summary>
        public static bool IsActive => Value > 0;

        /// <summary>
        /// AS3's <c>enemyAct &gt; 1</c> — the cadence threshold. Equivalent to
        /// <see cref="IsActive"/> at every reachable value; separate because the oracle writes it as a
        /// different comparison and a future writer could put the value between the two.
        /// </summary>
        public static bool RunsCadence => Value > CadenceFloor;

        /// <summary>
        /// AS3's console verb <c>enemy</c> (<c>Consol.as:478-486</c>) — flip between
        /// <see cref="Full"/> and <see cref="Frozen"/>.
        ///
        /// <para><b>The comparison is against 3, not against "not frozen".</b> The oracle is
        /// <c>if(enemyAct == 3) enemyAct = 0; else enemyAct = 3;</c>, so a value that is neither —
        /// unreachable through the console, but reachable if anything else ever writes the field — would
        /// be sent <i>to</i> 3 rather than toggled. Reproduced rather than "fixed".</para>
        /// </summary>
        /// <returns>The new value.</returns>
        public static int Toggle()
        {
            Value = Value == Full ? Frozen : Full;
            return Value;
        }

        /// <summary>
        /// Write the level directly. No clamp: the oracle's field is a plain <c>int</c> and nothing in
        /// AS3 validates it, so inventing a range here would be a divergence in the other direction.
        /// </summary>
        public static void Set(int level)
        {
            Value = level;
        }

        /// <summary>
        /// Return to <see cref="Default"/>, as a fresh <c>World</c> would. Call once per run.
        /// </summary>
        public static void Reset()
        {
            Value = Default;
        }
    }
}
