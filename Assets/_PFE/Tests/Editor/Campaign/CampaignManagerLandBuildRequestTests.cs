using System.Collections.Generic;
using MessagePipe;
using NUnit.Framework;
using PFE.Core.Messages;
using PFE.Systems.Campaign;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// The L0 acceptance check from <c>docs/LandGameplayLoop/04_IMPLEMENTATION_PLAN.md</c> §L0:
    /// <i>"A test asserts <c>TransitionToLand("surf")</c> publishes exactly one
    /// <c>LandBuildRequestMessage { LandId = "surf" }</c>, and that no <c>Debug.Log</c> claims success
    /// when the catalog lookup failed."</i>
    ///
    /// <para>This is the seam that closes gap §1: before it, <c>CampaignManager.TransitionToLand</c>
    /// only logged while <c>MapBridge.PerformLandTransition</c> really rebuilt, so a transition could
    /// look like it worked and change nothing.</para>
    /// </summary>
    [TestFixture]
    public class CampaignManagerLandBuildRequestTests
    {
        /// <summary>Records everything published, so "exactly one" is checkable rather than assumed.</summary>
        private sealed class RecordingPublisher<T> : IPublisher<T>
        {
            public readonly List<T> Published = new List<T>();

            public void Publish(T message)
            {
                Published.Add(message);
            }
        }

        private static RecordingPublisher<LandBuildRequestMessage> Build(RecordingPublisher<LandTransitionMessage> transitions,
            out CampaignManager manager)
        {
            var builds = new RecordingPublisher<LandBuildRequestMessage>();
            // catalog: null on purpose — no Resources.Load, no engine. `TransitionToLand` documents the
            // "transition by id only" path for exactly this case.
            manager = new CampaignManager(
                transitionSubscriber: null,
                transitionPublisher: transitions,
                catalog: null,
                buildPublisher: builds);
            return builds;
        }

        [Test]
        public void TransitionToLand_PublishesExactlyOneBuildRequest()
        {
            RecordingPublisher<LandTransitionMessage> transitions = new RecordingPublisher<LandTransitionMessage>();
            RecordingPublisher<LandBuildRequestMessage> builds = Build(transitions, out CampaignManager manager);

            manager.TransitionToLand("surf");

            Assert.That(builds.Published.Count, Is.EqualTo(1),
                "Exactly one build request — two would rebuild the land twice, zero would be the old dead path.");
            Assert.That(builds.Published[0].LandId, Is.EqualTo("surf"));
            Assert.That(builds.Published[0].EntryCoordinates, Is.Null);
            Assert.That(builds.Published[0].ForceRegenerate, Is.False,
                "A plain transition is not a forced regenerate (AS3 Game.crea).");

            // Negative control: the old path published LandTransitionMessage, which is the *request*.
            // A transition must not re-publish it, or the subscriber would loop.
            Assert.That(transitions.Published, Is.Empty);
        }

        [Test]
        public void TransitionToLand_CarriesTheSpawnPointAndTheForceFlag()
        {
            RecordingPublisher<LandTransitionMessage> transitions = new RecordingPublisher<LandTransitionMessage>();
            RecordingPublisher<LandBuildRequestMessage> builds = Build(transitions, out CampaignManager manager);

            manager.TransitionToLand("random_plant", "3:4", forceRegenerate: true);

            Assert.That(builds.Published.Count, Is.EqualTo(1));
            Assert.That(builds.Published[0].LandId, Is.EqualTo("random_plant"));
            Assert.That(builds.Published[0].EntryCoordinates, Is.EqualTo("3:4"));
            Assert.That(builds.Published[0].ForceRegenerate, Is.True,
                "`crea` must survive the hop, or a forced regenerate silently becomes a plain one.");
        }

        [Test]
        public void TransitionToLand_EmptyId_PublishesNothing()
        {
            RecordingPublisher<LandTransitionMessage> transitions = new RecordingPublisher<LandTransitionMessage>();
            RecordingPublisher<LandBuildRequestMessage> builds = Build(transitions, out CampaignManager manager);

            manager.TransitionToLand("   ");

            Assert.That(builds.Published, Is.Empty, "An empty land id must not queue a build.");

            // Positive control: the same instance does publish for a real id, so the assertion above is
            // about the guard and not about a broken publisher.
            manager.TransitionToLand("begin");
            Assert.That(builds.Published.Count, Is.EqualTo(1));
        }

        [Test]
        public void TransitionToLand_SetsTheCurrentLandId()
        {
            RecordingPublisher<LandTransitionMessage> transitions = new RecordingPublisher<LandTransitionMessage>();
            Build(transitions, out CampaignManager manager);

            manager.TransitionToLand("surf");

            Assert.That(manager.CurrentLandId.CurrentValue, Is.EqualTo("surf"),
                "The state owner must record where the player now is, independently of the builder.");
        }

        [Test]
        public void TransitionToLand_ClearsUpStageSoTheNextRunCanDescendAgain()
        {
            RecordingPublisher<LandTransitionMessage> transitions = new RecordingPublisher<LandTransitionMessage>();
            Build(transitions, out CampaignManager manager);

            // Enter surf, descend once (upland), leave, and come back.
            manager.TransitionToLand("surf");
            Assert.That(manager.UpLandLevel(), Is.True);
            Assert.That(manager.UpLandLevel(), Is.False, "A second upland in the same visit is a no-op.");

            manager.TransitionToLand("rbl");
            manager.TransitionToLand("surf");

            Assert.That(manager.UpLandLevel(), Is.True,
                "Re-entering a land clears upStage (Game.as:396-399), so the next run descends again.");
            Assert.That(manager.LandStates.Get("surf").landStage, Is.EqualTo(2));
        }

        [Test]
        public void Constructor_DoesNotTouchTheEngine()
        {
            // The constructor used to call Resources.Load unconditionally. Offline that is an ECall and
            // throws, which made the whole class impossible to construct — including in this fixture.
            // If that regresses, this fixture dies in SetUp with a SecurityException rather than failing
            // an assertion, which is exactly how the regression would present.
            RecordingPublisher<LandTransitionMessage> transitions = new RecordingPublisher<LandTransitionMessage>();
            CampaignManager manager = new CampaignManager(null, transitions, null, null);

            Assert.That(manager.Catalog, Is.Null,
                "No catalogue was supplied and none can be loaded here — and that must not throw.");
            Assert.That(manager.LandStates.Count, Is.EqualTo(0));
        }

        [Test]
        public void GotoNextLevel_ForcesARegenerate()
        {
            // `exit` → gotoNextLevel. AS3's next level is a fresh build, so it must be forced.
            RecordingPublisher<LandTransitionMessage> transitions = new RecordingPublisher<LandTransitionMessage>();
            RecordingPublisher<LandBuildRequestMessage> builds = Build(transitions, out CampaignManager manager);

            manager.TransitionToLand("random_plant");
            builds.Published.Clear();

            manager.GotoNextLevel();

            Assert.That(builds.Published.Count, Is.EqualTo(1));
            Assert.That(builds.Published[0].LandId, Is.EqualTo("random_plant"),
                "gotoNextLevel rebuilds the land the player is in.");
            Assert.That(builds.Published[0].ForceRegenerate, Is.True);
        }

        [Test]
        public void GotoNextLevel_WithoutACurrentLand_PublishesNothing()
        {
            RecordingPublisher<LandTransitionMessage> transitions = new RecordingPublisher<LandTransitionMessage>();
            RecordingPublisher<LandBuildRequestMessage> builds = Build(transitions, out CampaignManager manager);

            manager.GotoNextLevel();

            Assert.That(builds.Published, Is.Empty);
        }

        [Test]
        public void OpenLand_MarksAccessWithoutPublishingABuild()
        {
            RecordingPublisher<LandTransitionMessage> transitions = new RecordingPublisher<LandTransitionMessage>();
            RecordingPublisher<LandBuildRequestMessage> builds = Build(transitions, out CampaignManager manager);

            Assert.That(manager.OpenLand("nio"), Is.True,
                "`openland` is an unlock, not a visit — it must succeed without a catalogue.");

            Assert.That(manager.LandStates.Get("nio").access, Is.True);
            Assert.That(manager.LandStates.Get("nio").visited, Is.False,
                "Unlocking must not fake a visit, or the travel map would show it as already explored.");
            Assert.That(builds.Published, Is.Empty, "`openland` must not build anything.");
        }
    }
}
