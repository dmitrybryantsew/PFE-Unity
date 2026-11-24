using System.Collections.Generic;
using NUnit.Framework;
using PFE.ModAPI;
using PFE.Mods.RandomRooms.Core;
using PFE.Mods.RandomRooms.Unity;
using UnityEngine;

namespace PFE.Mods.RandomRooms.Tests.EditMode
{
    /// <summary>
    /// The provider's pure-data output: complete blueprint grids, AS3 coordinate space preserved,
    /// symmetric port declarations between neighbours, and determinism per seed.
    /// </summary>
    [TestFixture]
    public class Phase3BridgeTests
    {
        [Test]
        public void BlueprintBuilder_TranslatesSynthesizedRoom()
        {
            var synth = new RRSynth();
            int[] ports = RRPorts.Empty();
            ports[2] = 3;  // Right slot 2
            ports[13] = 3; // Left slot 2
            ports[8] = 2;  // Bottom slot 2
            ports[19] = 2; // Top slot 2

            SynthesizedRoom synthRoom = synth.Generate(
                n: 1,
                biome: "stable",
                rtype: "pass",
                boundary: ports,
                depth: 1,
                peaceful: false,
                safe: false,
                context: new GenerationContext { seed = 424242u, partition = "rules", contentVersion = "13" });

            synthRoom.name = "test_room_0_0";
            ProceduralRoomBlueprint bp = RandomRoomsBlueprintBuilder.ToBlueprint(synthRoom, new Vector3Int(1, 2, 0));

            Assert.That(bp, Is.Not.Null);
            Assert.That(bp.RoomId, Is.EqualTo("test_room_0_0"));
            Assert.That(bp.Position, Is.EqualTo(new Vector3Int(1, 2, 0)));
            Assert.That(bp.DoorPorts, Has.Length.EqualTo(22));
            Assert.That(bp.DoorPorts[2], Is.EqualTo(3));
            Assert.That(bp.DoorPorts[13], Is.EqualTo(3));
            Assert.That(bp.DoorPorts[8], Is.EqualTo(2));
            Assert.That(bp.DoorPorts[19], Is.EqualTo(2));
            Assert.That(bp.Environment.BorderType, Is.EqualTo(0), "BorderType must be 0 to prevent expanding array");

            string[] rows = bp.TileDataString.Split('\n');
            Assert.That(rows.Length, Is.EqualTo(25), "TileDataString must have exactly 25 rows");

            // Blueprint objects stay in AS3 top-down row space; the host converts on materialisation.
            for (int i = 0; i < bp.Objects.Count; i++)
            {
                Assert.That(bp.Objects[i].As3TileCoord.y, Is.EqualTo(synthRoom.objects[i].y),
                    "Object rows must NOT be Y-flipped inside the blueprint");
            }
        }

        [TestCase("plant", 1001)]
        [TestCase("stable", 2002)]
        [TestCase("sewer", 3003)]
        [TestCase("mane", 4004)]
        public void BuildInitialLand_ReturnsCompleteGrid(string biome, int seed)
        {
            var provider = new RandomRoomsLandProvider();
            ProceduralLandBuildResult result = provider.BuildInitialLand(
                new ProceduralLandLaunchRequest { LandId = "random_rooms", Biome = biome, Seed = (uint)seed });

            Assert.That(result.Success, Is.True);
            Assert.That(result.BoundsMax, Is.EqualTo(new Vector3Int(5, 5, 1)));
            Assert.That(result.StartRoomPosition, Is.EqualTo(Vector3Int.zero));
            Assert.That(result.Rooms.Count, Is.EqualTo(25), "5x5 map must contain exactly 25 room blueprints");

            var byPos = new Dictionary<Vector3Int, ProceduralRoomBlueprint>();
            foreach (ProceduralRoomBlueprint bp in result.Rooms)
            {
                Assert.That(byPos.ContainsKey(bp.Position), Is.False, $"Duplicate room at {bp.Position}");
                byPos[bp.Position] = bp;

                Assert.That(bp.DoorPorts, Has.Length.EqualTo(22));
                Assert.That(bp.TileDataString.Split('\n').Length, Is.EqualTo(25));
                Assert.That(bp.Environment.BorderType, Is.EqualTo(0));
            }

            for (int x = 0; x < 5; x++)
            {
                for (int y = 0; y < 5; y++)
                {
                    Assert.That(byPos.ContainsKey(new Vector3Int(x, y, 0)), Is.True, $"Room at ({x},{y}) must exist");
                }
            }

            Assert.That(byPos[Vector3Int.zero].RoomId, Is.EqualTo("rr_begin"));
            Assert.That(byPos[Vector3Int.zero].Archetype, Is.EqualTo("beg0"));
            Assert.That(byPos[new Vector3Int(4, 4, 0)].RoomId, Is.EqualTo("rr_exit"));
            Assert.That(byPos[new Vector3Int(4, 4, 0)].Archetype, Is.EqualTo("end"));

            // Adjacent port declarations agree: wherever the link rule (min quality >= 2) fires,
            // both sides declare the same quality — the blueprint-level mirror of the old
            // "every active door points back symmetrically" assertion.
            int linkedEdges = 0;
            for (int x = 0; x < 5; x++)
            {
                for (int y = 0; y < 5; y++)
                {
                    ProceduralRoomBlueprint current = byPos[new Vector3Int(x, y, 0)];
                    if (x < 4)
                    {
                        linkedEdges += CountSymmetricLinks(current, byPos[new Vector3Int(x + 1, y, 0)], 0, 5);
                    }
                    if (y < 4)
                    {
                        linkedEdges += CountSymmetricLinks(current, byPos[new Vector3Int(x, y + 1, 0)], 6, 10);
                    }
                }
            }

            Assert.That(linkedEdges, Is.GreaterThan(0), "5x5 map must have connected doors between rooms");
        }

