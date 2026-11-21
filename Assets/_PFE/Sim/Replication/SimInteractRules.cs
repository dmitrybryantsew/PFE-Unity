using PFE.Core.Input;

namespace PFE.Sim.Replication
{
    /// <summary>
    /// One well-formed "open this container" request, extracted from a
    /// <see cref="PlayerCommand"/>'s interact edge.
    ///
    /// <para><b>Why a separate value type rather than three out-parameters.</b> The host-side sink is
    /// implemented in the view assembly, and three loose parameters invite a call site that passes
    /// them in the wrong order — a room hash and a spawn index are both "an id", and swapping them
    /// would compile. A record with named fields cannot be mis-ordered.</para>
    /// </summary>
    public readonly struct SimInteractRequest
    {
        public SimInteractRequest(int tickIndex, ulong roomHash, int objectIndex)
        {
            TickIndex = tickIndex;
            RoomHash = roomHash;
            ObjectIndex = objectIndex;
        }

        /// <summary>The tick the edge belongs to. Not a de-duplication key on its own — the redundancy
        /// window can repeat a tick only as a duplicate command, which the ingress already refuses to
        /// pass on — but it is what makes a log line or a refusal traceable to one frame.</summary>
        public int TickIndex { get; }

        /// <summary>The target room, as <c>SimSnapshotProjectionRules.RoomKey</c> hashes it.</summary>
        public ulong RoomHash { get; }

        /// <summary>The target container's <c>ObjectInstance.spawnIndex</c>.</summary>
        public int ObjectIndex { get; }

        public override string ToString()
        {
            return $"SimInteractRequest(tick={TickIndex} room={RoomHash:X16} object={ObjectIndex})";
        }
    }

    /// <summary>
    /// The host-side consumer of admitted interact edges. The view layer implements this to run the
    /// authoritative open — roll the contents, spawn them, and write <c>lootState</c> so the next
    /// projection carries it. Keeping it as one method means the pure layer never learns what a
    /// <c>MonoBehaviour</c>, a room or an inventory is: the same seam shape as
    /// <see cref="ISimCommandSink"/> and <see cref="ISimRoomTransitionSink"/>.
    ///
    /// <para><b>What the implementation owes.</b> Resolve the room and the object, refuse the request
    /// if either is missing or the container is already opened, and refuse it if the peer is not in
    /// that room — a peer that names a room it is not standing in is asking the host to loot a chest
    /// on the other side of the world. None of that can live here: the pure layer has no room graph,
    /// no peer position and no content table.</para>
    /// </summary>
    public interface ISimInteractSink
    {
        /// <summary>One admitted, well-formed interact edge. <paramref name="slot"/> is the
        /// host-assigned controller slot for the peer, i.e. the body that should be doing the
        /// opening.</summary>
        void Apply(uint peerId, byte slot, in SimInteractRequest request);
    }

    /// <summary>
    /// Extracts interact requests from an admitted command window. Pure C# so the whole "which edge
    /// actually fires, and how many times" question can be executed offline — it is the one part of
    /// the container path that is a rule rather than wiring.
    ///
    /// <para><b>The bug this class exists to prevent.</b> A command batch is a <i>redundancy window</i>:
    /// it re-sends the last N commands every time, so the host may see the same interact edge in up to
    /// N consecutive batches. A sink driven from the whole window would therefore open the chest once
    /// per resend — the same double-loot the missing flag caused, reintroduced by the transport. Only
    /// the window's fresh suffix may fire, which is why every entry point takes
    /// <c>firstNewIndex</c>.</para>
    ///
    /// <para><b>Two edges in one window are two requests.</b> A player can press interact on tick 40
    /// and again on tick 43, and a window of ticks 38..45 contains both. The second press must not be
    /// swallowed by the first: the loop reports every fresh edge, and the sink decides (the second
    /// press on an already-opened container is refused there, not here).</para>
    /// </summary>
    public static class SimInteractRules
    {
        /// <summary>
        /// Whether one command carries a usable interact request.
        ///
        /// <para><b>The room hash is the presence signal, not the object index.</b> Index 0 is a
        /// legitimate container, so an index-based "absent" sentinel would make the first container in
        /// every room unaddressable. A zero room hash cannot be a real room —
        /// <c>SimSnapshotProjectionRules.RoomKey</c> prefixes the id precisely so it is never zero —
        /// so it is the only unambiguous "no target".</para>
        /// </summary>
        public static bool TryRead(in PlayerCommand command, out SimInteractRequest request)
        {
            if (!command.InteractPressed || command.TargetRoomHash == 0UL || command.TargetObjectIndex < 0)
            {
                request = default;
                return false;
            }

            request = new SimInteractRequest(
                command.TickIndex, command.TargetRoomHash, command.TargetObjectIndex);
            return true;
        }

        /// <summary>
        /// Deliver every fresh interact edge in an admitted window to <paramref name="sink"/>, in tick
        /// order. Returns how many were delivered — a readback, because a sink that is never called
        /// looks exactly like a player who never pressed the key.
        ///
        /// <para><b><paramref name="firstNewIndex"/> is honoured, never assumed.</b> The ingress already
        /// proved the window's prefix is a byte-identical repeat of commands it applied earlier, so
        /// re-firing an edge from that prefix is not a re-delivery the sink can detect — the command is
        /// the same value. Filtering here is the only place the duplication can be removed.</para>
        /// </summary>
        public static int Dispatch(PlayerCommand[] commands, int firstNewIndex, uint peerId, byte slot,
            ISimInteractSink sink)
        {
            if (sink == null || commands == null || firstNewIndex < 0 || firstNewIndex >= commands.Length)
            {
                return 0;
            }

            int delivered = 0;
            for (int i = firstNewIndex; i < commands.Length; i++)
            {
                if (!TryRead(commands[i], out SimInteractRequest request)) continue;

                sink.Apply(peerId, slot, in request);
                delivered++;
            }

            return delivered;
        }
    }
}
