namespace PFE.Core.Messages
{
    /// <summary>
    /// The cutscene dialogue surface — one message type for the oracle's one function.
    ///
    /// <para><b>Why a single message rather than four.</b> AS3 routes <c>dial</c>, <c>dialog</c>,
    /// <c>inform</c> and <c>landlevel</c> through <c>GUI.dialText(id, n, down, wait)</c>
    /// (<c>GUI.as:1741-1905</c>). The four differ only in where the text came from and which window it
    /// lands in (<c>@mod</c>), so four message types would be four ways to say the same thing and four
    /// subscribers to keep in step.</para>
    ///
    /// <para><b>The speaker is carried, not baked into the text.</b> <c>begDial</c>'s rows are
    /// <c>p="lp1"</c> — Littlepip — and the oracle picks the portrait with
    /// <c>dial.portret.gotoAndStop(sp)</c> (<c>GUI.as:1833-1846</c>). A view that only received a string
    /// could show the words but not who said them, which is the difference between a message and a
    /// caption.</para>
    /// </summary>
    public struct ScriptDialogueMessage
    {
        /// <summary>The <c>txt</c> table id (<c>begDial</c>), or <c>null</c> for an inline body.</summary>
        public string DialogueId;

        /// <summary>The row shown — AS3 <c>dial_n</c>. <c>-1</c> when there is no row walk.</summary>
        public int LineIndex;

        /// <summary>The row's <c>p</c> attribute (<c>lp1</c>…<c>lp4</c>, <c>pip</c>, <c>mtn</c>,
        /// <c>term</c>), or <c>null</c> for unattributed narration.</summary>
        public string Speaker;

        /// <summary>The resolved text, with <c>@n</c> key names already substituted.</summary>
        public string Text;

        /// <summary>AS3 <c>@mod</c>: 0 = the dialogue window, ≥1 = the information panel, 2 = the panel
        /// with an OK button.</summary>
        public int Mod;

        /// <summary>AS3 <c>@push &gt; 0</c> — append to the panel's current text instead of replacing
        /// it.</summary>
        public bool Push;

        /// <summary>False clears the window — the oracle's <c>dialText()</c> with no id
        /// (<c>Script.as:210-213</c>).</summary>
        public bool Visible;

        /// <summary>Whether the script is blocked on the player while this is up.</summary>
        public bool Wait;

        public ScriptDialogueMessage(string dialogueId, int lineIndex, string speaker, string text,
                                     int mod, bool push, bool visible, bool wait)
        {
            DialogueId = dialogueId;
            LineIndex = lineIndex;
            Speaker = speaker;
            Text = text;
            Mod = mod;
            Push = push;
            Visible = visible;
            Wait = wait;
        }

        /// <summary>A "hide the window" instance — <c>dialText()</c>.</summary>
        public static ScriptDialogueMessage Hidden =>
            new ScriptDialogueMessage(null, -1, null, null, 0, false, false, false);
    }

    /// <summary>
    /// AS3 <c>control off</c> / <c>control on</c> (<c>Script.as:239-246</c> →
    /// <c>UnitPlayer.controlOff/controlOn</c>): the cutscene input lock.
    ///
    /// <para><b>An event, not a property, because the lock is held and released by name.</b> A script
    /// says "off" at the top of a cutscene and "on" at the bottom, and the two can be separated by
    /// seconds and by a dialogue walk. A subscriber that only saw the current value could not tell a
    /// release it had already processed from one it had missed.</para>
    /// </summary>
    public struct ScriptControlLockMessage
    {
        /// <summary>False locks the player out of movement, jumping and interaction.</summary>
        public bool Enabled;

        public ScriptControlLockMessage(bool enabled)
        {
            Enabled = enabled;
        }
    }

    /// <summary>
    /// AS3 <c>gui off</c> / <c>gui on</c> (<c>Script.as:384-391</c> → <c>GUI.setGui</c>) — the HUD bar's
    /// visibility, and the blackout overlay behind it.
    ///
    /// <para><c>black val</c> (<c>Script.as:371-379</c>) sets the overlay's alpha directly and clears the
    /// camera's fade; <c>dblack val</c> (<c>:380-383</c>) sets the fade <i>rate</i> and leaves the alpha
    /// where it is. They are carried together because they drive the same overlay and a script routinely
    /// sets one right after the other — <c>beginBeg</c> opens with <c>black 1</c> and follows it with
    /// <c>dblack -2</c>.</para>
    /// </summary>
    public struct ScriptOverlayMessage
    {
        /// <summary>The HUD bar's visibility.</summary>
        public bool GuiVisible;

        /// <summary>The blackout overlay's alpha, 0…1.</summary>
        public float BlackAlpha;

        /// <summary>The fade rate, in alpha per frame. Negative fades in (alpha → 0), positive fades
        /// out. Zero means "hold".</summary>
        public float BlackFadeRate;

        /// <summary>Whether this message carries a new alpha at all. <c>dblack</c> alone does not.</summary>
        public bool SetsAlpha;

        /// <summary>Whether this message carries a new GUI visibility. <c>black</c> alone does not.</summary>
        public bool SetsGui;

        public ScriptOverlayMessage(bool setsGui, bool guiVisible, bool setsAlpha, float blackAlpha,
                                    float blackFadeRate)
        {
            SetsGui = setsGui;
            GuiVisible = guiVisible;
            SetsAlpha = setsAlpha;
            BlackAlpha = blackAlpha;
            BlackFadeRate = blackFadeRate;
        }
    }
}
