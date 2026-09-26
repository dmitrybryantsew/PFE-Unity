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
