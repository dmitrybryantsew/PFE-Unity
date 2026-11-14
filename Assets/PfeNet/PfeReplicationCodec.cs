using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PFE.Sim.Replication;

namespace PFE.Net
{
    /// <summary>Which end of a session this process is. <c>Off</c> means the bootstrap does nothing.</summary>
    public enum PfeNetRole
    {
        Off = 0,
        Host = 1,
        Client = 2,
    }

    /// <summary>
    /// The command-line configuration of one runtime session. Plain data: no Unity, no FishNet, no
    /// globals, so the whole parse can be executed offline and a typo in a launch argument is refused
    /// instead of silently starting a server on the wrong port.
    /// </summary>
    public sealed class PfeNetConfig
    {
        public PfeNetRole Role = PfeNetRole.Off;
        public string Address = "localhost";
        public ushort Port = 7770;
        /// <summary>0 means "derive from the process id at bootstrap", which is what makes two
        /// client processes joining one host get distinct nonces without extra launch arguments.
        /// An explicit non-zero value is still accepted for deterministic replays.</summary>
        public ulong ClientNonce = 0UL;
        public int CommandWindow = 8;
        public int ChunkSize = 1024;
        public int TicksPerSnapshot = 1;

        public bool IsHost => Role == PfeNetRole.Host;
    }

    /// <summary>
    /// Parses the <c>-pfe-net-*</c> launch arguments, following the project convention established by
    /// <c>PfeScenarioTest</c> (<c>-pfe-scenario</c> + <c>System.Environment.GetCommandLineArgs()</c>).
    ///
    /// <para><b>Why unknown options are refused.</b> A launch string is the only configuration a
    /// headless player has; there is no inspector to correct it. Silently ignoring a misspelled
    /// <c>-pfe-net-chunck</c> would start a session with a chunk size the operator never chose, and
    /// the symptom (late-join never completes) would be diagnosed as a networking bug.</para>
    /// </summary>
    public static class PfeNetCommandLine
    {
        public const string HostArg = "-pfe-net-host";
        public const string ClientArg = "-pfe-net-client";
        public const string AddressArg = "-pfe-net-address";
        public const string PortArg = "-pfe-net-port";
        public const string NonceArg = "-pfe-net-nonce";
        public const string WindowArg = "-pfe-net-window";
        public const string ChunkArg = "-pfe-net-chunk";
        public const string CadenceArg = "-pfe-net-tick-every";

        private static readonly string[] Known = new string[]
        {
            HostArg, ClientArg, AddressArg, PortArg, NonceArg, WindowArg, ChunkArg, CadenceArg,
        };

        private static readonly string[] Flags = new string[] { HostArg, ClientArg };

        public static bool TryParse(string[] args, out PfeNetConfig config)
        {
            config = new PfeNetConfig();
            if (args == null) return false;

            bool host = false;
            bool client = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == null || !arg.StartsWith("-pfe-net-", StringComparison.Ordinal)) continue;

                if (arg == HostArg) { host = true; continue; }
                if (arg == ClientArg) { client = true; continue; }

                if (!IsKnown(arg) || IsFlag(arg)) return false;
                if (i + 1 >= args.Length) return false;
                string value = args[++i];
                if (string.IsNullOrEmpty(value)) return false;

