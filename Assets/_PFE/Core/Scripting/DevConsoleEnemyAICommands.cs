using System.Text;
using PFE.Systems.Map.Rendering;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// The enemy-AI overlay's console surface, exposed to Lua as the global <c>ai</c>:
    /// <c>ai</c> (status), <c>ai vision,los</c>, <c>ai on senses</c>, <c>ai next</c>, <c>ai off</c>.
    ///
    /// <para><b>Why a separate object rather than more methods on <c>collider</c>.</b> The channel is
    /// reachable from <c>col on ai</c> — it is one bit of <see cref="DebugOverlayChannel"/> like every
    /// other overlay — but the <i>subject</i> here is one unit's perception, with a selection and a
    /// sub-filter of its own. Folding that into <c>collider</c> would make the verb mean two different
    /// things depending on its second word.</para>
    ///
    /// <para><b>A bare verb is status, never a toggle.</b> The same contract as <c>col</c>/<c>prof</c>/
    /// <c>rpg</c>/<c>eff</c>/<c>spell</c>/<c>inv</c>: a verb that flips state on a typo is one mistyped
    /// character away from an overlay appearing or vanishing, and the reply is the only thing that would
    /// have told you.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> As with the other command objects, the wiring is private and
    /// only the command methods below are public, because MoonSharp's default reflection interop exposes
    /// public members only.</para>
    ///
    /// <para><b>Not in AS3.</b> The oracle has no console and no overlay system. The methods here are
    /// instrumentation; the values they report come from the brain's own live state, which is why they
    /// are worth trusting.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleEnemyAICommands
    {
        /// <summary>Turn the overlay on, optionally setting which parts are drawn.</summary>
        public string On(string spec)
        {
            if (!TryParseFilter(spec, out EnemyAIDebugFilter filter, out string error)) return error;

            EnemyAIDebugOverlay.SetFilter(filter);
            return $"[ai] on: {EnemyAIDebugFilters.Format(filter)}\n" + Status();
        }

        /// <summary>Turn the overlay off. The sub-filter is kept, so <c>ai on</c> comes back as it was.</summary>
        public string Off()
        {
            PfeDebugSettings settings = DebugOverlays.Settings;
            if (settings == null) return NoSettings;

            settings.SetOverlay(DebugOverlayChannel.EnemyAI, false);
            return "[ai] off.";
        }

        /// <summary>
        /// Which parts are drawn. <paramref name="spec"/> is a comma-separated list drawn from
        /// <c>vision</c>, <c>hearing</c>, <c>close</c>, <c>los</c>, <c>target</c>, <c>states</c>, or
        /// <c>senses</c> for vision+hearing+close, or <c>all</c> / <c>off</c>. Empty means all.
        /// </summary>
        public string Parts(string spec)
        {
            if (!TryParseFilter(spec, out EnemyAIDebugFilter filter, out string error)) return error;

            EnemyAIDebugOverlay.SetFilter(filter);
            return $"[ai] parts={EnemyAIDebugFilters.Format(filter)}\n" + Status();
        }

        /// <summary>Step the selection forward. Wraps.</summary>
        public string Next()
        {
            EnemyAIDebugOverlay.CycleSelection(1);
            return Status();
        }

        /// <summary>Step the selection back. Wraps.</summary>
        public string Prev()
        {
            EnemyAIDebugOverlay.CycleSelection(-1);
            return Status();
        }

        /// <summary>Drop the selection.</summary>
        public string SelectNone()
        {
            EnemyAIDebugOverlay.ClearSelection();
            return "[ai] selection cleared.";
        }

        /// <summary>
        /// The overlay's state, the filter, the selection, and the selected unit's own state — one
        /// screen, because "which enemy is selected" and "what is that enemy doing" are one question.
        /// </summary>
        public string Status()
        {
            PfeDebugSettings settings = DebugOverlays.Settings;
            if (settings == null) return NoSettings;

            DebugOverlayChannel channels = settings.EnabledOverlays;

            var sb = new StringBuilder();
            sb.Append("[ai] ")
              .Append((channels & DebugOverlayChannel.EnemyAI) != 0 ? "ON" : "off (F3 to toggle)")
              .Append("  parts=")
              .Append(EnemyAIDebugFilters.Format(settings.EnemyAIFilter));

            if ((channels & DebugOverlayChannel.EnemyAI) != 0
                && settings.EnemyAIFilter == EnemyAIDebugFilter.None)
            {
                // On and empty is indistinguishable from broken, so say which it is.
                sb.Append("  <-- ON with no parts: nothing is drawn");
            }

            sb.Append('\n').Append(EnemyAIDebugFilters.DescribeAll(settings.EnemyAIFilter));

            sb.Append("\n  selected: ").Append(EnemyAIDebugOverlay.DescribeSelected());

            return sb.ToString();
        }

        /// <summary>Usage summary, so the console's <c>help</c> has a single source for it.</summary>
        public string Help()
        {
            var sb = new StringBuilder();
            sb.Append("[ai] enemy-AI perception overlay (channel `ai`, hotkey F3)");
            sb.Append("\n  ai                 - status, the parts list, and the selected unit's state");
            sb.Append("\n  ai <parts>         - which parts to draw; ").Append(EnemyAIDebugFilters.Usage);
            sb.Append("\n  ai on <parts>      - turn it on (empty = all parts)");
            sb.Append("\n  ai off             - turn it off, keeping the parts list");
            sb.Append("\n  ai next | prev     - cycle the selected unit (also the ] and [ keys)");
            sb.Append("\n  ai none            - drop the selection");
            sb.Append("\n  col on ai          - the same channel from the collider verb");
            return sb.ToString();
        }

        // ── Internals ───────────────────────────────────────────────────────

        private const string NoSettings = "[ai] PfeDebugSettings not found in Resources.";

        private static bool TryParseFilter(string spec, out EnemyAIDebugFilter filter, out string error)
        {
            if (EnemyAIDebugFilters.TryParse(spec, out filter, out string message))
            {
                error = null;
                return true;
            }

            error = "[ai] " + message;
            return false;
        }
    }
}
