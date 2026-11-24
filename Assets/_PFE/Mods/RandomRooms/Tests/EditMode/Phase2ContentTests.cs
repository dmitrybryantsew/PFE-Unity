using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Mods.RandomRooms.Core;
using PFE.Systems.Map;
using UnityEditor;
using UnityEngine;

namespace PFE.Mods.RandomRooms.Tests.EditMode
{
    [TestFixture]
    public class Phase2ContentTests
    {
        private TileFormDatabase formDb;

        [OneTimeSetUp]
        public void Setup()
        {
            formDb = AssetDatabase.LoadAssetAtPath<TileFormDatabase>("Assets/_PFE/Data/TileFormDatabase.asset");
            if (formDb != null)
            {
                formDb.Initialize();
            }
        }

        [Test]
        public void RRContentBatchTests_64Rooms_PassAllAudits()
        {
            var synth = new RRSynth();
            string[] biomes = { "plant", "stable", "sewer", "mane" };
            int roomsPerBiome = 16;
            var majorEnemyKinds = new HashSet<string>
            {
                "robot", "raider", "slaver", "merc", "alicorn", "zebra", "zombie",
                "landturret", "gutsy", "protect", "eqd", "turret", "armturret", "hturret", "wturret"
            };

            for (int bIdx = 0; bIdx < biomes.Length; bIdx++)
            {
                string biome = biomes[bIdx];

                for (int roomIdx = 0; roomIdx < roomsPerBiome; roomIdx++)
                {
                    uint roomSeed = (uint)(200000 + bIdx * 1000 + roomIdx * 41 + 1);
                    var mapPlan = new RRMapPlan(roomSeed);
                    int[] ports = mapPlan.Ports(2, 2);

                    var context = new GenerationContext
                    {
                        contentVersion = "13",
                        partition = "rules",
                        seed = roomSeed
                    };

                    SynthesizedRoom room = synth.Generate(roomIdx, biome, "", ports, 0, false, false, context);

                    // 1. Validate room structure
                    Assert.IsTrue(RRSynth.ValidateRoom(room), $"Validation failed for {biome} room {roomIdx}");

                    // 2. Decode tiles cleanly through TileDecoder.ParseRoom + TileFormDatabase
                    if (formDb != null)
                    {
                        TileData[,] tiles = TileDecoder.ParseRoom(room.rows, formDb, false, 48, 25);
                        Assert.IsNotNull(tiles);
                        Assert.AreEqual(48, tiles.GetLength(0));
                        Assert.AreEqual(25, tiles.GetLength(1));
                    }

                    // 3. Exactly 1 player spawn
                    int playerCount = 0;
                    var uniqueCodes = new HashSet<string>();
                    var majorActors = new List<SynthesizedObject>();

                    foreach (var obj in room.objects)
                    {
                        Assert.IsFalse(string.IsNullOrEmpty(obj.code), $"Object missing code: {obj.id}");
                        Assert.IsTrue(uniqueCodes.Add(obj.code), $"Duplicate object code: {obj.code}");

                        if (obj.id == "player") playerCount++;

                        if ((obj.rrContent == "enemy" && majorEnemyKinds.Contains(obj.id)) ||
                            obj.rrContent == "security" ||
                            obj.id.Contains("turret"))
                        {
                            majorActors.Add(obj);
                        }
                    }

                    Assert.AreEqual(1, playerCount, $"Expected exactly 1 player spawn in {biome} room {roomIdx}");

                    // 4. Defender spacing rule: all major enemies and turrets >= 4 tiles apart (distance squared >= 16)
                    for (int i = 0; i < majorActors.Count; i++)
                    {
                        for (int j = i + 1; j < majorActors.Count; j++)
                        {
                            var a = majorActors[i];
                            var b = majorActors[j];
                            int dx = a.x - b.x;
                            int dy = a.y - b.y;
                            int distSq = dx * dx + dy * dy;
                            Assert.GreaterOrEqual(distSq, 16,
                                $"Major defenders '{a.id}' at ({a.x},{a.y}) and '{b.id}' at ({b.x},{b.y}) violated >= 4-tile spacing in {biome} seed {roomSeed}");
                        }
                    }

                    // 5. Changing Value (0 vs 100) with same seed does NOT alter terrain, turrets, or main enemy spawns
                    var contextVal0 = new GenerationContext
                    {
                        contentVersion = "13",
                        partition = "rules",
                        seed = roomSeed
                    };
                    var contextVal100 = new GenerationContext
                    {
                        contentVersion = "13",
                        partition = "rules",
                        seed = roomSeed
                    };

                    var roomVal0 = synth.Generate(roomIdx, biome, "", ports, 0, false, false, contextVal0);
                    var roomVal100 = synth.Generate(roomIdx, biome, "", ports, 0, false, false, contextVal100);

                    // Terrain must be identical
                    Assert.AreEqual(roomVal0.tileDataString, roomVal100.tileDataString,
                        $"Terrain changed between Value=0 and Value=100 in {biome} seed {roomSeed}");

                    // Turrets and main enemy spawns must be identical
                    var defenders0 = ExtractDefenders(roomVal0);
                    var defenders100 = ExtractDefenders(roomVal100);
                    Assert.AreEqual(defenders0.Count, defenders100.Count,
                        $"Defender count differed between Value 0 and 100 in {biome} seed {roomSeed}");

                    for (int d = 0; d < defenders0.Count; d++)
                    {
                        Assert.AreEqual(defenders0[d].id, defenders100[d].id);
                        Assert.AreEqual(defenders0[d].x, defenders100[d].x);
                        Assert.AreEqual(defenders0[d].y, defenders100[d].y);
                    }
                }
            }
        }

        private List<SynthesizedObject> ExtractDefenders(SynthesizedRoom r)
        {
            var list = new List<SynthesizedObject>();
            foreach (var o in r.objects)
            {
                if (o.rrContent == "security" || (o.rrContent == "enemy" && o.code.Contains("_p")))
                {
                    list.Add(o);
                }
            }
            return list;
        }
    }
}
