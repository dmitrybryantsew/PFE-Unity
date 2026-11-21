using UnityEngine;
using UnityEngine.InputSystem;

namespace PFE.Core
{
    /// <summary>
    /// On-screen FPS readout for builds, toggled with <b>F4</b>. Created at runtime by
    /// <see cref="RuntimeInitializeOnLoadMethodAttribute"/> in every scene (including the main
    /// menu), so it needs no scene setup and costs nothing while hidden — the same shape as
    /// <c>PlayerDebugEditorOverlay</c>, deliberately separate from <see cref="SimDebugOverlay"/>,
    /// whose fps line lives behind the <c>col on clock</c> console channel and a panel of sim
    /// numbers this readout does not want to require.
    ///
    /// <para><b>F4 because every other low F-key is taken</b>: F1 console, F2 player editor,
    /// F3 enemy AI, F5/F6 colliders, F8 triggers, F9 doors, F10 objects, F11 quickload. The key
    /// is read through BOTH the legacy and the new Input System (the established dual-read
    /// pattern of the other overlays), so the toggle works whichever handler a scene runs.</para>
    ///
    /// <para><b>The average is a 30-frame window in <c>Update</c>, never <c>OnGUI</c></b>:
    /// OnGUI runs Layout AND Repaint, so sampling there would halve the frame time. Unscaled
    /// delta time, so a <c>timescale</c> change or a sim pause cannot fake the reading.</para>
    /// </summary>
    public sealed class FpsCounterOverlay : MonoBehaviour
    {
        public static FpsCounterOverlay Instance { get; private set; }

        private const KeyCode ToggleKey = KeyCode.F4;

        /// <summary>Frames per average. Half of SimDebugOverlay's window: this readout exists to
        /// catch hitches, and a 60-frame window smooths a 250 ms spike into invisibility.</summary>
        private const int FrameWindow = 30;

        // Drawn top-right, just under the developer console's "Console (~)" button (Screen.width-110, 5).
        private const float Width = 150f;
        private const float Height = 22f;
        private const float MarginX = 10f;
        private const float Top = 30f;

        private GUIStyle _style;
        private Texture2D _background;

        private bool _visible;
        private int _frameCount;
        private float _frameSeconds;
        private float _fps;
        private float _frameMs;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeInstance()
        {
            if (Instance == null && FindFirstObjectByType<FpsCounterOverlay>() == null)
            {
                var go = new GameObject(nameof(FpsCounterOverlay));
                Instance = go.AddComponent<FpsCounterOverlay>();
                DontDestroyOnLoad(go);
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        private void Update()
        {
            bool toggle = UnityEngine.Input.GetKeyDown(ToggleKey)
                          || (Keyboard.current != null && Keyboard.current.f4Key.wasPressedThisFrame);
            if (toggle)
            {
                _visible = !_visible;
            }

            // UnityEngine.Time, fully qualified: namespace PFE.Core also contains PFE.Core.Time
            // (UnityTimeProvider.cs), so a bare `Time` binds to that namespace — see SimDebugOverlay.
            _frameSeconds += UnityEngine.Time.unscaledDeltaTime;
            _frameCount++;

            if (_frameCount >= FrameWindow)
            {
                _frameMs = _frameSeconds / _frameCount * 1000f;
                _fps = _frameCount / Mathf.Max(_frameSeconds, 1e-5f);
                _frameCount = 0;
                _frameSeconds = 0f;
            }
        }

        private void OnGUI()
        {
            if (!_visible)
            {
                return;
            }

            EnsureStyle();

            var rect = new Rect(Screen.width - Width - MarginX, Top, Width, Height);
            GUI.DrawTexture(rect, _background);
            GUI.Label(new Rect(rect.x + 8f, rect.y, rect.width - 16f, rect.height),
                      _fps.ToString("0") + " FPS  (" + _frameMs.ToString("0.0") + " ms)", _style);
        }

        private void EnsureStyle()
        {
            if (_style != null)
            {
                return;
            }

            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                richText = false
            };
            _style.normal.textColor = new Color(0.85f, 0.95f, 0.85f, 1f);

            _background = new Texture2D(1, 1);
            _background.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
            _background.Apply();
        }

        private void OnDestroy()
        {
            if (_background != null)
            {
                Destroy(_background);
                _background = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
