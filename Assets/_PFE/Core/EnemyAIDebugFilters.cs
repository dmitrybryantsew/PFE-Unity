using System;
using System.Text;

namespace PFE.Core
{
    /// <summary>
    /// Which parts of the enemy-AI overlay are drawn. A flags set rather than an enum for the same
    /// reason <see cref="ColliderDebugTileFilter"/> is one: the useful question is "show me what it
    /// hears and where it last heard it, but not the vision cone", and the console wants to accept a
    /// list.
    ///
    /// <para><b>Every flag answers a question about one sense.</b> The overlay exists to make the
    /// difference between the three ways an enemy can know something visible at a glance, because they
    /// are indistinguishable in play and have completely different causes:</para>
    /// <list type="bullet">
    /// <item><see cref="Vision"/> — it can <i>see</i> the target (in range, in front of it, and
    /// unobstructed);</item>
    /// <item><see cref="Hearing"/> — it can <i>hear</i> the target without seeing it;</item>
    /// <item><see cref="CloseProximity"/> — the target is close enough to be detected regardless of
    /// which way the enemy is facing (<c>AS3 detecting</c>).</item>
    /// </list>
    /// <para>An enemy that is chasing is not evidence of which of the three fired, and an enemy that is
    /// <i>not</i> chasing while standing next to the player is exactly the bug this overlay is for.</para>
    /// </summary>
    [Flags]
    public enum EnemyAIDebugFilter
    {
        None = 0,

        /// <summary>The forward vision cone: the range arc clipped to the facing hemisphere, plus the
        /// eye point the ray is cast from.</summary>
        Vision = 1 << 0,

        /// <summary>The hearing radius — <c>EnemySensors.HearingRadiusPixels(loudestCandidateNoise)</c>,
        /// i.e. <c>noise × ear × earMult</c>. Drawn as a full circle, because hearing has no facing term.
        ///
        /// <para>It is <b>noise-dependent, so it collapses when nothing is being loud.</b> A unit whose
        /// candidate is standing still has a radius of 0 and draws no circle at all — which is the
        /// correct picture, and the reason this circle is no longer a fixed 320 px ring. The panel
        /// prints the radius and the noise it came from, so an absent circle reads as "inaudible"
        /// rather than "the overlay is broken".</para></summary>
        Hearing = 1 << 1,

        /// <summary>The close-proximity bubble (<c>EnemySensors.CloseProximityPixels</c>) inside which
        /// a target is detected regardless of facing.</summary>
        CloseProximity = 1 << 2,

        /// <summary>The ray from the enemy's eye to its target, coloured by whether the tile query
        /// reported it obstructed. This is the one flag that explains a <i>failed</i> detection.</summary>
        LineOfSight = 1 << 3,

        /// <summary>The current target's marker, and the last-known-position marker the blackboard
        /// carries while the target is out of sight.</summary>
        Target = 1 << 4,

        /// <summary>A one-line state label above every enemy in view, not just the selected one — so a
        /// room full of enemies can be read in one screenshot.</summary>
        States = 1 << 5,

        /// <summary>All of it.</summary>
        All = Vision | Hearing | CloseProximity | LineOfSight | Target | States
    }

    /// <summary>
    /// Parsing, formatting and description for <see cref="EnemyAIDebugFilter"/>, shared by the developer
    /// console and the overlay so the vocabulary exists in exactly one place.
    ///
    /// <para><b>An unknown token is an error, never a silent skip</b> — the same contract as
    /// <see cref="ColliderDebugFilters"/>, and for the same reason: <c>ai visionn</c> quietly matching
    /// nothing leaves the overlay on and drawing nothing, which is indistinguishable from "this enemy
    /// really cannot see the player". That is the exact conclusion this tool exists to support, so
    /// getting it wrong is worse than having no tool.</para>
    /// </summary>
    public static class EnemyAIDebugFilters
    {
        /// <summary>Usage text for the console's <c>help</c> and for a bad argument.</summary>
        public const string Usage =
            "all | off | a comma-separated list of vision, hearing, close, los, target, states " +
            "(or `senses` for vision+hearing+close)";

