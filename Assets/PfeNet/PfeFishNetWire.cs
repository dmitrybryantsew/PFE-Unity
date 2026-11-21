using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using PFE.Sim.Replication;
using UnityEngine;

namespace PFE.Net
{
    /// <summary>
    /// The FishNet implementation of the single transport seam. It lives outside every <c>PFE.*</c>
    /// asmdef on purpose: <c>PFE.Sim</c> stays transport-free, and no existing asmdef, scene or prefab
    /// has to be edited to bring FishNet into the project.
    /// </summary>
    public sealed class PfeFishNetWire : ISimReplicationWire
    {
        private readonly NetworkManager _manager;
        private readonly bool _isHost;
        private readonly PfeReplicationRouter _router;

        public PfeFishNetWire(NetworkManager manager, bool isHost, PfeReplicationRouter router)
        {
            if (manager == null) throw new System.ArgumentNullException(nameof(manager));
            if (router == null) throw new System.ArgumentNullException(nameof(router));

            _manager = manager;
            _isHost = isHost;
            _router = router;
        }

        public int SentCount { get; private set; }
        public int UndeliveredCount { get; private set; }
        public string LastUndeliveredKind { get; private set; }

        public void Send(SimReplicationEnvelope envelope)
        {
            var message = new PfeReplicationBroadcast
            {
                Bytes = PfeReplicationCodec.Encode(envelope),
            };

            if (_isHost)
            {
                int connectionId;
                if (!_router.TryResolve(envelope, out connectionId)
                    || _manager.ServerManager == null
                    || !_manager.ServerManager.Clients.TryGetValue(connectionId, out NetworkConnection connection)
                    || connection == null)
                {
                    CountUndelivered(envelope);
                    return;
                }

                // Per-tick snapshots ride the UNRELIABLE channel on purpose. The protocol repairs loss
                // at the app layer — the tile journal repeats unacked events until an ack arrives, loot
                // is a full-replace set, and the receiver admits any newer SnapshotSequence, so a lost
                // snapshot simply means the next one lands instead. Keeping a 60 Hz multi-KB stream on
                // FishNet's reliable channel costs a retained copy plus ack bookkeeping per packet per
                // tick for exactly zero benefit, and it was the prime suspect in the multiplayer FPS
                // drop. JoinAccept and BaselineChunk stay Reliable: they are one-shot and must arrive,
                // and a snapshot that overtakes an in-flight baseline is already refused by the
                // receiver's baseline-pending gate.
                Channel channel = envelope.Kind == SimReplicationMessageKind.Snapshot
                    ? Channel.Unreliable
                    : Channel.Reliable;

                // requireAuthenticated MUST be false. There is no authenticator in this runtime
                // bootstrap; leaving FishNet's default true drops every client-to-server broadcast
                // and kicks the sender, which looks exactly like "the client never joined".
                _manager.ServerManager.Broadcast(connection, message,
                    requireAuthenticated: false, channel: channel);
            }
            else
            {
                if (_manager.ClientManager == null)
                {
                    CountUndelivered(envelope);
                    return;
                }

                // Command batches ride the UNRELIABLE channel, mirroring the host-side snapshot
                // routing above. A lost batch is repaired at the app layer by the redundant
                // last-N window (the next batch repeats the missed commands, and the ingress
                // overlap check makes a repeat harmless), while one dropped datagram on the
                // reliable channel would head-of-line block every ack and room transition
                // behind a TCP-style retransmission — the input stutter Wave 1A exists to kill.
                // The batch carries its own _commandEnvelopeSequence, so it can neither mark a
                // reliable envelope stale nor be marked stale by one.
                Channel channel = envelope.Kind == SimReplicationMessageKind.PlayerCommandBatch
                    ? Channel.Unreliable
                    : Channel.Reliable;

                _manager.ClientManager.Broadcast(message, channel);
            }

            SentCount++;
        }

        private void CountUndelivered(SimReplicationEnvelope envelope)
        {
            UndeliveredCount++;
            LastUndeliveredKind = envelope.Kind.ToString();
        }

        /// <summary>Total decode of one inbound broadcast. A malformed frame is data, not an
        /// exception: FishNet invokes handlers from its receive loop and an uncaught throw there
        /// would abort the rest of that connection's packets.</summary>
        public bool TryReceive(byte[] bytes, out SimReplicationEnvelope envelope)
        {
            return PfeReplicationCodec.TryDecode(bytes, out envelope);
        }
    }
}
