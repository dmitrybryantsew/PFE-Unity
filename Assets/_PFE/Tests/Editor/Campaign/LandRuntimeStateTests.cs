using NUnit.Framework;
using PFE.Systems.Campaign;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// Asserts <see cref="LandRuntimeStateRegistry"/> against AS3 <c>Game.upLandLevel</c>
    /// (<c>Game.as:474-481</c>), <c>Game.enterToCurLand</c>'s <c>upStage</c> reset
    /// (<c>Game.as:396-399</c>) and the per-land save payload (<c>LandAct.as:267-301</c>).
    /// </summary>
    [TestFixture]
    public class LandRuntimeStateTests
    {
        [Test]
        public void UpLandLevel_IncrementsOncePerEntry()
        {
            var registry = new LandRuntimeStateRegistry();

            bool first = registry.UpLandLevel("random_plant");
            bool second = registry.UpLandLevel("random_plant");

            Assert.That(first, Is.True, "Positive control: the first upland bumps landStage.");
            Assert.That(second, Is.False, "Negative control: the upStage guard blocks a second bump.");
            Assert.That(registry.Get("random_plant").landStage, Is.EqualTo(1),
                "landStage moved by exactly 1, not 2.");
        }

        [Test]
        public void ResetUpStage_AllowsTheNextRunToIncrementAgain()
        {
            var registry = new LandRuntimeStateRegistry();
            registry.UpLandLevel("random_plant");
            Assert.That(registry.Get("random_plant").upStage, Is.True);

            registry.ResetUpStage("random_plant");

            Assert.That(registry.UpLandLevel("random_plant"), Is.True,
                "enterToCurLand clears upStage, so the next run's upland increments again (Game.as:396-399).");
            Assert.That(registry.Get("random_plant").landStage, Is.EqualTo(2));
        }

        [Test]
        public void StateIsPerLandId_NotShared()
        {
            var registry = new LandRuntimeStateRegistry();
            registry.UpLandLevel("random_plant");
            registry.UpLandLevel("random_plant");

            // Negative control: a different land must not inherit the depth.
            Assert.That(registry.Get("random_stable").landStage, Is.EqualTo(0));
            Assert.That(registry.Get("random_stable").upStage, Is.False);
            Assert.That(registry.Count, Is.EqualTo(2));
        }

        [Test]
        public void SaveRoundTrip_PreservesEveryField()
        {
            var registry = new LandRuntimeStateRegistry();
            registry.UpLandLevel("random_sewer");
            registry.SetAccess("nio", true);
            registry.MarkVisited("begin");
            registry.MarkPassed("surf");
            registry.Get("rbl").lastCpCode = "cp3";

            LandRuntimeStateSaveData[] saved = registry.ToSaveData();

            var reloaded = new LandRuntimeStateRegistry();
            reloaded.LoadFromSaveData(saved);

            Assert.That(reloaded.Get("random_sewer").landStage, Is.EqualTo(1));
            Assert.That(reloaded.Get("random_sewer").upStage, Is.True);
            Assert.That(reloaded.Get("nio").access, Is.True);
            Assert.That(reloaded.Get("begin").visited, Is.True);
            Assert.That(reloaded.Get("surf").passed, Is.True);
            Assert.That(reloaded.Get("rbl").lastCpCode, Is.EqualTo("cp3"));

            // Negative control: the round-trip did not leak one land's state into another.
            Assert.That(reloaded.Get("nio").visited, Is.False);
            Assert.That(reloaded.Get("begin").landStage, Is.EqualTo(0));
        }

        [Test]
        public void Get_UnknownLandReturnsAnEmptyStateNotAnException()
        {
            var registry = new LandRuntimeStateRegistry();
            LandRuntimeState state = registry.Get("no_such_land");
            Assert.That(state, Is.Not.Null);
            Assert.That(state.landStage, Is.EqualTo(0));
            Assert.That(state.landId, Is.EqualTo("no_such_land"));
        }

        [Test]
        public void Get_EmptyIdReturnsADetachedState()
        {
            var registry = new LandRuntimeStateRegistry();
            LandRuntimeState state = registry.Get("");
            Assert.That(state, Is.Not.Null);
            Assert.That(registry.Count, Is.EqualTo(0), "An empty id must not create a dictionary entry.");
        }
    }
}
