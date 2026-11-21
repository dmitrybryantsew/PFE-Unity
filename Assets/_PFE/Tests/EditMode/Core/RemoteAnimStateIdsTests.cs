using NUnit.Framework;
using PFE.Character.Animation;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The animation-state id table is a protocol fact: <c>SimUnitState.PoseId</c> carries its
    /// bytes to the other machine, so a mapping mistake does not corrupt one animation — it
    /// corrupts the remote player's BODY for as long as the wrong id survives. Everything the
    /// table promises is pinned here: round-trip identity for every compiled-in state, the unknown
    /// sentinel in both directions, and the append-only stability of the wire ids.
    /// </summary>
    [TestFixture]
    public sealed class RemoteAnimStateIdsTests
    {
        [Test]
        public void EveryCompiledInState_RoundTripsThroughItsId()
        {
            // The locomotion set the driver evaluates continuously plus the action set triggered
            // externally — the full vocabulary a host can be playing when a snapshot is taken.
            string[] states =
            {
                "stay", "walk", "trot", "run", "jump", "polz", "roll", "plav", "levit", "laz",
                "pinok", "free1", "free2", "free3",
                "die", "dieali", "res", "punch", "kick", "derg",
                "lurk1", "lurk2", "lurk3", "unlurk",
            };

            foreach (string state in states)
            {
                byte id = RemoteAnimStateIds.ToId(state);
                Assert.AreNotEqual(RemoteAnimStateIds.Unknown, id,
                    $"'{state}' is a compiled-in state and must have a wire id.");
                Assert.AreEqual(state, RemoteAnimStateIds.ToName(id),
                    $"'{state}' must come back from its own id — the id IS the protocol.");
            }
        }

        [Test]
        public void UnknownName_MapsToUnknown_AndKeepsTheReceiverState()
        {
            Assert.AreEqual(RemoteAnimStateIds.Unknown, RemoteAnimStateIds.ToId("no_such_clip"),
                "A state this build does not know (a newer peer's name) must not fabricate an id.");
            Assert.AreEqual(RemoteAnimStateIds.Unknown, RemoteAnimStateIds.ToId(null));
            Assert.AreEqual(RemoteAnimStateIds.Unknown, RemoteAnimStateIds.ToId(string.Empty));
            // ToName(Unknown) is null so the apply path KEEPS its current state — see the doc on
            // the table; a wrong guess animates the remote player, a frozen one does not.
            Assert.IsNull(RemoteAnimStateIds.ToName(RemoteAnimStateIds.Unknown));
        }

        [Test]
        public void OutOfRangeId_MapsToNull_NotACrashAndNotAGuess()
        {
            Assert.IsNull(RemoteAnimStateIds.ToName(200));
            Assert.IsNull(RemoteAnimStateIds.ToName(RemoteAnimStateIds.Unknown));
        }

        [Test]
        public void IdsAreStable_AppendOnlyTable()
        {
            // The pinned ids the wire has already shipped: re-ordering or re-numbering the table
            // breaks every peer that applied an older snapshot. New states take the next free id.
            Assert.AreEqual(0, RemoteAnimStateIds.ToId("stay"));
            Assert.AreEqual(4, RemoteAnimStateIds.ToId("jump"));
            Assert.AreEqual(8, RemoteAnimStateIds.ToId("levit"));
            Assert.AreEqual(14, RemoteAnimStateIds.ToId("die"));
            Assert.AreEqual(23, RemoteAnimStateIds.ToId("unlurk"));
        }
    }
}