                if (arg == AddressArg)
                {
                    config.Address = value;
                }
                else if (arg == PortArg)
                {
                    int port;
                    if (!TryInt(value, out port) || port < 1 || port > ushort.MaxValue) return false;
                    config.Port = (ushort)port;
                }
                else if (arg == NonceArg)
                {
                    ulong nonce;
                    if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out nonce)
                        || nonce == 0UL) return false;
                    config.ClientNonce = nonce;
                }
                else if (arg == WindowArg)
                {
                    int window;
                    if (!TryInt(value, out window) || window < 1 || window > 64) return false;
                    config.CommandWindow = window;
                }
                else if (arg == ChunkArg)
                {
                    int chunk;
                    if (!TryInt(value, out chunk) || chunk < 64 || chunk > 65536) return false;
                    config.ChunkSize = chunk;
                }
                else if (arg == CadenceArg)
                {
                    int cadence;
                    if (!TryInt(value, out cadence) || cadence < 1 || cadence > 30) return false;
                    config.TicksPerSnapshot = cadence;
                }
                else
                {
                    return false;
                }
            }

            if (host && client) return false;   // one process cannot be both ends of one session.
            config.Role = host ? PfeNetRole.Host : (client ? PfeNetRole.Client : PfeNetRole.Off);
            return true;
        }

        private static bool IsKnown(string arg)
        {
            for (int i = 0; i < Known.Length; i++)
            {
                if (Known[i] == arg) return true;
            }
            return false;
        }

        private static bool IsFlag(string arg)
        {
            for (int i = 0; i < Flags.Length; i++)
            {
                if (Flags[i] == arg) return true;
            }
            return false;
        }

        private static bool TryInt(string value, out int result)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
    }

    /// <summary>
    /// Rules a datagram must satisfy before an endpoint is allowed to interpret it.
    ///
    /// <para><b>Why this is separate from <c>SimReplicationEnvelope.ReadFrom</c>.</b> That reader is a
    /// strict <i>codec</i>: it validates framing and protocol version but deliberately does not judge
    /// whether the message is meaningful. A network peer can still send a well-framed envelope with
    /// <c>Kind.None</c> and a null payload; handing that to <c>SimReplicationHost.Receive</c> would
    /// throw inside a payload reader rather than count a rejected envelope. Admissibility is the
    /// transport boundary's job.</para>
    /// </summary>
    public static class PfeReplicationWireRules
    {
        public static bool IsKnownKind(SimReplicationMessageKind kind)
        {
            return kind >= SimReplicationMessageKind.JoinRequest
                && kind <= SimReplicationMessageKind.PlayerCommandBatch;
        }

        public static bool IsAdmissible(in SimReplicationEnvelope envelope)
        {
            return envelope.ProtocolVersion == SimReplicationProtocolRules.CurrentProtocolVersion
                && IsKnownKind(envelope.Kind)
                && envelope.Payload != null
                && envelope.Payload.Length > 0;
        }
    }

    /// <summary>
    /// The byte boundary between <c>PFE.Sim</c> and the FishNet adapter. Encode is strict; decode is
    /// total — a socket is an untrusted input and a truncated datagram must become a counted refusal,
    /// never an exception that unwinds a FishNet receive callback.
    /// </summary>
    public static class PfeReplicationCodec
    {
        public static byte[] Encode(in SimReplicationEnvelope envelope)
        {
            return SimReplicationEnvelopeSerializer.Serialize(envelope);
        }

        public static bool TryDecode(byte[] bytes, out SimReplicationEnvelope envelope)
        {
            envelope = default;
            if (bytes == null || bytes.Length == 0) return false;

            try
            {
                envelope = SimReplicationEnvelopeSerializer.Deserialize(bytes);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException
                                       || ex is InvalidDataException || ex is OverflowException)
            {
                return false;
            }

            return PfeReplicationWireRules.IsAdmissible(envelope);
        }
    }

    /// <summary>
    /// Pure peer-id → connection-id routing for the host side of <see cref="ISimReplicationWire"/>.
    ///
    /// <para><b>The seam has no connection handle.</b> <c>ISimReplicationWire.Send(envelope)</c> was
    /// deliberately kept transport-neutral, so the only thing a host knows about its recipient is
    /// <c>envelope.PeerId</c>. A join request, however, arrives from a connection that has no peer id
    /// yet — the accept and the first baseline chunks must go back to <i>that sender</i>. The router
    /// therefore has two modes: while a receive is in progress the reply connection wins; outside a
    /// receive the peer binding wins.</para>
    ///
    /// <para><b>Why not broadcast to everyone and let clients filter.</b> A baseline chunk addressed to
    /// one peer would then consume another peer's ordered envelope sequence space, and its receiver
    /// would refuse every subsequent snapshot. Targeted delivery is a correctness requirement, not an
    /// optimisation.</para>
    /// </summary>
    public sealed class PfeReplicationRouter
    {
        private readonly Dictionary<uint, int> _connectionByPeer = new Dictionary<uint, int>();
        private readonly Dictionary<int, uint> _peerByConnection = new Dictionary<int, uint>();

        private int _replyConnectionId = -1;
        private bool _inReceive;

        public int BoundPeerCount => _connectionByPeer.Count;
        public int ReplyConnectionId => _replyConnectionId;
        public bool InReceive => _inReceive;

        /// <summary>Opens a receive scope. Nested receives are refused: a broadcast callback that
        /// recursively delivered another packet would otherwise overwrite the outer sender.</summary>
        public bool BeginReceive(int connectionId)
        {
            if (_inReceive || connectionId < 0) return false;
            _replyConnectionId = connectionId;
            _inReceive = true;
            return true;
        }

        public void EndReceive()
        {
            _inReceive = false;
            _replyConnectionId = -1;
        }

        /// <summary>Binds an admitted peer id to the connection its join came from. One-to-one: a
        /// second join on the same connection, or a second connection claiming a live peer, is
        /// refused rather than silently stealing the peer's stream.</summary>
        public bool Bind(uint peerId, int connectionId)
        {
            if (peerId == 0u || connectionId < 0) return false;
            if (_connectionByPeer.ContainsKey(peerId)) return false;
            if (_peerByConnection.ContainsKey(connectionId)) return false;

            _connectionByPeer.Add(peerId, connectionId);
            _peerByConnection.Add(connectionId, peerId);
            return true;
        }

        public bool Unbind(int connectionId)
        {
            uint peerId;
            if (!_peerByConnection.TryGetValue(connectionId, out peerId)) return false;
            _peerByConnection.Remove(connectionId);
            _connectionByPeer.Remove(peerId);
            return true;
        }

        public bool TryGetPeer(int connectionId, out uint peerId)
        {
            return _peerByConnection.TryGetValue(connectionId, out peerId);
        }

        public bool TryResolve(in SimReplicationEnvelope envelope, out int connectionId)
        {
            if (_inReceive)
            {
                connectionId = _replyConnectionId;
                return connectionId >= 0;
            }

            connectionId = -1;
            if (envelope.PeerId == 0u) return false;
            return _connectionByPeer.TryGetValue(envelope.PeerId, out connectionId);
        }
    }

    /// <summary>
    /// View-side reassembly of a baseline's chunks.
    ///
    /// <para><b>Why this exists when <c>SimReplicationClient</c> already reassembles.</b> The pure
    /// client consumes a baseline to build its own receiver state and returns <c>void</c>; it does
    /// not hand the assembled <c>SimSnapshot</c> back. The view needs that snapshot to paint the
    /// baseline's tile journal once, so it has to reassemble the same bytes. Doing it here rather
    /// than by reflection keeps the accumulator's rules executable offline.</para>
    ///
    /// <para><b>Late parts are ignored, not stored.</b> Once a lifetime is whole, an unknown index
    /// would otherwise start an orphan baseline that overwrites the good result on the next chunk —
    /// the same rule <c>SimReplicationClient.ReceiveBaselineChunk</c> enforces.</para>
    /// </summary>
    public sealed class PfeBaselineViewAssembler
    {
        private readonly List<SimBaselineChunk> _chunks = new List<SimBaselineChunk>(8);
        private byte[] _assembled;
        private long _revision;
        private bool _hasRevision;
        private bool _assemblyPending;

        public int ChunkCount => _chunks.Count;
        public bool IsWhole => _assembled != null;
        public long Revision => _revision;
        public int RefusedStaleCount { get; private set; }
        public SimBaselineAssemblyResult LastResult { get; private set; }

        /// <summary>Adds one chunk. Returns true exactly once, when the lifetime becomes whole.</summary>
        public bool Add(SimBaselineChunk chunk)
        {
            if (chunk.ChunkCount <= 0 || chunk.ChunkIndex < 0 || chunk.ChunkIndex >= chunk.ChunkCount)
            {
                LastResult = SimBaselineAssemblyResult.InconsistentCount;
                return false;
            }

            // Mirror SimReplicationClient.ReceiveBaselineChunk exactly: a HIGHER revision opens a
            // new lifetime, a LOWER one is a stale part. Using != here would let a single stale
            // chunk clear a completed assembly and start an orphan baseline around it.
            if (!_hasRevision)
            {
                _hasRevision = true;
                _revision = chunk.WorldRevision;
            }
            else if (chunk.WorldRevision > _revision)
            {
                _revision = chunk.WorldRevision;
                _chunks.Clear();
                _assembled = null;
                _assemblyPending = false;
            }
            else if (chunk.WorldRevision < _revision)
            {
                RefusedStaleCount++;
                return false;
            }

            if (_assembled != null) return false;

            for (int i = 0; i < _chunks.Count; i++)
            {
                if (_chunks[i].ChunkIndex != chunk.ChunkIndex) continue;
                // Same index with different bytes is corruption. Silence is the safe response;
                // TryAssemble will still refuse the whole set if it ever sees both.
                if (!_chunks[i].Equals(chunk)) LastResult = SimBaselineAssemblyResult.InconsistentCount;
                return false;
            }

            _chunks.Add(chunk);
            if (_chunks.Count < chunk.ChunkCount) return false;

            byte[] payload;
            if (!SimReplicationBaselineRules.TryAssemble(_chunks, out payload, out SimBaselineAssemblyResult result))
            {
                LastResult = result;
                return false;
            }

            LastResult = result;
            _assembled = payload;
            _assemblyPending = true;
            return true;
        }

        /// <summary>Takes the newly assembled payload exactly once.</summary>
        public bool TryTakeAssembly(out byte[] payload)
        {
            payload = null;
            if (!_assemblyPending || _assembled == null) return false;
            payload = _assembled;
            _assemblyPending = false;
            return true;
        }
    }
}
