using System;
using PFE.ModAPI;
using PFE.Mods.RandomRooms.Core;
using UnityEngine;

namespace PFE.Mods.RandomRooms.Unity
{
    /// <summary>
    /// Translates SynthesizedRoom PCG data into a pure-data <see cref="ProceduralRoomBlueprint"/>.
    /// Coordinates stay in canonical AS3 row space (top-down); the host's ProceduralLandBuilder
    /// performs the Unity Y-up conversion when it materialises the blueprint.
    /// </summary>
    public static class RandomRoomsBlueprintBuilder
    {
        public static ProceduralRoomBlueprint ToBlueprint(SynthesizedRoom synth, Vector3Int position)
        {
            if (synth == null) throw new ArgumentNullException(nameof(synth));

            var blueprint = new ProceduralRoomBlueprint
            {
                Position = position,
                RoomId = !string.IsNullOrEmpty(synth.name) ? synth.name : $"rr_{synth.archetype}_{position.x}_{position.y}",
                Archetype = !string.IsNullOrEmpty(synth.archetype) ? synth.archetype : "pass",
                Danger = Mathf.Clamp(synth.danger, 0, 20),
                TileDataString = synth.tileDataString ?? string.Join("\n", synth.rows),
                DoorPorts = new int[22],
                HasPlayerSpawn = synth.spawn != default,
                As3PlayerSpawn = synth.spawn != default ? new Vector2Int(synth.spawn.x, synth.spawn.y) : default
            };

            if (synth.ports != null)
            {
                for (int i = 0; i < synth.ports.Length && i < 22; i++)
                {
                    blueprint.DoorPorts[i] = synth.ports[i];
                }
            }

            if (synth.objects != null)
            {
                foreach (var obj in synth.objects)
                {
                    if (obj == null) continue;
                    blueprint.Objects.Add(new ProceduralObjectSpawn
                    {
                        Id = obj.id,
                        Type = GetSpawnType(obj.id),
                        As3TileCoord = new Vector2Int(obj.x, obj.y),
                        Parameters = SerializeParameters(obj)
                    });
                }
            }

            if (synth.backs != null)
            {
                foreach (var back in synth.backs)
                {
                    if (back == null) continue;
                    blueprint.BackgroundDecorations.Add(new ProceduralBackDecoration
                    {
                        Id = back.id,
                        As3TileCoord = new Vector2Int(back.x, back.y)
                    });
                }
            }

            // borderType = 0 prevents the host's DoorCarver.ApplyBorder from expanding the 48x25 grid.
            var environment = new ProceduralRoomEnvironment { BorderType = 0 };
            if (synth.options != null)
            {
                environment.MusicTrack = synth.options.music ?? "music_0";
                environment.ColorScheme = synth.options.color ?? "";
                environment.BackgroundWall = synth.options.backwall ?? "";
                environment.WaterType = synth.options.wtip;
                environment.Radiation = synth.options.wrad;
                environment.VisibilityMultiplier = synth.options.vis > 0 ? synth.options.vis : 1f;
                environment.Darkness = synth.options.darkness;
            }
            blueprint.Environment = environment;

            return blueprint;
        }

        public static string GetSpawnType(string objectId)
        {
            if (string.IsNullOrEmpty(objectId)) return "obj";
            if (objectId == "player") return "player";
            if (objectId.StartsWith("tarakan") || objectId == "unit" || objectId.StartsWith("enemy") ||
                objectId.StartsWith("turret") || objectId.StartsWith("mine") || objectId.StartsWith("boss") ||
                objectId.StartsWith("bloat") || objectId.StartsWith("slime") || objectId.StartsWith("ant") ||
                objectId.StartsWith("rat") || objectId.StartsWith("zombie") || objectId.StartsWith("robot") ||
                objectId.StartsWith("raider")) return "unit";
            if (objectId.StartsWith("door")) return "door";
            if (objectId.StartsWith("box") || objectId.StartsWith("chest") || objectId.StartsWith("bigbox") ||
                objectId.StartsWith("woodbox") || objectId.StartsWith("medbox") || objectId.StartsWith("case") ||
                objectId.StartsWith("wallcab")) return "box";
            if (objectId.StartsWith("checkpoint")) return "checkpoint";
            if (objectId.StartsWith("trap")) return "trap";
            if (objectId.StartsWith("bonus") || objectId == "xp") return "bonus";
            if (objectId.StartsWith("area")) return "area";
            return "obj";
        }

        private static string SerializeParameters(SynthesizedObject obj)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(obj.code))
                sb.Append($"code=\"{obj.code}\" ");
            if (!string.IsNullOrEmpty(obj.fixture))
                sb.Append($"fixture=\"{obj.fixture}\" ");
            if (!string.IsNullOrEmpty(obj.rrContent))
                sb.Append($"rrContent=\"{obj.rrContent}\" ");
            if (!string.IsNullOrEmpty(obj.rrMount))
                sb.Append($"rrMount=\"{obj.rrMount}\" ");
            if (!string.IsNullOrEmpty(obj.uid))
                sb.Append($"uid=\"{obj.uid}\" ");

            if (obj.attrs != null)
            {
                foreach (var kvp in obj.attrs)
                {
                    if (kvp.Key != "x" && kvp.Key != "y" && kvp.Key != "id" && kvp.Key != "code")
                    {
                        sb.Append($"{kvp.Key}=\"{kvp.Value}\" ");
                    }
                }
            }
            return sb.ToString().Trim();
        }
    }
}
