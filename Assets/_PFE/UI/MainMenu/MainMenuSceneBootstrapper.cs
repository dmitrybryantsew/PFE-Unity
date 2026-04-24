using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using PFE.Character;
using PFE.Core;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.Systems.Map.Serialization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PFE.UI.MainMenu
{
    [DisallowMultipleComponent]
    public sealed class MainMenuSceneBootstrapper : MonoBehaviour
    {
        const float RefWidth = 1280f;
        const float RefHeight = 800f;
        const string SampleSceneName = "SampleScene";
        const float BackgroundCameraZoom = 4f;

        static readonly Color Accent = new(0.1f, 1f, 0.72f, 1f);
        static readonly Color AccentDim = new(0.08f, 0.65f, 0.48f, 1f);
        static readonly Color PanelText = new(0.18f, 1f, 0.76f, 1f);
        static readonly Color Hint = new(0.16f, 0.95f, 0.7f, 1f);
        static readonly Color Version = new(1f, 0.9f, 0.2f, 1f);
        static readonly Vector2 Center = new(0.5f, 0.5f);
        static readonly Vector2 BackgroundCameraOffset = new(8f, -4f);
        static readonly string[] Languages =
        {
            "Русский", "English", "Українська", "简体中文", "繁體中文", "Polski", "Deutsch", "Español", "日本語"
        };
        static readonly NewGameDifficulty[] DifficultyOrder =
        {
            NewGameDifficulty.VeryEasy, NewGameDifficulty.Easy, NewGameDifficulty.Normal, NewGameDifficulty.Hard, NewGameDifficulty.SuperHard
        };
        static readonly NewGameRuleFlags[] RuleOrder =
        {
            NewGameRuleFlags.SkipTraining, NewGameRuleFlags.FasterLevelUps, NewGameRuleFlags.WeLiveOnce,
            NewGameRuleFlags.RandomSkills, NewGameRuleFlags.SlowLearner, NewGameRuleFlags.LimitedInventory
        };

        Canvas _canvas;
        CanvasScaler _scaler;
        MainMenuManager _menuManager;
        MainMenuBackgroundRenderer _backgroundRenderer;
        Font _font;
        MainMenuCompositionDefinition _composition;
        CharacterAnimationDefinition _characterDefinition;
        CharacterStyleData _characterStyle;
        RuntimeSprites _sprites;
        NewGameSettings _settings;
        CharacterCustomizationSession _customizationSession;
        bool _suppressCustomizationEvents;

        InputField _nameInput;
        Text _loadText;
        Text _modsText;
        Text _summaryText;
        CharacterPreviewPresenter _newGamePreviewPresenter;
        CharacterPreviewPresenter _customizationPreviewPresenter;
        CanvasGroup _newGameContentGroup;
        CanvasGroup _customizationContentGroup;
        Slider _redSlider;
        Slider _greenSlider;
        Slider _blueSlider;
        Text _redValueText;
        Text _greenValueText;
        Text _blueValueText;
        Text _hairStyleValueText;
        Text _eyeStyleValueText;
        Text _customizationHintText;
        readonly List<CustomizationChannelRow> _customizationRows = new();

        void Awake()
        {
            _settings = NewGameSettings.CreateDefault();
            if (!LoadResources())
            {
                Debug.LogError("[MainMenuSceneBootstrapper] Missing required menu resources.");
                return;
            }

            ConfigureCanvas();
            BuildLayout();
        }

        void OnDestroy()
        {
            if (_menuManager != null)
            {
                _menuManager.ScreenChanged -= HandleScreenChanged;
            }
        }

        bool LoadResources()
        {
            _composition = Resources.Load<MainMenuCompositionDefinition>("MainMenu/MainMenuComposition");
            _characterDefinition = Resources.Load<CharacterAnimationDefinition>("Characters/PlayerAnimationDefinition");
            _characterStyle = Resources.Load<CharacterStyleData>("Character/CharacterStyleData");
            _font = CreateMenuFont();

            _sprites = new RuntimeSprites
            {
                menuButton = Resources.Load<Sprite>("MainMenuUI/Buttons/menu_new_game_fon"),
                languageButton = Resources.Load<Sprite>("MainMenuUI/Buttons/lang_btn_simple"),
                dialogBackground = Resources.Load<Sprite>("MainMenuUI/Dialogs/dialVid_background"),
                actionButton = Resources.Load<Sprite>("MainMenuUI/Dialogs/dialNew_butCancel"),
                difficultyOff = Resources.Load<Sprite>("MainMenuUI/Dialogs/dialNew_butVid_fon_f01"),
                difficultyOn = Resources.Load<Sprite>("MainMenuUI/Dialogs/dialNew_butVid_fon_f02"),
                checkboxOff = Resources.Load<Sprite>("MainMenuUI/Controls/checkbox_up"),
                checkboxOn = Resources.Load<Sprite>("MainMenuUI/Controls/checkbox_selected_up"),
                inputBackground = Resources.Load<Sprite>("MainMenuUI/Controls/textinput_up")
            };

            return _composition != null && _sprites.IsValid;
        }

        void ConfigureCanvas()
        {
            _canvas = GetComponent<Canvas>() ?? gameObject.AddComponent<Canvas>();
            _scaler = GetComponent<CanvasScaler>() ?? gameObject.AddComponent<CanvasScaler>();
            _ = GetComponent<GraphicRaycaster>() ?? gameObject.AddComponent<GraphicRaycaster>();
            _menuManager = GetComponent<MainMenuManager>() ?? gameObject.AddComponent<MainMenuManager>();

            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = new Vector2(RefWidth, RefHeight);
            _scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            _scaler.matchWidthOrHeight = 0.5f;

            RectTransform rect = _canvas.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = Center;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = Vector3.one;
        }

        void BuildLayout()
        {
            Transform oldRoot = transform.Find("GeneratedMainMenu");
            if (oldRoot != null)
            {
                Destroy(oldRoot.gameObject);
            }

            RectTransform root = MakeRect("GeneratedMainMenu", transform, Vector2.zero, Vector2.one, Center, Vector2.zero, Vector2.zero);
            BuildBackground(root);
            BuildStaticChrome(root);

            RectTransform overlayRoot = MakeRect("OverlayRoot", root, Vector2.zero, Vector2.one, Center, Vector2.zero, Vector2.zero);
            CanvasGroup newGamePanel = BuildNewGamePanel(overlayRoot);
            CanvasGroup loadPanel = BuildLoadPanel(overlayRoot);
            CanvasGroup optionsPanel = BuildOptionsPanel(overlayRoot);
            CanvasGroup modsPanel = BuildModsPanel(overlayRoot);
            CanvasGroup aboutPanel = BuildAboutPanel(overlayRoot);

            _menuManager.ConfigureRuntimeScreens(
                new Dictionary<MainMenuScreen, CanvasGroup>
                {
                    { MainMenuScreen.NewGame, newGamePanel },
                    { MainMenuScreen.LoadGame, loadPanel },
                    { MainMenuScreen.Options, optionsPanel },
                    { MainMenuScreen.Mods, modsPanel },
                    { MainMenuScreen.About, aboutPanel }
                },
                MainMenuScreen.Title);

            _menuManager.ScreenChanged -= HandleScreenChanged;
            _menuManager.ScreenChanged += HandleScreenChanged;
            HandleScreenChanged(_menuManager.CurrentScreen);

            RefreshSummary();
        }

        void BuildBackground(RectTransform parent)
        {
            _backgroundRenderer ??= GetComponent<MainMenuBackgroundRenderer>() ?? gameObject.AddComponent<MainMenuBackgroundRenderer>();
            _backgroundRenderer.Configure(_composition);
            _backgroundRenderer.ConfigureFraming(BackgroundCameraZoom, BackgroundCameraOffset);
        }

        void BuildStaticChrome(RectTransform parent)
        {
            RectTransform nav = MakeRect("Navigation", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(320f, 360f), new Vector2(84f, -76f));
            MakeMenuButton(nav, "Continue", new Vector2(0f, 0f), new Vector2(232f, 48f), 22, OnContinuePressed);
            MakeMenuButton(nav, "Load game", new Vector2(0f, -58f), new Vector2(196f, 32f), 16, OnLoadPressed);
            MakeMenuButton(nav, "New game", new Vector2(0f, -104f), new Vector2(196f, 32f), 16, () => _menuManager.ShowNewGame());
            MakeMenuButton(nav, "Options", new Vector2(0f, -150f), new Vector2(196f, 32f), 16, () => _menuManager.ShowOptions());
            MakeMenuButton(nav, "Mods", new Vector2(0f, -196f), new Vector2(196f, 32f), 16, OnModsPressed);
            MakeMenuButton(nav, "Authors", new Vector2(0f, -242f), new Vector2(196f, 32f), 16, () => _menuManager.ShowAbout());

            RectTransform info = MakeRect("InfoBlock", parent, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(280f, 180f), new Vector2(84f, 242f));
            MakeText("InfoHeader", info, "Useful information and links:", 16, Accent, TextAnchor.UpperLeft, new Vector2(0f, 152f), new Vector2(270f, 24f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f));
            MakeText("InfoLinks", info, "Game website\nThe latest version of the game\nGame Wiki in Russian\nGame Wiki in English\nDiscord Server\nAuthor DeviantArt page", 15, PanelText, TextAnchor.UpperLeft, new Vector2(0f, 126f), new Vector2(270f, 140f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f));

            RectTransform footer = MakeRect("Footer", parent, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(320f, 80f), new Vector2(64f, 20f));
            MakeText("Site", footer, "foe.ucoz.org", 20, Accent, TextAnchor.UpperLeft, new Vector2(0f, 42f), new Vector2(260f, 24f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f));
            MakeText("DA", footer, "deviantart.com/empalu", 18, Accent, TextAnchor.UpperLeft, new Vector2(0f, 18f), new Vector2(300f, 24f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f));
            MakeText("Version", footer, "Version 1.0.3", 16, Version, TextAnchor.UpperLeft, new Vector2(24f, -10f), new Vector2(220f, 22f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f));

            MakeText("Hint", parent, "You can throw a grenade or plant a mine at any moment using [G] key,\neven if you are currently using another weapon.", 16, Hint, TextAnchor.UpperLeft, new Vector2(-34f, -332f), new Vector2(560f, 48f), Center, Center, Center);

            RectTransform languages = MakeRect("Languages", parent, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(160f, 360f), new Vector2(-24f, 46f));
            float y = 0f;
            for (int i = 0; i < Languages.Length; i++)
            {
                MakeLanguageButton(languages, Languages[i], new Vector2(0f, y), new Vector2(140f, 34f), null);
                y += 38f;
            }
        }

        CanvasGroup BuildNewGamePanel(RectTransform parent)
        {
            CanvasGroup panel = MakePanel("NewGamePanel", parent, new Vector2(823f, 610f));
            RectTransform rect = (RectTransform)panel.transform;

            RectTransform content = MakeRect("NewGameContent", rect, Vector2.zero, Vector2.one, Center, Vector2.zero, Vector2.zero);
            _newGameContentGroup = content.gameObject.AddComponent<CanvasGroup>();
            MakeText("Title", content, "New game", 20, Accent, TextAnchor.MiddleCenter, new Vector2(0f, 250f), new Vector2(320f, 24f), Center, Center, Center);

            RectTransform difficultyRoot = MakeRect("DifficultyRoot", content, Center, Center, Center, new Vector2(270f, 320f), new Vector2(-215f, 54f));
            ToggleGroup difficultyGroup = difficultyRoot.gameObject.AddComponent<ToggleGroup>();
            float difficultyY = 120f;
            foreach (NewGameDifficulty difficulty in DifficultyOrder)
            {
                Toggle toggle = MakeDifficultyToggle(difficultyRoot, difficulty, difficultyGroup, new Vector2(0f, difficultyY));
                toggle.isOn = difficulty == _settings.difficulty;
                difficultyY -= 58f;
            }

            MakeText("NameLabel", content, "Enter your character's name", 16, PanelText, TextAnchor.MiddleCenter, new Vector2(30f, 196f), new Vector2(200f, 22f), Center, Center, Center);
            _nameInput = MakeInput(content, new Vector2(30f, 163f), new Vector2(195f, 32f));
            _nameInput.text = _settings.playerName;
            _nameInput.onValueChanged.AddListener(value =>
            {
                _settings.playerName = string.IsNullOrWhiteSpace(value) ? "Littlepip" : value.Trim();
                RefreshSummary();
            });

            RectTransform preview = MakeRect("Preview", content, Center, Center, Center, new Vector2(200f, 240f), new Vector2(30f, 22f));
            MakeImage("PreviewBacking", preview, null, new Color(0f, 0f, 0f, 0.35f), Vector2.zero, Vector2.one, Center, Vector2.zero, Vector2.zero);
            _newGamePreviewPresenter = BuildCharacterPreview(preview, _settings.appearance, 28);

            MakeActionButton(content, "Customize appearance", new Vector2(30f, -126f), new Vector2(220f, 40f), OpenCustomizationPanel);

            RectTransform rules = MakeRect("Rules", content, Center, Center, Center, new Vector2(250f, 250f), new Vector2(265f, 78f));
            float ruleY = 110f;
            foreach (NewGameRuleFlags flag in RuleOrder)
            {
                MakeRuleToggle(rules, flag, new Vector2(0f, ruleY));
                ruleY -= 38f;
            }

            _summaryText = MakeText("Summary", content, string.Empty, 15, PanelText, TextAnchor.MiddleCenter, new Vector2(0f, -196f), new Vector2(620f, 72f), Center, Center, Center);
            MakeText("Warning", content, "Attention! Autosave slot will be overwritten.", 15, Version, TextAnchor.MiddleCenter, new Vector2(0f, -256f), new Vector2(520f, 24f), Center, Center, Center);
            MakeActionButton(content, "Cancel", new Vector2(-104f, -286f), new Vector2(168f, 44f), () => _menuManager.ShowTitle());
            MakeActionButton(content, "Start", new Vector2(104f, -286f), new Vector2(168f, 44f), OnStartPressed);

            _customizationContentGroup = BuildCustomizationPanel(rect);
            SetCanvasGroupVisible(_customizationContentGroup, false);
            return panel;
        }

        CanvasGroup BuildLoadPanel(RectTransform parent)
        {
            CanvasGroup panel = MakePanel("LoadPanel", parent, new Vector2(760f, 420f));
            RectTransform rect = (RectTransform)panel.transform;
            MakeText("Title", rect, "Load game", 20, Accent, TextAnchor.MiddleCenter, new Vector2(0f, 166f), new Vector2(320f, 24f), Center, Center, Center);
            _loadText = MakeText("Body", rect, string.Empty, 16, PanelText, TextAnchor.UpperLeft, new Vector2(0f, 36f), new Vector2(620f, 220f), Center, Center, Center);
            MakeActionButton(rect, "Close", new Vector2(0f, -166f), new Vector2(180f, 44f), () => _menuManager.ShowTitle());
            PopulateLoadPanel();
            return panel;
        }

        CanvasGroup BuildOptionsPanel(RectTransform parent)
        {
            CanvasGroup panel = MakePanel("OptionsPanel", parent, new Vector2(760f, 420f));
            RectTransform rect = (RectTransform)panel.transform;
            MakeText("Title", rect, "Options", 20, Accent, TextAnchor.MiddleCenter, new Vector2(0f, 166f), new Vector2(320f, 24f), Center, Center, Center);
            MakeText("Body", rect, "Static layout only for now.\n\nThe original game has large Options and Controls pages.\nThis panel keeps the screen flow and recreated layout moving.\n\nTODO: hook real display, audio, controls, and gameplay toggles.", 17, PanelText, TextAnchor.UpperLeft, new Vector2(0f, 26f), new Vector2(620f, 230f), Center, Center, Center);
            MakeActionButton(rect, "Close", new Vector2(0f, -166f), new Vector2(180f, 44f), () => _menuManager.ShowTitle());
            return panel;
        }

        CanvasGroup BuildModsPanel(RectTransform parent)
        {
            CanvasGroup panel = MakePanel("ModsPanel", parent, new Vector2(760f, 420f));
            RectTransform rect = (RectTransform)panel.transform;
            MakeText("Title", rect, "Mods", 20, Accent, TextAnchor.MiddleCenter, new Vector2(0f, 166f), new Vector2(320f, 24f), Center, Center, Center);
            _modsText = MakeText("Body", rect, string.Empty, 16, PanelText, TextAnchor.UpperLeft, new Vector2(0f, 26f), new Vector2(620f, 230f), Center, Center, Center);
            MakeActionButton(rect, "Close", new Vector2(0f, -166f), new Vector2(180f, 44f), () => _menuManager.ShowTitle());
            PopulateModsPanel();
            return panel;
        }

        CanvasGroup BuildAboutPanel(RectTransform parent)
        {
            CanvasGroup panel = MakePanel("AboutPanel", parent, new Vector2(824f, 610f));
            RectTransform rect = (RectTransform)panel.transform;
            MakeText("Title", rect, "Authors", 20, Accent, TextAnchor.MiddleCenter, new Vector2(0f, 250f), new Vector2(320f, 24f), Center, Center, Center);
            MakeText("Body", rect, "<color=#FFE755>Empalu</color> - concept, programming, graphics\n<color=#FFE755>DipFanken</color> - graphics, locations design\n<color=#FFE755>Mihasik</color> - English localization\n\nThe game is based on the \"Fallout: Equestria\" story by Kkat.\nThis project is a free fan game recreation.\n\nMusic and license details from the original launcher can be moved here later.\n\nTODO: replace this block with the final authors/about content data.", 17, PanelText, TextAnchor.UpperLeft, new Vector2(0f, 26f), new Vector2(660f, 320f), Center, Center, Center);
            MakeActionButton(rect, "OK", new Vector2(0f, -286f), new Vector2(180f, 44f), () => _menuManager.ShowTitle());
            return panel;
        }

        void OnContinuePressed()
        {
            List<SaveMetadata> saves = SaveManager.Instance.GetAllSaves();
            if (saves.Count == 0)
            {
                _menuManager.ShowNewGame();
                return;
            }

            PopulateLoadPanel();
            _menuManager.ShowLoadGame();
        }

        void OnLoadPressed()
        {
            PopulateLoadPanel();
            _menuManager.ShowLoadGame();
        }

        void OnModsPressed()
        {
            PopulateModsPanel();
            _menuManager.ShowMods();
        }

        void OnStartPressed()
        {
            NewGameSettings pending = _settings.Clone();
            pending.playerName = string.IsNullOrWhiteSpace(_nameInput?.text) ? "Littlepip" : _nameInput.text.Trim();
            GameBootData.SetPendingNewGame(pending);
            SceneLoader.LoadSceneAsync(SampleSceneName, useFade: true, fadeDuration: 0.22f).Forget();
        }

        void PopulateLoadPanel()
        {
            if (_loadText == null)
            {
                return;
            }

            List<SaveMetadata> saves = SaveManager.Instance.GetAllSaves();
            if (saves.Count == 0)
            {
                _loadText.text = "No saves were found.\n\nContinue falls back to the New game panel until richer save summaries and load boot flow are wired.";
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine("Latest saves:");
            builder.AppendLine();
            int count = Mathf.Min(6, saves.Count);
            for (int i = 0; i < count; i++)
            {
                SaveMetadata save = saves[i];
                string saveId = string.IsNullOrEmpty(save.saveId) ? "unknown" : save.saveId;
                builder.AppendLine($"{i + 1}. {saveId}");
                builder.AppendLine($"   {save.GetDateTime():yyyy-MM-dd HH:mm} | {save.GetFileSizeString()} | rooms: {save.roomCount}");
                builder.AppendLine();
            }

            builder.Append("TODO: replace this temporary list with original-style save slot cards.");
            _loadText.text = builder.ToString();
        }

        void PopulateModsPanel()
        {
            if (_modsText == null)
            {
                return;
            }

            var loader = new ModLoader();
            List<PFE.ModAPI.ModManifest> mods = loader.DiscoverMods();
            var builder = new StringBuilder();
            builder.AppendLine($"Mods folder: {loader.ModsRoot}");
            builder.AppendLine($"Discovered mods: {mods.Count}");
            builder.AppendLine();

            if (mods.Count == 0)
            {
                builder.AppendLine("No mods found.");
            }
            else
            {
                foreach (var mod in mods)
                {
                    builder.AppendLine($"- {mod.displayName} ({mod.modId}) v{mod.version}");
                }
            }

            if (loader.ValidationLog.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Validation notes:");
                foreach (string line in loader.ValidationLog)
                {
                    builder.AppendLine($"- {line}");
                }
            }

            builder.AppendLine();
            builder.Append("TODO: add persisted enabled-state and a real launcher panel.");
            _modsText.text = builder.ToString();
        }

        void RefreshSummary()
        {
            if (_summaryText == null)
            {
                return;
            }

            string playerName = string.IsNullOrWhiteSpace(_settings.playerName) ? "Littlepip" : _settings.playerName;
            string flags = BuildSelectedRulesSummary();

            _summaryText.text = $"Player: {playerName}\nDifficulty: {NewGameSettings.GetDifficultyLabel(_settings.difficulty)}\n{flags}";
        }

        CharacterPreviewPresenter BuildCharacterPreview(RectTransform parent, CharacterAppearance appearance, int previewLayer)
        {
            if (_characterDefinition == null || _characterStyle == null)
            {
                MakeText("PreviewMissing", parent, "Character preview unavailable.", 15, PanelText, TextAnchor.MiddleCenter, Vector2.zero, new Vector2(160f, 80f), Center, Center, Center);
                return null;
            }

            RawImage raw = parent.gameObject.AddComponent<RawImage>();
            CharacterPreviewPresenter presenter = parent.gameObject.AddComponent<CharacterPreviewPresenter>();
            presenter.Initialize(raw, _characterDefinition, _characterStyle, appearance, CharacterVisualContext.Default, "stay", 0, previewLayer);
            return presenter;
        }

        CanvasGroup BuildCustomizationPanel(RectTransform parent)
        {
            RectTransform rect = MakeRect("CustomizationPanel", parent, Vector2.zero, Vector2.one, Center, Vector2.zero, Vector2.zero);
            CanvasGroup group = rect.gameObject.AddComponent<CanvasGroup>();

            MakeText("Title", rect, "Customize appearance", 20, Accent, TextAnchor.MiddleCenter, new Vector2(0f, 250f), new Vector2(360f, 24f), Center, Center, Center);

            RectTransform preview = MakeRect("Preview", rect, Center, Center, Center, new Vector2(260f, 280f), new Vector2(-185f, 58f));
            MakeImage("PreviewBacking", preview, null, new Color(0f, 0f, 0f, 0.34f), Vector2.zero, Vector2.one, Center, Vector2.zero, Vector2.zero);
            _customizationPreviewPresenter = BuildCharacterPreview(preview, _settings.appearance, 29);
            _customizationPreviewPresenter?.SetActive(false);

            RectTransform rowsRoot = MakeRect("ChannelRows", rect, Center, Center, Center, new Vector2(340f, 220f), new Vector2(145f, 88f));
            _customizationRows.Clear();
            float rowY = 82f;
            for (int i = 0; i < CharacterAppearance.ColorChannelCount; i++)
            {
                TintCategory channel = (TintCategory)i;
                _customizationRows.Add(MakeCustomizationChannelRow(rowsRoot, channel, new Vector2(0f, rowY)));
                rowY -= 44f;
            }

            RectTransform slidersRoot = MakeRect("Sliders", rect, Center, Center, Center, new Vector2(360f, 132f), new Vector2(145f, -92f));
            _redSlider = MakeCustomizationSlider(slidersRoot, "R", new Vector2(0f, 36f), out _redValueText);
            _greenSlider = MakeCustomizationSlider(slidersRoot, "G", new Vector2(0f, 0f), out _greenValueText);
            _blueSlider = MakeCustomizationSlider(slidersRoot, "B", new Vector2(0f, -36f), out _blueValueText);

            _redSlider.onValueChanged.AddListener(_ => HandleCustomizationSliderChanged());
            _greenSlider.onValueChanged.AddListener(_ => HandleCustomizationSliderChanged());
            _blueSlider.onValueChanged.AddListener(_ => HandleCustomizationSliderChanged());

            _customizationHintText = MakeText(
                "Hint",
                rect,
                string.Empty,
                15,
                PanelText,
                TextAnchor.UpperCenter,
                new Vector2(0f, -202f),
                new Vector2(620f, 64f),
                Center,
                Center,
                Center);

            MakeActionButton(rect, "Default", new Vector2(-176f, -286f), new Vector2(156f, 44f), ResetCustomizationToDefaults);
            MakeActionButton(rect, "Cancel", new Vector2(0f, -286f), new Vector2(156f, 44f), () => CloseCustomizationPanel(false));
            MakeActionButton(rect, "OK", new Vector2(176f, -286f), new Vector2(156f, 44f), () => CloseCustomizationPanel(true));

            return group;
        }

        CustomizationChannelRow MakeCustomizationChannelRow(RectTransform parent, TintCategory channel, Vector2 anchoredPosition)
        {
            RectTransform rect = MakeRect(channel + "Row", parent, Center, Center, Center, new Vector2(320f, 36f), anchoredPosition);
            Image background = rect.gameObject.AddComponent<Image>();
            background.sprite = _sprites.difficultyOff;
            background.type = Image.Type.Sliced;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.ColorTint;
            button.onClick.AddListener(() => SelectCustomizationChannel(channel));

            Image swatch = MakeImage("Swatch", rect, null, Color.white, Center, Center, Center, new Vector2(-132f, 0f), new Vector2(24f, 20f));
            swatch.raycastTarget = false;

            Text label = MakeText("Label", rect, CharacterAppearance.GetChannelName((int)channel), 15, PanelText, TextAnchor.MiddleLeft, new Vector2(-20f, 0f), new Vector2(196f, 24f), Center, Center, Center);

            var row = new CustomizationChannelRow
            {
                channel = channel,
                background = background,
                label = label,
                swatch = swatch
            };

            if (channel == TintCategory.Hair)
            {
                MakeMiniActionButton(rect, "<", new Vector2(86f, 0f), () => AdjustCustomizationStyle(channel, -1));
                row.styleValue = MakeText("StyleValue", rect, string.Empty, 14, Accent, TextAnchor.MiddleCenter, new Vector2(128f, 0f), new Vector2(52f, 22f), Center, Center, Center);
                MakeMiniActionButton(rect, ">", new Vector2(170f, 0f), () => AdjustCustomizationStyle(channel, 1));
                _hairStyleValueText = row.styleValue;
            }
            else if (channel == TintCategory.Eye)
            {
                MakeMiniActionButton(rect, "<", new Vector2(86f, 0f), () => AdjustCustomizationStyle(channel, -1));
                row.styleValue = MakeText("StyleValue", rect, string.Empty, 14, Accent, TextAnchor.MiddleCenter, new Vector2(128f, 0f), new Vector2(52f, 22f), Center, Center, Center);
                MakeMiniActionButton(rect, ">", new Vector2(170f, 0f), () => AdjustCustomizationStyle(channel, 1));
                _eyeStyleValueText = row.styleValue;
            }
            else if (channel == TintCategory.Hair2)
            {
                RectTransform toggleRoot = MakeRect("VisibilityToggle", rect, Center, Center, Center, new Vector2(28f, 24f), new Vector2(132f, 0f));
                Toggle toggle = toggleRoot.gameObject.AddComponent<Toggle>();
                toggle.transition = Selectable.Transition.None;
                toggle.onValueChanged.AddListener(SetSecondaryHairVisible);
                Image off = MakeImage("VisibilityOff", toggleRoot, _sprites.checkboxOff, Color.white, Center, Center, Center, Vector2.zero, new Vector2(18f, 18f));
                Image on = MakeImage("VisibilityOn", toggleRoot, _sprites.checkboxOn, Color.white, Center, Center, Center, Vector2.zero, new Vector2(18f, 18f));
                off.raycastTarget = true;
                on.raycastTarget = false;
                toggle.targetGraphic = off;
                toggle.graphic = on;
                row.visibilityToggle = toggle;
            }

            return row;
        }

        Slider MakeCustomizationSlider(RectTransform parent, string label, Vector2 anchoredPosition, out Text valueText)
        {
            RectTransform row = MakeRect(label + "SliderRow", parent, Center, Center, Center, new Vector2(340f, 28f), anchoredPosition);
            MakeText("Label", row, label + ":", 15, Accent, TextAnchor.MiddleLeft, new Vector2(-154f, 0f), new Vector2(24f, 20f), Center, Center, Center);

            RectTransform sliderRect = MakeRect(label + "Slider", row, Center, Center, Center, new Vector2(228f, 18f), new Vector2(-10f, 0f));
            Image background = sliderRect.gameObject.AddComponent<Image>();
            background.color = new Color(0f, 0.18f, 0.14f, 0.92f);

            RectTransform fillArea = MakeRect("FillArea", sliderRect, Vector2.zero, Vector2.one, Center, new Vector2(-14f, -8f), Vector2.zero);
            Image fill = MakeImage("Fill", fillArea, null, Accent, new Vector2(0f, 0f), new Vector2(1f, 1f), Center, Vector2.zero, Vector2.zero);
            RectTransform handle = MakeRect("Handle", sliderRect, Center, Center, Center, new Vector2(12f, 18f), Vector2.zero);
            Image handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = Color.white;

            Slider slider = sliderRect.gameObject.AddComponent<Slider>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 255f;
            slider.wholeNumbers = true;
            slider.targetGraphic = handleImage;
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle;

            valueText = MakeText("Value", row, "255", 15, PanelText, TextAnchor.MiddleRight, new Vector2(154f, 0f), new Vector2(46f, 20f), Center, Center, Center);
            return slider;
        }

        void OpenCustomizationPanel()
        {
            if (_characterDefinition == null || _characterStyle == null)
            {
                return;
            }

            _customizationSession = new CharacterCustomizationSession(
                _settings.appearance,
                _characterDefinition.hairStyleCount,
                _characterDefinition.eyeStyleCount);

            SetCanvasGroupVisible(_newGameContentGroup, false);
            _newGamePreviewPresenter?.SetActive(false);
            SetCanvasGroupVisible(_customizationContentGroup, true);
            _customizationPreviewPresenter?.SetActive(true);
            RefreshCustomizationUi();
        }

        void CloseCustomizationPanel(bool commit)
        {
            if (_customizationSession != null && commit)
            {
                _settings.appearance = _customizationSession.Commit();
            }

            _customizationSession = null;
            SetCanvasGroupVisible(_customizationContentGroup, false);
            _customizationPreviewPresenter?.SetActive(false);
            SetCanvasGroupVisible(_newGameContentGroup, true);
            _newGamePreviewPresenter?.SetAppearance(_settings.appearance);
            _newGamePreviewPresenter?.SetActive(_menuManager != null && _menuManager.CurrentScreen == MainMenuScreen.NewGame);
            RefreshSummary();
        }

        void ResetCustomizationToDefaults()
        {
            if (_customizationSession == null)
            {
                return;
            }

            _customizationSession.ResetToDefaults();
            RefreshCustomizationUi();
        }

        void SelectCustomizationChannel(TintCategory channel)
        {
            if (_customizationSession == null)
            {
                return;
            }

            _customizationSession.SetActiveChannel(channel);
            RefreshCustomizationUi();
        }

        void AdjustCustomizationStyle(TintCategory channel, int direction)
        {
            if (_customizationSession == null)
            {
                return;
            }

            if (channel == TintCategory.Hair)
            {
                _customizationSession.CycleHair(direction);
            }
            else if (channel == TintCategory.Eye)
            {
                _customizationSession.CycleEyes(direction);
            }

            SelectCustomizationChannel(channel);
        }

        void SetSecondaryHairVisible(bool visible)
        {
            if (_suppressCustomizationEvents || _customizationSession == null)
            {
                return;
            }

            _customizationSession.SetSecondaryHairVisible(visible);
            RefreshCustomizationUi();
        }

        void HandleCustomizationSliderChanged()
        {
            if (_suppressCustomizationEvents || _customizationSession == null)
            {
                return;
            }

            _customizationSession.SetChannelRgb(
                (byte)Mathf.RoundToInt(_redSlider.value),
                (byte)Mathf.RoundToInt(_greenSlider.value),
                (byte)Mathf.RoundToInt(_blueSlider.value));
            RefreshCustomizationUi();
        }

        void RefreshCustomizationUi()
        {
            if (_customizationSession == null)
            {
                return;
            }

            _suppressCustomizationEvents = true;

            Color activeColor = _customizationSession.GetActiveColor();
            Color32 activeRgb = activeColor;
            if (_redSlider != null) _redSlider.SetValueWithoutNotify(activeRgb.r);
            if (_greenSlider != null) _greenSlider.SetValueWithoutNotify(activeRgb.g);
            if (_blueSlider != null) _blueSlider.SetValueWithoutNotify(activeRgb.b);
            if (_redValueText != null) _redValueText.text = activeRgb.r.ToString();
            if (_greenValueText != null) _greenValueText.text = activeRgb.g.ToString();
            if (_blueValueText != null) _blueValueText.text = activeRgb.b.ToString();

            foreach (CustomizationChannelRow row in _customizationRows)
            {
                bool active = row.channel == _customizationSession.ActiveChannel;
                row.background.sprite = active ? _sprites.difficultyOn : _sprites.difficultyOff;
                row.label.color = active ? Accent : PanelText;
                row.swatch.color = _customizationSession.GetColor(row.channel);

                if (row.visibilityToggle != null)
                {
                    row.visibilityToggle.SetIsOnWithoutNotify(_customizationSession.Working.showSecondaryHair);
                    if (row.visibilityToggle.graphic != null)
                    {
                        row.visibilityToggle.graphic.enabled = _customizationSession.Working.showSecondaryHair;
                    }
                }
            }

            if (_hairStyleValueText != null)
            {
                _hairStyleValueText.text = $"{_customizationSession.Working.hairStyle}/{Mathf.Max(1, _characterDefinition?.hairStyleCount ?? 1)}";
            }

            if (_eyeStyleValueText != null)
            {
                _eyeStyleValueText.text = $"{_customizationSession.Working.eyeStyle}/{Mathf.Max(1, _characterDefinition?.eyeStyleCount ?? 1)}";
            }

            if (_customizationHintText != null)
            {
                _customizationHintText.text =
                    $"Active: {CharacterAppearance.GetChannelName((int)_customizationSession.ActiveChannel)}\n" +
                    $"Hair style: {_customizationSession.Working.hairStyle}  |  Eye style: {_customizationSession.Working.eyeStyle}";
            }

            _customizationPreviewPresenter?.SetAppearance(_customizationSession.Working);
            _suppressCustomizationEvents = false;
        }

        void HandleScreenChanged(MainMenuScreen screen)
        {
            _newGamePreviewPresenter?.SetActive(screen == MainMenuScreen.NewGame && _customizationSession == null);
            _customizationPreviewPresenter?.SetActive(screen == MainMenuScreen.NewGame && _customizationSession != null);

            if (screen != MainMenuScreen.NewGame && _customizationSession != null)
            {
                CloseCustomizationPanel(false);
            }
        }

        Toggle MakeDifficultyToggle(RectTransform parent, NewGameDifficulty difficulty, ToggleGroup group, Vector2 anchoredPosition)
        {
            RectTransform rect = MakeRect(difficulty.ToString(), parent, Center, Center, Center, new Vector2(250f, 50f), anchoredPosition);
            Image background = rect.gameObject.AddComponent<Image>();
            background.sprite = _sprites.difficultyOff;
            Toggle toggle = rect.gameObject.AddComponent<Toggle>();
            toggle.group = group;
            toggle.targetGraphic = background;
            toggle.transition = Selectable.Transition.None;

            Text label = MakeText("Label", rect, NewGameSettings.GetDifficultyLabel(difficulty), 18, PanelText, TextAnchor.MiddleCenter, Vector2.zero, new Vector2(220f, 24f), Center, Center, Center);
            toggle.onValueChanged.AddListener(isOn =>
            {
                background.sprite = isOn ? _sprites.difficultyOn : _sprites.difficultyOff;
                label.color = isOn ? Accent : PanelText;
                if (isOn)
                {
                    _settings.difficulty = difficulty;
                    RefreshSummary();
                }
            });
            return toggle;
        }

        void MakeRuleToggle(RectTransform parent, NewGameRuleFlags flag, Vector2 anchoredPosition)
        {
            RectTransform row = MakeRect(flag.ToString(), parent, Center, Center, Center, new Vector2(250f, 28f), anchoredPosition);
            Toggle toggle = row.gameObject.AddComponent<Toggle>();
            toggle.transition = Selectable.Transition.None;
            toggle.isOn = _settings.HasFlag(flag);

            Image off = MakeImage("Off", row, _sprites.checkboxOff, Color.white, Center, Center, Center, new Vector2(-112f, 0f), new Vector2(14f, 14f));
            Image on = MakeImage("On", row, _sprites.checkboxOn, Color.white, Center, Center, Center, new Vector2(-112f, 0f), new Vector2(14f, 14f));
            off.raycastTarget = true;
            on.enabled = toggle.isOn;
            toggle.targetGraphic = off;
            toggle.graphic = on;

            Text label = MakeText("Label", row, NewGameSettings.GetRuleLabel(flag), 15, toggle.isOn ? Accent : PanelText, TextAnchor.MiddleLeft, Vector2.zero, new Vector2(214f, 20f), Center, Center, Center);
            toggle.onValueChanged.AddListener(isOn =>
            {
                on.enabled = isOn;
                label.color = isOn ? Accent : PanelText;
                _settings.SetFlag(flag, isOn);
                RefreshSummary();
            });
        }

        InputField MakeInput(RectTransform parent, Vector2 anchoredPosition, Vector2 size)
        {
            RectTransform rect = MakeRect("Input", parent, Center, Center, Center, size, anchoredPosition);
            Image background = rect.gameObject.AddComponent<Image>();
            background.sprite = _sprites.inputBackground;
            var input = rect.gameObject.AddComponent<InputField>();
            input.characterLimit = 32;
            input.lineType = InputField.LineType.SingleLine;
            input.selectionColor = new Color(0.1f, 1f, 0.72f, 0.25f);

            RectTransform area = MakeRect("TextArea", rect, Vector2.zero, Vector2.one, Center, new Vector2(-28f, -10f), Vector2.zero);
            Text text = MakeText("Text", area, string.Empty, 18, Accent, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.one, Center);
            Text placeholder = MakeText("Placeholder", area, "Littlepip", 18, new Color(0.12f, 0.6f, 0.46f, 0.8f), TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.one, Center);
            input.textComponent = text;
            input.placeholder = placeholder;
            return input;
        }

        Button MakeMenuButton(RectTransform parent, string label, Vector2 anchoredPosition, Vector2 size, int fontSize, Action onClick)
        {
            return MakeButton(parent, label, anchoredPosition, size, fontSize, _sprites.menuButton, onClick, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), TextAnchor.MiddleLeft, new Vector2(16f, 0f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
        }

        Button MakeActionButton(RectTransform parent, string label, Vector2 anchoredPosition, Vector2 size, Action onClick)
        {
            return MakeButton(parent, label, anchoredPosition, size, 18, _sprites.actionButton, onClick, Center, Center, Center, TextAnchor.MiddleCenter, Vector2.zero, Center, Center, Center);
        }

        Button MakeMiniActionButton(RectTransform parent, string label, Vector2 anchoredPosition, Action onClick)
        {
            return MakeButton(parent, label, anchoredPosition, new Vector2(32f, 26f), 16, _sprites.actionButton, onClick, Center, Center, Center, TextAnchor.MiddleCenter, Vector2.zero, Center, Center, Center);
        }

        Button MakeLanguageButton(RectTransform parent, string label, Vector2 anchoredPosition, Vector2 size, Action onClick)
        {
            return MakeButton(parent, label, anchoredPosition, size, 16, _sprites.languageButton, onClick, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), TextAnchor.MiddleCenter, Vector2.zero, Center, Center, Center);
        }

        Button MakeButton(
            RectTransform parent,
            string label,
            Vector2 anchoredPosition,
            Vector2 size,
            int fontSize,
            Sprite sprite,
            Action onClick,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            TextAnchor textAlignment,
            Vector2 textPosition,
            Vector2 textAnchorMin,
            Vector2 textAnchorMax,
            Vector2 textPivot)
        {
            RectTransform rect = MakeRect(label.Replace(" ", string.Empty) + "Button", parent, anchorMin, anchorMax, pivot, size, anchoredPosition);
            Image background = rect.gameObject.AddComponent<Image>();
            background.sprite = sprite;
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;

            ColorBlock colors = button.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 0.92f);
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            Text text = MakeText("Label", rect, label, fontSize, Accent, textAlignment, textPosition, new Vector2(Mathf.Max(12f, size.x - 24f), size.y), textAnchorMin, textAnchorMax, textPivot);
            rect.gameObject.AddComponent<ButtonTextHover>().Initialize(text, Accent, AccentDim);
            button.onClick.AddListener(() => onClick?.Invoke());
            return button;
        }

        string BuildSelectedRulesSummary()
        {
            if (_settings.ruleFlags == NewGameRuleFlags.None)
            {
                return "Rules: none selected yet.";
            }

            var builder = new StringBuilder();
            builder.Append("Rules: ");

            bool first = true;
            foreach (NewGameRuleFlags flag in RuleOrder)
            {
                if (!_settings.HasFlag(flag))
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(", ");
                }

                builder.Append(NewGameSettings.GetRuleLabel(flag));
                first = false;
            }

            builder.Append("\nTODO: gameplay effects for these flags are not implemented yet.");
            return builder.ToString();
        }

        CanvasGroup MakePanel(string name, RectTransform parent, Vector2 size)
        {
            RectTransform rect = MakeRect(name, parent, Center, Center, Center, size, Vector2.zero);
            Image background = rect.gameObject.AddComponent<Image>();
            background.sprite = _sprites.dialogBackground;
            background.color = new Color(1f, 1f, 1f, 0.96f);
            CanvasGroup canvasGroup = rect.gameObject.AddComponent<CanvasGroup>();
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
            return canvasGroup;
        }

        void SetCanvasGroupVisible(CanvasGroup group, bool visible)
        {
            if (group == null)
            {
                return;
            }

            group.alpha = visible ? 1f : 0f;
            group.interactable = visible;
            group.blocksRaycasts = visible;
            group.gameObject.SetActive(true);
        }

        Image MakeCompositionImage(string name, RectTransform parent, Sprite sprite, Vector2 flashPosition, float scale)
        {
            if (sprite == null)
            {
                return null;
            }

            Vector2 size = new(sprite.rect.width * scale, sprite.rect.height * scale);
            Vector2 anchoredPosition = new(flashPosition.x - (RefWidth * 0.5f), (RefHeight * 0.5f) - flashPosition.y);
            return MakeImage(name, parent, sprite, Color.white, Center, Center, Center, anchoredPosition, size);
        }

        Font CreateMenuFont()
        {
            try
            {
                Font dynamic = Font.CreateDynamicFontFromOSFont(new[] { "Lucida Console", "Consolas", "Courier New" }, 18);
                if (dynamic != null)
                {
                    return dynamic;
                }
            }
            catch
            {
            }

            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        RectTransform MakeRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 sizeDelta, Vector2 anchoredPosition)
        {
            GameObject go = new(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.sizeDelta = sizeDelta;
            rect.anchoredPosition = anchoredPosition;
            rect.localScale = Vector3.one;
            return rect;
        }

        Image MakeImage(string name, RectTransform parent, Sprite sprite, Color color, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            RectTransform rect = MakeRect(name, parent, anchorMin, anchorMax, pivot, sizeDelta, anchoredPosition);
            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        Text MakeText(string name, RectTransform parent, string content, int fontSize, Color color, TextAnchor alignment, Vector2 anchoredPosition, Vector2 sizeDelta, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot)
        {
            RectTransform rect = MakeRect(name, parent, anchorMin, anchorMax, pivot, sizeDelta, anchoredPosition);
            Text text = rect.gameObject.AddComponent<Text>();
            text.font = _font;
            text.text = content;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;
            return text;
        }

        sealed class CustomizationChannelRow
        {
            public TintCategory channel;
            public Image background;
            public Text label;
            public Image swatch;
            public Text styleValue;
            public Toggle visibilityToggle;
        }

        sealed class RuntimeSprites
        {
            public Sprite menuButton;
            public Sprite languageButton;
            public Sprite dialogBackground;
            public Sprite actionButton;
            public Sprite difficultyOff;
            public Sprite difficultyOn;
            public Sprite checkboxOff;
            public Sprite checkboxOn;
            public Sprite inputBackground;

            public bool IsValid => menuButton != null && languageButton != null && dialogBackground != null &&
                                   actionButton != null && difficultyOff != null && difficultyOn != null &&
                                   checkboxOff != null && checkboxOn != null && inputBackground != null;
        }
    }

    internal sealed class ButtonTextHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        Text _text;
        Color _normal;
        Color _hover;

        public void Initialize(Text text, Color normal, Color hover)
        {
            _text = text;
            _normal = normal;
            _hover = hover;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_text != null) _text.color = _hover;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (_text != null) _text.color = _normal;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_text != null) _text.color = Color.white;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (_text != null) _text.color = _hover;
        }
    }
}
