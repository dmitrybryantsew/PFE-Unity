using System;
using System.Text;
using NUnit.Framework;
using PFE.Mods.RandomRooms.Core;

namespace PFE.Mods.RandomRooms.Tests.EditMode
{
    [TestFixture]
    public class Phase1GeometryTests
    {
        [Test]
        public void RRSeed_Determinism_And_Fork_Isolation()
        {
            var seedA1 = new RRSeed(12345);
            var seedA2 = new RRSeed(12345);

            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(seedA1.Next(), seedA2.Next(), 1e-12, $"Mismatch at index {i}");
            }

            // Test Fork isolation: forking should not advance parent stream
            var parent = new RRSeed(42);
            var parentCopy = new RRSeed(42);

            var child1 = parent.Fork("child_test");
            var child2 = parent.Fork("child_test");

            // Child streams derived with same label must produce identical sequences
            for (int i = 0; i < 50; i++)
            {
                Assert.AreEqual(child1.Next(), child2.Next(), 1e-12);
            }

            // Parent stream must be completely untouched by Fork calls
            for (int i = 0; i < 50; i++)
            {
                Assert.AreEqual(parent.Next(), parentCopy.Next(), 1e-12);
            }

            // Different fork labels must produce different seeds
            var childA = new RRSeed(100).Fork("label_A");
            var childB = new RRSeed(100).Fork("label_B");
            Assert.AreNotEqual(childA.seed, childB.seed);
        }

        [Test]
        public void RRPorts_Mirror_Involutive_ForAllSlots()
        {
            for (int p = 0; p < 22; p++)
            {
                int mirrored = RRPorts.Mirrored(p);
                int doubleMirrored = RRPorts.Mirrored(mirrored);
                Assert.AreEqual(p, doubleMirrored, $"Mirror(Mirror({p})) != {p}, got {doubleMirrored}");
            }

            int[] ports = new int[22];
            for (int p = 0; p < 22; p++) ports[p] = p + 1;
            int[] mirrorArray = RRPorts.Mirror(RRPorts.Mirror(ports));
            for (int p = 0; p < 22; p++)
            {
                Assert.AreEqual(ports[p], mirrorArray[p], $"Array mirror failed at slot {p}");
            }
        }

        [Test]
        public void RRGeometryBatchTests_64Rooms_PassAllAuditsAndDeterminism()
        {
            string[] biomes = { "plant", "stable", "sewer", "mane" };
            int roomsPerBiome = 16;
            int totalRooms = biomes.Length * roomsPerBiome;

            for (int bIdx = 0; bIdx < biomes.Length; bIdx++)
            {
                string biome = biomes[bIdx];

                for (int roomIdx = 0; roomIdx < roomsPerBiome; roomIdx++)
                {
                    uint roomSeed = (uint)(100000 + bIdx * 1000 + roomIdx * 37 + 1);

                    // Generate first run
                    var arch1 = GenerateRoomWithRetry(biome, roomSeed);

                    // Traversal assertions
                    Assert.DoesNotThrow(() => RRTraversal.Check(arch1, false), $"Room {biome} seed {roomSeed} failed regular traversal");
                    if (arch1.pools.Count > 0)
                    {
                        Assert.DoesNotThrow(() => RRTraversal.Check(arch1, true), $"Room {biome} seed {roomSeed} failed dry sewer traversal");
                    }

                    // Check declared ports are open
                    for (int p = 0; p < 22; p++)
                    {
                        if (arch1.ports[p] >= 2)
                        {
                            PortRect b = RRPorts.Rect(p, arch1.ports[p]);
                            for (int y = b.y0; y <= b.y1; y++)
                            {
                                for (int x = b.x0; x <= b.x1; x++)
                                {
                                    Assert.IsFalse(arch1.Solid(x, y), $"Declared port {p} blocked at ({x},{y}) in {biome} seed {roomSeed}");
                                }
                            }
                        }
                    }

                    // Generate second run with identical seed -> must be identical
                    var arch2 = GenerateRoomWithRetry(biome, roomSeed);

                    string grid1 = ExportGrid(arch1.grid);
                    string grid2 = ExportGrid(arch2.grid);
                    Assert.AreEqual(grid1, grid2, $"Seed {roomSeed} in biome {biome} was not deterministic!");
                }
            }
        }

        private RRArchitecture GenerateRoomWithRetry(string biome, uint roomSeed)
        {
            var seed = new RRSeed(roomSeed);
            var mapPlan = new RRMapPlan(seed.seed);
            int[] ports = mapPlan.Ports(2, 2);

            for (int attempt = 1; attempt <= 48; attempt++)
            {
                try
                {
                    var childSeed = seed.Fork($"geometry:{attempt}");
                    var arch = new RRArchitecture(() => childSeed.Next());
                    var context = new GenerationContext
                    {
                        contentVersion = "13",
                        partition = "rules",
                        seed = roomSeed
                    };
                    arch.Build(biome, "", ports, context);
                    return arch;
                }
                catch
                {
                    if (attempt == 48) throw;
                }
            }
            throw new InvalidOperationException($"Could not synthesize room after 48 attempts for {biome} seed {roomSeed}");
        }

        private string ExportGrid(string[,] grid)
        {
            var sb = new StringBuilder();
            for (int y = 0; y < 25; y++)
            {
                for (int x = 0; x < 48; x++)
                {
                    sb.Append(grid[y, x]).Append(';');
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
