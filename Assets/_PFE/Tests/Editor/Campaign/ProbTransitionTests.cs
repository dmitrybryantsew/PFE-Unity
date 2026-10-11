using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Systems.Map.Generation;
using UnityEngine;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// <see cref="ProbTransition"/> — the pure half of AS3 <c>Land.gotoProb</c>
    /// (<c>Land.as:1391-1438</c>): what an entry saves, where the return door puts the player, and which
    /// rooms a grid step may start from (<c>Land.gotoLoc</c>'s <c>this.prob</c> branch).
    ///
    /// <para>Offline, like <see cref="ProbationStateTests"/>: <c>Vector3Int</c>/<c>float</c> only, plus a
    /// bare <see cref="RoomInstance"/> — a plain class, so it constructs in a plain shell where a
    /// <c>RoomTemplate</c> (a <c>ScriptableObject</c>) would fail with <c>ECall</c>. See
    /// <c>ProbPlacementTests</c> for the same note.</para>
    /// </summary>
    [TestFixture]
    public class ProbTransitionTests
    {
        static readonly Vector3Int LandCell = new Vector3Int(3, 5, 1);

        // ── the entry save ─────────────────────────────────────────────────────────────────

        [Test]
        public void SaveReturnPoint_RemembersTheRoomThatWasLeft()
        {
            ProbReturnPoint saved = ProbTransition.SaveReturnPoint(LandCell, 100f, 200f, 400f, 500f);
            Assert.AreEqual(LandCell, saved.LandPosition);
        }

        [Test]
        public void SaveReturnPoint_PrefersTheDoorPositionOverThePlayers()
        {
            // Interact.as:1565/1576 always pass the interactable's own X/Y, so this is the door path.
            // Using the player's position would drop them back inside the door they just came through.
            ProbReturnPoint saved = ProbTransition.SaveReturnPoint(LandCell, 100f, 200f, 400f, 500f);
            Assert.AreEqual(400f, saved.PlayerX);
            Assert.AreEqual(500f, saved.PlayerY);
        }

        [Test]
        public void SaveReturnPoint_FallsBackToThePlayerWhenNoDoorPositionIsGiven()
        {
            // AS3's sentinel is `param2 < 0 || param3 < 0` (Land.as:1417).
            ProbReturnPoint saved = ProbTransition.SaveReturnPoint(LandCell, 100f, 200f);
            Assert.AreEqual(100f, saved.PlayerX);
            Assert.AreEqual(200f, saved.PlayerY);
        }

        [Test]
        public void SaveReturnPoint_WithOnlyOneNegativeCoordinate_AlsoFallsBack()
        {
            // The oracle ORs the two tests, so one missing coordinate discards the pair.
            ProbReturnPoint saved = ProbTransition.SaveReturnPoint(LandCell, 100f, 200f, 400f, -1f);
            Assert.AreEqual(100f, saved.PlayerX);
            Assert.AreEqual(200f, saved.PlayerY);
        }

        [Test]
        public void SaveReturnPoint_TreatsZeroAsASuppliedCoordinate()
        {
            // `param2 < 0` is the test, so 0 is a real position — unlike the *saved* (0,0) pair, which is
            // the return-side sentinel. The two zeros are different rules and this is the one place the
            // difference is visible.
            ProbReturnPoint saved = ProbTransition.SaveReturnPoint(LandCell, 100f, 200f, 0f, 0f);
            Assert.AreEqual(0f, saved.PlayerX);
            Assert.AreEqual(0f, saved.PlayerY);
        }

        [Test]
        public void SaveReturnPoint_OfTheOriginCell_KeepsThatCell()
        {
            // A real room at (0,0,0) is a perfectly good return room; only the *player* position has a
            // (0,0) sentinel.
            ProbReturnPoint saved = ProbTransition.SaveReturnPoint(Vector3Int.zero, 10f, 20f, 30f, 40f);
            Assert.AreEqual(Vector3Int.zero, saved.LandPosition);
            Assert.AreEqual(30f, saved.PlayerX);
        }

        // ── the return plan ────────────────────────────────────────────────────────────────

        [Test]
        public void PlanReturn_UsesTheSavedPositionWhenThereIsOne()
        {
            var saved = new ProbReturnPoint(LandCell, 400f, 500f);
            ProbArrival arrival = ProbTransition.PlanReturn(saved);
            Assert.AreEqual(ProbArrivalKind.SavedPlayerPosition, arrival.Kind);
            Assert.AreEqual(LandCell, arrival.LandPosition);
            Assert.AreEqual(400f, arrival.PlayerX);
            Assert.AreEqual(500f, arrival.PlayerY);
        }

        [Test]
        public void PlanReturn_AsksForTheSpawnPointWhenTheSavedPositionIsTheZeroSentinel()
        {
            // Land.as:1401-1408 — `retX == 0 && retY == 0` means "none recorded".
            var saved = new ProbReturnPoint(LandCell, 0f, 0f);
            ProbArrival arrival = ProbTransition.PlanReturn(saved);
            Assert.AreEqual(ProbArrivalKind.RoomSpawnPoint, arrival.Kind);
            Assert.AreEqual(LandCell, arrival.LandPosition);
        }

        [Test]
        public void PlanReturn_WithOnlyOneZeroCoordinate_UsesTheSavedPosition()
        {
            // The sentinel is the *pair*; a genuine (x, 0) is a position on the room's top edge.
            var saved = new ProbReturnPoint(LandCell, 250f, 0f);
            Assert.AreEqual(ProbArrivalKind.SavedPlayerPosition, ProbTransition.PlanReturn(saved).Kind);
        }

        [Test]
        public void HasRecordedPosition_MatchesTheSentinelTest()
        {
            Assert.IsTrue(ProbTransition.HasRecordedPosition(new ProbReturnPoint(LandCell, 1f, 0f)));
            Assert.IsTrue(ProbTransition.HasRecordedPosition(new ProbReturnPoint(LandCell, 0f, 1f)));
            Assert.IsFalse(ProbTransition.HasRecordedPosition(new ProbReturnPoint(LandCell, 0f, 0f)));
        }

        // ── the failed-entry rollback ──────────────────────────────────────────────────────

        [Test]
        public void RollbackLandPosition_RestoresTheRoomThatWasLeft()
        {
            var saved = new ProbReturnPoint(LandCell, 400f, 500f);
            Assert.AreEqual(LandCell, ProbTransition.RollbackLandPosition(saved));
        }

        // ── the constants the adapter keys off ─────────────────────────────────────────────

        [Test]
        public void ReturnActionId_IsTheDooroutsAllact()
        {
            // AllData.as:5017 — `<obj id='doorout' … allact='probreturn'/>`.
            Assert.AreEqual("probreturn", ProbTransition.ReturnActionId);
        }

        [Test]
        public void ProbRoomLandPosition_IsTheOriginTheOracleWrites()
        {
            // Land.as:1425 — `locX = locY = locZ = 0`. This is also the coordinate ProbRoomBuilder
            // builds the room at (Land.as:789), because in this port a room's coordinate IS its world
            // position: render origin, ITileQueryService.OriginPixel and player spawn are all derived
            // from it. A prob room built at an out-of-grid sentinel therefore rendered and collided
            // ~4e10 units from the player who had just entered it.
            //
            // The builder's own call site is not observable offline — it needs a RoomTemplate, which is
            // a ScriptableObject — so this pins the value and the play test pins the rest.
            Assert.AreEqual(Vector3Int.zero, ProbTransition.ProbRoomLandPosition);
        }

        // ── the grid a prob room is off ────────────────────────────────────────────────────

        [Test]
        public void AdmitsGridStep_RefusesAProbRoom()
        {
            // AS3 gotoLoc reads a prob room's neighbours from `probs[this.prob]` rather than `locs`
            // (Land.as:1335 vs :1343), and a prob land's grid holds exactly one room — this one — so
            // every neighbour lookup misses and the step refuses. RoomInstance.probId is non-empty for
            // exactly the rooms AS3 keeps in `probs`.
            var probRoom = new RoomInstance { probId = "exit_plant" };

            Assert.IsFalse(ProbTransition.AdmitsGridStep(probRoom));
        }

        [Test]
        public void AdmitsGridStep_AllowsARoomThatIsOnTheLandGrid()
        {
            // Control: the predicate is not a constant. A grid room is one with no probId.
            var gridRoom = new RoomInstance { id = "room_3_5_1", probId = string.Empty };

            Assert.IsTrue(ProbTransition.AdmitsGridStep(gridRoom));
        }

        [Test]
        public void AdmitsGridStep_TreatsANullProbIdAsAGridRoom()
        {
            // `probId` is `string.Empty` by construction, but a room read back out of a save can carry
            // null. `IsNullOrEmpty` is the test precisely so a null id does not read as "this is a prob
            // room" and refuse a step in an ordinary room.
            var room = new RoomInstance { probId = null };

            Assert.IsTrue(ProbTransition.AdmitsGridStep(room));
        }

        [Test]
        public void AdmitsGridStep_NullRoom_IsRefused()
        {
            Assert.IsFalse(ProbTransition.AdmitsGridStep(null));
        }
    }
}
