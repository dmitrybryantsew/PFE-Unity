using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Pins the <b>wiring</b> of the checkpoint's return interaction — the chain from a press on a
    /// checkpoint to <c>CampaignManager.TeleportToCheckpoint</c>.
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> Every piece of the return existed and was
    /// tested, and none of it was reachable from gameplay. <c>CheckpointRules.TeleportTarget</c> was ported
    /// and green; <c>CampaignManager.TeleportToCheckpoint</c> was ported, documented and green; and
    /// <c>DoorPropPresenter</c>'s checkpoint branch called <c>ActivateCheckpoint()</c> unconditionally,
    /// because AS3's <c>Interact</c> can name one of two functions and the port had only built one of
    /// them. So pressing E on a checkpoint with <c>tele="1"</c> — <c>rooms_surf/room_11_1.asset</c> and
    /// <c>room_1_1.asset</c> both carry it — saved the game and then did nothing for ever, and the
    /// <b>only</b> caller of <c>TeleportToCheckpoint</c> in the whole project was the F2 debug overlay.
    /// A player could not reach the camp from a checkpoint at all.</para>
    ///
    /// <para><b>Why a source lint rather than a behavioural test.</b> The routing lives in a
    /// <c>MonoBehaviour</c> that <c>RoomObjectVisualManager</c> adds with <c>AddComponent</c>, and the
    /// behaviour needs a <c>GameObject</c>, an <c>ObjectInstance</c>, a room and a live
    /// <c>CampaignManager.Current</c> — outside the editor the first of those throws
    /// <c>SecurityException: ECall methods must be packaged into a system module</c> before reaching an
    /// assertion, so a fixture that tried would never run here at all (rule: a test whose arrange cannot
    /// run proves nothing). The rules the routing <i>decides with</i> are covered behaviourally, in
    /// <c>CheckpointRulesTests</c> and <c>CampaignManagerCheckpointReturnTests</c>; what is left — and what
    /// this fixture asserts — is that the decision is actually consulted and acted on.</para>
    ///
    /// <para><b>Each assertion is one link, and a chain is only as strong as its weakest.</b> Reading the
    /// button state and then ignoring it is indistinguishable from not reading it; routing to
    /// <c>TeleportFromCheckpoint</c> and not travelling is indistinguishable from not routing; and a
    /// <c>main</c> term read from two different places is the drift <c>IsMainCheckpoint</c> exists to
    /// prevent. So every link is named, and the first one is a positive control on the destination.</para>
    /// </summary>
    [TestFixture]
    public sealed class CheckpointReturnWiringLintTests
    {
        private const string PresenterPath = "Systems/Map/Rendering/DoorPropPresenter.cs";

        private const string CampaignPath = "Systems/Campaign/CampaignManager.cs";

        /// <summary>
        /// The press path: <c>Interact</c> → <c>InteractWithCheckpoint</c> → the action switch →
        /// <c>TeleportFromCheckpoint</c> → <c>TeleportToCheckpoint</c>, and the two campaign reads that
        /// make the action anything other than a constant.
        /// </summary>
        [Test]
        public void CheckpointPress_ReachesTheTeleport_ThroughTheButtonState()
        {
            string presenter = SourceLint.ReadStripped(PresenterPath);
            string campaign = SourceLint.ReadStripped(CampaignPath);

            // ── Positive control on the destination. ─────────────────────────────────────────────
            // If this is renamed or made non-public, every assertion below is checking a dead name —
            // the presenter is in a different assembly from CampaignManager, so the reachability is
            // part of the invariant and not a detail of the compiler's.
            Assert.That(campaign, Does.Contain("public bool TeleportToCheckpoint(bool main)"),
                "CampaignManager no longer exposes TeleportToCheckpoint(bool main). If it moved, move " +
                "this guard with it — otherwise the presenter-side assertion below is checking a name " +
                "that exists nowhere.");

            // ── Link 1: the press goes through the routing, not straight to the save. ────────────
            string interact = SourceLint.MethodBody(presenter, "public void Interact(GameObject user)");

            Assert.That(interact, Does.Contain("InteractWithCheckpoint()"),
                "DoorPropPresenter.Interact no longer routes a checkpoint press through " +
                "InteractWithCheckpoint. AS3's checkpoint button names one of two functions " +
                "(CheckPoint.as:86 activate, :132/:236 teleport) and this is the only place that " +
                "chooses between them; calling ActivateCheckpoint directly is exactly the state the " +
                "return was unreachable in.");

            Assert.That(interact, Does.Not.Contain("ActivateCheckpoint("),
                "Interact calls ActivateCheckpoint directly again. The checkpoint has one route, and " +
                "it is InteractWithCheckpoint — a second entry point beside the routing would let a " +
                "press save without ever consulting the button state.");

            // ── Link 2: the routing delivers the action to something. ────────────────────────────
            string route = SourceLint.MethodBody(presenter, "private void InteractWithCheckpoint()");

            Assert.That(route, Does.Contain("CheckpointAction.Teleport"),
                "InteractWithCheckpoint does not act on CheckpointAction.Teleport. Reading the button " +
                "and ignoring it is indistinguishable from not reading it.");

            Assert.That(route, Does.Contain("TeleportFromCheckpoint"),
                "InteractWithCheckpoint does not reach TeleportFromCheckpoint, so the return action " +
                "has no destination.");

            Assert.That(route, Does.Contain("ActivateCheckpoint"),
                "InteractWithCheckpoint no longer handles CheckpointAction.Activate, so a checkpoint " +
                "that has never been touched can no longer be activated — which is the other half of " +
                "the same button.");

            // ── Link 3: the destination is the campaign's travel. ────────────────────────────────
            string teleport = SourceLint.MethodBody(presenter,
                "private void TeleportFromCheckpoint(PFE.Systems.Campaign.CampaignManager campaign)");

            Assert.That(teleport, Does.Contain("TeleportToCheckpoint"),
                "TeleportFromCheckpoint does not call CampaignManager.TeleportToCheckpoint, so the " +
                "return action arrives nowhere. That is the shape the original defect had: the rule " +
                "and the travel both existed, and nothing joined them.");

            // ── Link 4: the button state is read from the campaign, not assumed. ─────────────────
            string resolve = SourceLint.MethodBody(presenter,
                "private CheckpointAction ResolveCheckpointAction(PFE.Systems.Campaign.CampaignManager campaign)");

            Assert.That(resolve, Does.Contain("OffersReturn"),
                "ResolveCheckpointAction no longer reads CampaignManager.OffersReturn, so a " +
                "checkpoint activated earlier in the visit — the one whose button the oracle re-points " +
                "at teleport (CheckPoint.as:236-238) — would never offer the return.");

            Assert.That(resolve, Does.Contain("MainReturnIsLive"),
                "ResolveCheckpointAction no longer reads MainReturnIsLive, so a `main` checkpoint " +
                "(the camp's three, RoomsCamp.as:44/:699/:801) loses its button — and those are the " +
                "only way back to the mission from the hub.");

            Assert.That(resolve, Does.Contain("CheckpointAction.Activate"),
                "ResolveCheckpointAction no longer has an Activate outcome, so the routing would have " +
                "no answer for an untouched checkpoint.");

            // ── Link 5: `main` is one read, not two. ─────────────────────────────────────────────
            // The press routing and the activation must agree about what `main` is; a second
            // attribute read in either place is free to drift from the other.
            Assert.That(SourceLint.MethodBody(presenter, "private void ActivateCheckpoint("),
                Does.Contain("IsMainCheckpoint"),
                "ActivateCheckpoint reads `main` off the XML itself again instead of through " +
                "IsMainCheckpoint. The press routing reads the same property, so two reads of the " +
                "same attribute can now disagree about which checkpoint this is.");
        }

        /// <summary>
        /// The other end of the same chain: the button state must be <b>written</b>, and it must be
        /// written by the activation rather than by the routing.
        ///
        /// <para><b>Why this is a second test and not a longer first one.</b> "Read in three places and
        /// written in none" is this project's recurring shape — a flag whose readers all look correct and
        /// whose producer was never added. The behavioural fixture would catch it too, but only through a
        /// <c>CampaignManager</c> this harness can construct; naming the writer here means the failure
        /// reads as "the write was removed" rather than as a puzzling <c>OffersReturn</c> that never
        /// answers true.</para>
        /// </summary>
        [Test]
        public void ActivateCheckpoint_WritesTheButtonState_AndEveryLandRebuildClearsIt()
        {
            string campaign = SourceLint.ReadStripped(CampaignPath);

            string activate = SourceLint.MethodBody(campaign, "public bool ActivateCheckpoint(");
            Assert.That(activate, Does.Contain("ReturnCheckpoint ="),
                "CampaignManager.ActivateCheckpoint no longer writes ReturnCheckpoint. The presenter's " +
                "routing reads it and OffersReturn answers from it, so a missing write leaves the " +
                "return permanently unoffered while every reader still looks correct.");

            // Both places a land's rooms are built. AS3 builds a whole Location set per land entry
            // (Land.as:947) and only restores act/used (CheckPoint.as:110-122), so a rebuilt world has
            // no re-pointed button; `gotoNextLevel` reaches the same rebuild through gotoLand
            // (Game.as:471).
            foreach (string method in new[] { "public void TransitionToLand(", "public void GotoNextLevel(" })
            {
                Assert.That(SourceLint.MethodBody(campaign, method), Does.Contain("ReturnCheckpoint = null"),
                    $"CampaignManager.{method.TrimEnd('(')} no longer drops the return offer. An offer " +
                    "that outlives its land would let a checkpoint the player just arrived at travel " +
                    "them somewhere the oracle does not — the failing direction is a working-looking " +
                    "return, which is why this is asserted rather than left to the land rebuild.");
            }
        }
    }
}
