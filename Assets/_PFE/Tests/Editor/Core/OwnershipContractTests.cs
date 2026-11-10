using System.Collections.Generic;
using NUnit.Framework;
using PFE.Sim.Session;
using PFE.Sim.State;
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Row 3.4's acceptance: <i>"two-process-style contract harness covering claim, disconnect body,
    /// reconnect"</i> (F-07).
    ///
    /// <para><b>What "two-process-style" means here, and why it is not a unit test of the
    /// dictionary.</b> A test that calls <c>Claim</c> and reads back <c>EntityOf</c> proves the
    /// dictionary works; it proves nothing about the contract, because the contract is about what a
    /// <i>client</i> learns. So every scenario below drives the host, serialises the resulting
    /// <see cref="SimSnapshot"/> through the real codec, deserialises it, and asserts on what a client
    /// can reconstruct from those bytes. A rule that the host applies but does not put on the wire
    /// fails here even though it "works".</para>
    ///
    /// <para><b>The four rules under test.</b> A claim binds a slot to a body; a disconnect leaves the
    /// body in the world under host control; a reconnect rebinds the same body if it is still free;
    /// and a claim is never a steal. Each is a separate test so a regression names the rule.</para>
    /// </summary>
    [TestFixture]
    public sealed class OwnershipContractTests
    {
        private static readonly EntityId BodyA = EntityId.CreateForPlayer(0);
        private static readonly EntityId BodyB = EntityId.CreateForPlayer(1);

        /// <summary>
        /// The host's view, put on the wire exactly as a snapshot would carry it: one room, one record
        /// per body, each stamped by <see cref="OwnershipService.ApplyTo"/>.
        /// </summary>
        private static SimSnapshot HostSnapshot(OwnershipService host, params EntityId[] bodies)
        {
            var units = new SimUnitState[bodies.Length];

            for (int i = 0; i < bodies.Length; i++)
            {
                units[i] = new SimUnitState
                {
                    EntityHash = bodies[i].Hash,
                    Generation = 0,
                    LifecycleSequence = 1,
                    DefinitionId = 3,
                    Health = 100f,
                    MaxHealth = 100f,
                    HasRuntimeHealth = true,
                };

                // The only line that links ownership to the wire.
                host.ApplyTo(ref units[i]);
            }

            return new SimSnapshot
            {
                ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion,
                TickIndex = 100,
                SnapshotSequence = 1,
                WorldRevision = 1,
                BaselineHash = 0UL,
                Rooms = new[]
                {
                    new SimRoomDelta
                    {
                        RoomHash = 0x1234,
                        LifecycleSequence = 1,
                        Units = units,
                        Motors = new SimMotorState[0],
                        Projectiles = new SimProjectileState[0],
                        DroppedItems = new SimDroppedItem[0],
                        RemovedEntityHashes = new ulong[0],
                    },
                },
            };
        }

        /// <summary>Serialize → deserialize → the client's table: entity hash → controller slot.</summary>
        private static Dictionary<ulong, int> ClientView(SimSnapshot snapshot)
        {
            byte[] payload = SimSnapshotSerializer.Serialize(in snapshot);
            SimSnapshot received = SimSnapshotSerializer.Deserialize(payload);

            var view = new Dictionary<ulong, int>();
            foreach (SimUnitState unit in received.Rooms[0].Units)
            {
                view[unit.EntityHash] = unit.ControllerSlot;
            }
            return view;
        }

        // ── Claim ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Claim_BindsTheSlotAndTravelsOnTheWire()
        {
            var host = new OwnershipService();

            OwnershipResult result = host.Claim(0, BodyA);

            Assert.AreEqual(OwnershipChange.Bound, result.Change, "A first claim must bind.");
            Assert.IsTrue(result.Applied, "A bind is an applied change, so the caller may emit an event.");
            Assert.AreEqual(0, host.SlotOf(BodyA));

            Dictionary<ulong, int> client = ClientView(HostSnapshot(host, BodyA));

            Assert.AreEqual(0, client[BodyA.Hash],
                "The client must learn that slot 0 drives this body. A binding the host keeps to itself "
                + "is a player whose character nobody else can attribute.");
        }

        [Test]
        public void Claim_OfABodyAnotherSlotOwns_IsRejectedNotStolen()
        {
            var host = new OwnershipService();
            host.Claim(0, BodyA);

            OwnershipResult stolen = host.Claim(1, BodyA);

            Assert.AreEqual(OwnershipChange.Rejected, stolen.Change);
            Assert.AreEqual(OwnershipRejection.EntityOwnedByAnotherSlot, stolen.Rejection,
                "The refusal must name the reason: 'rejected' alone cannot distinguish a bad slot from a "
                + "contested body.");
            Assert.IsFalse(stolen.Applied, "A rejected request changes nothing.");

            Assert.AreEqual(0, host.SlotOf(BodyA), "The original owner keeps the body.");
            Assert.AreEqual(EntityId.Empty, host.EntityOf(1), "The losing slot drives nothing.");
            Assert.AreEqual(0, ClientView(HostSnapshot(host, BodyA))[BodyA.Hash]);
        }

        [Test]
        public void Claim_OfASecondBody_ReleasesTheFirst()
        {
            var host = new OwnershipService();
            host.Claim(0, BodyA);

            OwnershipResult second = host.Claim(0, BodyB);

            Assert.AreEqual(OwnershipChange.Replaced, second.Change);

            Dictionary<ulong, int> client = ClientView(HostSnapshot(host, BodyA, BodyB));

            Assert.AreEqual(OwnershipService.NoSlot, client[BodyA.Hash],
                "The released body must become host-simulated, not stay bound to a slot that no longer "
                + "reads its input.");
            Assert.AreEqual(0, client[BodyB.Hash]);
        }

        [Test]
        public void RepeatedClaim_IsUnchangedAndNotApplied()
        {
            var host = new OwnershipService();
            host.Claim(0, BodyA);

            OwnershipResult again = host.Claim(0, BodyA);

            Assert.AreEqual(OwnershipChange.Unchanged, again.Change);
            Assert.IsFalse(again.Applied,
                "A repeated claim must not count as an applied change — a caller that emitted a reliable "
                + "ownership event for it would spam the channel on every reconnect attempt.");
        }

        [Test]
        public void Claim_WithAnInvalidSlotOrEntity_IsRejected()
        {
            var host = new OwnershipService();

            Assert.AreEqual(OwnershipRejection.InvalidSlot, host.Claim(-1, BodyA).Rejection,
                "NoSlot is not a claimable slot: it means 'the host owns it'.");
            Assert.AreEqual(OwnershipRejection.InvalidEntity, host.Claim(0, EntityId.Empty).Rejection,
                "An empty id has hash 0, which is also the unowned sentinel.");
            Assert.AreEqual(0, host.BoundSlotCount);
        }

        // ── Disconnect: the body stays ───────────────────────────────────────────────────────

        [Test]
        public void Disconnect_LeavesTheBodyInTheWorldUnderHostControl()
        {
            var host = new OwnershipService();
            host.Claim(0, BodyA);

            OwnershipResult left = host.Disconnect(0);

            Assert.AreEqual(OwnershipChange.Disconnected, left.Change);
            Assert.IsFalse(host.IsConnected(0));
            Assert.IsFalse(host.IsBound(0));

            Dictionary<ulong, int> client = ClientView(HostSnapshot(host, BodyA));

            Assert.IsTrue(client.ContainsKey(BodyA.Hash),
                "The body must still be in the snapshot. Destroying a player's character when their "
                + "connection drops is a teleport home on every lost packet.");
            Assert.AreEqual(OwnershipService.NoSlot, client[BodyA.Hash],
                "A disconnected body must read as host-simulated, so the host keeps stepping it.");
            Assert.AreEqual(SimUnitState.HostAiControllerSlot, OwnershipService.NoSlot,
                "NoSlot and HostAiControllerSlot must be one value, or 'unowned' and 'host-simulated' "
                + "become two concepts that can drift apart.");
        }

        [Test]
        public void Disconnect_OfASlotThatIsNotConnected_IsRejected()
        {
            var host = new OwnershipService();

            Assert.AreEqual(OwnershipRejection.NotConnected, host.Disconnect(7).Rejection);
            Assert.AreEqual(OwnershipRejection.InvalidSlot, host.Disconnect(-1).Rejection);
        }

        // ── Reconnect ────────────────────────────────────────────────────────────────────────

        [Test]
        public void Reconnect_RebindsTheSameBody()
        {
            var host = new OwnershipService();
            host.Claim(0, BodyA);
            host.Disconnect(0);

            OwnershipResult back = host.Reconnect(0);

            Assert.AreEqual(OwnershipChange.Rebound, back.Change);
            Assert.IsTrue(host.IsBound(0));
            Assert.AreEqual(0, ClientView(HostSnapshot(host, BodyA))[BodyA.Hash]);
        }

        [Test]
        public void Reconnect_WhenAnotherSlotTookTheBody_ConnectsButStaysUnbound()
        {
            var host = new OwnershipService();
            host.Claim(0, BodyA);
            host.Disconnect(0);

            // Somebody else took the body while slot 0 was away.
            host.Claim(1, BodyA);

            OwnershipResult back = host.Reconnect(0);

            Assert.AreEqual(OwnershipChange.ReconnectedUnbound, back.Change,
                "A returning player must not steal a body another player is now driving.");
            Assert.IsTrue(host.IsConnected(0), "They are present even without a body.");
            Assert.IsFalse(host.IsBound(0));

            Dictionary<ulong, int> client = ClientView(HostSnapshot(host, BodyA));

            Assert.AreEqual(1, client[BodyA.Hash], "The wire must still say slot 1 drives it.");
        }

        [Test]
        public void Reconnect_OfAnAlreadyConnectedSlot_IsRejected()
        {
            var host = new OwnershipService();
            host.Claim(0, BodyA);

            Assert.AreEqual(OwnershipRejection.AlreadyConnected, host.Reconnect(0).Rejection);
        }

        [Test]
        public void Reconnect_WithNoPreviousBody_ConnectsUnbound()
        {
            var host = new OwnershipService();
            host.Claim(0, BodyA);
            host.Disconnect(0);

            Assert.AreEqual(OwnershipChange.ReconnectedUnbound, host.Reconnect(5).Change,
                "A slot that never held a body can still reconnect; it simply has nothing to rebind.");
            Assert.IsTrue(host.IsConnected(5));
        }

        // ── The decision that would be expensive to reverse ──────────────────────────────────

        /// <summary>
        /// A handover must not touch the record's lifetime. <c>Generation</c> is the staleness guard a
        /// receiver orders packets by: bumping it on a slot change would tell every client that the unit
        /// was despawned and respawned, so a player who reconnects would snap rather than continue.
        ///
        /// <para><b>The mutation control is in-fixture on purpose.</b> The shipped <c>ApplyTo</c> is
        /// three lines; a naive variant that also bumps the generation is written here and shown to fail
        /// the same assertion, so the assertion is proven able to see the defect rather than merely
        /// passing.</para>
        /// </summary>
        [Test]
        public void ApplyTo_WritesTheSlotAndNothingElse()
        {
            var host = new OwnershipService();
            host.Claim(0, BodyA);

            var stamped = new SimUnitState { EntityHash = BodyA.Hash, Generation = 4, LifecycleSequence = 9 };
            host.ApplyTo(ref stamped);

            Assert.AreEqual(0, stamped.ControllerSlot);
            Assert.AreEqual(4, stamped.Generation, "A handover is not a new lifetime.");
            Assert.AreEqual(9, stamped.LifecycleSequence, "A handover is not a new lifecycle event.");

            // ── the mutation control: the naive shape must fail this ──
            var naive = new SimUnitState { EntityHash = BodyA.Hash, Generation = 4, LifecycleSequence = 9 };
            naive.ControllerSlot = host.SlotOf(BodyA);
            naive.Generation = unchecked(naive.Generation + 1);   // the defect

            Assert.AreNotEqual(4, naive.Generation,
                "The mutation control did not actually mutate; the assertion below would be vacuous.");
            Assert.AreNotEqual(stamped.Generation, naive.Generation,
                "A guard that cannot separate ApplyTo from the generation-bumping variant is not a guard.");
        }

        [Test]
        public void Clear_ForgetsEverySlotWithoutTouchingBodies()
        {
            var host = new OwnershipService();
            host.Claim(0, BodyA);
            host.Claim(1, BodyB);

            host.Clear();

            Assert.AreEqual(0, host.ConnectedSlotCount);
            Assert.AreEqual(0, host.BoundSlotCount);
            Assert.AreEqual(OwnershipService.NoSlot, host.SlotOf(BodyA));

            Dictionary<ulong, int> client = ClientView(HostSnapshot(host, BodyA, BodyB));

            Assert.AreEqual(2, client.Count, "The bodies still exist; only the session forgot them.");
            Assert.AreEqual(OwnershipService.NoSlot, client[BodyA.Hash]);
        }
    }
}
