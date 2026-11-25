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
        public const string SettingRoomSizeMode = "room_size_mode";
        public const string SettingRoomWidth = "room_width";
        public const string SettingRoomHeight = "room_height";

        /// <summary>Legacy single-value canvas key ("80x40"), superseded by the two numeric keys.</summary>
        public const string SettingRoomSizeLegacy = "room_size";

        public const int MinLandDimension = 2;
        public const int MaxLandDimension = 10;
        public const int DefaultLandWidth = 5;
        public const int DefaultLandHeight = 5;

        public const string RoomSizeModeRandom = "random";
        public const string RoomSizeModeFixed = "fixed";

        /// <summary>
        /// Room canvas bounds, in tiles. 48x25 is the authored AS3 grid and the floor: the 22 door
        /// slots sit on a fixed tile lattice (9 tiles apart across the top/bottom, 4 down each side)
        /// and the host carves them against the room's own walls, so a canvas may only grow past
        /// 48x25, never shrink below it.
        /// <para>The ceiling is a performance boundary, not a structural one. Measured per room
        /// (offline, 25-room land): 80x48 = 36 ms, 120x60 = 83 ms, 160x80 = 151 ms, 240x120 = 479 ms,
        /// 320x160 = 1.1 s, 500x250 = 5.7 s. All of them fill 100% of rooms except 500x250, which
        /// refuses about 1 in 25 (absorbed by the per-room seed retry). The cost is quadratic in the
        /// linear dimension, so a 10x10 land at 500x250 is roughly 10 minutes of generation and
        /// ~125k tile cells per room — usable for a perf experiment, not for play.</para>
        /// </summary>
        public const int MinRoomWidth = 48;
        public const int MaxRoomWidth = 500;
        public const int MinRoomHeight = 25;
        public const int MaxRoomHeight = 250;
        public const int DefaultRoomWidth = 64;
        public const int DefaultRoomHeight = 32;

        /// <summary>
        /// Above this many interior tiles a room is slow enough (seconds each) that the land build
        /// visibly stalls; the provider logs the expected cost so a long freeze is not mistaken for
        /// a hang. Chosen at the 240x120 measurement (479 ms/room).
        /// </summary>
        private const int HeavyCanvasTiles = 240 * 120;

        /// <summary>How many extra seeds a room gets before the land gives up on its canvas.</summary>
        private const int RoomSeedRetries = 3;

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
        private int _currentRoomWidth = RRSynth.GRID_W;
        private int _currentRoomHeight = RRSynth.GRID_H;

        public RandomRoomsLandProvider(IModSettings settings = null)
        {
            _settings = settings;
        }

        public uint CurrentSeed => _currentSeed;
        public string CurrentBiome => _currentBiome;

        /// <summary>The canvas the live land's rooms are built at, in tiles.</summary>
        public int CurrentRoomWidth => _currentRoomWidth;

        /// <summary>The canvas the live land's rooms are built at, in tiles.</summary>
        public int CurrentRoomHeight => _currentRoomHeight;

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
            _currentRoomWidth = RRSynth.GRID_W;
            _currentRoomHeight = RRSynth.GRID_H;

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

                ResolveRoomSize(out _currentRoomWidth, out _currentRoomHeight);

                int roomTiles = _currentRoomWidth * _currentRoomHeight;
                if (roomTiles >= HeavyCanvasTiles)
                {
                    // Measured curve, fitted: cost is ~area^1.46 (36 ms at 80x48, 5.7 s at 500x250).
                    double perRoomMs = 36.0 * Math.Pow(roomTiles / (80.0 * 48.0), 1.46);
                    int roomCount = width * height;
                    long totalTiles = (long)roomTiles * roomCount;
                    Debug.LogWarning(
                        $"[RandomRooms] Heavy canvas {_currentRoomWidth}x{_currentRoomHeight} " +
                        $"({roomTiles:N0} tiles/room): {roomCount} rooms at ~{perRoomMs / 1000.0:0.0} s each, " +
                        $"so roughly {perRoomMs * roomCount / 1000.0:0} s of generation. TileData is a " +
                        $"class, so {totalTiles:N0} tiles is ~{totalTiles * 80 / (1024.0 * 1024.0):0} MB " +
                        "of live objects — keep the land small (2x2-3x3) at this size. This is a " +
                        "deliberate stress setting; lower Room width/height for normal play.");
                }
            }

            var result = new ProceduralLandBuildResult
            {
                Success = true,
                LandId = landId,
                BoundsMin = minBounds,
                BoundsMax = maxBounds,
                StartRoomPosition = Vector3Int.zero
            };

            try
            {
                BuildRooms(result, minBounds, maxBounds, landId);
            }
            catch (InvalidOperationException ex)
            {
                // Last resort: this seed's geometry cannot be partitioned at the chosen canvas even
                // after per-room seed retries. Rebuild the WHOLE land at the authored floor rather
                // than shrinking one room — every room must share one canvas or the host's door
                // slots carve at mismatched world rows.
                Debug.LogWarning(
                    $"[RandomRooms] Canvas {_currentRoomWidth}x{_currentRoomHeight} refused for land " +
                    $"'{landId}' ({ex.Message}); rebuilding at {RRSynth.GRID_W}x{RRSynth.GRID_H}.");
                _currentRoomWidth = RRSynth.GRID_W;
                _currentRoomHeight = RRSynth.GRID_H;
                _currentSerial = 0;
                result.Rooms.Clear();
                BuildRooms(result, minBounds, maxBounds, landId);
            }

            // One line that answers "why is the land black?" without a debugger: a land with zero
            // blueprints builds zero rooms, which renders as an empty black world.
            Debug.Log($"[RandomRooms] Built '{landId}': {result.Rooms.Count} rooms at " +
                      $"{_currentRoomWidth}x{_currentRoomHeight} tiles (seed {_currentSeed}, biome {_currentBiome}).");

            return result;
        }

        private void BuildRooms(
            ProceduralLandBuildResult result,
            Vector3Int minBounds,
            Vector3Int maxBounds,
            string landId)
        {
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

        /// <summary>
        /// Decide this land's room canvas. "fixed" uses the typed width/height exactly; "random"
        /// rolls each dimension independently between the floor and the typed value, from the land
        /// seed, so the same seed always produces the same canvas on both machines. Every room in
        /// the land then uses it — one canvas per land is what keeps neighbouring door slots aligned.
        /// </summary>
        private void ResolveRoomSize(out int width, out int height)
        {
            int maxWidth = ClampSetting(_settings, SettingRoomWidth, DefaultRoomWidth, MinRoomWidth, MaxRoomWidth);
            int maxHeight = ClampSetting(_settings, SettingRoomHeight, DefaultRoomHeight, MinRoomHeight, MaxRoomHeight);

            // A config written before the two numeric keys existed carries one "WxH" value.
            if (_settings != null && !HasSetting(SettingRoomWidth) && !HasSetting(SettingRoomHeight))
            {
                ParseLegacyCanvas(_settings.GetString(SettingRoomSizeLegacy, null), ref maxWidth, ref maxHeight);
            }

            string mode = _settings != null ? _settings.GetString(SettingRoomSizeMode, RoomSizeModeRandom) : RoomSizeModeRandom;
            if (string.Equals(mode, RoomSizeModeFixed, StringComparison.OrdinalIgnoreCase))
            {
                width = maxWidth;
                height = maxHeight;
                return;
            }

            width = RollDimension(maxWidth, MinRoomWidth, 41);
            height = RollDimension(maxHeight, MinRoomHeight, 43);
        }

        /// <summary>Roll one canvas dimension in [floor..max] from the land seed.</summary>
        private int RollDimension(int max, int floor, int salt)
        {
            if (max <= floor) return floor;
            uint span = (uint)(max - floor + 1);
            return floor + (int)(_currentPlanner.Value(0, 0, salt) % span);
        }

        private int ClampSetting(IModSettings settings, string key, int fallback, int min, int max)
        {
            int value = settings != null ? settings.GetInt(key, fallback) : fallback;
            if (value < min) value = min;
            if (value > max) value = max;
            return value;
        }

        /// <summary>True when the stored config actually carries a value for this key.</summary>
        private bool HasSetting(string key)
        {
            string raw = _settings != null ? _settings.GetString(key, null) : null;
            return !string.IsNullOrEmpty(raw);
        }

        /// <summary>Read a legacy "WxH" canvas value into the two dimensions, clamped.</summary>
        private static void ParseLegacyCanvas(string raw, ref int width, ref int height)
        {
            if (string.IsNullOrEmpty(raw)) return;

            string[] parts = raw.Split(new[] { 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return;

            if (int.TryParse(parts[0], out int parsedWidth)) width = Mathf.Clamp(parsedWidth, MinRoomWidth, MaxRoomWidth);
            if (int.TryParse(parts[1], out int parsedHeight)) height = Mathf.Clamp(parsedHeight, MinRoomHeight, MaxRoomHeight);
        }

        private SynthesizedRoom Synthesize(string name, string archetype, Vector3Int pos, bool safe)
        {
            int[] ports = _currentPlanner.Ports(pos.x, pos.y);
            uint baseSeed = _currentPlanner.Value(pos.x, pos.y, 99);
            var synth = new RRSynth();

            // The partition/ladder fit search refuses a small fraction of rooms at the larger
            // canvases ("Partition demand search exhausted: storeys"). A refusal is seed-specific
            // geometry, so re-rolling the room's own seed almost always fits at the SAME canvas —
            // which is what matters, because shrinking one room would break the land's door lattice.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
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
                            seed = baseSeed + (uint)attempt * 7919u,
                            partition = "rules",
                            contentVersion = "13"
                        },
                        width: _currentRoomWidth,
                        height: _currentRoomHeight);

                    synthRoom.name = name;
                    synthRoom.archetype = archetype;
                    return synthRoom;
                }
                catch (InvalidOperationException) when (attempt < RoomSeedRetries)
                {
                    // Try another seed; the loop re-throws once the budget is spent.
                }
            }
        }
    }
}
