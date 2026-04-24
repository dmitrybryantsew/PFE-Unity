using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.UI.MainMenu
{
    public enum MainMenuScreen
    {
        Title,
        NewGame,
        LoadGame,
        Options,
        Mods,
        About
    }

    /// <summary>
    /// Simple screen state manager for the main menu.
    /// Each top-level menu screen is represented by a CanvasGroup root.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuManager : MonoBehaviour
    {
        [Serializable]
        private struct ScreenView
        {
            public MainMenuScreen screen;
            public CanvasGroup root;
        }

        [Header("Screens")]
        [SerializeField] private MainMenuScreen initialScreen = MainMenuScreen.Title;
        [SerializeField] private ScreenView[] screens = Array.Empty<ScreenView>();
        [SerializeField] private bool deactivateHiddenScreens = true;

        private readonly Dictionary<MainMenuScreen, CanvasGroup> _screenLookup = new();
        private readonly Stack<MainMenuScreen> _history = new();

        public MainMenuScreen CurrentScreen { get; private set; } = MainMenuScreen.Title;
        public bool IsOverlayOpen => CurrentScreen != MainMenuScreen.Title;
        public bool CanGoBack => _history.Count > 0;

        public event Action<MainMenuScreen> ScreenChanged;

        private void Awake()
        {
            if (screens.Length == 0)
            {
                CurrentScreen = initialScreen;
                return;
            }

            RebuildLookup();
            ResetToInitialScreen();
        }

        public void ConfigureRuntimeScreens(
            IDictionary<MainMenuScreen, CanvasGroup> screenRoots,
            MainMenuScreen startScreen = MainMenuScreen.Title)
        {
            _screenLookup.Clear();

            if (screenRoots != null)
            {
                foreach (var pair in screenRoots)
                {
                    if (pair.Value == null)
                    {
                        continue;
                    }

                    _screenLookup[pair.Key] = pair.Value;
                }
            }

            _history.Clear();
            ShowScreenImmediate(startScreen);
        }

        public void ShowTitle()
        {
            _history.Clear();
            ShowScreenImmediate(MainMenuScreen.Title);
        }

        public void ShowNewGame()
        {
            OpenScreen(MainMenuScreen.NewGame);
        }

        public void ShowLoadGame()
        {
            OpenScreen(MainMenuScreen.LoadGame);
        }

        public void ShowOptions()
        {
            OpenScreen(MainMenuScreen.Options);
        }

        public void ShowMods()
        {
            OpenScreen(MainMenuScreen.Mods);
        }

        public void ShowAbout()
        {
            OpenScreen(MainMenuScreen.About);
        }

        public void Back()
        {
            if (_history.Count == 0)
            {
                ShowTitle();
                return;
            }

            ShowScreenImmediate(_history.Pop());
        }

        public void OpenScreen(MainMenuScreen screen)
        {
            if (screen == CurrentScreen)
            {
                return;
            }

            _history.Push(CurrentScreen);
            ShowScreenImmediate(screen);
        }

        private void ResetToInitialScreen()
        {
            _history.Clear();
            ShowScreenImmediate(initialScreen);
        }

        private void RebuildLookup()
        {
            _screenLookup.Clear();

            for (int i = 0; i < screens.Length; i++)
            {
                if (screens[i].root == null)
                {
                    continue;
                }

                if (_screenLookup.ContainsKey(screens[i].screen))
                {
                    Debug.LogWarning(
                        $"[MainMenuManager] Duplicate screen mapping for {screens[i].screen} on {name}. Keeping the latest reference.",
                        this);
                }

                _screenLookup[screens[i].screen] = screens[i].root;
            }
        }

        private void ShowScreenImmediate(MainMenuScreen screen)
        {
            if (screen == MainMenuScreen.Title && !_screenLookup.ContainsKey(screen))
            {
                foreach (var pair in _screenLookup)
                {
                    SetScreenVisible(pair.Value, false);
                }

                CurrentScreen = MainMenuScreen.Title;
                ScreenChanged?.Invoke(CurrentScreen);
                return;
            }

            if (!_screenLookup.ContainsKey(screen))
            {
                Debug.LogWarning($"[MainMenuManager] Screen {screen} is not assigned on {name}. Falling back to Title.", this);
                screen = MainMenuScreen.Title;
            }

            foreach (var pair in _screenLookup)
            {
                SetScreenVisible(pair.Value, pair.Key == screen);
            }

            CurrentScreen = screen;
            ScreenChanged?.Invoke(CurrentScreen);
        }

        private void SetScreenVisible(CanvasGroup canvasGroup, bool visible)
        {
            if (canvasGroup == null)
            {
                return;
            }

            if (visible && !canvasGroup.gameObject.activeSelf)
            {
                canvasGroup.gameObject.SetActive(true);
            }

            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.interactable = visible;
            canvasGroup.blocksRaycasts = visible;

            if (!visible && deactivateHiddenScreens && canvasGroup.gameObject.activeSelf)
            {
                canvasGroup.gameObject.SetActive(false);
            }
        }
    }
}
