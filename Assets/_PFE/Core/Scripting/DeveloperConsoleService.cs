using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using PFE.Core;
using PFE.Systems.Map;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Core service for the developer console and REPL.
    /// Pure C# domain logic with no UnityEngine.MonoBehaviour dependencies,
    /// enabling fast, deterministic unit testing in CLI runners.
    /// </summary>
    public sealed class DeveloperConsoleService
    {
        public static DeveloperConsoleService Instance { get; set; }

        private ILuaEngine _luaEngine;
        private LandMap _landMap;
        private Action<bool> _setFogDisabledAction;
        private Action _revealFogAction;
        private Func<bool> _isFogDisabledFunc;

        // Debug command objects, exposed to Lua as the globals `player` and `sim`.
        private DevConsolePlayerCommands _playerCommands;
        private DevConsoleSimCommands _simCommands;
        private bool _commandObjectsRegistered;

        private bool _isOpen;
        private readonly List<string> _history = new List<string>();
        private readonly List<string> _commandHistory = new List<string>();
        private const int MaxHistory = 100;

        public bool IsOpen
        {
            get => _isOpen;
            set => _isOpen = value;
        }

        public IReadOnlyList<string> History => _history;
        public IReadOnlyList<string> CommandHistory => _commandHistory;

        public DeveloperConsoleService(ILuaEngine luaEngine = null, LandMap landMap = null)
        {
            _luaEngine = luaEngine;
            _landMap = landMap;
            Instance = this;

            AppendLog("=== PFE Developer Console ===");
            AppendLog("Type 'help' for commands, or run any Lua code (e.g. 'return 2+2', 'pfe.reveal_map()').");
            AppendLog("Press ~ or F1 to toggle console.");
        }

        public void SetDependencies(
            ILuaEngine luaEngine,
            LandMap landMap = null,
            Action<bool> setFogDisabledAction = null,
            Action revealFogAction = null,
            Func<bool> isFogDisabledFunc = null)
        {
            if (luaEngine != null) _luaEngine = luaEngine;
            if (landMap != null) _landMap = landMap;
            if (setFogDisabledAction != null) _setFogDisabledAction = setFogDisabledAction;
            if (revealFogAction != null) _revealFogAction = revealFogAction;
            if (isFogDisabledFunc != null) _isFogDisabledFunc = isFogDisabledFunc;
        }

        /// <summary>
        /// Attach the debug command objects and publish them to Lua as the globals <c>player</c>
        /// and <c>sim</c>, enabling <c>player:Heal(50)</c> / <c>sim:SetTickRate(120)</c>.
        ///
        /// <para>Idempotent: the console re-wires its dependencies on every open and on every
        /// submit (so a respawned player is picked up), and re-registering the MoonSharp types on
        /// each of those calls would be wasteful. The objects themselves are re-assigned every
        /// time, so a later call always wins.</para>
        /// </summary>
        public void SetCommandObjects(
            DevConsolePlayerCommands playerCommands,
            DevConsoleSimCommands simCommands)
        {
            if (playerCommands != null) _playerCommands = playerCommands;
            if (simCommands != null) _simCommands = simCommands;

            if (_luaEngine == null) return;

            // UserData.Create throws for an unregistered CLR type, so this must precede SetGlobal.
            // Both registrations are guarded internally by UserData.IsTypeRegistered.
            if (!_commandObjectsRegistered)
            {
                _luaEngine.RegisterType<DevConsolePlayerCommands>();
                _luaEngine.RegisterType<DevConsoleSimCommands>();
                _commandObjectsRegistered = true;
            }

            if (_playerCommands != null) _luaEngine.SetGlobal("player", _playerCommands);
            if (_simCommands != null) _luaEngine.SetGlobal("sim", _simCommands);
        }

        /// <summary>
        /// Execute command or Lua input string.
        /// </summary>
        public string ExecuteInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            string trimmed = input.Trim();
            AppendLog($"> {trimmed}");
            _commandHistory.Add(trimmed);

            string lower = trimmed.ToLowerInvariant();

            // Built-in shortcut commands
            if (lower == "help" || lower == "?")
            {
                string help = "=== PFE Console Commands ===\n" +
                              "  map / reveal    - Reveal all rooms on map and clear fog of war\n" +
                              "  fog / black     - Toggle room fog of war on/off\n" +
                              "  fog off         - Disable room fog of war (fully visible)\n" +
                              "  fog on          - Enable room fog of war\n" +
                              "  clear / cls     - Clear console log\n" +
                              "  help / ?        - Show this help\n" +
                              "  -- player --\n" +
                              "  heal <n>        - Heal the player by n\n" +
                              "  damage <n>      - Damage the player by n\n" +
                              "  god             - Set the player's HP to max\n" +
                              "  kill            - Kill the player\n" +
                              "  killall         - Kill every non-player unit in the scene\n" +
                              "  weapon <id>     - Equip a weapon by content id (e.g. weapon 10mm)\n" +
                              "  ammo            - Refill the equipped weapon's magazine\n" +
                              "  tp <x> <y>      - Teleport to a world position\n" +
                              "  room <x> <y>    - Teleport to the room at a land coordinate\n" +
                              "  status          - Report position, health and equipped weapon\n" +
                              "  -- simulation --\n" +
                              "  tick <n>        - Set the sim tick rate (30/60/90/120)\n" +
                              "  -- lua --\n" +
                              "  <lua code>      - Run any Lua expression (e.g. 'return 2+2', 'player:Heal(50)')\n" +
                              "                    Globals: pfe.* (map/fog/rng), player, sim";
                AppendLog(help);
                return help;
            }

            if (lower == "map" || lower == "reveal" || lower == "reveal_map" || lower == "reveal map")
            {
                return RevealMap();
            }

            if (lower == "fog" || lower == "black" || lower == "fogofwar" || lower == "toggle_fog")
            {
                return ToggleFog();
            }

            if (lower == "fog off" || lower == "fog 0" || lower == "fog false")
            {
                return SetFog(false);
            }

            if (lower == "fog on" || lower == "fog 1" || lower == "fog true")
            {
                return SetFog(true);
            }

            if (lower == "clear" || lower == "cls")
            {
                ClearHistory();
                return string.Empty;
            }

            // ── Debug shortcuts ──────────────────────────────────────────────────
            // These are thin sugar over the same objects Lua reaches as `player` / `sim`, so the
            // console is usable without knowing Lua. Lua remains the fallback for everything else.
            if (TryRunDebugShortcut(trimmed, out string shortcutResult))
            {
                AppendLog(shortcutResult);
                return shortcutResult;
            }

            // Fallback to Lua execution
            if (_luaEngine == null)
            {
                string err = "Error: ILuaEngine service is not available.";
                AppendLog(err);
                return err;
            }

            try
            {
                string code = trimmed;
                DynValue result;

                // Try evaluating as an expression first
                try
                {
                    result = _luaEngine.ExecuteString(code.StartsWith("return ") ? code : $"return {code}");
                }
                catch
                {
                    result = _luaEngine.ExecuteString(code);
                }

                string formattedResult = result.IsNil() ? "nil" : result.ToPrintString();
                AppendLog(formattedResult);
                return formattedResult;
            }
            catch (Exception ex)
            {
                string err = $"[Lua Error] {ex.Message}";
                AppendLog(err);
                return err;
            }
        }

        /// <summary>
        /// Reveals all rooms in the land map and disables/clears fog of war in the active room.
        /// </summary>
        public string RevealMap()
        {
            int roomsRevealed = 0;

            // 1. Reveal rooms in LandMap
            if (_landMap != null)
            {
                _landMap.RevealAllRooms(true);
                roomsRevealed = _landMap.GetRoomCount();
            }

            // 2. Clear fog of war in active room
            _setFogDisabledAction?.Invoke(true);
            _revealFogAction?.Invoke();

            string msg = $"[Map] Revealed {roomsRevealed} rooms on map and cleared fog of war in active room.";
            AppendLog(msg);
            return msg;
        }

        /// <summary>
        /// Toggles fog of war on or off in the active room.
        /// </summary>
        public string ToggleFog()
        {
            if (_isFogDisabledFunc != null && _setFogDisabledAction != null)
            {
                bool currentlyDisabled = _isFogDisabledFunc();
                bool newDisabled = !currentlyDisabled;
                _setFogDisabledAction(newDisabled);
                string msg = $"[FogOfWar] Fog of war is now {(newDisabled ? "DISABLED (revealed)" : "ENABLED")}.";
                AppendLog(msg);
                return msg;
            }

            string notFound = "[FogOfWar] No active room fog controller connected.";
            AppendLog(notFound);
            return notFound;
        }

        /// <summary>
        /// Enables or disables fog of war in the active room.
        /// </summary>
        public string SetFog(bool enabled)
        {
            if (_setFogDisabledAction != null)
            {
                _setFogDisabledAction(!enabled);
                string msg = $"[FogOfWar] Fog of war is now {(enabled ? "ENABLED" : "DISABLED (revealed)")}.";
                AppendLog(msg);
                return msg;
            }

            string notFound = "[FogOfWar] No active room fog controller connected.";
            AppendLog(notFound);
            return notFound;
        }

        /// <summary>
        /// Parse and dispatch the non-Lua debug shortcuts. Returns false when
        /// <paramref name="trimmed"/> is not a recognised shortcut, in which case the caller falls
        /// through to Lua evaluation.
        ///
        /// <para>Argument parsing is deliberately lenient about arity and strict about types: a
        /// malformed argument produces a readable message rather than a Lua stack trace, because
        /// the whole point of these commands is to be fast to type under pressure.</para>
        /// </summary>
        private bool TryRunDebugShortcut(string trimmed, out string result)
        {
            result = null;

            string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            string verb = parts[0].ToLowerInvariant();

            switch (verb)
            {
                case "heal":
                case "hp":
                    if (_playerCommands == null) return false;
                    if (!TryParseArg(parts, 1, out float healAmount))
                        { result = "Usage: heal <amount>"; return true; }
                    result = _playerCommands.Heal(healAmount);
                    return true;

                case "hurt":
                case "damage":
                    if (_playerCommands == null) return false;
                    if (!TryParseArg(parts, 1, out float dmg))
                        { result = "Usage: damage <amount>"; return true; }
                    result = _playerCommands.Damage(dmg);
                    return true;

                case "god":
                    if (_playerCommands == null) return false;
                    result = _playerCommands.SetHealth(float.MaxValue);
                    return true;

                case "kill":
                    if (_playerCommands == null) return false;
                    result = _playerCommands.Kill();
                    return true;

                case "killall":
                case "wipe":
                    if (_playerCommands == null) return false;
                    result = _playerCommands.KillAll();
                    return true;

                case "weapon":
                case "give":
                    if (_playerCommands == null) return false;
                    if (parts.Length < 2) { result = "Usage: weapon <weaponId>"; return true; }
                    result = _playerCommands.GiveWeapon(parts[1]);
                    return true;

                case "ammo":
                case "refill":
                    if (_playerCommands == null) return false;
                    result = _playerCommands.RefillAmmo();
                    return true;

                case "tp":
                case "teleport":
                    if (_playerCommands == null) return false;
                    if (parts.Length < 3 ||
                        !float.TryParse(parts[1], out float tx) ||
                        !float.TryParse(parts[2], out float ty))
                        { result = "Usage: tp <x> <y>   (world position)"; return true; }
                    result = _playerCommands.Teleport(tx, ty);
                    return true;

                case "room":
                    if (_playerCommands == null) return false;
                    if (parts.Length < 3 ||
                        !int.TryParse(parts[1], out int rx) ||
                        !int.TryParse(parts[2], out int ry))
                        { result = "Usage: room <x> <y>   (land coordinate)"; return true; }
                    result = _playerCommands.TeleportToRoom(rx, ry);
                    return true;

                case "status":
                case "pos":
                    if (_playerCommands == null) return false;
                    result = _playerCommands.Status();
                    return true;

                case "tick":
                    if (_simCommands == null) return false;
                    if (!TryParseIntArg(parts, 1, out int rate))
                        { result = "Usage: tick <30|60|90|120>"; return true; }
                    result = _simCommands.SetTickRate(rate);
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseArg(string[] parts, int index, out float value)
        {
            value = 0f;
            return parts.Length > index && float.TryParse(parts[index], out value);
        }

        private static bool TryParseIntArg(string[] parts, int index, out int value)
        {
            value = 0;
            return parts.Length > index && int.TryParse(parts[index], out value);
        }

        public void ClearHistory()
        {
            _history.Clear();
        }

        public void AppendLog(string message)
        {
            _history.Add(message);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }
        }
    }
}
