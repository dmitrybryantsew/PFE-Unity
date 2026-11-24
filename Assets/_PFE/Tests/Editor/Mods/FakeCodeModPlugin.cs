using System;
using System.Collections.Generic;
using PFE.ModAPI;
using UnityEngine;

[assembly: PfeModPlugin(typeof(PFE.Tests.Editor.Mods.FakeCodeModPlugin))]
[assembly: PfeModPlugin(typeof(PFE.Tests.Editor.Mods.FakeDependentModPlugin))]

namespace PFE.Tests.Editor.Mods
{
    /// <summary>
    /// A minimal code mod used by the host-side mod tests. Discovered through the assembly
    /// attribute exactly like a real mod, so the whole ModLoader pipeline (discovery, ordering,
    /// enable/disable, registration) runs against it. Inert outside test runs: it registers one
    /// fake procedural land and one console prefix, nothing else.
    /// </summary>
    public sealed class FakeCodeModPlugin : IModPlugin, IModSettingsProvider
    {
        public const string ModId = "pfe.testmod";
        public const string LandId = "test_procedural_land";
        public const string ConsolePrefix = "fake";
        public const string SettingIntKey = "fake_int";
        public const string SettingBoolKey = "fake_flag";

        public ModManifest Manifest { get; } = new ModManifest
        {
            modId = ModId,
            displayName = "PFE Test Mod (test fixture)",
            version = "0.1.0",
            author = "PFE Tests",
            description = "Inert code mod used by EditMode tests.",
            loadOrder = 100
        };

        public IReadOnlyList<ModSettingDescriptor> GetSettingsSchema()
        {
            return new[]
            {
                new ModSettingDescriptor
                {
                    Key = SettingIntKey,
                    DisplayName = "Fake Int",
                    Type = ModSettingType.Int,
                    DefaultValue = "3",
                    MinValue = 1,
                    MaxValue = 9
                },
                new ModSettingDescriptor
                {
                    Key = SettingBoolKey,
                    DisplayName = "Fake Flag",
                    Type = ModSettingType.Bool,
                    DefaultValue = "true"
                }
            };
        }

        public void Register(IModHostContext host)
        {
            if (host == null) return;
            host.RegisterProceduralLandProvider(new FakeProceduralLandProvider());
            host.RegisterConsoleCommands(ConsolePrefix, new FakeConsoleHandler());
        }
    }

    /// <summary>
    /// Second fixture mod: declares a dependency on <see cref="FakeCodeModPlugin"/> with a LOWER
    /// loadOrder, so the ordering test can prove dependencies beat raw loadOrder.
    /// </summary>
    public sealed class FakeDependentModPlugin : IModPlugin
    {
        public const string ModId = "pfe.testmod.dependent";

        public ModManifest Manifest { get; } = new ModManifest
        {
            modId = ModId,
            displayName = "PFE Test Mod Dependent (test fixture)",
            version = "0.1.0",
            author = "PFE Tests",
            description = "Depends on pfe.testmod; must sort after it despite loadOrder 10.",
            loadOrder = 10,
            dependencies = new[] { FakeCodeModPlugin.ModId }
        };

        public void Register(IModHostContext host)
        {
        }
    }

    public sealed class FakeProceduralLandProvider : IProceduralLandProvider
    {
        private readonly Dictionary<string, ProceduralLandLaunchRequest> _pending =
            new Dictionary<string, ProceduralLandLaunchRequest>(StringComparer.OrdinalIgnoreCase);

        private static readonly ProceduralLandDescriptor[] Descriptors =
        {
            new ProceduralLandDescriptor
            {
                LandId = FakeCodeModPlugin.LandId,
                DisplayName = "Test Procedural Land",
                SupportsSeed = true,
                Biomes = new[] { "alpha", "beta" },
                DefaultBiome = "alpha",
                InitialBoundsMin = Vector3Int.zero,
                InitialBoundsMax = new Vector3Int(2, 2, 1),
                Tip = "rnd",
                Stage = 1
            }
        };

        public bool HandlesLand(string landId) =>
            string.Equals(landId, FakeCodeModPlugin.LandId, StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<ProceduralLandDescriptor> GetDescriptors() => Descriptors;

        public ProceduralLandDescriptor GetDescriptor(string landId) =>
            HandlesLand(landId) ? Descriptors[0] : null;

        public void SetPendingLaunchRequest(ProceduralLandLaunchRequest request)
        {
            if (request != null && HandlesLand(request.LandId))
            {
                _pending[request.LandId] = request;
            }
        }

        public ProceduralLandLaunchRequest GetPendingLaunchRequest(string landId) =>
            landId != null && _pending.TryGetValue(landId, out var request) ? request : null;

        public ProceduralLandBuildResult BuildInitialLand(ProceduralLandLaunchRequest request)
        {
            return new ProceduralLandBuildResult
            {
                Success = true,
                LandId = FakeCodeModPlugin.LandId,
                BoundsMin = Vector3Int.zero,
                BoundsMax = new Vector3Int(2, 2, 1),
                StartRoomPosition = Vector3Int.zero
            };
        }

        public ProceduralLandExpansionResult TryExpandLand(
            string landId, Vector3Int playerRoomPos, Vector3Int currentBoundsMin, Vector3Int currentBoundsMax)
        {
            return new ProceduralLandExpansionResult
            {
                Expanded = false,
                NewBoundsMin = currentBoundsMin,
                NewBoundsMax = currentBoundsMax
            };
        }
    }

    public sealed class FakeConsoleHandler : IModConsoleCommandHandler
    {
        public string Execute(string commandLine, IModConsoleContext context) => "fake ok";
    }
}
