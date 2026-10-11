using System.Collections.Generic;
using MessagePipe;
using NUnit.Framework;
using PFE.Core.Messages;
using PFE.Systems.Campaign;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// The camp's ability to send the player somewhere — <c>CampaignManager</c> as the
    /// <see cref="ITravelMapHost"/>.
    ///
    /// <para><b>Oracle.</b> Two things grant travel and both run the same three steps:
    /// <c>allact='map'</c> on the camp's wall map (<c>Interact.as:1636-1641</c>) and an NPC whose
    /// <c>inter</c> is <c>travel</c> (<c>NPC.as:210-216</c>). Separately,
    /// <c>PipBuck.setButtons</c> recomputes the flag from the land you are standing in
    /// (<c>PipBuck.as:271-273</c>, <c>:342-345</c>) — so standing in a <c>tip='base'</c> land unlocks it
    /// on its own and stepping out locks it again.</para>
    ///
    /// <para><b>What this fixture can and cannot reach.</b> Every case that needs only a land <i>id</i>
    /// is here. The catalogue-dependent half of <see cref="CampaignManager.BeginMission"/> — "a mission
    /// land sets <c>crea = true</c>, a <c>base</c> land does not" (<c>Game.as:453-460</c>) — is
    /// <b>not</b> covered, because it needs a real <c>CampaignCatalog</c> and neither route to one
    /// works here: <c>ScriptableObject.CreateInstance</c> is an ECall, and the offline stub
    /// (<c>OfflineScriptableObject</c>) skips field initializers, so the catalogue's
    /// <c>readonly Dictionary</c> fields come back null and <c>Initialize()</c> throws. The rule is
    /// pinned where it is reachable — <see cref="CampaignManager.IsBaseLandTip"/> below — and the branch
    /// itself is owner-only play-test. Recorded rather than papered over.</para>
    /// </summary>
    [TestFixture]
    public class TravelMapHostTests
    {
        private sealed class RecordingPublisher<T> : IPublisher<T>
        {
            public readonly List<T> Published = new List<T>();

            public void Publish(T message)
            {
                Published.Add(message);
            }
        }

        /// <summary>
        /// A host with no catalogue. Deliberate: no <c>Resources.Load</c>, no engine — and it is what
        /// makes <c>CurrentLandTip()</c> answer "not a base", which is the interesting direction for the
        /// recompute tests below.
        /// </summary>
        private static CampaignManager Host(RecordingPublisher<TravelMapOpenedMessage> opened = null)
        {
            return new CampaignManager(
                transitionSubscriber: null,
                transitionPublisher: null,
                catalog: null,
                buildPublisher: null,
                travelMapPublisher: opened);
        }

        // ------------------------------------------------------------------
        //  PipBuck.travel
        // ------------------------------------------------------------------

        [Test]
        public void TravelStartsLocked()
        {
            Assert.IsFalse(Host().TravelUnlocked,
                "AS3's flag is false until something grants it (PipBuck.as:342-345).");
        }

        [Test]
        public void UnlockTravel_SetsTheFlag()
        {
            CampaignManager host = Host();
            host.UnlockTravel();

            Assert.IsTrue(host.TravelUnlocked);
        }

        [Test]
        public void GrantTravelAndOpenMap_LeavesTravelUnlockedOutsideABase()
        {
            // The whole point of the three-step verb. The player is in "surf", which is not a base land,
            // so OpenTravelMap's recompute clears the grant — and the trailing re-grant is what survives.
            CampaignManager host = Host();
            host.TransitionToLand("surf");
            Assert.IsFalse(host.TravelUnlocked, "Arriving outside a base land locks travel.");

            host.GrantTravelAndOpenMap();

            Assert.IsTrue(host.TravelUnlocked,
                "Interact.as:1640 — `pip.travel = true` is written twice on purpose.");
        }

        [Test]
        public void UnlockThenOpenAlone_ClearsTheGrantOutsideABase()
        {
            // Positive control for the test above: it proves the third write is what saves the grant.
            // Without this, `GrantTravelAndOpenMap` could be a no-op and that test would still pass if
            // OpenTravelMap never cleared anything.
            CampaignManager host = Host();
            host.TransitionToLand("surf");

            host.UnlockTravel();
            host.OpenTravelMap();

            Assert.IsFalse(host.TravelUnlocked,
                "OpenTravelMap re-derives the flag, so two single calls lose the grant — which is exactly "
                + "why Interact.as writes it a third time.");
        }

        [Test]
        public void RefreshTravelUnlockedForCurrentLand_WithoutACatalogue_LocksTravel()
        {
            // The safe direction. A host with no catalogue cannot tell whether the land is a hub, and
            // granting travel by default would let the player leave a story land through a bug.
            CampaignManager host = Host();
            host.UnlockTravel();
            host.TransitionToLand("surf");

            Assert.IsFalse(host.TravelUnlocked);
        }

        [Test]
        public void IsBaseLandTip_MatchesOnlyBase()
        {
            Assert.IsTrue(CampaignManager.IsBaseLandTip("base"));
            Assert.IsTrue(CampaignManager.IsBaseLandTip("BASE"), "The comparison folds case, as AS3's == does not "
                + "but the imported data is lowercase either way.");
            Assert.IsFalse(CampaignManager.IsBaseLandTip("story"));
            Assert.IsFalse(CampaignManager.IsBaseLandTip("rnd"));
            Assert.IsFalse(CampaignManager.IsBaseLandTip("prob"));
            Assert.IsFalse(CampaignManager.IsBaseLandTip(null));
            Assert.IsFalse(CampaignManager.IsBaseLandTip(""));
        }

        [Test]
        public void OpenTravelMap_PublishesTheOpenedMessageWithTheCurrentLand()
        {
            var opened = new RecordingPublisher<TravelMapOpenedMessage>();
            CampaignManager host = Host(opened);

            host.TransitionToLand("surf");
            host.OpenTravelMap();

            Assert.AreEqual(1, opened.Published.Count,
                "Exactly one open per call — the page is not opened by a transition.");
            Assert.AreEqual("surf", opened.Published[0].CurrentLandId,
                "The map shows the land you are in as \"here\" (PipPageInfo.as:724).");
        }

        // ------------------------------------------------------------------
        //  Game.beginMission — the world map's confirm button
        // ------------------------------------------------------------------

        [Test]
        public void BeginMission_EmptyId_ReturnsFalse()
        {
            CampaignManager host = Host();

            Assert.IsFalse(host.BeginMission(null));
            Assert.IsFalse(host.BeginMission("   "));
        }

        [Test]
        public void BeginMission_SameLand_IsANoOp()
        {
            // Game.as:449-452 — re-selecting where you already are returns immediately. Not even a
            // rebuild: a rebuild here would discard the camp's contents while you stand in them.
            var builds = new RecordingPublisher<LandBuildRequestMessage>();
            CampaignManager host = new CampaignManager(null, null, null, builds, null);

            host.TransitionToLand("surf");
            builds.Published.Clear();

            Assert.IsFalse(host.BeginMission("surf"), "Nothing was started.");
            Assert.AreEqual(0, builds.Published.Count, "And nothing was rebuilt.");
        }

        [Test]
        public void BeginMission_UnknownLand_TransitionsButIsNotAMission()
        {
            // The catalogue lookup fails, so `missionId` is not recorded and no forced regenerate is
            // asked for — but the transition still happens. AS3's guard is `lands[param1]`, and the port
            // deliberately does not refuse an id the catalogue does not know (03_GAP_LEDGER.md §1, D1).
            var builds = new RecordingPublisher<LandBuildRequestMessage>();
            CampaignManager host = new CampaignManager(null, null, null, builds, null);

            Assert.IsTrue(host.BeginMission("nio"), "A transition was started.");

            Assert.AreEqual(1, builds.Published.Count);
            Assert.AreEqual("nio", builds.Published[0].LandId);
            Assert.IsFalse(builds.Published[0].ForceRegenerate);
            Assert.IsNull(host.MissionId,
                "An unknown land does not become the mission — Game.as:453 requires lands[param1].");
        }

        // ------------------------------------------------------------------
        //  NPC.as:210-216 — the other travel grant
        // ------------------------------------------------------------------

        [Test]
        public void ActivateNpc_Travel_GrantsTravelAndOpensTheMap()
        {
            var opened = new RecordingPublisher<TravelMapOpenedMessage>();
            CampaignManager host = Host(opened);

            host.TransitionToLand("surf");

            Assert.IsTrue(host.ActivateNpc("travel"));
            Assert.IsTrue(host.TravelUnlocked);
            Assert.AreEqual(1, opened.Published.Count);
        }

        [Test]
        public void ActivateNpc_OtherInteraction_IsNotATravelGrant()
        {
            var opened = new RecordingPublisher<TravelMapOpenedMessage>();
            CampaignManager host = Host(opened);
            host.TransitionToLand("surf");

            Assert.IsFalse(host.ActivateNpc("talk"));
            Assert.IsFalse(host.TravelUnlocked);
            Assert.AreEqual(0, opened.Published.Count);
        }

        [Test]
        public void IsTravelNpcInteraction_MatchesOnlyTravel()
        {
            Assert.IsTrue(CampaignManager.IsTravelNpcInteraction("travel"));
            Assert.IsTrue(CampaignManager.IsTravelNpcInteraction("TRAVEL"));
            Assert.IsFalse(CampaignManager.IsTravelNpcInteraction("talk"));
            Assert.IsFalse(CampaignManager.IsTravelNpcInteraction(null));
            Assert.IsFalse(CampaignManager.IsTravelNpcInteraction(""));
        }
    }
}
