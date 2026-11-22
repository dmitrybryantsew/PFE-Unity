using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using PFE.Sim.Replication;
using PFE.Sim.State;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Measurement (not contract) tests for the steady-state replication loop: what one host tick and
    /// one client receive cost at realistic room populations, how many bytes leave the host per tick,
    /// and what the allocation pressure looks like. Drives the same transport-neutral wall the
    /// contract tests use — real snapshot serializer, real envelope codec, real receiver — so the
    /// numbers name production code, not stand-ins. Numbers are reported through the test log; the
    /// only assertions are loop-integrity guards (a refused snapshot would silently skew every mean).
    /// </summary>
    [TestFixture]
    public sealed class SimNetcodePerfTests
    {
        private const ulong SessionId = 0x5E5510A11UL;
        private const int ChunkSize = 1024; // production PfeNetConfig default
        private const int TicksPerSecond = 30; // SimClock.CanonicalTicksPerSecond

        private static readonly ulong[] RoomHashes =
        {
            0xAAAA000000000001UL, 0xBBBB000000000001UL, 0xCCCC000000000001UL,
        };

        // ── Payload builders (plausible values; nothing reads them on this path) ──

        private static SimUnitState Unit(ulong hash, float px)
        {
            return new SimUnitState
            {
                EntityHash = hash,
                Generation = 1,
                LifecycleSequence = 1,
                ControllerSlot = SimUnitState.HostAiControllerSlot,
                DefinitionId = 7,
                Px = px,
                Py = 640f,
                FacingDirection = 1,
                Vx = 1.5f,
                IsGrounded = true,
                OverhangLeft = 0.25f,
                OverhangRight = 0.75f,
                Health = 82f,
                MaxHealth = 100f,
                HasRuntimeHealth = true,
                Faction = 2,
                PoseId = 3,
                PoseFrame = 12,
                PoseSequence = 4,
                WeaponIdHash = 0xCAFE000000000001UL,
                AimAngle = 15f,
            };
        }

        private static SimMotorState Motor(ulong hash, float px)
        {
            return new SimMotorState
            {
                EntityHash = hash,
                Px = px,
                Py = 640f,
                Vx = 2f,
                Vy = -0.5f,
                IsGrounded = true,
                FacingDirection = 1,
                GravityScale = 1f,
            };
        }

        private static SimProjectileState Projectile(ulong hash, int index)
        {
            return new SimProjectileState
            {
                EntityHash = hash,
                LauncherHash = 0x77AA000000000001UL,
                Generation = 1,
                Sequence = 9u,
                DefinitionId = 3,
                Kind = 1,
                Px = 100f + index * 12f,
                Py = 300f,
                Vx = 24f,
                Vy = -2f,
                LifetimeRemaining = 1.25f,
                RemainingDamage = 30f,
            };
        }

        private static SimDroppedItem Drop(ulong hash, int index)
        {
            return new SimDroppedItem
            {
                EntityHash = hash,
                ItemIdHash = 0x1700000000000001UL + (ulong)index,
                IsWeapon = false,
                Quantity = 1 + index % 5,
                Variant = 0,
                Condition = 0.85f,
                Px = 48f + index * 20f,
                Py = 660f,
            };
        }

        private static SimRoomDelta Room(int roomIndex, int unitsPerRoom, int projectiles, int dropped)
        {
            var units = new SimUnitState[unitsPerRoom];
            var motors = new SimMotorState[unitsPerRoom];
            for (int i = 0; i < unitsPerRoom; i++)
            {
                ulong hash = RoomHashes[roomIndex] ^ (0x1000UL + (ulong)i);
                units[i] = Unit(hash, 32f + i * 24f);
                motors[i] = Motor(hash, 32f + i * 24f);
            }

            var projectileSet = new SimProjectileState[projectiles];
            for (int i = 0; i < projectiles; i++)
            {
                projectileSet[i] = Projectile(RoomHashes[roomIndex] ^ (0x9000UL + (ulong)i), i);
            }

            var drops = new SimDroppedItem[dropped];
            for (int i = 0; i < dropped; i++)
            {
                drops[i] = Drop(RoomHashes[roomIndex] ^ (0xA000UL + (ulong)i), i);
            }

            return new SimRoomDelta
            {
                RoomHash = RoomHashes[roomIndex],
                LifecycleSequence = 1,
                Units = units,
                Motors = motors,
                Projectiles = projectileSet,
                DroppedItems = drops,
                RemovedEntityHashes = Array.Empty<ulong>(),
                LootedObjects = new[] { new SimObjectState { ObjectIndex = 2, LootState = 1 } },
                DoorStates = new[]
                {
                    new SimDoorState { ObjectIndex = 0, IsOpen = false },
                    new SimDoorState { ObjectIndex = 1, IsOpen = true },
                },
            };
        }

        private static SimSnapshot Source(params SimRoomDelta[] rooms)
        {
            return new SimSnapshot
            {
                ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion,
                TickIndex = 1L,
                SnapshotSequence = 1u,
                WorldRevision = 7L,
                BaselineHash = 0UL,
                Rooms = rooms ?? Array.Empty<SimRoomDelta>(),
                TileMutations = Array.Empty<SimTileMutation>(),
            };
        }

        private static SimPeerInterest InterestAll(int roomCount)
        {
            var adjacent = new List<ulong>(roomCount);
            for (int i = 0; i < roomCount; i++) adjacent.Add(RoomHashes[i]);
            return new SimPeerInterest(RoomHashes[0], adjacent);
        }

        // ── Harness pieces mirroring SimReplicationLoopTests ──────────────────────

        private sealed class FakeSource : ISimReplicationSnapshotSource
        {
            private readonly SimTileJournal _journal = new SimTileJournal();

            public SimSnapshot Snapshot;
            public SimTileJournal Journal => _journal;
            public SimSnapshot Current => Snapshot;
        }

        private sealed class Bench
        {
            public readonly SimReplicationOutbox ToClient = new SimReplicationOutbox();
            public readonly SimReplicationOutbox ToHost = new SimReplicationOutbox();
            public readonly FakeSource Source = new FakeSource();
            public readonly SimReplicationHost Host;
            public readonly List<SimReplicationClient> Clients = new List<SimReplicationClient>();

            public Bench(SimSnapshot snapshot, int peers)
            {
                Source.Snapshot = snapshot;
                Host = new SimReplicationHost(SessionId, Source, ToClient, ChunkSize);
                for (ulong nonce = 1; nonce <= (ulong)peers; nonce++)
                {
                    var client = new SimReplicationClient(nonce, ToHost);
                    client.Join(InterestAll(snapshot.Rooms.Length));
                    Clients.Add(client);
                }
            }

            public void PumpUntilQuiet()
            {
                for (int round = 0; round < 64; round++)
                {
                    bool any = ToHost.Count > 0 || ToClient.Count > 0;
                    DeliverToHost();
                    DeliverToClient();
                    if (!any) return;
                }
                Assert.Fail("The replication loop did not converge during handshake.");
            }

            public void DeliverToHost()
            {
                SimReplicationEnvelope[] items = ToHost.ToArray();
                ToHost.Clear();
                for (int i = 0; i < items.Length; i++)
                {
                    byte[] bytes = SimReplicationEnvelopeSerializer.Serialize(items[i]);
                    Host.Receive(SimReplicationEnvelopeSerializer.Deserialize(bytes));
                }
            }

            public void DeliverToClient()
            {
                SimReplicationEnvelope[] items = ToClient.ToArray();
                ToClient.Clear();
                for (int i = 0; i < items.Length; i++)
                {
                    SimReplicationEnvelope envelope = items[i];
                    int index = (int)envelope.PeerId - 1;
                    Assert.That(index, Is.InRange(0, Clients.Count - 1),
                        "Every host envelope must have a client to land on.");
                    Clients[index].Receive(SimReplicationEnvelopeSerializer.Deserialize(
                        SimReplicationEnvelopeSerializer.Serialize(envelope)));
                }
            }
        }

        private sealed class Accum
        {
            public long Ticks;
            public double TotalMs;
            public double MaxMs;
            public long Bytes;

            public void Add(double ms, long bytes = 0)
            {
                Ticks++;
                TotalMs += ms;
                if (ms > MaxMs) MaxMs = ms;
                Bytes += bytes;
            }

            public double Avg => Ticks == 0 ? 0.0 : TotalMs / Ticks;

            public override string ToString() => $"avg={Avg:F3}ms max={MaxMs:F3}ms n={Ticks}";
        }

        // ── The benchmark ─────────────────────────────────────────────────────────

        [Test]
        [Category("Performance")]
        public void SteadyState_LoopBenchmark()
        {
            Run("1 room x 30 units, 1 peer", rooms: 1, unitsPerRoom: 30, projectiles: 8,
                dropped: 12, peers: 1);
            Run("3 rooms x 30 units, 4 peers", rooms: 3, unitsPerRoom: 30, projectiles: 8,
                dropped: 12, peers: 4);
        }

        private static void Run(string label, int rooms, int unitsPerRoom, int projectiles,
            int dropped, int peers)
        {
            var roomDeltas = new SimRoomDelta[rooms];
            for (int i = 0; i < rooms; i++)
            {
                roomDeltas[i] = Room(i, unitsPerRoom, projectiles, dropped);
            }

            var bench = new Bench(Source(roomDeltas), peers);
            bench.PumpUntilQuiet();
            int acceptedAfterHandshake = bench.Clients[0].Receiver.AcceptedSnapshotCount;
            Assert.Greater(acceptedAfterHandshake, 0, "Handshake must deliver at least one baseline.");

            const int warmup = 30;
            const int measured = 270;
            var hostTick = new Accum();
            var codecEncode = new Accum();
            var codecDecode = new Accum();
            var clientReceive = new Accum();
            var secondDeserialize = new Accum();
            var ackRoundTrip = new Accum();

            long gcBeforeBytes = GC.GetTotalMemory(true);
            int gcBefore0 = GC.CollectionCount(0);

            var sw = new Stopwatch();
            for (int iter = 0; iter < warmup + measured; iter++)
            {
                // Move something so no phase can exploit constant input.
                SimRoomDelta first = roomDeltas[0];
                first.Units[0].Px += 1f;
                first.Motors[0].Px += 1f;
                roomDeltas[0] = first;

                bool count = iter >= warmup;

                sw.Restart();
                bench.Host.Tick();
                sw.Stop();
                if (count) hostTick.Add(sw.Elapsed.TotalMilliseconds);

                // Host -> client: production encode (PfeReplicationCodec is this serializer verbatim),
                // then decode, then client receive (first deserialize happens inside).
                SimReplicationEnvelope[] outbound = bench.ToClient.ToArray();
                bench.ToClient.Clear();
                Assert.AreEqual(peers, outbound.Length,
                    "One snapshot envelope per peer per tick was expected.");
                foreach (SimReplicationEnvelope envelope in outbound)
                {
                    sw.Restart();
                    byte[] bytes = SimReplicationEnvelopeSerializer.Serialize(envelope);
                    sw.Stop();
                    if (count) codecEncode.Add(sw.Elapsed.TotalMilliseconds, bytes.LongLength);

                    sw.Restart();
                    SimReplicationEnvelope decoded = SimReplicationEnvelopeSerializer.Deserialize(bytes);
                    sw.Stop();
                    if (count) codecDecode.Add(sw.Elapsed.TotalMilliseconds);

                    SimReplicationClient client = bench.Clients[(int)decoded.PeerId - 1];
                    sw.Restart();
                    client.Receive(decoded);
                    sw.Stop();
                    if (count) clientReceive.Add(sw.Elapsed.TotalMilliseconds);

                    // The session layer deserializes the same payload a second time for the applier
                    // (PfeReplicationSession.OnClientBroadcast). Measured separately because it is
                    // pure overhead on the live path.
                    if (decoded.Kind == SimReplicationMessageKind.Snapshot)
                    {
                        sw.Restart();
                        SimSnapshotSerializer.Deserialize(decoded.Payload);
                        sw.Stop();
                        if (count) secondDeserialize.Add(sw.Elapsed.TotalMilliseconds);
                    }
                }

                // Client -> host: acks ride the wire like production (reliable channel, per snapshot).
                sw.Restart();
                bench.DeliverToHost();
                sw.Stop();
                if (count) ackRoundTrip.Add(sw.Elapsed.TotalMilliseconds);
            }

            long gcDeltaBytes = GC.GetTotalMemory(true) - gcBeforeBytes;
            int gc0Collections = GC.CollectionCount(0) - gcBefore0;

            SimReplicationClient probe = bench.Clients[0];
            Assert.AreEqual(acceptedAfterHandshake + warmup + measured,
                probe.Receiver.AcceptedSnapshotCount,
                "Every measured snapshot must be accepted, or the timings measured refusals.");

            double perTickAllPeersBytes = codecEncode.Bytes / (double)measured;
            double perPeerBytes = perTickAllPeersBytes / peers;
            double hostMsPerTickPerPeer = hostTick.Avg / peers;
            double clientMsPerSnapshot = codecDecode.Avg + clientReceive.Avg + secondDeserialize.Avg;
            double bandwidthPerPeer = perPeerBytes * TicksPerSecond;

            // v10 wire-size expectation, computed from the fixture so a packing regression is caught
            // by name at the perf seam too: unit 82 B + motor 64 B (v10 record widths; see
            // SimSnapshotRoundTripTests.V10RecordWireSizes_ArePinned), projectile 69 B, drop 37 B,
            // one looted object 8 B, two doors 10 B, room hash+lifecycle 12 B, seven count prefixes
            // 28 B; snapshot header 44 B (magic 4 + version 4 + tick 8 + seq 4 + revision 8 +
            // baseline 8 + room count 4 + journal count 4), envelope framing 29 B.
            int expectedRoomBytes = unitsPerRoom * (82 + 64) + projectiles * 69 + dropped * 37
                                    + 8 + 10 + 12 + 28;
            int expectedPerPeerBytes = expectedRoomBytes * rooms + 44 + 29;
            Assert.AreEqual(expectedPerPeerBytes, (int)perPeerBytes,
                "The v10 envelope size changed. Either a record codec regressed (see " +
                "SimSnapshotRoundTripTests.V10RecordWireSizes_ArePinned) or the fixture grew a " +
                "record the expectation above must name.");

            UnityEngine.Debug.Log(
                $"[NETPERF] {label}\n" +
                $"[NETPERF]   snapshot envelope: {perPeerBytes / 1024.0:F1} KB/peer/tick " +
                $"-> {bandwidthPerPeer / 1024.0:F0} KB/s/peer at {TicksPerSecond} Hz " +
                $"({perPeerBytes * TicksPerSecond * 8.0 / 1e6:F2} Mbit/s)\n" +
                $"[NETPERF]   host:   Tick(all {peers} peers) {hostTick} | per-peer share {hostMsPerTickPerPeer:F3} ms/tick\n" +
                $"[NETPERF]   codec:  encode {codecEncode} ({codecEncode.Bytes / (double)measured / 1024.0:F1} KB total/tick) | decode {codecDecode}\n" +
                $"[NETPERF]   client: Receive {clientReceive} + session-layer 2nd deserialize {secondDeserialize}\n" +
                $"[NETPERF]   client total: {clientMsPerSnapshot:F3} ms/snapshot\n" +
                $"[NETPERF]   ack round trip (codec + host.Receive, all peers): {ackRoundTrip}\n" +
                $"[NETPERF]   GC: {gcDeltaBytes / (double)(measured):F0} bytes/iter delta, " +
                $"gen0 collections {gc0Collections} over {measured} iters");
        }
    }
}
