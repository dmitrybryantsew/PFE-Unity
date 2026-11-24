using System.Collections.Generic;
using PFE.ModAPI;

[assembly: PfeModPlugin(typeof(PFE.Mods.RandomRooms.Unity.RandomRoomsPlugin))]

namespace PFE.Mods.RandomRooms.Unity
{
    /// <summary>
    /// RandomRooms mod entry point, discovered by the host's ModLoader through the assembly-level
    /// <see cref="PfeModPluginAttribute"/>. Registers the procedural land provider and the 'rr'
    /// console commands; the host auto-registers the lands declared by the provider's descriptors.
    /// Also exposes the mod's settings schema (land grid size, endless growth) which the host
    /// renders in the Main Menu Mods tab.
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