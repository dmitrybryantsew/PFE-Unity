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

                // requireAuthenticated MUST be false. There is no authenticator in this runtime
                // bootstrap; leaving FishNet's default true drops every client-to-server broadcast
                // and kicks the sender, which looks exactly like "the client never joined".
                _manager.ServerManager.Broadcast(connection, message,
                    requireAuthenticated: false, channel: Channel.Reliable);
            }
            else
            {
                if (_manager.ClientManager == null)
                {
                    CountUndelivered(envelope);
                    return;
                }

                _manager.ClientManager.Broadcast(message, Channel.Reliable);
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