        [Test]
        public void BuildInitialLand_IsDeterministic_PerSeed()
        {
            var first = new RandomRoomsLandProvider().BuildInitialLand(
                new ProceduralLandLaunchRequest { LandId = "random_rooms", Biome = "plant", Seed = 777u });
            var second = new RandomRoomsLandProvider().BuildInitialLand(
                new ProceduralLandLaunchRequest { LandId = "random_rooms", Biome = "plant", Seed = 777u });

            Assert.That(first.Rooms.Count, Is.EqualTo(second.Rooms.Count));
            for (int i = 0; i < first.Rooms.Count; i++)
            {
                Assert.That(second.Rooms[i].Position, Is.EqualTo(first.Rooms[i].Position));
                Assert.That(second.Rooms[i].TileDataString, Is.EqualTo(first.Rooms[i].TileDataString),
                    $"Room at {first.Rooms[i].Position} must be byte-identical for the same seed");
            }
        }

        [Test]
        public void Showroom_Returns4x3Grid()
        {
            var provider = new RandomRoomsLandProvider();
            ProceduralLandBuildResult result = provider.BuildInitialLand(
                new ProceduralLandLaunchRequest { LandId = "rr_showroom", Biome = "plant", Seed = 5555u });

            Assert.That(result.Success, Is.True);
            Assert.That(result.Rooms.Count, Is.EqualTo(12), "4x3 showroom must contain 12 rooms");

            var byPos = new Dictionary<Vector3Int, ProceduralRoomBlueprint>();
            foreach (ProceduralRoomBlueprint bp in result.Rooms) byPos[bp.Position] = bp;

            Assert.That(byPos[Vector3Int.zero].RoomId, Is.EqualTo("rr_begin"));
            Assert.That(byPos[new Vector3Int(3, 2, 0)].RoomId, Is.EqualTo("rr_show_end"));
        }

        [Test]
        public void PendingLaunchRequest_RoundTrips()
        {
            var provider = new RandomRoomsLandProvider();
            Assert.That(provider.GetPendingLaunchRequest("random_rooms"), Is.Null);

            provider.SetPendingLaunchRequest(new ProceduralLandLaunchRequest
            {
                LandId = "random_rooms",
                Biome = "mane",
                Seed = 99u
            });

            ProceduralLandLaunchRequest staged = provider.GetPendingLaunchRequest("random_rooms");
            Assert.That(staged, Is.Not.Null);
            Assert.That(staged.Biome, Is.EqualTo("mane"));
            Assert.That(staged.Seed, Is.EqualTo(99u));

            // Foreign lands are not staged on this provider.
            provider.SetPendingLaunchRequest(new ProceduralLandLaunchRequest { LandId = "other_land" });
            Assert.That(provider.GetPendingLaunchRequest("other_land"), Is.Null);
        }

        /// <summary>
        /// Counts linked port pairs between two horizontally (slots 0..5 vs 11..16) or vertically
        /// (6..10 vs 17..21) adjacent blueprints, asserting quality agreement on every link.
        /// </summary>
        private static int CountSymmetricLinks(
            ProceduralRoomBlueprint a, ProceduralRoomBlueprint b, int startSlot, int endSlot)
        {
            int links = 0;
            for (int p = startSlot; p <= endSlot; p++)
            {
                int qualityA = a.DoorPorts[p];
                int qualityB = b.DoorPorts[p + 11];
                if (System.Math.Min(qualityA, qualityB) < 2) continue;

                Assert.That(qualityB, Is.EqualTo(qualityA),
                    $"Linked doors must declare equal quality (room {a.Position} slot {p})");
                links++;
            }
            return links;
        }
    }
}
