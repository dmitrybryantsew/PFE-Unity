using System;
using System.IO;
using NUnit.Framework;
using PFE.Sim.State;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Phase 1.4 acceptance (F-01/F-02/F-06, §8 row 1.4, §11 item 1): a <see cref="SimSnapshot"/>
    /// must survive <c>serialize → deserialize → state hash</c> unchanged, and each corruption the
    /// readiness doc names must be rejected with its own failing assertion.
    ///
    /// <para><b>Why a behavioural test, not a lint.</b> Unlike the record-purity invariant (1.3),
    /// serialization is pure C# with no <c>MonoBehaviour</c> in the path, so it executes in the offline
    /// harness. This is the first Phase-1 guard that actually <i>runs</i> the round-trip rather than
    /// reading source.</para>
    ///
    /// <para><b>The corruptions are the point.</b> §11 item 1 lists them: wrong version, truncated
    /// delta, trailing garbage, bad magic, absurd count. A round-trip test that only proves "good
    /// bytes round-trip" is half the contract — the other half is that <i>bad</i> bytes are rejected
    /// loudly rather than silently fabricated into state. Each corruption below is a separate test so
    /// a regression names exactly which guard broke.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimSnapshotRoundTripTests
    {
        /// <summary>A snapshot exercising every record type and every field width, so a serialization
        /// bug in any one of them moves the hash. Two rooms, a unit, a motor, a projectile, a dropped
        /// item, and a despawn set.</summary>
        private static SimSnapshot BuildRepresentativeSnapshot()
        {
            return new SimSnapshot
            {
                ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion,
                TickIndex = 1420,
                SnapshotSequence = 77,
                WorldRevision = 9001,
                BaselineHash = 0xABCDEF0123456789,
                Rooms = new[]
                {
                    new SimRoomDelta
                    {
                        RoomHash = 0x1111111111111111,
                        LifecycleSequence = 3,
                        Units = new[]
                        {
                            new SimUnitState
                            {
                                EntityHash = 0xAAAA000000000001, Generation = 1, LifecycleSequence = 2,
                                ControllerSlot = 0, DefinitionId = 42,
                                Px = 120.5f, Py = 40.25f, FacingDirection = 1,
                                // Phase 1.5: non-default values so the round-trip actually exercises
                                // the motor-less unit's live motion/gait half (all-zero defaults would
                                // let a codec that skips every new field pass silently).
                                Vx = 3.25f, Vy = -1.75f,
                                IsGrounded = true, DropThroughPlatforms = true,
                                OverhangLeft = 0.25f, OverhangRight = 1f,
                                SupportCarryX = 0.5f, SupportCarryY = -0.125f,
                                Health = 87.5f, MaxHealth = 100f, HasRuntimeHealth = true,
                                IsDead = false, IsDown = false, IsDissolving = false, DissolveProgress = 0f,
                                Faction = 100, Digger = 0, AmbushArmed = false,
                                PoseId = 3, PoseFrame = 12, PoseSequence = 9,
                                // v8 avatar extras: non-default weapon + aim so a codec that skips
                                // the trailing fields cannot pass the round-trip silently.
                                WeaponIdHash = 0xEEEE00000000000F, AimAngle = 33.5f,
                            },
                        },
                        Motors = new[]
                        {
                            new SimMotorState
                            {
                                EntityHash = 0xAAAA000000000001,
                                Px = 120.5f, Py = 40.25f, Vx = 2.5f, Vy = -1.25f,
                                IsGrounded = true, IsOnPlatform = false, IsInWater = false,
                                IsFullySubmerged = false, IsOnLadder = false, IsCrouching = true,
                                HitCeiling = false, WallLeft = false, WallRight = true,
                                FacingDirection = 1, PendingVerticalImpulse = 0.5f, DashTimer = 0.2f,
                                DashVelocityX = 6f, DashVelocityY = 0f, PlatformDropTimer = 0f,
                                ActiveLadderDirection = 0, ActiveLadderSnapX = 0f, GravityScale = 1f,
                                SupportCarryX = 0.1f, SupportCarryY = -0.2f,
                            },
                        },
                        Projectiles = new[]
                        {
                            new SimProjectileState
                            {
                                EntityHash = 0xBBBB0000000000AA, LauncherHash = 0xAAAA000000000001,
                                Generation = 0, Sequence = 5, DefinitionId = 7,
                                Px = 200f, Py = 64f, Vx = 480f, Vy = 0f,
                                AccelerationX = 0f, AccelerationY = -600f, HomingStrength = 0f,
                                TraveledPixels = 320.5f, LifetimeRemaining = 2.5f, RemainingDamage = 25f,
                                HasDetonated = false, IsImpacting = false,
                            },
                            // A placed mine: the same record shape, a different kind. Two transient
                            // kinds in one array is what makes the discriminator's round-trip and its
                            // place in the state hash observable rather than assumed.
                            new SimProjectileState
                            {
                                EntityHash = 0xBBBB0000000000BB, LauncherHash = 0xAAAA000000000001,
                                Generation = 2, Sequence = 9, DefinitionId = 11,
                                Kind = SimTransientKind.Mine,
                                Px = 44f, Py = 40f, Vx = 0f, Vy = 0f,
                                AccelerationX = 0f, AccelerationY = 0f, HomingStrength = 0f,
                                TraveledPixels = 0f, LifetimeRemaining = 0f, RemainingDamage = 60f,
                                HasDetonated = false, IsImpacting = false,
                            },
                        },
                        // v4 §10.2 gap 2: two stacks with identical content AND identical position,
                        // distinguishable only by the host-assigned EntityHash. Before v4 these two
                        // records were the same record, which is exactly the pickup/despawn hole.
                        DroppedItems = new[]
                        {
                            new SimDroppedItem
                            {
                                EntityHash = 0xDDDD000000000001,
                                ItemIdHash = 0xCCCC0000000000FF, IsWeapon = true, Quantity = 1,
                                Variant = 2, Condition = 0.85f, Px = 88f, Py = 40f,
                            },
                            new SimDroppedItem
                            {
                                EntityHash = 0xDDDD000000000002,
                                ItemIdHash = 0xCCCC0000000000FF, IsWeapon = true, Quantity = 1,
                                Variant = 2, Condition = 0.85f, Px = 88f, Py = 40f,
                            },
                        },
                        RemovedEntityHashes = new ulong[] { 0xDEAD000000000001, 0xDEAD000000000002 },
                        // v9 doors: every door box in the room with its current state, both
                        // directions — a closed door is state, not the absence of state.
                        DoorStates = new[]
                        {
                            new SimDoorState { ObjectIndex = 4, IsOpen = true },
                            new SimDoorState { ObjectIndex = 9, IsOpen = false },
                        },
                    },
                    new SimRoomDelta
                    {
                        RoomHash = 0x2222222222222222,
                        LifecycleSequence = 1,
                        Units = new SimUnitState[0],
                        Motors = new SimMotorState[0],
                        Projectiles = new SimProjectileState[0],
                        DroppedItems = new SimDroppedItem[0],
                        RemovedEntityHashes = new ulong[0],
                    },
                },
                // v4 §10.2 gap 1: the reliable journal lives on the snapshot, after the room deltas.
                // Every field is non-default so a codec that skips the journal cannot pass on zeroes.
                TileMutations = new[]
                {
                    new SimTileMutation
                    {
                        RoomHash = 0x1111111111111111, WorldRevision = 9001, EventSequence = 1,
                        TileX = 12, TileY = 7, PhysicsType = 3, HitPoints = 0,
                    },
                    new SimTileMutation
                    {
                        RoomHash = 0x2222222222222222, WorldRevision = 9001, EventSequence = 2,
                        TileX = -4, TileY = 19, PhysicsType = 1, HitPoints = 42,
                    },
                },
            };
        }

        // ── The acceptance: round-trip preserves the state hash ───────────────

        [Test]
        public void RoundTrip_PreservesStateHash()
        {
            // v10 note: the fixture deliberately uses wire-representable values for the two quantized
            // fields (DissolveProgress 0, AimAngle 33.5 = 2144/64), so the state hash stays exactly
            // invariant here; the lossy fields themselves are pinned by tolerance in
            // RoundTrip_PreservesValueEquality. Both peers deserialize the same bytes, so the hash
            // both would compute on the wire form agrees — determinism is what the codec must keep.
            SimSnapshot snapshot = BuildRepresentativeSnapshot();
            ulong before = snapshot.ComputeStateHash();

            byte[] payload = SimSnapshotSerializer.Serialize(in snapshot);
            SimSnapshot restored = SimSnapshotSerializer.Deserialize(payload);
            ulong after = restored.ComputeStateHash();

            Assert.AreEqual(before, after,
                "serialize → deserialize → ComputeStateHash must be invariant (§8 row 1.4) for " +
                "wire-representable values. The codec lost or altered state in the round-trip.");
        }

        [Test]
        public void RoundTrip_PreservesValueEquality()
        {
            SimSnapshot snapshot = BuildRepresentativeSnapshot();
            SimSnapshot restored = SimSnapshotSerializer.Deserialize(SimSnapshotSerializer.Serialize(in snapshot));

            // v10: identity/key/exact-width fields must still compare field-for-field — a hash
            // collision would pass AreEqual on the hash while losing a field — while the two
            // quantized fields (DissolveProgress as a byte, AimAngle as 1/64-degree fixed point)
            // are asserted with their wire tolerances. The struct fields are unchanged; the codec
            // is what narrowed.
            Assert.AreEqual(snapshot.TickIndex, restored.TickIndex);
            Assert.AreEqual(snapshot.SnapshotSequence, restored.SnapshotSequence);
            Assert.AreEqual(snapshot.WorldRevision, restored.WorldRevision);
            Assert.AreEqual(snapshot.BaselineHash, restored.BaselineHash);
            Assert.AreEqual(snapshot.Rooms.Length, restored.Rooms.Length);
            AssertUnitStateMatches(in snapshot.Rooms[0].Units[0], in restored.Rooms[0].Units[0]);
            Assert.IsTrue(snapshot.Rooms[0].Motors[0].Equals(restored.Rooms[0].Motors[0]),
                "The motor record is fixed-width exact in v10 and must compare field-for-field.");
            CollectionAssert.AreEqual(snapshot.Rooms[0].Projectiles, restored.Rooms[0].Projectiles,
                "Projectile records are unchanged by v10 and must round-trip exactly.");
            CollectionAssert.AreEqual(snapshot.Rooms[0].DroppedItems, restored.Rooms[0].DroppedItems);
            CollectionAssert.AreEqual(snapshot.Rooms[0].DoorStates, restored.Rooms[0].DoorStates);
            CollectionAssert.AreEqual(snapshot.TileMutations, restored.TileMutations);
        }

        /// <summary>The v10 unit-record assertion: every identity/key/exact-width field bit-for-bit,
        /// the two quantized fields within their documented wire tolerances (1/510 for the byte
        /// dissolve, 1/128 degree for the fixed-point aim).</summary>
        private static void AssertUnitStateMatches(in SimUnitState expected, in SimUnitState actual)
        {
            Assert.AreEqual(expected.EntityHash, actual.EntityHash);
            Assert.AreEqual(expected.Generation, actual.Generation);
            Assert.AreEqual(expected.LifecycleSequence, actual.LifecycleSequence);
            Assert.AreEqual(expected.ControllerSlot, actual.ControllerSlot);
            Assert.AreEqual(expected.DefinitionId, actual.DefinitionId);
            Assert.AreEqual(expected.FacingDirection, actual.FacingDirection);
            Assert.AreEqual(expected.Px, actual.Px);
            Assert.AreEqual(expected.Py, actual.Py);
            Assert.AreEqual(expected.Vx, actual.Vx);
            Assert.AreEqual(expected.Vy, actual.Vy);
            Assert.AreEqual(expected.IsGrounded, actual.IsGrounded);
            Assert.AreEqual(expected.DropThroughPlatforms, actual.DropThroughPlatforms);
            Assert.AreEqual(expected.OverhangLeft, actual.OverhangLeft);
            Assert.AreEqual(expected.OverhangRight, actual.OverhangRight);
            Assert.AreEqual(expected.SupportCarryX, actual.SupportCarryX);
            Assert.AreEqual(expected.SupportCarryY, actual.SupportCarryY);
            Assert.AreEqual(expected.Health, actual.Health);
            Assert.AreEqual(expected.MaxHealth, actual.MaxHealth);
            Assert.AreEqual(expected.HasRuntimeHealth, actual.HasRuntimeHealth);
            Assert.AreEqual(expected.IsDead, actual.IsDead);
            Assert.AreEqual(expected.IsDown, actual.IsDown);
            Assert.AreEqual(expected.IsDissolving, actual.IsDissolving);
            Assert.AreEqual(expected.Faction, actual.Faction);
            Assert.AreEqual(expected.Digger, actual.Digger);
            Assert.AreEqual(expected.AmbushArmed, actual.AmbushArmed);
            Assert.AreEqual(expected.PoseId, actual.PoseId);
            Assert.AreEqual(expected.PoseFrame, actual.PoseFrame);
            Assert.AreEqual(expected.PoseSequence, actual.PoseSequence);
            Assert.AreEqual(expected.WeaponIdHash, actual.WeaponIdHash);
            Assert.AreEqual(expected.DissolveProgress, actual.DissolveProgress,
                1.0 / 510.0 + 1e-6, "DissolveProgress is a byte on the wire (1/510 tolerance).");
            Assert.AreEqual(expected.AimAngle, actual.AimAngle,
                1.0 / 128.0 + 1e-6, "AimAngle is 1/64-degree fixed point (1/128 degree tolerance).");
        }

        [Test]
        public void RoundTrip_IsByteDeterministic()
        {
            // The same snapshot must serialize to the same bytes twice — the property that lets a host
            // and a client agree a state hash without sending the bytes.
            SimSnapshot snapshot = BuildRepresentativeSnapshot();
            byte[] a = SimSnapshotSerializer.Serialize(in snapshot);
            byte[] b = SimSnapshotSerializer.Serialize(in snapshot);
            CollectionAssert.AreEqual(a, b, "Serialization must be deterministic across calls.");
        }

        [Test]
        public void StateHash_DistinguishesFieldChanges()
        {
            // Absent control: the hash must actually see a field. Flip one field and confirm the hash
            // moves — a hash that ignored a field would let a corrupted snapshot pass.
            SimSnapshot snapshot = BuildRepresentativeSnapshot();
            ulong baseline = snapshot.ComputeStateHash();

            SimSnapshot changed = snapshot;
            changed.Rooms[0].Units[0].Health = snapshot.Rooms[0].Units[0].Health + 1f;
            Assert.AreNotEqual(baseline, changed.ComputeStateHash(),
                "Changing a unit's Health must change the state hash. The hash is blind to a field.");

            SimSnapshot changedSeq = snapshot;
            changedSeq.SnapshotSequence = snapshot.SnapshotSequence + 1;
            Assert.AreNotEqual(baseline, changedSeq.ComputeStateHash(),
                "Changing SnapshotSequence must change the state hash.");
        }

        /// <summary>
        /// Phase 1.5 (F-02): the eight new motor-less-unit fields must survive the wire AND be mixed
        /// into <see cref="SimSnapshot.ComputeStateHash"/>. Adding a field to a record without adding
        /// it to both <c>WriteTo</c>/<c>ReadFrom</c> and <c>HashUnit</c> is the classic silent drift:
        /// the round-trip passes on defaults and the state hash cannot see a real divergence.
        /// Each field gets its own absent control so a failure names the field.
        /// </summary>
        [Test]
        public void Phase15UnitMotionFields_AreSerializedAndHashed()
        {
            SimSnapshot source = BuildRepresentativeSnapshot();
            SimSnapshot restored = SimSnapshotSerializer.Deserialize(
                SimSnapshotSerializer.Serialize(in source));

            AssertUnitStateMatches(in source.Rooms[0].Units[0], in restored.Rooms[0].Units[0]);

            var probes = new (string Name, System.Action<SimSnapshot> Mutate)[]
            {
                ("Vx",                  s => s.Rooms[0].Units[0].Vx += 1f),
                ("Vy",                  s => s.Rooms[0].Units[0].Vy += 1f),
                ("IsGrounded",          s => s.Rooms[0].Units[0].IsGrounded = false),
                ("DropThroughPlatforms",s => s.Rooms[0].Units[0].DropThroughPlatforms = false),
                ("OverhangLeft",        s => s.Rooms[0].Units[0].OverhangLeft += 1f),
                ("OverhangRight",       s => s.Rooms[0].Units[0].OverhangRight += 1f),
                ("SupportCarryX",       s => s.Rooms[0].Units[0].SupportCarryX += 1f),
                ("SupportCarryY",       s => s.Rooms[0].Units[0].SupportCarryY += 1f),
            };

            foreach ((string name, System.Action<SimSnapshot> mutate) in probes)
            {
                SimSnapshot fresh = BuildRepresentativeSnapshot();
                ulong baseline = fresh.ComputeStateHash();
                mutate(fresh);
                Assert.AreNotEqual(baseline, fresh.ComputeStateHash(),
                    "SimUnitState." + name + " is not mixed into ComputeStateHash. A host/client " +
                    "divergence in this field would be invisible to the state-hash check.");
            }
        }

        /// <summary>
        /// Phase 3.3: <c>SimProjectileState.Kind</c> must be on the wire, in the state hash, and
        /// default to <see cref="SimTransientKind.Projectile"/>.
        ///
        /// <para><b>Why the default is a contract, not a convenience.</b> A producer that forgets to
        /// stamp the kind, or a fixture that builds a record by hand, must not produce an entity a
        /// receiver cannot classify. Zero meaning "projectile" makes the unstamped case degrade to the
        /// commonest kind; zero meaning "unset" would make every such record a silent drop.</para>
        /// </summary>
        [Test]
        public void TransientKind_DefaultsToProjectileAndSurvivesTheWireAndTheHash()
        {
            Assert.AreEqual(SimTransientKind.Projectile, default(SimProjectileState).Kind,
                "A default-constructed record must read as a fired round, not as an unknown kind.");

            SimSnapshot source = BuildRepresentativeSnapshot();
            SimSnapshot restored = SimSnapshotSerializer.Deserialize(
                SimSnapshotSerializer.Serialize(in source));

            SimProjectileState[] before = source.Rooms[0].Projectiles;
            SimProjectileState[] after = restored.Rooms[0].Projectiles;

            Assert.AreEqual(before.Length, after.Length, "The transient array lost a record on the wire.");

            for (int i = 0; i < before.Length; i++)
            {
                Assert.AreEqual(before[i].Kind, after[i].Kind,
                    "Record " + i + " lost its kind across serialize -> deserialize.");
            }

            // Absent control: the state hash must actually see the field. A hash that ignored Kind
            // would let a mine be reconciled as a fired round without the divergence being visible.
            SimSnapshot changed = BuildRepresentativeSnapshot();
            ulong baseline = changed.ComputeStateHash();
            changed.Rooms[0].Projectiles[0].Kind = SimTransientKind.Mine;

            Assert.AreNotEqual(baseline, changed.ComputeStateHash(),
                "SimProjectileState.Kind is not mixed into ComputeStateHash.");
        }

        // ── v4 (§10.2 gaps 1 and 2) ───────────────────────────────────────────

        [Test]
        public void V10ProtocolVersion_IsTen()
        {
            // The literal, not the symbol: a test that compares CurrentProtocolVersion against itself
            // stays green through a silent rollback to v9, which is precisely the change this pins.
            // v10 re-encoded the wire in place (SimUnitState flags byte + quantized scalars,
            // SimMotorState flags ushort, PlayerCommand interned weapon key + conditional target and
            // cursor), so a v9 peer would misread the first record of every payload — refuse at the
            // handshake before a single field is interpreted.
            Assert.AreEqual(10u, SimSnapshotSerializer.CurrentProtocolVersion,
                "The replication handshake shares this constant. Rolling back to 9 would let a v9 " +
                "peer join and then misparse every record in every room delta.");
        }

        /// <summary>
        /// v10 golden wire sizes. A record's byte width is a protocol fact, not an implementation
        /// detail: the snapshot body is a run of fixed-width records, so one re-packed field silently
        /// re-frames every record after it. Pinned as literals so a packing regression is caught by
        /// name. (PlayerCommand's variable-width sizes are pinned in SimCommandUplinkTests, which
        /// owns the command codec fixture.)
        /// </summary>
        [Test]
        public void V10RecordWireSizes_ArePinned()
        {
            var unit = new SimUnitState();
            Assert.AreEqual(82, WireSize(in unit),
                "v10 SimUnitState = hash 8 + generation 4 + lifecycle 4 + slot-bias 2 + definition 2 " +
                "+ flags 1 + pos 8 + facing 1 + vel 8 + overhang 8 + carry 8 + health 8 + dissolve 1 " +
                "+ faction 1 + digger 1 + pose 1+2+4 + weapon-hash 8 + aim 2. A different width means " +
                "a field changed width without the version bump that must accompany it.");

            var motor = new SimMotorState();
            Assert.AreEqual(64, WireSize(in motor),
                "v10 SimMotorState = hash 8 + pos 8 + vel 8 + flags 2 + facing 1 + impulse 4 + dash 4 " +
                "+ dash-vel 8 + drop-timer 4 + ladder-dir 1 + ladder-snap 4 + gravity 4 + carry 8.");
        }

        private static int WireSize<T>(in T record)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                switch (record)
                {
                    case SimUnitState unit: unit.WriteTo(writer); break;
                    case SimMotorState motor: motor.WriteTo(writer); break;
                    default: throw new ArgumentException("No wire codec for " + typeof(T).Name);
                }
            }
            return (int)stream.Length;
        }

        // ── v10 quantization behaviour ─────────────────────────────────────────

        [Test]
        public void V10FlagsByte_PacksLsbFirstInDocumentedOrder()
        {
            // Every bool set → its own bit, and each bit set alone → exactly its own bool. This is
            // the mutation control for the packing: a swapped bit order passes a round-trip of
            // all-true but fails here.
            for (int bit = 0; bit < 7; bit++)
            {
                var unit = new SimUnitState();
                switch (bit)
                {
                    case 0: unit.IsGrounded = true; break;
                    case 1: unit.DropThroughPlatforms = true; break;
                    case 2: unit.HasRuntimeHealth = true; break;
                    case 3: unit.IsDead = true; break;
                    case 4: unit.IsDown = true; break;
                    case 5: unit.IsDissolving = true; break;
                    case 6: unit.AmbushArmed = true; break;
                }

                SimUnitState restored = SimUnitState.ReadFrom(ReaderFor(unit));
                Assert.AreEqual(bit == 0, restored.IsGrounded, "bit " + bit + " leaked into IsGrounded");
                Assert.AreEqual(bit == 1, restored.DropThroughPlatforms, "bit " + bit + " leaked into DropThroughPlatforms");
                Assert.AreEqual(bit == 2, restored.HasRuntimeHealth, "bit " + bit + " leaked into HasRuntimeHealth");
                Assert.AreEqual(bit == 3, restored.IsDead, "bit " + bit + " leaked into IsDead");
                Assert.AreEqual(bit == 4, restored.IsDown, "bit " + bit + " leaked into IsDown");
                Assert.AreEqual(bit == 5, restored.IsDissolving, "bit " + bit + " leaked into IsDissolving");
                Assert.AreEqual(bit == 6, restored.AmbushArmed, "bit " + bit + " leaked into AmbushArmed");
            }
        }

        [Test]
        public void V10ControllerSlot_SpanningTheAdmittedRange_RoundTripsExactly()
        {
            // The admission rule admits peer ids 1..255 as slots (TrySlotForPeer), plus -1 for host
            // AI — the biased ushort must carry all 257 values losslessly.
            foreach (int slot in new[] { -1, 0, 1, 7, 127, 128, 255 })
            {
                var unit = new SimUnitState { ControllerSlot = slot };
                Assert.AreEqual(slot, SimUnitState.ReadFrom(ReaderFor(unit)).ControllerSlot,
                    "ControllerSlot " + slot + " must survive the wire exactly.");
            }
        }

        [Test]
        public void V10ControllerSlot_BiasBeyondTheAdmittedRange_IsRejectedAsCorrupt()
        {
            var unit = new SimUnitState { ControllerSlot = 0 };
            byte[] bytes = BytesFor(unit);
            // The bias is the 4th field: EntityHash 8 + Generation 4 + LifecycleSequence 4 = bytes
            // 16..17. 257 (> 256) cannot be a legal slot bias.
            bytes[16] = 0x01;
            bytes[17] = 0x01;

            var ex = Assert.Throws<InvalidDataException>(() => SimUnitState.ReadFrom(ReaderFor(bytes)),
                "A slot bias above 256 names a slot the admission rule can never assign; the reader " +
                "must refuse rather than fabricate an owner.");
            StringAssert.Contains("controller slot", ex.Message);
        }

        [Test]
        public void V10DissolveAndAim_StayWithinTheirDocumentedWireErrors()
        {
            // Endpoints exact, interior within half a step.
            foreach (float progress in new[] { 0f, 0.25f, 0.5f, 0.999f, 1f })
            {
                var unit = new SimUnitState { DissolveProgress = progress };
                float restored = SimUnitState.ReadFrom(ReaderFor(unit)).DissolveProgress;
                Assert.AreEqual(progress, restored, 0.5f / 255f + 1e-6f,
                    "DissolveProgress " + progress + " drifted past the byte wire's half-step.");
            }

            foreach (float angle in new[] { -180f, -90f, -0.4f, 0f, 12.345f, 90f, 180f })
            {
                var unit = new SimUnitState { AimAngle = angle };
                float restored = SimUnitState.ReadFrom(ReaderFor(unit)).AimAngle;
                Assert.AreEqual(angle, restored, 0.5f / SimUnitState.AimAngleWireScale + 1e-4f,
                    "AimAngle " + angle + " drifted past the fixed-point wire's half-step.");
                Assert.LessOrEqual(Math.Abs(restored), 180.0001f, "The wire angle left the atan2 range.");
            }
        }

        private static byte[] BytesFor(in SimUnitState unit)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                unit.WriteTo(writer);
            }
            return stream.ToArray();
        }

        private static BinaryReader ReaderFor(in SimUnitState unit)
        {
            return ReaderFor(BytesFor(in unit));
        }

        private static BinaryReader ReaderFor(byte[] bytes)
        {
            return new BinaryReader(new MemoryStream(bytes, false));
        }

        [Test]
        public void V9DoorStates_SurviveTheWireAndMoveTheHash()
        {
            SimSnapshot source = BuildRepresentativeSnapshot();
            SimSnapshot restored = SimSnapshotSerializer.Deserialize(
                SimSnapshotSerializer.Serialize(in source));

            SimDoorState[] before = source.Rooms[0].DoorStates;
            SimDoorState[] after = restored.Rooms[0].DoorStates;

            Assert.AreEqual(before.Length, after.Length, "The door array lost a record on the wire.");
            for (int i = 0; i < before.Length; i++)
            {
                Assert.AreEqual(before[i], after[i],
                    "Record " + i + " changed across serialize -> deserialize.");
            }

            // The closed-door record (index 9) is the one a "only opened doors" reading would drop:
            // flipping it must move the state hash, or a receiver could not tell it from "no door".
            SimSnapshot changed = CloneWithDoorFlip(source);
            Assert.AreNotEqual(source.ComputeStateHash(), changed.ComputeStateHash(),
                "SimDoorState.IsOpen is not mixed into ComputeStateHash.");
        }

        private static SimSnapshot CloneWithDoorFlip(SimSnapshot source)
        {
            // A hand copy of the one field under test — the records are structs, so a memberwise
            // clone of the first room's door array is enough; nothing else may move.
            SimSnapshot changed = source;
            SimRoomDelta room = source.Rooms[0];
            SimDoorState[] doors = new SimDoorState[room.DoorStates.Length];
            for (int i = 0; i < doors.Length; i++) doors[i] = room.DoorStates[i];
            doors[1].IsOpen = !doors[1].IsOpen;
            room.DoorStates = doors;
            SimRoomDelta[] rooms = new SimRoomDelta[source.Rooms.Length];
            for (int i = 0; i < rooms.Length; i++) rooms[i] = source.Rooms[i];
            rooms[0] = room;
            changed.Rooms = rooms;
            return changed;
        }

        [Test]
        public void V4DroppedItemIdentity_SurvivesTheWireAndMovesTheHash()
        {
            SimSnapshot source = BuildRepresentativeSnapshot();
            SimSnapshot restored = SimSnapshotSerializer.Deserialize(
                SimSnapshotSerializer.Serialize(in source));

            SimDroppedItem[] before = source.Rooms[0].DroppedItems;
            SimDroppedItem[] after = restored.Rooms[0].DroppedItems;

            Assert.AreEqual(before.Length, after.Length, "The loot array lost a record on the wire.");
            for (int i = 0; i < before.Length; i++)
            {
                Assert.AreEqual(before[i].EntityHash, after[i].EntityHash,
                    "Record " + i + " lost its host-assigned EntityHash across serialize -> deserialize.");
            }

            // The two stacks are identical in content and position; only the id separates them. If the
            // record's equality ignored the id they would compare equal, and a receiver could not tell
            // which one a pickup removed.
            Assert.AreEqual(before[0].ItemIdHash, before[1].ItemIdHash);
            Assert.AreEqual(before[0].Px, before[1].Px);
            Assert.AreEqual(before[0].Py, before[1].Py);
            Assert.IsFalse(before[0].Equals(before[1]),
                "Two stacks at the same pixel must not be the same record; EntityHash is their identity.");

            SimSnapshot changed = BuildRepresentativeSnapshot();
            ulong baseline = changed.ComputeStateHash();
            changed.Rooms[0].DroppedItems[0].EntityHash ^= 1UL;

            Assert.AreNotEqual(baseline, changed.ComputeStateHash(),
                "SimDroppedItem.EntityHash is not mixed into ComputeStateHash.");
        }

        [Test]
        public void V4TileJournal_SurvivesTheWireAndMovesTheHash()
        {
            SimSnapshot source = BuildRepresentativeSnapshot();
            SimSnapshot restored = SimSnapshotSerializer.Deserialize(
                SimSnapshotSerializer.Serialize(in source));

            Assert.IsNotNull(restored.TileMutations, "The codec read no journal at all.");
            Assert.AreEqual(source.TileMutations.Length, restored.TileMutations.Length,
                "The reliable journal lost an event on the wire.");
            for (int i = 0; i < source.TileMutations.Length; i++)
            {
                Assert.IsTrue(source.TileMutations[i].Equals(restored.TileMutations[i]),
                    "Tile event " + i + " did not survive serialize -> deserialize field-for-field.");
            }

            var probes = new (string Name, System.Action<SimSnapshot> Mutate)[]
            {
                ("EventSequence", s => s.TileMutations[0].EventSequence += 1u),
                ("TileX",         s => s.TileMutations[0].TileX += 1),
                ("TileY",         s => s.TileMutations[0].TileY += 1),
                ("PhysicsType",   s => s.TileMutations[0].PhysicsType += 1),
                ("HitPoints",     s => s.TileMutations[0].HitPoints += 1),
                ("RoomHash",      s => s.TileMutations[0].RoomHash ^= 1UL),
                ("WorldRevision", s => s.TileMutations[0].WorldRevision += 1),
            };

            foreach ((string name, System.Action<SimSnapshot> mutate) in probes)
            {
                SimSnapshot fresh = BuildRepresentativeSnapshot();
                ulong baseline = fresh.ComputeStateHash();
                mutate(fresh);
                Assert.AreNotEqual(baseline, fresh.ComputeStateHash(),
                    "SimTileMutation." + name + " is not mixed into ComputeStateHash. A receiver " +
                    "would apply a geometry change the state hash cannot see.");
            }

            // A producer with no tile changes may leave the journal null; the codec reads zero events
            // back as an empty array. Those are one state, so they must hash and compare equal.
            SimSnapshot withNull = BuildRepresentativeSnapshot();
            withNull.TileMutations = null;
            SimSnapshot withEmpty = BuildRepresentativeSnapshot();
            withEmpty.TileMutations = new SimTileMutation[0];

            Assert.AreEqual(withNull.ComputeStateHash(), withEmpty.ComputeStateHash(),
                "A null journal and an empty journal are the same state and must hash the same.");
            Assert.IsTrue(withNull.Equals(withEmpty),
                "A null journal and an empty journal must compare equal, or every round trip of a " +
                "tile-free snapshot would report a false divergence.");
        }

        [Test]
        public void V4Equals_SeesTheJournalWhenRoomsAreShared()
        {
            // The regression this pins: Equals used to return true as soon as Rooms was the same array
            // instance, which was harmless while the journal did not exist and is a false "equal" now.
            SimSnapshot a = BuildRepresentativeSnapshot();
            SimSnapshot b = a;                       // deliberately the same Rooms reference
            b.TileMutations = new SimTileMutation[0];

            Assert.IsFalse(a.Equals(b),
                "Two snapshots with the same room array but different tile journals are not equal. " +
                "Returning true on a shared Rooms reference hides the whole reliable stream.");
            Assert.AreNotEqual(a.ComputeStateHash(), b.ComputeStateHash(),
                "The state hash must disagree too; otherwise the equality bug is only half visible.");
        }

        [Test]
        public void V3Payload_WithoutTheJournalCount_IsRejected()
        {
            // A v3 payload ends immediately after the rooms. A v4 reader must not silently accept it
            // as "zero tile events": the missing count is a layout mismatch, not an empty journal.
            // Serialize with no journal first, so the last four bytes really are the v4-only count —
            // trimming those reproduces the v3 layout exactly.
            SimSnapshot snapshot = BuildRepresentativeSnapshot();
            snapshot.TileMutations = null;
            byte[] payload = SimSnapshotSerializer.Serialize(in snapshot);

            byte[] withoutJournalCount = new byte[payload.Length - 4];
            Array.Copy(payload, withoutJournalCount, withoutJournalCount.Length);

            var ex = Assert.Throws<InvalidDataException>(
                () => SimSnapshotSerializer.Deserialize(withoutJournalCount),
                "A payload whose layout predates the journal must be rejected, not read as a " +
                "tile-free snapshot.");
            StringAssert.Contains("tile mutation count", ex.Message);
        }

        // ── Corruptions: each must be rejected, loudly ────────────────────────

        [Test]
        public void WrongVersion_IsRejected()
        {
            SimSnapshot snapshot = BuildRepresentativeSnapshot();
            byte[] payload = SimSnapshotSerializer.Serialize(in snapshot);

            // Rewrite the version field (bytes 4..7) to a version this build does not read.
            byte[] wrongVersion = (byte[])payload.Clone();
            uint bad = SimSnapshotSerializer.CurrentProtocolVersion + 1;
            wrongVersion[4] = (byte)(bad & 0xFF);
            wrongVersion[5] = (byte)((bad >> 8) & 0xFF);
            wrongVersion[6] = (byte)((bad >> 16) & 0xFF);
            wrongVersion[7] = (byte)((bad >> 24) & 0xFF);

            var ex = Assert.Throws<InvalidDataException>(
                () => SimSnapshotSerializer.Deserialize(wrongVersion),
                "A payload at the wrong protocol version must be rejected before any field is read.");
            StringAssert.Contains("version", ex.Message);
        }

        [Test]
        public void TruncatedPayload_IsRejected()
        {
            SimSnapshot snapshot = BuildRepresentativeSnapshot();
            byte[] payload = SimSnapshotSerializer.Serialize(in snapshot);

            // Drop the tail mid-record. The reader must hit a hard end-of-stream, not fabricate state.
            byte[] truncated = new byte[payload.Length - 7];
            Array.Copy(payload, truncated, truncated.Length);

            Assert.Catch(
                () => SimSnapshotSerializer.Deserialize(truncated),
                "A truncated snapshot must throw (EndOfStreamException or InvalidDataException), never " +
                "silently fabricate the missing state.");
        }

        [Test]
        public void TrailingBytes_AreRejected()
        {
            SimSnapshot snapshot = BuildRepresentativeSnapshot();
            byte[] payload = SimSnapshotSerializer.Serialize(in snapshot);

            // Append garbage after the last record. A well-formed payload ends exactly; extra bytes
            // mean the payload does not match the version it claims.
            byte[] withTail = new byte[payload.Length + 4];
            Array.Copy(payload, withTail, payload.Length);

            var ex = Assert.Throws<InvalidDataException>(
                () => SimSnapshotSerializer.Deserialize(withTail),
                "A payload with trailing bytes after the last record must be rejected as corrupt.");
            StringAssert.Contains("trailing", ex.Message);
        }

        [Test]
        public void BadMagic_IsRejected()
        {
            SimSnapshot snapshot = BuildRepresentativeSnapshot();
            byte[] payload = SimSnapshotSerializer.Serialize(in snapshot);

            byte[] notASnapshot = (byte[])payload.Clone();
            notASnapshot[0] = (byte)'X';

            var ex = Assert.Throws<InvalidDataException>(
                () => SimSnapshotSerializer.Deserialize(notASnapshot),
                "A payload that is not a snapshot at all must be rejected on its magic.");
            StringAssert.Contains("magic", ex.Message);
        }

        [Test]
        public void CorruptCount_IsRejected()
        {
            // Hand-build a payload whose room count is absurd, and assert the count guard rejects it
            // before it can drive an allocation or a nonsense loop.
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(new byte[] { (byte)'P', (byte)'F', (byte)'E', (byte)'S' });
                writer.Write(SimSnapshotSerializer.CurrentProtocolVersion);
                writer.Write((long)0);      // tick
                writer.Write((uint)0);      // sequence
                writer.Write((long)0);      // revision
                writer.Write((ulong)0);     // baseline hash
                writer.Write(int.MaxValue); // corrupt room count
            }

            var ex = Assert.Throws<InvalidDataException>(
                () => SimSnapshotSerializer.Deserialize(stream.ToArray()),
                "An absurd count prefix must be rejected as corrupt, not trusted to allocate.");
            StringAssert.Contains("out of range", ex.Message);
        }
    }
}
