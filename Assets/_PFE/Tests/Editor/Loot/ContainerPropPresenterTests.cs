using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;

namespace PFE.Tests.Editor.Loot
{
    /// <summary>
    /// The pure decisions of the container interaction surface (D6). The MonoBehaviour itself needs a
    /// scene, so only its static predicates are asserted here — which is deliberate: those are the two
    /// questions that decide whether a crate can be opened at all, and they can be pinned without a
    /// Unity run.
    /// </summary>
    [TestFixture]
    public class ContainerPropPresenterTests
    {
        [Test]
        public void Admits_IsTrueOnlyForAContainerThatIsNotYetLooted()
        {
            Assert.IsTrue(ContainerPropPresenter.Admits(isContainer: true, isLooted: false),
                "a container with contents is openable.");

            Assert.IsFalse(ContainerPropPresenter.Admits(true, true),
                "an already-looted container offers nothing (AS3 cont == \"empty\").");

            Assert.IsFalse(ContainerPropPresenter.Admits(false, false),
                "a non-container is not openable at all.");

            Assert.IsFalse(ContainerPropPresenter.Admits(false, true));
        }

        [Test]
        public void UsesContainerPresenter_FollowsTheResolvedCont_NotTheId()
        {
            // The rule is `cont`-based. A pushable crate (mcrate1..5) carries no cont, so it is NOT a
            // container even though the old hand-maintained ContainerIds listed it.
            Assert.IsFalse(RoomObjectVisualManager.UsesContainerPresenter(string.Empty),
                "no cont -> not a container.");
            Assert.IsFalse(RoomObjectVisualManager.UsesContainerPresenter(null));

            Assert.IsTrue(RoomObjectVisualManager.UsesContainerPresenter("chest"));
            Assert.IsTrue(RoomObjectVisualManager.UsesContainerPresenter("safe"));

            Assert.IsFalse(RoomObjectVisualManager.UsesContainerPresenter(ContainerRule.EmptyCont),
                "the looted sentinel is not a container.");

            // '*' is openable (AS3 answers "itsEmpty"), even though it names no table. This is the
            // distinction between IsContainer and HasLootTable, pinned here so a later change to one
            // cannot quietly move the other.
            Assert.IsTrue(RoomObjectVisualManager.UsesContainerPresenter(ContainerRule.WildcardCont));
            Assert.IsFalse(ContainerRule.HasLootTable(ContainerRule.WildcardCont));
        }

        [Test]
        public void ResolveCont_PlacementWins_ThenDefinition()
        {
            // The container presenter resolves through ContainerRule; this pins the order it relies on
            // (placement over definition) so the presenter cannot diverge from the rule.
            Assert.AreEqual("chest", ContainerRule.ResolveCont("chest", "case"));
            Assert.AreEqual("case", ContainerRule.ResolveCont(string.Empty, "case"));
            Assert.AreEqual(string.Empty, ContainerRule.ResolveCont(null, null));
        }
    }
}