        /// <summary>
        /// Parse a filter. Empty means <see cref="EnemyAIDebugFilter.All"/>, so a bare <c>ai</c> reads
        /// as "show me everything".
        /// </summary>
        public static bool TryParse(string text, out EnemyAIDebugFilter filter, out string error)
        {
            filter = EnemyAIDebugFilter.All;
            error = null;

            if (string.IsNullOrWhiteSpace(text)) return true;

            string trimmed = text.Trim();
            if (IsOffToken(trimmed)) { filter = EnemyAIDebugFilter.None; return true; }
            if (IsAllToken(trimmed)) { filter = EnemyAIDebugFilter.All; return true; }

            EnemyAIDebugFilter accumulated = EnemyAIDebugFilter.None;

            foreach (string token in SplitTokens(trimmed))
            {
                if (IsAllToken(token)) { accumulated = EnemyAIDebugFilter.All; continue; }
                if (IsOffToken(token)) continue;

                switch (Normalize(token))
                {
                    case "vision":
                    case "sight":
                    case "see":
                    case "cone":
                        accumulated |= EnemyAIDebugFilter.Vision;
                        break;

                    case "hearing":
                    case "hear":
                    case "ear":
                    case "noise":
                        accumulated |= EnemyAIDebugFilter.Hearing;
                        break;

                    // "close" / "detecting" are the names the oracle and the port's own field use
                    // (`CloseProximityPixels`, AS3 `detecting`), so they are first-class spellings
                    // rather than tolerated typos.
                    case "close":
                    case "proximity":
                    case "detecting":
                    case "near":
                        accumulated |= EnemyAIDebugFilter.CloseProximity;
                        break;

                    case "los":
                    case "lineofsight":
                    case "ray":
                    case "raycast":
                        accumulated |= EnemyAIDebugFilter.LineOfSight;
                        break;

                    case "target":
                    case "lastknown":
                    case "last":
                        accumulated |= EnemyAIDebugFilter.Target;
                        break;

                    case "states":
                    case "state":
                    case "labels":
                    case "label":
                        accumulated |= EnemyAIDebugFilter.States;
                        break;

                    // The three senses at once. The request that produced this overlay was literally
                    // "visualize what the AI sees, hears, etc", so the phrase is the primary spelling
                    // for the group — and it is an alias that EXPANDS, which is why it is handled here
                    // and not in TryMatch-style single-flag matching.
                    case "senses":
                    case "sense":
                        accumulated |= EnemyAIDebugFilter.Vision
                                     | EnemyAIDebugFilter.Hearing
                                     | EnemyAIDebugFilter.CloseProximity;
                        break;

                    default:
                        error = $"Unknown AI overlay part '{token}'. Expected {Usage}.";
                        filter = EnemyAIDebugFilter.None;
                        return false;
                }
            }

            filter = accumulated;
            return true;
        }

        /// <summary>Render a filter the way <see cref="TryParse"/> accepts it back.</summary>
        public static string Format(EnemyAIDebugFilter filter)
        {
            if (filter == EnemyAIDebugFilter.None) return "off";
            if (filter == EnemyAIDebugFilter.All) return "all";

            var sb = new StringBuilder();
            Append(sb, filter, EnemyAIDebugFilter.Vision, "vision");
            Append(sb, filter, EnemyAIDebugFilter.Hearing, "hearing");
            Append(sb, filter, EnemyAIDebugFilter.CloseProximity, "close");
            Append(sb, filter, EnemyAIDebugFilter.LineOfSight, "los");
            Append(sb, filter, EnemyAIDebugFilter.Target, "target");
            Append(sb, filter, EnemyAIDebugFilter.States, "states");
            return sb.ToString();
        }

        /// <summary>
        /// One line per flag: name, on/off, and what it draws. Used by <c>ai</c> status and by the
        /// panel, so the two cannot disagree about what a flag means.
        /// </summary>
        public static string DescribeAll(EnemyAIDebugFilter enabled)
        {
            var sb = new StringBuilder();
            AppendLine(sb, enabled, EnemyAIDebugFilter.Vision, "vision",
                "forward vision cone (range arc, facing hemisphere) + the eye point");
            AppendLine(sb, enabled, EnemyAIDebugFilter.Hearing, "hearing",
                "hearing radius (range x ear), a full circle — hearing has no facing");
            AppendLine(sb, enabled, EnemyAIDebugFilter.CloseProximity, "close",
                "close-proximity bubble: detected regardless of facing (AS3 detecting)");
            AppendLine(sb, enabled, EnemyAIDebugFilter.LineOfSight, "los",
                "eye -> target ray, coloured by whether the tile query obstructed it");
            AppendLine(sb, enabled, EnemyAIDebugFilter.Target, "target",
                "target marker + the blackboard's last-known-position marker");
            AppendLine(sb, enabled, EnemyAIDebugFilter.States, "states",
                "state label above every enemy in view");
            return sb.ToString();
        }

        /// <summary>Is one flag set? Kept as a method so a call site cannot forget the mask test.</summary>
        public static bool Has(EnemyAIDebugFilter filter, EnemyAIDebugFilter flag)
        {
            return (filter & flag) != 0;
        }

        /// <summary>
        /// The filter to fall back to when the overlay is switched on and the current one would draw
        /// nothing. Turning a toggle ON must never leave it showing nothing — that reads as a broken
        /// toggle, not as an empty filter (the same rule <c>ColliderDebugOverlay.ToggleTiles</c>
        /// follows).
        /// </summary>
        public static EnemyAIDebugFilter PermissiveIfEmpty(EnemyAIDebugFilter filter)
        {
            return filter == EnemyAIDebugFilter.None ? EnemyAIDebugFilter.All : filter;
        }

        // ── Internals ────────────────────────────────────────────────────────

        private static void Append(StringBuilder sb, EnemyAIDebugFilter filter,
                                   EnemyAIDebugFilter flag, string name)
        {
            if ((filter & flag) == 0) return;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(name);
        }

        private static void AppendLine(StringBuilder sb, EnemyAIDebugFilter filter,
                                       EnemyAIDebugFilter flag, string name, string hint)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append((filter & flag) != 0 ? "  [x] " : "  [ ] ")
              .Append(name.PadRight(10))
              .Append(hint);
        }

        private static bool IsAllToken(string token)
        {
            string t = Normalize(token);
            return t == "all" || t == "*" || t == "everything" || t == "on";
        }

        private static bool IsOffToken(string token)
        {
            string t = Normalize(token);
            return t == "off" || t == "none" || t == "0" || t == "false"
                || t == "hide" || t == "clear";
        }

        /// <summary>
        /// Case and surrounding whitespace are not information here. C# string <c>switch</c> is
        /// case-sensitive, so without this <c>ai Vision</c> fails with "Unknown AI overlay part" and
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
