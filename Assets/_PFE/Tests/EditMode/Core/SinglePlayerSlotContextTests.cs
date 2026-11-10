using NUnit.Framework;
using PFE.Core;
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Phase 2.2 acceptance (F-09, §8 row 2.2): the offline-checkable half of the slot-context
    /// contract — a <see cref="SinglePlayerSlotContext"/> reports exactly one local slot and resolves
    /// slot 0 to the same <see cref="EntityId"/> as <c>EntityId.CreateForPlayer(0)</c>.
    ///
    /// <para><b>Why only the pure-C# half runs here.</b> <c>GetPlayer</c> and <c>GetCamera</c> reach
    /// the Unity scene (<c>FindFirstObjectByType</c>, <c>Camera.main</c>), which is an ECall that
    /// throws outside the editor — the same reason the golden traces and
    /// <c>ClientPredictionSeamTests</c>'s probe are editor-only. This fixture deliberately asserts
    /// only the container-free, Unity-free surface: the slot count and the identity mapping, which is
    /// the part a multiplayer <c>IPlayerSlotContext</c> must keep stable. The scene-resolving half is
    /// verified by the owner in the editor.</para>
    ///
    /// <para><b>What is pinned.</b> (1) <c>LocalSlotCount == 1</c> — single-player is exactly one
    /// slot, the number <c>IPlayerSlotContext</c> consumers rely on before multiplayer raises it; and
    /// (2) <c>GetEntityId(0)</c> equals <c>EntityId.CreateForPlayer(0)</c> — the slot→entity mapping
    /// that gives <c>PlayerCommand.SlotIndex</c> its meaning (F-03). A non-zero slot maps to
    /// <c>player:1</c>, proving the index flows through rather than being hardcoded to 0.</para>
    /// </summary>
    [TestFixture]
    public sealed class SinglePlayerSlotContextTests
    {
        [Test]
        public void LocalSlotCount_IsExactlyOne()
        {
            var context = new SinglePlayerSlotContext();

            Assert.That(context.LocalSlotCount, Is.EqualTo(1),
                "Single-player is exactly one local slot. IPlayerSlotContext consumers rely on this " +
                "before multiplayer raises it.");
        }

        [Test]
        public void GetEntityId_SlotZero_MatchesCreateForPlayer()
        {
            var context = new SinglePlayerSlotContext();

            EntityId fromContext = context.GetEntityId(0);
            EntityId expected = EntityId.CreateForPlayer(0);

            Assert.That(fromContext, Is.EqualTo(expected),
                "Slot 0 must resolve to EntityId.CreateForPlayer(0) — the mapping that gives " +
                "PlayerCommand.SlotIndex its meaning (F-03).");
            Assert.That(fromContext.Hash, Is.EqualTo(expected.Hash),
                "The wire form (Hash) must match so a snapshot keys the predicted entity identically.");
            Assert.That(fromContext.DebugString, Is.EqualTo("player:0"));
        }

        [Test]
        public void GetEntityId_FlowsTheSlotIndexThrough()
        {
            var context = new SinglePlayerSlotContext();

            // A non-zero slot must map to player:N, not collapse to player:0 — proving the index is
            // honoured, so a future multiplayer context can address slots 1..3 without a consumer change.
            Assert.That(context.GetEntityId(1), Is.EqualTo(EntityId.CreateForPlayer(1)));
            Assert.That(context.GetEntityId(3), Is.EqualTo(EntityId.CreateForPlayer(3)));
            Assert.That(context.GetEntityId(1).Hash, Is.Not.EqualTo(context.GetEntityId(0).Hash),
                "Distinct slots must produce distinct entity ids.");
        }
    }
}
