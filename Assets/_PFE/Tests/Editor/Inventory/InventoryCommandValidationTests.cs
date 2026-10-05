using NUnit.Framework;
using PFE.Systems.Inventory;

namespace PFE.Tests.Editor
{
    /// <summary>
    /// The command seam's input contract — the rules a request must satisfy before any state is
    /// touched.
    ///
    /// <para><b>These run in the offline wall</b> (<c>.workbuddy-ai/tmp/wallrun</c>), unlike
    /// <see cref="LocalInventoryCommandSinkTests"/>: <see cref="InventoryCommandValidation"/> references
    /// no <c>UnityEngine</c> type and makes no engine call, so nothing here throws the
    /// <c>ECall methods must be packaged into a system module</c> failure that makes every fixture
    /// touching <c>ScriptableObject.CreateInstance</c> unrunnable outside the editor.</para>
    ///
    /// <para><b>Every rejection rule has a positive control.</b> A suite that only asserts "this is
    /// rejected" cannot tell "correctly rejects" from "rejects everything", which is the exact shape of
    /// failure this project keeps hitting (a scan that returned 0 for every needle because the needle
    /// itself was malformed). <see cref="WellFormedCommand_HasNoRejectReason"/> is that control: it
    /// fails if the validator ever starts rejecting valid commands.</para>
    /// </summary>
    [TestFixture]
    public class InventoryCommandValidationTests
    {
        // ── Positive control ──────────────────────────────────────────────────

        [Test]
        public void WellFormedCommand_HasNoRejectReason()
        {
            Assert.IsNull(InventoryCommandValidation.RejectReason(InventoryCommand.AddItem("kombu", 3)));
            Assert.IsNull(InventoryCommandValidation.RejectReason(InventoryCommand.RemoveItem("kombu", 1)));
            Assert.IsNull(InventoryCommandValidation.RejectReason(InventoryCommand.AddArmor("leather")));
            Assert.IsNull(InventoryCommandValidation.RejectReason(InventoryCommand.ConsumeAmmo("p10", 12)));
        }

        [Test]
        public void WellFormedCommand_IsWellFormed()
        {
            Assert.IsTrue(InventoryCommandValidation.IsWellFormed(InventoryCommand.AddItem("kombu")));
        }

        [Test]
        public void EveryKnownKind_IsAccepted()
        {
            Assert.IsTrue(InventoryCommandValidation.IsKnownKind(InventoryCommandKind.AddItem));
            Assert.IsTrue(InventoryCommandValidation.IsKnownKind(InventoryCommandKind.RemoveItem));
            Assert.IsTrue(InventoryCommandValidation.IsKnownKind(InventoryCommandKind.AddArmor));
            Assert.IsTrue(InventoryCommandValidation.IsKnownKind(InventoryCommandKind.ConsumeAmmo));
        }

        // ── Id ────────────────────────────────────────────────────────────────

        [Test]
        public void NullId_IsRejected()
        {
            string reason = InventoryCommandValidation.RejectReason(InventoryCommand.AddItem(null));
            Assert.IsNotNull(reason);
            StringAssert.Contains("id", reason);
        }

        [Test]
        public void EmptyId_IsRejected()
        {
            Assert.IsNotNull(InventoryCommandValidation.RejectReason(InventoryCommand.AddItem("")));
            Assert.IsNotNull(InventoryCommandValidation.RejectReason(InventoryCommand.RemoveItem("")));
            Assert.IsNotNull(InventoryCommandValidation.RejectReason(InventoryCommand.AddArmor("")));
            Assert.IsNotNull(InventoryCommandValidation.RejectReason(InventoryCommand.ConsumeAmmo("", 5)));
        }

        [Test]
        public void RejectReason_NamesTheKind_SoTheFailingCommandIsIdentifiable()
        {
            string reason = InventoryCommandValidation.RejectReason(InventoryCommand.RemoveItem(""));
            StringAssert.Contains(nameof(InventoryCommandKind.RemoveItem), reason);
        }

        // ── Quantity ──────────────────────────────────────────────────────────

        [Test]
        public void ZeroQuantity_IsRejected()
        {
            string reason = InventoryCommandValidation.RejectReason(InventoryCommand.AddItem("kombu", 0));
            Assert.IsNotNull(reason);
            StringAssert.Contains("positive", reason);
        }

        [Test]
        public void NegativeQuantity_IsRejected()
        {
            Assert.IsNotNull(InventoryCommandValidation.RejectReason(InventoryCommand.RemoveItem("kombu", -1)));
            Assert.IsNotNull(InventoryCommandValidation.RejectReason(InventoryCommand.ConsumeAmmo("p10", -5)));
        }

        [Test]
        public void RejectReason_QuotesTheOffendingQuantity()
        {
            string reason = InventoryCommandValidation.RejectReason(InventoryCommand.AddItem("kombu", -3));
            StringAssert.Contains("-3", reason);
        }

