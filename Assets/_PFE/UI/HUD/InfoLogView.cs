using UnityEngine;
using UnityEngine.UI;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Draws <see cref="InfoLogRules"/> — the corner message list AS3 calls <c>vis.info</c>
    /// (<c>GUI.as:1553-1577</c>). One <see cref="Text"/> whose body is the joined lines; the state
    /// machine decides what those lines are.
    ///
    /// <para><b>Alpha is NOT driven by content, and that is the oracle's behaviour.</b> The obvious
    /// design — fade the panel out when the list empties — is not what the game does.
    /// <c>infoAlpha</c> is declared <c>1</c> and written from exactly one place: <c>Ctr.onMouseMove1</c>
    /// sets it to <c>0.2</c> while the cursor is in the <b>top-right 400 × 100 strip</b>, i.e. over the
    /// panel's own corner, and back to <c>1</c> everywhere else (<c>Ctr.as:528-535</c>). The panel fades
    /// so it does not hide the world behind it while the player is looking there. Emptying the list is
    /// done by <c>remStr</c> clearing <c>htmlText</c>, not by alpha.</para>
    ///
    /// <para>So <see cref="Dimmed"/> is the only input to the fade, and it defaults to off. The input
    /// side — "is the cursor in that strip" — is <b>not wired</b>: nothing in the port's HUD tracks the
    /// cursor position, and inventing a hit test here would duplicate whatever eventually owns it. The
    /// seam is one bool and the oracle line numbers are above.</para>
    ///
    /// <para><b>Rich text is required.</b> <see cref="PickupFeedbackRules.Substitute"/> wraps <c>@1</c>
    /// in <c>&lt;b&gt;</c> exactly as the oracle's <c>htmlText</c> does, so the label must have
    /// <c>supportRichText</c> on (Unity's default) or the tags would render literally.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InfoLogView : MonoBehaviour
    {
        /// <summary>AS3's fade step (<c>GUI.as:1969</c>, <c>:1973</c>).</summary>
        public const float FadePerFrame = 0.1f;

        /// <summary><c>Ctr.as:530</c> — <c>World.w.gui.infoAlpha = 0.2</c> under the cursor strip.</summary>
        public const float DimAlpha = 0.2f;

        private Text _text;
        private CanvasGroup _group;
        private float _alpha = 1f;

        private readonly InfoLogRules _rules = new InfoLogRules();

        /// <summary>The state machine, for tests and for the F2 readout.</summary>
        public InfoLogRules Rules => _rules;

        /// <summary>Current drawn alpha, 0.2…1.</summary>
        public float Alpha => _alpha;

        /// <summary>
        /// AS3 <c>gui.infoAlpha</c> — true while the cursor is in the panel's own corner strip. See the
        /// class remarks: nothing sets this yet.
        /// </summary>
        public bool Dimmed { get; set; }

        /// <summary>Wires the label and its fade group. Called once by the HUD bootstrapper.</summary>
        public void Construct(Text text, CanvasGroup group)
        {
            _text = text;
            _group = group;
            _alpha = 1f;

            if (_group != null)
            {
                _group.alpha = _alpha;
                _group.blocksRaycasts = false;
                _group.interactable = false;
            }

            Refresh();
        }

        /// <summary>One accepted <c>infoText</c> call. Returns whether the line was drawn.</summary>
        public bool Push(string line)
        {
            if (!_rules.Add(line)) return false;
            Refresh();
            return true;
        }

        /// <summary>Drops every line — the oracle's <c>remStr</c> at one line, and what a room change wants.</summary>
        public void Clear()
        {
            _rules.Clear();
            Refresh();
        }

        private void Update()
        {
            if (_rules.Step()) Refresh();

            float target = Dimmed ? DimAlpha : 1f;
            if (Mathf.Abs(target - _alpha) <= 0.01f) return;

            // 0.01 is the oracle's own deadband (GUI.as:1966) — below it the step is skipped, which is
            // what keeps this from rewriting the CanvasGroup every frame once it has settled.
            _alpha = Mathf.MoveTowards(_alpha, target, FadePerFrame);
            if (_group != null) _group.alpha = _alpha;
        }

        private void Refresh()
        {
            if (_text == null) return;

            // AS3 appends `<br>` per line, oldest first, and remStr drops the first — so the newest
            // line is at the bottom. Joining with '\n' is the same document in a Unity Text.
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < _rules.Lines.Count; i++)
            {
                if (i > 0) builder.Append('\n');
                builder.Append(_rules.Lines[i]);
            }

            _text.text = builder.ToString();
        }
    }
}
