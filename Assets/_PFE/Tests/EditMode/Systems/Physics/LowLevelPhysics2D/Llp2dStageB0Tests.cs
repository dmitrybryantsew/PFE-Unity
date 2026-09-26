using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Physics.LowLevelPhysics2D
{
    /// <summary>
    /// Stage B0 remainder: the two items Stage A explicitly did **not** answer, both listed in
    /// <c>docs/Roadmap/LLP2D_STAGE_A_RESULTS.md</c> §4.
    ///
    /// <para><b>(a) Real generated rooms.</b> Q3 measured 0.148 MB/room against a <i>synthetic</i>
    /// 48×25 room with six features. A generated room has more exposed surface, so that figure could
    /// only ever be a lower bound — and L1 ("one world per active room set") was decided on it. This
    /// fixture re-measures against every shipped <see cref="RoomTemplate"/>, through the
    /// <b>production</b> path (<see cref="RoomGenerator.GenerateRoom"/> →
    /// <see cref="PhysicsWorldService.BuildRoomGeometry"/>), not the throwaway spike builder.</para>
    ///
    /// <para><b>(b) Cross-process determinism.</b> Q5's caveat, stated plainly: both of its runs
    /// happened in one process/app-domain, which proves reproducibility across independently built
    /// worlds but says nothing about a restart. <see cref="Llp2dCrossProcessDeterminismTests"/>
    /// closes that by comparing against a hash recorded by an <i>earlier process</i>.</para>
    /// </summary>
    [TestFixture]
    public sealed class Llp2dRealRoomCostTests
    {
        private const string TileFormDatabasePath = "Assets/_PFE/Data/TileFormDatabase.asset";
        private const string BaseRoomsResourcesPath = "Rooms/Base";

        /// <summary>AS3 ships 557 rooms; Stage A extrapolated to this number.</summary>
        private const int ShippedRooms = 557;

        /// <summary>Stage A's synthetic-room figure, kept so the re-measurement has a baseline.</summary>
        private const double StageASyntheticMbPerRoom = 0.148;

        /// <summary>One room's measurement. A struct so the report is a plain list.</summary>
        private readonly struct RoomCost
        {
            public RoomCost(string id, int width, int height, int bodies, int shapes, long bytes, long millis)
            {
                Id = id;
                Width = width;
                Height = height;
                Bodies = bodies;
                Shapes = shapes;
                Bytes = bytes;
                Millis = millis;
            }

            public string Id { get; }
            public int Width { get; }
            public int Height { get; }
            public int Bodies { get; }
            public int Shapes { get; }
            public long Bytes { get; }
            public long Millis { get; }
        }

        private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

        [Test]
        public void RealRooms_ChainCost_MeasuredOnEveryShippedTemplate()
        {
            TileFormDatabase formDb =
                AssetDatabase.LoadAssetAtPath<TileFormDatabase>(TileFormDatabasePath);
            Assert.IsNotNull(formDb,
                $"Expected the tile form database at '{TileFormDatabasePath}'. " +
                "Without it ParseTiles cannot run and the measurement would be of an empty room.");

            RoomTemplate[] templates = Resources.LoadAll<RoomTemplate>(BaseRoomsResourcesPath);
            Assert.GreaterOrEqual(templates.Length, 10,
                $"Found only {templates.Length} room templates under Resources/'{BaseRoomsResourcesPath}'. " +
                "A near-empty collection would make the extrapolation meaningless.");

            var generator = new RoomGenerator(formDb);

            var rows = new List<RoomCost>(templates.Length);
            foreach (RoomTemplate template in templates)
            {
                RoomInstance room = generator.GenerateRoom(template, Vector3Int.zero);
                Assert.IsNotNull(room, $"GenerateRoom returned null for template '{template.id}'.");

                // One service (== one world) per room, disposed immediately. That keeps each
                // memoryUsed delta a clean per-room figure, and respects PhysicsConstants.MaxWorlds.
                var service = new PhysicsWorldService();
                try
                {
                    long before = service.World.counters.memoryUsed;

                    // Fully qualified on purpose: `using System.Diagnostics;` would make the bare name
                    // `Debug` ambiguous with UnityEngine.Debug (CS0104) for the whole file.
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    service.BuildRoomGeometry(room);
                    stopwatch.Stop();

                    long delta = service.World.counters.memoryUsed - before;

                    rows.Add(new RoomCost(
                        template.id, room.width, room.height,
                        service.World.counters.bodyCount, service.World.counters.shapeCount,
                        delta, stopwatch.ElapsedMilliseconds));
                }
                finally
                {
                    service.Dispose();
                }
            }

            long totalBytes = 0;
            long maxBytes = 0;
            string maxId = "";
            int totalShapes = 0;
            long totalMillis = 0;

            var sb = new StringBuilder();
            sb.Append("[LLP2D B0] real-room chain cost — ").Append(rows.Count)
              .Append(" shipped templates from Resources/").Append(BaseRoomsResourcesPath).Append('\n');

            foreach (RoomCost row in rows)
            {
                totalBytes += row.Bytes;
                totalShapes += row.Shapes;
                totalMillis += row.Millis;
                if (row.Bytes > maxBytes)
                {
                    maxBytes = row.Bytes;
                    maxId = row.Id;
                }

                sb.Append("  ").Append(row.Id.PadRight(16))
                  .Append(row.Width).Append('x').Append(row.Height)
                  .Append(" bodies=").Append(row.Bodies)
                  .Append(" shapes=").Append(row.Shapes)
                  .Append(" buildMs=").Append(row.Millis)
                  .Append(" deltaBytes=").Append(row.Bytes)
                  .Append(" (").Append(Mb(row.Bytes).ToString("F4")).Append(" MB)\n");
            }

            double meanMb = Mb(totalBytes) / rows.Count;
            double maxMb = Mb(maxBytes);
            double allRoomsMb = meanMb * ShippedRooms;
            double ratio = meanMb / StageASyntheticMbPerRoom;

            sb.Append("  mean ").Append(meanMb.ToString("F4")).Append(" MB/room")
              .Append("; max ").Append(maxMb.ToString("F4")).Append(" MB (").Append(maxId).Append(')')
              .Append("; totalShapes ").Append(totalShapes)
              .Append("; totalBuildMs ").Append(totalMillis).Append('\n')
              .Append("  x").Append(ShippedRooms).Append(" rooms = ").Append(allRoomsMb.ToString("F1"))
              .Append(" MB   (Stage A synthetic: ").Append(StageASyntheticMbPerRoom.ToString("F3"))
              .Append(" MB/room -> real/synthetic = ").Append(ratio.ToString("F2")).Append("x)");

            Debug.Log(sb.ToString());

            // ── What is actually asserted ────────────────────────────────────────────────────────
            // The measurement's *validity*, not the guide's prediction about its direction. §4 says a
            // real room "will cost more" than the synthetic one; whether that held is reported above
            // and recorded in the write-up. Asserting the direction would turn a finding into a
            // failure, which is the opposite of what a measurement is for.
            Assert.Greater(totalShapes, 0, "No chain shapes were produced for any real room.");
            Assert.Greater(meanMb, 0.0, "Mean per-room cost was zero — the counters are not being read.");

            Assert.Less(allRoomsMb, 2048.0,
                $"All {ShippedRooms} shipped rooms resident at once would need " +
                $"{allRoomsMb:F0} MB, which exceeds the 2 GB bound Stage A used. L1 (" +
                "\"one world per active room set\") assumed ~82.5 MB; a figure this far above it means " +
                "streaming must evict far more aggressively than the design assumed.");

            foreach (RoomCost row in rows)
            {
                Assert.Greater(row.Bytes, 0,
                    $"Room '{row.Id}' produced no geometry at all — the build silently did nothing.");
                Assert.Less(Mb(row.Bytes), 1.0,
                    $"Room '{row.Id}' cost {Mb(row.Bytes):F3} MB, an order of magnitude above the rest. " +
                    "Either the geometry is wrong or the counters are being read at the wrong moment.");
            }
        }
    }

    /// <summary>
    /// Closes the caveat Stage A recorded against Q5: <i>"both runs happen in the same
    /// process/app-domain… it does not prove cross-process or cross-machine determinism. Before a
    /// LowLevel world enters a golden trace, this needs a restart-and-compare test."</i>
    ///
    /// <para><b>How a restart is obtained.</b> Every Unity test run is a new process. So the hash is
    /// recorded to a file on the first run and compared on the next — the comparison is between two
    /// processes, which is exactly what was missing. The file lives under <c>Library/</c>
    /// (git-ignored, machine-local, survives restarts), so this proves <b>restart</b>
    /// reproducibility. Promoting the file into the repo would extend it to cross-machine; that is a
    /// deliberate later step, because a machine difference should fail loudly rather than be
    /// committed away.</para>
    ///
    /// <para><b>A RECORDED pass is not a verified pass.</b> The first run cannot compare, so it
    /// records, logs a warning, and passes. Only the second and later runs actually verify.</para>
    /// </summary>
    [TestFixture]
    public sealed class Llp2dCrossProcessDeterminismTests
    {
        private const int Ticks = 1000;
        private const string GoldenFileName = "Llp2dDeterminism.golden.txt";

        private static float StepSeconds => 1f / Llp2d.SimTicksPerSecond;

        /// <summary>
        /// <c>Application.dataPath</c> is <c>&lt;project&gt;/Assets</c>, so this resolves to
        /// <c>&lt;project&gt;/Library/…</c>.
        /// </summary>
        private static string GoldenPath => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Library", GoldenFileName));

        [Test]
        public void Determinism_MatchesTheHashRecordedByAPreviousProcess()
        {
            uint hash = Llp2d.RunDeterminismProbe(Ticks, seedOffset: 0f, stepSeconds: StepSeconds);
            string actual = hash.ToString("X8");

            if (!File.Exists(GoldenPath))
            {
                string directory = Path.GetDirectoryName(GoldenPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(GoldenPath, actual + "\n");

                Debug.LogWarning(
                    $"[LLP2D B0] determinism golden RECORDED, not verified: 0x{actual} ({Ticks} ticks) " +
                    $"-> {GoldenPath}. A second run in a new process is what proves cross-process " +
                    "reproducibility; this run could not.");
                return;
            }

            string golden = File.ReadAllText(GoldenPath).Trim();

            Assert.AreEqual(golden, actual,
                $"Determinism broke across processes: this run hashed 0x{actual}, the golden recorded " +
                $"by an earlier process is 0x{golden} ({GoldenPath}). A LowLevelPhysics2D world cannot " +
                "enter a golden trace until this is explained — delete the file only to re-record, " +
                "never to make the suite green.");

            Debug.Log(
                $"[LLP2D B0] determinism golden MATCHED across processes: 0x{actual} ({Ticks} ticks).");
        }

        /// <summary>
        /// Guards the golden against being a constant: if a changed initial state hashes the same,
        /// the probe is not measuring the simulation and a match would mean nothing.
        /// </summary>
        [Test]
        public void Determinism_PerturbingTheInitialState_ChangesTheHash()
        {
            uint baseline = Llp2d.RunDeterminismProbe(Ticks, seedOffset: 0f, stepSeconds: StepSeconds);
            uint perturbed = Llp2d.RunDeterminismProbe(Ticks, seedOffset: 0.01f, stepSeconds: StepSeconds);

            Assert.AreNotEqual(baseline, perturbed,
                "A 0.01-unit change to the initial state did not change the hash, so the golden " +
                "comparison above is not evidence of anything.");
        }
    }
}
