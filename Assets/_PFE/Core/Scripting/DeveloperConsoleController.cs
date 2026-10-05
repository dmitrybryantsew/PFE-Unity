using System;
using MoonSharp.Interpreter;
using PFE.Core;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;
using VContainer;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Local-only developer console MonoBehaviour overlay and IMGUI view.
    /// Delegates execution to DeveloperConsoleService.
    /// </summary>
    [LocalOnly]
    public sealed class DeveloperConsoleController : MonoBehaviour
    {
        public static DeveloperConsoleController Instance { get; private set; }

        private DeveloperConsoleService _service;
        private IObjectResolver _resolver;
        private string _inputBuffer = string.Empty;
        private Vector2 _scrollPos;

        // Tracks the history length so the log can follow new output (see OnGUI).
        private int _lastHistoryCount = -1;

        // Debug command objects. Built once, then re-wired on every SyncDependencies so a
        // respawned player or a rebuilt container is picked up without reallocating them.
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

        // The quick-action grid's buttons and their actions. Built once (OnGUI runs several times per
        // frame) and rebuilt never — the lambdas read the live `_service` field, so a respawned service
        // is picked up without reallocating this array.
        private (string Label, Action Invoke)[] _quickButtons;

        // Vertical space the grid occupies, recomputed each OnGUI so the log below it gets exactly what
        // is left. Zero when the grid is hidden.
        private const float InputRowHeight = 26f;
        private const float RowGap = 4f;

        private const KeyCode ToggleKey1 = KeyCode.BackQuote;
        private const KeyCode ToggleKey2 = KeyCode.F1;

        public DeveloperConsoleService Service => _service;

        [Inject]
        public void Construct(DeveloperConsoleService service, IObjectResolver resolver)
        {
            _service = service;
            _resolver = resolver;
            SyncDependencies();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            if (_service == null)
            {
                _service = DeveloperConsoleService.Instance ?? new DeveloperConsoleService();
            }

            if (Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeInstance()
        {
            if (Instance == null && FindFirstObjectByType<DeveloperConsoleController>() == null)
            {
                var go = new GameObject("DeveloperConsole");
                Instance = go.AddComponent<DeveloperConsoleController>();
                DontDestroyOnLoad(go);
            }
        }

        private void Start()
        {
            SyncDependencies();
        }

        private void OnDestroy()
        {
            // The profiler is a static class, so the context source the profiler commands register
            // outlives this object and would keep it -- and the LandMap it holds -- alive after the
            // scene unloads. Guarded on Instance == this because Awake destroys a duplicate console,
            // and that duplicate must not clear the live one's source.
            if (Instance == this)
            {
                _profilerCommands?.Unwire();
            }
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(ToggleKey1) || UnityEngine.Input.GetKeyDown(ToggleKey2))
            {
                if (_service != null)
                {
                    _service.IsOpen = !_service.IsOpen;
                }
            }
        }

        public void SyncDependencies()
        {
            if (_service == null) return;

            LandMap landMap = null;
            if (_resolver != null)
            {
                _resolver.TryResolve(out landMap);
            }

            RoomVisualController visual = GetRoomVisualController();
            Action<bool> setFogAction = null;
            Action revealFogAction = null;
            Func<bool> isFogDisabledFunc = null;

            if (visual != null)
            {
                setFogAction = visual.SetFogOfWarDisabled;
                revealFogAction = visual.RevealFogOfWar;
                isFogDisabledFunc = () => visual.FogOfWarDisabled;
            }

            _service.SetDependencies(null, landMap, setFogAction, revealFogAction, isFogDisabledFunc);
            SyncCommandObjects(landMap);
        }

        /// <summary>
        /// Resolve everything the <c>player</c> / <c>sim</c> console commands need and hand them to
        /// the service.
        ///
        /// <para>Every lookup is a TryResolve because the console is a debug tool that must survive
        /// a partially built container — it is most useful exactly when something else has failed to
        /// wire up. A missing dependency degrades to "that command says it is unavailable", which is
        /// more useful than an exception thrown from the tool you opened to debug the problem.</para>
        /// </summary>
        private void SyncCommandObjects(LandMap landMap)
        {
            _playerCommands ??= new DevConsolePlayerCommands();
            _simCommands ??= new DevConsoleSimCommands();
            _saveCommands ??= new DevConsoleSaveCommands();
            _colliderCommands ??= new DevConsoleColliderCommands();
            _profilerCommands ??= new DevConsoleProfilerCommands();
            _rpgCommands ??= new DevConsoleRpgCommands();
            _effectCommands ??= new DevConsoleEffectCommands();
            _spellCommands ??= new DevConsoleSpellCommands();
            _inventoryCommands ??= new DevConsoleInventoryCommands();
            _uiCommands ??= new DevConsoleUiCommands();

            if (_resolver != null)
            {
                _resolver.TryResolve(out PFE.Data.GameDatabase database);
                _resolver.TryResolve(out PFE.Systems.Weapons.PlayerWeaponLoadout loadout);
                _resolver.TryResolve(out PFE.Core.SimClock simClock);
                _resolver.TryResolve(out PFE.Core.SimLoop simLoop);
                _resolver.TryResolve(out PFE.Core.GameManager gameManager);
                _resolver.TryResolve(out MessagePipe.IPublisher<PFE.Core.Messages.HealMessage> healPublisher);

                LandMap resolvedMap = landMap ?? ResolveLandMap();

                _playerCommands.Wire(
                    resolvedMap,
                    database,
                    loadout,
                    healPublisher,
                    () => FindFirstObjectByType<PFE.Entities.Player.PlayerController>());

                _simCommands.Wire(simClock);

                // SaveManager.Instance is a self-creating singleton, so the save commands need no
                // registration of their own - only the GameManager, which owns the IsInitialized
                // gate that stops a save during the world build.
                _saveCommands.Wire(gameManager, resolvedMap);

                // The map as well as the loop: `prof status` reports the current room's prop counts
                // beside the tick rate, because every prop-layer region's cost is a loop over one of
                // those lists and a per-tick figure is uninterpretable without them.
                _profilerCommands.Wire(simLoop, resolvedMap);
            }

            // The collider commands need no dependency injection: the overlay finds its own scene
            // objects, and its state lives in PfeDebugSettings. That is deliberate — this is the
            // tool you reach for when something else failed to wire up, so it must not depend on
            // anything having been wired up.
            //
            // The RPG commands are wired unconditionally for the same reason: they need only a scene
            // lookup, and CharacterStats resolves its own database and level curve. `rpg status` is
            // specifically the command you run when you suspect nothing wired up, so it must not be
            // the thing that fails when nothing wired up.
            _rpgCommands.Wire(() => FindFirstObjectByType<PFE.Entities.Player.PlayerController>());

            // The effect commands are wired unconditionally for the same reason: they need only a scene
            // lookup (the player, or any UnitController) and read the live effect set from it, so they
            // must work precisely when the effect wiring is what failed.
            _effectCommands.Wire(() => FindFirstObjectByType<PFE.Entities.Player.PlayerController>());

            // The spell commands are wired unconditionally for the same reason as `rpg` and `eff`: they
            // need only a scene lookup for the player (the caster is a component on it), so `spell
            // status` must work precisely when the spell wiring is what failed.
            _spellCommands.Wire(() => FindFirstObjectByType<PFE.Entities.Player.PlayerController>());

            // Wired unconditionally for the same reason as `spell`: the inventory is a component on the
            // player, so `inv status` must work precisely when the inventory wiring is what failed.
            _inventoryCommands.Wire(() => FindFirstObjectByType<PFE.Entities.Player.PlayerController>());

            _service.SetCommandObjects(_playerCommands, _simCommands, _saveCommands, _colliderCommands, _profilerCommands, _rpgCommands, _effectCommands, _spellCommands, _uiCommands, _inventoryCommands);
        }

        private LandMap ResolveLandMap()
        {
            if (_resolver != null && _resolver.TryResolve(out LandMap resolved) && resolved != null)
                return resolved;

            return null;
        }

        private RoomVisualController GetRoomVisualController()
        {
            var bridge = FindFirstObjectByType<MapBridge>();
            if (bridge != null && bridge.VisualController != null)
            {
                return bridge.VisualController;
            }
            return FindFirstObjectByType<RoomVisualController>();
        }

        private void OnGUI()
        {
            if (_service == null) return;

            if (!_service.IsOpen)
            {
                if (GUI.Button(new Rect(Screen.width - 110, 5, 100, 22), "Console (~)"))
                {
                    _service.IsOpen = true;
                }
                return;
            }

            float height = Screen.height * 0.45f;
            float width = Screen.width;
            float areaWidth = width - 20f;
            float areaHeight = height - 30f;

            GUI.Box(new Rect(0, 0, width, height), string.Empty);
            GUI.Box(new Rect(0, 0, width, height), "=== PFE Developer Console (Lua REPL & Cheats) [Press ~ or F1 to Close] ===");

            // The grid's toggle lives on the title bar, NOT in the grid: a control that hides the grid
            // must not live inside the thing it hides, or hiding it removes the way back.
            DrawQuickButtonsToggle(width);

            GUILayout.BeginArea(new Rect(10, 25, areaWidth, areaHeight));

            // The quick-action buttons are a grid whose cells divide the console's own width. The old
            // row was one horizontal strip of fixed-width buttons, and a horizontal strip does not
            // wrap: once there were more buttons than fitted, the last few ran off the right edge and
            // could not be clicked. A grid cannot overflow, at any window size.
            //
            // Drawn with explicit rects because GUILayout has no wrapping flow, then reserved with one
            // Space so the log below starts exactly where the grid ends.
            float buttonsHeight = 0f;
            if (QuickButtonsShown)
            {
                buttonsHeight = DrawQuickButtonGrid(QuickButtons, areaWidth);
                GUILayout.Space(buttonsHeight);
            }

            var history = _service.History;

            // Follow the tail. GUILayout's scroll view has no notion of "follow", so a command's
            // reply is appended below the fold and stays there until the user drags - which is
            // exactly how a command that worked reads as "it did nothing".
            if (history.Count != _lastHistoryCount)
            {
                _lastHistoryCount = history.Count;
                _scrollPos.y = float.MaxValue;
            }

            float logHeight = Mathf.Max(40f, areaHeight - buttonsHeight - InputRowHeight - RowGap);
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(logHeight));
            for (int i = 0; i < history.Count; i++)
            {
                GUILayout.Label(history[i]);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(RowGap);
            GUILayout.BeginHorizontal();
            GUILayout.Label(">", GUILayout.Width(15));
            GUI.SetNextControlName("ConsoleInputField");
            _inputBuffer = GUILayout.TextField(_inputBuffer);

            Event e = Event.current;
            if (e.isKey && e.keyCode == KeyCode.Return && GUI.GetNameOfFocusedControl() == "ConsoleInputField")
            {
                if (!string.IsNullOrWhiteSpace(_inputBuffer))
                {
                    SyncDependencies();
                    _service.ExecuteInput(_inputBuffer);
                    _inputBuffer = string.Empty;
                }
            }

            if (GUILayout.Button("Submit", GUILayout.Width(70)))
            {
                if (!string.IsNullOrWhiteSpace(_inputBuffer))
                {
                    SyncDependencies();
                    _service.ExecuteInput(_inputBuffer);
                    _inputBuffer = string.Empty;
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            GUI.FocusControl("ConsoleInputField");
        }

        // ── Quick-action grid ────────────────────────────────────────────────

        /// <summary>
        /// Whether the grid is drawn. Read live from the settings asset on every OnGUI, so a console
        /// write or an Inspector edit lands on the next frame — the same "one value, three front ends"
        /// contract the debug overlays use.
        ///
        /// <para>Defaults to <b>shown</b> when there is no settings asset, which is the opposite of the
        /// overlay default and deliberate: an overlay that defaults to "draw" is permanent clutter in
        /// the game, while this is the console's own control surface — hiding it would remove the primary
        /// way to drive a tool that is only visible while it is open.</para>
        /// </summary>
        private static bool QuickButtonsShown
        {
            get
            {
                PfeDebugSettings settings = DebugOverlays.Settings;
                return settings == null || settings.ShowConsoleQuickButtons;
            }
        }

        /// <summary>
        /// The grid's buttons, in order. Built once — OnGUI runs several times per frame and this array
        /// would otherwise be reallocated on every pass. The lambdas read the live <c>_service</c> field,
        /// so a respawned service is picked up without rebuilding the array.
        ///
        /// <para><b>Why the two overlay groups are separate and not one "all".</b> The six collider
        /// visualisations and the four text readouts answer different questions, and turning on ten
        /// things when you want one is how an overlay stops being readable. <c>legend</c> is included in
        /// "Colliders on" on purpose: that button's whole workflow is "click, close the console,
        /// screenshot", and a key-less picture of coloured boxes is only readable by whoever chose the
        /// colours. It stays an independent channel, so <c>col off legend</c> still strips the text plate
        /// off the geometry when the geometry is the whole point.</para>
        ///
        /// <para><b>Why "Tele probe" is not a toggle.</b> It is one click for the "I press Q and nothing
        /// happens" report, and a button that flips a trace on and off is one mis-click away from an
        /// empty Console that looks exactly like the bug you opened it to find. Use <c>tele on</c> for
        /// the live trace.</para>
        /// </summary>
        private (string Label, Action Invoke)[] QuickButtons
        {
            get
            {
                return _quickButtons ??= new (string Label, Action Invoke)[]
                {
                    ("Reveal Map (map)", () => RunCommand("map")),
                    ("Toggle Fog (fog)", () => RunCommand("fog")),
                    // One-click save/load: the F-row is claimed by plugins and debug overlays, so the
                    // round-trip is exercised from here instead.
                    ("Save (save)", () => RunCommand("save")),
                    ("Load (load)", () => RunCommand("load")),
                    ("Saves", () => RunCommand("saves")),
                    ("Player Editor (F2)", TogglePlayerEditor),
                    ("Colliders on", () => RunCommand("col on tiles,units,doors,triggers,transitions,objects,legend")),
                    ("Text on", () => RunCommand("col on tilequery,room,pool,clock")),
                    ("Probe", () => RunCommand("col probe")),
                    ("Tele probe", () => RunCommand("tele probe")),
                    ("Overlays off", () => RunCommand("col off")),
                    ("Help", () => RunCommand("help")),
                    ("Clear", ClearConsole),
                    ("Close [X]", CloseConsole),
                };
            }
        }

        /// <summary>
        /// Draw the grid and return the vertical space it used, so the caller can reserve exactly that
        /// much for it. Cells are placed with explicit rects — GUILayout cannot wrap — and the arithmetic
        /// lives in <see cref="ConsoleButtonGrid"/> so the "does it fit?" contract is covered by an
        /// offline test rather than by a screenshot.
        /// </summary>
        private float DrawQuickButtonGrid((string Label, Action Invoke)[] buttons, float areaWidth)
        {
            ConsoleButtonGridLayout grid = ConsoleButtonGrid.Compute(buttons.Length, areaWidth);

            for (int i = 0; i < buttons.Length; i++)
            {
                int row = i / grid.Columns;
                int column = i % grid.Columns;

                var rect = new Rect(
                    grid.ColumnX(column),
                    grid.RowY(row),
                    Mathf.Max(1f, grid.CellDrawWidth),
                    grid.CellHeight);

                if (GUI.Button(rect, buttons[i].Label))
                {
                    buttons[i].Invoke?.Invoke();
                }
            }

            return grid.TotalHeight;
        }

        /// <summary>
        /// The title-bar toggle for the grid. It writes the same <see cref="PfeDebugSettings"/> field the
        /// Inspector and <c>ui buttons on|off</c> write, so the three cannot disagree. Absent when the
        /// project has no settings asset — in that case the grid is always shown and there is nothing to
        /// persist a choice to.
        /// </summary>
        private static void DrawQuickButtonsToggle(float width)
        {
            PfeDebugSettings settings = DebugOverlays.Settings;
            if (settings == null) return;

            bool shown = settings.ShowConsoleQuickButtons;
            string label = shown ? "Buttons: on  (hide)" : "Buttons: off  (show)";

            if (GUI.Button(new Rect(width - 180f, 3f, 170f, 20f), label))
            {
                settings.ShowConsoleQuickButtons = !shown;
            }
        }

        private void RunCommand(string command)
        {
            SyncDependencies();
            _service.ExecuteInput(command);
        }

        private void TogglePlayerEditor()
        {
            if (PlayerDebugEditorOverlay.Instance != null)
            {
                PlayerDebugEditorOverlay.Instance.IsOpen = !PlayerDebugEditorOverlay.Instance.IsOpen;
            }
        }

        private void ClearConsole()
        {
            _service.ClearHistory();
        }

        private void CloseConsole()
        {
            _service.IsOpen = false;
        }

        public string ExecuteInput(string input)
        {
            SyncDependencies();
            return _service?.ExecuteInput(input) ?? string.Empty;
        }

        public string RevealMap()
        {
            SyncDependencies();
            return _service?.RevealMap() ?? string.Empty;
        }

        public string ToggleFog()
        {
            SyncDependencies();
            return _service?.ToggleFog() ?? string.Empty;
        }

        public string SetFog(bool enabled)
        {
            SyncDependencies();
            return _service?.SetFog(enabled) ?? string.Empty;
        }
    }
}
