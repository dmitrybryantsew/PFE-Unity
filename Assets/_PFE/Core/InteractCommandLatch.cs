namespace PFE.Core
{
    /// <summary>
    /// The one-frame carrier between a <b>suppressed</b> container interact and the next
    /// <c>PlayerCommand</c> build. When a joined client presses the action key on a container, the
    /// local open does not run (the host is authoritative — running it here would roll and spawn a
    /// second copy of the chest); the press instead has to ride the command channel as the
    /// <c>InteractPressed</c> edge plus its target ids.
    ///
    /// <para><b>Why a latch and not a parameter.</b> The two endpoints live on opposite sides of a
    /// frame boundary: the suppression fires inside <c>ContainerPropPresenter.Interact</c> — which the
    /// E-key path, the click path AND the hold-completion path all reach, in Update or in a SimTick —
    /// while the command is built once per Update in <c>PlayerController</c>. The press is therefore
    /// recorded here and consumed by the next command build; the command tape's per-tick fold keeps
    /// exactly-once delivery, and a press that no build ever consumes (no player, no locomotion) is
    /// dropped, which is the same answer a lost press gets.</para>
    ///
    /// <para><b>Single producer, single consumer, main thread only</b> — the process has exactly one
    /// local player, like the rest of the ambient carriers this project already uses.</para>
    /// </summary>
    public static class SimInteractCommandLatch
    {
        private static bool _pending;
        private static ulong _roomHash;
        private static int _objectIndex;

        /// <summary>Record one suppressed container interact. A second raise before consumption
        /// overwrites the first: one frame carries one press, and the edge is what matters, not the
        /// count.</summary>
        public static void Raise(ulong roomHash, int objectIndex)
        {
            _pending = true;
            _roomHash = roomHash;
            _objectIndex = objectIndex;
        }

        /// <summary>Take the pending edge, if any. Exactly one consumer gets it.</summary>
        public static bool TryConsume(out ulong roomHash, out int objectIndex)
        {
            if (!_pending)
            {
                roomHash = 0UL;
                objectIndex = 0;
                return false;
            }

            _pending = false;
            roomHash = _roomHash;
            objectIndex = _objectIndex;
            return true;
        }

        /// <summary>Drop any pending edge. Test hook and the session-teardown case.</summary>
        public static void Reset()
        {
            _pending = false;
            _roomHash = 0UL;
            _objectIndex = 0;
        }
    }
}
