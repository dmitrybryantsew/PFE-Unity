using System;
using System.Collections.Generic;
using MessagePipe;
using PFE.Core.Messages;
using PFE.UI.PipBuck;
using UnityEngine;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// The port of the oracle's staging half — <c>GUI.messText</c> (<c>GUI.as:1656-1691</c>),
    /// <c>GUI.dialText</c> (<c>:1741-1905</c>) and the <c>World.w.gg</c> control calls the script chain
    /// makes (<c>Script.as:239-391</c>).
    ///
    /// <para><b>Why this type is the one that makes <c>begin</c> readable.</b> The begin land's opening
    /// script is thirteen actions, and ten of them are <c>mess</c>, <c>dial</c> or <c>control</c>. Before
    /// this existed the port had <i>no</i> consumer for <c>TutorialPromptMessage</c> — the message was
    /// published by <c>AreaTriggerSystem</c> and subscribed to by nobody — and no dialogue surface at
    /// all, so a <c>mess</c> or a <c>dial</c> produced nothing visible whether the data was right or
    /// wrong. That is the shape this project calls "unreachable with the wall green": every test could
    /// pass and the player would still see an empty screen.</para>
    ///
    /// <para><b>Text resolution is here, not in the view.</b> The oracle resolves a key inside
    /// <c>dialText</c> before it touches a text field, and the two readers disagree in detail —
    /// <c>messText</c> joins every <c>&lt;r&gt;</c> row and substitutes the <i>entry's</i> keys, while
    /// <c>dialText</c> picks one row by index and substitutes the <i>row's</i> keys. Putting that in a
    /// view would give every view its own opinion about which key list applies.</para>
    /// </summary>
    public sealed class ScriptPresentationHost : IScriptPresentationHost
    {
        /// <summary>The Oracle's <c>Res.clob</c> table name for the <c>txt</c> rows — see
        /// <see cref="PipTextTable.TextInTable"/> for why this is not an alias.</summary>
        private const string TxtTable = "txt";

        private readonly IPublisher<TutorialPromptMessage> _promptPublisher;
        private readonly IPublisher<ScriptDialogueMessage> _dialoguePublisher;
        private readonly IPublisher<ScriptControlLockMessage> _controlPublisher;
        private readonly IPublisher<ScriptOverlayMessage> _overlayPublisher;
        private readonly PipTextTable _text;
        private readonly Action<string> _report;

        /// <summary>Features with no consumer in the port yet, reported once each by name. A silent
        /// return here is indistinguishable from "the command ran and changed nothing", which is the
        /// failure mode this whole type exists to end.</summary>
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        private bool _guiVisible = true;
        private float _blackAlpha;
        private float _blackFade;

        /// <summary>All optional: a container built without the message brokers must degrade to a
        /// warning rather than a throw on the room-build path.</summary>
        public ScriptPresentationHost(
            PipTextTable text = null,
            IPublisher<TutorialPromptMessage> promptPublisher = null,
            IPublisher<ScriptDialogueMessage> dialoguePublisher = null,
            IPublisher<ScriptControlLockMessage> controlPublisher = null,
            IPublisher<ScriptOverlayMessage> overlayPublisher = null,
            Action<string> report = null)
        {
            _text = text ?? PipTextTable.Empty;
            _promptPublisher = promptPublisher;
            _dialoguePublisher = dialoguePublisher;
            _controlPublisher = controlPublisher;
            _overlayPublisher = overlayPublisher;
            _report = report ?? (message => Debug.LogWarning(message));
        }

        /// <summary>Whether the HUD bar is currently told to draw. Read by tests and by a later
        /// consumer.</summary>
        public bool GuiVisible => _guiVisible;

        /// <summary>The blackout overlay's current alpha. <c>0</c> is clear.</summary>
        public float BlackAlpha => _blackAlpha;

        /// <summary>The blackout fade rate, alpha per frame.</summary>
        public float BlackFadeRate => _blackFade;

        // ── AS3 `mess` ────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>gui.messText(val, "", opt1&gt;0, opt2&gt;0)</c> (<c>Script.as:247-250</c>) —
        /// <c>Res.messText(val)</c> resolved here and published as a
        /// <see cref="TutorialPromptMessage"/>.
        ///
        /// <para><b>Why <c>TutorialPromptMessage</c> and not a new type.</b> It is the port's existing
        /// banner seam, already registered, and its shape is the oracle's: a key, resolved text, a
        /// show/hide flag and a vertical position (<c>mess.y = 50</c> vs <c>screenY - 50 - height</c>,
        /// <c>GUI.as:1682-1689</c>). What it lacked was a subscriber.</para>
        ///
        /// <para><b>There is deliberately NO <c>@imp</c> gate here, and the reason is worth stating.</b>
        /// <c>Res.messText(param1, param2 = 0, param3 = true)</c> defaults its third parameter — the
        /// <c>imp</c> flag — to <b>true</b>, and its suppression test is
        /// <c>if(!imp &amp;&amp; xml.@imp &lt;= 0) return ""</c> (<c>Res.as:174-178</c>). The script path calls
        /// <c>Res.messText(param1)</c> (<c>GUI.as:1664</c>), so <c>!imp</c> is <b>false</b> and the entry's
        /// own <c>@imp</c> is never consulted. An earlier revision of this method read the condition
        /// without reading the default and suppressed every <c>mess</c> whose row has no <c>imp</c>
        /// attribute — which is <c>beginMess</c>, <c>trWalk</c> and most of the table, i.e. precisely the
        /// messages the begin land is built on. <c>Res.messText</c>'s other caller is the PipBuck's own
        /// notification path, and it passes <c>imp</c> explicitly; that caller is where the flag
        /// matters.</para>
        ///
        /// <para><b>A missing row leaves the previous banner up.</b> The oracle assigns the banner text
        /// only <c>if(_loc6_ != "")</c> (<c>GUI.as:1673</c>) but sets <c>t_mess = 150</c> before that
        /// check — so a miss still re-arms the timer on whatever is already showing. That is a real
        /// (if odd) behaviour and it is reproduced: the miss reports and returns without publishing, and
        /// the view's timer is not touched because nothing was published.</para>
        /// </summary>
        public void ShowMessage(string messKey, bool down, bool push)
        {
            if (string.IsNullOrEmpty(messKey)) return;

            if (!_text.TryResolveBody(TxtTable, messKey, out string text) || string.IsNullOrEmpty(text))
            {
                ReportOnce("mess-missing:" + messKey,
                    $"[ScriptPresentationHost] `mess {messKey}` has no row in the `txt` table of " +
                    "text_en.xml, so the banner would have been empty. The Oracle leaves the previous " +
                    "banner's text in place (GUI.as:1673).");
                return;
            }

            var message = new TutorialPromptMessage(messKey, text, true, down, push);
            if (_promptPublisher == null)
            {
                ReportOnce("(prompt publisher)",
                    $"[ScriptPresentationHost] `mess {messKey}` resolved to \"{text}\" but no " +
                    "TutorialPromptMessage publisher is wired, so nothing can draw it.");
                return;
            }

            _promptPublisher.Publish(message);
        }

        // ── AS3 `dial` / `dialog` ─────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>gui.dialText(id, n, down, wait)</c> (<c>GUI.as:1741-1905</c>) for a table id.
        ///
        /// <para><b>The return value is the script's control flow, not a convenience.</b>
        /// <c>Script.step</c> reads it to decide whether the monologue is finished
        /// (<c>Script.as:170-183</c>), so <c>false</c> must mean exactly what the oracle means: the id is
        /// unknown, or there is no row <c>n</c>. <c>n</c> is zero-based.</para>
        ///
        /// <para><b>The <c>&lt;r&gt;</c>-less entry is handled, not skipped.</b> An entry whose body is
        /// plain text has no rows, and <c>dialText</c> then renders <c>xml.n[0]</c> whole
        /// (<c>GUI.as:1770-1784</c>). Row 0 of such an entry is the body, and any higher index is a miss —
        /// which is what lets a script walk a plain message with the same code path.</para>
        /// </summary>
        public bool ShowDialogue(string dialogueId, int lineIndex, bool down, bool wait)
        {
            if (string.IsNullOrEmpty(dialogueId) || lineIndex < 0) return false;

            if (!_text.TryGetDialogueRow(TxtTable, dialogueId, lineIndex, out PipTextTable.DialogueRow row))
            {
                // No rows: fall back to the whole-body form, and only for row 0.
                if (lineIndex != 0 || !_text.TryResolveBody(TxtTable, dialogueId, out string body))
                {
                    return false;
                }

                row = new PipTextTable.DialogueRow(null, body, 0, false);
            }

            Publish(row.Speaker, row.Text, row.Mod, row.Push, dialogueId, lineIndex, wait);
            return true;
        }

        /// <summary>
        /// AS3 <c>gui.dialText(&lt;r mod=… push=…&gt;text&lt;/r&gt;, 0, false, true)</c> — the inline
        /// form, used by <c>inform</c> (<c>Script.as:273-280</c>) and <c>landlevel</c>
        /// (<c>:281-295</c>). No lookup: the text is already in hand, which is exactly why the oracle
        /// builds a synthetic <c>&lt;r&gt;</c> node for these two and only these two.
        /// </summary>
        public void ShowRawDialogue(string text, int mod, bool push, bool wait)
        {
            if (string.IsNullOrEmpty(text)) return;
            Publish(speaker: null, text: text, mod: mod, push: push,
                    dialogueId: null, lineIndex: 0, wait: wait);
        }

        public void HideDialogue() => _dialoguePublisher?.Publish(ScriptDialogueMessage.Hidden);

        private void Publish(string speaker, string text, int mod, bool push, string dialogueId,
                             int lineIndex, bool wait)
        {
            if (_dialoguePublisher == null)
            {
                ReportOnce("(dialogue publisher)",
                    "[ScriptPresentationHost] a dialogue line was resolved but no " +
                    "ScriptDialogueMessage publisher is wired, so nothing can draw it.");
                return;
            }

            _dialoguePublisher.Publish(new ScriptDialogueMessage(
                dialogueId, lineIndex, speaker, text, mod, push, visible: true, wait: wait));
        }

        // ── AS3 `landlevel` ───────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Script.as:281-295</c>: the land's name, the recommended level, the player's level and —
        /// when the land outranks the player — the "you are under-levelled" line.
        ///
        /// <para><b>The composition is the oracle's, and the two missing inputs are named rather than
        /// guessed.</b> The oracle reads <c>World.w.game.lands[val].dif</c> and
        /// <c>World.w.pers.level</c>. Neither is reachable from this seam, so the land's name and
        /// difficulty are shown and the comparison line is omitted — reported once, because a
        /// recommended-level panel that silently drops the warning is worse than one that says it
        /// cannot.</para>
        /// </summary>
        public void ShowLandLevel(string landId)
        {
            if (string.IsNullOrEmpty(landId)) return;

            string name = _text.Map(landId);
            if (string.IsNullOrEmpty(name) || name.StartsWith("*", StringComparison.Ordinal))
            {
                name = landId;
            }

            string body = name + "\n" + _text.Pip("recLevel");

            ReportOnce("landlevel",
                "[ScriptPresentationHost] `landlevel` shows the land's name but not its difficulty " +
                "comparison: the Oracle reads `game.lands[val].dif` and `pers.level` " +
                "(Script.as:281-295) and neither is reachable from the script seam yet.");

            ShowRawDialogue(body, mod: 1, push: false, wait: true);
        }

        // ── Control, HUD and the overlay ──────────────────────────────────────

        public void SetControlEnabled(bool enabled) =>
            _controlPublisher?.Publish(new ScriptControlLockMessage(enabled));

        public void SetGuiEnabled(bool enabled)
        {
            _guiVisible = enabled;
            _overlayPublisher?.Publish(new ScriptOverlayMessage(
                setsGui: true, guiVisible: enabled, setsAlpha: false, blackAlpha: 0f, blackFadeRate: 0f));
        }

        /// <summary>
        /// AS3 <c>black val</c> (<c>Script.as:371-379</c>) — sets the overlay's alpha and clears the
        /// camera's fade. <c>val</c> is clamped because the authored values are 0 and 1 and a stray
        /// value would otherwise leave the screen permanently dark.
        /// </summary>
        public void SetBlack(float alpha)
        {
            _blackAlpha = Mathf.Clamp01(alpha);
            _blackFade = 0f;
            _overlayPublisher?.Publish(new ScriptOverlayMessage(
                setsGui: false, guiVisible: _guiVisible, setsAlpha: true, blackAlpha: _blackAlpha,
                blackFadeRate: 0f));
        }

        /// <summary>
        /// AS3 <c>dblack val</c> (<c>Script.as:380-383</c>) — the fade <i>rate</i>. A negative value
        /// fades the overlay in (alpha → 0), a positive one out. The alpha is left where it is, which is
        /// why <c>beginBeg</c>'s <c>black 1</c> followed by <c>dblack -2</c> reads as "start black, then
        /// fade up".
        /// </summary>
        public void SetBlackFade(float rate)
        {
            _blackFade = rate;
            _overlayPublisher?.Publish(new ScriptOverlayMessage(
                setsGui: false, guiVisible: _guiVisible, setsAlpha: false, blackAlpha: 0f,
                blackFadeRate: rate));
        }

        // ── Commands the port has no consumer for yet ─────────────────────────

        public void PlayPlayerAnimation(string animation, bool flag) =>
            ReportUnconsumed("anim", animation);

        public void TurnPlayer(bool faceRight) => ReportUnconsumed("turn", faceRight ? "1" : "0");

        public void PlayMusic(string trackId, int loops) => ReportUnconsumed("music", trackId);

        public void ClearCameraIntro() => ReportUnconsumed("show", null);

        public void OpenPipPage(string page, string subPage) =>
            ReportUnconsumed("pip", page + " " + subPage);

        /// <summary>
        /// Reports a command the oracle has and the port cannot yet honour, <b>once per command</b>.
        ///
        /// <para>These are not no-ops in the oracle: <c>anim</c> plays a player animation, <c>turn</c>
        /// flips the character, <c>music</c> starts a track, <c>show</c> ends the camera's opening pan
        /// and <c>pip</c> opens a PipBuck page. Each needs a consumer the port does not have, and saying
        /// so once is the difference between "not implemented" and "implemented and broken".</para>
        /// </summary>
        private void ReportUnconsumed(string command, string detail)
        {
            ReportOnce(command,
                $"[ScriptPresentationHost] `{command}`" +
                (string.IsNullOrEmpty(detail) ? string.Empty : $" '{detail}'") +
                " has no consumer in the port yet, so it did nothing. The Oracle's equivalent is in " +
                "Script.as:337-419; add a subscriber rather than a second switch.");
        }

        private void ReportOnce(string key, string message)
        {
            if (_reported.Add(key)) _report(message);
        }
    }
}
