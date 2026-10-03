using System;
using System.Collections.Generic;
using System.Text;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Effects;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Debug commands exposed to the Lua console as the global <c>eff</c> table, plus the console verb
    /// <c>eff</c> (sugar over the same methods).
    ///
    /// <para><b>Why this exists.</b> The same reason <c>rpg</c> does, and the failure it guards against
    /// is the one this project keeps hitting: an effect set that is <i>wired but inert</i>. A live unit
    /// with a resolver-less set adds nothing and reads as "no effects", and a set with a resolver but
    /// no matching definition row behaves identically — both look like an effect system that simply has
    /// nothing to do. <c>eff status</c> prints the one figure that separates them (whether the target's
    /// set has a resolver at all, and the definition count the runtime can actually resolve), and
    /// <c>eff add</c> then proves the count-down is running rather than asserting it.</para>
    ///
    /// <para>A bare <c>eff</c> is <b>status</b>, never a toggle, matching <c>col</c>, <c>prof</c> and
    /// <c>rpg</c>.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> <c>Wire</c> is <c>internal</c>; MoonSharp's default
    /// reflection interop exposes public members only, so only the command methods below become callable
    /// from Lua. Do not make <c>_playerProvider</c> or <c>Wire</c> public.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleEffectCommands
    {
        /// <summary>
        /// Late-resolved so the commands survive a respawn. Resolved per call rather than cached: a
        /// cached reference to a destroyed player is the classic "MissingReferenceException in a debug
        /// tool" failure, and this one would take the console down with it.
        /// </summary>
        private Func<PlayerController> _playerProvider;

        /// <summary>
        /// The selected target, by scene index: <c>0</c> = the player (the default), <c>1..n</c> = the
        /// other <c>UnitController</c>s in the scene in discovery order. Kept across calls so
        /// <c>eff unit 3</c> then <c>eff add burning</c> address the same unit — the whole point of a
        /// debug console is to establish a context and act within it.
        /// </summary>
        private int _targetIndex;

        internal void Wire(Func<PlayerController> playerProvider)
        {
            _playerProvider = playerProvider;
        }

        private static PlayerController FindPlayer()
            => UnityEngine.Object.FindFirstObjectByType<PlayerController>();

        private PlayerController ResolvePlayer()
        {
            var player = _playerProvider != null ? _playerProvider() : null;
            if (player == null) player = FindPlayer();
            return player;
        }

        /// <summary>
        /// Resolve the selected unit. Falls back to the player when the index is stale (the unit died or
        /// the room was torn down), which is the same behaviour the F2 overlay's selector has — a debug
        /// tool should not go blank on a stale selection.
        /// </summary>
        private UnitController ResolveTarget(out string label)
        {
            PlayerController player = ResolvePlayer();

            if (_targetIndex <= 0)
            {
                label = "player";
                return player;
            }

            UnitController[] units = UnityEngine.Object.FindObjectsByType<UnitController>(
                UnityEngine.FindObjectsSortMode.None);

            int seen = 0;
            foreach (UnitController u in units)
            {
                if (u == null || u == player) continue;
                seen++;
                if (seen == _targetIndex)
                {
                    label = $"{u.name} ({u.GetType().Name})";
                    return u;
                }
            }

            _targetIndex = 0;
            label = "player";
            return player;
        }

        // ── Commands ──────────────────────────────────────────────────────────

        /// <summary>
        /// Engine health: can this target's effect set actually resolve an id, how many definitions can
        /// it see, and what is live right now. Ends with a verdict line that names the broken link.
        /// </summary>
        public string Status()
        {
            UnitController target = ResolveTarget(out string label);
            if (target == null)
                return "[eff] No UnitController in the scene (no player, no spawned unit).";

            UnitStats stats = target.UnitStats;
            ActiveEffectSet set = target.Effects;

            var sb = new StringBuilder();
            sb.AppendLine($"[eff] --- effect engine health ({label}) ---");
            sb.AppendLine($"  target type              : {(target is PlayerController ? "player (PersMode.Player)" : "NPC (PersMode.Npc)")}");
            sb.AppendLine($"  set attached             : {(set != null ? "YES" : "NO   <- nothing can be applied")}");

            bool hasResolver = stats != null && stats.HasEffectResolver;
            sb.AppendLine($"  resolver wired           : {(hasResolver ? "YES" : "NO   <- AddEffect refuses every id (resolver-less set)")}");
            sb.AppendLine($"  persistence mode         : {(stats != null ? stats.EffectMode.ToString() : "-")}");
            sb.AppendLine($"  live effects             : {(set != null ? set.Count : 0)}");
            sb.AppendLine($"  definitions under Effects: {EffectDefinitionCount()} (Resources/Effects)");

            int unmapped = 0;
            if (set?.UnmappedParamNames != null)
            {
                foreach (var kv in set.UnmappedParamNames) unmapped += kv.Value;
            }
            sb.AppendLine($"  unmapped <sk> writes     : {unmapped}");

            if (stats != null)
            {
                sb.AppendLine($"  maxHp / skin / dexter    : {stats.MaxHp.Value:0.##} / {stats.skinResistance:0.###} / {stats.dexterity:0.##}");
                sb.AppendLine($"  armour                   : {(stats.armour.IsEquipped ? $"{stats.ArmourId.Value} ({stats.armour.integrity:0}/{stats.armour.maxIntegrity:0})" : "none")}");
            }

            sb.AppendLine();
            sb.AppendLine("  " + Verdict(set, hasResolver));
            return sb.ToString();
        }

        /// <summary>The live effects on the selected target, in add order.</summary>
        public string List()
        {
            UnitController target = ResolveTarget(out string label);
            if (target == null)
                return "[eff] No UnitController in the scene.";

            ActiveEffectSet set = target.Effects;
            if (set == null)
                return "[eff] The target has no effect set attached.";

            var sb = new StringBuilder();
            sb.AppendLine($"[eff] --- {set.Count} live effect(s) on {label} ---");
            if (set.Count == 0)
            {
                sb.AppendLine("  (none)");
                return sb.ToString();
            }

            sb.AppendLine("  id                 tip          state      ticks      lvl  val");
            foreach (ActiveEffect eff in set.Effects)
            {
                if (eff == null) continue;
                string dur = eff.Forever ? "forever" : eff.TicksRemaining.ToString();
                string state = eff.IsBeingUnset ? "unsetting" : "live";
                sb.AppendLine($"  {eff.Id,-18} {eff.Tip,-12} {state,-10} {dur,-10} {eff.Level,3}  {eff.Value:0.##}");
                if (eff.HasTransitioned)
                    sb.AppendLine($"    -> transitioned from '{eff.OriginalId}' (a post/postbad aftereffect)");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Apply an effect to the selected target — the same call the on-hit producers and the F2 tab
        /// make. <c>seconds</c> and <c>value</c> are overrides; <c>0</c>/omitted means "use the
        /// definition's own" (<c>Effect.as:82</c>, <c>:87-90</c>).
        /// </summary>
        public string Add(string effectId, float seconds = 0f, float value = 0f)
        {
            UnitController target = ResolveTarget(out string label);
            if (target == null)
                return "[eff] No UnitController in the scene.";
            if (string.IsNullOrEmpty(effectId))
                return "Usage: eff add <effectId> [seconds] [value]    e.g. eff add burning 10";
            if (target.Effects == null)
                return "[eff] The target has no effect set attached.";

            int ticks = seconds > 0f ? UnityEngine.Mathf.RoundToInt(seconds * 30f) : 0;
            ActiveEffect applied = target.Effects.AddEffect(effectId, value, ticks);

            if (applied == null)
            {
                // The only way AddEffect returns null for a non-empty id is an unresolvable definition —
                // which is the two-cause case: no resolver, or no matching row. Name which.
                bool hasResolver = target.UnitStats != null && target.UnitStats.HasEffectResolver;
                return hasResolver
                    ? $"[eff] '{effectId}' has no definition the runtime can resolve. There are " +
                      $"{EffectDefinitionCount()} under Resources/Effects — run `eff defs` to list them."
                    : $"[eff] The target's effect set is RESOLVER-LESS, so every id is refused. The set " +
                      $"is attached but was never handed a resolver (the room spawner does this for " +
                      $"spawned units; the player gets it from the container).";
            }

            return $"[eff] Applied '{applied.Id}' to {label}: t={applied.TicksRemaining} ticks " +
                   $"({applied.TicksRemaining / 30f:0.0}s{(applied.Forever ? ", forever" : "")}), " +
                   $"lvl={applied.Level}, val={applied.Value:0.##}, params={applied.HasParams}.";
        }

        /// <summary>Remove every effect with this id from the selected target (deferred, as the oracle's
        /// <c>remEffect</c> is).</summary>
        public string Remove(string effectId)
        {
            UnitController target = ResolveTarget(out string label);
            if (target == null) return "[eff] No UnitController in the scene.";
            if (string.IsNullOrEmpty(effectId))
                return "Usage: eff remove <effectId>";
            if (target.Effects == null) return "[eff] The target has no effect set attached.";

            if (!target.Effects.Has(effectId))
                return $"[eff] {label} has no live '{effectId}'. Run `eff list`.";

            target.Effects.RemoveEffect(effectId);
            return $"[eff] Marked '{effectId}' for removal on {label}. It splices on the next tick " +
                   "(the oracle defers removal so the reset-then-replay can undo its writes first — " +
                   "`eff list` will show it 'unsetting' until then).";
        }

        /// <summary>Drop everything on the selected target without firing end-of-effect callbacks — the
        /// oracle's respawn teardown.</summary>
        public string Clear()
        {
            UnitController target = ResolveTarget(out string label);
            if (target == null) return "[eff] No UnitController in the scene.";
            if (target.Effects == null) return "[eff] The target has no effect set attached.";

            int before = target.Effects.Count;
            target.Effects.Clear();
            return $"[eff] Cleared {before} effect(s) from {label}.";
        }

        /// <summary>
        /// Which unit <c>add</c>/<c>remove</c>/<c>list</c>/<c>clear</c> act on. No argument prints the
        /// current selection and the menu; <c>player</c> selects the player, a number selects the
        /// nth other unit.
        /// </summary>
        public string Unit(string selector = null)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                ResolveTarget(out string current);
                return $"[eff] Current target: {current}.\n" + TargetMenu();
            }

            selector = selector.Trim();
            if (selector.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                selector.Equals("pip", StringComparison.OrdinalIgnoreCase) ||
                selector == "0")
            {
                _targetIndex = 0;
                return "[eff] Target set to the player.";
            }

            if (!int.TryParse(selector, out int index) || index < 0)
                return $"[eff] '{selector}' is not a target. Use a number, or 'player'.\n" + TargetMenu();

            UnitController[] units = UnityEngine.Object.FindObjectsByType<UnitController>(
                UnityEngine.FindObjectsSortMode.None);
            PlayerController player = ResolvePlayer();

            int seen = 0;
            foreach (UnitController u in units)
            {
                if (u == null || u == player) continue;
                seen++;
                if (seen == index)
                {
                    _targetIndex = index;
                    return $"[eff] Target set to '{u.name}' ({u.GetType().Name}).";
                }
            }

            return $"[eff] There is no unit #{index}. Only {seen} other unit(s) exist.\n" + TargetMenu();
        }

        /// <summary>
        /// The definition catalogue the runtime can resolve, optionally filtered by a substring. This is
        /// what <c>add</c> draws from, so a name that appears here but is refused by <c>add</c> is a
        /// resolver problem, not a data problem.
        /// </summary>
        public string Defs(string filter = null)
        {
            EffectDefinition[] defs = LoadEffectDefinitions();
            var sb = new StringBuilder();
            sb.AppendLine($"[eff] --- {defs.Length} effect definition(s) under Resources/Effects ---");

            string f = filter?.Trim();
            int shown = 0;
            foreach (EffectDefinition d in defs)
            {
                if (f != null && f.Length > 0 &&
                    d.effectId.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                shown++;
                string dur = d.forever ? "forever" : $"{d.durationTicks / 30f:0.#}s";
                int params_ = d.effects?.Length ?? 0;
                sb.AppendLine($"  {d.effectId,-18} tip={d.type,-10} t={dur,-10} val={d.value,6:0.##}  sk={params_}" +
                              (string.IsNullOrEmpty(d.afterEffectId) ? "" : $"  post={d.afterEffectId}"));
            }

            if (shown == 0)
                sb.AppendLine($"  (no definition matches '{filter}')");
            return sb.ToString();
        }

        /// <summary>
        /// The unmapped <c>&lt;sk&gt;</c> names the target's set recorded — the oracle's
        /// <c>hasOwnProperty</c> guard makes an unmapped name a silent no-op, so this is how that silence
        /// is broken. A name here is a porting gap (an effect writes a field the port has no setter for).
        /// </summary>
        public string Dump()
        {
            UnitController target = ResolveTarget(out string label);
            if (target == null) return "[eff] No UnitController in the scene.";

            ActiveEffectSet set = target.Effects;
            if (set == null) return "[eff] The target has no effect set attached.";

            var sb = new StringBuilder();
            sb.AppendLine($"[eff] --- <sk> write health on {label} ---");

            if (set.UnmappedParamNames == null || set.UnmappedParamNames.Count == 0)
            {
                sb.AppendLine("  No unmapped <sk> names recorded this session.");
            }
            else
            {
                sb.AppendLine("  Unmapped names (a write the port has no setter for — an oracle no-op):");
                foreach (var kv in set.UnmappedParamNames)
                    sb.AppendLine($"    {kv.Key,-24} x{kv.Value}");
            }

            sb.AppendLine();
            sb.AppendLine($"  Live effects' param counts:");
            if (set.Count == 0)
            {
                sb.AppendLine("    (none)");
            }
            else
            {
                foreach (ActiveEffect eff in set.Effects)
                {
                    if (eff == null) continue;
                    int n = eff.Definition?.effects?.Length ?? 0;
                    sb.AppendLine($"    {eff.Id,-18} {n} param(s), index used = {ParamIndexFor(eff, target)}");
                }
            }
            return sb.ToString();
        }

        /// <summary>Usage for the <c>eff</c> verb.</summary>
        public string Help()
        {
            return
                "eff                     - effect engine health: is the set wired and can it resolve ids?\n" +
                "eff list                - live effects on the selected target, in add order\n" +
                "eff unit                - show/choose the target (player, or another unit in the scene)\n" +
                "eff unit <n>            - select the nth other unit (eff unit player = back to the player)\n" +
                "eff add <id> [s] [val]  - apply an effect; s/val override the definition's t/val\n" +
                "eff remove <id>         - remove it (deferred one tick, as the oracle does)\n" +
                "eff clear               - drop everything on the target (no end callbacks)\n" +
                "eff defs [filter]       - the definition catalogue the runtime can resolve\n" +
                "eff dump                - unmapped <sk> writes (the oracle's silent no-op)\n" +
                "Lua: eff:Status() | eff:List() | eff:Add(\"burning\",10) | eff:Remove(\"burning\")";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string Verdict(ActiveEffectSet set, bool hasResolver)
        {
            if (set == null)
                return "VERDICT: DEAD -- no effect set is attached, so nothing can be applied. A unit gets " +
                       "one from UnitStats; a raw object without one cannot carry effects.";

            if (!hasResolver)
                return "VERDICT: DEAD -- the set is attached but RESOLVER-LESS, so AddEffect refuses every " +
                       "id. A spawned unit gets its resolver from the room spawner; a bare AddComponent " +
                       "unit never does.";

            if (EffectDefinitionCount() == 0)
                return "VERDICT: EMPTY -- the set is wired but Resources/Effects has no definitions, so " +
                       "every id resolves to nothing. Run the `Import Effects from AllData.as` menu item.";

            if (set.Count == 0)
                return "VERDICT: LIVE but idle -- the set is wired and can resolve ids; nothing is applied. " +
                       "Try `eff add burning 10` then `eff list` twice to watch the count-down.";

            return $"VERDICT: LIVE -- {set.Count} effect(s) running. `eff list` twice shows the count-down.";
        }

        private string TargetMenu()
        {
            UnitController[] units = UnityEngine.Object.FindObjectsByType<UnitController>(
                UnityEngine.FindObjectsSortMode.None);
            PlayerController player = ResolvePlayer();

            var sb = new StringBuilder();
            sb.AppendLine("  Targets:");
            sb.AppendLine("    0  player");
            int seen = 0;
            foreach (UnitController u in units)
            {
                if (u == null || u == player) continue;
                seen++;
                sb.AppendLine($"    {seen}  {u.name} ({u.GetType().Name})  hp {u.CurrentHealth:0}/{u.MaxHealth:0}");
            }
            if (seen == 0)
                sb.AppendLine("    (no other units in the scene)");
            return sb.ToString();
        }

        private static int ParamIndexFor(ActiveEffect effect, UnitController target)
        {
            bool player = target is PlayerController;
            if (player)
            {
                // PersMode.Player: index is eff.lvl, and an effect being unset is skipped entirely.
                return effect.IsBeingUnset ? -1 : effect.Level;
            }

            // PersMode.Npc: index 1 active, 0 being unset.
            return effect.IsBeingUnset ? 0 : 1;
        }

        private static int EffectDefinitionCount() => LoadEffectDefinitions().Length;

        private static EffectDefinition[] _cached;
        private static EffectDefinition[] LoadEffectDefinitions()
        {
            if (_cached == null)
            {
                _cached = UnityEngine.Resources.LoadAll<EffectDefinition>("Effects") ?? Array.Empty<EffectDefinition>();
                Array.Sort(_cached, (a, b) => string.Compare(
                    a != null ? a.effectId : string.Empty,
                    b != null ? b.effectId : string.Empty,
                    StringComparison.OrdinalIgnoreCase));
            }
            return _cached;
        }
    }
}
