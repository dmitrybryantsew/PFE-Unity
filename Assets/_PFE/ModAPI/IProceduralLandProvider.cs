using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.ModAPI
{
    /// <summary>
    /// Self-describing metadata for a procedural land supplied by a mod.
    /// Allows host UI (Travel / Debug overlay) and CampaignCatalog to list and configure
    /// any procedural mod land without hardcoding mod-specific IDs or biome lists.
    /// </summary>
    [Serializable]
    public sealed class ProceduralLandDescriptor
    {
        public string LandId;
        public string DisplayName;
        public bool SupportsSeed;
        public string[] Biomes = Array.Empty<string>();
        public string DefaultBiome;
        public Vector3Int InitialBoundsMin = Vector3Int.zero;
        public Vector3Int InitialBoundsMax = new Vector3Int(1, 1, 1);
        public string Tip = "rnd";
        public int Stage = 1;
    }

    /// <summary>
    /// Parameters chosen by the player/UI before launching a procedural mod land.
    /// </summary>
    [Serializable]
    public sealed class ProceduralLandLaunchRequest
    {
        public string LandId;
        public string Biome;
        public uint? Seed;
    }

    /// <summary>
    /// Pure-data spawn entry inside a procedural room blueprint (AS3 top-down row space).
    /// </summary>
    [Serializable]
    public sealed class ProceduralObjectSpawn
    {
        public string Id;
        public string Type;
        public Vector2Int As3TileCoord;
        public string Parameters;
    }

    /// <summary>
    /// Pure-data background decoration entry inside a procedural room blueprint (AS3 top-down row space).
    /// </summary>
    [Serializable]
    public sealed class ProceduralBackDecoration
    {
        public string Id;
        public Vector2Int As3TileCoord;
    }

    /// <summary>
    /// Pure-data environment settings for a procedural room blueprint.
    /// </summary>
    [Serializable]
    public sealed class ProceduralRoomEnvironment
    {
        public string MusicTrack = "music_0";
        public string ColorScheme = "";
        public string BackgroundWall = "";
        public int WaterType;
        public float Radiation;
        public float VisibilityMultiplier = 1f;
        public int Darkness;
        public int BorderType;
    }

    /// <summary>
    /// Pure-data room blueprint returned by an <see cref="IProceduralLandProvider"/>.
    /// The host translates blueprints into runtime RoomTemplate/RoomInstance objects so mods
    /// never depend on host map/rendering internals.
    /// </summary>
    [Serializable]
    public sealed class ProceduralRoomBlueprint
    {
        public Vector3Int Position;
        public string RoomId;
        public string Archetype = "pass";
        public int Danger;
        public string TileDataString;
        public int[] DoorPorts = Array.Empty<int>();

        /// <summary>
        /// The room canvas in tiles. 0 means "the host default" (48x25, the authored AS3 grid), so a
        /// provider written before this field existed keeps building 48x25 rooms. Every room of one
        /// land must declare the same size: the host's door slots are anchored to the room's own
        /// walls, and two neighbours only line up when those walls coincide.
        /// </summary>
        public int Width;

        /// <summary>The room canvas height in tiles. 0 means the host default. See <see cref="Width"/>.</summary>
        public int Height;

        public List<ProceduralObjectSpawn> Objects = new List<ProceduralObjectSpawn>();
        public bool HasPlayerSpawn;
        public Vector2Int As3PlayerSpawn;
        public List<ProceduralBackDecoration> BackgroundDecorations = new List<ProceduralBackDecoration>();
        public ProceduralRoomEnvironment Environment = new ProceduralRoomEnvironment();
    }

    /// <summary>
    /// Pure-data result of building an initial procedural land grid.
    /// </summary>
    public sealed class ProceduralLandBuildResult
    {
        public bool Success;
        public string LandId;
        public Vector3Int BoundsMin;
        public Vector3Int BoundsMax;
        public Vector3Int StartRoomPosition;
        public List<ProceduralRoomBlueprint> Rooms = new List<ProceduralRoomBlueprint>();
    }

    /// <summary>
    /// Pure-data result of dynamically expanding a procedural land grid near its boundaries.
    /// </summary>
    public sealed class ProceduralLandExpansionResult
    {
        public bool Expanded;
        public Vector3Int NewBoundsMin;
        public Vector3Int NewBoundsMax;
        public List<ProceduralRoomBlueprint> NewRooms = new List<ProceduralRoomBlueprint>();
    }

    /// <summary>
    /// Optional host-supplied context for mod console commands registered via
    /// <see cref="IModHostContext.RegisterConsoleCommands"/>.
    /// </summary>
    public interface IModConsoleContext
    {
        string CurrentLandId { get; }
        Vector3Int CurrentRoomPosition { get; }
        Vector3Int BoundsMin { get; }
        Vector3Int BoundsMax { get; }
        int RoomCount { get; }
        void RequestTravelToLand(string landId, bool forceRegenerate = true);

        /// <summary>Moves the player to the room at the given land coordinate, if it exists.</summary>
        bool RequestTeleportToRoom(Vector3Int roomPosition);

        /// <summary>Host-formatted diagnostics of the current room (doors, units, objects).</summary>
        string DescribeCurrentRoom();
    }

    /// <summary>
    /// Typed console command handler contract for mods.
    /// </summary>
    public interface IModConsoleCommandHandler
    {
        string Execute(string commandLine, IModConsoleContext context);
    }

    /// <summary>
    /// Pure-ModAPI capability interface for procedural land generation mods.
    /// Providers describe their supported lands/biomes/seed options and return pure room blueprints.
    /// </summary>
    public interface IProceduralLandProvider
    {
        bool HandlesLand(string landId);

        IReadOnlyList<ProceduralLandDescriptor> GetDescriptors();

        ProceduralLandDescriptor GetDescriptor(string landId);

        void SetPendingLaunchRequest(ProceduralLandLaunchRequest request);

        ProceduralLandLaunchRequest GetPendingLaunchRequest(string landId);

        ProceduralLandBuildResult BuildInitialLand(ProceduralLandLaunchRequest request);

        ProceduralLandExpansionResult TryExpandLand(
            string landId,
            Vector3Int playerRoomPos,
            Vector3Int currentBoundsMin,
            Vector3Int currentBoundsMax);
    }
}
