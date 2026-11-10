using System;

namespace PFE.Core.Ids
{
    /// <summary>
    /// Phase 3.2 (F-05): the <b>policy</b> behind host-assigned runtime entity ids, kept as a
    /// Unity-free pure decision class so the replay acceptance can be executed offline.
    ///
    /// <para><b>The claim.</b> Row 3.2's acceptance is "1 000 replayed ticks produce identical ID
    /// sets on two runs". That is a claim about an <i>allocation policy</i>, not about
    /// <see cref="EntityId.CreateRuntime"/> being a pure function — the existing
    /// <c>SpawnSequence_IsDeterministic</c> fixture already proves the latter and would pass even if
    /// every runtime spawner kept its own process-lifetime serial. The policy here is what makes the
    /// spawner's <i>sequence</i> reproducible.</para>
    ///
    /// <para><b>One monotonic counter for the whole run, stamped with the host tick.</b> The tempting
    /// alternative is a counter that resets at every tick boundary. That is wrong for two reasons:
    /// spawns also happen outside a tick (Awake, console commands, load, event handlers) and would
    /// then land on the same <c>(tick, counter)</c> pair as an in-tick spawn; and a client prediction
    /// loop stepping the same tick index twice would mint the same id twice. A monotonic counter makes
    /// every allocation unique within a run, while the tick stamp keeps the id traceable to the host
    /// tick that created it (Phase 3.3's lifecycle sequencing needs that).</para>
    ///
    /// <para><b>The ambient tick never rewinds.</b> Several <c>SimLoop</c> instances can exist in a
    /// process (the two-loop determinism fixtures build two). If each wrote its own index straight
    /// into the ambient tick, interleaved ticks would look like a replay and rewind the allocator.
    /// <see cref="AdvanceTick"/> therefore takes the maximum. A replay is an explicit
    /// <see cref="RuntimeEntityIdAllocator.Reset"/>, never an inference from a backwards number.</para>
    /// </summary>
    public static class RuntimeEntityIdRules
    {
        /// <summary>No tick has been observed yet (bootstrap, Awake, save/load, console).</summary>
        public const long NoActiveTick = -1L;

        /// <summary>The tick a spawn is stamped with when no host tick has run yet.</summary>
        public const long BootstrapTickIndex = 0L;

        /// <summary>The prefix of a host-assigned runtime id's debug string.</summary>
        public const string RuntimeIdPrefix = "runtime:";

        /// <summary>
        /// Clamp a tick index into the range <see cref="EntityId.CreateRuntime"/> accepts. Negative
        /// means "no tick active", not "the minus-first tick".
        /// </summary>
        public static long EffectiveTickIndex(long tickIndex)
        {
            return tickIndex < BootstrapTickIndex ? BootstrapTickIndex : tickIndex;
        }

        /// <summary>
        /// Advance the ambient host tick without ever rewinding it. See the class remarks for why a
        /// straight assignment would corrupt replays with more than one <c>SimLoop</c> in the process.
        /// </summary>
        public static long AdvanceTick(long current, long observed)
        {
            return observed > current ? observed : current;
        }

        /// <summary>
        /// The counter is monotonic across the entire run. It is deliberately <b>not</b> per-tick.
        /// </summary>
        public static int NextCounter(int counter)
        {
            return unchecked(counter + 1);
        }

        /// <summary>The one place that turns (tick, counter) into an id.</summary>
        public static EntityId IdFor(long tickIndex, int counter)
        {
            return EntityId.CreateRuntime(EffectiveTickIndex(tickIndex), counter);
        }

