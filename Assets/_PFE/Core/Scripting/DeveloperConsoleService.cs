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

        // Debug command objects, exposed to Lua as the globals `player`, `sim`, `save` and `collider`.
        private DevConsolePlayerCommands _playerCommands;
        private DevConsoleSimCommands _simCommands;
        private DevConsoleSaveCommands _saveCommands;
        private DevConsoleColliderCommands _colliderCommands;
        private DevConsoleProfilerCommands _profilerCommands;
        private DevConsoleRpgCommands _rpgCommands;
        private DevConsoleEffectCommands _effectCommands;
        private DevConsoleSpellCommands _spellCommands;
        private DevConsoleInventoryCommands _inventoryCommands;
        private DevConsoleUiCommands _uiCommands;
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
        /// Attach the debug command objects and publish them to Lua as the globals <c>player</c>,
        /// <c>sim</c> and <c>save</c>, enabling <c>player:Heal(50)</c> / <c>sim:SetTickRate(120)</c>
        /// / <c>save:Load()</c>.
        ///
        /// <para>Idempotent: the console re-wires its dependencies on every open and on every
        /// submit (so a respawned player is picked up), and re-registering the MoonSharp types on
        /// each of those calls would be wasteful. The objects themselves are re-assigned every
        /// time, so a later call always wins.</para>
        /// </summary>
        public void SetCommandObjects(
            DevConsolePlayerCommands playerCommands,
            DevConsoleSimCommands simCommands,
            DevConsoleSaveCommands saveCommands = null,
            DevConsoleColliderCommands colliderCommands = null,
            DevConsoleProfilerCommands profilerCommands = null,
            DevConsoleRpgCommands rpgCommands = null,
            DevConsoleEffectCommands effectCommands = null,
            DevConsoleSpellCommands spellCommands = null,
            DevConsoleUiCommands uiCommands = null,
            DevConsoleInventoryCommands inventoryCommands = null)
        {
            if (playerCommands != null) _playerCommands = playerCommands;
            if (simCommands != null) _simCommands = simCommands;
            if (saveCommands != null) _saveCommands = saveCommands;
            if (colliderCommands != null) _colliderCommands = colliderCommands;
            if (profilerCommands != null) _profilerCommands = profilerCommands;
            if (rpgCommands != null) _rpgCommands = rpgCommands;
            if (effectCommands != null) _effectCommands = effectCommands;
            if (spellCommands != null) _spellCommands = spellCommands;
            if (inventoryCommands != null) _inventoryCommands = inventoryCommands;
            if (uiCommands != null) _uiCommands = uiCommands;

            if (_luaEngine == null) return;

            // UserData.Create throws for an unregistered CLR type, so this must precede SetGlobal.
            // Both registrations are guarded internally by UserData.IsTypeRegistered.
            if (!_commandObjectsRegistered)
            {
                _luaEngine.RegisterType<DevConsolePlayerCommands>();
                _luaEngine.RegisterType<DevConsoleSimCommands>();
                _luaEngine.RegisterType<DevConsoleSaveCommands>();
                _luaEngine.RegisterType<DevConsoleColliderCommands>();
                _luaEngine.RegisterType<DevConsoleProfilerCommands>();
                _luaEngine.RegisterType<DevConsoleRpgCommands>();
                _luaEngine.RegisterType<DevConsoleEffectCommands>();
                _luaEngine.RegisterType<DevConsoleSpellCommands>();
                _luaEngine.RegisterType<DevConsoleInventoryCommands>();
                _luaEngine.RegisterType<DevConsoleUiCommands>();
                _commandObjectsRegistered = true;
            }

            if (_playerCommands != null) _luaEngine.SetGlobal("player", _playerCommands);
            if (_simCommands != null) _luaEngine.SetGlobal("sim", _simCommands);
            if (_saveCommands != null) _luaEngine.SetGlobal("save", _saveCommands);
            if (_colliderCommands != null) _luaEngine.SetGlobal("collider", _colliderCommands);
            if (_profilerCommands != null) _luaEngine.SetGlobal("prof", _profilerCommands);
            if (_rpgCommands != null) _luaEngine.SetGlobal("rpg", _rpgCommands);
            if (_effectCommands != null) _luaEngine.SetGlobal("eff", _effectCommands);
            if (_spellCommands != null) _luaEngine.SetGlobal("spell", _spellCommands);
            if (_inventoryCommands != null) _luaEngine.SetGlobal("inv", _inventoryCommands);
            if (_uiCommands != null) _luaEngine.SetGlobal("ui", _uiCommands);
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
                              "  editor          - Open LittlePip Character/Loadout Editor (F2)\n" +
                              "  heal <n>        - Heal the player by n\n" +
                              "  damage <n>      - Damage the player by n\n" +
                              "  god             - Set the player's HP to max\n" +
                              "  kill            - Kill the player\n" +
                              "  killall         - Kill every non-player unit in the scene\n" +
                              "  weapon <id>     - Equip a weapon by content id (e.g. weapon 10mm)\n" +
                              "  ammo            - Refill the equipped weapon's magazine\n" +
                              "  giveammo [id] [n] - Stock n rounds of an ammo type in the inventory\n" +
                              "  tp <x> <y>      - Teleport to a world position\n" +
                              "  room <x> <y>    - Teleport to the room at a land coordinate\n" +
                              "  status          - Report position, health and equipped weapon\n" +
                              "  -- simulation --\n" +
                              "  tick <n>        - Set the sim tick rate (30/60/90/120)\n" +
                              "  -- overlays (col; hotkeys F5 tiles / F6 units / F8 areas / F9 doors / F10 objects) --\n" +
                              "  col             - List every overlay and whether it is on\n" +
                              "  col on [list]   - Turn overlays on (no list = all incl. legend). Independent.\n" +
                              "  col off [list]  - Turn overlays off (no list = all)\n" +
                              "  col <name>      - Shorthand for 'col on <name>'\n" +
                              "  col tiles <t>   - Tile colliders. t = all | off | air,wall,platform,shelf,stair\n" +
                              "  col units <t>   - Unit colliders. t = all | off | player,npc\n" +
                              "  col probe       - What is under the player's feet, in game pixels\n" +
                              "  col gaps        - Every tile: collider top vs VISIBLE sprite top, by type\n" +
                              "  overlays: tiles units doors triggers transitions objects\n" +
                              "            tilequery room pool clock legend\n" +
                              "  -- telekinesis / teleport (Q) --\n" +
                              "  tele            - Report whether the [tele] trace is on\n" +
                              "  tele on         - Trace the whole Q path to the Console (grab gate + teleport charge)\n" +
                              "  tele off        - Stop tracing\n" +
                              "  tele probe      - Dump what the grab path sees RIGHT NOW, in the order it tests it\n" +
                              "  -- save (console-only: the F-row is taken by plugins/overlays) --\n" +
                              "  save            - Quick-save the world to the 'quicksave' slot\n" +
                              "  load            - Quick-load the world from the 'quicksave' slot\n" +
                              "  saves           - Report the save file, size, write time and position\n" +
                              "  -- profiling (region profiler; dumps land in ProfilerCaptures/) --\n" +
                              "  prof            - Status: enabled, region count, ticksThisFrame/max\n" +
                              "  prof reset      - Drop all regions. Run at the door of the room you are measuring\n" +
                              "  prof dump [tag] - Write the region report to the console and to a file\n" +
                              "  prof on / off   - Turn region collection on/off\n" +
                              "  -- rpg (is the modifier engine actually running?) --\n" +
                              "  rpg             - Engine health + a verdict naming the broken link if any\n" +
                              "  rpg skills      - Skill table: points, tier, imported modifier count, applied?\n" +
                              "  rpg derived     - The derived stats after the last recalculation\n" +
                              "  rpg tracked     - Every stat id that recorded a factor, with its count\n" +
                              "  rpg factors <s> - Which skill/perk produced a stat's value, in order\n" +
                              "  rpg res         - Damage multipliers (AS3 gg.vulner)\n" +
                              "  rpg set <s> <n> - Set a skill level, recalculate, print before/after\n" +
                              "  -- spells (acquire and select without a weapon) --\n" +
                              "  spell           - Engine health: caster, book, selection, catalogue size\n" +
                              "  spell list      - The spells the player knows, in acquisition order\n" +
                              "  spell add <id>  - Grant a spell (no weapon needed). e.g. spell add sp_mshit\n" +
                              "  spell select <id> - Choose the spell the Def key (C) casts. A TOGGLE.\n" +
                              "  spell defs [f]  - The catalogue the caster can accept\n" +
                              "  -- inventory (the player's own, through the command seam) --\n" +
                              "  inv             - Engine health: component present, wired, and what is held\n" +
                              "  inv list        - Every item stack with its quantity (same as `inv`)\n" +
                              "  inv add <id> [n]    - Grant n of an item row. e.g. inv add kombu 3\n" +
                              "  inv remove <id> [n] - Take n of an item row\n" +
                              "  inv armor <id>  - Grant armour by id\n" +
                              "  inv ammo <id>   - How many rounds of an ammo id are held\n" +
                              "  -- console ui (the console's own chrome) --\n" +
                              "  ui              - Report whether the quick-action button grid is shown\n" +
                              "  ui buttons on|off - Show / hide the button grid (title bar has a toggle too)\n" +
                              "  -- lua --\n" +
                              "  <lua code>      - Run any Lua expression (e.g. 'return 2+2', 'player:Heal(50)')\n" +
                              "                    Globals: pfe.* (map/fog/rng), player, sim, save, collider, prof, rpg, eff, spell, inv, ui";
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
            //
            // Wrapped, because this path sits ABOVE the Lua try/catch below. A shortcut that threw
            // used to escape to Unity's console and leave the REPL showing the echoed command and
            // nothing else - indistinguishable from a command that quietly did nothing, which is
            // the worst thing a debug tool can look like.
            try
            {
                if (TryRunDebugShortcut(trimmed, out string shortcutResult))
                {
                    AppendLog(shortcutResult);
                    return shortcutResult;
                }
            }
            catch (Exception ex)
            {
                string shortcutError = $"[console] '{trimmed}' threw {ex.GetType().Name}: {ex.Message}";
                AppendLog(shortcutError);
                return shortcutError;
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
                case "editor":
                case "edit":
                case "loadout":
                    if (_playerCommands == null) return false;
                    result = _playerCommands.Editor();
                    return true;

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

                // Deliberately NOT an alias of "ammo": that verb writes the magazine directly, while
                // this one feeds the inventory so the reload path is what gets exercised. Both ids and
                // the count are optional, so a bare 'giveammo' stocks the equipped weapon's own type —
                // the common case, and the one that makes the two verbs easy to tell apart in a log.
                case "giveammo":
                case "ammo+":
                    if (_playerCommands == null) return false;
                    {
                        string giveAmmoId = parts.Length > 1 ? parts[1] : null;
                        int giveAmmoCount = 30;
                        if (parts.Length > 2 && !int.TryParse(parts[2], out giveAmmoCount))
                            { result = "Usage: giveammo [ammoId] [amount]   (defaults: equipped type, 30)"; return true; }
                        result = _playerCommands.GiveAmmo(giveAmmoId, giveAmmoCount);
                        return true;
                    }

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

                case "save":
                case "quicksave":
                    if (_saveCommands == null) return false;
                    result = _saveCommands.Save();
                    return true;

                case "load":
                case "quickload":
                    if (_saveCommands == null) return false;
                    result = _saveCommands.Load();
                    return true;

                case "saves":
                case "saveinfo":
                case "savestatus":
                    if (_saveCommands == null) return false;
                    result = _saveCommands.Status();
                    return true;

                case "tick":
                    if (_simCommands == null) return false;
                    if (!TryParseIntArg(parts, 1, out int rate))
                        { result = "Usage: tick <30|60|90|120>"; return true; }
                    result = _simCommands.SetTickRate(rate);
                    return true;

                case "col":
                case "collider":
                    if (_colliderCommands == null) return false;
                    result = RunColliderShortcut(parts);
                    return true;

                case "tele":
                case "telekinesis":
                    if (_playerCommands == null) return false;
                    result = RunTelekinesisShortcut(parts);
                    return true;

                case "prof":
                case "profile":
                    if (_profilerCommands == null) return false;
                    result = RunProfilerShortcut(parts);
                    return true;

                case "rpg":
                    if (_rpgCommands == null) return false;
                    result = RunRpgShortcut(parts);
                    return true;

                case "eff":
                case "effect":
                    if (_effectCommands == null) return false;
                    result = RunEffectShortcut(parts);
                    return true;

                case "spell":
                case "spells":
                    if (_spellCommands == null) return false;
                    result = RunSpellShortcut(parts);
                    return true;

                // The player's inventory. A bare `inv` is STATUS, matching col/prof/rpg/eff/spell: a verb
                // that mutates on a typo is one mistyped character from an inventory you did not mean to
                // change. Deliberately NOT an alias of `giveammo` — that one predates the inventory and
                // writes through PlayerDebugEditorOverlay; this one goes through the command seam.
                case "inv":
                case "inventory":
                    if (_inventoryCommands == null) return false;
                    result = RunInventoryShortcut(parts);
                    return true;

                // The console's OWN chrome — the quick-action button grid. Deliberately not a `col`
                // channel: the grid is not a world visualisation, and `col on all` must not be able to
                // show or hide the console's buttons. See DevConsoleUiCommands.
                case "ui":
                    if (_uiCommands == null) return false;
                    result = RunUiShortcut(parts);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Dispatch the profiler shortcuts: <c>prof</c> (status), <c>prof dump [tag]</c>,
        /// <c>prof reset</c>, <c>prof on</c>, <c>prof off</c>.
        ///
        /// <para>A bare <c>prof</c> is <b>status</b>, matching <c>col</c>: a verb that flips state on
        /// a typo is one mistyped character from a measurement run that silently collected nothing.
        /// The tag is the whole remainder of the line, so a tag with a space works without quoting
        /// games.</para>
        /// </summary>
        private string RunProfilerShortcut(string[] parts)
        {
            if (parts.Length < 2) return _profilerCommands.Status();

            string sub = parts[1].ToLowerInvariant();

            switch (sub)
            {
                case "dump":
                case "report":
                case "save":
                    return parts.Length > 2
                        ? _profilerCommands.Dump(string.Join("_", parts, 2, parts.Length - 2))
                        : _profilerCommands.Dump();

                case "reset":
                case "clear":
                    return _profilerCommands.Reset();

                case "on":
                case "enable":
                    return _profilerCommands.On();

                case "off":
                case "disable":
                    return _profilerCommands.Off();

                case "status":
                case "info":
                    return _profilerCommands.Status();

                default:
                    // A parse failure is an error, not a fallback: silently treating `prof dmp` as
                    // status would report "enabled, 0 regions" and read as a healthy measurement run.
                    return $"Unknown profiler subject '{parts[1]}'. Use: prof | prof dump [tag] | " +
                           "prof reset | prof on | prof off";
            }
        }

        /// <summary>
        /// Dispatch the <c>rpg</c> shortcuts: <c>rpg</c> (status), <c>rpg skills</c>,
        /// <c>rpg derived</c>, <c>rpg tracked</c>, <c>rpg factors &lt;statId&gt;</c>, <c>rpg res</c>,
        /// <c>rpg set &lt;skillId&gt; &lt;points&gt;</c>.
        ///
        /// <para><b>Why this verb exists.</b> The RPG modifier engine is observable only as a final stat
        /// value, and a wrong value is indistinguishable from an engine that never ran — which is
        /// exactly how nine correct fixes stayed inert without anyone noticing. <c>rpg status</c> prints
        /// the modifier-row count that settles liveness; <c>rpg factors</c> names the source.</para>
        ///
        /// <para>A bare <c>rpg</c> is <b>status</b>, never a toggle, matching <c>col</c> and
        /// <c>prof</c>.</para>
        /// </summary>
        private string RunRpgShortcut(string[] parts)
        {
            if (parts.Length < 2) return _rpgCommands.Status();

            switch (parts[1].ToLowerInvariant())
            {
                case "status":
                case "health":
                    return _rpgCommands.Status();

                case "skills":
                case "skill":
                    return _rpgCommands.Skills();

                case "derived":
                case "stats":
                    return _rpgCommands.Derived();

                case "tracked":
                case "ids":
                    return _rpgCommands.Tracked();

                case "factors":
                case "why":
                    return _rpgCommands.Factors(parts.Length > 2 ? parts[2] : null);

                case "res":
                case "resist":
                case "vulner":
                    return _rpgCommands.Resist();

                case "set":
                    if (parts.Length < 4 || !int.TryParse(parts[3], out int level))
                        return "Usage: rpg set <skillId> <points>    e.g. rpg set tele 5";
                    return _rpgCommands.Set(parts[2], level);

                case "help":
                case "?":
                    return _rpgCommands.Help();

                default:
                    // A parse failure is an error, not a fallback: silently treating `rpg skils` as
                    // status would print a healthy report for a command that never ran.
                    return $"Unknown rpg subject '{parts[1]}'.\n" + _rpgCommands.Help();
            }
        }

        /// <summary>
        /// Dispatch the effect shortcuts: <c>eff</c> (status), <c>eff list</c>, <c>eff unit [n]</c>,
        /// <c>eff add &lt;id&gt; [s] [val]</c>, <c>eff remove &lt;id&gt;</c>, <c>eff clear</c>,
        /// <c>eff defs [filter]</c>, <c>eff dump</c>.
        ///
        /// <para>A bare <c>eff</c> is <b>status</b>, matching <c>col</c>, <c>prof</c> and <c>rpg</c>: a verb
        /// that flips state on a typo is one mistyped character from an effect applied to the wrong
        /// target.</para>
        /// </summary>
        private string RunEffectShortcut(string[] parts)
        {
            if (parts.Length < 2) return _effectCommands.Status();

            string sub = parts[1].ToLowerInvariant();

            switch (sub)
            {
                case "status":
                case "health":
                    return _effectCommands.Status();

                case "list":
                case "ls":
                    return _effectCommands.List();

                case "unit":
                case "target":
                    return _effectCommands.Unit(parts.Length > 2 ? parts[2] : null);

                case "add":
                case "apply":
                    if (parts.Length < 3)
                        return "Usage: eff add <effectId> [seconds] [value]    e.g. eff add burning 10";
                    return _effectCommands.Add(parts[2], ParseFloat(parts, 3), ParseFloat(parts, 4));

                case "remove":
                case "rem":
                case "del":
                    return _effectCommands.Remove(parts.Length > 2 ? parts[2] : null);

                case "clear":
                case "wipe":
                    return _effectCommands.Clear();

                case "defs":
                case "def":
                case "catalog":
                    return _effectCommands.Defs(parts.Length > 2 ? parts[2] : null);

                case "dump":
                case "sk":
                    return _effectCommands.Dump();

                case "help":
                case "?":
                    return _effectCommands.Help();

                default:
                    return $"Unknown eff subject '{parts[1]}'.\n" + _effectCommands.Help();
            }
        }

        /// <summary>
        /// Dispatch the spell shortcuts: <c>spell</c> (status), <c>spell list</c>,
        /// <c>spell add &lt;id&gt;</c>, <c>spell select &lt;id&gt;</c>, <c>spell defs [filter]</c>.
        ///
        /// <para>A bare <c>spell</c> is <b>status</b>, matching <c>col</c>, <c>prof</c>, <c>rpg</c> and
        /// <c>eff</c>: a verb that flips state on a typo is one mistyped character from a spell granted
        /// or selected by accident.</para>
        ///
        /// <para><b><c>add</c> and <c>select</c> are separate on purpose</b> and must not be merged into
        /// one verb. In AS3 they are different calls at different times — <c>addSpell</c> happens when
        /// the item is used, <c>changeSpell</c> when it is selected — and <c>changeSpell</c> is a
        /// <i>toggle</i>. A merged <c>spell add</c> that also selected would deselect on the second run,
        /// which reads as "the command stopped working".</para>
        /// </summary>
        private string RunSpellShortcut(string[] parts)
        {
            if (parts.Length < 2) return _spellCommands.Status();

            string sub = parts[1].ToLowerInvariant();

            switch (sub)
            {
                case "status":
                case "health":
                    return _spellCommands.Status();

                case "list":
                case "ls":
                    return _spellCommands.List();

                case "add":
                case "grant":
                case "learn":
                    if (parts.Length < 3)
                        return "Usage: spell add <spellId>    e.g. spell add sp_mshit   (see `spell defs`)";
                    return _spellCommands.Add(parts[2]);

                case "select":
                case "use":
                    if (parts.Length < 3)
                        return "Usage: spell select <spellId>    (a toggle — the same id twice deselects)";
                    return _spellCommands.Select(parts[2]);

                case "defs":
                case "def":
                case "catalog":
                    return _spellCommands.Defs(parts.Length > 2 ? parts[2] : null);

                case "help":
                case "?":
                    return _spellCommands.Help();

                default:
                    return $"Unknown spell subject '{parts[1]}'.\n" + _spellCommands.Help();
            }
        }

        /// <summary>
        /// Dispatch the <c>inv</c> shortcuts: <c>inv</c> (status), <c>inv list</c>,
        /// <c>inv add &lt;id&gt; [n]</c>, <c>inv remove &lt;id&gt; [n]</c>,
        /// <c>inv armor &lt;id&gt;</c>, <c>inv ammo &lt;id&gt;</c>.
        ///
        /// <para><b>Every mutating subcommand goes through <see cref="PFE.Systems.Inventory.IInventoryCommandSink"/></b>
        /// — the same seam gameplay and a future host will use — so this verb exercises the real path
        /// (validate → resolve → apply) instead of a shortcut around it.</para>
        ///
        /// <para>A parse failure is an error, not a fallback: silently treating <c>inv ad x</c> as status
        /// would print an empty inventory and read as "the inventory is broken".</para>
        /// </summary>
        private string RunInventoryShortcut(string[] parts)
        {
            if (parts.Length < 2) return _inventoryCommands.Status();

            string sub = parts[1].ToLowerInvariant();

            switch (sub)
            {
                // `list` is status: the status report already prints every stack, and a second listing
                // implementation is a second thing to keep in step.
                case "status":
                case "health":
                case "list":
                case "ls":
                    return _inventoryCommands.Status();

                case "add":
                case "give":
                    {
                        if (parts.Length < 3) return "Usage: inv add <itemId> [amount]";
                        int amount = 1;
                        if (parts.Length > 3 && !TryParseIntArg(parts, 3, out amount))
                            return "Usage: inv add <itemId> [amount]";
                        return _inventoryCommands.Add(parts[2], amount);
                    }

                case "remove":
                case "rm":
                case "take":
                    {
                        if (parts.Length < 3) return "Usage: inv remove <itemId> [amount]";
                        int amount = 1;
                        if (parts.Length > 3 && !TryParseIntArg(parts, 3, out amount))
                            return "Usage: inv remove <itemId> [amount]";
                        return _inventoryCommands.Remove(parts[2], amount);
                    }

                case "armor":
                case "armour":
                    if (parts.Length < 3) return "Usage: inv armor <armorId>";
                    return _inventoryCommands.Armor(parts[2]);

                case "ammo":
                    if (parts.Length < 3) return "Usage: inv ammo <ammoId>";
                    return _inventoryCommands.Ammo(parts[2]);

                default:
                    return $"Unknown inventory subject '{parts[1]}'. Use: inv | inv list | " +
                           "inv add <id> [n] | inv remove <id> [n] | inv armor <id> | inv ammo <id>";
            }
        }

        /// <summary>
        /// Dispatch the <c>ui</c> shortcuts: the console's own chrome. <c>ui</c> and <c>ui buttons</c>
        /// are <b>status</b>; <c>ui buttons on|off</c> sets the flag.
        ///
        /// <para>A bare verb is status, matching <c>col</c>, <c>prof</c>, <c>rpg</c>, <c>eff</c> and
        /// <c>spell</c>: a verb that flips on a typo is one mistyped character away from the button grid
        /// vanishing (or reappearing) with no visible cause.</para>
        /// </summary>
        private string RunUiShortcut(string[] parts)
        {
            if (parts.Length < 2) return _uiCommands.Status();

            switch (parts[1].ToLowerInvariant())
            {
                case "buttons":
                case "button":
                    if (parts.Length < 3) return _uiCommands.Status();

                    switch (parts[2].ToLowerInvariant())
                    {
                        case "on":
                        case "show":
                            return _uiCommands.SetButtons(true);

                        case "off":
                        case "hide":
                            return _uiCommands.SetButtons(false);

                        default:
                            // An unknown state is an error, not a fallback: silently treating `ui buttons
                            // of` as status would report "on" and read as a command that did nothing.
                            return $"Unknown ui state '{parts[2]}'. Use: ui buttons on | ui buttons off";
                    }

                case "status":
                    return _uiCommands.Status();

                case "help":
                case "?":
                    return _uiCommands.Help();

                default:
                    return $"Unknown ui subject '{parts[1]}'.\n" + _uiCommands.Help();
            }
        }

        /// <summary>Parse <c>parts[index]</c> as a float, treating a missing or unparseable token as 0 —
        /// which for both <c>eff add</c> overrides means "use the definition's own value".</summary>
        private static float ParseFloat(string[] parts, int index)
        {
            if (parts == null || index < 0 || index >= parts.Length) return 0f;
            return float.TryParse(parts[index], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
        }

        /// <summary>
        /// Dispatch the overlay shortcuts. Split out of the switch because this is the one verb with
        /// sub-verbs and a free-form argument tail.
        ///
        /// <para>The channel list is the remainder of the line, re-joined with commas so that both
        /// <c>col on doors,triggers</c> and <c>col on doors triggers</c> reach the parser as the same
        /// thing. The parsers own the vocabulary; nothing here knows a channel or tile-type name.</para>
        ///
        /// <para><c>col</c> on its own is <b>status</b>, not "toggle". A bare verb that flips state is
        /// one mistyped character away from an overlay silently appearing or vanishing, and the reply
        /// is the only thing that would tell you.</para>
        /// </summary>
        private string RunColliderShortcut(string[] parts)
        {
            if (parts.Length < 2) return _colliderCommands.Status();

            string sub = parts[1].ToLowerInvariant();
            string spec = parts.Length > 2
                ? string.Join(",", parts, 2, parts.Length - 2)
                : string.Empty;

            switch (sub)
            {
                // Every overlay, or a named subset. `col on` with no list means all.
                case "on":
                case "all":
                case "show":
                    return _colliderCommands.On(spec);

                // `col off` with no list means all; `col off room` clears just that one.
                case "off":
                case "hide":
                case "none":
                    return _colliderCommands.Off(spec);

                // Channel-scoped shorthands. These set the sub-filter as well as the channel, which
                // is the whole reason they exist next to `col on tiles`.
                case "tiles":
                case "tile":
                    return _colliderCommands.Tiles(spec);

                case "units":
                case "unit":
                    return _colliderCommands.Units(spec);

                case "probe":
                case "under":
                    return _colliderCommands.Probe();

                // Every tile's collider top vs the VISIBLE top of its sprite. `probe` answers "what is
                // under the player now"; this answers "is the drawn surface where the collider says it
                // is" for the whole room, which is the question a screenshot at ~0.5 px per game px
                // cannot resolve.
                case "gaps":
                case "surface":
                    return _colliderCommands.Gaps();

                case "status":
                case "list":
                    return _colliderCommands.Status();

                case "help":
                case "?":
                    return _colliderCommands.Help();

                default:
                    // A bare channel name is accepted as shorthand for turning it on, because that is
                    // overwhelmingly what is meant by `col doors`. It is still explicit about which
                    // channel it resolved to, so a near-miss is visible rather than silent.
                    if (DebugOverlayChannels.TryMatch(sub, out DebugOverlayChannel channel))
                    {
                        return _colliderCommands.On(DebugOverlayChannels.NameOf(channel));
                    }

                    return $"Unknown overlay subject '{parts[1]}'.\n" + _colliderCommands.Help();
            }
        }

        /// <summary>
        /// Dispatch the <c>tele</c> shortcuts: the telekinesis / teleport Q path.
        ///
        /// <para><b>Why this verb exists.</b> "I press Q and nothing happens" has four unrelated causes
        /// — the key never arrives, the controller has no room, the cursor is nowhere near a prop, or one
        /// gate term refuses — and they are indistinguishable from the outside. <c>tele on</c> traces
        /// each step; <c>tele probe</c> reports the current state without pressing anything. Together
        /// they replace guessing with a named reason.</para>
        ///
        /// <para>Like <c>col</c>, a bare verb is <b>status</b>, never a toggle: a verb that flips on a
        /// typo is one keystroke away from silently changing what you are looking at.</para>
        /// </summary>
        private string RunTelekinesisShortcut(string[] parts)
        {
            if (parts.Length < 2)
            {
                return _playerCommands.TeleStatus();
            }

            switch (parts[1].ToLowerInvariant())
            {
                case "on":
                case "trace":
                    return _playerCommands.TeleTrace(true);

                case "off":
                    return _playerCommands.TeleTrace(false);

                case "probe":
                case "where":
                    return _playerCommands.TeleProbe();

                case "status":
                    return _playerCommands.TeleStatus();

                case "help":
                    return "tele              - report whether the [tele] trace is on\n" +
                           "tele on           - trace the Q path to the Console (grab gate + teleport charge)\n" +
                           "tele off          - stop tracing\n" +
                           "tele probe        - dump what the grab path sees right now, in test order\n" +
                           "Lua: player:TeleTrace(true) | player:TeleProbe()";

                default:
                    return $"Unknown tele sub-command '{parts[1]}'. Use: tele on | tele off | tele probe";
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
