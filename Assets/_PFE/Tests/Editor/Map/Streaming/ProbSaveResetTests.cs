using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Streaming;

namespace PFE.Tests.Editor.Map.Streaming
{
    /// <summary>
    /// A new land must forget the prob-room save — AS3 <c>Land.gotoProb</c>'s <c>prob</c>,
    /// <c>retLoc*</c> and <c>retX/retY</c> are fields of the <c>Land</c> object, so they start empty by
    /// construction (<c>Land.as:102-105</c>, <c>:1391-1438</c>).
    ///
    /// <para><b>The symptom this pins.</b> Leaving a prob room through its <c>exit</c> box — the way the
    /// plant descent actually ends (<c>allact='exit'</c> → <c>Game.gotoNextLevel</c>,
    /// <c>Game.as:464-472</c>) — rebuilds the whole land and never calls
    /// <c>TryReturnFromProb</c>, which was the only place the port cleared the save. The player stands
    /// in a brand-new land while <c>IsInProbRoom</c> still answers <c>true</c>, so <c>CanEnterProb</c> is
    /// false and every later prob door answers <c>Refused</c>: the interaction animation plays and
    /// nothing happens, for the rest of the run.</para>
    ///
    /// <para>Editor-only: <c>RoomTransitionManager</c> is a <c>MonoBehaviour</c>, so this fixture needs
    /// the engine like <see cref="DoorTransitionTests"/> does.</para>
    /// </summary>
    [TestFixture]
    public class ProbSaveResetTests
    {
        private GameObject _holder;

        [SetUp]
        public void Setup() => _holder = new GameObject("ProbSaveResetHolder");

        [TearDown]
        public void Teardown()
        {
            if (_holder != null) Object.DestroyImmediate(_holder);
        }

        [Test]
        public void SetProbContext_ForANewLand_ClearsTheProbSave()
        {
            RoomTransitionManager manager = _holder.AddComponent<RoomTransitionManager>();

            // Arrange the "player is inside a prob room" state without a live transition: the save is
            // what `IsInProbRoom` reads, and it is the only thing this test needs to be set.
            typeof(RoomTransitionManager)
                .GetField("_savedRoom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(manager, new RoomInstance { id = "stale_prob_room" });

            Assert.That(manager.IsInProbRoom, Is.True, "Arrange: the save must really be set.");

            // A land build hands over the new land's prob rooms.
            manager.SetProbContext(null);

            Assert.That(manager.IsInProbRoom, Is.False,
                "A new Land starts with no prob save (Land.as:102-105). Leaving this set makes every later " +
                "prob door answer Refused, which is the 'second descent will not let me through' symptom.");
        }

        [Test]
        public void SetProbContext_DoesNotAffectCanEnterProbBeforeAnyRoomIsBuilt()
        {
            // A null context still means "no prob rooms", so the door must refuse — but for the right
            // reason (no registry), not because a stale save says the player is already inside one.
            RoomTransitionManager manager = _holder.AddComponent<RoomTransitionManager>();
            manager.SetProbContext(null);

            Assert.That(manager.CanEnterProb, Is.False);
            Assert.That(manager.IsInProbRoom, Is.False);
        }
    }
}
