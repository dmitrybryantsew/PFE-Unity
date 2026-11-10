using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Offline fixture for Phase 3.1's pure <see cref="ActiveRoomSetRules"/>.
    ///
    /// <para><b>Why the rule is a separate Unity-free class.</b> The acceptance in the readiness plan
    /// is "two scripted players in two different rooms both simulate". The manager that owns the rooms
    /// is a <c>MonoBehaviour</c> with <c>Update()</c>-frame bookkeeping, and no offline harness can
    /// instantiate one. So the <i>decision</i> lives here where it can actually be executed, and the
    /// <i>wiring</i> is pinned by <c>ActiveRoomSetWiringLintTests</c>.</para>
    ///
    /// <para><b>Controls.</b> Every positive claim has an absent counterpart (zero/negative presence,
    /// the zero key, a room nobody is in). <see cref="PreRuleShape_ASingleGlobalRoom_LosesTheSecondPlayer"/>
    /// reconstructs the one-room behaviour this phase replaces, so the green two-room test cannot be
    /// a restatement of a rule that was always true. A scripted mutation run against the shipped file
    /// supplies the external "can this guard fail" control.</para>
    /// </summary>
    [TestFixture]
    public sealed class ActiveRoomSetRulesTests
    {
        private const ulong RoomA = 0x1111111111111111UL;
        private const ulong RoomB = 0x2222222222222222UL;
        private const ulong RoomC = 0x3333333333333333UL;

        private static ActiveRoomPresence Present(ulong room, int players = 1)
        {
            return new ActiveRoomPresence(room, players);
        }

        private static ulong[] Resolve(ulong presentation, params ActiveRoomPresence[] presence)
        {
            return ActiveRoomSetRules.ResolveActiveRooms(presence, presentation);
        }

        // ---------------------------------------------------------------- presence / activity

        [Test]
        public void EmptyPresence_NoPresentation_ResolvesNoRooms()
        {
            Assert.That(Resolve(ActiveRoomSetRules.NoPresentationRoom), Is.Empty);
        }

        [Test]
        public void TwoRoomsWithPlayers_AreBothActive_AndBothMaySimulate()
        {
            // The plan's acceptance, in its pure form: two scripted players in two different rooms.
            ulong[] active = Resolve(RoomA, Present(RoomA, 1), Present(RoomB, 2));

            Assert.That(active, Is.EquivalentTo(new[] { RoomA, RoomB }),
                "both players' rooms must be in the active set");
            Assert.That(ActiveRoomSetRules.MaySimulate(Present(RoomA, 1)), Is.True);
            Assert.That(ActiveRoomSetRules.MaySimulate(Present(RoomB, 2)), Is.True,
                "the second player's room must be allowed to originate simulation state");
        }

        [Test]
        public void PresentationRoomWithoutPlayers_IsActiveButDoesNotSimulate()
        {
            var viewOnly = Present(RoomA, 0);

            Assert.That(Resolve(RoomA, viewOnly), Is.EqualTo(new[] { RoomA }),
                "the camera's room needs tile geometry whether or not a player stands in it");
            Assert.That(ActiveRoomSetRules.IsActive(viewOnly, RoomA), Is.True);
            Assert.That(ActiveRoomSetRules.MaySimulate(viewOnly), Is.False,
                "'active' is a geometry word, 'simulate' is a truth word");
        }

        [Test]
        public void RoomWithNoPlayers_IsNeitherActiveNorSimulating()
        {
            // Absent control for the two-room test above: a room that merely exists is not in the set.
            ActiveRoomPresence empty = Present(RoomC, 0);

            Assert.That(Resolve(RoomA, Present(RoomA, 1), empty), Is.EqualTo(new[] { RoomA }));
            Assert.That(ActiveRoomSetRules.IsActive(empty, RoomA), Is.False);
            Assert.That(ActiveRoomSetRules.MaySimulate(empty), Is.False);
        }

        [Test]
        public void MaySimulate_IsNarrowerThanIsActive()
        {
            ActiveRoomPresence viewOnly = Present(RoomB, 0);

            Assert.That(ActiveRoomSetRules.IsActive(viewOnly, RoomB), Is.True);
            Assert.That(ActiveRoomSetRules.MaySimulate(viewOnly), Is.False,
                "IsActive and MaySimulate must not be the same predicate");
        }

        [Test]
        public void HasPresence_ZeroAndNegativeAreAbsent()
        {
            Assert.That(ActiveRoomSetRules.HasPresence(1), Is.True);
            Assert.That(ActiveRoomSetRules.HasPresence(4), Is.True);
            Assert.That(ActiveRoomSetRules.HasPresence(0), Is.False);
            Assert.That(ActiveRoomSetRules.HasPresence(-1), Is.False,
                "a negative count is a bookkeeping error, not a player");
        }

        [Test]
        public void NegativePlayerCount_DoesNotActivateTheRoom()
        {
            Assert.That(Resolve(RoomA, Present(RoomA, 1), Present(RoomB, -3)),
                Is.EqualTo(new[] { RoomA }));
        }

        [Test]
        public void ZeroRoomKey_IsNeverEmitted()
        {
            // Zero is the "no presentation room" sentinel. A caller that hashes an empty room id must
            // not be able to smuggle it into the set and make it indistinguishable from the sentinel.
            Assert.That(Resolve(RoomA, Present(0UL, 2), Present(RoomA, 1)),
                Is.EqualTo(new[] { RoomA }));
            Assert.That(Resolve(ActiveRoomSetRules.NoPresentationRoom, Present(0UL, 2)), Is.Empty);
        }

        // ---------------------------------------------------------------- determinism / dedupe

        [Test]
        public void Resolve_PutsPresentationFirst_AndKeepsPresenceOrder()
        {
            ulong[] active = Resolve(RoomC, Present(RoomB, 1), Present(RoomA, 1), Present(RoomC, 1));

            Assert.That(active, Is.EqualTo(new[] { RoomC, RoomB, RoomA }),
                "event order must be stable: PhysicsWorldService builds geometry in this order");
        }

        [Test]
        public void DuplicatePresenceKeys_CollapseToOneRoom()
        {
            ulong[] active = Resolve(RoomA, Present(RoomA, 1), Present(RoomA, 2), Present(RoomB, 1));

            Assert.That(active, Is.EqualTo(new[] { RoomA, RoomB }));
        }

        [Test]
        public void Resolve_NullPresence_StillReturnsThePresentationRoom()
        {
            Assert.That(ActiveRoomSetRules.ResolveActiveRooms(null, RoomB),
                Is.EqualTo(new[] { RoomB }));
            Assert.That(ActiveRoomSetRules.ResolveActiveRooms(null, ActiveRoomSetRules.NoPresentationRoom),
                Is.Empty);
        }

        // ---------------------------------------------------------------- transitions

        [Test]
        public void Diff_DeactivatesDroppedRooms_AndActivatesNewRooms()
        {
            ActiveRoomSetRules.Diff(
                new[] { RoomA, RoomB },
                new[] { RoomB, RoomC },
                out ulong[] activate,
                out ulong[] deactivate);

            Assert.That(deactivate, Is.EqualTo(new[] { RoomA }));
            Assert.That(activate, Is.EqualTo(new[] { RoomC }),
                "a room already active must not be torn down and rebuilt");
        }

        [Test]
        public void Diff_SecondApplicationIsEmpty()
        {
            ulong[] desired = Resolve(RoomA, Present(RoomA, 1), Present(RoomB, 1));
            var current = new List<ulong>();
            ActiveRoomSetRules.Diff(current, desired, out ulong[] firstAdd, out _);
            current.AddRange(firstAdd);

            ActiveRoomSetRules.Diff(current, desired, out ulong[] add, out ulong[] drop);

            Assert.That(firstAdd, Is.EqualTo(new[] { RoomA, RoomB }));
            Assert.That(add, Is.Empty, "reconciling an unchanged presence set must be a no-op");
            Assert.That(drop, Is.Empty);
        }

        [Test]
        public void Diff_NeverNamesTheSameKeyInBothDirections()
        {
            ActiveRoomSetRules.Diff(
                new[] { RoomA, RoomB },
                new[] { RoomB, RoomC },
                out ulong[] activate,
                out ulong[] deactivate);

            foreach (ulong key in activate)
            {
                // Does.Not.Contain binds to the string overload in this NUnit version; Array.IndexOf
                // keeps the assertion about the ulong[] itself.
                Assert.That(Array.IndexOf(deactivate, key), Is.LessThan(0));
            }
        }

        [Test]
        public void Diff_DuplicateCurrentKeys_ProduceOneDeactivation()
        {
            ActiveRoomSetRules.Diff(
                new[] { RoomA, RoomA, RoomA },
                new[] { RoomB },
                out ulong[] activate,
                out ulong[] deactivate);

            Assert.That(deactivate, Is.EqualTo(new[] { RoomA }));
            Assert.That(activate, Is.EqualTo(new[] { RoomB }));
        }

        [Test]
        public void Diff_NullInputs_DoNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                ActiveRoomSetRules.Diff(null, null, out ulong[] add, out ulong[] drop);
                Assert.That(add, Is.Empty);
                Assert.That(drop, Is.Empty);
            });
        }

        // ---------------------------------------------------------------- non-vacuity controls

        [Test]
        public void PreRuleShape_ASingleGlobalRoom_LosesTheSecondPlayer()
        {
            // Reconstruct what the pre-3.1 one-room model would have produced for the same input:
            // the presentation room only. This is the defect the set-based rule exists to remove.
            var oldShape = new List<ulong> { RoomA };
            ulong[] setBased = Resolve(RoomA, Present(RoomA, 1), Present(RoomB, 1));

            Assert.That(setBased, Does.Contain(RoomB));
            Assert.That(oldShape.Contains(RoomB), Is.False,
                "the one-room shape must be observably different, or the two-room test proves nothing");
        }

        [Test]
        public void EquivalenceControl_ZeroPresenceIsNotPresence()
        {
            // The obvious wrong implementation of HasPresence is `>= 0`. Pin the boundary explicitly.
            int zero = 0;
            Assert.That(ActiveRoomSetRules.HasPresence(zero), Is.False);
            Assert.That(zero >= 0, Is.True,
                "the tempting `>= 0` variant disagrees with the rule at exactly this input");
        }
    }
}
