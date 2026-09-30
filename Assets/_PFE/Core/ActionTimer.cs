namespace PFE.Core
{
    /// <summary>
    /// Lifecycle of an <see cref="ActionTimer"/>.
    ///
    /// <para><see cref="Idle"/> and <see cref="Cancelled"/> are both "not running" and both report
    /// <see cref="ActionTimer.Progress"/> 0, but they are distinct: Idle has never started, Cancelled
    /// was abandoned part-way. A caller that only cares whether work is in flight tests
    /// <see cref="ActionTimer.IsRunning"/>; one that reports to a log wants the difference.</para>
    /// </summary>
    public enum ActionTimerState
    {
        /// <summary>Never started, or reset. Not running.</summary>
        Idle = 0,

        /// <summary>Counting down. <see cref="ActionTimer.RemainingFrames"/> may already read 0 —
        /// see the off-by-one note on <see cref="ActionTimer.Advance"/>.</summary>
        Running = 1,

        /// <summary>Reached zero and fired. Terminal until <see cref="ActionTimer.Start"/>.</summary>
        Completed = 2,

        /// <summary>Abandoned before firing (the player released the key). Terminal until
        /// <see cref="ActionTimer.Start"/>.</summary>
        Cancelled = 3
    }

    /// <summary>
    /// A frame-count countdown: the one clock behind every "hold this for N frames" action, and the
    /// source of the 0&#8594;1 progress a UI bar renders.
    ///
    /// <para><b>Why this exists.</b> AS3 spreads the same countdown over four places — the value
    /// (<c>Interact.t_action</c>, <c>Interact.as:102</c>), its authoring (<c>@time</c>,
    /// <c>Interact.as:310</c>), its decrement (<c>UnitPlayer.t_action</c>,
    /// <c>UnitPlayer.as:1077-1079</c>) and its scale (<c>UnitPlayer.mt_action</c>,
    /// <c>UnitPlayer.as:71</c>/<c>:1990</c>) — and then re-implements it per feature (mining and
    /// lockpicking compose <c>t_action + getLockPickTime(…)</c> at <c>:1953</c>/<c>:1976</c>, traps
    /// and NPCs assign their own at <c>UnitTrap.as:42</c>/<c>NPC.as:106</c>). This type is that
    /// countdown with the policy removed, so a new timed action supplies only a duration and an
    /// effect.</para>
    ///
    /// <para><b>Frames, not seconds.</b> <see cref="TotalFrames"/> is an AS3 frame count and the unit
    /// is the tick — see the "frame counters stay frames" rule on <see cref="ISimTickable"/>. Nothing
    /// here reads <c>Time.deltaTime</c> or the tick rate, so the type is usable from a tick, from a
    /// test, and from a headless context alike. <see cref="SimClock.FramesPerSecond"/> is only for
    /// converting a duration for <i>display</i>.</para>
    ///
    /// <para><b>A class, not a struct.</b> The state mutates in place, and a mutable struct reached
    /// through a property would silently advance a copy. The cost is one small allocation per active
    /// action — at most one per player.</para>
    /// </summary>
    public sealed class ActionTimer
    {
        private int _totalFrames;
        private int _remainingFrames;
        private ActionTimerState _state = ActionTimerState.Idle;

        /// <summary>The authored duration, in frames. This is the progress-bar denominator — AS3's
        /// <c>mt_action</c>, clamped to at least 1 at <c>UnitPlayer.as:1991-1993</c> so a divide can
        /// never be by zero.</summary>
        public int TotalFrames => _totalFrames;

        /// <summary>Frames left — AS3's <c>t_action</c>. Meaningful only while
        /// <see cref="IsRunning"/>; reads 0 in every other state.</summary>
        public int RemainingFrames => _remainingFrames;

        /// <summary>Current lifecycle position.</summary>
        public ActionTimerState State => _state;

        /// <summary>True while the countdown is in flight.</summary>
        public bool IsRunning => _state == ActionTimerState.Running;

        /// <summary>
        /// Fraction elapsed, 0&#8594;1 — AS3's <c>perc = (mt_action - t_action) / mt_action</c>
        /// (<c>GUI.as:1288</c>), the value the hold bar is scaled by.
        ///
        /// <para>Reads 0 in <see cref="ActionTimerState.Idle"/> and
        /// <see cref="ActionTimerState.Cancelled"/>, and exactly 1 in
        /// <see cref="ActionTimerState.Completed"/>. While running it can also reach 1 one tick
        /// <i>before</i> completion — that is AS3's behaviour, not a rounding artefact; see
        /// <see cref="Advance"/>.</para>
        /// </summary>
        public float Progress
        {
            get
            {
                switch (_state)
                {
                    case ActionTimerState.Completed:
                        return 1f;

                    case ActionTimerState.Running:
                        // Guard kept even though Start() clamps: a bar must never divide by zero, and
                        // this property is the only place a consumer could observe it.
                        return _totalFrames <= 0
                            ? 1f
                            : Clamp01(1f - _remainingFrames / (float)_totalFrames);

                    default:
                        return 0f;
                }
            }
        }

        /// <summary>
        /// Begins a countdown of <paramref name="frames"/>.
        ///
        /// <para>A non-positive count is <b>not</b> a zero-length hold — it means the action has no
        /// hold at all, and AS3 fires it on the spot (<c>UnitPlayer.as:1985-1988</c> sets
        /// <c>is_act = true</c> without ever touching <c>t_action</c>). So it lands directly in
        /// <see cref="ActionTimerState.Completed"/> with <see cref="Progress"/> 1, and
        /// <see cref="Advance"/> is never needed. A caller decides hold-versus-instant by testing
        /// <c>frames &gt; 0</c>; this method agrees with that test rather than throwing.</para>
        ///
        /// <para>Restarts unconditionally, including from a terminal state — re-arming is a normal
        /// thing to do and the caller owns the "may I start?" question.</para>
        /// </summary>
        /// <returns>The state entered.</returns>
        public ActionTimerState Start(int frames)
        {
            if (frames <= 0)
            {
                _totalFrames = 0;
                _remainingFrames = 0;
                _state = ActionTimerState.Completed;
                return _state;
            }

            _totalFrames = frames;
            _remainingFrames = frames;
            _state = ActionTimerState.Running;
            return _state;
        }

        /// <summary>
        /// Advances the countdown by exactly one tick.
        ///
        /// <para><b>The off-by-one is reproduced deliberately.</b> AS3's guard is
        /// <c>if (t_action &gt; 0) --t_action; else fire</c> (<c>UnitPlayer.as:1077-1085</c>): the
        /// completing tick is the one where the counter <i>already</i> reads zero, not the one that
        /// brings it there. An authored <c>time='10'</c> therefore fires on the <b>11th</b> tick.
        /// The counter is a countdown value, not a tick budget.</para>
        ///
        /// <para>This is also why <see cref="Progress"/> can read 1 while still running: at
        /// <c>RemainingFrames == 0</c> the bar is full (<c>(10 - 0) / 10</c>) and the effect lands
        /// one tick later. The bar fills, then the door opens — as in the original.</para>
        ///
        /// <para>Terminal states are absorbing; calling this on an
        /// <see cref="ActionTimerState.Idle"/> timer is a no-op rather than an error, because a
        /// caller that advances every tick should not have to ask first.</para>
        /// </summary>
        /// <returns>The state after the tick.</returns>
        public ActionTimerState Advance()
        {
            if (_state != ActionTimerState.Running)
            {
                return _state;
            }

            if (_remainingFrames > 0)
            {
                _remainingFrames--;
                return _state;
            }

            _state = ActionTimerState.Completed;
            return _state;
        }

        /// <summary>
        /// Abandons a running countdown — the port of the release path at
        /// <c>UnitPlayer.as:2131-2135</c>, where a released action key nulls <c>actionObj</c> and
        /// progress stops where it stood. AS3 leaves <c>t_action</c> at its partial value rather than
        /// zeroing it; <see cref="RemainingFrames"/> is zeroed here instead, because an orphaned
        /// non-zero counter reads as "still in flight" to anyone who did not see the cancel.
        ///
        /// <para>Cancelling an <see cref="ActionTimerState.Idle"/> timer is a no-op; cancelling a
        /// <see cref="ActionTimerState.Completed"/> one does <b>not</b> un-complete it, since the
        /// effect has already landed.</para>
        /// </summary>
        /// <returns>The state after the call.</returns>
        public ActionTimerState Cancel()
        {
            if (_state != ActionTimerState.Running)
            {
                return _state;
            }

            _remainingFrames = 0;
            _state = ActionTimerState.Cancelled;
            return _state;
        }

        /// <summary>Returns to <see cref="ActionTimerState.Idle"/> and forgets the duration.</summary>
        public void Reset()
        {
            _totalFrames = 0;
            _remainingFrames = 0;
            _state = ActionTimerState.Idle;
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        public override string ToString()
        {
            return _state == ActionTimerState.Running
                ? "ActionTimer(" + _remainingFrames + "/" + _totalFrames + ", " +
                  Progress.ToString("0.###") + ")"
                : "ActionTimer(" + _state + ")";
        }
    }
}
