using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core;
using PFE.Core.Ids;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 3.2 (F-05), row 3.2 acceptance: "1 000 replayed ticks produce identical ID sets on two
    /// runs" — plus the controls that make that sentence mean something.
    ///
    /// <para><b>What is being tested is a policy, not a pure function.</b>
    /// <c>EntityIdTests.SpawnSequence_IsDeterministic</c> already proves
    /// <see cref="EntityId.CreateForRoomSpawn"/> returns the same id for the same arguments. That
    /// fixture would still pass if every runtime spawner kept its private process-lifetime serial,
    /// because those serials are the *inputs* the pure function never sees. So this suite replays an
    /// allocation schedule through <see cref="RuntimeEntityIdAllocator"/> and compares the resulting
    /// ID <i>sets</i>, with an explicit pre-3.2 shape control that must FAIL the same comparison.</para>
    /// </summary>
    [TestFixture]
    public class RuntimeEntityIdRulesTests
    {
        private const int ReplayTicks = 1000;

        [SetUp]
        public void SetUp()
        {
            RuntimeEntityIdContext.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            // The context is a process-wide static. A leaked allocator or registry would turn the
            // next fixture into a false pass or a false failure.
            RuntimeEntityIdContext.Reset();
        }

        /// <summary>
        /// Non-uniform on purpose: bloat nests, boss summons and alarm reinforcements do not fire on
        /// every tick. A constant schedule would not exercise the counter across tick boundaries.
        /// </summary>
        private static int SpawnsForTick(int tick)
        {
            return (tick % 7 == 0 ? 2 : 0)
                   + (tick % 13 == 0 ? 1 : 0)
                   + (tick % 97 == 0 ? 3 : 0);
        }

        private static List<EntityId> Replay(RuntimeEntityIdAllocator allocator, int ticks, int extraAtZero = 0)
        {
            var ids = new List<EntityId>();
            for (int tick = 0; tick < ticks; tick++)
            {
                int spawns = SpawnsForTick(tick) + (tick == 0 ? extraAtZero : 0);
                for (int i = 0; i < spawns; i++)
                {
                    ids.Add(allocator.Allocate(tick));
                }
            }

            return ids;
        }

        private static HashSet<ulong> HashSetOf(List<EntityId> ids)
        {
            var set = new HashSet<ulong>();
            for (int i = 0; i < ids.Count; i++) set.Add(ids[i].Hash);
            return set;
        }

        private static bool SameSequence(List<EntityId> a, List<EntityId> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Hash != b[i].Hash || a[i].DebugString != b[i].DebugString) return false;
            }

            return true;
        }

        // ── Row 3.2 acceptance ────────────────────────────────────────────────────────────────

        [Test]
        public void Acceptance_ThousandReplayedTicks_ProduceIdenticalIdSetsOnTwoRuns()
        {
            var run1 = Replay(new RuntimeEntityIdAllocator(), ReplayTicks);
            var run2 = Replay(new RuntimeEntityIdAllocator(), ReplayTicks);

            int expected = 0;
            for (int tick = 0; tick < ReplayTicks; tick++) expected += SpawnsForTick(tick);
            Assert.That(run1.Count, Is.EqualTo(expected),
                "the replay harness must drive exactly the schedule it claims to drive");
            Assert.That(run1.Count, Is.GreaterThan(100),
                "the replay must actually allocate ids, or an empty set would pass vacuously");
            Assert.That(SameSequence(run1, run2), Is.True, "sequences must match, not only set sizes");

            HashSet<ulong> set1 = HashSetOf(run1);
            HashSet<ulong> set2 = HashSetOf(run2);
            Assert.That(set1.SetEquals(set2), Is.True);
            Assert.That(set1.Count, Is.EqualTo(run1.Count),
                "every allocation in a run must be unique — a repeated id is an F-05 failure");
        }

        [Test]
        public void Acceptance_SameAllocatorAfterExplicitReset_ProducesIdenticalIdSets()
        {
            var allocator = new RuntimeEntityIdAllocator();

            var run1 = Replay(allocator, ReplayTicks);
            allocator.Reset();
            var run2 = Replay(allocator, ReplayTicks);

            Assert.That(SameSequence(run1, run2), Is.True,
                "a replay starts with Reset(), not with a new process");
        }

        [Test]
        public void AbsentControl_ReplayWithoutReset_ProducesDifferentIds()
        {
            var allocator = new RuntimeEntityIdAllocator();

            var run1 = Replay(allocator, ReplayTicks);
            var run2 = Replay(allocator, ReplayTicks);

            Assert.That(SameSequence(run1, run2), Is.False,
                "the acceptance must be able to fail: a continuing counter is not a replay");
        }

        [Test]
        public void MutationControl_AnExtraSpawnAtTickZero_ShiftsTheWholeRun()
        {
            var run1 = Replay(new RuntimeEntityIdAllocator(), ReplayTicks);
            var run2 = Replay(new RuntimeEntityIdAllocator(), ReplayTicks, extraAtZero: 1);

            Assert.That(HashSetOf(run1).SetEquals(HashSetOf(run2)), Is.False,
                "one changed spawn must change the id set, or the comparison is not sensitive to policy");
        }

        [Test]
        public void PreRuleShape_ProcessLifetimeSerial_DivergesAcrossTwoRuns()
        {
            // The pre-3.2 shape: one static int per spawner, never reset between replays. This is the
            // equivalence control — if the acceptance above cannot distinguish this from the new
            // allocator, it proves nothing about the change that was made.
            int processLifetimeSerial = 0;

            Func<List<EntityId>> run = () =>
            {
                var ids = new List<EntityId>();
                for (int tick = 0; tick < ReplayTicks; tick++)
                {
                    for (int i = 0; i < SpawnsForTick(tick); i++)
                    {
                        ids.Add(EntityId.CreateForRoomSpawn("r_test", "emit:flyer", processLifetimeSerial++));
                    }
                }

                return ids;
            };

            List<EntityId> run1 = run();
            List<EntityId> run2 = run();

            Assert.That(SameSequence(run1, run2), Is.False,
                "the one-per-spawner static serial is exactly the bug F-05 names; it must be observably different");
        }

        // ── Policy decisions ──────────────────────────────────────────────────────────────────

        [Test]
        public void Counter_IsMonotonicAcrossTicks_NotResetPerTick()
        {
            var allocator = new RuntimeEntityIdAllocator();

            EntityId a = allocator.Allocate(10);
            EntityId b = allocator.Allocate(10);
            EntityId c = allocator.Allocate(11);
            EntityId d = allocator.Allocate(11);

            Assert.That(a.DebugString, Is.EqualTo("runtime:10:000"));
            Assert.That(b.DebugString, Is.EqualTo("runtime:10:001"));
            Assert.That(c.DebugString, Is.EqualTo("runtime:11:002"),
                "a per-tick counter reset would reuse :000 here and could collide with an off-tick spawn");
            Assert.That(d.DebugString, Is.EqualTo("runtime:11:003"));
            Assert.That(allocator.Counter, Is.EqualTo(4));
        }

        [Test]
        public void TwoAllocationsInTheSameTick_AreDistinct()
        {
            var allocator = new RuntimeEntityIdAllocator();

            EntityId a = allocator.Allocate(42);
            EntityId b = allocator.Allocate(42);

            Assert.That(a.Hash == b.Hash, Is.False);
            Assert.That(RuntimeEntityIdRules.IsRuntimeId(a), Is.True);
            Assert.That(RuntimeEntityIdRules.IsRuntimeId(b), Is.True);
        }

        [Test]
        public void EffectiveTickIndex_ClampsNoActiveTickToBootstrap()
        {
            Assert.That(RuntimeEntityIdRules.EffectiveTickIndex(RuntimeEntityIdRules.NoActiveTick),
                Is.EqualTo(RuntimeEntityIdRules.BootstrapTickIndex));
            Assert.That(RuntimeEntityIdRules.EffectiveTickIndex(-500L), Is.EqualTo(0L));
            Assert.That(RuntimeEntityIdRules.EffectiveTickIndex(1420L), Is.EqualTo(1420L));
        }

        [Test]
        public void AdvanceTick_NeverRewinds()
        {
            Assert.That(RuntimeEntityIdRules.AdvanceTick(-1L, 0L), Is.EqualTo(0L));
            Assert.That(RuntimeEntityIdRules.AdvanceTick(10L, 11L), Is.EqualTo(11L));
            Assert.That(RuntimeEntityIdRules.AdvanceTick(11L, 3L), Is.EqualTo(11L),
                "a second SimLoop in the process must not rewind the host tick");
        }

        [Test]
        public void IdFor_MatchesCreateRuntime()
        {
            EntityId direct = EntityId.CreateRuntime(77L, 4);
            EntityId viaRule = RuntimeEntityIdRules.IdFor(77L, 4);

            Assert.That(viaRule.Hash, Is.EqualTo(direct.Hash));
            Assert.That(viaRule.DebugString, Is.EqualTo(direct.DebugString));
        }

        [Test]
        public void IdFor_NoActiveTick_StampsBootstrapTick()
        {
            EntityId id = RuntimeEntityIdRules.IdFor(RuntimeEntityIdRules.NoActiveTick, 0);

            Assert.That(id.DebugString, Is.EqualTo("runtime:0:000"));
        }

        [Test]
        public void IsRuntimeId_RejectsRoomSpawnAndEmpty()
        {
            Assert.That(RuntimeEntityIdRules.IsRuntimeId(EntityId.CreateRuntime(1L, 0)), Is.True);
            Assert.That(RuntimeEntityIdRules.IsRuntimeId(EntityId.CreateForRoomSpawn("r", "box", 0)), Is.False);
            Assert.That(RuntimeEntityIdRules.IsRuntimeId(EntityId.Empty), Is.False);
        }

        [Test]
        public void NextCounter_IncrementsByOne()
        {
            Assert.That(RuntimeEntityIdRules.NextCounter(0), Is.EqualTo(1));
            Assert.That(RuntimeEntityIdRules.NextCounter(41), Is.EqualTo(42));
        }

        [Test]
        public void ShouldReplace_NullOrSameObject_IsLegal()
        {
            object record = new object();

            Assert.That(RuntimeEntityIdRules.ShouldReplace(null, record), Is.True);
            Assert.That(RuntimeEntityIdRules.ShouldReplace(record, record), Is.True);
        }

        [Test]
        public void ShouldReplace_ADifferentRecordWithTheSameId_IsACollision()
        {
            var first = new object();
            var second = new object();

            Assert.That(RuntimeEntityIdRules.ShouldReplace(first, second), Is.False,
                "two live records claiming one id must be surfaced, not silently overwritten");
        }

        [Test]
        public void Allocator_ExposesStateAndResetRestartsIt()
        {
            var allocator = new RuntimeEntityIdAllocator();

            Assert.That(allocator.LastTickIndex, Is.EqualTo(RuntimeEntityIdRules.NoActiveTick));
            Assert.That(allocator.AllocatedCount, Is.EqualTo(0L));

            allocator.Allocate(5L);
            allocator.Allocate(9L);
            allocator.Allocate(3L); // a lower tick must not move LastTickIndex backwards

            Assert.That(allocator.LastTickIndex, Is.EqualTo(9L));
            Assert.That(allocator.Counter, Is.EqualTo(3));
            Assert.That(allocator.AllocatedCount, Is.EqualTo(3L));

            allocator.Reset();
            Assert.That(allocator.LastTickIndex, Is.EqualTo(RuntimeEntityIdRules.NoActiveTick));
            Assert.That(allocator.Counter, Is.EqualTo(0));
            Assert.That(allocator.AllocatedCount, Is.EqualTo(0L));
        }

        // ── Ambient context: empty-ID resolution + registration ───────────────────────────────

        [Test]
        public void Context_AdvanceTick_IsMonotonicAndStampsAllocations()
        {
            RuntimeEntityIdContext.AdvanceTick(0);
            RuntimeEntityIdContext.AdvanceTick(41);
            RuntimeEntityIdContext.AdvanceTick(7); // must not rewind

            Assert.That(RuntimeEntityIdContext.CurrentTickIndex, Is.EqualTo(41L));
            Assert.That(RuntimeEntityIdContext.AllocateRuntimeId().DebugString,
                Is.EqualTo("runtime:41:000"));
        }

        [Test]
        public void Context_EmptyRawId_IsResolvedToAHostRuntimeId()
        {
            var record = new object();

            Assert.That(RuntimeEntityIdContext.TryRegister(record, string.Empty), Is.True);
            Assert.That(RuntimeEntityIdContext.TryGetId(record, out EntityId id), Is.True);
            Assert.That(RuntimeEntityIdRules.IsRuntimeId(id), Is.True);
            Assert.That(RuntimeEntityIdContext.DerivedIdCount, Is.EqualTo(1L));
            Assert.That(RuntimeEntityIdContext.RegisteredCount, Is.EqualTo(1));
        }

        [Test]
        public void Context_EmptyRawId_SurvivesARoomTransitionUnregisterAndReregister()
        {
            // Room teardown unregisters a record; re-entry registers the SAME record object. If the
            // derived id were thrown away at unregister, the entity would come back under a new id
            // after a transition — the exact failure SimUnitState.EntityHash documents.
            var record = new object();

            RuntimeEntityIdContext.TryRegister(record, string.Empty);
            RuntimeEntityIdContext.TryGetId(record, out EntityId first);
            Assert.That(RuntimeEntityIdContext.Unregister(record), Is.True);
            Assert.That(RuntimeEntityIdContext.TrackedIdCount, Is.EqualTo(0));
            Assert.That(RuntimeEntityIdContext.TryGetId(record, out EntityId whileDead), Is.True);

            RuntimeEntityIdContext.TryRegister(record, string.Empty);
            RuntimeEntityIdContext.TryGetId(record, out EntityId second);

            Assert.That(whileDead.Hash, Is.EqualTo(first.Hash));
            Assert.That(second.Hash, Is.EqualTo(first.Hash));
            Assert.That(second.DebugString, Is.EqualTo(first.DebugString));
            Assert.That(RuntimeEntityIdContext.DerivedIdCount, Is.EqualTo(1L),
                "re-registering a known record must not mint a second runtime id");
            Assert.That(RuntimeEntityIdContext.RegisteredCount, Is.EqualTo(1));
        }

        [Test]
        public void Context_AuthoredRawId_IsUsedVerbatim()
        {
            var record = new object();
            EntityId authored = EntityId.CreateForRoomSpawn("r_01_01", "chest", 2);

            Assert.That(RuntimeEntityIdContext.TryRegister(record, authored.DebugString), Is.True);
            Assert.That(RuntimeEntityIdContext.TryGetId(record, out EntityId resolved), Is.True);
            Assert.That(resolved.Hash, Is.EqualTo(authored.Hash));
            Assert.That(RuntimeEntityIdContext.DerivedIdCount, Is.EqualTo(0L));
        }

        [Test]
        public void Context_TryRegister_IsIdempotent()
        {
            var record = new object();

            Assert.That(RuntimeEntityIdContext.TryRegister(record, "r:box:000"), Is.True);
            Assert.That(RuntimeEntityIdContext.TryRegister(record, "r:box:000"), Is.True);
            Assert.That(RuntimeEntityIdContext.TryRegister(record, "r:box:000"), Is.True);

            Assert.That(RuntimeEntityIdContext.RegisteredCount, Is.EqualTo(1));
            Assert.That(RuntimeEntityIdContext.TrackedIdCount, Is.EqualTo(1));
        }

        [Test]
        public void Context_Unregister_RemovesTheEntityAndIsIdempotent()
        {
            var record = new object();
            RuntimeEntityIdContext.TryRegister(record, "r:box:000");

            Assert.That(RuntimeEntityIdContext.Unregister(record), Is.True);
            Assert.That(RuntimeEntityIdContext.RegisteredCount, Is.EqualTo(0));
            Assert.That(RuntimeEntityIdContext.Unregister(record), Is.False);
        }

        [Test]
        public void Context_TwoDistinctRecordsWithOneId_DoNotOverwrite()
        {
            var first = new object();
            var second = new object();

            Assert.That(RuntimeEntityIdContext.TryRegister(first, "r:box:000"), Is.True);
            Assert.That(RuntimeEntityIdContext.TryRegister(second, "r:box:000"), Is.False);
            Assert.That(RuntimeEntityIdContext.CollisionCount, Is.EqualTo(1L));
            Assert.That(RuntimeEntityIdContext.RegisteredCount, Is.EqualTo(1));
            Assert.That(RuntimeEntityIdContext.Registry.TryGetEntity<object>(
                EntityId.FromString("r:box:000"), out object held), Is.True);
            Assert.That(ReferenceEquals(held, first), Is.True);
        }

        [Test]
        public void Context_TwoDistinctEmptyIdRecords_GetDistinctDerivedIds()
        {
            var first = new object();
            var second = new object();

            RuntimeEntityIdContext.TryRegister(first, string.Empty);
            RuntimeEntityIdContext.TryRegister(second, string.Empty);

            RuntimeEntityIdContext.TryGetId(first, out EntityId a);
            RuntimeEntityIdContext.TryGetId(second, out EntityId b);
            Assert.That(a.Hash == b.Hash, Is.False);
            Assert.That(RuntimeEntityIdContext.RegisteredCount, Is.EqualTo(2));
        }

        [Test]
        public void Context_NullEntity_IsRejectedWithoutThrowing()
        {
            Assert.That(RuntimeEntityIdContext.TryRegister(null, "r:box:000"), Is.False);
            Assert.That(RuntimeEntityIdContext.Unregister(null), Is.False);
            Assert.That(RuntimeEntityIdContext.TryGetId(null, out EntityId id), Is.False);
            Assert.That(id.IsValid, Is.False);
        }

        [Test]
        public void Context_Configure_PushesTheDIRegistryAndResetsTheRun()
        {
            var diRegistry = new EntityRegistry();
            var allocator = new RuntimeEntityIdAllocator();
            allocator.Allocate(3L); // a stale allocation that bootstrap must discard

            RuntimeEntityIdContext.Configure(diRegistry, allocator);

            Assert.That(ReferenceEquals(RuntimeEntityIdContext.Registry, diRegistry), Is.True);
            Assert.That(allocator.Counter, Is.EqualTo(0));
            Assert.That(RuntimeEntityIdContext.CurrentTickIndex,
                Is.EqualTo(RuntimeEntityIdRules.NoActiveTick));

            RuntimeEntityIdContext.TryRegister(new object(), "r:box:000");
            Assert.That(diRegistry.Count, Is.EqualTo(1));
        }

        [Test]
        public void Context_Reset_RestoresContainerlessDefaults()
        {
            var diRegistry = new EntityRegistry();
            RuntimeEntityIdContext.Configure(diRegistry, new RuntimeEntityIdAllocator());
            RuntimeEntityIdContext.TryRegister(new object(), "r:box:000");

            RuntimeEntityIdContext.Reset();

            Assert.That(ReferenceEquals(RuntimeEntityIdContext.Registry, diRegistry), Is.False);
            Assert.That(RuntimeEntityIdContext.RegisteredCount, Is.EqualTo(0));
            Assert.That(RuntimeEntityIdContext.TrackedIdCount, Is.EqualTo(0));
            Assert.That(RuntimeEntityIdContext.DerivedIdCount, Is.EqualTo(0L));
            Assert.That(RuntimeEntityIdContext.CollisionCount, Is.EqualTo(0L));
        }
    }
}
