using System;
using System.Text;
using PFE.Systems.Map;

namespace PFE.Core
{
    /// <summary>
    /// Which tile colliders the collider-debug overlay draws. A flags set rather than an enum
    /// because the useful question is "show me the shelves and the stairs but not the walls",
    /// and because the console wants to accept a list.
    /// </summary>
    [Flags]
    public enum ColliderDebugTileFilter
    {
        None = 0,
        Air = 1 << 0,
        Wall = 1 << 1,
        Platform = 1 << 2,
        Stair = 1 << 3,
        All = Air | Wall | Platform | Stair
    }

    /// <summary>
    /// Which unit colliders the collider-debug overlay draws. AS3 has no such concept — this is a
    /// port-side debugging aid, so the split is the one that is useful (is it the player, or is it
    /// everyone else) rather than one the oracle implies.
    /// </summary>
    [Flags]
    public enum ColliderDebugUnitFilter
    {
        None = 0,
        Player = 1 << 0,
        Npc = 1 << 1,
        All = Player | Npc
    }

    /// <summary>
    /// Parsing and formatting for the collider-debug filters, shared by the developer console and
    /// the overlay so that the two can never drift apart.
    ///
    /// <para><b>Why a parse failure is an error and not a fallback.</b> A filter parser that
    /// silently ignores a token it does not recognise turns a typo into "the overlay is on and
    /// shows nothing", which is indistinguishable from "the tile you are looking at really has no
    /// collider" — the exact conclusion this tool exists to support. So an unknown token fails and
    /// names itself, and the caller reports it.</para>
    /// </summary>
    public static class ColliderDebugFilters
    {
        public const string TileUsage =
            "all | off | a comma-separated list of air, wall, platform (shelf/catwalk), stair (slope)";

        public const string UnitUsage =
            "all | off | a comma-separated list of player, npc (nps/enemy)";

        // ── Tiles ────────────────────────────────────────────────────────────

        /// <summary>
        /// Parse a tile filter. Empty means <see cref="ColliderDebugTileFilter.All"/>, so a bare
        /// <c>tiles</c> reads as "show me everything".
        /// </summary>
        public static bool TryParseTiles(string text, out ColliderDebugTileFilter filter, out string error)
        {
            filter = ColliderDebugTileFilter.All;
            error = null;

            if (string.IsNullOrWhiteSpace(text)) return true;

            string trimmed = text.Trim();
            if (IsOffToken(trimmed)) { filter = ColliderDebugTileFilter.None; return true; }
            if (IsAllToken(trimmed)) { filter = ColliderDebugTileFilter.All; return true; }

            ColliderDebugTileFilter accumulated = ColliderDebugTileFilter.None;

            foreach (string token in SplitTokens(trimmed))
            {
                if (IsAllToken(token)) { accumulated = ColliderDebugTileFilter.All; continue; }
                if (IsOffToken(token)) continue;

                switch (Normalize(token))
                {
                    // Note: "none" is not repeated here. IsOffToken already consumed it above, so a
                    // case for it in this switch would be unreachable.
                    case "air":
                    case "empty":
                        accumulated |= ColliderDebugTileFilter.Air;
                        break;

                    case "wall":
                    case "solid":
                        accumulated |= ColliderDebugTileFilter.Wall;
                        break;

                    // "shelf" is the name the original game and the user use for the one-way
                    // catwalk tile (AS3 Tile.shelf, the '-' family), which decodes to
                    // TilePhysicsType.Platform. Accepting the word matters more than the
                    // canonical name here — it is what someone will type.
                    case "platform":
                    case "plat":
                    case "shelf":
                    case "shelve":
                    case "shelves":
                    case "catwalk":
                    case "balcony":
                        accumulated |= ColliderDebugTileFilter.Platform;
                        break;

                    case "stair":
                    case "stairs":
                    case "slope":
                    case "ramp":
                        accumulated |= ColliderDebugTileFilter.Stair;
                        break;

                    default:
                        error = $"Unknown tile type '{token}'. Expected {TileUsage}.";
                        filter = ColliderDebugTileFilter.All;
                        return false;
                }
            }

            filter = accumulated;
            return true;
        }

        /// <summary>Render a tile filter the way <see cref="TryParseTiles"/> accepts it back.</summary>
        public static string Format(ColliderDebugTileFilter filter)
        {
            if (filter == ColliderDebugTileFilter.None) return "off";
            if (filter == ColliderDebugTileFilter.All) return "all";

            var sb = new StringBuilder();
            AppendTileToken(sb, filter, ColliderDebugTileFilter.Air, "air");
            AppendTileToken(sb, filter, ColliderDebugTileFilter.Wall, "wall");
            AppendTileToken(sb, filter, ColliderDebugTileFilter.Platform, "platform");
            AppendTileToken(sb, filter, ColliderDebugTileFilter.Stair, "stair");
            return sb.ToString();
        }

