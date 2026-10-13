using PFE.Core;
using PFE.Core.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// The <c>wait</c> latch — the port of AS3 <c>World.w.ctr.keyPressed</c> / <c>keyPressed2</c>
    /// (<c>Ctr.as:233</c>, <c>:582-588</c>).
    ///
    /// <para><b>Why the device is read here and not in the runner.</b> <see cref="ISimTickable"/>'s
    /// determinism contract forbids a tick from reading <c>Input</c> — the tick index is the only time
    /// source, and input is gathered once on the frame boundary into a snapshot
    /// (<c>ISimTickable.cs:10-29</c>). So this type has two halves: <see cref="Poll"/> runs on the frame
    /// and reads the device into latches, and the <see cref="IScriptInputProbe"/> members the runner sees
    /// only ever read those latches. A probe that called <c>IsPressed()</c> from inside the tick would
    /// work today and break the moment a second peer exists.</para>
    ///
    /// <para><b>The two latches are not two names for one thing.</b> <c>keyPressed</c> is the action key
    /// <i>held</i> (<c>Ctr.onKeyboardDownEvent</c> / <c>onKeyboardUpEvent</c>), and it is what advances a
    /// dialogue one line per press. <c>keyPressed2</c> is the <b>right mouse button</b> going down
    /// (<c>Ctr.onRightMouseDown1</c>, <c>Ctr.as:582-588</c>), and it makes <c>Script.step</c> set
    /// <c>dial_n = 10000</c> — run past the last line and release the script immediately
    /// (<c>Script.as:156-159</c>). A port that mapped both to the action key would either skip every
    /// dialogue or never skip one.</para>
    /// </summary>
    public sealed class ScriptInputProbe : IScriptInputProbe
    {
        private readonly InputReader _input;

        private bool _held;
        private bool _advance;

        /// <summary>Whether the port should read the right mouse button for the skip latch. False in a
        /// headless or offline run, where <c>Mouse.current</c> is null anyway.</summary>
        private readonly bool _readMouse;

        public ScriptInputProbe(InputReader input = null, bool readMouse = true)
        {
            _input = input;
            _readMouse = readMouse;
        }

        /// <inheritdoc />
        public bool ActionKeyHeld => _held;

        /// <inheritdoc />
        public bool AdvancePressed => _advance;

        /// <inheritdoc />
        public void ClearPresses() => _advance = false;

        /// <summary>
        /// The frame-boundary half: reads the device into the latches. Called once per rendered frame by
        /// <c>ScriptSystem</c>'s <c>ITickable</c> entry point.
        ///
        /// <para><b>Held is sampled, advance is latched.</b> <c>IsPressed()</c> is a level, so sampling it
        /// each frame is exact. <c>wasPressedThisFrame</c> is an edge, and the tick runs at 30 Hz while
        /// the frame may run faster — so the edge is <i>or</i>-ed into a latch that only
        /// <see cref="ClearPresses"/> resets. Without the latch a press that landed on a frame with no
        /// tick would be lost, which shows up as "the dialogue sometimes ignores a click".</para>
        /// </summary>
        public void Poll()
        {
            if (_input?.Interact != null)
            {
                _held = _input.Interact.IsPressed();
            }

            if (!_readMouse) return;

            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.wasPressedThisFrame)
            {
                _advance = true;
            }
        }
    }
}
