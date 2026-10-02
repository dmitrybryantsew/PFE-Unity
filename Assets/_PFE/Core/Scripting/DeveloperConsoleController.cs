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
            _service.SetCommandObjects(_playerCommands, _simCommands, _saveCommands, _colliderCommands, _profilerCommands);
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

            GUI.Box(new Rect(0, 0, width, height), string.Empty);
            GUI.Box(new Rect(0, 0, width, height), "=== PFE Developer Console (Lua REPL & Cheats) [Press ~ or F1 to Close] ===");

            GUILayout.BeginArea(new Rect(10, 25, width - 20, height - 30));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reveal Map (map)", GUILayout.Width(130)))
            {
                SyncDependencies();
                _service.ExecuteInput("map");
            }
            if (GUILayout.Button("Toggle Fog (fog)", GUILayout.Width(130)))
            {
                SyncDependencies();
                _service.ExecuteInput("fog");
            }
            // One-click save/load: the F-row is claimed by plugins and debug overlays, so the
            // round-trip is exercised from here instead.
            if (GUILayout.Button("Save (save)", GUILayout.Width(95)))
            {
                SyncDependencies();
                _service.ExecuteInput("save");
            }
            if (GUILayout.Button("Load (load)", GUILayout.Width(95)))
            {
                SyncDependencies();
                _service.ExecuteInput("load");
            }
            if (GUILayout.Button("Saves", GUILayout.Width(60)))
            {
                SyncDependencies();
                _service.ExecuteInput("saves");
            }
            // One-click overlays. The console covers 45% of the screen, so the workflow is "click
            // here, close the console, screenshot" — and the F5/F6 hotkeys cover the case where the
            // console should not be open at all.
            //
            // Deliberately two groups rather than one "all": the six collider visualisations and the
            // four text readouts answer different questions, and turning on ten things when you want
            // one is how an overlay stops being readable.
            // `legend` is included on purpose. This button's whole workflow is "click, close the
            // console, screenshot", and the legend is what turns that screenshot back into something
            // readable — coloured boxes with no key are only interpretable by whoever wrote the
            // colours. It stays an independent channel, so `col off legend` still strips the text
            // plate off the geometry when the geometry is the whole point.
            if (GUILayout.Button("Colliders on", GUILayout.Width(95)))
            {
                SyncDependencies();
                _service.ExecuteInput("col on tiles,units,doors,triggers,transitions,objects,legend");
            }
            if (GUILayout.Button("Text on", GUILayout.Width(65)))
            {
                SyncDependencies();
                _service.ExecuteInput("col on tilequery,room,pool,clock");
            }
            if (GUILayout.Button("Probe", GUILayout.Width(55)))
            {
                SyncDependencies();
                _service.ExecuteInput("col probe");
            }
            // One click for the "I press Q and nothing happens" report. Deliberately NOT a toggle:
            // a button that flips a trace on/off is one mis-click away from an empty Console that
            // looks exactly like the bug you opened it to find. Use `tele on` for the live trace.
            if (GUILayout.Button("Tele probe", GUILayout.Width(80)))
            {
                SyncDependencies();
                _service.ExecuteInput("tele probe");
            }
            if (GUILayout.Button("Overlays off", GUILayout.Width(85)))
            {
                SyncDependencies();
                _service.ExecuteInput("col off");
            }
            if (GUILayout.Button("Help", GUILayout.Width(60)))
            {
                _service.ExecuteInput("help");
            }
            if (GUILayout.Button("Clear", GUILayout.Width(60)))
            {
                _service.ClearHistory();
            }
            if (GUILayout.Button("Close [X]", GUILayout.Width(75)))
            {
                _service.IsOpen = false;
            }
            GUILayout.EndHorizontal();

            var history = _service.History;

            // Follow the tail. GUILayout's scroll view has no notion of "follow", so a command's
            // reply is appended below the fold and stays there until the user drags - which is
            // exactly how a command that worked reads as "it did nothing".
            if (history.Count != _lastHistoryCount)
            {
                _lastHistoryCount = history.Count;
                _scrollPos.y = float.MaxValue;
            }

            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(height - 95));
            for (int i = 0; i < history.Count; i++)
            {
                GUILayout.Label(history[i]);
            }
            GUILayout.EndScrollView();

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