        /// <summary>
        /// Does <paramref name="type"/> survive <paramref name="filter"/>?
        ///
        /// <para>The <c>default</c> arm returns false deliberately. A new <see cref="TilePhysicsType"/>
        /// member that nobody added a flag for should be visibly absent from the overlay, not
        /// silently folded into one of the four — a filter that quietly matches the wrong thing is
        /// how a debugging tool starts lying.</para>
        /// </summary>
        public static bool Matches(ColliderDebugTileFilter filter, TilePhysicsType type)
        {
            switch (type)
            {
                case TilePhysicsType.Air: return (filter & ColliderDebugTileFilter.Air) != 0;
                case TilePhysicsType.Wall: return (filter & ColliderDebugTileFilter.Wall) != 0;
                case TilePhysicsType.Platform: return (filter & ColliderDebugTileFilter.Platform) != 0;
                case TilePhysicsType.Stair: return (filter & ColliderDebugTileFilter.Stair) != 0;
                default: return false;
            }
        }

        // ── Units ────────────────────────────────────────────────────────────

        /// <summary>
        /// Parse a unit filter. Empty means <see cref="ColliderDebugUnitFilter.All"/>.
        /// </summary>
        public static bool TryParseUnits(string text, out ColliderDebugUnitFilter filter, out string error)
        {
            filter = ColliderDebugUnitFilter.All;
            error = null;

            if (string.IsNullOrWhiteSpace(text)) return true;

            string trimmed = text.Trim();
            if (IsOffToken(trimmed)) { filter = ColliderDebugUnitFilter.None; return true; }
            if (IsAllToken(trimmed)) { filter = ColliderDebugUnitFilter.All; return true; }

            ColliderDebugUnitFilter accumulated = ColliderDebugUnitFilter.None;

            foreach (string token in SplitTokens(trimmed))
            {
                if (IsAllToken(token)) { accumulated = ColliderDebugUnitFilter.All; continue; }
                if (IsOffToken(token)) continue;

                switch (Normalize(token))
                {
                    case "player":
                    case "pc":
                    case "hero":
                        accumulated |= ColliderDebugUnitFilter.Player;
                        break;

                    // "nps" is what the user typed, so it is a first-class spelling and not a
                    // tolerated typo.
                    case "npc":
                    case "nps":
                    case "enemy":
                    case "enemies":
                    case "unit":
                    case "units":
                    case "mob":
                    case "mobs":
                        accumulated |= ColliderDebugUnitFilter.Npc;
                        break;

                    default:
                        error = $"Unknown unit type '{token}'. Expected {UnitUsage}.";
                        filter = ColliderDebugUnitFilter.All;
                        return false;
                }
            }

            filter = accumulated;
            return true;
        }

        /// <summary>Render a unit filter the way <see cref="TryParseUnits"/> accepts it back.</summary>
        public static string Format(ColliderDebugUnitFilter filter)
        {
            if (filter == ColliderDebugUnitFilter.None) return "off";
            if (filter == ColliderDebugUnitFilter.All) return "all";

            var sb = new StringBuilder();
            AppendUnitToken(sb, filter, ColliderDebugUnitFilter.Player, "player");
            AppendUnitToken(sb, filter, ColliderDebugUnitFilter.Npc, "npc");
            return sb.ToString();
        }

        /// <summary>Does a unit survive the filter? <paramref name="isPlayer"/> is UnitController.IsPlayer.</summary>
        public static bool Matches(ColliderDebugUnitFilter filter, bool isPlayer)
        {
            return isPlayer
                ? (filter & ColliderDebugUnitFilter.Player) != 0
                : (filter & ColliderDebugUnitFilter.Npc) != 0;
        }

        // ── Shared ───────────────────────────────────────────────────────────

        private static void AppendTileToken(StringBuilder sb, ColliderDebugTileFilter filter,
                                            ColliderDebugTileFilter flag, string name)
        {
            if ((filter & flag) == 0) return;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(name);
        }

        private static void AppendUnitToken(StringBuilder sb, ColliderDebugUnitFilter filter,
                                            ColliderDebugUnitFilter flag, string name)
        {
            if ((filter & flag) == 0) return;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(name);
        }

        private static bool IsAllToken(string token)
        {
            string t = Normalize(token);
            return t == "all" || t == "*" || t == "everything";
        }

        private static bool IsOffToken(string token)
        {
            // "none" is here, and the tiles switch relies on it: the comment there says a `case
            // "none"` would be unreachable because this method already consumed it. That was not
            // true until this line was added — `col tiles none` used to fall through to the unknown
            // token error. DebugOverlayChannels.IsOffToken has always accepted "none", so the two
            // parsers disagreed about a word people actually type.
            string t = Normalize(token);
            return t == "off" || t == "none" || t == "0" || t == "false"
                || t == "hide" || t == "clear";
        }

        /// <summary>
        /// Case and surrounding whitespace are not information here. C# string <c>switch</c> is
        /// case-sensitive, so without this <c>col tiles Shelf</c> fails with "Unknown tile type" and
        /// reads as a typo the user cannot see.
        /// </summary>
        private static string Normalize(string token)
        {
            return token?.Trim().ToLowerInvariant();
        }

        private static string[] SplitTokens(string text)
        {
            return text.Split(new[] { ',', ';', '+', '|', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
