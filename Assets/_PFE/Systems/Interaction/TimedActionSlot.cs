using PFE.Core;

namespace PFE.Systems.Interaction
{
    /// <summary>
    /// Why <see cref="TimedActionSlot{TPayload}.TryBegin"/> did or did not start a hold. Each refusal
    /// is named rather than collapsed into a <c>false</c>, so a caller that gets <c>false</c> can tell
    /// "you are already busy" (expected, ignore) from "this action has no hold" (a wiring mistake —
    /// the caller should have taken the instant path).
    /// </summary>
    public enum TimedActionBeginResult
    {
        /// <summary>The hold started.</summary>
        Begun = 0,

        /// <summary>Refused: a hold is already in flight. AS3 has exactly one <c>actionObj</c>, so a
        /// second begin while one runs is a no-op there too (<c>UnitPlayer.as:1924-1930</c> only
        /// distance-checks an existing <c>actionObj</c>).</summary>
        AlreadyActive = 1,

        /// <summary>Refused: <c>frames &lt;= 0</c>. This is not a zero-length hold — AS3 fires a
        /// zero-time action on the spot (<c>UnitPlayer.as:1985-1988</c>) and never arms a timer, so
        /// the caller must run its instant path instead of asking for a hold.</summary>
        NotATimedAction = 2
    }

    /// <summary>
    /// What one <see cref="TimedActionSlot{TPayload}.Advance"/> call did.
    /// </summary>
    public enum TimedActionTickResult
    {
        /// <summary>Nothing was in flight.</summary>
        Idle = 0,

        /// <summary>Still counting down.</summary>
        Running = 1,

        /// <summary>Reached zero and fired. The caller should run the effect.</summary>
        Completed = 2,

        /// <summary>Abandoned before firing (key released, target out of range, owner disabled).
        /// The caller should undo whatever the hold had begun — AS3 stops the action loop sound on
        /// this path (<c>UnitPlayer.as:1088-1092</c>).</summary>
        Cancelled = 3
    }

    /// <summary>
    /// The result of one tick: the outcome, plus the target the outcome refers to.
    ///
    /// <para><b>Why the payload is handed back rather than left on the slot.</b> On a terminal tick
    /// the caller needs to know <i>what</i> finished — to run the effect on completion, and to unwind
    /// it on cancellation. Leaving it readable as a property would create a state where
    /// <see cref="TimedActionSlot{TPayload}.IsActive"/> is false while a stale target is still
    /// visible, which is exactly how a wrong-object interaction gets written. Handing it out and
    /// clearing it makes the wrong thing unrepresentable.</para>
    ///
    /// <para><see cref="Payload"/> is only meaningful when <see cref="IsTerminal"/>; it is
    /// <c>default</c> otherwise, and <c>default</c> for a reference-type payload is null.</para>
    /// </summary>
    public readonly struct TimedActionTick<TPayload>
    {
        public TimedActionTick(TimedActionTickResult result, TPayload payload)
        {
            Result = result;
            Payload = payload;
        }

        /// <summary>What the tick did.</summary>
        public TimedActionTickResult Result { get; }

        /// <summary>The action's target. Valid only when <see cref="IsTerminal"/>.</summary>
        public TPayload Payload { get; }

        /// <summary>True when this tick ended the hold, one way or the other.</summary>
        public bool IsTerminal =>
            Result == TimedActionTickResult.Completed || Result == TimedActionTickResult.Cancelled;

        public override string ToString()
        {
            return IsTerminal
                ? "TimedActionTick(" + Result + ", payload=" + Payload + ")"
                : "TimedActionTick(" + Result + ")";
        }
    }

    /// <summary>
    /// Holds <b>one</b> timed action at a time: the port of <c>UnitPlayer</c>'s
    /// <c>actionObj</c> + <c>t_action</c> + <c>mt_action</c> trio, including the release-cancel.
    ///
    /// <para><b>The single slot is the AS3 shape, not a simplification.</b> A player has exactly one
    /// <c>actionObj</c> (<c>UnitPlayer.as:67</c>), and <c>actAction()</c> refuses to start a second
    /// while one is live — it only distance-checks the incumbent (<c>:1924-1930</c>). Every door,
    /// terminal, mine, lock, NPC and checkpoint in the game competes for that one field. So this is a
    /// slot, not a scheduler: several <i>sources</i> of a timed action, one <i>active</i> one.</para>
    ///
    /// <para><b>It is not <see cref="PFE.Core.ISimTickable"/> on purpose.</b> It has no tick order of
    /// its own — it must be advanced by whoever owns the player's action, at the order that suits
    /// that owner (for the player, <see cref="PFE.Core.SimTickOrder.Triggers"/>, after movement has
    /// settled). Registering it independently would let it be advanced twice, or at an order that
    /// disagrees with the movement it is gated on.</para>
    ///
    /// <para><b>Policy deliberately left out.</b> What starts a hold, whether walking away cancels it,
    /// and what a completion <i>does</i> are all caller decisions — AS3 spreads those over
    /// <c>actAction()</c>, the distance guard and <c>Interact.act()</c>. This type owns only the
    /// clock and the progress report, which is the part every timed action shares.</para>
    /// </summary>
    /// <typeparam name="TPayload">What the hold is acting on. Passed to
    /// <see cref="TryBegin"/> and handed back on the terminal tick.</typeparam>
    public sealed class TimedActionSlot<TPayload>
    {
        private readonly ActionTimer _timer = new ActionTimer();
        private readonly IActionProgressView _view;

