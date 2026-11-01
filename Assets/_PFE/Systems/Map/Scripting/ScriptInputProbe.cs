using PFE.Core;
using PFE.Core.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// The <c>wait</c> latch — the port of AS3 <c>World.w.ctr.keyPressed</c> / <c>keyPressed2</c>
    /// (<c>Ctr.as:231</c>, <c>:233</c>, set at <c>:551</c> and <c>:586</c>).
    ///
    /// <para><b>Why the device is read here and not in the runner.</b> <see cref="ISimTickable"/>'s
    /// determinism contract forbids a tick from reading <c>Input</c> — the tick index is the only time
    /// source, and input is gathered once on the frame boundary into a snapshot
    /// (<c>ISimTickable.cs:10-29</c>). So this type has two halves: <see cref="Poll"/> runs on the frame
    /// and reads the device into latches, and the <see cref="IScriptInputProbe"/> members the runner sees
    /// only ever read those latches. A probe that called <c>IsPressed()</c> from inside the tick would
    /// work today and break the moment a second peer exists.</para>
    ///
    /// <para><b>Both latches are PRESS EDGES, and this is the bug that was here.</b> <c>keyPressed</c> is
    /// assigned only inside <c>Ctr.onMouseDown1</c> (<c>Ctr.as:551</c>) and
    /// <c>Ctr.onRightMouseDown1</c> (<c>:586</c>) — event handlers, which run once per physical press.
    /// Nothing re-asserts it while the button stays down, and <c>Script.step</c> clears it after
    /// consuming it (<c>Script.as:183</c>) and at the top of every command (<c>:215</c>). It therefore
    /// means "a press happened since the last clear", i.e. <b>one press, one line</b>.</para>
    ///
    /// <para>The port used to sample <c>InputAction.IsPressed()</c> here — a <i>level</i>. Because the
    /// runner steps at 30 Hz, holding the interact key then walked the monologue at thirty lines a
    /// second, which is exactly what "holding E skips too quickly" looked like on screen. Reading a level
    /// and calling it <c>keyPressed</c> is the same mistake as reading <c>@t</c> per step instead of
    /// once: the oracle's name for a value is not the oracle's meaning for it.</para>
    ///
    /// <para><b>The two latches are not two names for one thing.</b> <c>keyPressed</c> advances a
    /// dialogue one line; <c>keyPressed2</c> makes <c>Script.step</c> set <c>dial_n = 10000</c> — run
    /// past the last line and release the script immediately (<c>Script.as:156-159</c>). A port that
    /// mapped both to the action key would either skip every dialogue or never skip one. Note that
    /// <c>onRightMouseDown1</c> sets <b>both</b> flags, so a right click is a skip; that asymmetry is the
    /// oracle's, not an oversight here.</para>
    ///
    /// <para><b>Which device feeds <c>keyPressed</c>.</b> In the oracle it is the <b>left mouse
    /// button</b> — the keyboard never sets it, so E does not advance a line in the original game. The
    /// port feeds it from the interact action as well, because E is the port's interact key and a
    /// keyboard-only player must be able to read a conversation. That is a deliberate addition, not a
    /// reading of the oracle: it cannot skip anything the mouse path could not, since both are single
    /// presses on the same latch. The left button itself is not wired — see <see cref="Poll"/>.</para>
    /// </summary>
    public sealed class ScriptInputProbe : IScriptInputProbe
    {
        private readonly InputReader _input;

        /// <summary>AS3 <c>ctr.keyPressed</c> — "advance one line", latched on a press edge.</summary>
        private bool _pressed;

        /// <summary>AS3 <c>ctr.keyPressed2</c> — "run past the last line", latched on the RMB press
        /// edge. Set together with <see cref="_pressed"/>, as <c>Ctr.as:586-587</c> does.</summary>
        private bool _skip;

        /// <summary>Whether the port should read the mouse buttons at all. False in a headless or
        /// offline run, where <c>Mouse.current</c> is null anyway.</summary>
        private readonly bool _readMouse;

        /// <summary>
        /// Set by <see cref="ClearPresses"/> so the <b>next</b> <see cref="Poll"/> latches nothing.
        ///
        /// <para><b>Why a clear has to reach forward one sample.</b> The oracle's events are delivered:
        /// the mouse-down that starts a conversation has already happened when <c>com()</c> clears the
        /// latch (<c>Script.as:215</c>), so it cannot be seen again. The port's events are <i>sampled</i>
        /// on the frame boundary, and the frame boundary and the tick are not ordered relative to each
        /// other — <c>SimLoop</c> and <c>ScriptSystem</c> are both VContainer tickables, and the NPC that
        /// starts the script runs in <c>MonoBehaviour.Update</c>. If the poll happens to run after the
        /// clear, the very press that opened the dialogue is re-latched and consumed by the first
        /// blocking tick, and the opening line is replaced before it can be read. Suppressing exactly
        /// one sample reproduces the delivery order without depending on the order.</para>
        /// </summary>
        private bool _suppressNextPoll;

        public ScriptInputProbe(InputReader input = null, bool readMouse = true)
        {
            _input = input;
            _readMouse = readMouse;
        }

        /// <inheritdoc />
        public bool ActionKeyPressed => _pressed;

        /// <inheritdoc />
        public bool AdvancePressed => _skip;

        /// <inheritdoc />
        public void ClearPresses()
        {
            _pressed = false;
            _skip = false;
            _suppressNextPoll = true;
        }

        /// <summary>
        /// The frame-boundary half: reads the device into the latches. Called once per rendered frame by
        /// <c>ScriptSystem</c>'s <c>ITickable</c> entry point.
        ///
        /// <para><b>Press edges are latched, never sampled.</b> <c>wasPressedThisFrame</c> is true for one
        /// frame only, and the tick runs at 30 Hz while the frame may run faster — so the edge is
        /// <i>or</i>-ed into a latch that only <see cref="ClearPresses"/> resets. Without the latch a
        /// press that landed on a frame with no tick would be lost, which shows up as "the dialogue
        /// sometimes ignores a click".</para>
        ///
        /// <para><b>Do not substitute <c>IsPressed()</c> for the edge.</b> That is the level form, and it
        /// is what made a held key walk a whole conversation in a fraction of a second; the difference is
        /// invisible in a one-press test and unmissable in play.</para>
        /// </summary>
        public void Poll()
        {
            // One-shot: the sample immediately after a clear is the one that could still be carrying
            // the press that clear was about. See `_suppressNextPoll`.
            if (_suppressNextPoll)
            {
                _suppressNextPoll = false;
                return;
            }

            // E / gamepad West. `WasPressedThisFrame` on the action, not `IsPressed` — see the summary.
            if (_input?.Interact != null && _input.Interact.WasPressedThisFrame())
            {
                _pressed = true;
            }

            if (!_readMouse) return;

            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            // AS3 `onRightMouseDown1` (Ctr.as:586-587) — right click sets BOTH flags: it advances and
            // it asks the walk to run past the last line. Setting only `_skip` would still skip, but
            // would diverge from the oracle on the frame a skip happens to be the only press.
            if (mouse.rightButton.wasPressedThisFrame)
            {
                _pressed = true;
                _skip = true;
            }

            // The LEFT button is deliberately NOT read, although `Ctr.onMouseDown1` (Ctr.as:551) is the
            // oracle's main way to advance a line. In AS3 an attack and an interact are both gated by
            // `ctr.active` / `gg`, which `Script.step` clears while a dialogue waits — so a click during
            // a conversation cannot fire the weapon. The port's attack path is not gated on the
            // cutscene lock (`PlayerController.HandleAttackStart` runs straight from `AttackMessage`),
            // so wiring LMB here would make every "click to read the next line" also shoot. The
            // action-key latch below covers the same need without that coupling; restore LMB when the
            // attack path learns the lock, not before.
        }
    }
}
