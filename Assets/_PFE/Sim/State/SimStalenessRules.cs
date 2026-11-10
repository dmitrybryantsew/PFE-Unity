using System;

namespace PFE.Sim.State
{
    /// <summary>
    /// Phase 3.3 (F-06): the wrap-safe ordering and lifetime guards a replicated packet must pass
    /// before it may overwrite local state.
    ///
    /// <para><b>Why this is a pure class and not a few comparisons at the call sites.</b> The
    /// readiness plan's §10 rule table requires every replicated field group to have a
    /// reconciliation rule, and the one rule that is easy to get subtly wrong is the sequence
    /// compare. <c>incoming &gt; current</c> is wrong exactly when the <c>uint</c> wraps; the correct
    /// compare is the three-line signed-difference idiom used by Remains'
    /// <c>ActorMotionReplication.IsNewer</c>. Keeping it here means the offline wall can execute the
    /// wrap case, the stale case and the lifetime case without a transport, a socket or a
    /// <c>MonoBehaviour</c>.</para>
    ///
    /// <para><b>Three separate guards, because they answer three different questions.</b>
    /// <see cref="AcceptSnapshot"/> decides whether a whole snapshot is newer than the last one
    /// accepted. <see cref="AcceptLifetime"/> decides whether a spawn/despawn record belongs to the
    /// current lifetime of an entity. <see cref="AcceptProjectileProgress"/> decides whether a
    /// per-tick projectile sample is newer <i>within</i> one lifetime. Collapsing them into one
    /// comparator would make a wrapped motion sequence look like a new entity lifetime, or a
    /// re-spawn look like a stale tick.</para>
    /// </summary>
    public static class SimStalenessRules
    {
        /// <summary>
        /// Wrap-safe "is <paramref name="incoming"/> newer than <paramref name="current"/>?".
        ///
        /// <para>This is the exact three-line idiom the readiness doc §4.3 records from Remains:
        /// <c>unchecked((int)(incoming - current)) &gt; 0</c>. The subtraction is performed in
        /// <c>uint</c>, then reinterpreted as <c>int</c>; half the sequence space is "newer" and half
        /// is "older", so <c>0</c> after <c>uint.MaxValue</c> is correctly newer, while a duplicate
        /// sequence is not.</para>
        /// </summary>
        public static bool IsNewer(uint incoming, uint current)
        {
            return unchecked((int)(incoming - current)) > 0;
        }

        /// <summary>
        /// Whether a snapshot may replace the last accepted one. A newer <c>worldRevision</c> always
        /// wins: it names a different reliable-event lifetime, so its sequence space is fresh. Equal
        /// revisions are ordered by the wrap-safe snapshot sequence. An older revision is stale no
        /// matter how new its sequence looks.
        /// </summary>
        public static bool AcceptSnapshot(
            uint incomingSequence,
            long incomingWorldRevision,
            uint currentSequence,
            long currentWorldRevision)
        {
            if (incomingWorldRevision < currentWorldRevision) return false;
            if (incomingWorldRevision > currentWorldRevision) return true;
            return IsNewer(incomingSequence, currentSequence);
        }

        /// <summary>
        /// Whether a spawn/despawn record belongs to a newer lifetime than the one already held.
        /// <c>generation</c> is the coarse lifetime (a destroyed-and-reused id), and
        /// <c>lifecycleSequence</c> is the fine spawn/despawn counter within it. A lower generation is
        /// stale even when its lifecycle counter is larger; an equal generation must have a strictly
        /// larger lifecycle counter.
        /// </summary>
        public static bool AcceptLifetime(
            int incomingGeneration,
            int incomingLifecycleSequence,
            int currentGeneration,
            int currentLifecycleSequence)
        {
            if (incomingGeneration != currentGeneration)
            {
                return incomingGeneration > currentGeneration;
            }

            return incomingLifecycleSequence > currentLifecycleSequence;
        }

        /// <summary>True when both records describe the same entity lifetime.</summary>
        public static bool IsSameLifetime(
            int incomingGeneration,
            int incomingLifecycleSequence,
            int currentGeneration,
            int currentLifecycleSequence)
        {
            return incomingGeneration == currentGeneration
                   && incomingLifecycleSequence == currentLifecycleSequence;
        }

        /// <summary>
        /// Whether a per-tick projectile sample may replace the last one. The sample is only meaningful
        /// inside one lifetime, so a different generation must be strictly newer; within the same
        /// generation the motion sequence is ordered with <see cref="IsNewer"/>. A duplicate sequence
        /// is rejected — an unreliable channel can deliver the same packet twice.
        /// </summary>
        public static bool AcceptProjectileProgress(
            int incomingGeneration,
            uint incomingSequence,
            int currentGeneration,
            uint currentSequence)
        {
            if (incomingGeneration != currentGeneration)
            {
                return incomingGeneration > currentGeneration;
            }

            return IsNewer(incomingSequence, currentSequence);
        }
    }
}
