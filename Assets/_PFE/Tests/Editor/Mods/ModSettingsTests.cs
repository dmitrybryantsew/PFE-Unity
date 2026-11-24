using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Data;
using PFE.ModAPI;

namespace PFE.Tests.Editor.Mods
{
    /// <summary>
    /// Mod settings: schema exposure, the live accessor mods read through, and persistence of
    /// per-mod values in mods_config.json (including legacy files written before settings existed).
    /// </summary>
    [TestFixture]
    public class ModSettingsTests
    {
        private static ModLoader CreateLoader(IModConfigStore store)
        {
            return new ModLoader(store, new[] { typeof(FakeCodeModPlugin).Assembly })
            {
                ModsRoot = Path.Combine(Path.GetTempPath(), "pfe_test_no_such_mods_dir")
            };
        }

        [Test]
        public void Schema_IsExposedOnlyForSettingsProviderMods()
        {
            ModLoader loader = CreateLoader(new InMemoryModConfigStore());
            loader.DiscoverMods();

            IReadOnlyList<ModSettingDescriptor> schema = loader.GetSettingsSchema(FakeCodeModPlugin.ModId);
            Assert.That(schema, Is.Not.Null);
            Assert.That(schema.Count, Is.EqualTo(2));
            Assert.That(schema[0].Key, Is.EqualTo(FakeCodeModPlugin.SettingIntKey));
            Assert.That(schema[0].Type, Is.EqualTo(ModSettingType.Int));
            Assert.That(schema[1].Key, Is.EqualTo(FakeCodeModPlugin.SettingBoolKey));

            Assert.That(loader.HasModSettings(FakeCodeModPlugin.ModId), Is.True);
            Assert.That(loader.HasModSettings(FakeDependentModPlugin.ModId), Is.False,
                "The dependent fixture declares no schema");
            Assert.That(loader.GetSettingsSchema("pfe.no.such.mod"), Is.Null);
        }

        [Test]
        public void SetModSetting_PersistsThroughStore()
        {
            var store = new InMemoryModConfigStore();
            ModLoader loader = CreateLoader(store);

            loader.SetModSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingIntKey, "7");

            ModConfigSnapshot persisted = store.Load();
            Assert.That(persisted.GetSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingIntKey), Is.EqualTo("7"));

            ModLoader reloaded = CreateLoader(store);
            reloaded.DiscoverMods();
            Assert.That(reloaded.GetModSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingIntKey), Is.EqualTo("7"),
                "A fresh loader over the same store must see the persisted value");
        }

        [Test]
        public void SettingsAccessor_ReadsLiveValuesWithFallbacks()
        {
            var store = new InMemoryModConfigStore();
            ModLoader loader = CreateLoader(store);
            IModSettings settings = loader.GetSettingsAccessor(FakeCodeModPlugin.ModId);

            Assert.That(settings.GetInt(FakeCodeModPlugin.SettingIntKey, 3), Is.EqualTo(3),
                "Unset key yields the caller's fallback");
            Assert.That(settings.GetBool(FakeCodeModPlugin.SettingBoolKey, true), Is.True);

            loader.SetModSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingIntKey, "7");
            Assert.That(settings.GetInt(FakeCodeModPlugin.SettingIntKey, 3), Is.EqualTo(7),
                "The accessor is live — no re-discovery needed after an edit");

            loader.SetModSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingIntKey, "not-a-number");
            Assert.That(settings.GetInt(FakeCodeModPlugin.SettingIntKey, 3), Is.EqualTo(3),
                "Unparsable values fall back instead of throwing");

            loader.SetModSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingBoolKey, "false");
            Assert.That(settings.GetBool(FakeCodeModPlugin.SettingBoolKey, true), Is.False);
            loader.SetModSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingBoolKey, "1");
            Assert.That(settings.GetBool(FakeCodeModPlugin.SettingBoolKey, false), Is.True);

            loader.SetModSetting(FakeCodeModPlugin.ModId, "ratio", "1.5");
            Assert.That(settings.GetFloat("ratio", 0f), Is.EqualTo(1.5f).Within(0.0001f));

            loader.SetModSetting(FakeCodeModPlugin.ModId, "label", "hello");
            Assert.That(settings.GetString("label", ""), Is.EqualTo("hello"));
            Assert.That(settings.GetString("missing", "fb"), Is.EqualTo("fb"));
        }

        [Test]
        public void DiscoverMods_LoadsPersistedSettings()
        {
            var store = new InMemoryModConfigStore(
                initialSettings: new Dictionary<string, Dictionary<string, string>>
                {
                    [FakeCodeModPlugin.ModId] = new Dictionary<string, string>
                    {
                        [FakeCodeModPlugin.SettingIntKey] = "5"
                    }
                });

            ModLoader loader = CreateLoader(store);
            loader.DiscoverMods();

            Assert.That(loader.GetModSetting(FakeCodeModPlugin.ModId, FakeCodeModPlugin.SettingIntKey), Is.EqualTo("5"));
        }

        [Test]
        public void JsonStore_RoundTripsEnabledAndSettings()
        {
            string path = Path.Combine(Path.GetTempPath(), $"pfe_mods_config_{System.Guid.NewGuid():N}.json");
            try
            {
                var store = new JsonFileModConfigStore(path);
                var snapshot = new ModConfigSnapshot();
                snapshot.Enabled["pfe.mods.randomrooms"] = false;
                snapshot.SetSetting("pfe.mods.randomrooms", "land_width", "7");
                snapshot.SetSetting("pfe.mods.randomrooms", "endless_growth", "false");
                store.Save(snapshot);

                ModConfigSnapshot loaded = store.Load();
                Assert.That(loaded.Enabled["pfe.mods.randomrooms"], Is.False);
                Assert.That(loaded.GetSetting("pfe.mods.randomrooms", "land_width"), Is.EqualTo("7"));
                Assert.That(loaded.GetSetting("pfe.mods.randomrooms", "endless_growth"), Is.EqualTo("false"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void JsonStore_LoadsLegacyFileWithoutSettings()
        {
            string path = Path.Combine(Path.GetTempPath(), $"pfe_mods_config_{System.Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path,
                    "{\n  \"mods\": [\n    {\n      \"modId\": \"pfe.mods.randomrooms\",\n      \"enabled\": false\n    }\n  ]\n}");

                ModConfigSnapshot loaded = new JsonFileModConfigStore(path).Load();
                Assert.That(loaded.Enabled["pfe.mods.randomrooms"], Is.False);
                Assert.That(loaded.GetSetting("pfe.mods.randomrooms", "land_width"), Is.Null,
                    "Legacy entries simply have no settings");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}