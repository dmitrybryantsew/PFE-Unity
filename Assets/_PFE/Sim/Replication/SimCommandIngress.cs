using System;
using System.Collections.Generic;
using System.IO;
using PFE.Core.Input;

namespace PFE.Sim.Replication
{
    /// <summary>
    /// The host-side consumer of admitted command batches. The view layer implements this to feed the
    /// commands into a player body; keeping it as one method means the pure layer never learns what a
    /// <c>MonoBehaviour</c>, a slot or a motor is.
    ///
    /// <para><b>One call per batch, not one call per command.</b> A batch is an atomic redundancy
    /// window: the sink must see the whole contiguous run so it can replay from the first new tick.
    /// <paramref name="firstNewIndex"/> lets it skip the overlap without a second array allocation.</para>
    /// </summary>
    public interface ISimCommandSink
    {
        void Apply(uint peerId, byte slot, PlayerCommand[] commands, int firstNewIndex);
    }

    /// <summary>What one batch did. Readbacks are part of the contract: an ingress that silently drops
    /// every intent looks identical to a client that never sent any.</summary>
    public readonly struct SimCommandReceipt
    {
        public SimCommandReceipt(bool applied, SimCommandRejectReason reason, int freshCommands,
            int duplicateCommands, int gapTicks, int lastAppliedTick)
        {
            Applied = applied;
            Reason = reason;
            FreshCommands = freshCommands;
            DuplicateCommands = duplicateCommands;
            GapTicks = gapTicks;
            LastAppliedTick = lastAppliedTick;
        }

        public bool Applied { get; }
        public SimCommandRejectReason Reason { get; }
        public int FreshCommands { get; }
        public int DuplicateCommands { get; }
        public int GapTicks { get; }
        public int LastAppliedTick { get; }

        public static SimCommandReceipt Reject(SimCommandRejectReason reason) =>
            new SimCommandReceipt(false, reason, 0, 0, 0, 0);
    }

    /// <summary>
    /// Phase 5: host-side command admission, per peer. Pure C# — no transport, no Unity, no scene —
    /// so the whole client → host intent path can be executed by the offline wall.
    ///
    /// <para><b>Ordering of the reliable channel belongs to the host, not here.</b>
    /// <c>SimReplicationHost</c> owns one high-water mark per peer shared by acks, interest updates
    /// and command batches. This class therefore receives only envelopes that already passed the
    /// session/peer/sequence gate, and refuses to invent a second sequence space: a duplicate batch
    /// must not be able to re-enter through a lower, private watermark.</para>
    ///
    /// <para><b>Redundancy, not retransmission.</b> Every batch carries the last N commands of the
    /// client's tape. Commands at or below <c>LastAppliedTick</c> are duplicates: they must be
    /// byte-identical to what was applied under that tick, or the client rewrote history and the whole
    /// batch is refused. A gap between the last applied tick and the first new one is <i>accepted</i>
    /// but reported in <see cref="SimCommandReceipt.GapTicks"/>: commands are absolute per-tick intent
    /// and the host sim is wall-clock driven, so refusing a gap would wedge the peer forever after one
    /// lost datagram. The next redundant batch repairs it.</para>
    ///
    /// <para><b>Atomicity.</b> Every check runs before the peer's tape is touched. A rejected batch
    /// leaves <c>LastAppliedTick</c>, the buffer and the sink exactly as they were.</para>
    /// </summary>
    public sealed class SimCommandIngress
    {
        private sealed class PeerState
        {
            public readonly PlayerCommandBuffer Applied;
            public int LastAppliedTick;
            public bool HasApplied;

            public PeerState(int capacity)
            {
                Applied = new PlayerCommandBuffer(capacity);
            }
        }

        private readonly ulong _sessionId;
        private readonly int _maxCommands;
        private readonly Func<uint, bool> _isPeerJoined;
        private readonly ISimCommandSink _sink;
        private readonly Dictionary<uint, PeerState> _peers = new Dictionary<uint, PeerState>();

        public SimCommandIngress(ulong sessionId, int maxCommandsPerBatch,
            Func<uint, bool> isPeerJoined, ISimCommandSink sink)
        {
            if (sessionId == 0UL) throw new ArgumentException("A host session id must be non-zero.", nameof(sessionId));
            if (maxCommandsPerBatch <= 0) throw new ArgumentOutOfRangeException(nameof(maxCommandsPerBatch));
            if (isPeerJoined == null) throw new ArgumentNullException(nameof(isPeerJoined));

            _sessionId = sessionId;
            _maxCommands = maxCommandsPerBatch;
            _isPeerJoined = isPeerJoined;
            _sink = sink;
        }

        public int AcceptedBatchCount { get; private set; }
        public int RejectedBatchCount { get; private set; }
        public int AppliedCommandCount { get; private set; }
        public int DuplicateCommandCount { get; private set; }
        public int GapBatchCount { get; private set; }
        public SimCommandRejectReason LastRejectReason { get; private set; }

        public int LastAppliedTick(uint peerId)
        {
            return _peers.TryGetValue(peerId, out PeerState peer) && peer.HasApplied
                ? peer.LastAppliedTick : 0;
        }

        public int BufferedCommandCount(uint peerId)
        {
            return _peers.TryGetValue(peerId, out PeerState peer) ? peer.Applied.Count : 0;
        }

