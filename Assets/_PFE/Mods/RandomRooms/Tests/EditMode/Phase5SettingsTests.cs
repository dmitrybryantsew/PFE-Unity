using System.Collections.Generic;
using NUnit.Framework;
using PFE.ModAPI;
using PFE.Mods.RandomRooms.Unity;
using UnityEngine;

namespace PFE.Mods.RandomRooms.Tests.EditMode
{
    /// <summary>
    /// Mod settings through the seam: the plugin declares a schema the host renders, and the
    /// provider honors land size / endless growth values read live from IModSettings.
    /// </summary>
    [TestFixture]
    public class Phase5SettingsTests
    {
        private sealed class StubSettings : IModSettings
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();

            public int GetInt(string key, int fallback = 0) =>
                Values.TryGetValue(key, out string v) && int.TryParse(v, out int i) ? i : fallback;

            public float GetFloat(string key, float fallback = 0f) =>
                Values.TryGetValue(key, out string v) && float.TryParse(v, out float f) ? f : fallback;

            public bool GetBool(string key, bool fallback = false) =>
                Values.TryGetValue(key, out string v) && bool.TryParse(v, out bool b) ? b : fallback;

            public string GetString(string key, string fallback = "") =>
                Values.TryGetValue(key, out string v) ? v : fallback;
        }

        private static ProceduralLandLaunchRequest Request(string landId)
        {
            return new ProceduralLandLaunchRequest { LandId = landId, Biome = "plant", Seed = 42u };
        }

        [Test]
        public void Plugin_DeclaresSettingsSchema()
        {
            var plugin = new RandomRoomsPlugin();
            IReadOnlyList<ModSettingDescriptor> schema = plugin.GetSettingsSchema();

            Assert.That(schema, Is.Not.Null);
            Assert.That(schema.Count, Is.EqualTo(3));
            Assert.That(schema[0].Key, Is.EqualTo(RandomRoomsLandProvider.SettingLandWidth));
            Assert.That(schema[0].Type, Is.EqualTo(ModSettingType.Int));
            Assert.That(schema[0].MinValue, Is.EqualTo(RandomRoomsLandProvider.MinLandDimension));
            Assert.That(schema[0].MaxValue, Is.EqualTo(RandomRoomsLandProvider.MaxLandDimension));
            Assert.That(schema[1].Key, Is.EqualTo(RandomRoomsLandProvider.SettingLandHeight));
            Assert.That(schema[2].Key, Is.EqualTo(RandomRoomsLandProvider.SettingEndlessGrowth));
            Assert.That(schema[2].Type, Is.EqualTo(ModSettingType.Bool));
        }

        [Test]
        public void NoSettings_BuildsDefault5x5Grid()
        {
            var provider = new RandomRoomsLandProvider();
            ProceduralLandBuildResult result = provider.BuildInitialLand(Request("random_rooms"));

            Assert.That(result.BoundsMax, Is.EqualTo(new Vector3Int(5, 5, 1)));
            Assert.That(result.Rooms.Count, Is.EqualTo(25));
        }

        [Test]
        public void LandSizeSettings_ResizeGrid()
        {
            var settings = new StubSettings
            {
                Values =
                {
                    [RandomRoomsLandProvider.SettingLandWidth] = "7",
                    [RandomRoomsLandProvider.SettingLandHeight] = "3"
                }
            };
            var provider = new RandomRoomsLandProvider(settings);
            ProceduralLandBuildResult result = provider.BuildInitialLand(Request("random_rooms"));

            Assert.That(result.BoundsMax, Is.EqualTo(new Vector3Int(7, 3, 1)));
            Assert.That(result.Rooms.Count, Is.EqualTo(21));

            ProceduralRoomBlueprint exit = result.Rooms.Find(bp => bp.Position == new Vector3Int(6, 2, 0));
            Assert.That(exit, Is.Not.Null, "The exit room must sit at the far corner of the resized grid");
            Assert.That(exit.Archetype, Is.EqualTo("end"));
        }

        [Test]
        public void LandSizeSettings_AreClamped()
        {
            var settings = new StubSettings
            {
                Values =
                {
                    [RandomRoomsLandProvider.SettingLandWidth] = "99",
                    [RandomRoomsLandProvider.SettingLandHeight] = "0"
                }
            };
            var provider = new RandomRoomsLandProvider(settings);
            ProceduralLandBuildResult result = provider.BuildInitialLand(Request("random_rooms"));

            Assert.That(result.BoundsMax, Is.EqualTo(new Vector3Int(
                RandomRoomsLandProvider.MaxLandDimension, RandomRoomsLandProvider.MinLandDimension, 1)));
        }

        [Test]
        public void EndlessGrowth_CanBeDisabled()
        {
            var settings = new StubSettings
            {
                Values = { [RandomRoomsLandProvider.SettingEndlessGrowth] = "false" }
            };
            var provider = new RandomRoomsLandProvider(settings);
            ProceduralLandBuildResult build = provider.BuildInitialLand(Request("random_rooms"));

            ProceduralLandExpansionResult expansion = provider.TryExpandLand(
                "random_rooms", new Vector3Int(4, 4, 0), build.BoundsMin, build.BoundsMax);

            Assert.That(expansion.Expanded, Is.False, "Growth disabled in settings must suppress expansion");
            Assert.That(expansion.NewRooms, Is.Empty);
        }

        [Test]
        public void EndlessGrowth_DefaultsToEnabled()
        {
            var provider = new RandomRoomsLandProvider(new StubSettings());
            ProceduralLandBuildResult build = provider.BuildInitialLand(Request("random_rooms"));

            ProceduralLandExpansionResult expansion = provider.TryExpandLand(
                "random_rooms", new Vector3Int(4, 4, 0), build.BoundsMin, build.BoundsMax);

            Assert.That(expansion.Expanded, Is.True, "Unset setting keeps the endless dungeon growing");
        }

        [Test]
        public void Showroom_IgnoresLandSizeSettings()
        {
            var settings = new StubSettings
            {
                Values =
                {
                    [RandomRoomsLandProvider.SettingLandWidth] = "7",
                    [RandomRoomsLandProvider.SettingLandHeight] = "3"
                }
            };
            var provider = new RandomRoomsLandProvider(settings);
            ProceduralLandBuildResult result = provider.BuildInitialLand(Request("rr_showroom"));

            Assert.That(result.BoundsMax, Is.EqualTo(new Vector3Int(4, 3, 1)),
                "The showroom is a fixed exhibit and never resizes");
            Assert.That(result.Rooms.Count, Is.EqualTo(12));
        }
    }
}