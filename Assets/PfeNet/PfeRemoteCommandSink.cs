using System.Collections.Generic;
using PFE.Core.Input;
using PFE.Sim.Replication;

namespace PFE.Net
{
    /// <summary>
    /// The host-side consumer of the Phase 5 command uplink.
    ///
    /// <para><b>What this is, and what it is not.</b> It is the endpoint that proves a second player's
    /// intent crossed the socket: every admitted command window lands here with the peer's slot, and
    /// the session overlay reads it back. It is <b>not</b> a remote avatar. Feeding a
    /// <c>PlayerCommand</c> into the local sim requires a second <c>PlayerController</c> with its own
    /// motor/brain/entity id, and the project has no runtime player factory — <c>MapBridge.SpawnPlayer</c>
    /// only finds an already-authored, "Player"-tagged scene object. That is F-12 (remote motion and
    /// reconciliation) and remains owed; cloning a fully DI-wired MonoBehaviour at runtime would look
    /// like a second player while sharing half its state with the first.</para>
    ///
    /// <para><b>Why record rather than discard.</b> A sink that throws commands away leaves the
    /// ingress untestable from a running build: accepted batches would be indistinguishable from
    /// dropped ones. Keeping the newest command per slot gives the owner's play-test a visible
    /// end-to-end signal and gives F-12 a concrete place to bind an avatar later.</para>
    /// </summary>
    public sealed class PfeRemoteCommandSink : ISimCommandSink
    {
        private readonly Dictionary<byte, PlayerCommand> _latestBySlot =
            new Dictionary<byte, PlayerCommand>();
        private readonly Dictionary<uint, byte> _slotByPeer = new Dictionary<uint, byte>();

        public int AppliedBatches { get; private set; }
        public int AppliedCommands { get; private set; }
        public int KnownSlotCount => _latestBySlot.Count;

        public void Apply(uint peerId, byte slot, PlayerCommand[] commands, int firstNewIndex)
        {
            if (commands == null || firstNewIndex < 0 || firstNewIndex >= commands.Length) return;

            _slotByPeer[peerId] = slot;

            int fresh = 0;
            for (int i = firstNewIndex; i < commands.Length; i++)
            {
                PlayerCommand command = commands[i];
                // The ingress guarantees a single slot per batch, but re-stamping here keeps the
                // recorded command bound to slot the host actually admitted.
                command.SlotIndex = slot;
                _latestBySlot[slot] = command;
                fresh++;
            }

            AppliedBatches++;
            AppliedCommands += fresh;
        }

        public bool TryGetLatest(byte slot, out PlayerCommand command)
        {
            return _latestBySlot.TryGetValue(slot, out command);
        }

        public bool TryGetSlotForPeer(uint peerId, out byte slot)
        {
            return _slotByPeer.TryGetValue(peerId, out slot);
        }
    }
}
