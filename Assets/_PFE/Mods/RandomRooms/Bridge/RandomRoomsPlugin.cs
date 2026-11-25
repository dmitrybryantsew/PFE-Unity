using System.Collections.Generic;
using PFE.ModAPI;

[assembly: PfeModPlugin(typeof(PFE.Mods.RandomRooms.Unity.RandomRoomsPlugin))]

namespace PFE.Mods.RandomRooms.Unity
{
    /// <summary>
    /// RandomRooms mod entry point, discovered by the host's ModLoader through the assembly-level
    /// <see cref="PfeModPluginAttribute"/>. Registers the procedural land provider and the 'rr'
    /// console commands; the host auto-registers the lands declared by the provider's descriptors.
    /// Also exposes the mod's settings schema (land grid size, endless growth, room canvas) which the
    /// host renders in the Main Menu Mods tab.
    /// </summary>
    public sealed class RandomRoomsPlugin : IModPlugin, IModSettingsProvider
    {
        public ModManifest Manifest { get; } = new ModManifest
        {
            modId = "pfe.mods.randomrooms",
            displayName = "RandomRooms",
            version = "13.2.0",
            author = "RandomRooms Contributors / PFE Port",
            description = "Pure C# procedural dungeon generator ported from RandomRooms v13.2",
            loadOrder = 100
        };

        public IReadOnlyList<ModSettingDescriptor> GetSettingsSchema()
        {
            return new[]
            {
                new ModSettingDescriptor
                {
                    Key = RandomRoomsLandProvider.SettingLandWidth,
                    DisplayName = "Land width (rooms)",
                    Description = "Rooms across the starting map of the 'random_rooms' land.",
                    Type = ModSettingType.Int,
                    DefaultValue = "5",
                    MinValue = RandomRoomsLandProvider.MinLandDimension,
                    MaxValue = RandomRoomsLandProvider.MaxLandDimension
                },
                new ModSettingDescriptor
                {
                    Key = RandomRoomsLandProvider.SettingLandHeight,
                    DisplayName = "Land height (rooms)",
                    Description = "Rooms down the starting map of the 'random_rooms' land.",
                    Type = ModSettingType.Int,
                    DefaultValue = "5",
                    MinValue = RandomRoomsLandProvider.MinLandDimension,
                    MaxValue = RandomRoomsLandProvider.MaxLandDimension
                },
                new ModSettingDescriptor
                {
                    Key = RandomRoomsLandProvider.SettingEndlessGrowth,
                    DisplayName = "Endless growth",
                    Description = "The map grows new rooms when you approach its edge.",
                    Type = ModSettingType.Bool,
                    DefaultValue = "true"
                },
                new ModSettingDescriptor
                {
                    Key = RandomRoomsLandProvider.SettingRoomSizeMode,
                    DisplayName = "Room size",
                    Description = "Fixed: every land uses the selected room size. Random: each land rolls " +
                                  "a size up to the selected one, from its own seed.",
                    Type = ModSettingType.Choice,
                    DefaultValue = RandomRoomsLandProvider.RoomSizeModeRandom,
                    Options = new[] { RandomRoomsLandProvider.RoomSizeModeRandom, RandomRoomsLandProvider.RoomSizeModeFixed }
                },
                new ModSettingDescriptor
                {
                    Key = RandomRoomsLandProvider.SettingRoomWidth,
                    DisplayName = "Room width (tiles)",
                    Description = $"Type the room width in tiles. {RandomRoomsLandProvider.MinRoomWidth}-" +
                                  $"{RandomRoomsLandProvider.MaxRoomWidth}; random mode rolls each land " +
                                  $"between {RandomRoomsLandProvider.MinRoomWidth} and this value. " +
                                  $"{RandomRoomsLandProvider.MinRoomWidth} is the authored AS3 grid and the " +
                                  "floor — the 22 door slots use a fixed tile lattice, so a room cannot " +
                                  "be narrower. Cost grows fast: 80 tiles wide is ~36 ms per room, 500 " +
                                  "is ~5.7 s.",
                    Type = ModSettingType.Int,
                    DefaultValue = RandomRoomsLandProvider.DefaultRoomWidth.ToString(),
                    MinValue = RandomRoomsLandProvider.MinRoomWidth,
                    MaxValue = RandomRoomsLandProvider.MaxRoomWidth
                },
                new ModSettingDescriptor
                {
                    Key = RandomRoomsLandProvider.SettingRoomHeight,
                    DisplayName = "Room height (tiles)",
                    Description = $"Type the room height in tiles. {RandomRoomsLandProvider.MinRoomHeight}-" +
                                  $"{RandomRoomsLandProvider.MaxRoomHeight}. Measured per room at 5x5: " +
                                  "80x48 = 36 ms, 160x80 = 151 ms, 320x160 = 1.1 s, 500x250 = 5.7 s. " +
                                  "Above ~240x120 the land build logs its expected duration and will " +
                                  "visibly stall — that is the generator working, not a hang.",
                    Type = ModSettingType.Int,
                    DefaultValue = RandomRoomsLandProvider.DefaultRoomHeight.ToString(),
                    MinValue = RandomRoomsLandProvider.MinRoomHeight,
                    MaxValue = RandomRoomsLandProvider.MaxRoomHeight
                }
            };
        }

        public void Register(IModHostContext host)
        {
            if (host == null) return;

            var provider = new RandomRoomsLandProvider(host.Settings);
            host.RegisterProceduralLandProvider(provider);
            host.RegisterConsoleCommands("rr", new RandomRoomsConsole(provider));
        }
    }
}