using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using PFE.Data;
using PFE.Data.Definitions.Campaign;
using PFE.UI.MainMenu;
using UnityEngine;
using UnityEngine.UI;

namespace PFE.Tests.Editor.Mods
{
    /// <summary>
    /// The Main Menu Mods tab, end to end at the seams the player touches: discovered mods render
    /// as rows, the row toggle persists through the config store, and Apply rebuilds the content
    /// registry and campaign catalog so the next game start reflects the active mod set.
    /// </summary>
    [TestFixture]
    public class MainMenuModsTabTests
    {
        private GameObject _menuObject;
        private MainMenuSceneBootstrapper _bootstrapper;
        private ModLoader _loader;
        private ContentRegistry _registry;
        private CampaignCatalog _catalog;

        [SetUp]
        public void SetUp()
        {
            _loader = new ModLoader(
                new InMemoryModConfigStore(),
                new[] { typeof(FakeCodeModPlugin).Assembly })
            {
                ModsRoot = Path.Combine(Path.GetTempPath(), "pfe_test_no_such_mods_dir")
            };
            _registry = new ContentRegistry();
            _catalog = ScriptableObject.CreateInstance<CampaignCatalog>();

            _menuObject = new GameObject("TestMainMenu", typeof(Canvas), typeof(MainMenuSceneBootstrapper));
            _bootstrapper = _menuObject.GetComponent<MainMenuSceneBootstrapper>();

            // EditMode tests do not run Awake, and the full Awake cannot be invoked either: the
            // Load-Game panel's population touches SaveManager.Instance, whose DontDestroyOnLoad
            // is play-mode-only. Build just the Mods panel — the piece under test.
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            Type type = typeof(MainMenuSceneBootstrapper);
            Assert.That((bool)type.GetMethod("LoadResources", flags).Invoke(_bootstrapper, null), Is.True,
                "Menu resources must load in the editor");
            type.GetMethod("ConfigureCanvas", flags).Invoke(_bootstrapper, null);

            var panelParent = new GameObject("PanelParent", typeof(RectTransform));
            panelParent.transform.SetParent(_menuObject.transform, false);
            type.GetMethod("BuildModsPanel", flags)
                .Invoke(_bootstrapper, new object[] { (RectTransform)panelParent.transform });

            _bootstrapper.ConfigureModServices(_loader, _registry, _catalog);
        }

        [TearDown]
        public void TearDown()
        {
            if (_menuObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_menuObject);
            }
            if (_catalog != null)
            {
                UnityEngine.Object.DestroyImmediate(_catalog);
            }
        }

        [Test]
        public void DiscoveredMods_RenderAsRows()
        {
            Assert.That(_bootstrapper.ModsListContainer, Is.Not.Null,
                "The Mods panel must exist — menu resources are expected to load in the editor");
            Assert.That(_bootstrapper.RenderedModRowCount, Is.EqualTo(2),
                "Both fixture plugins render a row (name/version/author/order/badge + toggle)");
        }

        [Test]
        public void RowToggle_PersistsEnabledState()
        {
            var toggles = _bootstrapper.ModsListContainer.GetComponentsInChildren<Toggle>();
            Assert.That(toggles.Length, Is.EqualTo(2));

            // Row order follows discovery order: the dependency (pfe.testmod) sorts first.
            toggles[0].isOn = false;

            Assert.That(_loader.IsModEnabled(FakeCodeModPlugin.ModId), Is.False,
                "Flipping the row toggle must reach ModLoader.SetModEnabled");
            Assert.That(_loader.ConfigStore.Load().Enabled[FakeCodeModPlugin.ModId], Is.False,
                "…and persist through the config store");
        }

        [Test]
        public void Apply_RebuildsRegistryAndCatalog_ExcludingDisabledMods()
        {
            _bootstrapper.ApplyModChanges();
            Assert.That(_catalog.AllLands, Has.Some.Matches<LandDefinition>(
                l => l.landId == FakeCodeModPlugin.LandId),
                "Enabled fixture mod's land reaches the campaign catalog via Apply");

            var toggles = _bootstrapper.ModsListContainer.GetComponentsInChildren<Toggle>();
            toggles[0].isOn = false;
            _bootstrapper.ApplyModChanges();

            Assert.That(_catalog.AllLands, Has.None.Matches<LandDefinition>(
                l => l.landId == FakeCodeModPlugin.LandId),
                "After Apply, the disabled mod's land is gone from the catalog the next game start uses");
            Assert.That(_registry.Contains(PFE.ModAPI.ContentType.Campaign, FakeCodeModPlugin.LandId), Is.False);
            Assert.That(_bootstrapper.ModsStatusText, Does.Contain("Applied mod settings"));
        }

        [Test]
        public void SettingsButton_RendersOnlyForModsWithSchema()
        {
            var buttons = _bootstrapper.ModsListContainer.GetComponentsInChildren<Button>();
            int settingsButtons = 0;
            foreach (Button button in buttons)
            {
                if (button.name == "SettingsButton") settingsButtons++;
            }

            Assert.That(settingsButtons, Is.EqualTo(1),
                "Only the fixture mod with a settings schema gets a Settings button");
        }

        [Test]
        public void OpenModSettings_RendersSchemaRows_AndEditsPersist()
        {
            _bootstrapper.OpenModSettings(FakeCodeModPlugin.ModId);

            Assert.That(_bootstrapper.IsModSettingsOpen, Is.True);
            Assert.That(_bootstrapper.RenderedModSettingRowCount, Is.EqualTo(2),
                "One row per declared setting");

            var inputs = _bootstrapper.ModSettingsPanel.GetComponentsInChildren<InputField>();
            Assert.That(inputs.Length, Is.EqualTo(1), "The Int setting renders an input field");
            inputs[0].onEndEdit.Invoke("7");
            Assert.That(_loader.GetModSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingIntKey), Is.EqualTo("7"));
            Assert.That(_loader.ConfigStore.Load().GetSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingIntKey),
                Is.EqualTo("7"), "Edits persist through the config store immediately");

            inputs[0].onEndEdit.Invoke("99");
            Assert.That(_loader.GetModSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingIntKey), Is.EqualTo("9"),
                "Values are clamped to the declared [Min, Max] range");

            var settingToggles = _bootstrapper.ModSettingsPanel.GetComponentsInChildren<Toggle>();
            Assert.That(settingToggles.Length, Is.EqualTo(1), "The Bool setting renders a toggle");
            settingToggles[0].isOn = false;
            Assert.That(_loader.GetModSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingBoolKey), Is.EqualTo("false"));

            _bootstrapper.CloseModSettings();
            Assert.That(_bootstrapper.IsModSettingsOpen, Is.False);
        }
    }
}
