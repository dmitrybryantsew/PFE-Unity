using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// §10.2's producer wiring: the projector must actually send the two payloads its receiver was
    /// written against.
    ///
    /// <para><b>The failure this exists to catch.</b> Before this change the projector emitted
    /// <c>DroppedItems = Array.Empty&lt;SimDroppedItem&gt;()</c> and no <c>TileMutations</c> at all. Both
    /// fields were in the wire format, both were covered by round-trip tests, and both were empty in
    /// every real session — a payload that is perfectly correct and never populated. That is this
    /// project's most expensive failure mode: the contract is green, the game sends nothing. A
    /// behavioural test cannot see it (the projector needs a scene), so the wiring is pinned here.</para>
    ///
    /// <para><b>Positive assertions only.</b> Each check names the call that must be present rather than
    /// the constant that must be absent. "The file does not contain <c>Array.Empty&lt;SimDroppedItem&gt;</c>"
    /// would pass on a file that dropped the field entirely; "the file calls
    /// <c>ProjectDroppedItems(room)</c>" fails on both.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimProducerWiringLintTests
    {
        private const string ProjectorFile = "Systems/Replication/SimProjectileSnapshotProjector.cs";
        private const string HostFile = "Systems/Replication/SimTileJournalHost.cs";

        [Test]
        public void TheProjector_AttachesTheTileJournalToTheSnapshot()
        {
            string stripped = SourceLint.ReadStripped(ProjectorFile);

            Assert.That(stripped, Does.Contain("SimTileJournalHost"),
                "The projector no longer owns a journal host, so the reliable tile stream has no "
                + "producer and every snapshot carries an empty journal.");
            Assert.That(stripped, Does.Contain(".Sync(_rooms)"),
                "The journal is not reconciled with the projector's active rooms, so no room is ever "
                + "seeded and no tile notification reaches the journal.");
            Assert.That(stripped, Does.Contain("BeginLifetime("),
                "A new world revision must restart the reliable stream with a fresh baseline; without "
                + "this the journal's sequences belong to a lifetime the receiver has already dropped.");
            Assert.That(stripped, Does.Contain("TileMutations = "),
                "The snapshot is built without its tile journal — the §10.2 gap-1 payload is then "
                + "decoration: serialized, round-tripped, and never populated.");
        }

        [Test]
        public void TheProjector_ProjectsLootThroughTheIdentityRule()
        {
            string stripped = SourceLint.ReadStripped(ProjectorFile);

            Assert.That(stripped, Does.Contain("DroppedItems = ProjectDroppedItems(room)"),
                "The room delta no longer projects floor loot, so every room reports an empty loot set "
                + "and a client never sees a drop.");
            Assert.That(stripped, Does.Contain("LootItemKey("),
                "The content key must come from the one rule that separates the item and weapon tables.");
            Assert.That(stripped, Does.Contain("entityHash"),
                "Projecting loot without the record's host-assigned id is exactly the "
                + "(content id, position) keying §10.2 removed.");

            string body = MethodBody(stripped, "private SimDroppedItem[] ProjectDroppedItems(RoomInstance room)",
                "private void CollectProjectiles()");

            Assert.That(body, Does.Contain("_lastUnidentifiedDroppedItemCount++"),
                "A loot record with no id is skipped; if that skip is not counted, a stack silently "
                + "disappears from the client and nothing anywhere reports it.");
            Assert.That(body, Does.Contain(".Sort("),
                "The emitted order must be pinned. Drop order differs between two hosts, and the state "
                + "hash is a coin flip if the array order is not deterministic.");
        }

        [Test]
        public void TheHost_SeedsRecordsAndSubscribes()
        {
            string stripped = SourceLint.ReadStripped(HostFile);

            Assert.That(stripped, Does.Contain("TilesMutated +="),
                "Without the subscription the journal never learns that a tile changed — the adapter "
                + "would be a class that seeds rooms and then does nothing.");
            Assert.That(stripped, Does.Contain("TilesMutated -="),
                "A room that leaves the active set must be unsubscribed: a leaked handler keeps a "
                + "destroyed room alive and records mutations into a journal nobody sends.");
            Assert.That(stripped, Does.Contain("_journal.Seed("),
                "Seeding is what tells the journal the state the client already generated; without it "
                + "every cell looks unseeded and every baseline becomes a full room dump.");
            Assert.That(stripped, Does.Contain("_journal.Record("),
                "The notification has to reach the journal as an event, not just refresh its view.");
        }

        [Test]
        public void TheHost_DoesNotPollTheDirtySet()
        {
            string stripped = SourceLint.ReadStripped(HostFile);

            // DrainMutations is a per-tick dirty set cleared on read: a listener that reads it steals
            // the change from every other listener, and a change between two reads is lost for good.
            // Geometry cannot be rebuilt from a lost event, so the journal must not be fed from it.
            Assert.That(stripped, Does.Not.Contain("DrainMutations"),
                "The journal must not be fed from ITileQueryService.DrainMutations: it is cleared on "
                + "read, so the event is lost for every other listener and lost entirely across ticks.");
        }

        // ── Controls ─────────────────────────────────────────────────────────

        [Test]
        public void TheMethodBodyHelper_IsScopedToTheMethod()
        {
            // A whole-file `Does.Contain` would be satisfiable by a docstring elsewhere in the file, and
            // SourceLint strips comments — but not other methods. This control proves the scoping works
            // by taking a body that must NOT contain the other method's text.
            string stripped = SourceLint.ReadStripped(ProjectorFile);
            string body = MethodBody(stripped, "private void CollectUnits()", "private RoomStreamingManager ResolveStreaming()");

            Assert.That(body, Does.Not.Contain("ProjectDroppedItems("),
                "The method-body window is not scoped: assertions about ProjectDroppedItems would then "
                + "be satisfiable from anywhere in the file.");
            Assert.That(body, Does.Contain("UnitViewRegistry.Live"),
                "The window missed the method it names, so every scoped assertion is meaningless.");
        }

        [Test]
        public void TheTargetFilesExist()
        {
            var files = new List<string> { ProjectorFile, HostFile };
            foreach (string file in files)
            {
                Assert.DoesNotThrow(() => SourceLint.ReadStripped(file),
                    "Lint target missing: " + file);
            }
        }

        private static string MethodBody(string stripped, string start, string end)
        {
            int from = stripped.IndexOf(start, StringComparison.Ordinal);
            Assert.GreaterOrEqual(from, 0,
                "Declaration not found: " + start + ". If it was renamed, this lint must be updated "
                + "rather than left to fail as a missing-anchor error that reads like a code defect.");

            int to = stripped.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.Greater(to, from, "End anchor not found after " + start + ": " + end);

            return stripped.Substring(from, to - from);
        }
    }
}
