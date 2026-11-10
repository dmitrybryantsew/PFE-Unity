using System;

namespace PFE.Core.Input
{
    /// <summary>
    /// Turns a <b>per-render-frame</b> intent stream into a <b>one-command-per-sim-tick</b> replay tape.
    ///
    /// <para><b>Why this exists (the 2026-11-06 Space-does-not-jump defect).</b>
    /// <c>PlayerController.Update()</c> runs every render frame; <c>SimLoop</c> advances its tick at
    /// 30 Hz. At 60–144 fps several frames therefore share one <c>commandTick</c>. The Phase 2.3 code
    /// enqueued a command <i>per frame</i> and then consumed it with
    /// <see cref="PlayerCommandBuffer.TryGetForTick"/>, which scans <b>oldest-first</b>. On the second
    /// frame of a tick window the consumer got the first frame's command — so a
    /// <c>WasPressedThisFrame</c> jump edge produced on that second frame never reached
    /// <c>SetIntent</c>, the jump buffer was never armed, and Space silently did nothing.</para>
    ///
    /// <para><b>The two halves of the fix, deliberately separate.</b> Local intent must consume the
    /// command it <i>just built</i> (that is a <c>PlayerController</c> wiring fact, pinned by a source
    /// lint). This class owns only the other half: the <b>tape</b> a host would replay or a client
    /// would resend. A tick-aligned consumer must see exactly one command for tick N, and that command
    /// must carry <i>every</i> edge produced during N — otherwise a host replaying the tape loses the
    /// same jump the local player just made.</para>
    ///
    /// <para><b>Folding rule.</b> Edges (<c>JumpPressed</c>, <c>DashPressed</c>,
    /// <c>DropThroughPressed</c>) are OR'd across the tick because they are one-frame signals: losing
    /// one loses the action. Levels (<c>JumpHeld</c>, <c>RunHeld</c>) and the analogue intent
    /// (move axes, aim angle) come from the <b>newest</b> frame, because they describe a state that is
    /// still true at the end of the tick. <see cref="Sequence"/> stays the first frame's — the folded
    /// entry is one wire message, named by when the tick's command was first published.</para>
    ///
    /// <para><b>Pure C#, no Unity.</b> The whole policy is executable in the offline harness. That
    /// matters here: the original defect lived in a <c>MonoBehaviour</c>, which no offline fixture can
    /// instantiate. Moving the <i>policy</i> out of the component leaves only a two-line wiring fact
    /// behind for a source lint to pin.</para>
    /// </summary>
    public sealed class PlayerCommandTape
    {
        /// <summary>One-frame signals: an edge on ANY frame of the tick must survive the fold.</summary>
        public const PlayerCommand.Button EdgeButtons =
            PlayerCommand.Button.JumpPressed |
            PlayerCommand.Button.DashPressed |
            PlayerCommand.Button.DropThroughPressed;

        /// <summary>Held signals: the newest frame of the tick is authoritative.</summary>
        public const PlayerCommand.Button LevelButtons =
            PlayerCommand.Button.JumpHeld |
            PlayerCommand.Button.RunHeld;

        private readonly PlayerCommandBuffer _buffer;
        private PlayerCommand _pending;
        private bool _hasPending;

        public PlayerCommandTape(int capacity)
            : this(new PlayerCommandBuffer(capacity))
        {
        }

        public PlayerCommandTape(PlayerCommandBuffer buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }

        /// <summary>The redundant last-N window. <see cref="PlayerCommandTape"/> owns it exclusively.</summary>
        public PlayerCommandBuffer Buffer => _buffer;

        /// <summary>
        /// Folds one frame's intent into the tape and returns the tick's cumulative command. When the
        /// frame starts a new tick, its command is appended; when it shares the pending tick, the
        /// buffered entry for that tick is <i>replaced in place</i> — never appended a second time.
        /// </summary>
        public PlayerCommand Push(in PlayerCommand frame)
        {
            if (_hasPending
                && _pending.TickIndex == frame.TickIndex
                && _buffer.TryGetLatest(out PlayerCommand tail)
                && tail.TickIndex == frame.TickIndex)
            {
                _pending = Fold(_pending, frame);
                _buffer.ReplaceLast(_pending);
            }
            else
            {
                // A new tick — or someone cleared/acked the pending entry out from under us. Either
                // way the tape needs one entry for this tick, carrying the intent seen so far.
                _pending = frame;
                _hasPending = true;
                _buffer.Enqueue(_pending);
            }

            return _pending;
        }

        /// <summary>
        /// Pure fold of two frames of the same tick. Kept public and static so fixtures can exercise
        /// the exact rule without constructing a tape or a Unity object.
        /// </summary>
        public static PlayerCommand Fold(in PlayerCommand accumulated, in PlayerCommand frame)
        {
            PlayerCommand merged = accumulated;
            merged.Buttons = (accumulated.Buttons & EdgeButtons)
                             | (frame.Buttons & EdgeButtons)
                             | (frame.Buttons & LevelButtons);
            merged.MoveX = frame.MoveX;
            merged.MoveY = frame.MoveY;
            merged.AimAngle = frame.AimAngle;
            // TickIndex, SlotIndex and Sequence deliberately stay the accumulated (first) frame's:
            // a folded tick is ONE wire message, not a second one.
            return merged;
        }
    }
}
