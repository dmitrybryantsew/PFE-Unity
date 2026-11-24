using System;
using System.Collections.Generic;
using PFE.Data;
using PFE.Data.Definitions.Campaign;
using PFE.ModAPI;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// Data-driven model/presenter for the F2 Lands travel tab (<see cref="PFE.Core.Scripting.PlayerDebugEditorOverlay"/>).
    /// Lists all lands in <see cref="CampaignCatalog.AllLands"/>, queries <see cref="IProceduralLandProvider"/>
    /// descriptors without hardcoding mod IDs or biomes, and dispatches travel via <see cref="CampaignManager.TransitionToLand"/>.
    /// </summary>
    public sealed class TravelOverlayPresenter
    {
        public sealed class LandEntry
        {
            public LandDefinition Land { get; }
            public ProceduralLandDescriptor Descriptor { get; }
            public bool IsProceduralMod => Descriptor != null;
            public string[] Biomes => Descriptor?.Biomes ?? Array.Empty<string>();
            public bool SupportsSeed => Descriptor != null && Descriptor.SupportsSeed;

            public LandEntry(LandDefinition land, ProceduralLandDescriptor descriptor)
            {
                Land = land;
                Descriptor = descriptor;
            }
        }

        private readonly Dictionary<string, string> _selectedBiomeByLand = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _seedTextByLand = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Random _random = new System.Random();

        public CampaignCatalog Catalog { get; set; }
        public ProceduralLandRegistry ProceduralRegistry { get; set; }

        public TravelOverlayPresenter(CampaignCatalog catalog = null, ProceduralLandRegistry proceduralRegistry = null)
        {
            Catalog = catalog;
            ProceduralRegistry = proceduralRegistry;
        }

        public List<LandEntry> BuildEntries()
        {
            var entries = new List<LandEntry>();
            if (Catalog == null || Catalog.AllLands == null)
            {
                return entries;
            }

            IReadOnlyList<LandDefinition> allLands = Catalog.AllLands;
            for (int i = 0; i < allLands.Count; i++)
            {
                LandDefinition land = allLands[i];
                if (land == null || string.IsNullOrEmpty(land.landId)) continue;

                ProceduralLandDescriptor descriptor = ProceduralRegistry?.GetDescriptor(land.landId);
                entries.Add(new LandEntry(land, descriptor));
            }

            return entries;
        }

        public string GetSelectedBiome(string landId, ProceduralLandDescriptor descriptor = null)
        {
            if (string.IsNullOrEmpty(landId)) return string.Empty;

            if (_selectedBiomeByLand.TryGetValue(landId, out string selected) && !string.IsNullOrEmpty(selected))
            {
                return selected;
            }

            descriptor ??= ProceduralRegistry?.GetDescriptor(landId);
            if (descriptor == null) return string.Empty;

            string fallback = !string.IsNullOrEmpty(descriptor.DefaultBiome)
                ? descriptor.DefaultBiome
                : (descriptor.Biomes != null && descriptor.Biomes.Length > 0 ? descriptor.Biomes[0] : string.Empty);

            _selectedBiomeByLand[landId] = fallback;
            return fallback;
        }

        public void SetSelectedBiome(string landId, string biome)
        {
            if (string.IsNullOrEmpty(landId)) return;
            _selectedBiomeByLand[landId] = biome ?? string.Empty;
        }

        public string GetSeedText(string landId)
        {
            if (string.IsNullOrEmpty(landId)) return string.Empty;
            return _seedTextByLand.TryGetValue(landId, out string seed) ? seed : string.Empty;
        }

        public void SetSeedText(string landId, string seedText)
        {
            if (string.IsNullOrEmpty(landId)) return;
            _seedTextByLand[landId] = seedText ?? string.Empty;
        }

        public uint RollRandomSeed(string landId)
        {
            uint value = (uint)_random.Next(1, int.MaxValue);
            SetSeedText(landId, value.ToString());
            return value;
        }

        /// <summary>
        /// Stores the <see cref="ProceduralLandLaunchRequest"/> on the handling
        /// <see cref="IProceduralLandProvider"/> if <paramref name="landId"/> is procedural.
        /// </summary>
        public ProceduralLandLaunchRequest ApplyPendingLaunchRequest(string landId)
        {
            if (string.IsNullOrEmpty(landId) || ProceduralRegistry == null)
            {
                return null;
            }

            IProceduralLandProvider provider = ProceduralRegistry.GetProviderForLand(landId);
            if (provider == null)
            {
                return null;
            }

            ProceduralLandDescriptor descriptor = provider.GetDescriptor(landId);
            string biome = GetSelectedBiome(landId, descriptor);
            uint? seed = null;

            if (descriptor != null && descriptor.SupportsSeed)
            {
                string rawSeed = GetSeedText(landId);
                if (!string.IsNullOrWhiteSpace(rawSeed) && uint.TryParse(rawSeed.Trim(), out uint parsed))
                {
                    seed = parsed;
                }
                else
                {
                    seed = (uint)_random.Next(1, int.MaxValue);
                }
            }

            var request = new ProceduralLandLaunchRequest
            {
                LandId = landId,
                Biome = biome,
                Seed = seed
            };
            provider.SetPendingLaunchRequest(request);
            return request;
        }

        /// <summary>
        /// Stores any procedural launch parameters on the provider and triggers standard
        /// <see cref="CampaignManager.TransitionToLand"/> (publishing <c>LandBuildRequestMessage</c>).
        /// </summary>
        public void ConfirmTravel(CampaignManager campaign, string landId, bool forceRegenerate = true)
        {
            if (campaign == null || string.IsNullOrWhiteSpace(landId))
            {
                return;
            }

            ApplyPendingLaunchRequest(landId);
            campaign.TransitionToLand(landId, null, forceRegenerate);
        }
    }
}
