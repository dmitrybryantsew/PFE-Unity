using System;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 4 wiring lints. The behavioural half of the loop is executed by
    /// <c>SimReplicationLoopTests</c>; what those fixtures <b>cannot</b> reach is the join between the
    /// pure loop and the scene-side producer (<c>SimProjectileSnapshotProjector</c> is a tick-driven
    /// service that reads live components), and the promise that the Sim assembly stays free of a
    /// transport. Both are claims about source shape, so a stripped-source lint is the only guard that
    /// runs offline.
    ///
    /// <para><b>The FishNet decision is the reason for the purity lint.</b> §12 D5 is settled as
    /// FishNet, but the adapter is a later view-layer step. Nothing in <c>PFE.Sim</c> may name it now:
    /// importing a package here would make the offline wall depend on a Unity package and would bake
    /// §12's "do not bake in a transport assumption" contract into the very assembly the loop lives in.</para>
    /// </summary>
    [TestFixture]
    public class SimReplicationPhase4WiringLintTests
    {
        private const string ProjectorPath = "Systems/Replication/SimProjectileSnapshotProjector.cs";

        private static readonly string[] LoopPaths =
        {
            "Sim/Replication/ISimReplicationWire.cs",
            "Sim/Replication/SimReplicationEnvelope.cs",
            "Sim/Replication/SimReplicationPayloads.cs",
            "Sim/Replication/SimReplicationBaseline.cs",
            "Sim/Replication/SimReplicationInterest.cs",
            "Sim/Replication/SimReplicationHost.cs",
            "Sim/Replication/SimReplicationClient.cs",
        };

        private const string HostPath = "Sim/Replication/SimReplicationHost.cs";
        private const string ClientPath = "Sim/Replication/SimReplicationClient.cs";
        private const string WirePath = "Sim/Replication/ISimReplicationWire.cs";

        private static readonly string[] Forbidden =
        {
            "FishNet", "UnityEngine", "NetworkManager", "MonoBehaviour", "Socket", "UdpClient",
        };

        private static string Read(string path) => SourceLint.ReadStripped(path);

        private static void AssertContains(string source, string needle, string why)
        {
            Assert.That(source, Does.Contain(needle), why);
        }

        private static void AssertCount(string source, string needle, int expected, string why)
        {
            int count = 0, index = 0;
            while ((index = source.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            Assert.AreEqual(expected, count, why);
        }

        /// <summary>Brace-matched body of a type declaration. A whole-file count of <c>void Send(</c>
        /// sees both the interface declaration and its implementation; scoping to the interface is
        /// what makes "one seam method" mean one seam method.</summary>
        private static string TypeBody(string source, string declaration)
        {
            int start = source.IndexOf(declaration, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Lint target missing: " + declaration + ".");
            int open = source.IndexOf('{', start);
            Assert.That(open, Is.GreaterThan(start), "No body after " + declaration + ".");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }

            Assert.Fail("Unbalanced braces after " + declaration + ".");
            return null;
        }

        /// <summary>The <c>=&gt; …;</c> expression of an expression-bodied member, scoped to that one
        /// member. A whole-file search for the expression could be satisfied by the same read in a
        /// different property, which is exactly how a green lint stops meaning anything.</summary>
        private static string ExpressionBody(string source, string signature)
        {
            int sig = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(sig, Is.GreaterThanOrEqualTo(0), "Lint target missing: " + signature + ".");
            int arrow = source.IndexOf("=>", sig, StringComparison.Ordinal);
            Assert.That(arrow, Is.GreaterThan(sig), "No expression body after " + signature + ".");
            int end = source.IndexOf(';', arrow);
            Assert.That(end, Is.GreaterThan(arrow), "Unterminated expression body after " + signature + ".");
            return source.Substring(arrow, end - arrow + 1);
        }

        private static void AssertTransportFree(string source, string path)
        {
            foreach (string forbidden in Forbidden)
            {
                Assert.That(source, Does.Not.Contain(forbidden),
                    path + " is pure Sim code and must not name " + forbidden + " outside comments. " +
                    "The FishNet adapter belongs in the view assembly, not here.");
            }
        }

        [Test]
        public void Control_PresenceAndPurityChecksCanBothFail()
        {
            Assert.Throws<AssertionException>(
                () => AssertContains("void Tick() { }", "Acknowledge(", "control"),
                "A presence check that cannot fail is not a guard.");

            Assert.DoesNotThrow(
                () => AssertContains("void Tick() { journal.Acknowledge(n); }", "Acknowledge(", "control"));

            Assert.Throws<AssertionException>(
                () => AssertTransportFree("using UnityEngine; class X { }", "control"),
                "An absent-control that never fires would let every purity assertion be vacuous.");

            Assert.DoesNotThrow(
                () => AssertTransportFree("using System; class X { }", "control"),
                "The purity check must accept ordinary pure C#.");

            Assert.Throws<AssertionException>(
                () => AssertCount("a(); a();", "a();", 1, "control"),
                "A count check that ignores repetitions is not a guard.");

            Assert.Throws<AssertionException>(
                () => AssertCount(
                    TypeBody("interface ISimReplicationWire { void Send(A a); void Send(B b); }",
                        "interface ISimReplicationWire"),
                    "void Send(", 1, "control"),
                "A second seam method must be visible inside the scoped interface body.");

            Assert.DoesNotThrow(
                () => AssertContains(
                    ExpressionBody("public SimTileJournal Journal => _journalHost.Journal;",
                        "public SimTileJournal Journal"),
                    "_journalHost.Journal", "control"),
                "The expression-body check must accept the real producer readback.");

            Assert.Throws<AssertionException>(
                () => AssertContains(
                    ExpressionBody("public SimTileJournal Journal => new SimTileJournal();",
                        "public SimTileJournal Journal"),
                    "_journalHost.Journal", "control"),
                "A property returning a throwaway journal must be visible to the lint.");
        }

        [Test]
        public void Projector_ImplementsTheLoopSourceAndExposesTheJournal()
        {
            string source = Read(ProjectorPath);

            AssertContains(source, "IAutoRegisteredSimTickable, ISimReplicationSnapshotSource",
                "The projector must implement the loop's source interface; a host that cannot read the "
                + "snapshot and the journal from it has no way to acknowledge reliable events.");

            AssertContains(source, "public SimTileJournal Journal",
                "The ack has to reach SimTileJournal.Acknowledge. A source interface without the "
                + "journal would let the host acknowledge bytes it sent while the producer grows forever.");

            AssertContains(ExpressionBody(source, "public SimTileJournal Journal"),
                "_journalHost.Journal",
                "The property must return the producer's *own* journal. A fresh SimTileJournal here "
                + "compiles, passes every loop test (they use a fake source), and silently sends every "
                + "ack into a throwaway object while the real journal grows for the whole session.");

            AssertCount(source, "public SimSnapshot Current", 1,
                "Exactly one Current property: the loop must read the same snapshot the tick built.");
        }

        [Test]
        public void Host_AcknowledgesTheProducerJournalExactlyOnce()
        {
            string host = Read(HostPath);

            AssertCount(host, "_source.Journal.Acknowledge(", 1,
                "There must be exactly one call into the producer's journal. Zero is the §8 owed ack; "
                + "two would let a duplicate ack trim a prefix twice.");

            AssertContains(SourceLint.MethodBody(host, "private void TrimGlobalJournal("),
                "_source.Journal.Acknowledge(minimum)",
                "The call must live in the min-over-peers trim path, not in the ack handler: one fast "
                + "peer must not discard an event a slow peer still needs.");

            Assert.That(SourceLint.MethodBody(host, "private void HandleAck("),
                Does.Not.Contain(".Acknowledge("),
                "An ack handler that trims directly ignores the other peers' water marks.");
        }

        [Test]
        public void Client_AckCarriesTheReceiverReadback()
        {
            string client = Read(ClientPath);
            string sendAck = SourceLint.MethodBody(client, "private void SendAck(uint snapshotSequence)");

            AssertContains(sendAck, "_receiver.LastEventSequence",
                "The ack must report the sequence the receiver actually applied, not the incoming "
                + "snapshot's sequence; otherwise a refused snapshot could trim the host journal.");

            AssertContains(sendAck, "_receiver.WorldRevision",
                "The ack must name the receiver's lifetime so a stale-lifetime ack cannot trim a new one.");
        }

        [Test]
        public void WireSeam_IsOneSendMethod()
        {
            string wire = Read(WirePath);

            AssertCount(TypeBody(wire, "interface ISimReplicationWire"), "void Send(", 1,
                "The transport seam is one interface method. Adding a second entry point is how a "
                + "transport assumption sneaks back into the pure assembly.");

            // Two occurrences in the file: the declaration and the loopback implementation. A second
            // seam anywhere — a second interface method, or a second Send on the outbox — changes
            // this count, so the scoped check cannot be satisfied by moving the extra method away.
            AssertCount(wire, "void Send(", 2,
                "Exactly one declaration and one implementation: the seam must not grow a sibling.");

            AssertContains(
                SourceLint.MethodBody(wire, "public void Send(SimReplicationEnvelope envelope)"),
                "_sent.Add(envelope)",
                "Positive control: the second occurrence really is the loopback recorder, not a "
                + "hidden second transport entry point.");
        }

        [Test]
        public void EveryLoopFile_IsTransportFree()
        {
            foreach (string path in LoopPaths)
            {
                AssertTransportFree(Read(path), path);
            }
        }
    }
}