        [Test]
        public void OneQuantity_IsAccepted_SoTheBoundaryIsNotOffByOne()
        {
            Assert.IsNull(InventoryCommandValidation.RejectReason(InventoryCommand.AddItem("kombu", 1)));
        }

        // ── Unknown kind ──────────────────────────────────────────────────────

        [Test]
        public void UnknownKind_IsRejected()
        {
            // Reachable because the constructor is public for the deserializer's sake: a command can
            // arrive from a peer on a newer build, and an unrecognised kind must be a stated rejection,
            // never a silent no-op.
            var unknown = (InventoryCommandKind)9876;
            Assert.IsFalse(InventoryCommandValidation.IsKnownKind(unknown));

            var command = new InventoryCommand(unknown, "kombu", 1, PickupType.Loot);
            string reason = InventoryCommandValidation.RejectReason(command);

            Assert.IsNotNull(reason);
            StringAssert.Contains("9876", reason);
        }

        [Test]
        public void UnknownKind_IsRejectedBeforeTheIdIsEvenConsidered()
        {
            // The kind guard runs first, so a command that is unknown *and* malformed reports the
            // unknown kind — the more useful diagnosis of the two. Asserting on "unsupported" rather
            // than on the numeric kind is what pins *which* guard fired: the id guard's message would
            // also have named the kind, so a weaker assertion would pass with the kind guard removed.
            var command = new InventoryCommand((InventoryCommandKind)9876, "", 0, PickupType.Loot);

            Assert.IsFalse(InventoryCommandValidation.IsWellFormed(command));
            StringAssert.Contains("unsupported", InventoryCommandValidation.RejectReason(command));
        }

        // ── Command factories ─────────────────────────────────────────────────

        [Test]
        public void AddItem_CarriesItsArguments()
        {
            var command = InventoryCommand.AddItem("kombu", 7, PickupType.Trade);

            Assert.AreEqual(InventoryCommandKind.AddItem, command.Kind);
            Assert.AreEqual("kombu", command.Id);
            Assert.AreEqual(7, command.Quantity);
            Assert.AreEqual(PickupType.Trade, command.PickupType);
        }

        [Test]
        public void AddItem_DefaultsToASingleLootPickup()
        {
            var command = InventoryCommand.AddItem("kombu");

            Assert.AreEqual(1, command.Quantity);
            Assert.AreEqual(PickupType.Loot, command.PickupType);
        }

        [Test]
        public void AddArmor_IsAlwaysOneUnit()
        {
            var command = InventoryCommand.AddArmor("leather");

            Assert.AreEqual(InventoryCommandKind.AddArmor, command.Kind);
            Assert.AreEqual(1, command.Quantity);
        }

        [Test]
        public void ConsumeAmmo_CarriesTheRoundCount()
        {
            var command = InventoryCommand.ConsumeAmmo("p10", 12);

            Assert.AreEqual(InventoryCommandKind.ConsumeAmmo, command.Kind);
            Assert.AreEqual("p10", command.Id);
            Assert.AreEqual(12, command.Quantity);
        }

        [Test]
        public void ToString_NamesTheCommand_SoALogLineIsReadable()
        {
            StringAssert.Contains("kombu", InventoryCommand.AddItem("kombu", 3).ToString());
            StringAssert.Contains("3", InventoryCommand.AddItem("kombu", 3).ToString());
        }

        // ── Result ────────────────────────────────────────────────────────────

        [Test]
        public void Ok_CarriesTheAmountMoved()
        {
            InventoryCommandResult result = InventoryCommandResult.Ok(5);

            Assert.IsTrue(result.Applied);
            Assert.AreEqual(5, result.Amount);
            Assert.IsNull(result.Reason);
        }

        [Test]
        public void Ok_WithNoAmount_IsStillApplied()
        {
            // The zero-amount case is real: a consume that drew nothing is an applied command that
            // moved 0 units, not a rejection — the reload path reads Amount, not Applied, for the count.
            InventoryCommandResult result = InventoryCommandResult.Ok();

            Assert.IsTrue(result.Applied);
            Assert.AreEqual(0, result.Amount);
        }

        [Test]
        public void Fail_CarriesTheReason()
        {
            InventoryCommandResult result = InventoryCommandResult.Fail("no item row for 'nope'");

            Assert.IsFalse(result.Applied);
            Assert.AreEqual(0, result.Amount);
            StringAssert.Contains("nope", result.Reason);
        }

        [Test]
        public void Fail_WithNoReason_StillHasOne()
        {
            // A rejection with no stated reason is indistinguishable from a command that never
            // arrived, so the reason is never allowed to be null.
            Assert.IsNotNull(InventoryCommandResult.Fail(null).Reason);
        }
    }
}
