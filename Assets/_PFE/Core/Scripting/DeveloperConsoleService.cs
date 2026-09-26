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
                              "  <lua code>      - Run any Lua expression (e.g. 'return 2+2', 'pfe.reveal_map()')";
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
