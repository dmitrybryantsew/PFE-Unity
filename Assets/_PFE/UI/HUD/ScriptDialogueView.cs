using System;
using MessagePipe;
using PFE.Core.Messages;
using UnityEngine;
using UnityEngine.UI;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The dialogue window (<c>vis.dial</c>) and the terminal reader (<c>vis.inform</c>) — a port of the
    /// two branches of <c>GUI.dialText</c> (<c>GUI.as:1741-1905</c>).
    ///
    /// <para><b>One view for two windows, because the Oracle has one function.</b> <c>dial</c>,
    /// <c>dialog</c>, <c>inform</c> and <c>landlevel</c> all reach <c>GUI.dialText</c>, and the only thing
    /// that decides which box the text lands in is the row's <c>@mod</c>: <c>0</c> is the dialogue window,
    /// <c>&gt;= 1</c> is the information panel (<c>GUI.as:1793-1801</c>). Splitting this into two views
    /// would mean two subscribers deciding the same thing from the same field, which is how they end up
    /// disagreeing.</para>
    ///
    /// <para><b>The two branches differ in more than the parent, and both differences are reproduced.</b>
    /// The append separator is <c>"&lt;br&gt;"</c> for the dialogue and <c>"&lt;br&gt;&lt;br&gt;"</c> for the
    /// panel (<c>GUI.as:1835-1843</c> vs <c>:1855-1863</c>) — the panel is a scrolling reader, so its
    /// paragraphs are spaced. And <c>mod == 2</c> shows the panel's OK button (<c>:1872</c>), which is how
    /// <c>landlevel</c> — the one caller that uses <c>mod='1'</c> explicitly — reads as a notice rather
    /// than as a wall of terminal output.</para>
    ///
    /// <para><b>The speaker is a portrait, never a name.</b> The Oracle's <c>@p</c> (<c>lp1</c>…<c>lp4</c>,
    /// <c>calam</c>, <c>pip</c>, <c>mtn</c>…) selects a <i>portrait frame</i> —
    /// <c>dial.portret.gotoAndStop(sp)</c> (<c>GUI.as:1831-1851</c>) — not a caption, so the port draws
    /// <see cref="DialoguePortraitFrames"/>' sprite for the label and never prints the label itself. The
    /// raw string is still exposed through <see cref="Speaker"/> because it is what the view resolved
    /// from, and <see cref="PortraitFrameIndex"/> records the frame it landed on. The optional
    /// <c>speakerLabel</c> Text exists only for a debug layout that wants to show the label; the shipping
    /// HUD passes <c>null</c>.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScriptDialogueView : MonoBehaviour
    {
        /// <summary>Oracle <c>reg == 2</c> — the information panel with its OK button
        /// (<c>GUI.as:1872</c>).</summary>
        public const int ModPanelWithButton = 2;

        private RectTransform _window;
        private Text _windowText;
        private Text _speakerLabel;
        private Image _portrait;
        private Sprite[] _portraitFrames;

        private RectTransform _panel;
        private Text _panelText;
        private GameObject _panelButton;

        private IDisposable _subscription;

        /// <summary>AS3 <c>dial_n</c> row currently shown, or <c>-1</c> for an inline body. For tests.</summary>
        public int LineIndex { get; private set; } = -1;

        /// <summary>The <c>@p</c> frame label of the current line, or null. For a future portrait binder.</summary>
        public string Speaker { get; private set; }

        /// <summary>
        /// The portrait sprite currently shown, or <c>null</c> for the Oracle's blank default. For tests.
        /// </summary>
        public Sprite PortraitSprite => _portrait != null ? _portrait.sprite : null;

        /// <summary>Whether the portrait slot is drawing anything. For tests.</summary>
        public bool PortraitVisible => _portrait != null && _portrait.enabled;

        /// <summary>The catalog index (frame - 1) the current line resolved to, or <c>-1</c>. For tests.</summary>
        public int PortraitFrameIndex { get; private set; } = -1;

        /// <summary>
        /// AS3 <c>World.w.alicorn</c> (<c>World.as:206</c>) — when set, every <c>lp*</c> speaker resolves to
        /// the <c>lpa</c> frame instead (<c>GUI.as:1838-1842</c>).
        ///
        /// <para><b>Nothing in the port writes this yet</b>, and that is a recorded gap rather than an
        /// oversight: the alicorn transformation is a late-game state the port has not reached, and
        /// inventing a second, competing source for the flag would be worse than the missing branch. The
        /// rule itself is implemented and tested in <see cref="DialoguePortraitFrames.Resolve"/>, so
        /// wiring a real flag is a one-line change here when the transformation is ported.</para>
        /// </summary>
        public bool AlicornMode { get; set; }

        /// <summary>AS3 <c>dial.visible</c>. For tests.</summary>
        public bool WindowVisible => _window != null && _window.gameObject.activeSelf;

        /// <summary>AS3 <c>inform.visible</c>. For tests.</summary>
        public bool PanelVisible => _panel != null && _panel.gameObject.activeSelf;

        /// <summary>Current dialogue-window text, with the append separator applied. For tests.</summary>
        public string WindowText => _windowText != null ? _windowText.text : null;

        /// <summary>Current panel text. For tests.</summary>
        public string PanelText => _panelText != null ? _panelText.text : null;

        /// <summary>Assigns the widgets this view draws into. Called by <c>HudBootstrapper</c>.</summary>
        public void Construct(RectTransform window, Text windowText, RectTransform panel, Text panelText,
                              Text speakerLabel = null, GameObject panelButton = null,
                              Image portrait = null, Sprite[] portraitFrames = null)
        {
            _window = window;
            _windowText = windowText;
            _panel = panel;
            _panelText = panelText;
            _speakerLabel = speakerLabel;
            _panelButton = panelButton;
            _portrait = portrait;
            _portraitFrames = portraitFrames;

            Hide();
        }

        /// <summary>Subscribes to the dialogue stream. Split from <see cref="Construct"/> for the same
        /// reason as <see cref="ScriptToastView.Bind"/>.</summary>
        public void Bind(ISubscriber<ScriptDialogueMessage> subscriber)
        {
            _subscription?.Dispose();
            _subscription = subscriber?.Subscribe(OnDialogue);
        }

        /// <summary>
        /// AS3 <c>GUI.dialText</c> (<c>GUI.as:1741-1905</c>) and the hide path at
        /// <c>Script.as:210-213</c>.
        /// </summary>
        private void OnDialogue(ScriptDialogueMessage message)
        {
            if (!message.Visible || string.IsNullOrEmpty(message.Text))
            {
                Hide();
                return;
            }

            LineIndex = message.LineIndex;
            Speaker = message.Speaker;

            if (_speakerLabel != null)
            {
                _speakerLabel.text = message.Speaker ?? string.Empty;
            }

            if (message.Mod == 0)
            {
                ApplyPortrait(message.Speaker);
                ShowWindow(message.Text, message.Push);
            }
            else
            {
                ShowPanel(message.Text, message.Push, message.Mod);
            }
        }

        /// <summary>
        /// AS3 <c>dial.portret.gotoAndStop(...)</c> — <c>GUI.as:1831-1851</c>.
        ///
        /// <para><b>The reset comes first, and that is the whole point.</b> The Oracle does an
        /// unconditional <c>gotoAndStop(1)</c> and only then, if the row has a <c>@p</c>, tries
        /// <c>gotoAndStop(sp)</c> inside a <c>try</c> whose <c>catch</c> is another <c>gotoAndStop(1)</c>.
        /// Frame 1 is blank, so an unattributed line and a mistyped label both mean "no portrait" — and
        /// neither may leave the previous speaker's face on screen. A view that only ever <i>set</i> a
        /// sprite when a label was present would carry the last speaker over every narration line.</para>
        ///
        /// <para>The label itself is resolved by <see cref="DialoguePortraitFrames"/>, which owns the
        /// SWF's frame table and the <c>World.alicorn</c> substitution.</para>
        /// </summary>
        private void ApplyPortrait(string speaker)
        {
            PortraitFrameIndex = -1;

            if (_portrait == null) return;

            if (_portraitFrames != null &&
                DialoguePortraitFrames.TryGetIndexFor(speaker, AlicornMode, out int index) &&
                index >= 0 && index < _portraitFrames.Length)
            {
                Sprite sprite = _portraitFrames[index];
                _portrait.sprite = sprite;
                _portrait.enabled = sprite != null;
                if (sprite != null) PortraitFrameIndex = index;
                return;
            }

            // The blank default. The transparent frame 1 is not loaded — clearing the slot draws the same
            // thing without a texture read.
            _portrait.sprite = null;
            _portrait.enabled = false;
        }

        /// <summary><c>reg == 0</c> (<c>GUI.as:1826-1850</c>).</summary>
        private void ShowWindow(string text, bool push)
        {
            if (_window != null) _window.gameObject.SetActive(true);
            if (_panel != null) _panel.gameObject.SetActive(false);

            if (_windowText != null)
            {
                _windowText.text = push && !string.IsNullOrEmpty(_windowText.text)
                    ? _windowText.text + "\n" + text
                    : text;
            }
        }

        /// <summary><c>reg &gt;= 1</c> (<c>GUI.as:1852-1878</c>).</summary>
        private void ShowPanel(string text, bool push, int mod)
        {
            if (_panel != null) _panel.gameObject.SetActive(true);
            if (_window != null) _window.gameObject.SetActive(false);

            if (_panelText != null)
            {
                // "<br><br>", not "<br>" — see the class summary.
                _panelText.text = push && !string.IsNullOrEmpty(_panelText.text)
                    ? _panelText.text + "\n\n" + text
                    : text;
            }

            if (_panelButton != null)
            {
                _panelButton.SetActive(mod == ModPanelWithButton);
            }
        }

        /// <summary>
        /// AS3 <c>dialText()</c> with no id (<c>GUI.as:1756-1761</c>) — both windows off. The oracle also
        /// clears the panel text on the next show rather than here, so the text is deliberately left in
        /// place: a re-show with <c>push</c> appends to it exactly as the Oracle's <c>htmlText</c> does.
        /// </summary>
        public void Hide()
        {
            if (_window != null) _window.gameObject.SetActive(false);
            if (_panel != null) _panel.gameObject.SetActive(false);
            if (_portrait != null)
            {
                _portrait.sprite = null;
                _portrait.enabled = false;
            }
            LineIndex = -1;
            Speaker = null;
            PortraitFrameIndex = -1;
        }

        private void OnDestroy()
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }
}
