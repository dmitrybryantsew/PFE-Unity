using NUnit.Framework;
using PFE.Core;
using PFE.Core.Ids;
using PFE.Sim.State;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 3.3 (F-06): the lifetime half of an entity id — the producer side of
    /// <c>SimUnitState.Generation</c> / <c>LifecycleSequence</c> and
    /// <c>SimProjectileState.Generation</c>.
    ///
    /// <para><b>Why a producer fixture is not optional here.</b> Row 3.3 adds four header fields, and
    /// three of them already existed on the records. A field that exists and is never produced is this
    /// project's documented trap — <c>Has()</c> answering "present" for every weapon while nothing
    /// misbehaves. <c>generation</c> is the one most at risk, because a naive implementation leaves it
    /// at 0 forever and every assertion about "stale lifetime rejected" then passes vacuously: with a
    /// constant generation, <see cref="SimStalenessRules.AcceptLifetime"/> degenerates to a plain
    /// counter compare and never rejects the case it was written for.</para>
    ///
    /// <para>So the cases below are built from <b>values the context actually produced</b>, not from
    /// hand-written pairs, and the last test feeds those produced values back through the guard to show
    /// the generation is load-bearing.</para>
    /// </summary>
    [TestFixture]
    public class EntityLifetimeTests
    {
        private const string StableId = "room:cellar:unit:001";

        [SetUp]
        public void SetUp()
        {
            RuntimeEntityIdContext.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            // Process-wide static; a leaked lifetime table would make the next fixture's generation
            // assertions read one higher for no reason.
            RuntimeEntityIdContext.Reset();
        }

        /// <summary>
        /// The ordinary case: one record object, registered once, is generation 0 / lifecycle 1.
        /// Asserted explicitly because every later case is a deviation from it.
        /// </summary>
        [Test]
        public void FirstRegistration_IsGenerationZeroLifecycleOne()
        {
            var record = new object();

            Assert.IsTrue(RuntimeEntityIdContext.TryRegister(record, StableId));
            Assert.IsTrue(RuntimeEntityIdContext.TryDescribe(record, out EntityId id, out EntityLifetime lifetime));

            Assert.IsTrue(id.IsValid);
            Assert.AreEqual(RuntimeEntityIdRules.FirstGeneration, lifetime.Generation);
            Assert.AreEqual(RuntimeEntityIdRules.FirstLifecycleSequence, lifetime.LifecycleSequence);
            Assert.AreEqual(0L, RuntimeEntityIdContext.RecycledIdCount,
                "A first registration recycles nothing.");
        }

        /// <summary>
        /// Re-registration without an intervening unregister is not a lifecycle event — it is the same
        /// live record, which is what <c>RefreshAll</c> does on every room entry. If it advanced the
        /// counter, a room that calls RefreshAll twice per entry would look like two respawns.
        /// </summary>
        [Test]
        public void ReRegistrationWhileLive_IsIdempotent()
        {
            var record = new object();

            Assert.IsTrue(RuntimeEntityIdContext.TryRegister(record, StableId));
            Assert.IsTrue(RuntimeEntityIdContext.TryRegister(record, StableId));

            RuntimeEntityIdContext.TryDescribe(record, out EntityId id, out EntityLifetime lifetime);

            Assert.AreEqual(RuntimeEntityIdRules.FirstGeneration, lifetime.Generation);
            Assert.AreEqual(RuntimeEntityIdRules.FirstLifecycleSequence, lifetime.LifecycleSequence,
                "A second registration of a live record must not advance the lifetime.");
            Assert.AreEqual(1, RuntimeEntityIdContext.RegisteredCount);
            Assert.AreEqual(0L, RuntimeEntityIdContext.CollisionCount,
                "Idempotence must be handled by the fast path, not by the collision branch.");
            Assert.IsTrue(id.IsValid);
        }

        /// <summary>
        /// Teardown then re-entry with the <b>same</b> record object: the entity came back. The
        /// generation must not move — it is the same occupant — but the lifecycle counter must, or a
        /// despawn record and the spawn after it would share one lifetime and the respawn would read as
        /// a duplicate.
        /// </summary>
        [Test]
        public void SameRecordAfterUnregister_ContinuesTheLifecycleCounter()
        {
            var record = new object();

            RuntimeEntityIdContext.TryRegister(record, StableId);
            RuntimeEntityIdContext.TryDescribe(record, out _, out EntityLifetime spawned);

            RuntimeEntityIdContext.Unregister(record);
            RuntimeEntityIdContext.TryDescribe(record, out _, out EntityLifetime despawned);

            RuntimeEntityIdContext.TryRegister(record, StableId);
            RuntimeEntityIdContext.TryDescribe(record, out _, out EntityLifetime respawned);

            Assert.AreEqual(spawned.Generation, respawned.Generation,
                "The same record coming back is the same occupant, so the generation must not move.");
            Assert.Greater(despawned.LifecycleSequence, spawned.LifecycleSequence,
                "A despawn must be newer than the spawn it ends, or the guard would call it a duplicate.");
            Assert.Greater(respawned.LifecycleSequence, despawned.LifecycleSequence,
                "A respawn must be newer than the despawn before it.");
            Assert.AreEqual(0L, RuntimeEntityIdContext.RecycledIdCount);
        }

        /// <summary>
        /// The case <c>generation</c> exists for: the room is reloaded, the old record object is gone and
        /// a <b>new</b> object claims the same stable id string. Its lifecycle counter restarts at 1,
        /// which reads as <i>older</i> than the previous occupant's counter — so a counter-only compare
        /// would freeze the entity at its pre-transition state forever.
        /// </summary>
        [Test]
        public void RecycledId_WithADifferentRecord_BumpsTheGeneration()
        {
            var firstOccupant = new object();
            var secondOccupant = new object();

            RuntimeEntityIdContext.TryRegister(firstOccupant, StableId);
            RuntimeEntityIdContext.Unregister(firstOccupant);
            RuntimeEntityIdContext.TryDescribe(firstOccupant, out _, out EntityLifetime left);

            Assert.IsTrue(RuntimeEntityIdContext.TryRegister(secondOccupant, StableId));
            RuntimeEntityIdContext.TryDescribe(secondOccupant, out EntityId id, out EntityLifetime taken);

            Assert.AreEqual(left.Generation + 1, taken.Generation,
                "A different record on the same stable id is a new occupancy and must bump the "
                + "generation — this is the only thing that makes the id's reuse observable.");
            Assert.AreEqual(RuntimeEntityIdRules.FirstLifecycleSequence, taken.LifecycleSequence,
                "The new occupancy starts its own counter.");
            Assert.Less(taken.LifecycleSequence, left.LifecycleSequence,
                "Deliberately lower than the previous occupancy's counter — that asymmetry is why the "
                + "generation has to outrank it.");
            Assert.AreEqual(1L, RuntimeEntityIdContext.RecycledIdCount);
            Assert.IsTrue(id.IsValid);
        }

        /// <summary>
        /// The mutation control the acceptance asks for, run on <b>produced</b> values: the guard accepts
        /// the recycled lifetime, and a counter-only compare rejects it. If a future change left
        /// <c>generation</c> at 0, this test fails — which is exactly the vacuity the fixture exists to
        /// prevent.
        /// </summary>
        [Test]
        public void MutationControl_TheProducedGenerationIsWhatTheGuardNeeds()
        {
            var firstOccupant = new object();
            var secondOccupant = new object();

            RuntimeEntityIdContext.TryRegister(firstOccupant, StableId);
            RuntimeEntityIdContext.Unregister(firstOccupant);
            RuntimeEntityIdContext.TryDescribe(firstOccupant, out _, out EntityLifetime left);

            RuntimeEntityIdContext.TryRegister(secondOccupant, StableId);
            RuntimeEntityIdContext.TryDescribe(secondOccupant, out _, out EntityLifetime taken);

            Assert.IsTrue(
                SimStalenessRules.AcceptLifetime(
                    taken.Generation, taken.LifecycleSequence,
                    left.Generation, left.LifecycleSequence),
                "The real guard must accept the new occupancy.");

            bool counterOnly = taken.LifecycleSequence > left.LifecycleSequence;
            Assert.IsFalse(counterOnly,
                "The counter-only shape must reject it — that is the mutant this case kills. If this "
                + "ever becomes true, the generated values stopped discriminating and the generation "
                + "field became decorative.");
        }

        /// <summary>
        /// Runtime ids are never recycled within a run, so the context deliberately keeps no lifetime
        /// table entry for them — otherwise the table would grow with every fired projectile. The
        /// generation must still read 0 and the recycle counter must not move.
        /// </summary>
        [Test]
        public void RuntimeIds_AreNeverTrackedAndNeverRecycled()
        {
            EntityId runtimeId = EntityId.CreateRuntime(12, 3);
            var a = new object();
            var b = new object();

            Assert.IsTrue(RuntimeEntityIdContext.TryRegister(a, runtimeId));

            // Release it first: two *live* records under one id is the collision branch, which is a
            // different rule and is pinned by RuntimeEntityIdRulesTests. What is under test here is what
            // happens when the id is free again.
            RuntimeEntityIdContext.Unregister(a);

            Assert.IsTrue(RuntimeEntityIdContext.TryRegister(b, runtimeId),
                "A freed runtime id can be taken by another record.");

            Assert.AreEqual(0L, RuntimeEntityIdContext.RecycledIdCount,
                "Runtime ids must never reach the recycle path — they are unique per run, and tracking "
                + "them would grow the lifetime table by one entry per fired projectile.");

            RuntimeEntityIdContext.TryDescribe(b, out _, out EntityLifetime lifetime);
            Assert.AreEqual(RuntimeEntityIdRules.FirstGeneration, lifetime.Generation);
            Assert.AreEqual(RuntimeEntityIdRules.FirstLifecycleSequence, lifetime.LifecycleSequence);
        }

        /// <summary>
        /// The pooled-object path: the same component is handed a brand-new id for each shot. Without
        /// <see cref="RuntimeEntityIdContext.TryReassign"/> the idempotence fast path would answer
        /// "already registered" and the object would keep firing under the first shot's id forever.
        /// </summary>
        [Test]
        public void TryReassign_GivesAPooledObjectAFreshIdAndALifetime()
        {
            var pooled = new object();
            EntityId first = EntityId.CreateRuntime(4, 0);
            EntityId second = EntityId.CreateRuntime(4, 1);

            Assert.IsTrue(RuntimeEntityIdContext.TryReassign(pooled, first));
            Assert.IsTrue(RuntimeEntityIdContext.TryGetId(pooled, out EntityId resolvedFirst));
            Assert.AreEqual(first.Hash, resolvedFirst.Hash);

            Assert.IsTrue(RuntimeEntityIdContext.TryReassign(pooled, second));
            Assert.IsTrue(RuntimeEntityIdContext.TryGetId(pooled, out EntityId resolvedSecond));

            Assert.AreEqual(second.Hash, resolvedSecond.Hash,
                "Reassignment must replace the cached id; keeping the first one is the bug the "
                + "idempotence fast path would otherwise hide.");
            Assert.AreNotEqual(first.Hash, resolvedSecond.Hash);
            Assert.AreEqual(1, RuntimeEntityIdContext.RegisteredCount,
                "Reassignment must not leave the previous id registered.");
            Assert.IsFalse(RuntimeEntityIdContext.Registry.Contains(first),
                "The superseded id must be gone from the registry, or a snapshot would project the "
                + "same pooled object twice.");
        }

        /// <summary>
        /// Absent control: the pre-3.3 shape — a plain monotonic counter with no generation — answers
        /// "the recycled lifetime is stale", which is the failure the field prevents. Written as an
        /// assertion on the numbers rather than a comment, so it cannot rot.
        /// </summary>
        [Test]
        public void AbsentControl_AConstantGenerationCollapsesTheGuardToACounterCompare()
        {
            const int constantGeneration = 0;

            // A recycled occupancy: counter restarts at 1 against the previous occupancy's 4.
            bool withGeneration = SimStalenessRules.AcceptLifetime(
                constantGeneration + 1, RuntimeEntityIdRules.FirstLifecycleSequence,
                constantGeneration, 4);

            bool asIfConstant = RuntimeEntityIdRules.FirstLifecycleSequence > 4;

            Assert.IsTrue(withGeneration);
            Assert.IsFalse(asIfConstant,
                "With a constant generation the guard reduces to `1 > 4`, i.e. false. The generation "
                + "is the only thing that turns that into an accept.");
        }
    }
}
