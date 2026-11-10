using System;
using System.Collections.Generic;

namespace PFE.Core.Input
{
    /// <summary>
    /// A fixed-capacity ring of the most recent <see cref="PlayerCommand"/>s for one slot.
    ///
    /// <para><b>Why a ring buffer.</b> The multiplayer plan (readiness §1.3, F-03/Phase 2.1) sends
    /// the <i>last N inputs</i> redundantly on the unreliable command channel, and a predicting
    /// client re-simulates from the last acked tick using its buffered commands (reconciliation,
    /// §10). Both need the same thing: cheap access to a bounded window of recent commands, oldest
    /// evicted first, with no per-tick allocation.</para>
    ///
    /// <para><b>Determinism note.</b> The buffer is pure C# — no Unity object, no wall time. The
    /// same sequence of <see cref="Enqueue"/> calls always yields the same contents, which is what
    /// lets two independent consumers (host authority + client prediction) replay a command tape
    /// identically.</para>
    /// </summary>
    public sealed class PlayerCommandBuffer
    {
        private readonly PlayerCommand[] _items;
        private int _head;   // index of the oldest live entry
        private int _count;

        public PlayerCommandBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Buffer capacity must be positive.");
            }

            _items = new PlayerCommand[capacity];
            _head = 0;
            _count = 0;
        }

        public int Capacity => _items.Length;
        public int Count => _count;

        /// <summary>
        /// Appends a command, evicting the oldest when full. Commands are expected to arrive in
        /// non-decreasing <see cref="PlayerCommand.TickIndex"/> order; the buffer does not sort.
        /// </summary>
        public void Enqueue(in PlayerCommand command)
        {
            if (_count < _items.Length)
            {
                int tail = (_head + _count) % _items.Length;
                _items[tail] = command;
                _count++;
            }
            else
            {
                // Overwrite the oldest and advance the head.
                _items[_head] = command;
                _head = (_head + 1) % _items.Length;
            }
        }

        /// <summary>
        /// Overwrites the most recently enqueued command <b>without adding an entry</b>. Used by
        /// <see cref="PlayerCommandTape"/> to fold several render frames of one sim tick into a single
        /// tape entry: a tick-aligned consumer must find exactly one command for a tick, and it must
        /// carry the edges from every frame of that tick.
        ///
        /// <para>The buffer stays pure and deterministic — this is an in-place update of the newest
        /// slot, not a new message. An empty buffer has no newest slot, so the first push falls back
        /// to <see cref="Enqueue"/>.</para>
        /// </summary>
        public void ReplaceLast(in PlayerCommand command)
        {
            if (_count == 0)
            {
                Enqueue(command);
                return;
            }

            int tail = (_head + _count - 1) % _items.Length;
            _items[tail] = command;
        }

        /// <summary>The most recently enqueued command, or false when empty.</summary>
        public bool TryGetLatest(out PlayerCommand command)
        {
            if (_count == 0)
            {
                command = default;
                return false;
            }

            int tail = (_head + _count - 1) % _items.Length;
            command = _items[tail];
            return true;
        }

        /// <summary>The command for an exact tick, when still buffered.</summary>
        public bool TryGetForTick(int tickIndex, out PlayerCommand command)
        {
            for (int i = 0; i < _count; i++)
            {
                int index = (_head + i) % _items.Length;
                if (_items[index].TickIndex == tickIndex)
                {
                    command = _items[index];
                    return true;
                }
            }

            command = default;
            return false;
        }

        /// <summary>
        /// Copies the live commands, oldest first, into <paramref name="destination"/>. Used to send
        /// the redundant last-N window on the wire and to rebuild a replay tape.
        /// </summary>
        public int CopyOldestFirst(PlayerCommand[] destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            int written = 0;
            for (int i = 0; i < _count && i < destination.Length; i++)
            {
                destination[i] = _items[(_head + i) % _items.Length];
                written++;
            }

            return written;
        }

        /// <summary>Enumerates live commands oldest-first without allocation.</summary>
        public IEnumerable<PlayerCommand> OldestFirst()
        {
            for (int i = 0; i < _count; i++)
            {
                yield return _items[(_head + i) % _items.Length];
            }
        }

        /// <summary>Drops every command at or before a tick (e.g. once the host has acked it).</summary>
        public void DiscardThrough(int tickIndex)
        {
            while (_count > 0 && _items[_head].TickIndex <= tickIndex)
            {
                _head = (_head + 1) % _items.Length;
                _count--;
            }
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
        }
    }
}
