using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PFE.Data;
using PFE.Data.Definitions.Campaign;
using PFE.ModAPI;
using PFE.Mods.RandomRooms.Unity;
using UnityEngine;

namespace PFE.Mods.RandomRooms.Tests.EditMode
{
    /// <summary>
    /// The mod's host seam: discovered via [assembly: PfeModPlugin], registered through
    /// IModHostContext, persisted through IModConfigStore, and fully inert when disabled.
    /// </summary>
    [TestFixture]
    public class Phase0SeamTests
    {
        private const string ModId = "pfe.mods.randomrooms";

        private static ModLoader CreateLoader(IModConfigStore store = null)
        {
            var loader = new ModLoader(
                store ?? new InMemoryModConfigStore(),
                new[] { typeof(RandomRoomsPlugin).Assembly })
            {
                // Never pick up real folder mods from the developer machine during tests.
                ModsRoot = Path.Combine(Path.GetTempPath(), "pfe_test_no_such_mods_dir")
            };
            return loader;
        }

        [Test]
        public void Plugin_IsDiscovered_ViaAssemblyAttribute()
        {
            ModLoader loader = CreateLoader();
            var mods = loader.DiscoverMods();

            ModManifest manifest = mods.FirstOrDefault(m => m.modId == ModId);
            Assert.That(manifest, Is.Not.Null, "RandomRoomsPlugin must be discovered via [assembly: PfeModPlugin]");
            Assert.That(manifest.isCodeMod, Is.True);
            Assert.That(manifest.isEnabled, Is.True, "Mods default to enabled when no config exists");
            Assert.That(manifest.displayName, Is.EqualTo("RandomRooms"));
        }

        [Test]
        public void EnabledMod_RegistersProviderConsoleAndLands()
        {
            ModLoader loader = CreateLoader();
            var sources = loader.BuildSourceList();

            Assert.That(loader.ProceduralLands.Providers.Count, Is.EqualTo(1));
            Assert.That(loader.ProceduralLands.Providers[0], Is.TypeOf<RandomRoomsLandProvider>());
            Assert.That(loader.ProceduralLands.ConsoleHandlers.ContainsKey("rr"), Is.True);

            // The host auto-registers one LandDefinition per declared descriptor. The base-game
            // source is skipped: it Resources.LoadAll's the whole database, which is not what
            // this test is about.
            var registry = new ContentRegistry();
            registry.Initialize(sources.Where(s => s is not BuiltInContentSource));

            Assert.That(registry.Contains(ContentType.Campaign, "random_rooms"), Is.True);
            Assert.That(registry.Contains(ContentType.Campaign, "rr_showroom"), Is.True);

            LandDefinition land = registry.GetAll<LandDefinition>(ContentType.Campaign)
                .First(l => l.landId == "random_rooms");
            Assert.That(land.isModProcedural, Is.True);
            Assert.That(land.isProcedural, Is.True);
            Assert.That(land.gridWidth, Is.EqualTo(5));
            Assert.That(land.gridHeight, Is.EqualTo(5));
        }

        [Test]
        public void DisabledMod_RegistersNothing()
        {
            var store = new InMemoryModConfigStore(new Dictionary<string, bool> { [ModId] = false });
            ModLoader loader = CreateLoader(store);
            var sources = loader.BuildSourceList();

            Assert.That(loader.ProceduralLands.Providers.Count, Is.EqualTo(0));
            Assert.That(loader.ProceduralLands.ConsoleHandlers.Count, Is.EqualTo(0));

            var registry = new ContentRegistry();
            registry.Initialize(sources.Where(s => s is not BuiltInContentSource));
            Assert.That(registry.Contains(ContentType.Campaign, "random_rooms"), Is.False);
            Assert.That(registry.Contains(ContentType.Campaign, "rr_showroom"), Is.False);
        }

        [Test]
        public void SetModEnabled_PersistsThroughConfigStore()
        {
            var store = new InMemoryModConfigStore();
            ModLoader loader = CreateLoader(store);
            loader.DiscoverMods();

            loader.SetModEnabled(ModId, false);
            Assert.That(store.Load().Enabled[ModId], Is.False);

            // A fresh loader over the same store sees the persisted choice.
            ModLoader reloaded = CreateLoader(store);
            Assert.That(reloaded.DiscoverMods().First(m => m.modId == ModId).isEnabled, Is.False);
        }

        [Test]
        public void Console_Enter_StagesLaunchRequest_AndRequestsTravel()
        {
            var provider = new RandomRoomsLandProvider();
            var console = new RandomRoomsConsole(provider);
            var context = new FakeConsoleContext();

            string output = console.Execute("rr enter sewer 4242", context);

            ProceduralLandLaunchRequest staged = provider.GetPendingLaunchRequest("random_rooms");
            Assert.That(staged, Is.Not.Null);
            Assert.That(staged.Biome, Is.EqualTo("sewer"));
            Assert.That(staged.Seed, Is.EqualTo(4242u));
            Assert.That(context.TravelRequests, Is.EqualTo(new[] { "random_rooms" }));
            Assert.That(output, Does.Contain("random_rooms"));
        }

        [Test]
        public void Console_UnknownSubCommand_ReturnsHelp()
        {
            var console = new RandomRoomsConsole(new RandomRoomsLandProvider());
            string output = console.Execute("rr frobnicate", new FakeConsoleContext());
            Assert.That(output, Does.Contain("Unknown sub-command"));
            Assert.That(output, Does.Contain("rr enter"));
        }

        private sealed class FakeConsoleContext : IModConsoleContext
        {
            public readonly List<string> TravelRequests = new List<string>();
            public string CurrentLandId => "random_rooms";
            public Vector3Int CurrentRoomPosition => Vector3Int.zero;
            public Vector3Int BoundsMin => Vector3Int.zero;
            public Vector3Int BoundsMax => new Vector3Int(5, 5, 1);
            public int RoomCount => 25;
            public void RequestTravelToLand(string landId, bool forceRegenerate = true) => TravelRequests.Add(landId);
            public bool RequestTeleportToRoom(Vector3Int roomPosition) => true;
            public string DescribeCurrentRoom() => "fake room";
        }
    }
}
