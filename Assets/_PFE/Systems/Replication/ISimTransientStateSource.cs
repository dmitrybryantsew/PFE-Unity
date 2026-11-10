using PFE.Sim.State;

namespace PFE.Systems.Replication
{
    /// <summary>
    /// A live, transient entity the host replicates without a room record behind it — a fired round, a
    /// thrown object, a placed mine.
    ///
    /// <para><b>Why an interface rather than three lists in the projector.</b> All three already hold a
    /// host-minted <c>EntityId</c>, self-register in <c>RuntimeEntityIdContext</c> (they have no record
    /// for the record-keyed path to key on), and can describe one tick of themselves as a
    /// <see cref="SimProjectileState"/>. What they do not share is a base class — they are three
    /// unrelated <c>MonoBehaviour</c>s. Three parallel lists would need three sorts and three capture
    /// branches that must stay in lockstep; one list of this interface needs one of each, and the
    /// compiler is what keeps the three in lockstep.</para>
    ///
    /// <para><b>Unity-side by necessity.</b> The implementations are <c>MonoBehaviour</c>s, so this
    /// contract lives beside the projector rather than in <c>PFE.Sim</c> — the record it produces is
    /// pure and offline-testable, the thing that produces it is not.</para>
    /// </summary>
    public interface ISimTransientStateSource
    {
        /// <summary>
        /// The entity's id hash, used both as the record's identity and as the deterministic sort key.
        /// A <c>Dictionary</c> walk has unspecified order, and two hosts projecting the same state must
        /// emit byte-identical payloads or the state hash is a coin flip.
        /// </summary>
        ulong SimEntityHash { get; }

        /// <summary>
        /// Describe this entity's current tick. <paramref name="sequence"/> is the caller's progress
        /// counter — the wrap-safe ordering on the unreliable motion channel — and is deliberately the
        /// caller's rather than the entity's, so exactly one sample is taken per projection.
        /// </summary>
        SimProjectileState CaptureSimState(uint sequence);
    }
}
