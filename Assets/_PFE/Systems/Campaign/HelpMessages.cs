namespace PFE.Systems.Campaign
{
    /// <summary>
    /// The "tutorial messages" option — AS3 <c>World.w.helpMess</c> (<c>World.as:218</c>), the flag
    /// <c>Invent.take</c> tests before showing a one-time popup (<c>Invent.as:1293</c>).
    ///
    /// <para><b>Default <c>true</c>, and that is the Oracle's own start:</b>
    /// <c>public var helpMess:Boolean = true;</c>. It is then loaded from the config document
    /// (<c>World.as:621 this.helpMess = this.configObj.data.help</c>) and written back on save
    /// (<c>:1707</c>). So in a default run the tutorial popups <b>do</b> fire — a port that defaulted
    /// this to <c>false</c> would silently drop every one of them.</para>
    ///
    /// <para><b>Why a static rather than a field on <c>GameSettings</c>.</b> Two reasons, both
    /// practical. The readers cannot inject: the popup is fired from <c>WorldItemPickup</c>, an
    /// <c>AddComponent</c>-built component, which is the same limitation <c>GlobalDifficulty</c>
    /// documents. And adding a serialised <c>bool</c> to <c>GameSettings</c> would <b>default to
    /// false on the existing asset</b> — Unity deserialises a missing key as the type's default, not as
    /// the field initialiser — so the flag would read "off" until someone re-saved the asset, which is
    /// precisely the silent-drop failure this default exists to avoid.</para>
    ///
    /// <para><b>The port has no settings screen yet</b>, so nothing calls the setter outside a test.
    /// That is the gap, not the design: the Oracle's load/save pair is the shape a settings screen
    /// would fill in, and <see cref="Reset"/> exists so a fixture can put the default back.</para>
    /// </summary>
    public static class HelpMessages
    {
        /// <summary><c>World.as:218</c> — <c>helpMess = true</c>.</summary>
        public const bool Default = true;

        private static bool _enabled = Default;

        /// <summary>
        /// Whether one-time tutorial popups are shown. Set from the settings screen (not built yet) or
        /// from the save's config document.
        /// </summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>Restores the Oracle's default. For fixtures and for a new game.</summary>
        public static void Reset() => _enabled = Default;
    }
}
