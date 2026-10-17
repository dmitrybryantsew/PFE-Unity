using UnityEngine;
using UnityEngine.UI;
using PFE.UI.Common;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The world-anchored interaction prompt — the port of the Oracle's <c>celobj</c> text
    /// (<c>GUI.as:1244-1467</c>, drawn from <c>GUI.step</c> at <c>:1949</c>).
    ///
    /// <para><b>What it is.</b> A label that floats over the object the player is looking at, showing
    /// the object's name and the action the player may take on it. It is <b>not</b> a fixed "Press E"
    /// banner at the bottom of the screen; the Oracle re-projects it onto the target every frame
    /// (<c>cam.setKoord(this.celobj, this.celObj.X, this.celObj.Y)</c>, <c>GUI.as:1461</c>) and then
    /// lifts it clear of the object's own height
    /// (<c>celobj.y = pr_bar.y = cam.screenY - 40 - celobj.height</c>, <c>GUI.as:1463</c>). The
    /// position half of that is <see cref="WorldAnchoredHudElement"/>'s job; this class owns the text
    /// and the show/hide.</para>
    ///
    /// <para><b>What it shows, and what it deliberately does not.</b> The Oracle composes the string as
    /// <c>celObj.nazv</c> then a newline then the action text (<c>GUI.as:1286-1299</c>). Both are ported:
    /// the name arrives as an argument (the port resolves it from the object's definition — see
    /// <c>PlayerController.CurrentInteractTargetName</c>) and the action text is
    /// <c>IInteractable.ActionText</c>. <b>Not</b> ported here: the Oracle's risk colouring
    /// (<c>.warn</c> when <c>celObj.warn &gt; 0</c>), the lock-difficulty tag, and the state tags —
    /// none of those inputs is exposed by the port's <c>IInteractable</c> yet, and inventing a colour
    /// would launder a guess into the interface. The single colour below is the Oracle's normal prompt
    /// colour, <c>.r</c> (<c>PfeUiColors.Green</c>).</para>
    ///
    /// <para><b>Visibility is an alpha, not a <c>SetActive</c>.</b> Same reason <c>ScriptToastView</c>
    /// documents: a deactivated GameObject runs no <c>LateUpdate</c>, and the component that positions
    /// this element (<see cref="WorldAnchoredHudElement"/>) needs one more frame of life to place it
    /// on the frame it reappears. A <see cref="CanvasGroup"/> alpha leaves the object alive and simply
    /// makes it invisible, and the group also turns raycasts off so the prompt can never eat a click
    /// meant for the world.</para>
    /// </summary>
    public sealed class InteractionPromptView : MonoBehaviour
    {
        [Header("UI Components")]
        [SerializeField]
        [Tooltip("Text that renders the prompt. Its own font/alignment are the bootstrapper's; only the string and colour are written here.")]
        private Text _text;

        [SerializeField]
        [Tooltip("Group whose alpha shows and hides the prompt. Never deactivate this GameObject.")]
        private CanvasGroup _group;

        [SerializeField]
        [Tooltip("Pins the prompt over the object it describes. See WorldAnchoredHudElement.")]
        private WorldAnchoredHudElement _anchor;

        [Header("Visual Settings")]
        [SerializeField]
        [Tooltip("Oracle `.r` — the normal interaction-prompt colour (GUI.as:189-217).")]
        private Color _normalColour = PfeUiColors.Green;

        /// <summary>The last name handed to <see cref="Show"/>. For tests and inspection.</summary>
        public string LastName { get; private set; }

        /// <summary>The last action text handed to <see cref="Show"/>. For tests and inspection.</summary>
        public string LastAction { get; private set; }

        /// <summary>Whether the prompt is currently visible. For tests and inspection.</summary>
        public bool IsShown { get; private set; }

        private void Awake()
        {
            if (_text == null)
            {
                _text = GetComponentInChildren<Text>(true);
            }

            if (_group == null)
            {
                _group = GetComponent<CanvasGroup>();
            }

            // Start hidden; nothing is under the cursor at boot.
            Apply(false, string.Empty);
        }

        /// <summary>Hand the view its parts. Called once by the bootstrapper that builds them.</summary>
        public void Construct(Text text, CanvasGroup group, WorldAnchoredHudElement anchor)
        {
            _text = text;
            _group = group;
            _anchor = anchor;
            Apply(false, string.Empty);
        }

        /// <summary>
        /// Show the prompt over a world position, and remember the text it was given.
        ///
        /// <para>An empty name or an empty action is skipped rather than leaving a blank line — the
        /// Oracle's own composition is unconditional, but a port whose name is not resolved yet would
        /// otherwise render a leading blank line, which reads as a rendering fault. A prompt with
        /// neither a name nor an action hides rather than drawing an empty box.</para>
        /// </summary>
        public void Show(string displayName, string actionText, Vector3 worldPosition)
        {
            LastName = displayName;
            LastAction = actionText;

            string body = Compose(displayName, actionText);
            if (string.IsNullOrEmpty(body))
            {
                Hide();
                return;
            }

            _anchor?.SetWorldPoint(worldPosition);
            Apply(true, body);
        }

        /// <summary>Hide the prompt. Idempotent.</summary>
        public void Hide()
        {
            _anchor?.ClearAnchor();
            Apply(false, string.Empty);
        }

        /// <summary>
        /// The Oracle's two-line composition — <c>name</c> then <c>actionText</c> (<c>GUI.as:1286-1299</c>)
        /// — with an empty part omitted. Pure and static so it can be asserted without a scene.
        /// </summary>
        public static string Compose(string displayName, string actionText)
        {
            bool hasName = !string.IsNullOrEmpty(displayName);
            bool hasAction = !string.IsNullOrEmpty(actionText);

            if (hasName && hasAction)
            {
                return displayName + "\n" + actionText;
            }

            if (hasName)
            {
                return displayName;
            }

            return hasAction ? actionText : string.Empty;
        }

        private void Apply(bool visible, string body)
        {
            IsShown = visible;

            if (_text != null)
            {
                _text.text = body;
                _text.color = _normalColour;
            }

            if (_group != null)
            {
                _group.alpha = visible ? 1f : 0f;

                // A prompt is a label, never a control. Leaving raycasts on would let it swallow the
                // click that targets the object underneath it.
                _group.blocksRaycasts = false;
                _group.interactable = false;
            }
        }
    }
}
