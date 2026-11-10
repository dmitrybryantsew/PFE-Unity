using System;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Source lint for the <b>wiring half</b> of Phase 3.1 (set-based active rooms).
    ///
    /// <para><b>Why a lint.</b> <c>RoomStreamingManager</c> is a <c>MonoBehaviour</c> with
    /// <c>Update()</c> frame bookkeeping; fixtures that instantiate one die with
    /// <c>SecurityException: ECall …</c> in this project's offline harness. The pure decisions are
    /// proven by <c>ActiveRoomSetRulesTests</c>; this fixture pins the calls that remain in the
    /// component and in <c>PhysicsWorldService</c>. It runs offline in both venues.</para>
    ///
    /// <para><b>The regression it exists for.</b> F-04: with one <c>currentActiveRoom</c>, two players
    /// in two rooms is unrepresentable and a projectile in the non-presentation room hits nothing —
    /// even though the physics mirror already loops over every room it holds. A future edit that
    /// reintroduces a single active room, or drops the pure-rule call, would compile cleanly and
    /// silently undo the phase.</para>
    ///
    /// <para><b>Guards that can fail.</b> Every presence predicate has a mutation control that deletes
    /// or renames the very token it checks, plus a literal good/bad body equivalence control.
    /// <c>SourceLint.ReadStripped</c> removes comments and string literals, so a doc paragraph naming
    /// the right call cannot satisfy any assertion here.</para>
    /// </summary>
    [TestFixture]
    public sealed class ActiveRoomSetWiringLintTests
    {
        private const string ManagerPath = "Systems/Map/Streaming/RoomStreamingManager.cs";
        private const string PhysicsPath = "Systems/Physics/PhysicsWorldService.cs";
        private const string RulesPath = "Sim/Map/ActiveRoomSetRules.cs";

        private const string ResolveCall = "ActiveRoomSetRules.ResolveActiveRooms(";
        private const string DiffCall = "ActiveRoomSetRules.Diff(";
        private const string ActiveRoomsProperty = "public IReadOnlyCollection<RoomInstance> ActiveRooms => _activeRooms;";
        private const string CurrentRoomAlias = "public RoomInstance CurrentRoom => _presentationRoom;";
        private const string ActivateSet = "_activeRooms.Add(room);";
        private const string DeactivateSet = "_activeRooms.Remove(room);";
        private const string PhysicsLoop = "foreach (KeyValuePair<RoomInstance, RoomChainGeometry> entry in _roomGeometry)";

        private static string Manager()
        {
            return SourceLint.ReadStripped(ManagerPath);
        }

        private static int Count(string haystack, string needle)
        {
            int n = 0;
            int at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                n++;
                at += needle.Length;
            }
            return n;
        }

        private static bool HasSetBasedSurface(string source)
        {
            return source.Contains(ActiveRoomsProperty)
                   && source.Contains("public RoomInstance PresentationRoom => _presentationRoom;")
                   && source.Contains(CurrentRoomAlias)
                   && source.Contains("public bool MaySimulate(RoomInstance room)")
                   && source.Contains("public void ApplyRoomPresence(")
                   && source.Contains(ResolveCall)
                   && source.Contains(DiffCall)
                   && source.Contains("ActiveRoomSetRules.MaySimulate(")
                   && source.Contains(ActivateSet)
                   && source.Contains(DeactivateSet);
        }

        [Test]
        public void Manager_ExposesTheSetBasedSurface_AndUsesThePureRules()
        {
            string source = Manager();

            Assert.That(HasSetBasedSurface(source), Is.True,
                "the manager must expose ActiveRooms/PresentationRoom/MaySimulate and drive them " +
                "through ActiveRoomSetRules, not a local one-room decision");
        }

        [Test]
        public void CurrentRoom_IsAnAliasForPresentationRoom()
        {
            // 23 consumers read CurrentRoom. It must stay a projection, not a second source of truth.
            Assert.That(Manager(), Does.Contain(CurrentRoomAlias));
        }

        [Test]
        public void ActivateRoom_IsStillTheSinglePlayerDriver()
        {
            string body = SourceLint.MethodBody(
                Manager(), "public void ActivateRoom(RoomInstance newRoom, RoomInstance oldRoom = null)");

            Assert.That(body, Does.Contain("SetSinglePlayerPresence(newRoom);"),
                "the legacy path must still register the one local player, or MaySimulate is dark in single-player");
            Assert.That(body, Does.Contain("DeactivateRoomInternal(room);"),
                "the legacy path must still deactivate the previous room");
            Assert.That(body, Does.Contain("ActivateRoomInternal(newRoom);"),
                "the legacy path must still activate exactly one room");
            Assert.That(body, Does.Not.Contain("ApplyRoomPresence("),
                "the two drivers must not be mixed inside one call");
        }

        [Test]
        public void ApplyRoomPresence_UsesTheRules_AndDeactivatesBeforeActivating()
        {
            string body = SourceLint.MethodBody(
                Manager(), "IReadOnlyDictionary<RoomInstance, int> connectedPlayers,");

            Assert.That(body, Does.Contain(ResolveCall));
            Assert.That(body, Does.Contain(DiffCall));
            Assert.That(body, Does.Contain("SetActiveInternal(room)"),
                "a second player's room must be activated without becoming the presentation room");

            int drop = body.IndexOf("foreach (ulong key in toDeactivate)", StringComparison.Ordinal);
            int add = body.IndexOf("foreach (ulong key in toActivate)", StringComparison.Ordinal);
            Assert.That(drop, Is.GreaterThanOrEqualTo(0));
            Assert.That(add, Is.GreaterThan(drop),
                "geometry must be released before it is built, or one room ends up with two chain sets");
        }

        [Test]
        public void PhysicsService_AlreadySweepsEveryMirroredRoom()
        {
            // This is the "connection, not construction" fact from the research: the physics half is
            // set-based and needed no edit. Pin it so a future refactor cannot quietly narrow it.
            string source = SourceLint.ReadStripped(PhysicsPath);

            Assert.That(source, Does.Contain("MirroredRooms => _roomGeometry;"));
            Assert.That(Count(source, PhysicsLoop), Is.GreaterThanOrEqualTo(2),
                "both TrySweepTiles and TryGetRoomTileQueryAt must iterate the whole room set");

            string sweep = SourceLint.MethodBody(
                source, "public bool TrySweepTiles(Vector2 fromPx, Vector2 deltaPx, Vector2 sizePx, Vector2 facing,");
            Assert.That(sweep, Does.Contain(PhysicsLoop),
                "a projectile in a non-presentation room must still be able to hit its tiles");
        }

        [Test]
        public void RulesFile_IsUnityFree()
        {
            string source = SourceLint.ReadStripped(RulesPath);

            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Contain("namespace PFE.Systems.Map"),
                "the rules file lives under Sim/Map but keeps the Sim/Map namespace convention");
        }

        // ---------------------------------------------------------------- mutation controls

        [Test]
        public void MutationControl_DroppingThePureRuleCall_IsDetected()
        {
            string real = Manager();
            Assert.That(HasSetBasedSurface(real), Is.True, "Precondition.");

            Assert.That(HasSetBasedSurface(real.Replace(ResolveCall, "LocalResolve(")), Is.False,
                "deleting the rule call was not detected");
            Assert.That(HasSetBasedSurface(real.Replace(DiffCall, "LocalDiff(")), Is.False,
                "deleting the transition diff was not detected");
        }

        [Test]
        public void MutationControl_DroppingTheSetBookkeeping_IsDetected()
        {
            string real = Manager();
            Assert.That(HasSetBasedSurface(real), Is.True, "Precondition.");

            Assert.That(HasSetBasedSurface(real.Replace(ActivateSet, "")), Is.False,
                "a room that is never added to the set is the pre-3.1 defect");
            Assert.That(HasSetBasedSurface(real.Replace(DeactivateSet, "")), Is.False,
                "a room that is never removed leaves stale chain geometry behind");
        }

        [Test]
        public void MutationControl_CurrentRoomBecomingASecondSourceOfTruth_IsDetected()
        {
            string real = Manager();
            Assert.That(HasSetBasedSurface(real), Is.True, "Precondition.");

            Assert.That(HasSetBasedSurface(
                real.Replace(CurrentRoomAlias, "public RoomInstance CurrentRoom => currentActiveRoom;")),
                Is.False, "restoring one global currentActiveRoom was not detected");
        }

        [Test]
        public void MutationControl_NarrowingThePhysicsSweep_IsDetected()
        {
            string source = SourceLint.ReadStripped(PhysicsPath);
            string sweep = SourceLint.MethodBody(
                source, "public bool TrySweepTiles(Vector2 fromPx, Vector2 deltaPx, Vector2 sizePx, Vector2 facing,");

            Assert.That(sweep, Does.Contain(PhysicsLoop), "Precondition.");
            Assert.That(sweep.Replace(PhysicsLoop, "if (false)"), Does.Not.Contain(PhysicsLoop),
                "the positive control must be able to fail");
        }

        [Test]
        public void EquivalenceControl_LiteralGoodAndBadShapes()
        {
            string good = "class Shape {" +
                          ActiveRoomsProperty +
                          "public RoomInstance PresentationRoom => _presentationRoom;" +
                          CurrentRoomAlias +
                          "public bool MaySimulate(RoomInstance room)" +
                          "public void ApplyRoomPresence(" +
                          ResolveCall + "p, k);" + DiffCall + "a, b, out x, out y);" +
                          "ActiveRoomSetRules.MaySimulate(p);" +
                          ActivateSet + DeactivateSet + "}";
            const string bad = "class Shape {" +
                               "public RoomInstance CurrentRoom => currentActiveRoom;" +
                               "ActivateRoomInternal(newRoom); }";

            Assert.That(HasSetBasedSurface(good), Is.True,
                "the predicate must accept a literal body carrying the reviewed shape");
            Assert.That(HasSetBasedSurface(bad), Is.False,
                "the pre-3.1 one-room shape must be rejected by the predicate itself, " +
                "independent of whatever is in the shipping file");
        }
    }
}
