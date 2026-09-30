using System.Text;
using PFE.Systems.Map.Rendering;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// The debug-overlay console surface, exposed to Lua as the global <c>collider</c>:
    /// <c>collider:On("doors,triggers")</c>, <c>collider:Tiles("shelf")</c>, <c>collider:Probe()</c>.
    ///
    /// <para><b>One list of channels, each independent.</b> Every overlay in the game — tile colliders,
    /// unit colliders, doors, area triggers, room transitions, map objects, and the four text readouts
    /// (tile query, room streaming, object pool, SIM CLOCK) — is a bit in
    /// <see cref="DebugOverlayChannel"/>. <c>col on doors,triggers</c> adds two and changes nothing
    /// else; <c>col on</c> adds all; <c>col off</c> removes all. Tiles and units in particular are
    /// independent, so they are on together or apart in any combination.</para>
    ///
    /// <para><b>Why this is a separate object and not more methods on <c>player</c>.</b> The subject is
    /// the world and the HUD, not the player; and the console's non-Lua shortcuts (<c>col on room</c>)
    /// are a thin wrapper over exactly these methods, so the shortcut and the Lua form cannot behave
    /// differently.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> As with the other command objects, the wiring is private and
    /// only the command methods below are public, because MoonSharp's default reflection interop
    /// exposes public members only.</para>
    ///
    /// <para><b>Not in AS3.</b> The oracle has no console and no overlay system. The methods here are
    /// instrumentation; the values they report come from Unity's own <c>Collider2D</c> and
    /// <c>SpriteRenderer</c> bounds and from the rooms' own geometry, which is why they are worth
    /// trusting — nothing is re-derived.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleColliderCommands
    {
        // ── The master list ──────────────────────────────────────────────────

        /// <summary>
        /// Turn overlays on. <paramref name="channels"/> is a comma-separated list of channel names,
        /// or empty for all of them. Every named channel is added; nothing is turned off.
        /// </summary>
        public string On(string channels)
        {
            if (!TryParse(channels, out DebugOverlayChannel parsed, out string error)) return error;

            PfeDebugSettings settings = DebugOverlays.Settings;
            if (settings == null) return NoSettings;

            // OR, never assignment: this is the operation that guarantees independence. Turning
            // `doors` on must not silently take `units` off.
            settings.EnabledOverlays |= parsed;

            Refresh();
            return $"[collider] on: {DebugOverlayChannels.Format(settings.EnabledOverlays)}\n" + Status();
        }

        /// <summary>
        /// Turn overlays off. Empty means every overlay. The sub-filters (which tile types, which
        /// units) are left alone, so <c>col on tiles</c> comes back exactly as it was.
        /// </summary>
        public string Off(string channels)
        {
            if (!TryParse(channels, out DebugOverlayChannel parsed, out string error)) return error;

            PfeDebugSettings settings = DebugOverlays.Settings;
            if (settings == null) return NoSettings;

            // AND with the complement, for the same reason: this must only clear the bits named.
            settings.EnabledOverlays &= ~parsed;

            Refresh();
            return $"[collider] on: {DebugOverlayChannels.Format(settings.EnabledOverlays)}\n" + Status();
        }

        /// <summary>Every channel, whether it is on, and what it draws.</summary>
        public string Status()
        {
            PfeDebugSettings settings = DebugOverlays.Settings;
            if (settings == null) return NoSettings;

            DebugOverlayChannel channels = settings.EnabledOverlays;

            var sb = new StringBuilder();
            sb.Append("[collider] ").Append(channels == DebugOverlayChannel.None
                ? "nothing on (default — the game and nothing else)"
                : "on: " + DebugOverlayChannels.Format(channels));
            sb.Append('\n').Append(DebugOverlayChannels.DescribeAll(channels));

            if ((channels & DebugOverlayChannel.Tiles) != 0)
            {
                sb.Append($"\n  tiles sub-filter: {ColliderDebugFilters.Format(settings.TileColliderFilter)}");
            }

            if ((channels & DebugOverlayChannel.Units) != 0)
            {
                sb.Append($"\n  units sub-filter: {ColliderDebugFilters.Format(settings.UnitColliderFilter)}");
            }

            sb.Append("\n  live counts: ").Append(ColliderDebugOverlay.DescribeState());

            return sb.ToString();
        }

        // ── Channel-scoped shorthands ────────────────────────────────────────

        /// <summary>
        /// Show tile colliders, optionally restricted to physics types. <paramref name="spec"/> is a
        /// comma-separated list drawn from <c>air</c>, <c>wall</c>, <c>platform</c> (aliases
        /// <c>shelf</c>, <c>catwalk</c>), <c>stair</c> (alias <c>slope</c>), or <c>all</c> /
        /// <c>off</c>. Empty means all.
        /// </summary>
        public string Tiles(string spec)
        {
            if (!ColliderDebugFilters.TryParseTiles(spec, out ColliderDebugTileFilter filter, out string error))
                return "[collider] " + error;

            ColliderDebugOverlay.SetTileFilter(filter);
            return $"[collider] tiles={ColliderDebugFilters.Format(filter)}\n" + Status();
        }

        /// <summary>
        /// Show unit colliders. <paramref name="spec"/> is a comma-separated list drawn from
        /// <c>player</c>, <c>npc</c> (aliases <c>nps</c>, <c>enemy</c>), or <c>all</c> / <c>off</c>.
        /// Empty means all.
        /// </summary>
        public string Units(string spec)
        {
            if (!ColliderDebugFilters.TryParseUnits(spec, out ColliderDebugUnitFilter filter, out string error))
                return "[collider] " + error;

            ColliderDebugOverlay.SetUnitFilter(filter);
            return $"[collider] units={ColliderDebugFilters.Format(filter)}\n" + Status();
        }

        /// <summary>Hide every overlay, keeping the sub-filters so they come back the same.</summary>
        public string AllOff()
        {
            ColliderDebugOverlay.DisableAll();
            return "[collider] Every overlay off.";
        }

        // ── Geometry queries ────────────────────────────────────────────────

        /// <summary>
        /// Report what is geometrically under the player's feet: the tile the <i>colliders</i> say is
        /// there, the tile the <i>sprites</i> say is there, and the gap in game pixels.
        ///
        /// <para>This is the numeric form of the same question the overlay draws. It is here because
        /// the gap has to be compared against AS3's porog allowance (10 px for a unit), and a number
        /// can be compared while a picture can only be eyeballed.</para>
        /// </summary>
        public string Probe()
        {
            return ColliderDebugOverlay.Probe();
        }

        /// <summary>
        /// Every tile's collider top against the VISIBLE top of its sprite, grouped by physics type.
        ///
        /// <para>This is <see cref="Probe"/>'s question asked of the whole room, and it is the
        /// measurement the shelf/catwalk investigation could not make from a screenshot: it samples
        /// the texture's alpha rather than <c>SpriteRenderer.bounds</c>, so a plank that does not fill
        /// its cell shows up as a number. See
        /// <c>docs/Research/SHELF_CATWALK_DIAGNOSIS_2026-09-30.md</c> §5.</para>
        /// </summary>
        public string Gaps()
        {
            return ColliderDebugOverlay.Gaps();
        }

        /// <summary>Usage summary, so the console's <c>help</c> has a single source for it.</summary>
        public string Help()
        {
            var sb = new StringBuilder();
            sb.Append(DebugOverlayChannels.Usage());
            sb.Append("\n  col tiles <t>      - tile colliders; t = ").Append(ColliderDebugFilters.TileUsage);
            sb.Append("\n  col units <t>      - unit colliders; t = ").Append(ColliderDebugFilters.UnitUsage);
            sb.Append("\n  col probe          - what is under the player's feet, in game pixels");
            sb.Append("\n  col off            - every overlay off");
            return sb.ToString();
        }

        // ── Internals ───────────────────────────────────────────────────────

        private const string NoSettings = "[collider] PfeDebugSettings not found in Resources.";

        private static bool TryParse(string channels, out DebugOverlayChannel parsed, out string error)
        {
            if (DebugOverlayChannels.TryParse(channels, out parsed, out string message))
            {
                error = null;
                return true;
            }

            error = "[collider] " + message + "\n" + DebugOverlayChannels.Usage();
            return false;
        }

        /// <summary>
        /// Redraw immediately rather than waiting for the overlay's next refresh tick. The tick is
        /// 0.2 s, which is short — but a console reply that says "on" while the screen still shows the
        /// old frame is the kind of half-truth that wastes an afternoon.
        /// </summary>
        private static void Refresh()
        {
            ColliderDebugOverlay.EnsureInstance().RefreshNow();
        }
    }
}
