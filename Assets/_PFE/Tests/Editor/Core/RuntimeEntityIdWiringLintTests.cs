using System;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 3.2 wiring lint. The decisions are pinned by <c>RuntimeEntityIdRulesTests</c>; the
    /// <b>calls that connect them to the game</b> cannot be executed offline at all —
    /// <c>RoomUnitSpawner</c>, <c>RoomObjectVisualManager</c>, <c>GameLifetimeScope</c> and the six
    /// static spawners all need a Unity component graph, and the offline harness throws
    /// <c>SecurityException: ECall methods must be packaged into a system module</c> before reaching
    /// an assertion. Reading stripped source is the only executable guard.
    ///
    /// <para><b>Why stripping matters here specifically.</b> Every edited call site carries a comment
    /// explaining the Phase 3.2 change, and several name the old <c>_serial++</c> shape. A raw-text
    /// lint would pass on a file whose call was deleted, and would also "find" the very serials that
    /// were removed. <see cref="SourceLint.ReadStripped"/> removes comments and string literals, so
    /// only code can satisfy or violate these assertions.</para>
    /// </summary>
    [TestFixture]
    public class RuntimeEntityIdWiringLintTests
    {
        private const string SimLoopPath = "Core/SimLoop.cs";
        private const string ScopePath = "Core/Installers/GameLifetimeScope.cs";
        private const string UnitSpawnerPath = "Systems/Map/Rendering/RoomUnitSpawner.cs";
        private const string ObjectManagerPath = "Systems/Map/Rendering/RoomObjectVisualManager.cs";

        private const string RulesPath = "Sim/Ids/RuntimeEntityIdRules.cs";
        private const string AllocatorPath = "Sim/Ids/RuntimeEntityIdAllocator.cs";
        private const string ContextPath = "Core/RuntimeEntityIdContext.cs";

        private static readonly string[] SpawnerPaths =
        {
            "Entities/Enemies/Archetypes/Flyer/FlyerSpawner.cs",
            "Entities/Pets/PetSpawner.cs",
            "Entities/Enemies/Bosses/BossSummonSpawner.cs",
            "Entities/Enemies/Bosses/BossAlicornBrain.cs",
            "Systems/Security/SecurityRuntime.cs",
            "Core/Scripting/PlayerDebugEditorOverlay.cs",
        };

        private const string AllocateCall = "PFE.Core.RuntimeEntityIdContext.AllocateRuntimeId()";
        private const string UnitRegisterCall = "RuntimeEntityIdContext.TryRegister(unit, unit.entityId);";
        private const string ObjectRegisterCall = "PFE.Core.RuntimeEntityIdContext.TryRegister(obj, obj.entityId);";
        private const string UnitUnregisterCall = "RuntimeEntityIdContext.Unregister(";
        private const string ObjectUnregisterCall = "PFE.Core.RuntimeEntityIdContext.Unregister(";

        private static readonly string[] RemovedSerials =
        {
            "_serial++", "_scytheSerial++", "_reinforcementSerial++", "_unitSpawnSerial++",
        };

        private static string Read(string path) => SourceLint.ReadStripped(path);

        private static int CountOf(string haystack, string needle)
        {
            int count = 0;
            int at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }

            return count;
        }

        /// <summary>
        /// A spawner is Phase-3.2-shaped iff it goes through the host allocator AND no longer carries
        /// any per-spawner serial or room-spawn scheme. Both halves matter: adding the new call while
        /// leaving the old one would look green and still mint replay-dependent ids.
        /// </summary>
        private static bool HasHostAllocatorShape(string source)
        {
            if (!source.Contains(AllocateCall)) return false;
            if (source.Contains("CreateForRoomSpawn")) return false;
            for (int i = 0; i < RemovedSerials.Length; i++)
            {
                if (source.Contains(RemovedSerials[i])) return false;
            }

            return true;
        }

        private static bool HasRegistryShape(string source, string registerCall, string unregisterCall)
        {
            return source.Contains(registerCall) && source.Contains(unregisterCall);
        }

        // ── Tick / bootstrap ──────────────────────────────────────────────────────────────────

        [Test]
        public void SimLoop_StampsTheTickBeforeAnyTickableCanSpawn()
        {
            string body = SourceLint.MethodBody(Read(SimLoopPath), "public int StepOnce()");

            Assert.That(body, Does.Contain("RuntimeEntityIdContext.AdvanceTick(_tickIndex);"),
                "a spawn inside a tickable must see this tick, so the stamp has to precede dispatch");
            Assert.That(body.IndexOf("AdvanceTick", StringComparison.Ordinal),
                Is.LessThan(body.IndexOf("EnsureOrder()", StringComparison.Ordinal)));
            Assert.That(body.IndexOf("AdvanceTick", StringComparison.Ordinal),
                Is.LessThan(body.IndexOf("SimTick(_tickIndex)", StringComparison.Ordinal)));
        }

        [Test]
        public void SimLoop_IncrementsExactlyOneTickPerStep()
        {
            string body = SourceLint.MethodBody(Read(SimLoopPath), "public int StepOnce()");

            Assert.That(CountOf(body, "_tickIndex++"), Is.EqualTo(1),
                "the increment moved to the top of StepOnce; a second one would double the host clock");
        }

        [Test]
        public void LifetimeScope_PushesTheDIRegistryIntoTheAmbientContext()
        {
            string source = Read(ScopePath);

            Assert.That(source, Does.Contain("RuntimeEntityIdContext.Configure("));
            Assert.That(source, Does.Contain("container.Resolve<IEntityRegistry>()"),
                "the ambient carrier must hold the container's singleton, not a second registry nobody queries");
            Assert.That(source, Does.Contain("new RuntimeEntityIdAllocator()"));
        }

        // ── Runtime spawners ──────────────────────────────────────────────────────────────────

        [Test]
        public void AllSixRuntimeSpawners_UseTheHostAllocatorAndNoPrivateSerial()
        {
            for (int i = 0; i < SpawnerPaths.Length; i++)
            {
                string source = Read(SpawnerPaths[i]);
                Assert.That(HasHostAllocatorShape(source), Is.True,
                    SpawnerPaths[i] + " must allocate its entity id from RuntimeEntityIdContext and " +
                    "must not keep a per-spawner serial or a room-spawn scheme");
            }
        }

        [Test]
        public void NoPrivateSpawnSerial_SurvivesAnywhereInProductionCode()
        {
            string[] all = new string[SpawnerPaths.Length + 1];
            Array.Copy(SpawnerPaths, all, SpawnerPaths.Length);
            all[SpawnerPaths.Length] = UnitSpawnerPath;

            for (int i = 0; i < all.Length; i++)
            {
                string source = Read(all[i]);
                for (int s = 0; s < RemovedSerials.Length; s++)
                {
                    Assert.That(source, Does.Not.Contain(RemovedSerials[s]),
                        all[i] + " still uses " + RemovedSerials[s]);
                }
            }
        }

        // ── Spawn / despawn registration ─────────────────────────────────────────────────────

        [Test]
        public void RoomUnitSpawner_RefreshAll_RegistersTheRecordAtSpawn()
        {
            string body = SourceLint.MethodBody(Read(UnitSpawnerPath), "public void RefreshAll()");

            Assert.That(body, Does.Contain(UnitRegisterCall));
            Assert.That(body.IndexOf(UnitRegisterCall, StringComparison.Ordinal),
                Is.GreaterThan(body.IndexOf("_spawned[unit] = Spawn(unit)", StringComparison.Ordinal)),
                "registration belongs to the new-spawn branch, not to the per-frame walk");
        }

        [Test]
        public void RoomUnitSpawner_BothTeardownPaths_UnregisterTheRecord()
        {
            string source = Read(UnitSpawnerPath);
            string destroyUnit = SourceLint.MethodBody(source, "void DestroyUnit(UnitInstance unit)");
            string destroyAll = SourceLint.MethodBody(source, "public void DestroyAll()");

            Assert.That(destroyUnit, Does.Contain("RuntimeEntityIdContext.Unregister(unit);"));
            Assert.That(destroyAll, Does.Contain("RuntimeEntityIdContext.Unregister(pair.Key);"));
            Assert.That(CountOf(source, UnitUnregisterCall), Is.EqualTo(2),
                "exactly two teardown paths exist; a third unregister is a stray, a missing one is a leak");
        }

        [Test]
        public void RoomObjectVisualManager_RegistersByRoomMembershipNotByPresenter()
        {
            string body = SourceLint.MethodBody(Read(ObjectManagerPath), "void SyncPresenters(bool forceRefresh)");

            Assert.That(body, Does.Contain(ObjectRegisterCall));
            Assert.That(body.IndexOf(ObjectRegisterCall, StringComparison.Ordinal),
                Is.LessThan(body.IndexOf("TryResolveRenderableVisual", StringComparison.Ordinal)),
                "an object with no authored visual is still a live entity; registering only presenters " +
                "would leave RoomPopulator.CreateHiddenObject's empty-id boxes anonymous");
            Assert.That(body, Does.Contain("_registeredObjects.Add(obj)"));
        }

        [Test]
        public void RoomObjectVisualManager_BothRemovalPaths_Unregister()
        {
            string source = Read(ObjectManagerPath);
            string sync = SourceLint.MethodBody(source, "void SyncPresenters(bool forceRefresh)");
            string destroyAll = SourceLint.MethodBody(source, "public void DestroyAll()");

            Assert.That(sync, Does.Contain(ObjectUnregisterCall));
            Assert.That(destroyAll, Does.Contain(ObjectUnregisterCall));
            Assert.That(destroyAll, Does.Contain("_registeredObjects"),
                "DestroyAll must walk the membership set, not _presenters, or visual-less objects leak");
            Assert.That(CountOf(source, ObjectUnregisterCall), Is.EqualTo(2));
        }

        // ── Purity ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void Phase32IdPolicy_IsFreeOfUnityTypes()
        {
            string[] files = { RulesPath, AllocatorPath, ContextPath };
            string[] forbidden = { "using UnityEngine", "MonoBehaviour", "GameObject", "Transform" };

            for (int i = 0; i < files.Length; i++)
            {
                string source = Read(files[i]);
                for (int f = 0; f < forbidden.Length; f++)
                {
                    Assert.That(source, Does.Not.Contain(forbidden[f]),
                        files[i] + " must stay offline-executable; found " + forbidden[f]);
                }
            }
        }

        // ── Equivalence / absent controls ────────────────────────────────────────────────────

        [Test]
        public void EquivalenceControl_HostAllocatorPredicate_AcceptsCodeAndRejectsThePrePhaseShape()
        {
            const string good = "class Shape { entityId = " + AllocateCall + ".ToString(); }";
            const string bad = "class Shape { entityId = PFE.Core.Ids.EntityId" +
                               ".CreateForRoomSpawn(room.id, tag, _serial++).ToString(); }";

            Assert.That(HasHostAllocatorShape(good), Is.True,
                "the predicate must accept a literal body carrying the reviewed shape");
            Assert.That(HasHostAllocatorShape(bad), Is.False,
                "the pre-3.2 per-spawner serial shape must be rejected by the predicate itself, " +
                "independent of whatever is in the shipping files");
        }

        [Test]
        public void AbsentControl_ACommentCannotSatisfyTheAllocatorPredicate()
        {
            // FlyerSpawner's real comment names `_serial++`; if the stripper stopped working, that
            // comment would both satisfy the presence half and violate the absence half. Prove it here
            // with a literal, rather than trusting every shipping file to stay comment-shaped.
            const string commentOnly = "// " + AllocateCall + "\nclass Shape { }";
            string stripped = SourceLint.StripCommentsAndStrings(commentOnly);

            Assert.That(stripped, Does.Not.Contain(AllocateCall));
            Assert.That(HasHostAllocatorShape(stripped), Is.False,
                "a deleted call must not be resurrected by the comment that describes it");
        }

        [Test]
        public void EquivalenceControl_RegistryPredicate_RejectsASpawnOnlyShape()
        {
            const string good = "class Shape { " + UnitRegisterCall + UnitUnregisterCall + "u); }";
            const string bad = "class Shape { _spawned[unit] = Spawn(unit); DestroyObject(unitObject); }";

            Assert.That(HasRegistryShape(good, UnitRegisterCall, UnitUnregisterCall), Is.True);
            Assert.That(HasRegistryShape(bad, UnitRegisterCall, UnitUnregisterCall), Is.False,
                "the pre-3.2 spawner built a view and destroyed it without ever registering anything");
        }
    }
}