        /// <summary>True for ids minted by this policy, including ids assigned to empty-ID records.</summary>
        public static bool IsRuntimeId(EntityId id)
        {
            return id.IsValid
                   && id.DebugString != null
                   && id.DebugString.StartsWith(RuntimeIdPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether a registration may replace the entity already held under an id. Two <i>different</i>
        /// live records claiming one id is a collision, not an overwrite: silently replacing would hide
        /// the exact bug F-05 is about (a replay producing a different id for the same entity).
        /// Re-registering the <i>same</i> object is idempotent and legal — <c>RefreshAll</c> is called
        /// on room entry, not only on spawn.
        /// </summary>
        public static bool ShouldReplace(object existing, object candidate)
        {
            return existing == null || ReferenceEquals(existing, candidate);
        }

        // ---------------------------------------------------------------------------------------
        // Phase 3.3 (F-06) — the lifetime half of an id.
        //
        // Row 3.2 gave every live entity an id. An id alone cannot answer "is this packet about the
        // entity I am holding, or about a previous occupant of the same id?", because a room-spawn id
        // (`room:…`) is a *stable string* that the same map slot keeps across every room re-entry.
        // Two numbers answer it, and they answer different questions:
        //
        //   generation        — how many distinct occupants the id has had. Bumped only when a
        //                       *different record object* claims an id the previous occupant left.
        //   lifecycleSequence — the id's own spawn/despawn event counter, continued across a
        //                       same-record re-registration so a respawn is strictly newer than the
        //                       despawn that preceded it.
        //
        // Without the generation bump, a snapshot captured before a room transition would be
        // indistinguishable from one captured after it, and `SimStalenessRules.AcceptLifetime` would
        // accept it — the entity would appear at its pre-transition position with its pre-transition
        // health. That is the concrete failure the readiness plan's F-06 names.
        // ---------------------------------------------------------------------------------------

        /// <summary>The generation of an id that has never been recycled.</summary>
        public const int FirstGeneration = 0;

        /// <summary>The lifecycle counter a first registration receives. Zero means "never registered".</summary>
        public const int FirstLifecycleSequence = 1;

        /// <summary>The lifecycle event counter's successor. A spawn, a despawn and a respawn are 1, 2, 3.</summary>
        public static int NextLifecycleSequence(int current)
        {
            return unchecked(current + 1);
        }

        /// <summary>The generation's successor: the id has been taken over by a different record.</summary>
        public static int NextGeneration(int current)
        {
            return unchecked(current + 1);
        }

        /// <summary>
        /// The lifetime a registration receives, given what the id's previous occupant left behind.
        ///
        /// <para><b>Three cases, and the third is the one that matters.</b> No previous record means a
        /// first spawn. The <i>same</i> record object re-registering is a room re-entry of the same
        /// entity: its lifecycle counter continues, so a respawn outranks the despawn before it. A
        /// <i>different</i> record object claiming the id is a new occupant and must bump the
        /// generation, even though it is reusing the same id string and even though its counter starts
        /// lower — comparing counters alone would read that as stale and freeze the entity.</para>
        /// </summary>
        /// <param name="hasPrevious">Whether this id was ever registered before.</param>
        /// <param name="previousGeneration">The generation the previous occupant left behind.</param>
        /// <param name="previousLifecycleSequence">The lifecycle counter the previous occupant left behind.</param>
        /// <param name="sameOwner">Whether the previous occupant is the very object registering now.</param>
        public static EntityLifetime NextLifetime(
            bool hasPrevious,
            int previousGeneration,
            int previousLifecycleSequence,
            bool sameOwner)
        {
            if (!hasPrevious)
            {
                return new EntityLifetime(FirstGeneration, FirstLifecycleSequence);
            }

            if (sameOwner)
            {
                return new EntityLifetime(
                    previousGeneration,
                    NextLifecycleSequence(previousLifecycleSequence));
            }

            return new EntityLifetime(
                NextGeneration(previousGeneration),
                FirstLifecycleSequence);
        }
    }

    /// <summary>
    /// A pair of counters describing <i>which lifetime</i> of an id a record belongs to. Ordered by
    /// <see cref="PFE.Sim.State.SimStalenessRules.AcceptLifetime"/>, never by a plain compare of the
    /// fields — see that method for why generation outranks the sequence.
    /// </summary>
    public readonly struct EntityLifetime
    {
        public readonly int Generation;
        public readonly int LifecycleSequence;

        public EntityLifetime(int generation, int lifecycleSequence)
        {
            Generation = generation;
            LifecycleSequence = lifecycleSequence;
        }

        public override string ToString()
        {
            return "g" + Generation + "/l" + LifecycleSequence;
        }
    }
}
