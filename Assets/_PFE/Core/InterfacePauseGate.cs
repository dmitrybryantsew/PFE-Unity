using System.Collections.Generic;

namespace PFE.Core
{
    /// <summary>
    /// Whether the world simulation is currently suspended because an interface is open — the port of
    /// the <c>allStat</c> mode from <c>World.step()</c>.
    ///
    /// <para><b>What the Oracle actually does.</b> <c>World.as:1385</c> sets</para>
    /// <code>this.allStat = this.pip.active || this.sats.active || this.stand.active || this.gui.guiPause ? 2 : 1;</code>
    /// <para>and the room step is guarded by <c>allStat == 1</c>: <c>World.as:1257</c> opens
    /// <c>if(this.allStat == 1 &amp;&amp; !this.onPause)</c> and <c>World.as:1270</c> calls
    /// <c>this.land.step()</c> inside it. So when the PipBuck is open the room — and every unit,
    /// projectile and object in it — <b>stops</b>, while the interface itself keeps stepping:
    /// <c>gui.step()</c>, <c>pip.step()</c> and <c>sats.step()</c> live under the looser
    /// <c>allStat &gt;= 1</c> at <c>World.as:1315-1320</c>.</para>
    ///
    /// <para><b>Why a holder set and not a bool.</b> Four separate things can hold the world paused,
    /// and the Oracle ORs them. A single <c>bool IsPaused</c> would let the PipBuck closing un-pause a
    /// world that S.A.T.S. still had frozen — a bug that only appears with two interfaces open at
    /// once, which is exactly the case a naive test never reaches. Holders are reference-compared, so
    /// each caller passes itself.</para>
    ///
    /// <para><b>What is NOT paused.</b> <see cref="SimTickOrder.ViewAligned"/> tickables keep running.
    /// That is the port's expression of "the interface still steps": the PipBuck's own page animation
    /// and its live status panel are view-aligned, so they must not be caught by the world gate. The
    /// split is structural rather than a special case — see <c>SimLoop.StepOnce</c>.</para>
    /// </summary>
    public sealed class InterfacePauseGate
    {
        private readonly HashSet<object> _holders = new HashSet<object>();

        /// <summary>True while at least one interface holds the world suspended.</summary>
        public bool IsWorldPaused => _holders.Count > 0;

        /// <summary>How many interfaces currently hold it. For diagnostics and tests.</summary>
        public int HolderCount => _holders.Count;

        /// <summary>
        /// Suspends the world on behalf of <paramref name="holder"/>. Idempotent — holding twice is
        /// one hold, so a repeated <c>onoff(0)</c> cannot leak a count.
        /// </summary>
        public void Acquire(object holder)
        {
            if (holder == null) return;
            _holders.Add(holder);
        }

        /// <summary>
        /// Releases <paramref name="holder"/>'s hold. The world resumes only when the last holder lets
        /// go. Releasing a holder that was never acquired is a no-op, not an error — the Oracle's
        /// <c>onoff(-1)</c> can be called on a device that was never opened (<c>Stand.as:595</c>,
        /// <c>Game.as:577</c> both do exactly that).
        /// </summary>
        public void Release(object holder)
        {
            if (holder == null) return;
            _holders.Remove(holder);
        }

        /// <summary>Drops every hold. For a session teardown or a test arrange.</summary>
        public void ReleaseAll()
        {
            _holders.Clear();
        }
    }
}
