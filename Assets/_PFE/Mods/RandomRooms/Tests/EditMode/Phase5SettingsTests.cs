using System.Collections.Generic;
using NUnit.Framework;
using PFE.ModAPI;
using PFE.Mods.RandomRooms.Core;
using PFE.Mods.RandomRooms.Unity;
using UnityEngine;

namespace PFE.Mods.RandomRooms.Tests.EditMode
{
    /// <summary>
    /// Mod settings through the seam: the plugin declares a schema the host renders, and the
    /// provider honors land size / endless growth / room canvas values read live from IModSettings.
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
            Assert.That(schema.Count, Is.EqualTo(6));
            Assert.That(schema[0].Key, Is.EqualTo(RandomRoomsLandProvider.SettingLandWidth));
            Assert.That(schema[0].Type, Is.EqualTo(ModSettingType.Int));
            Assert.That(schema[0].MinValue, Is.EqualTo(RandomRoomsLandProvider.MinLandDimension));
            Assert.That(schema[0].MaxValue, Is.EqualTo(RandomRoomsLandProvider.MaxLandDimension));
            Assert.That(schema[1].Key, Is.EqualTo(RandomRoomsLandProvider.SettingLandHeight));
            Assert.That(schema[2].Key, Is.EqualTo(RandomRoomsLandProvider.SettingEndlessGrowth));
            Assert.That(schema[2].Type, Is.EqualTo(ModSettingType.Bool));

            Assert.That(schema[3].Key, Is.EqualTo(RandomRoomsLandProvider.SettingRoomSizeMode));
            Assert.That(schema[3].Type, Is.EqualTo(ModSettingType.Choice));
            Assert.That(schema[3].Options, Is.EqualTo(new[]
            {
                RandomRoomsLandProvider.RoomSizeModeRandom,
                RandomRoomsLandProvider.RoomSizeModeFixed
            }));

            // The canvas is typed, not chosen from a preset list: two Int rows the host renders as
            // InputFields (ModSettingType.Int -> BuildNumericSettingControl), each carrying the
            // bounds the provider clamps to.
            Assert.That(schema[4].Key, Is.EqualTo(RandomRoomsLandProvider.SettingRoomWidth));
            Assert.That(schema[4].Type, Is.EqualTo(ModSettingType.Int));
            Assert.That(schema[4].MinValue, Is.EqualTo(RandomRoomsLandProvider.MinRoomWidth));
            Assert.That(schema[4].MaxValue, Is.EqualTo(RandomRoomsLandProvider.MaxRoomWidth));
            Assert.That(schema[4].DefaultValue, Is.EqualTo(RandomRoomsLandProvider.DefaultRoomWidth.ToString()));

            Assert.That(schema[5].Key, Is.EqualTo(RandomRoomsLandProvider.SettingRoomHeight));
            Assert.That(schema[5].Type, Is.EqualTo(ModSettingType.Int));
            Assert.That(schema[5].MinValue, Is.EqualTo(RandomRoomsLandProvider.MinRoomHeight));
            Assert.That(schema[5].MaxValue, Is.EqualTo(RandomRoomsLandProvider.MaxRoomHeight));
            Assert.That(schema[5].DefaultValue, Is.EqualTo(RandomRoomsLandProvider.DefaultRoomHeight.ToString()));
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

        [Test]
        public void RoomSizeFixed_EveryRoomUsesTheTypedCanvas()
        {
            var settings = new StubSettings
            {
                Values =
                {
                    [RandomRoomsLandProvider.SettingRoomSizeMode] = RandomRoomsLandProvider.RoomSizeModeFixed,
                    [RandomRoomsLandProvider.SettingRoomWidth] = "61",
                    [RandomRoomsLandProvider.SettingRoomHeight] = "37"
                }
            };
            var provider = new RandomRoomsLandProvider(settings);
            ProceduralLandBuildResult result = provider.BuildInitialLand(Request("random_rooms"));

            Assert.That(result.Rooms, Is.Not.Empty);
            Assert.That(provider.CurrentRoomWidth, Is.EqualTo(61), "A typed size must be honoured exactly");
            Assert.That(provider.CurrentRoomHeight, Is.EqualTo(37));

            foreach (ProceduralRoomBlueprint bp in result.Rooms)
            {
                Assert.That(bp.Width, Is.EqualTo(61), bp.RoomId);
                Assert.That(bp.Height, Is.EqualTo(37), bp.RoomId);

                string[] rows = bp.TileDataString.Replace("\r\n", "\n").Split('\n');
                Assert.That(rows.Length, Is.EqualTo(37), $"{bp.RoomId} row count");
                Assert.That(rows[0].Split('.').Length, Is.EqualTo(61), $"{bp.RoomId} row width");
            }
        }

