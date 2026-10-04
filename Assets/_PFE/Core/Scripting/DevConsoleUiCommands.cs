using System;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// The developer console's own UI surface, exposed to Lua as the global <c>ui</c>:
    /// <c>ui:SetButtons(false)</c>.
    ///
    /// <para><b>Why this is not a <c>col</c> channel.</b> The quick-action button grid is the console's
    /// chrome, not a world visualisation. Folding it into <see cref="DebugOverlayChannel"/> would make
    /// <c>col on all</c> show and hide the console's own buttons, and would make "the game draws nothing
    /// by default" mean something about a panel only visible while the console is open. So it is a plain
    /// flag, like <c>showEntityIdOverlay</c> — a behaviour/UI toggle, not an overlay bit.</para>
    ///
    /// <para><b>One value, three front ends.</b> This writes the same
    /// <see cref="PfeDebugSettings.ShowConsoleQuickButtons"/> field the Inspector edits and the console's
    /// title-bar toggle flips, so the three cannot disagree. Nothing is cached here: the value is read
    /// from the asset on every call, which is what lets a console write take effect on the next frame.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> Only the command methods below are public; the wiring is
    /// private, as with the other command objects, because MoonSharp's default reflection interop exposes
    /// public members only.</para>
    ///
    /// <para><b>Not in AS3.</b> The oracle has no console and no button grid; this is instrumentation.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleUiCommands
    {
        private readonly Func<PfeDebugSettings> _settingsSource;

        /// <summary>Production constructor: read the live settings asset, as every other command object does.</summary>
        public DevConsoleUiCommands() : this(null)
        {
        }

        /// <summary>
        /// Test seam for the settings source.
        ///
        /// <para><b>Why it exists.</b> <c>DebugOverlays.Settings</c> is a <c>Resources.Load</c>, and the
        /// offline test wall cannot execute an ECall — so a test that goes through the default source
        /// cannot be run without Unity, and a test that cannot be run proves nothing. Injecting the
        /// source also means the test never touches the project's real settings asset.</para>
        ///
        /// <para>A null source falls back to the live asset, so production code cannot accidentally get
        /// a command object that reports "no settings" because of a wiring slip.</para>
        /// </summary>
        public DevConsoleUiCommands(Func<PfeDebugSettings> settingsSource)
        {
            _settingsSource = settingsSource ?? (() => DebugOverlays.Settings);
        }

        /// <summary>Whether the quick-action button grid is currently shown.</summary>
        public string Status()
        {
            PfeDebugSettings settings = _settingsSource();
            if (settings == null) return NoSettings;

            return $"[ui] console buttons: {(settings.ShowConsoleQuickButtons ? "on" : "off")}";
        }

        /// <summary>
        /// Show or hide the quick-action button grid. The console itself is unaffected — with the grid
        /// hidden it is a plain REPL, which is the point: the grid is a convenience, not a dependency.
        /// </summary>
        public string SetButtons(bool show)
        {
            PfeDebugSettings settings = _settingsSource();
            if (settings == null) return NoSettings;

            settings.ShowConsoleQuickButtons = show;
            return Status();
        }

        /// <summary>Usage summary, so the console's <c>help</c> has a single source for it.</summary>
        public string Help()
        {
            return "ui              - report whether the console button grid is shown\n" +
                   "ui buttons      - same as `ui`\n" +
                   "ui buttons on   - show the quick-action buttons\n" +
                   "ui buttons off  - hide them (the console stays a REPL)\n" +
                   "Lua: ui:SetButtons(true | false)";
        }

        private const string NoSettings = "[ui] PfeDebugSettings not found in Resources.";
    }
}
