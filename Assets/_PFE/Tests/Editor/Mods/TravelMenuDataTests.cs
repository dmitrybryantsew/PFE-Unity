using System.Collections.Generic;
using MessagePipe;
using NUnit.Framework;
using PFE.Core.Messages;
using PFE.Data;
using PFE.Data.Definitions.Campaign;
using PFE.ModAPI;
using PFE.Systems.Campaign;
using UnityEngine;

namespace PFE.Tests.Editor.Mods
{
    /// <summary>
    /// The F2 Lands tab's data source: mod lands appear with their declared biomes/seed support
    /// while the mod is enabled, disappear when it is disabled, and confirming travel stages the
    /// launch request on the provider before publishing the standard LandBuildRequestMessage.
    /// </summary>
    [TestFixture]
    public class TravelMenuDataTests
    {
        private sealed class RecordingPublisher<T> : IPublisher<T>
        {
            public readonly List<T> Published = new List<T>();
            public void Publish(T message) => Published.Add(message);
        }

        private CampaignCatalog _catalog;
        private ProceduralLandRegistry _registry;
        private FakeProceduralLandProvider _provider;
        private TravelOverlayPresenter _presenter;

        [SetUp]
        public void SetUp()
        {
            _catalog = ScriptableObject.CreateInstance<CampaignCatalog>();
            _provider = new FakeProceduralLandProvider();
            _registry = new ProceduralLandRegistry();
            _registry.RegisterProvider(_provider);
            _presenter = new TravelOverlayPresenter(_catalog, _registry);
        }

        [TearDown]
        public void TearDown()
        {
            if (_catalog != null)
            {
                Object.DestroyImmediate(_catalog);
            }
        }

        private LandDefinition AddModLand()
        {
            var land = ScriptableObject.CreateInstance<LandDefinition>();
            land.landId = FakeCodeModPlugin.LandId;
            land.landName = "Test Procedural Land";
            land.isProcedural = true;
            land.isModProcedural = true;
            _catalog.SetModLands(new[] { land });
            return land;
        }

        [Test]
        public void EnabledModLand_Appears_WithDeclaredBiomesAndSeedSupport()
        {
            AddModLand();

            var entries = _presenter.BuildEntries();

            Assert.That(entries.Count, Is.EqualTo(1));
            TravelOverlayPresenter.LandEntry entry = entries[0];
            Assert.That(entry.Land.landId, Is.EqualTo(FakeCodeModPlugin.LandId));
            Assert.That(entry.IsProceduralMod, Is.True);
            Assert.That(entry.Biomes, Is.EqualTo(new[] { "alpha", "beta" }),
                "The UI renders the mod's own biome list — nothing is hardcoded host-side");
            Assert.That(entry.SupportsSeed, Is.True);
        }

        [Test]
        public void DisabledModLand_Disappears_FromEntries()
        {
            AddModLand();
            Assert.That(_presenter.BuildEntries().Count, Is.EqualTo(1));

            // What GameDatabase.SyncCampaignCatalog pushes when the mod is toggled off.
            _catalog.SetModLands(new LandDefinition[0]);

            Assert.That(_presenter.BuildEntries().Count, Is.EqualTo(0),
                "A disabled mod's lands must vanish from the travel list");
        }

        [Test]
        public void VanillaLand_HasNoModOptions_ButStillTravels()
        {
            var land = ScriptableObject.CreateInstance<LandDefinition>();
            land.landId = "stable";
            land.landName = "Stable";
            _catalog.SetModLands(new[] { land });

            var entries = _presenter.BuildEntries();
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].IsProceduralMod, Is.False);
            Assert.That(entries[0].Biomes, Is.Empty);
            Assert.That(entries[0].SupportsSeed, Is.False);

            Assert.That(_presenter.ApplyPendingLaunchRequest("stable"), Is.Null,
                "No provider handles a vanilla land, so nothing is staged");
        }

        [Test]
        public void ApplyPendingLaunchRequest_StagesChosenBiomeAndSeed()
        {
            AddModLand();

            _presenter.SetSelectedBiome(FakeCodeModPlugin.LandId, "beta");
            _presenter.SetSeedText(FakeCodeModPlugin.LandId, "12345");

            ProceduralLandLaunchRequest request = _presenter.ApplyPendingLaunchRequest(FakeCodeModPlugin.LandId);

            Assert.That(request, Is.Not.Null);
            Assert.That(request.Biome, Is.EqualTo("beta"));
            Assert.That(request.Seed, Is.EqualTo(12345u));

            ProceduralLandLaunchRequest staged = _provider.GetPendingLaunchRequest(FakeCodeModPlugin.LandId);
            Assert.That(staged, Is.Not.Null, "The provider must hold the request before the transition fires");
            Assert.That(staged.Biome, Is.EqualTo("beta"));
        }

        [Test]
        public void ApplyPendingLaunchRequest_BlankSeed_RollsRandom()
        {
            AddModLand();

            _presenter.SetSeedText(FakeCodeModPlugin.LandId, string.Empty);
            ProceduralLandLaunchRequest request = _presenter.ApplyPendingLaunchRequest(FakeCodeModPlugin.LandId);

            Assert.That(request.Seed.HasValue, Is.True, "Blank seed box means 'roll one for me'");
            Assert.That(request.Seed.Value, Is.GreaterThan(0u));
        }

        [Test]
        public void ConfirmTravel_StagesRequest_AndPublishesExactlyOneBuildRequest()
        {
            AddModLand();
            _presenter.SetSelectedBiome(FakeCodeModPlugin.LandId, "beta");
            _presenter.SetSeedText(FakeCodeModPlugin.LandId, "777");

            var builds = new RecordingPublisher<LandBuildRequestMessage>();
            var transitions = new RecordingPublisher<LandTransitionMessage>();
            var campaign = new CampaignManager(
                transitionSubscriber: null,
                transitionPublisher: transitions,
                catalog: null,
                buildPublisher: builds);

            _presenter.ConfirmTravel(campaign, FakeCodeModPlugin.LandId);

            Assert.That(_provider.GetPendingLaunchRequest(FakeCodeModPlugin.LandId), Is.Not.Null,
                "Launch options reach the provider before the world rebuilds");
            Assert.That(builds.Published.Count, Is.EqualTo(1),
                "Travel routes through the standard CampaignManager flow — one LandBuildRequestMessage");
            Assert.That(builds.Published[0].LandId, Is.EqualTo(FakeCodeModPlugin.LandId));
            Assert.That(builds.Published[0].ForceRegenerate, Is.True,
                "A debug travel re-assembles the procedural land (gotoNextLevel semantics)");
        }

        [Test]
        public void TravelMapModel_TreatsModProceduralLand_AsLoaded()
        {
            LandDefinition land = AddModLand();

            TravelLand facts = TravelLand.From(land);

            Assert.That(TravelMapModel.IsLoaded(facts), Is.True,
                "A mod-procedural land has no static room templates but is always ready to generate");
        }
    }
}
