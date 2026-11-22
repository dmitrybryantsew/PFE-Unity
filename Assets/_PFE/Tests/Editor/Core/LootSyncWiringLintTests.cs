using System;
using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// The slice-1 loot-sync wiring lints. The pure halves of the feature are executed by
    /// <c>SimDroppedItemSetRulesTests</c>, <c>SimLootHashContextTests</c>, <c>SimHostInteractRulesTests</c>
    /// and <c>SimInteractCommandLatchTests</c>; THIS fixture pins the seams those rules cannot reach —
    /// the MonoBehaviour and session glue that only exists as source outside the offline harness.
    ///
    /// <para><b>Why the net files need their own reader.</b> <c>SourceLint.ReadStripped</c> is rooted at
    /// <c>Assets/_PFE</c>, and the session/applier/sink live in <c>Assets/PfeNet</c> (Assembly-CSharp,
    /// where the FishNet adapter deliberately sits). The stripper and the project-root walk are reused;
    /// only the path composition is local.</para>
    /// </summary>
    [TestFixture]
    public class LootSyncWiringLintTests
    {
        private const string PresenterPath = "Systems/Map/Rendering/ContainerPropPresenter.cs";
        private const string PickupPath = "Systems/Inventory/WorldItemPickup.cs";
        private const string AutoPickupPath = "Entities/Player/PlayerAutoPickup.cs";
        private const string SamplerPath = "Entities/Player/LocalPlayerInputSampler.cs";
        private const string ScopePath = "Core/Installers/GameLifetimeScope.cs";

        private const string SessionFile = "PfeReplicationSession.cs";
        private const string ApplierFile = "PfeReplicationWorldApplier.cs";
        private const string SinkFile = "PfeInteractSink.cs";

        private static string Read(string path) => SourceLint.ReadStripped(path);

        private static string ReadNet(string fileName)
        {
            string fullPath = Path.Combine(
                SourceLint.ProjectRoot(), "Assets", "PfeNet", fileName);
            Assert.IsTrue(File.Exists(fullPath),
                "Lint target missing: " + fullPath + ". If the file moved, this fixture must be updated.");
            return SourceLint.StripCommentsAndStrings(File.ReadAllText(fullPath));
        }

        private static void AssertContains(string source, string needle, string why)
        {
            Assert.That(source, Does.Contain(needle), why);
        }

        private static void AssertBefore(string source, string first, string second, string why)
        {
            int a = source.IndexOf(first, StringComparison.Ordinal);
            int b = source.IndexOf(second, StringComparison.Ordinal);
            Assert.That(a, Is.GreaterThanOrEqualTo(0), "Missing first marker: " + first);
            Assert.That(b, Is.GreaterThanOrEqualTo(0), "Missing second marker: " + second);
            Assert.That(a, Is.LessThan(b), why);
        }

        [Test]
        public void Control_TheLintCanActuallyFail()
        {
            Assert.Throws<AssertionException>(
                () => AssertContains(Read(PresenterPath), "__NO_SUCH_SYMBOL_IN_LOOT_SYNC__", "control"),
                "A lint that cannot report a missing symbol is not a guard.");
            AssertContains(ReadNet(SessionFile), "class PfeReplicationSession",
                "Positive control: the net-file reader reaches the real session source.");
        }

        // ── Container presenter ───────────────────────────────────────────────

        [Test]
        public void Presenter_AJoinedClientsPress_LatchesInsteadOfOpening()
        {
            string body = SourceLint.MethodBody(Read(PresenterPath),
                "public void Interact(GameObject user)");

            AssertContains(body, "SimAuthorityContext.PeerRole == SimPeerRole.Client",
                "Only a joined client suppresses; the host and single-player keep the local open.");
            AssertBefore(body, "SimAuthorityContext.PeerRole == SimPeerRole.Client",
                "SimInteractCommandLatch.Raise(",
                "The role check must gate the latch: a host process raising the latch would send " +
                "itself an open request for a chest it already opened.");
            AssertBefore(body, "SimInteractCommandLatch.Raise(",
                "PerformOpen(user)",
                "The local open is unreachable on a client — the latch replaces it, it does not " +
                "precede it, or the chest rolls twice.");
            AssertContains(body, "spawnIndex >= 0",
                "Only a wire-addressable container may eat the press; an unaddressable object keeps " +
                "its local behaviour rather than swallowing the key.");
            AssertBefore(body, "Time.frameCount == _lastInteractFrame",
                "SimInteractCommandLatch.Raise(",
                "The per-frame double-press guard must also cover the latch, or one press becomes " +
                "two command edges.");
        }

        [Test]
        public void Presenter_TheHostEntryRunsTheOpenWithoutAUser()
        {
            string source = Read(PresenterPath);
            string body = SourceLint.MethodBody(source, "public bool InteractHost()");

            AssertContains(body, "Admits(IsContainer, IsLooted)",
                "The idempotency re-check that keeps a duplicated edge from re-rolling the chest.");
            AssertContains(body, "PerformOpen(null)",
                "The host open runs with no local user: the peer's inventory and perks live on the " +
                "client's machine.");
        }

        [Test]
        public void Presenter_RegistersItselfForTheHostSink()
        {
            string source = Read(PresenterPath);
            AssertContains(source, "public static bool TryGetPresenter(",
                "The sink's ObjectInstance → presenter bridge.");
            AssertContains(source, "Presenters[obj] = this;",
                "Registration at Initialize; without it every host open refuses 'no live presenter'.");
            AssertContains(source, "ReferenceEquals(registered, this)",
                "A rebuilt room's new presenter must not be unregistered by its predecessor's teardown.");
        }

        [Test]
        public void Presenter_ResolvesDefinitionsWithoutALocalInventory()
        {
            string source = Read(PresenterPath);
            string resolveItem = SourceLint.MethodBody(source,
                "private static ItemDefinition ResolveItem(GameObject user, string itemId)");
            string resolveWeapon = SourceLint.MethodBody(source,
                "private static WeaponDefinition ResolveWeapon(GameObject user, string weaponId)");

            AssertContains(resolveItem, "WorldItemPickup.ItemDefinitionResolver?.Invoke(itemId)",
                "The static seam is what lets a host-side network open spill anything at all.");
            AssertContains(resolveWeapon, "WorldItemPickup.WeaponDefinitionResolver?.Invoke(weaponId)",
                "The weapon table is disjoint from the item table; the fallback must be the weapon seam.");
        }

        // ── Pickup and auto-pickup ────────────────────────────────────────────

        [Test]
        public void Pickup_ReplicatedViewsRefuseCollection()
        {
            string source = Read(PickupPath);
            AssertContains(source, "public bool Replicated { get; private set; }",
                "The flag the whole take-gate hangs off.");
            AssertContains(source, "public bool CanInteract(GameObject user) => !Replicated && user != null;",
                "The cursor path must not even select a replicated view.");
            string magnet = SourceLint.MethodBody(source, "public void MagnetToward(Vector2 target)");
            AssertContains(magnet, "if (Replicated) return;",
                "A magnet would visually rip the mirror away from the host's reported position.");
        }

        [Test]
        public void Pickup_ReplicatedRebuildMarksViews()
        {
            string source = Read(PickupPath);
            AssertContains(source,
                "public static int RestoreForRoom(RoomInstance room, bool replicatedViews = false)",
                "The applier reuses the room-rebuild path with a replicated marker.");
            AssertContains(source, "replicated: replicatedViews",
                "Both SpawnCore arms (item and weapon) must thread the flag through.");
            AssertContains(source, "public static bool TryGetLiveView(",
                "The applier's handle for moving/despawning a mirror it did not build.");
            AssertContains(source, "public void SyncViewToRecord(RoomInstance room)",
                "The kept-update direction: the view snaps to the wire position.");
        }

        [Test]
        public void AutoPickup_SkipsReplicatedViews()
        {
            string body = SourceLint.MethodBody(Read(AutoPickupPath), "private void Step()");
            AssertContains(body, "pickup.Replicated",
                "Walk-over collection is the high-frequency path; without this skip a client that " +
                "strides over the host's loot duplicates every stack.");
        }

        // ── Controller ────────────────────────────────────────────────────────

        [Test]
        public void Sampler_TheCommandCarriesTheSuppressedEdge()
        {
            // Thread F moved the per-frame command build into LocalPlayerInputSampler.SampleFrame;
            // the latch consume and the named-target fields moved with it, so the lint follows.
            string source = Read(SamplerPath);
            AssertContains(source, "SimInteractCommandLatch.TryConsume(",
                "The latch has exactly one consumer: the per-frame command build.");
            AssertContains(source, "interactPressed: interactPressed,",
                "The edge rides the same command as the movement intent.");
            AssertContains(source, "targetRoomHash: interactRoomHash,",
                "The resolved room hash — never re-derived from the cursor on the host.");
            AssertContains(source, "targetObjectIndex: interactObjectIndex",
                "The resolved spawnIndex — the same key the contents roll is keyed on.");
        }

        // ── Session and applier (Assets/PfeNet) ───────────────────────────────

        [Test]
        public void Session_ConstructsTheHostWithTheInteractSink()
        {
            string source = ReadNet(SessionFile);
            AssertContains(source, "_interactSink = new PfeInteractSink(FindRoomByHash, LookupBodyBySlot);",
                "The sink resolves rooms by the same RoomKey the projector hashes and bodies from " +
                "the live slot index.");
            AssertContains(source,
                "_config.CommandWindow, _sink, this, _interactSink);",
                "A SimReplicationHost built without the interact sink admits the batch and drops " +
                "the edge — every chest stays closed on both screens.");
            AssertContains(source, "private RoomInstance FindRoomByHash(ulong roomHash)",
                "The hash resolver covers the land map AND the prob sessions, like FindRoomById.");
        }

        [Test]
        public void Session_AppliesSnapshotsInsideTheSnapshotApplicationScope()
        {
            string source = ReadNet(SessionFile);
            AssertBefore(source, "using (SimAuthorityContext.EnterSnapshotApplication())",
                "_applier.Apply(snapshot);",
                "Mirroring host state is a SnapshotApplication write; outside the scope the " +
                "authority tripwires read a client mirror as a client-originated write.");
        }

        [Test]
        public void Session_AClientPushesThePeerRole()
        {
            string source = ReadNet(SessionFile);
            AssertContains(source, "SimAuthorityContext.SetPeerRole(SimPeerRole.Client);",
                "Without the push a client process keeps the host ambient context and the " +
                "presenter's suppression gate never fires.");
            AssertContains(source, "SimInteractCommandLatch.Reset();",
                "A dying session must not leave a stale edge for the next command build.");
        }

        [Test]
        public void Applier_ReconcilesDropsBeforeLootedFlags()
        {
            string source = ReadNet(ApplierFile);
            AssertBefore(source, "ApplyDroppedItems(snapshot.Rooms);",
                "ApplyLootedObjects(snapshot.Rooms);",
                "The world gains the spilled items first, then the chest marks itself opened.");
            AssertContains(source, "SimDroppedItemSetRules.TryValidateWireSet(",
                "Malformed sets (zero/duplicate identity) are refused whole, never half-applied.");
            AssertContains(source, "SimDroppedItemSetRules.ComputePlan(",
                "The add/remove/keep decision is the pure rule, not an inline loop that can drift.");
            AssertContains(source, "SimLootHashContext.TryGetId(",
                "The hash → id step: without it the client cannot rebuild the host's drop.");
            AssertContains(source, "entityHash = drop.EntityHash",
                "The record keeps the identity the host minted; the next reconcile keys on it.");
            AssertContains(source, "WorldItemPickup.RestoreForRoom(room, replicatedViews: true)",
                "Views spawn through the SAME data path a room rebuild uses — a mirror view is a " +
                "restored view that refuses collection.");
            AssertContains(source, "view.SyncViewToRecord(room)",
                "Kept records follow the host's position updates (a fall in progress).");
            AssertContains(source, "WorldItemPickup.TryGetLiveView(record, out view) && view != null",
                "Removals destroy the view AND drop the record — the two meanings of 'going away' " +
                "must not be conflated.");
        }

        [Test]
        public void Sink_RunsTheAdmittedOpenThroughThePresenter()
        {
            string source = ReadNet(SinkFile);
            AssertContains(source, "SimHostInteractRules.Admit(",
                "Every guard is the pure rule's verdict, never a local policy re-implementation.");
            AssertContains(source, "presenter.InteractHost()",
                "The authoritative open itself runs on the presenter that owns the roll.");
            AssertContains(source, "AppliedOpens++",
                "A readback that separates 'the peer never pressed' from 'the host never ran it'.");
            AssertContains(source, "ReferenceEquals(body.Room, room)",
                "The 'loot a chest across the map' guard: the peer's body must stand in the room it " +
                "asks the host to open.");
        }

        // ── Composition root ──────────────────────────────────────────────────

        [Test]
        public void Scope_PublishesTheLootHashCatalogSources()
        {
            string source = Read(ScopePath);
            AssertContains(source, "SimLootHashContext.SetSources(",
                "Without the publish every replicated drop fails the hash → id step.");
            AssertBefore(source, "ContentInternContext.SetSources(",
                "SimLootHashContext.SetSources(",
                "The loot catalog is published in the same build callback as the intern tables, " +
                "with the same lazy-sources shape.");
            AssertContains(source, "IdsInBothForms(contentRegistry, PFE.ModAPI.ContentType.Item)",
                "The item table's ids seed the reverse catalog — in BOTH registry vocabularies, " +
                "qualified and bare: the wire hash is computed from whichever form the producer " +
                "carried (loot tables name the bare oracle id; definition-driven paths can carry " +
                "the qualified one).");
            AssertContains(source, "IdsInBothForms(contentRegistry, PFE.ModAPI.ContentType.Weapon)",
                "The weapon table seeds it too — the two tables are disjoint and both reachable, " +
                "in both id forms.");
            AssertContains(source, "contentRegistry.GetAllBareIds(type)",
                "The bare half of the dual-form source — without it every loot-table drop hashes " +
                "to a key the catalog cannot answer (the fourth play-test's unknownContent).");
        }
    }
}