        /// <summary>
        /// Admit one already-ordered envelope. Returns a receipt rather than throwing on a malformed
        /// payload: a corrupt packet from one peer must not take the host's tick down.
        /// </summary>
        public SimCommandReceipt Receive(SimReplicationEnvelope envelope)
        {
            SimCommandReceipt receipt = Inspect(envelope);
            if (!receipt.Applied)
            {
                RejectedBatchCount++;
                LastRejectReason = receipt.Reason;
            }
            return receipt;
        }

        private SimCommandReceipt Inspect(SimReplicationEnvelope envelope)
        {
            if (envelope.Kind != SimReplicationMessageKind.PlayerCommandBatch)
                return SimCommandReceipt.Reject(SimCommandRejectReason.WrongKind);
            if (envelope.SessionId != _sessionId)
                return SimCommandReceipt.Reject(SimCommandRejectReason.WrongSession);
            if (envelope.Payload == null)
                return SimCommandReceipt.Reject(SimCommandRejectReason.MalformedBatch);
            if (!_isPeerJoined(envelope.PeerId))
                return SimCommandReceipt.Reject(SimCommandRejectReason.UnknownPeer);
            if (_sink == null)
                return SimCommandReceipt.Reject(SimCommandRejectReason.NoConsumer);

            SimPlayerCommandBatch batch;
            try
            {
                batch = SimReplicationPayloadSerializer.ReadPlayerCommandBatch(envelope.Payload, _maxCommands);
            }
            catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException
                                      || e is IOException || e is ArgumentException)
            {
                return SimCommandReceipt.Reject(SimCommandRejectReason.MalformedBatch);
            }

            if (batch.SessionId != envelope.SessionId)
                return SimCommandReceipt.Reject(SimCommandRejectReason.WrongSession);
            if (batch.PeerId != envelope.PeerId)
                return SimCommandReceipt.Reject(SimCommandRejectReason.WrongPeer);

            PlayerCommand[] commands = batch.Commands;
            if (commands == null || commands.Length == 0)
                return SimCommandReceipt.Reject(SimCommandRejectReason.EmptyBatch);

            byte slot;
            if (!SimCommandAdmissionRules.TrySlotForPeer(envelope.PeerId, out slot))
                return SimCommandReceipt.Reject(SimCommandRejectReason.SlotNotOwnedByPeer);
            if (!SimCommandAdmissionRules.IsSingleSlot(commands, slot))
                return SimCommandReceipt.Reject(SimCommandRejectReason.SlotNotOwnedByPeer);
            if (!SimCommandAdmissionRules.AreTicksContiguous(commands))
                return SimCommandReceipt.Reject(SimCommandRejectReason.NonContiguousTicks);
            if (!SimCommandAdmissionRules.AreSequencesIncreasing(commands))
                return SimCommandReceipt.Reject(SimCommandRejectReason.SequencesNotIncreasing);

            PeerState peer;
            if (!_peers.TryGetValue(envelope.PeerId, out peer))
            {
                peer = new PeerState(_maxCommands);
                _peers[envelope.PeerId] = peer;
            }

            int lastApplied = peer.HasApplied ? peer.LastAppliedTick : 0;
            int firstNew = peer.HasApplied
                ? SimCommandAdmissionRules.FirstNewIndex(commands, lastApplied)
                : 0;

            // Overlap integrity: a re-delivered command must be the *same* command, not a rewrite of
            // history. An overlap that fell out of the bounded tape is likewise refused; silently
            // accepting it would make "duplicate" mean two different things.
            for (int i = 0; i < firstNew; i++)
            {
                PlayerCommand previous;
                if (!peer.Applied.TryGetForTick(commands[i].TickIndex, out previous)
                    || !previous.Equals(commands[i]))
                {
                    return SimCommandReceipt.Reject(SimCommandRejectReason.OverlapWasRewritten);
                }
            }

            int fresh = commands.Length - firstNew;
            int gapTicks = 0;
            if (fresh > 0 && peer.HasApplied)
            {
                // Wrap-safe at int.MaxValue: lastApplied + 1 is computed in uint space, not int.
                uint expected = unchecked((uint)lastApplied + 1u);
                uint skipped = unchecked((uint)commands[firstNew].TickIndex - expected);
                if (skipped > 0u) gapTicks = skipped > int.MaxValue ? int.MaxValue : (int)skipped;
            }

            // ── Commit. Reachable only after every check above, so nothing below can reject. ──
            for (int i = firstNew; i < commands.Length; i++) peer.Applied.Enqueue(commands[i]);
            peer.LastAppliedTick = commands[commands.Length - 1].TickIndex;
            peer.HasApplied = true;

            // A window made entirely of duplicates is a legitimate redundant retransmission, but it
            // carries no new intent. Calling the sink with firstNewIndex == length would let a lazy
            // consumer re-run a whole window of edges (jump/dash are one-shot) on every resend.
            if (fresh > 0) _sink.Apply(envelope.PeerId, slot, commands, firstNew);

            AcceptedBatchCount++;
            AppliedCommandCount += fresh;
            DuplicateCommandCount += firstNew;
            if (gapTicks > 0) GapBatchCount++;

            return new SimCommandReceipt(true, SimCommandRejectReason.None,
                fresh, firstNew, gapTicks, peer.LastAppliedTick);
        }
    }
}
