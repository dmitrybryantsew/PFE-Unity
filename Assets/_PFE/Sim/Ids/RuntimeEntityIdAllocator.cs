using System;

namespace PFE.Core.Ids
{
    /// <summary>
    /// Phase 3.2 (F-05): the host-side state behind <see cref="RuntimeEntityIdRules"/>. Pure C#, no
    /// Unity dependency — this class is the thing a replay re-runs, so it must be constructible in an
    /// offline fixture.
    ///
    /// <para><b>Host-assigned, not caller-assigned.</b> Before this phase, every runtime spawner owned
    /// a private static serial (<c>_serial++</c>, <c>_scytheSerial++</c>,
    /// <c>_reinforcementSerial++</c>). Those survive across room rebuilds and replays, so the same
    /// replayed tick sequence produced different ids on the second run. All spawners now ask this one
    /// allocator, which is reset exactly once per run.</para>
    ///
    /// <para><b>Not thread-safe.</b> The simulation is Unity main-thread only. A lock here would add a
    /// cost to every spawn and would hide, rather than fix, an off-thread spawn.</para>
    /// </summary>
    public sealed class RuntimeEntityIdAllocator
    {
        private long _lastTickIndex = RuntimeEntityIdRules.NoActiveTick;
        private int _counter;
        private long _allocatedCount;

        /// <summary>The largest tick index stamped so far; <c>NoActiveTick</c> before the first call.</summary>
        public long LastTickIndex => _lastTickIndex;

        /// <summary>The counter the <b>next</b> allocation will use. Monotonic across the whole run.</summary>
        public int Counter => _counter;

        /// <summary>How many ids this run has minted. Diagnostics and the replay fixtures.</summary>
        public long AllocatedCount => _allocatedCount;

        /// <summary>
        /// Start a new run. Called at session bootstrap and by the replay fixtures; a second run on the
        /// same instance produces the same ids as the first.
        /// </summary>
        public void Reset()
        {
            _lastTickIndex = RuntimeEntityIdRules.NoActiveTick;
            _counter = 0;
            _allocatedCount = 0;
        }

        /// <summary>
        /// Mint the next host-assigned runtime id for <paramref name="tickIndex"/>. The tick is a
        /// <i>stamp</i>, not a scope: two allocations in one tick differ by the counter, and two
        /// allocations in different ticks also differ by the counter.
        /// </summary>
        public EntityId Allocate(long tickIndex)
        {
            if (_counter == int.MaxValue)
            {
                throw new InvalidOperationException(
                    "RuntimeEntityIdAllocator exhausted its counter for this run. Reset the allocator " +
                    "at the session boundary; do not let the counter wrap into negative ids.");
            }

            long effective = RuntimeEntityIdRules.EffectiveTickIndex(tickIndex);
            EntityId id = RuntimeEntityIdRules.IdFor(effective, _counter);

            _lastTickIndex = RuntimeEntityIdRules.AdvanceTick(_lastTickIndex, effective);
            _counter = RuntimeEntityIdRules.NextCounter(_counter);
            _allocatedCount++;
            return id;
        }
    }
}
