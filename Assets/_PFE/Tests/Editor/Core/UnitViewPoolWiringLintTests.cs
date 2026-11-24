using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Thread J (UnitViewPool): the <b>wiring</b> guards for the pooled unit visual.
    ///
    /// <para><b>Why a lint and not only the behavioural fixtures.</b> <c>UnitViewPoolTests</c> pins
    /// what the pool does when it is used; these pins are about the call sites a refactor can
    /// silently bypass — a spawner that builds a renderer inline again, a teardown path that
    /// forgets to release, a scope that stops registering the pool. Each is invisible to the
    /// behavioural tests (they construct the pool directly) and silent in a live session (the
    /// inline path still draws; the leak only shows as churn).</para>
    /// </summary>
    [TestFixture]
    public class UnitViewPoolWiringLintTests
    {
        private const string SpawnerPath = "Systems/Map/Rendering/RoomUnitSpawner.cs";
        private const string ViewPath = "Entities/Units/UnitView.cs";
        private const string PoolPath = "Entities/Units/UnitViewPool.cs";
        private const string VisualControllerPath = "Systems/Map/Rendering/RoomVisualController.cs";
        private const string ScopePath = "Core/Installers/GameLifetimeScope.cs";
        private const string SettingsPath = "Core/PfeDebugSettings.cs";

        private static string Read(string path)
        {
            return SourceLint.ReadStripped(path);
        }

        private static void AssertContains(string source, string required, string why)
        {
            Assert.That(source, Does.Contain(required), why);
        }

        private static void AssertLacks(string source, string forbidden, string why)
        {
            Assert.That(source, Does.Not.Contain(forbidden), why);
        }

        /// <summary>
        /// Control for the whole fixture: the predicates must reject a source that lacks (or has)
        /// the call. Without this, a typo in a needle makes every assertion a no-op that passes.
        /// </summary>
        [Test]
        public void Control_Assertions_RejectTheWrongSource()
        {
            Assert.Throws<AssertionException>(
                () => AssertContains("void Spawn() { }", "_viewPool.Acquire(", "control"),
                "A presence check that cannot fail is not a guard.");
            Assert.Throws<AssertionException>(
                () => AssertLacks("void Spawn() { AddComponent<UnitAnimator>(); }", "AddComponent<UnitAnimator>", "control"),
                "An absence check that cannot fail is not a guard.");

            Assert.DoesNotThrow(() => AssertContains("v.Acquire(x);", "Acquire(", "control"));
            Assert.DoesNotThrow(() => AssertLacks("void Spawn() { }", "AddComponent<UnitAnimator>", "control"));
        }

        [Test]
        public void Spawner_AcquiresAndReleasesThroughThePool()
        {
            string source = Read(SpawnerPath);

            AssertContains(source, "unitViewPool", "the spawner takes the pool as a constructor handover");
            AssertContains(source, "_viewPool.Acquire(", "spawn draws the visual from the pool");
            AssertContains(source, "_viewPool.Release(", "teardown returns the visual to the pool");
            AssertContains(source, "ReleaseView(unit);", "DestroyUnit releases before the unit root goes");
            AssertContains(source, "ReleaseView(pair.Key);", "DestroyAll releases before the unit root goes");
        }

        [Test]
        public void Spawner_BuildsVisualsOnlyThroughUnitView()
        {
            string source = Read(SpawnerPath);

            // The inline construction moved into UnitView.Configure. If either of these comes back,
            // the pooled and inline paths have diverged into two visual builds again.
            AssertLacks(source, "AddComponent<SpriteRenderer>", "the renderer is UnitView's, not the spawner's");
            AssertLacks(source, "AddComponent<UnitAnimator>", "the animator is UnitView's, not the spawner's");
            AssertContains(source, "AddComponent<UnitView>()", "the inline path still builds a UnitView");
        }

        [Test]
        public void View_OwnsTheVisualConstruction()
        {
            string source = Read(ViewPath);

            AssertContains(source, "UnitSpriteAnchor.ApplyResolved(", "the anchor rule lives in the view");
            AssertContains(source, "PonPonVariants.TryResolve(", "the villager-variant path lives in the view");
            AssertContains(source, "TurretPartPresenter.Attach(", "the turret-part rebuild lives in the view");
            AssertContains(source, "_animator.Initialize(definition", "the view (re)initialises the animator on every configure");
            AssertContains(source, "SpritePresenterMaterial.Get()", "the presenter material lives in the view");
        }

        [Test]
        public void Pool_RecyclesOnlyCleanViews()
        {
            string source = Read(PoolPath);

            AssertContains(source, "IsRecyclable", "the recycling gate exists");
            AssertContains(source, "childCount", "children (a shield dome, turret parts) block recycling");
            AssertContains(source, "GetComponents<Component>()", "extra components (an NPC persona animator) block recycling");
        }

        [Test]
        public void Pool_IsRegisteredAndFlagGated()
        {
            AssertContains(Read(ScopePath), "UnitViewPool",
                "the container owns the one session pool");
            AssertContains(Read(SettingsPath), "PooledUnitViews",
                "the A/B flag exists");
            AssertContains(Read(VisualControllerPath), "ResolveUnitViewPool",
                "the visual controller hands the pool down the spawner chain");
        }
    }
}
