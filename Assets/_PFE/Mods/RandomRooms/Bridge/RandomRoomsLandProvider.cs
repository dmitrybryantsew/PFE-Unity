using System;
using System.Collections.Generic;
using PFE.ModAPI;
using PFE.Mods.RandomRooms.Core;
using UnityEngine;

namespace PFE.Mods.RandomRooms.Unity
{
    /// <summary>
    /// Procedural land provider for 'random_rooms' (5x5, dynamically growing) and 'rr_showroom'
    /// (4x3 archetype showcase). Pure-ModAPI implementation: returns room blueprints and never
    /// touches host map/runtime internals — the host's ProceduralLandBuilder materialises them.
    /// </summary>
    public sealed class RandomRoomsLandProvider : IProceduralLandProvider
    {
        public const string LandId = "random_rooms";
        public const string ShowroomLandId = "rr_showroom";

        public const string SettingLandWidth = "land_width";
        public const string SettingLandHeight = "land_height";
        public const string SettingEndlessGrowth = "endless_growth";
        public const int MinLandDimension = 2;
        public const int MaxLandDimension = 10;
        public const int DefaultLandWidth = 5;
        public const int DefaultLandHeight = 5;

        private static readonly ProceduralLandDescriptor[] Descriptors =
        {
            new ProceduralLandDescriptor
            {
                LandId = LandId,
                DisplayName = "Random Rooms",
                SupportsSeed = true,
                Biomes = (string[])RRSynth.BIOMES.Clone(),
                DefaultBiome = "plant",
                InitialBoundsMin = Vector3Int.zero,
                InitialBoundsMax = new Vector3Int(5, 5, 1),
                Tip = "rnd",
                Stage = 1
            },
            new ProceduralLandDescriptor
            {
                LandId = ShowroomLandId,
                DisplayName = "Random Rooms Showroom",
                SupportsSeed = true,
                Biomes = (string[])RRSynth.BIOMES.Clone(),
                DefaultBiome = "plant",
                InitialBoundsMin = Vector3Int.zero,
                InitialBoundsMax = new Vector3Int(4, 3, 1),
                Tip = "rnd",
                Stage = 1
            }
        };

        private static readonly string[] ShowroomArchetypes =
        {
            "beg0", "beg1", "roof", "vert",
            "surf", "pass", "hall", "arena",
            "shop", "vault", "end", "end1"
        };

        private readonly Dictionary<string, ProceduralLandLaunchRequest> _pendingRequests =
            new Dictionary<string, ProceduralLandLaunchRequest>(StringComparer.OrdinalIgnoreCase);

        private readonly IModSettings _settings;
        private uint _currentSeed;
        private string _currentBiome = "plant";
        private RRMapPlan _currentPlanner;
        private int _currentSerial;

        public RandomRoomsLandProvider(IModSettings settings = null)
        {
            _settings = settings;
        }

        public uint CurrentSeed => _currentSeed;
        public string CurrentBiome => _currentBiome;

