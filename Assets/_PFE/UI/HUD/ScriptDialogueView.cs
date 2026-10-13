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
    /// <para><b>The speaker is carried but not printed.</b> The Oracle's <c>@p</c> (<c>lp1</c>…<c>lp4</c>,
    /// <c>pip</c>, <c>mtn</c>) selects a <i>portrait frame</i> — <c>dial.portret.gotoAndStop(sp)</c>
    /// (<c>GUI.as:1829-1846</c>) — not a name. The port has no portrait art wired, and printing the raw
    /// frame label ("lp1") would be inventing text the Oracle never shows. The label is exposed through
    /// <see cref="Speaker"/> and through the optional <c>speakerLabel</c> so a portrait binder can attach
    /// later without this view having to change.</para>
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

        private RectTransform _panel;
        private Text _panelText;
        private GameObject _panelButton;

        private IDisposable _subscription;

        /// <summary>AS3 <c>dial_n</c> row currently shown, or <c>-1</c> for an inline body. For tests.</summary>
        public int LineIndex { get; private set; } = -1;

        /// <summary>The <c>@p</c> frame label of the current line, or null. For a future portrait binder.</summary>
        public string Speaker { get; private set; }

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
                              Text speakerLabel = null, GameObject panelButton = null)
        {
            _window = window;
            _windowText = windowText;
            _panel = panel;
            _panelText = panelText;
            _speakerLabel = speakerLabel;
            _panelButton = panelButton;

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
                ShowWindow(message.Text, message.Push);
            }
            else
            {
                ShowPanel(message.Text, message.Push, message.Mod);
            }
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
            LineIndex = -1;
            Speaker = null;
        }

        private void OnDestroy()
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }
}