        private TPayload _payload;

        /// <param name="view">
        /// Where progress is reported. Null is legal and means "draw nothing" — the slot substitutes
        /// <see cref="NullActionProgressView"/>, so no call site needs a null check.
        /// </param>
        public TimedActionSlot(IActionProgressView view = null)
        {
            _view = view ?? NullActionProgressView.Instance;
        }

        /// <summary>True while a hold is counting down.</summary>
        public bool IsActive => _timer.IsRunning;

        /// <summary>Fraction elapsed, 0&#8594;1, for a UI bar. Same value last pushed to the view.</summary>
        public float Progress => _timer.Progress;

        /// <summary>Frames left on the countdown.</summary>
        public int RemainingFrames => _timer.RemainingFrames;

        /// <summary>The authored duration of the hold in flight, in frames.</summary>
        public int TotalFrames => _timer.TotalFrames;

        /// <summary>
        /// The target of the hold in flight, for callers that need to re-check it every tick (is it
        /// still in range? still alive?) before advancing.
        ///
        /// <para><b>Valid only while <see cref="IsActive"/>.</b> It reads <c>default</c> before the
        /// first <see cref="TryBegin"/> and is cleared the moment a terminal tick hands the payload
        /// out through <see cref="TimedActionTick{TPayload}"/> — so it can never be the source of an
        /// effect, only of a validity check. Running an effect off this property instead of the tick
        /// payload is the mistake this design is shaped to prevent.</para>
        /// </summary>
        public TPayload ActivePayload => _payload;

        /// <summary>
        /// Starts a hold of <paramref name="frames"/> on <paramref name="payload"/>, and shows the
        /// progress bar at 0.
        ///
        /// <para>The view is told immediately rather than on the first tick, so the bar appears on the
        /// press instead of one tick (33 ms) later.</para>
        /// </summary>
        public TimedActionBeginResult TryBegin(int frames, TPayload payload)
        {
            if (frames <= 0)
            {
                return TimedActionBeginResult.NotATimedAction;
            }

            if (_timer.IsRunning)
            {
                return TimedActionBeginResult.AlreadyActive;
            }

            _payload = payload;
            _timer.Start(frames);
            _view.Show(_timer.Progress);
            return TimedActionBeginResult.Begun;
        }

        /// <summary>
        /// Advances the hold by one tick and reports progress to the view.
        ///
        /// <para>On <see cref="TimedActionTickResult.Completed"/> the bar is shown full and then
        /// hidden, so a bar driven by a scale animation reaches 1 rather than snapping away at 0.9.
        /// The payload is cleared, which is what makes a repeated terminal read impossible.</para>
        /// </summary>
        public TimedActionTick<TPayload> Advance()
        {
            if (!_timer.IsRunning)
            {
                return new TimedActionTick<TPayload>(TimedActionTickResult.Idle, default);
            }

            ActionTimerState state = _timer.Advance();
            if (state == ActionTimerState.Completed)
            {
                TPayload payload = _payload;
                _payload = default;

                _view.Show(1f);
                _view.Hide();
                return new TimedActionTick<TPayload>(TimedActionTickResult.Completed, payload);
            }

            _view.Show(_timer.Progress);
            return new TimedActionTick<TPayload>(TimedActionTickResult.Running, default);
        }

        /// <summary>
        /// Abandons the hold in flight — the port of the released-key path,
        /// <c>UnitPlayer.as:2131-2135</c>.
        ///
        /// <para>Safe to call when nothing is running (returns <see cref="TimedActionTickResult.Idle"/>
        /// and touches no view), because the natural call sites are "on key up" and "on disable",
        /// neither of which can know whether a hold was live.</para>
        /// </summary>
        public TimedActionTick<TPayload> Cancel()
        {
            if (!_timer.IsRunning)
            {
                return new TimedActionTick<TPayload>(TimedActionTickResult.Idle, default);
            }

            _timer.Cancel();

            TPayload payload = _payload;
            _payload = default;

            _view.Hide();
            return new TimedActionTick<TPayload>(TimedActionTickResult.Cancelled, payload);
        }

        /// <summary>
        /// Cancels any hold and hides the bar, discarding the payload. For teardown — a disabled or
        /// destroyed owner must not leave a bar on screen or a half-counted action behind.
        /// </summary>
        public void Reset()
        {
            _timer.Reset();
            _payload = default;
            _view.Hide();
        }
    }
}
