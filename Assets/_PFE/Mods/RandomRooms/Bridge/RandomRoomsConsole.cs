using System;
using PFE.ModAPI;
using UnityEngine;

namespace PFE.Mods.RandomRooms.Unity
{
    /// <summary>
    /// Developer console command handler for RandomRooms, registered under the 'rr' prefix.
    /// Travel goes through the host's <see cref="IModConsoleContext"/>; generation parameters are
    /// staged on the provider as a <see cref="ProceduralLandLaunchRequest"/> before the request.
    /// </summary>
    public sealed class RandomRoomsConsole : IModConsoleCommandHandler
    {
        private readonly RandomRoomsLandProvider _provider;

        public RandomRoomsConsole(RandomRoomsLandProvider provider)
        {
            _provider = provider;
        }

        public string Execute(string commandLine, IModConsoleContext context)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return GetHelpText();
            }

            string[] tokens = commandLine.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || !string.Equals(tokens[0], "rr", StringComparison.OrdinalIgnoreCase))
            {
                return "Unknown command prefix. RandomRooms commands start with 'rr'.";
            }

            if (tokens.Length == 1)
            {
                return GetHelpText();
            }

            string subCmd = tokens[1].ToLowerInvariant();
            switch (subCmd)
            {
                case "enter":
                {
                    string biome = tokens.Length > 2 ? tokens[2] : "plant";
                    uint seed = tokens.Length > 3 && uint.TryParse(tokens[3], out uint s) ? s : 1337u;
                    return StageAndTravel(RandomRoomsLandProvider.LandId, biome, seed, context);
                }

                case "showroom":
                {
                    string biome = tokens.Length > 2 ? tokens[2] : "plant";
                    uint seed = tokens.Length > 3 && uint.TryParse(tokens[3], out uint s) ? s : 1337u;
                    return StageAndTravel(RandomRoomsLandProvider.ShowroomLandId, biome, seed, context);
                }

                case "next":
                {
                    if (context == null) return "Error: no active game context.";
                    var exitPos = new Vector3Int(context.BoundsMax.x - 1, context.BoundsMax.y - 1, 0);
                    return context.RequestTeleportToRoom(exitPos)
                        ? $"Advanced to exit room at {exitPos}."
                        : $"Exit room at {exitPos} not found.";
                }

                case "status":
                {
                    if (context == null) return "No active game context.";
                    return $"[RandomRooms Status]\n" +
                           $"LandId: {context.CurrentLandId}\n" +
                           $"Bounds: {context.BoundsMin} to {context.BoundsMax}\n" +
                           $"Total Rooms: {context.RoomCount}\n" +
                           $"Current Room: {context.CurrentRoomPosition}\n" +
                           $"Seed: {(_provider != null ? _provider.CurrentSeed.ToString() : "N/A")}\n" +
                           $"Biome: {(_provider != null ? _provider.CurrentBiome : "N/A")}";
                }

                case "debug":
                {
                    if (context == null) return "No active game context.";
                    return $"[RandomRooms Debug]\n{context.DescribeCurrentRoom()}";
                }

                default:
                    return $"Unknown sub-command '{subCmd}'.\n{GetHelpText()}";
            }
        }

        private string StageAndTravel(string landId, string biome, uint seed, IModConsoleContext context)
        {
            if (_provider == null)
            {
                return "Error: RandomRoomsLandProvider is not available.";
            }
            if (context == null)
            {
                return "Error: no active game context (enter a land first).";
            }

            _provider.SetPendingLaunchRequest(new ProceduralLandLaunchRequest
            {
                LandId = landId,
                Biome = biome,
                Seed = seed
            });
            context.RequestTravelToLand(landId);
            return $"Travel to '{landId}' requested (biome: {biome}, seed: {seed}).";
        }

        public static string GetHelpText()
        {
            return "RandomRooms Console Commands:\n" +
                   "  rr enter <biome> [seed]    - Generate and enter 5x5 procedural dungeon (biomes: plant, stable, sewer, mane)\n" +
                   "  rr showroom <biome> [seed] - Generate and enter 4x3 archetype showroom\n" +
                   "  rr next                    - Teleport to exit room\n" +
                   "  rr status                  - Show current dungeon and map status\n" +
                   "  rr debug                   - Inspect current active room";
        }
    }
}
