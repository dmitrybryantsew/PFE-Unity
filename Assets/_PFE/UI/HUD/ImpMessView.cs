using UnityEngine;
using UnityEngine.UI;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The one-time tutorial popup — the port of AS3 <c>GUI.impMess</c>
    /// (<c>inter/GUI.as:1905-1932</c>) and the <c>vis.imp</c> symbol it fills.
    ///
    /// <para><b>What it is.</b> Picking up the screwdriver for the first time in a save shows a boxed
    /// message — the item's name, its icon, and a paragraph explaining what it is for — and the player
    /// dismisses it by pressing anything. That is the "text + popup" the original game shows, and it is
    /// a <i>different</i> system from the corner message list and the rising label: those fire on every
    /// take, this fires once per save per <c>mess</c> key.</para>
    ///
    /// <para><b>Why the input lock is not in this class.</b> The oracle's <c>impMess</c> writes
    /// <c>World.w.ctr.active = false</c> and <c>guiPause = true</c>, i.e. it stops the <i>player</i>, not
    /// the GUI. The port's equivalent is <c>PlayerController.ScriptControlEnabled</c>, which lives on a
    /// component this view has no reference to and must not acquire — <see cref="HudBootstrapper"/>
    /// already tracks the player, so it owns that half. This view owns only what it can see: its own
    /// frame counter and the keyboard/mouse edge that ends the popup.</para>
    ///
    /// <para><b>The dismissal rule, and the 30-frame lockout is load-bearing.</b> The oracle sets
    /// <c>t_bulb = 30</c> and then, every frame, dismisses when <c>ctr.active &amp;&amp; t_bulb &lt;= 0</c>
    /// and otherwise forces <c>ctr.active = false</c> again (<c>GUI.as:2069-2076</c>). Without the
    /// lockout the very click that picked the item up would dismiss the popup on the same frame it
    /// appeared. So: swallow all input for 30 frames, then the first press dismisses.</para>
    ///
    /// <para><b>Not reproduced: <c>t_bulb</c> is shared.</b> The oracle uses one counter for the popup
    /// (30), the floating label (10) and the bulb hint (20), so picking something up while a popup is
    /// open <i>shortens</i> the popup's lockout. That is an artefact of one field doing three jobs; the
    /// port keeps them separate, and the only visible difference is that a popup opened during a loot
    /// spill stays input-locked for its full 30 frames instead of less.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ImpMessView : MonoBehaviour
    {
        /// <summary><c>this.t_bulb = 30</c> (<c>GUI.as:1930</c>) — frames of swallowed input. At a 60 Hz
        /// display this is 0.5 s where the oracle's 30-tick lockout at its 30 fps frame loop is 1 s,
        /// which is the same frame-vs-second asymmetry <see cref="InfoLogRules"/> documents. Half a
        /// second is still far longer than the click that caused the pickup, so the lockout keeps its
        /// purpose; it is noted here rather than converted because the HUD has no tick source.</summary>
        public const int LockoutFrames = 30;

        private Text _title;
        private Text _body;
        private Image _icon;
        private CanvasGroup _group;

        /// <summary>Frames left in which input is swallowed.</summary>
        private int _lockout;

        /// <summary>Whether the popup is on screen.</summary>
        public bool Visible { get; private set; }

        /// <summary>How many popups have been shown. For tests and the debug overlay.</summary>
        public int ShownCount { get; private set; }

        /// <summary>How many were dismissed by a press. For tests.</summary>
        public int DismissedCount { get; private set; }

        /// <summary>Frames left before a press counts. For tests.</summary>
        public int LockoutRemaining => _lockout;

        /// <summary>Wires the widgets. Called once by the HUD bootstrapper.</summary>
        public void Construct(Text title, Text body, Image icon, CanvasGroup group)
        {
            _title = title;
            _body = body;
            _icon = icon;
            _group = group;

            SetShown(false);
        }

        /// <summary>
        /// <c>impMess(title, text, ico)</c>. <paramref name="icon"/> may be null — the oracle falls back
        /// to the symbol's frame 1, which is blank, so a null icon draws nothing rather than a
        /// placeholder box.
        /// </summary>
        public void Show(string title, string body, Sprite icon)
        {
            if (_title != null) _title.text = title ?? string.Empty;
            if (_body != null) _body.text = body ?? string.Empty;

            if (_icon != null)
            {
                _icon.sprite = icon;
                _icon.enabled = icon != null;
                _icon.preserveAspect = true;
            }

            _lockout = LockoutFrames;
            ShownCount++;
            SetShown(true);
        }

        /// <summary>Hide it and re-arm. Idempotent.</summary>
        public void Dismiss()
        {
            if (!Visible) return;

            DismissedCount++;
            _lockout = 0;
            SetShown(false);
        }

        /// <summary>
        /// One HUD frame. Returns true when <b>this</b> frame dismissed the popup, so the caller can
        /// release the input lock on the same frame rather than a frame late.
        /// </summary>
        public bool Tick()
        {
            if (!Visible) return false;

            if (_lockout > 0)
            {
                _lockout--;
                return false;
            }

            if (!AnyPressThisFrame()) return false;

            Dismiss();
            return true;
        }

        private void SetShown(bool shown)
        {
            Visible = shown;

            if (_group != null)
            {
                _group.alpha = shown ? 1f : 0f;
                // A hidden popup must not eat the cursor: the HUD is ScreenSpaceOverlay and every
                // other element sets this false for the same reason.
                _group.blocksRaycasts = shown;
                _group.interactable = false;
            }

            // The GameObject is deliberately never deactivated. Deactivating it would stop this
            // component's own Tick — the same deadlock ScriptToastView documents — and the alpha is
            // what hides it anyway.
        }

        /// <summary>
        /// "Any key or mouse button", which is what <c>ctr.active</c> means — the oracle's input layer
        /// sets it from its own key and mouse handlers, not from a bound action.
        ///
        /// <para>The Input System is read directly, matching every other raw-key reader in this project
        /// (<c>PlayerController</c>'s <c>E</c>/right-click, <c>DoorPropPresenter</c>'s F9). Legacy
        /// <c>Input.anyKeyDown</c> is not used because this project runs the new Input System only.</para>
        /// </summary>
        private static bool AnyPressThisFrame()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.anyKey.wasPressedThisFrame) return true;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return false;

            return mouse.leftButton.wasPressedThisFrame
                || mouse.rightButton.wasPressedThisFrame
                || mouse.middleButton.wasPressedThisFrame;
        }
    }
}
