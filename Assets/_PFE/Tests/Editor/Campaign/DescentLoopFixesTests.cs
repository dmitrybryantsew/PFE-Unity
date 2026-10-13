using System.Collections.Generic;
using MessagePipe;
using NUnit.Framework;
using PFE.Core.Messages;
using PFE.Systems.Campaign;
using PFE.Systems.Map;
using PFE.Systems.Map.Scripting;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// The three latches that froze the descent loop at stage 0, plus the room-picker rules they
    /// were hiding.
    ///
    /// <para><b>What the symptom was.</b> The plant rebuilt on every descent but never deepened: still
    /// 4x3 instead of 4x6, still the shallowest rooms, and after the first descent the exit door stopped
    /// working altogether.</para>
    ///
    /// <para><b>Why it could not go red on its own.</b> All three failures are "the state did not
    /// change", and a rebuild looks exactly like progress: the land visibly regenerates, so nothing
    /// logged an error. Each test below asserts the <em>state change</em>, not the rebuild.</para>
    /// </summary>
    [TestFixture]
    public class DescentLoopFixesTests
    {
        private sealed class RecordingPublisher<T> : IPublisher<T>
        {
            public readonly List<T> Published = new List<T>();
            public void Publish(T message) => Published.Add(message);
        }

        private static CampaignManager NewManager(out RecordingPublisher<LandBuildRequestMessage> builds)
        {
            var b = new RecordingPublisher<LandBuildRequestMessage>();
            builds = b;
            var manager = new CampaignManager(
                transitionSubscriber: null,
                transitionPublisher: new RecordingPublisher<LandTransitionMessage>(),
                catalog: null,
                buildPublisher: b);
            manager.Initialize();
            return manager;
        }

        [TearDown]
        public void Teardown()
        {
            // `CampaignManager.Current` is a process-wide static and `AreaTriggerSystem` now reads it,
            // so a leaked instance would silently change what every later fixture's "no host" case does.
            if (CampaignManager.Current != null) CampaignManager.Current.Dispose();
        }

        // =====================================================================
        //  1 — `upland` must reach a campaign even when the system was built
        //      with the parameterless constructor
        // =====================================================================

        /// <summary>
        /// The only production construction site is <c>RoomVisualController.Initialize</c>
        /// (<c>RoomVisualController.cs:527</c>), which calls <c>new AreaTriggerSystem()</c> — no
        /// collaborators at all. So <c>upland</c> never raised <c>landStage</c>, and the land rebuilt
        /// at stage 0 for ever.
        /// </summary>
        [Test]
        public void Upland_WithNoInjectedHost_StillRaisesTheLiveCampaignsLandStage()
        {
            CampaignManager manager = NewManager(out _);
            manager.TransitionToLand("random_plant");

            var system = new AreaTriggerSystem();
            system.ExecuteAction(new RoomInstance { id = "r" }, new MapObjectScriptActionData { act = "upland" });

            Assert.That(manager.LandStates.Get("random_plant").landStage, Is.EqualTo(1),
                "AS3 `upland` -> Game.upLandLevel (Script.as:396-399). Without this the conf-0 grid clamp " +
                "(Land.as:180-183) pins the plant at 4x3 for the whole run.");
        }

        /// <summary>
        /// The positive control for the case above: with no campaign either, an unhosted <c>upland</c>
        /// is still a silent no-op rather than an exception.
        /// </summary>
        [Test]
        public void Upland_WithNoHostAndNoCampaign_ReportsNoMove()
        {
            // No manager constructed, so CampaignManager.Current is null.
            var system = new AreaTriggerSystem();
            bool? reported = null;
            system.OnUpLandLevel += moved => reported = moved;

            Assert.DoesNotThrow(() =>
                system.ExecuteAction(new RoomInstance { id = "r" }, new MapObjectScriptActionData { act = "upland" }));
            Assert.That(reported, Is.False);
        }

        // =====================================================================
        //  2 — `gotoNextLevel` must clear `upStage`
        // =====================================================================

        /// <summary>
        /// AS3 reaches the next level through <c>gotoLand(missionId)</c> (<c>Game.as:471</c>) and
        /// <c>gotoLand</c> calls <c>enterToCurLand</c>, which ends with
        /// <c>if(curLand.upStage) curLand.upStage = false;</c> (<c>Game.as:398-400</c>).
        ///
        /// <para><c>GotoNextLevel</c> publishes a build request directly and never went through
        /// <c>TransitionToLand</c>, where the port's only <c>ResetUpStage</c> lives. So after one descent
        /// <c>upStage</c> stayed set and every later <c>upland</c> was the "already raised this entry"
        /// no-op: landStage froze at 1 however many times the player descended.</para>
        /// </summary>
        [Test]
        public void GotoNextLevel_ClearsUpStage_SoTheNextDescentRaisesTheStageAgain()
        {
            CampaignManager manager = NewManager(out RecordingPublisher<LandBuildRequestMessage> builds);
            manager.TransitionToLand("random_plant");

            Assert.That(manager.UpLandLevel(), Is.True, "First upland of the visit raises landStage to 1.");
            Assert.That(manager.LandStates.Get("random_plant").landStage, Is.EqualTo(1));

            manager.GotoNextLevel();
            Assert.That(builds.Published.Count, Is.EqualTo(2),
                "One request from the transition, one from gotoNextLevel.");
            Assert.That(builds.Published[1].ForceRegenerate, Is.True,
                "AS3 sets `crea = true` before re-entering the land (Game.as:468).");

            Assert.That(manager.UpLandLevel(), Is.True,
                "Game.as:398-400 clears upStage on entry, so the next run's upland must move again.");
            Assert.That(manager.LandStates.Get("random_plant").landStage, Is.EqualTo(2),
                "Two descents must mean landStage 2 — this is the 'second level still 3x4' bug.");
        }

        /// <summary>
        /// Control: without the <c>GotoNextLevel</c> step the latch still holds. Proves the assertion
        /// above is about the clear, not about <c>upland</c> being unlatched.
        /// </summary>
        [Test]
        public void UpLandLevel_TwiceInOneVisit_IncrementsOnlyOnce()
        {
            CampaignManager manager = NewManager(out _);
            manager.TransitionToLand("random_plant");

            Assert.That(manager.UpLandLevel(), Is.True);
            Assert.That(manager.UpLandLevel(), Is.False, "AS3 Game.upLandLevel latches on upStage (Game.as:476-480).");
            Assert.That(manager.LandStates.Get("random_plant").landStage, Is.EqualTo(1));
        }

        // =====================================================================
        //  3 — the room picker: AS3's `kol` weight and its exhaustion fallback
        // =====================================================================

        private static RoomTemplate Template(string id, int level, int maxInstances, bool allowRandom = true, string type = "")
        {
            RoomTemplate t = PFE.Tests.Editor.Core.OfflineScriptableObject.Create<RoomTemplate>();
            t.id = id;
            t.type = type;
            t.difficultyLevel = level;
            t.maxInstances = maxInstances;
            t.allowRandom = allowRandom;
            return t;
        }

        [Test]
        public void DrawWeight_IsKolSquared()
        {
            Assert.That(RoomGenerator.DrawWeight(3, 1, 0, false), Is.EqualTo(9), "Land.as:909 — `_loc9_ = kol * kol`.");
            Assert.That(RoomGenerator.DrawWeight(2, 1, 0, false), Is.EqualTo(4));
            Assert.That(RoomGenerator.DrawWeight(1, 1, 0, false), Is.EqualTo(1));
        }

        [Test]
        public void DrawWeight_HalvesForAStage0RoomOnceTheLandIsDeeperThanOne()
        {
            // Land.as:910-913 — `_loc9_ == 4 && lvl == 0 && param1 > 1` -> 2.
            Assert.That(RoomGenerator.DrawWeight(2, 0, 2, false), Is.EqualTo(2));
            Assert.That(RoomGenerator.DrawWeight(2, 0, 3, false), Is.EqualTo(2));
        }

        [Test]
        public void DrawWeight_LeavesTheExceptionAloneOnTheShallowLevels()
        {
            // `param1 > 1` is the gate: stage 0 and 1 keep weight 4.
            Assert.That(RoomGenerator.DrawWeight(2, 0, 0, false), Is.EqualTo(4));
            Assert.That(RoomGenerator.DrawWeight(2, 0, 1, false), Is.EqualTo(4));
        }

        [Test]
        public void DrawWeight_LeavesTheExceptionAloneForADeeperRoom()
        {
            // `lvl == 0` is the other gate: a level-1 room keeps weight 4 however deep the land is.
            Assert.That(RoomGenerator.DrawWeight(2, 1, 4, false), Is.EqualTo(4));
        }

        /// <summary>
        /// AS3's exhaustion fallback (<c>Land.as:926-935</c>): when every eligible room has spent its
        /// <c>kol</c>, AS3 re-pools on <c>rnd</c> alone and draws from that — it never leaves the cell
        /// empty. This is what makes honouring <c>maxInstances</c> safe: without it a grid would develop
        /// holes, and a land can then be missing the room its own exit lives in.
        /// </summary>
        [Test]
        public void SelectRandomRoom_WhenEveryRoomHasSpentItsKol_StillReturnsARoom()
        {
            var generator = new RoomGenerator();
            generator.ConfigureGenerationMode(usePrototypeMode: false);
            generator.Initialize(new List<RoomTemplate> { Template("a", 0, 1), Template("b", 0, 1) });

            Assert.That(generator.SelectRandomRoom(0), Is.Not.Null, "First cell.");
            Assert.That(generator.SelectRandomRoom(0), Is.Not.Null, "Second cell — both rooms have now spent their kol.");
            Assert.That(generator.SelectRandomRoom(0), Is.Not.Null,
                "Third cell: the pool is exhausted, so AS3 re-pools on `rnd` alone (Land.as:926-935).");
        }

        /// <summary>
        /// Control for the case above: with no room marked <c>rnd</c> there is nothing to fall back to,
        /// and <c>null</c> is the honest answer (the caller logs and skips the cell).
        /// </summary>
        [Test]
        public void SelectRandomRoom_WhenNoRoomIsRandomAtAll_ReturnsNull()
        {
            var generator = new RoomGenerator();
            generator.ConfigureGenerationMode(usePrototypeMode: false);
            generator.Initialize(new List<RoomTemplate> { Template("a", 0, 1, allowRandom: false) });

            Assert.That(generator.SelectRandomRoom(0), Is.Null);
        }
    }
}
