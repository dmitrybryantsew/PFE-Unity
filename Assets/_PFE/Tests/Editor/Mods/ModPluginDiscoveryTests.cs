using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PFE.Data;
using PFE.Data.Definitions.Campaign;
using PFE.ModAPI;

namespace PFE.Tests.Editor.Mods
{
    /// <summary>
    /// Host-side gate for the plugin contract: assembly-attribute discovery, dependency-aware
    /// ordering, persisted enable/disable, and full removal of a disabled mod's registrations.
    /// Uses the inert fixture plugins in this assembly — never the real RandomRooms mod, which
    /// has its own seam tests (Phase0SeamTests) inside the mod.
    /// </summary>
    [TestFixture]
    public class ModPluginDiscoveryTests
    {
        private static ModLoader CreateLoader(IModConfigStore store = null)
        {
            var loader = new ModLoader(
                store ?? new InMemoryModConfigStore(),
                new[] { typeof(FakeCodeModPlugin).Assembly })
            {
                ModsRoot = Path.Combine(Path.GetTempPath(), "pfe_test_no_such_mods_dir")
            };
            return loader;
        }

        [Test]
        public void CodePlugins_AreDiscovered_ViaAssemblyAttribute()
        {
            ModLoader loader = CreateLoader();
            var mods = loader.DiscoverMods();

            ModManifest fake = mods.FirstOrDefault(m => m.modId == FakeCodeModPlugin.ModId);
            Assert.That(fake, Is.Not.Null, "Fixture plugin must be discovered via [assembly: PfeModPlugin]");
            Assert.That(fake.isCodeMod, Is.True);
            Assert.That(fake.isEnabled, Is.True, "Mods default to enabled when no config exists");

            Assert.That(mods.Any(m => m.modId == FakeDependentModPlugin.ModId), Is.True,
                "Both assembly attributes must be discovered (AllowMultiple)");
        }

        [Test]
        public void Dependencies_SortBeforeDependents_EvenWithHigherLoadOrder()
        {
            ModLoader loader = CreateLoader();
            var mods = loader.DiscoverMods();

            int dependencyIndex = mods.FindIndex(m => m.modId == FakeCodeModPlugin.ModId);
            int dependentIndex = mods.FindIndex(m => m.modId == FakeDependentModPlugin.ModId);

            Assert.That(dependencyIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(dependentIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(dependencyIndex, Is.LessThan(dependentIndex),
                "pfe.testmod (order 100) must precede pfe.testmod.dependent (order 10) because the " +
                "dependent declares the dependency — raw loadOrder alone would invert them.");
        }

        [Test]
        public void MissingDependency_SkipsDependentMod_WithValidationLog()
        {
            // Disable the dependency; the dependent mod stays enabled but must be skipped, and the
            // validation log must say why. (ValidateManifest is private — this is its observable seam.)
            var store = new InMemoryModConfigStore(new Dictionary<string, bool> { [FakeCodeModPlugin.ModId] = false });
            ModLoader loader = CreateLoader(store);

            var sources = loader.BuildSourceList();

            Assert.That(loader.ProceduralLands.Providers.Count, Is.EqualTo(0),
                "The disabled dependency registers nothing");
            Assert.That(
                loader.ValidationLog.Any(line =>
                    line.Contains(FakeDependentModPlugin.ModId) && line.Contains("missing dependency")),
                Is.True,
                "The dependent mod must be flagged for its unmet dependency, got: " +
                string.Join(" | ", loader.ValidationLog));
            Assert.That(sources.Count, Is.EqualTo(1), "Only the base-game source remains");
        }

        [Test]
        public void EnabledMod_RegistersProviderConsoleAndLand()
        {
            ModLoader loader = CreateLoader();
            var sources = loader.BuildSourceList();

            Assert.That(loader.ProceduralLands.Providers.Count, Is.EqualTo(1));
            Assert.That(loader.ProceduralLands.Providers[0], Is.TypeOf<FakeProceduralLandProvider>());
            Assert.That(loader.ProceduralLands.ConsoleHandlers.ContainsKey(FakeCodeModPlugin.ConsolePrefix), Is.True);

            var registry = new ContentRegistry();
            registry.Initialize(sources.Where(s => s is not BuiltInContentSource));

            Assert.That(registry.Contains(ContentType.Campaign, FakeCodeModPlugin.LandId), Is.True,
                "The host must auto-register a LandDefinition from the provider's descriptor");
            LandDefinition land = registry.GetAll<LandDefinition>(ContentType.Campaign)
                .First(l => l.landId == FakeCodeModPlugin.LandId);
            Assert.That(land.isModProcedural, Is.True);
            Assert.That(land.landName, Is.EqualTo("Test Procedural Land"));
        }

        [Test]
        public void DisabledMod_RegistersNothing_AndPersists()
        {
            var store = new InMemoryModConfigStore();
            ModLoader loader = CreateLoader(store);
            loader.DiscoverMods();

            loader.SetModEnabled(FakeCodeModPlugin.ModId, false);
            Assert.That(store.Load().Enabled[FakeCodeModPlugin.ModId], Is.False, "Toggle must persist to the store");

            // Rebuild from the same store: provider, console prefix and land all disappear.
            ModLoader reloaded = CreateLoader(store);
            var sources = reloaded.BuildSourceList();

            Assert.That(reloaded.ProceduralLands.Providers.Count, Is.EqualTo(0));
            Assert.That(reloaded.ProceduralLands.ConsoleHandlers.Count, Is.EqualTo(0));

            var registry = new ContentRegistry();
            registry.Initialize(sources.Where(s => s is not BuiltInContentSource));
            Assert.That(registry.Contains(ContentType.Campaign, FakeCodeModPlugin.LandId), Is.False);

            // The dependent mod stays enabled but now misses its dependency: it must be skipped too.
            Assert.That(reloaded.ValidationLog.Any(line => line.Contains("missing dependency")), Is.True,
                "Disabling a dependency must surface a validation warning for the dependent mod");
        }

        [Test]
        public void ReEnable_RestoresRegistrations()
        {
            var store = new InMemoryModConfigStore(new Dictionary<string, bool> { [FakeCodeModPlugin.ModId] = false });
            ModLoader loader = CreateLoader(store);
            loader.BuildSourceList();
            Assert.That(loader.ProceduralLands.Providers.Count, Is.EqualTo(0));

            loader.SetModEnabled(FakeCodeModPlugin.ModId, true);
            loader.BuildSourceList();
            Assert.That(loader.ProceduralLands.Providers.Count, Is.EqualTo(1));
        }
    }
}