        [Test]
        public void RoomSize_IsClampedToTheDoorLatticeFloorAndTypedCeiling()
        {
            // Width past the ceiling, height below the floor, on a 2x2 land so the build stays
            // cheap: the point is the clamps, not the geometry. (A clamped 500x250 land would take
            // minutes — the ceiling is a performance boundary, see the provider's constants.)
            var settings = new StubSettings
            {
                Values =
                {
                    [RandomRoomsLandProvider.SettingRoomSizeMode] = RandomRoomsLandProvider.RoomSizeModeFixed,
                    [RandomRoomsLandProvider.SettingLandWidth] = "2",
                    [RandomRoomsLandProvider.SettingLandHeight] = "2",
                    [RandomRoomsLandProvider.SettingRoomWidth] = "9999",
                    [RandomRoomsLandProvider.SettingRoomHeight] = "1"
                }
            };
            var provider = new RandomRoomsLandProvider(settings);
            provider.BuildInitialLand(Request("random_rooms"));

            Assert.That(provider.CurrentRoomWidth, Is.EqualTo(RandomRoomsLandProvider.MaxRoomWidth),
                "Above the ceiling is clamped, not rejected");
            Assert.That(provider.CurrentRoomHeight, Is.EqualTo(RandomRoomsLandProvider.MinRoomHeight),
                "Below 25 tiles the 22 door slots cannot be carved on the room's own walls");
        }

        [Test]
        public void RoomSizeRandom_IsSeededAndBoundedByTheTypedMaximum()
        {
            var settings = new StubSettings
            {
                Values =
                {
                    [RandomRoomsLandProvider.SettingRoomSizeMode] = RandomRoomsLandProvider.RoomSizeModeRandom,
                    [RandomRoomsLandProvider.SettingRoomWidth] = "80",
                    [RandomRoomsLandProvider.SettingRoomHeight] = "40"
                }
            };

            ProceduralLandBuildResult first = new RandomRoomsLandProvider(settings).BuildInitialLand(Request("random_rooms"));
            ProceduralLandBuildResult second = new RandomRoomsLandProvider(settings).BuildInitialLand(Request("random_rooms"));

            int width = first.Rooms[0].Width;
            int height = first.Rooms[0].Height;

            Assert.That(second.Rooms[0].Width, Is.EqualTo(width), "The same seed must roll the same canvas");
            Assert.That(second.Rooms[0].Height, Is.EqualTo(height), "The same seed must roll the same canvas");

            Assert.That(width, Is.InRange(RandomRoomsLandProvider.MinRoomWidth, 80),
                "A random canvas must stay inside the typed maximum and the door-lattice floor");
            Assert.That(height, Is.InRange(RandomRoomsLandProvider.MinRoomHeight, 40));

            foreach (ProceduralRoomBlueprint bp in first.Rooms)
            {
                Assert.That(bp.Width, Is.EqualTo(width), "One land, one canvas");
                Assert.That(bp.Height, Is.EqualTo(height), "One land, one canvas");
            }
        }

        [Test]
        public void RoomSizeRandom_VariesAcrossSeeds()
        {
            var settings = new StubSettings
            {
                Values =
                {
                    [RandomRoomsLandProvider.SettingRoomSizeMode] = RandomRoomsLandProvider.RoomSizeModeRandom,
                    [RandomRoomsLandProvider.SettingRoomWidth] = "80",
                    [RandomRoomsLandProvider.SettingRoomHeight] = "40"
                }
            };

            var sizes = new HashSet<int>();
            for (uint seed = 1; seed <= 16; seed++)
            {
                var provider = new RandomRoomsLandProvider(settings);
                ProceduralLandBuildResult result = provider.BuildInitialLand(new ProceduralLandLaunchRequest
                {
                    LandId = "random_rooms",
                    Biome = "plant",
                    Seed = seed
                });
                sizes.Add(result.Rooms[0].Width);
            }

            Assert.That(sizes.Count, Is.GreaterThan(1), "Random mode must not always pick the same canvas");
        }

        [Test]
        public void LegacyRoomSizeKey_IsStillHonoured()
        {
            // A config written before the two numeric keys carries one "WxH" value; it must keep
            // working instead of silently snapping back to the default canvas.
            var settings = new StubSettings
            {
                Values =
                {
                    [RandomRoomsLandProvider.SettingRoomSizeMode] = RandomRoomsLandProvider.RoomSizeModeFixed,
                    [RandomRoomsLandProvider.SettingRoomSizeLegacy] = "72x36"
                }
            };
            var provider = new RandomRoomsLandProvider(settings);
            provider.BuildInitialLand(Request("random_rooms"));

            Assert.That(provider.CurrentRoomWidth, Is.EqualTo(72));
            Assert.That(provider.CurrentRoomHeight, Is.EqualTo(36));
        }

        [Test]
        public void Showroom_KeepsTheAuthoredCanvas()
        {
            var settings = new StubSettings
            {
                Values =
                {
                    [RandomRoomsLandProvider.SettingRoomSizeMode] = RandomRoomsLandProvider.RoomSizeModeFixed,
                    [RandomRoomsLandProvider.SettingRoomWidth] = "80",
                    [RandomRoomsLandProvider.SettingRoomHeight] = "40"
                }
            };
            var provider = new RandomRoomsLandProvider(settings);
            ProceduralLandBuildResult result = provider.BuildInitialLand(Request("rr_showroom"));

            Assert.That(provider.CurrentRoomWidth, Is.EqualTo(RRSynth.GRID_W));
            Assert.That(provider.CurrentRoomHeight, Is.EqualTo(RRSynth.GRID_H));
            foreach (ProceduralRoomBlueprint bp in result.Rooms)
            {
                Assert.That(bp.Width, Is.EqualTo(RRSynth.GRID_W), bp.RoomId);
                Assert.That(bp.Height, Is.EqualTo(RRSynth.GRID_H), bp.RoomId);
            }
        }
    }
}