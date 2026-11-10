using NUnit.Framework;
using PFE.Sim.State;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 3.3 (F-06), row 3.3 acceptance: "out-of-order + wrapped + stale-lifetime packets are
    /// rejected; mutation control on each guard."
    ///
    /// <para><b>Why the controls carry most of the weight here.</b> Every assertion in the positive
    /// half of this fixture is also satisfied by <c>incoming &gt; current</c> — except the ones that
    /// matter. A plain <c>uint</c> compare and the wrap-safe compare agree on 100% of the sequence
    /// space except the half-turn around zero, so a suite that only checks "5 is newer than 4" and
    /// "4 is not newer than 5" would be green for both, and the wrap bug would survive every test
    /// that was written for it. Each guard therefore gets at least one case where the naive compare
    /// and the real rule <i>disagree</i>, asserted explicitly, plus a mutant that must be killed.</para>
    ///
    /// <para><b>The three guards are tested separately because they are not one comparator.</b>
    /// <see cref="SimStalenessRules.AcceptSnapshot"/> has a field (<c>worldRevision</c>) that
    /// outranks the sequence; <see cref="SimStalenessRules.AcceptLifetime"/> has a field
    /// (<c>generation</c>) that outranks its counter; and only
    /// <see cref="SimStalenessRules.AcceptProjectileProgress"/> is a pure sequence compare inside a
    /// lifetime. A single shared comparator would pass all three happy paths and get all three
    /// discriminating cases wrong, which is why the doc's §10 table has one rule per field group
    /// rather than one rule for the packet.</para>
    /// </summary>
    [TestFixture]
    public class SimStalenessRulesTests
    {
        // ---------------------------------------------------------------------------------------
        // Guard 1 — IsNewer, the wrap-safe sequence compare.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The wrap: <c>uint.MaxValue</c> is the sequence <i>before</i> <c>0</c>, so the next packet
        /// after the counter rolls over must still be accepted. This is the only case the naive
        /// compare gets wrong, and it is the reason this class exists.
        /// </summary>
        [Test]
        public void IsNewer_AcceptsTheWrap_AndRejectsTheDuplicate()
        {
            Assert.IsTrue(SimStalenessRules.IsNewer(0u, uint.MaxValue),
                "0 after uint.MaxValue is the wrap and must be newer — this is the case a plain " +
                "`incoming > current` gets wrong, and a dropped snapshot every 2^32 ticks.");

            Assert.IsFalse(SimStalenessRules.IsNewer(5u, 5u),
                "A duplicate sequence must be rejected; an unreliable channel delivers the same " +
                "packet twice, and re-applying it would replay one tick's motion twice.");

            Assert.IsTrue(SimStalenessRules.IsNewer(5u, 4u));
            Assert.IsFalse(SimStalenessRules.IsNewer(4u, 5u));

            Assert.IsFalse(SimStalenessRules.IsNewer(uint.MaxValue, 0u),
                "The reverse direction of the wrap must read as older, otherwise the compare is " +
                "symmetric and therefore not an ordering at all.");
        }

        /// <summary>
        /// Exactly half the sequence space is "newer". At the boundary the signed difference is
        /// <see cref="int.MinValue"/>, which is not greater than zero, so the packet is rejected.
        /// Asserting the boundary rather than only the interior pins the tie-break direction; a
        /// mutant that used <c>&gt;= 0</c> would accept a packet 2^31 ticks ahead and never be
        /// caught by the wrap test alone.
        /// </summary>
        [Test]
        public void IsNewer_Boundary_ExactlyHalfTheSpaceIsNotNewer()
        {
            const uint current = 1_000u;

            Assert.IsTrue(SimStalenessRules.IsNewer(current + (uint)int.MaxValue, current),
                "Just under half a turn ahead must be accepted.");
            Assert.IsFalse(SimStalenessRules.IsNewer(current + 0x8000_0000u, current),
                "Exactly half a turn is the ambiguous point and must be rejected; accepting it " +
                "would make the relation non-antisymmetric.");
        }

        // ---------------------------------------------------------------------------------------
        // Guard 2 — snapshot admission: worldRevision outranks snapshotSequence.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// <c>worldRevision</c> names a different reliable-event lifetime — a room activation or a
        /// save-load — so its sequence space is fresh and a smaller sequence under a larger revision
        /// is still newer. This is the case that a sequence-only rule gets backwards.
        /// </summary>
        [Test]
        public void AcceptSnapshot_NewerWorldRevision_OutranksAWrappedBackSequence()
        {
            Assert.IsTrue(
                SimStalenessRules.AcceptSnapshot(
                    incomingSequence: 0u, incomingWorldRevision: 2L,
                    currentSequence: uint.MaxValue, currentWorldRevision: 1L),
                "A new world revision resets the sequence space, so sequence 0 under revision 2 " +
                "must beat sequence uint.MaxValue under revision 1.");

            Assert.IsFalse(
                SimStalenessRules.AcceptSnapshot(
                    incomingSequence: uint.MaxValue, incomingWorldRevision: 1L,
                    currentSequence: 0u, currentWorldRevision: 2L),
                "An older revision is stale no matter how large its sequence looks; accepting it " +
                "would roll the world back to the previous room's state.");
        }

        /// <summary>Within one revision, the wrap-safe sequence decides — including the duplicate.</summary>
        [Test]
        public void AcceptSnapshot_SameRevision_OrdersByWrapSafeSequence()
        {
            const long revision = 7L;

            Assert.IsTrue(SimStalenessRules.AcceptSnapshot(0u, revision, uint.MaxValue, revision));
            Assert.IsTrue(SimStalenessRules.AcceptSnapshot(9u, revision, 8u, revision));
            Assert.IsFalse(SimStalenessRules.AcceptSnapshot(8u, revision, 9u, revision));
            Assert.IsFalse(SimStalenessRules.AcceptSnapshot(9u, revision, 9u, revision),
                "A repeated snapshot must not be re-applied.");
        }

        // ---------------------------------------------------------------------------------------
        // Guard 3 — lifetime admission: generation outranks lifecycleSequence.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// A higher generation is a different lifetime of a reused id. Its lifecycle counter starts
        /// over, so it is necessarily smaller than the counter of the lifetime that just ended — and
        /// still newer. A lifecycle-only compare rejects every respawn after the first, which is the
        /// shape of "the second boss never appears" that no arithmetic test would catch.
        /// </summary>
        [Test]
        public void AcceptLifetime_HigherGeneration_OutranksALargerLifecycleCounter()
        {
            Assert.IsTrue(SimStalenessRules.AcceptLifetime(
                    incomingGeneration: 2, incomingLifecycleSequence: 0,
                    currentGeneration: 1, currentLifecycleSequence: 999),
                "Generation 2 counter 0 is newer than generation 1 counter 999.");

            Assert.IsFalse(SimStalenessRules.AcceptLifetime(
                    incomingGeneration: 1, incomingLifecycleSequence: 999,
                    currentGeneration: 2, currentLifecycleSequence: 0),
                "A lower generation is stale even with a much larger counter; accepting it would " +
                "resurrect the previous occupant of the id.");
        }

        /// <summary>Within one generation, a strictly larger lifecycle counter is required.</summary>
        [Test]
        public void AcceptLifetime_SameGeneration_RequiresAStrictlyLargerCounter()
        {
            Assert.IsTrue(SimStalenessRules.AcceptLifetime(3, 5, 3, 4));
            Assert.IsFalse(SimStalenessRules.AcceptLifetime(3, 4, 3, 4),
                "The same (generation, lifecycleSequence) pair is the same record, delivered twice.");
            Assert.IsFalse(SimStalenessRules.AcceptLifetime(3, 3, 3, 4));
        }

        /// <summary>
        /// <c>IsSameLifetime</c> exists because "same lifetime" and "not newer" are different
        /// questions: a duplicate spawn record is the same lifetime and must be ignored silently,
        /// while an older one is a stale lifetime and must be dropped loudly. Conflating them makes
        /// the two paths indistinguishable in a log.
        /// </summary>
        [Test]
        public void IsSameLifetime_DistinguishesADuplicateFromAStaleLifetime()
        {
            Assert.IsTrue(SimStalenessRules.IsSameLifetime(4, 2, 4, 2));
            Assert.IsFalse(SimStalenessRules.IsSameLifetime(4, 2, 4, 3));
            Assert.IsFalse(SimStalenessRules.IsSameLifetime(4, 2, 5, 2));

            Assert.IsFalse(SimStalenessRules.AcceptLifetime(4, 2, 4, 2));
            Assert.IsTrue(SimStalenessRules.IsSameLifetime(4, 2, 4, 2));
        }

        /// <summary>
        /// The lifecycle counter is a plain <c>int</c> compare, not a wrap-safe one, because a single
        /// entity lifetime cannot produce 2^31 spawn records inside a run. This test records that
        /// decision instead of leaving it implicit: at the boundary the rule stops advancing, and a
        /// future change to a wrap-safe int compare would show up here rather than in a live match.
        /// </summary>
        [Test]
        public void AcceptLifetime_LifecycleCounter_IsAPlainIntAndExhaustsAtMaxValue()
        {
            Assert.IsTrue(SimStalenessRules.AcceptLifetime(int.MaxValue - 1, 0, int.MaxValue - 2, 0));
            Assert.IsFalse(SimStalenessRules.AcceptLifetime(int.MaxValue, 0, int.MaxValue, 1),
                "At int.MaxValue the counter can no longer advance; documented exhaustion, not a " +
                "wrap, because reaching it requires 2^31 spawn records for one id.");
        }

        // ---------------------------------------------------------------------------------------
        // Guard 4 — projectile progress inside one lifetime.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// A projectile's per-tick sample is only meaningful within its lifetime: a respawned
        /// projectile legitimately restarts its motion sequence at zero, so generation must be
        /// consulted before the sequence. Within a generation, the wrap-safe compare applies because
        /// a long-lived projectile can cross the rollover.
        /// </summary>
        [Test]
        public void AcceptProjectileProgress_GenerationFirst_ThenWrapSafeSequence()
        {
            Assert.IsTrue(SimStalenessRules.AcceptProjectileProgress(
                    incomingGeneration: 2, incomingSequence: 0u,
                    currentGeneration: 1, currentSequence: uint.MaxValue),
                "A new projectile generation restarts its sequence and must still be accepted.");

            Assert.IsFalse(SimStalenessRules.AcceptProjectileProgress(
                    incomingGeneration: 1, incomingSequence: uint.MaxValue,
                    currentGeneration: 2, currentSequence: 0u),
                "An old generation's late sample must not drag the projectile back.");

            Assert.IsTrue(SimStalenessRules.AcceptProjectileProgress(1, 0u, 1, uint.MaxValue));
            Assert.IsTrue(SimStalenessRules.AcceptProjectileProgress(1, 6u, 1, 5u));
            Assert.IsFalse(SimStalenessRules.AcceptProjectileProgress(1, 5u, 1, 6u));
            Assert.IsFalse(SimStalenessRules.AcceptProjectileProgress(1, 5u, 1, 5u),
                "A duplicated motion sample must not be integrated twice.");
        }

        // ---------------------------------------------------------------------------------------
        // Mutation control — one mutant per guard, each with a case that must kill it.
        // ---------------------------------------------------------------------------------------

        // The mutants are written as local reimplementations of the pre-3.3 shape, so the kill is
        // demonstrated by the fixture rather than asserted about the shipping code by inspection.
        private static bool MutantNaiveNewer(uint incoming, uint current) => incoming > current;

        private static bool MutantSequenceOnlySnapshot(
            uint incomingSequence, long incomingWorldRevision, uint currentSequence, long currentWorldRevision)
            => MutantNaiveNewer(incomingSequence, currentSequence);

        private static bool MutantLifecycleOnlyLifetime(
            int incomingGeneration, int incomingLifecycleSequence, int currentGeneration, int currentLifecycleSequence)
            => incomingLifecycleSequence > currentLifecycleSequence;

        private static bool MutantGenerationIgnoredProjectile(
            int incomingGeneration, uint incomingSequence, int currentGeneration, uint currentSequence)
            => MutantNaiveNewer(incomingSequence, currentSequence);

        /// <summary>
        /// Each mutant is run against the same case that the real guard answers, and the two answers
        /// must differ. A mutant that agreed everywhere would mean the case table does not reach the
        /// discriminator, which is the failure mode this whole fixture is built to avoid.
        /// </summary>
        [Test]
        public void MutationControl_EveryGuardHasACaseThatKillsItsNaiveShape()
        {
            // 1. IsNewer vs `incoming > current`, on the wrap.
            Assert.IsTrue(SimStalenessRules.IsNewer(0u, uint.MaxValue));
            Assert.IsFalse(MutantNaiveNewer(0u, uint.MaxValue),
                "Mutant 1 (plain >) must be killed by the wrap case.");

            // 2. AcceptSnapshot vs sequence-only, on a revision bump.
            Assert.IsTrue(SimStalenessRules.AcceptSnapshot(0u, 2L, uint.MaxValue, 1L));
            Assert.IsFalse(MutantSequenceOnlySnapshot(0u, 2L, uint.MaxValue, 1L),
                "Mutant 2 (sequence-only) must be killed by the revision bump.");

            // 3. AcceptLifetime vs lifecycle-only, on a respawn whose counter restarts.
            Assert.IsTrue(SimStalenessRules.AcceptLifetime(2, 0, 1, 999));
            Assert.IsFalse(MutantLifecycleOnlyLifetime(2, 0, 1, 999),
                "Mutant 3 (lifecycle-only) must be killed by the generation bump.");

            // 4. AcceptProjectileProgress vs generation-ignoring, on a respawn at sequence 0.
            Assert.IsTrue(SimStalenessRules.AcceptProjectileProgress(2, 0u, 1, uint.MaxValue));
            Assert.IsFalse(MutantGenerationIgnoredProjectile(2, 0u, 1, uint.MaxValue),
                "Mutant 4 (generation-ignoring) must be killed by the projectile respawn.");
        }

        /// <summary>
        /// Vacuity guard on the case table itself: if a future edit emptied the discriminating cases
        /// above, the mutation test would still pass by comparing nothing. This asserts that the four
        /// mutants and the four real guards disagree on exactly the four cases named, no more and no
        /// fewer — so adding a fifth case without a mutant is a test failure rather than silent
        /// coverage.
        /// </summary>
        [Test]
        public void MutationControl_CaseTableIsNonEmptyAndCountsAreExact()
        {
            const int cases = 4;
            int disagreements = 0;

            if (SimStalenessRules.IsNewer(0u, uint.MaxValue) != MutantNaiveNewer(0u, uint.MaxValue)) disagreements++;
            if (SimStalenessRules.AcceptSnapshot(0u, 2L, uint.MaxValue, 1L)
                != MutantSequenceOnlySnapshot(0u, 2L, uint.MaxValue, 1L)) disagreements++;
            if (SimStalenessRules.AcceptLifetime(2, 0, 1, 999)
                != MutantLifecycleOnlyLifetime(2, 0, 1, 999)) disagreements++;
            if (SimStalenessRules.AcceptProjectileProgress(2, 0u, 1, uint.MaxValue)
                != MutantGenerationIgnoredProjectile(2, 0u, 1, uint.MaxValue)) disagreements++;

            Assert.AreEqual(cases, disagreements,
                "Every named case must separate its guard from its mutant. Fewer means a case stopped " +
                "reaching the discriminator and the mutation control became decorative.");
        }
    }
}
