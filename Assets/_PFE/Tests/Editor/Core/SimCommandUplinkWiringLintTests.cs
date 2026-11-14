using System;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 5 wiring lints. <c>SimCommandUplinkTests</c> executes the pure uplink end to end, but
    /// three of its claims are about <b>shape</b> and cannot be reached behaviourally: that the host
    /// refuses before it moves the ordered high-water mark, that the client re-stamps the slot on the
    /// wire only, and that the codec really writes every declared field. Those are source facts, so a
    /// stripped-source lint is the only guard that runs offline.
    ///
    /// <para><b>Why the stripper matters here.</b> The shipping files document these exact decisions in
    /// comments that name the calls by hand. An unstripped lint would stay green after the call was
    /// deleted. <c>SourceLint.ReadStripped</c> removes comments and string literals and carries its own
    /// positive control.</para>
    /// </summary>
    [TestFixture]
    public class SimCommandUplinkWiringLintTests
    {
        private const string BatchPath = "Sim/Replication/SimPlayerCommandBatch.cs";
        private const string IngressPath = "Sim/Replication/SimCommandIngress.cs";
        private const string HostPath = "Sim/Replication/SimReplicationHost.cs";
        private const string ClientPath = "Sim/Replication/SimReplicationClient.cs";
        private const string EnvelopePath = "Sim/Replication/SimReplicationEnvelope.cs";
        private const string PayloadsPath = "Sim/Replication/SimReplicationPayloads.cs";
        private const string SerializerPath = "Sim/State/SimSnapshotSerializer.cs";
        private const string CommandPath = "Sim/Input/PlayerCommand.cs";

        private static string Read(string path) => SourceLint.ReadStripped(path);

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
            string source = Read(IngressPath);
            Assert.Throws<AssertionException>(
                () => AssertContains(source, "__NO_SUCH_SYMBOL_IN_THE_UPLINK__", "control"),
                "A lint that cannot report a missing symbol is not a guard.");
            AssertContains(source, "public sealed class SimCommandIngress",
                "Positive control: the stripped file still contains real code, not an empty string.");
        }

        [Test]
        public void ProtocolV5_VersionAndCommandKindAreDeclaredInShippingSource()
        {
            AssertContains(Read(SerializerPath), "CurrentProtocolVersion = 5;",
                "The handshake shares this constant. A rollback to 4 admits a peer with no uplink.");
            AssertContains(Read(EnvelopePath), "PlayerCommandBatch = 8",
                "The first intent-carrying kind must keep a stable wire value.");
            AssertContains(Read(EnvelopePath), "NoControllerSlot = 3",
                "The join must be able to name the reason a peer cannot own a controller slot.");
        }

        [Test]
        public void Host_RoutesCommandBatchesThroughTheSharedOrderedChannel()
        {
            string source = Read(HostPath);
            AssertContains(source, "case SimReplicationMessageKind.PlayerCommandBatch:",
                "Without a switch case the host silently drops every command envelope.");
            AssertContains(source, "HandleCommandBatch(envelope);",
                "The case must actually route the envelope.");

            string body = SourceLint.MethodBody(source,
                "private void HandleCommandBatch(SimReplicationEnvelope envelope)");
            AssertContains(body, "AcceptEnvelope(",
                "Command batches must share the per-peer ordered high-water mark with acks.");
            AssertContains(body, "_commands.Receive(envelope)",
                "The ingress is the only consumer of an admitted command envelope.");
            AssertContains(body, "AcceptedCommandBatchCount++",
                "An ingress that silently drops every intent looks exactly like an idle client.");
        }

        [Test]
        public void Host_RefusesBeforeItMovesTheHighWaterMarkWhenNoIngressExists()
        {
            string body = SourceLint.MethodBody(Read(HostPath),
                "private void HandleCommandBatch(SimReplicationEnvelope envelope)");

            AssertBefore(body, "if (_commands == null)",
                "peer.ClientEnvelopeSequence = envelope.Sequence",
                "A session with no consumer must not burn the ordered channel that acks still need: " +
                "the refusal has to come before the high-water mark is committed.");
        }

        [Test]
        public void Client_ReStampsTheHostAssignedSlotOnTheWireOnly()
        {
            string body = SourceLint.MethodBody(Read(ClientPath),
                "public bool SendCommands(PlayerCommandBuffer buffer, int windowSize)");

            AssertContains(body, "TrySlotForPeer(_peerId",
                "The wire slot comes from the host-assigned peer id, not the local slot.");
            AssertContains(body, "command.SlotIndex = slot;",
                "PlayerController builds slot 0 in single-player; without the re-stamp the ingress " +
                "refuses every honest batch as an attempt to drive the host's body.");
            AssertContains(body, "SimReplicationMessageKind.PlayerCommandBatch",
                "The uplink must use the Phase 5 kind, not an existing control kind.");
            Assert.That(body, Does.Not.Contain("buffer.ReplaceLast"),
                "Re-stamping must not mutate the local tape: local prediction still uses the local slot.");
        }

        [Test]
        public void Ingress_NeverReRunsASinkOnADuplicateOnlyWindow()
        {
            string source = Read(IngressPath);
            AssertContains(source, "if (fresh > 0) _sink.Apply(",
                "A window made entirely of duplicates must not re-run one-shot jump/dash edges.");
            AssertContains(source, "OverlapWasRewritten",
                "Re-delivered commands must be byte-identical to what was applied under that tick.");
            AssertBefore(source, "for (int i = 0; i < firstNew; i++)", "peer.Applied.Enqueue",
                "Overlap integrity must be checked before the peer's tape is touched; a rejected " +
                "batch has to leave no trace. A comment marker would be stripped, so pin the code.");
        }

        [Test]
        public void AdmissionRules_UseIncreasingSequencesNotContiguousOnes()
        {
            string source = Read(BatchPath);
            AssertContains(source, "AreSequencesIncreasing",
                "PlayerCommandTape.Fold keeps the first frame's sequence, so a real tape has gaps.");
            Assert.That(source, Does.Not.Contain("AreSequencesContiguous"),
                "A +1 sequence guard would refuse every honest batch from the shipping input path.");
            AssertContains(source, "SequencesNotIncreasing = 9",
                "Every reject reason must name a check that can actually fail.");
        }

        [Test]
        public void PayloadCodec_ExposesTheBoundedReader()
        {
            string source = Read(PayloadsPath);
            AssertContains(source, "ReadPlayerCommandBatch(byte[] bytes, int maxCommands)",
                "The strict reader is what bounds a forged length prefix.");
            AssertContains(source, "SimPlayerCommandBatch.ReadFrom(reader, maxCommands)",
                "The window size must be passed through, not replaced by an invented constant.");
            AssertContains(source, "Serialize(SimPlayerCommandBatch value)",
                "One serializer per payload kind, so a corrupted kind cannot be read as a shorter message.");
        }

        [Test]
        public void CommandCodec_WritesEveryDeclaredField()
        {
            string source = Read(CommandPath);
            string body = SourceLint.MethodBody(source, "public readonly void WriteTo(BinaryWriter writer)");

            AssertContains(body, "writer.Write(TickIndex);", "Tick is the ordering authority.");
            AssertContains(body, "writer.Write(SlotIndex);", "Slot bounds which body the intent drives.");
            AssertContains(body, "writer.Write(Sequence);", "Sequence is the provenance counter.");
            AssertContains(body, "writer.Write(MoveX);", "Horizontal intent must survive the wire.");
            AssertContains(body, "writer.Write(MoveY);", "Vertical intent must survive the wire.");
            AssertContains(body, "writer.Write(AimAngle);", "Aim faces at pointer precision.");
            AssertContains(body, "writer.Write((ushort)Buttons);",
                "One-shot edges are the commands a folded tape must not lose.");

            string reader = SourceLint.MethodBody(source, "public static PlayerCommand ReadFrom(BinaryReader reader)");
            AssertContains(reader, "Buttons = (Button)reader.ReadUInt16()",
                "The read order must be the exact inverse of the write order.");
        }

        [Test]
        public void NewUplinkFiles_AreStillTransportFree()
        {
            string[] paths = { BatchPath, IngressPath };
            string[] forbidden = { "FishNet", "UnityEngine", "NetworkManager", "MonoBehaviour", "UdpClient" };

            foreach (string path in paths)
            {
                string source = Read(path);
                foreach (string needle in forbidden)
                {
                    Assert.That(source, Does.Not.Contain(needle),
                        path + " must stay pure C# so the offline wall can execute the whole uplink.");
                }
            }
        }
    }
}