        public bool HandlesLand(string landId)
        {
            return string.Equals(landId, LandId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(landId, ShowroomLandId, StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<ProceduralLandDescriptor> GetDescriptors() => Descriptors;

        public ProceduralLandDescriptor GetDescriptor(string landId)
        {
            for (int i = 0; i < Descriptors.Length; i++)
            {
                if (string.Equals(Descriptors[i].LandId, landId, StringComparison.OrdinalIgnoreCase))
                {
                    return Descriptors[i];
                }
            }
            return null;
        }

        public void SetPendingLaunchRequest(ProceduralLandLaunchRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.LandId) || !HandlesLand(request.LandId))
            {
                return;
            }
            _pendingRequests[request.LandId] = request;
        }

        public ProceduralLandLaunchRequest GetPendingLaunchRequest(string landId)
        {
            if (string.IsNullOrEmpty(landId)) return null;
            return _pendingRequests.TryGetValue(landId, out var request) ? request : null;
        }

        public ProceduralLandBuildResult BuildInitialLand(ProceduralLandLaunchRequest request)
        {
            string landId = request != null && HandlesLand(request.LandId) ? request.LandId : LandId;
            ProceduralLandDescriptor descriptor = GetDescriptor(landId);

            _currentSeed = request != null && request.Seed.HasValue
                ? request.Seed.Value
                : (uint)new System.Random().Next(1, int.MaxValue);
            // xorshift32 degenerates at 0; the AS3 original mapped a zero seed to 1337.
            if (_currentSeed == 0) _currentSeed = 1337u;
            _currentBiome = request != null && !string.IsNullOrEmpty(request.Biome) ? request.Biome : "plant";
            _currentPlanner = new RRMapPlan(_currentSeed);
            _currentSerial = 0;

            Vector3Int minBounds = descriptor.InitialBoundsMin;
            Vector3Int maxBounds = descriptor.InitialBoundsMax;

            // The endless dungeon's starting grid comes from the mod settings (Mods tab);
            // the showroom stays a fixed exhibit.
            if (landId == LandId)
            {
                int width = Mathf.Clamp(
                    _settings != null ? _settings.GetInt(SettingLandWidth, DefaultLandWidth) : DefaultLandWidth,
                    MinLandDimension, MaxLandDimension);
                int height = Mathf.Clamp(
                    _settings != null ? _settings.GetInt(SettingLandHeight, DefaultLandHeight) : DefaultLandHeight,
                    MinLandDimension, MaxLandDimension);
                maxBounds = new Vector3Int(minBounds.x + width, minBounds.y + height, 1);
            }

            var result = new ProceduralLandBuildResult
            {
                Success = true,
                LandId = landId,
                BoundsMin = minBounds,
                BoundsMax = maxBounds,
                StartRoomPosition = Vector3Int.zero
            };

            for (int x = minBounds.x; x < maxBounds.x; x++)
            {
                for (int y = minBounds.y; y < maxBounds.y; y++)
                {
                    var pos = new Vector3Int(x, y, 0);
                    string archetype;
                    string name;

                    if (x == 0 && y == 0)
                    {
                        archetype = "beg0";
                        name = "rr_begin";
                    }
                    else if (x == maxBounds.x - 1 && y == maxBounds.y - 1)
                    {
                        archetype = "end";
                        name = landId == ShowroomLandId ? "rr_show_end" : "rr_exit";
                    }
                    else if (landId == ShowroomLandId)
                    {
                        int index = (y * maxBounds.x + x) % ShowroomArchetypes.Length;
                        archetype = ShowroomArchetypes[index];
                        name = $"rr_show_{archetype}_{x}_{y}";
                    }
                    else
                    {
                        archetype = "pass";
                        name = $"rr_room_{x}_{y}";
                    }

                    SynthesizedRoom synthRoom = Synthesize(name, archetype, pos, safe: x == 0 && y == 0);
                    result.Rooms.Add(RandomRoomsBlueprintBuilder.ToBlueprint(synthRoom, pos));
                }
            }

            return result;
        }

        public ProceduralLandExpansionResult TryExpandLand(
            string landId,
            Vector3Int playerRoomPos,
            Vector3Int currentBoundsMin,
            Vector3Int currentBoundsMax)
        {
            // Only the endless dungeon grows; the showroom is a fixed exhibit.
            if (!string.Equals(landId, LandId, StringComparison.OrdinalIgnoreCase) || _currentPlanner == null)
            {
                return new ProceduralLandExpansionResult
                {
                    Expanded = false,
                    NewBoundsMin = currentBoundsMin,
                    NewBoundsMax = currentBoundsMax
                };
            }

            if (_settings != null && !_settings.GetBool(SettingEndlessGrowth, true))
            {
                return new ProceduralLandExpansionResult
                {
                    Expanded = false,
                    NewBoundsMin = currentBoundsMin,
                    NewBoundsMax = currentBoundsMax
                };
            }

            int w = currentBoundsMax.x;
            int h = currentBoundsMax.y;

            bool right = playerRoomPos.x >= w - 2;
            bool down = playerRoomPos.y >= h - 2;

            if (!right && !down)
            {
                return new ProceduralLandExpansionResult
                {
                    Expanded = false,
                    NewBoundsMin = currentBoundsMin,
                    NewBoundsMax = currentBoundsMax
                };
            }

            int nw = w + (right ? 1 : 0);
            int nh = h + (down ? 1 : 0);

            var expansion = new ProceduralLandExpansionResult
            {
                Expanded = true,
                NewBoundsMin = currentBoundsMin,
                NewBoundsMax = new Vector3Int(nw, nh, currentBoundsMax.z)
            };

            for (int x = 0; x < nw; x++)
            {
                for (int y = 0; y < nh; y++)
                {
                    if (x < w && y < h) continue;

                    var pos = new Vector3Int(x, y, 0);
                    SynthesizedRoom synthRoom = Synthesize($"rr_growth_{x}_{y}", "pass", pos, safe: false);
                    expansion.NewRooms.Add(RandomRoomsBlueprintBuilder.ToBlueprint(synthRoom, pos));
                }
            }

            return expansion;
        }

        private SynthesizedRoom Synthesize(string name, string archetype, Vector3Int pos, bool safe)
        {
            int[] ports = _currentPlanner.Ports(pos.x, pos.y);
            var synth = new RRSynth();
            SynthesizedRoom synthRoom = synth.Generate(
                _currentSerial++,
                biome: _currentBiome,
                rtype: archetype,
                boundary: ports,
                depth: pos.x + pos.y,
                peaceful: false,
                safe: safe,
                context: new GenerationContext
                {
                    seed = _currentPlanner.Value(pos.x, pos.y, 99),
                    partition = "rules",
                    contentVersion = "13"
                });

            synthRoom.name = name;
            synthRoom.archetype = archetype;
            return synthRoom;
        }
    }
}
