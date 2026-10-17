using System.Collections.Generic;
using MessagePipe;
using NUnit.Framework;
using PFE.Core.Messages;
using PFE.Systems.Campaign;
using UnityEngine;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// The checkpoint's <b>button</b> — the state that decides whether a press activates or travels
    /// (<c>fe.loc.CheckPoint</c>, <c>CheckPoint.as:184-267</c>).
    ///
    /// <para><b>Why this fixture exists next to <c>CheckpointRulesTests</c>.</b> That file pins the
    /// <i>decision</i>; this one pins the decision's <b>lifetime</b>, which has no counterpart in
    /// <c>PFE.Sim</c> because it is about how long a piece of the world lives. AS3's button lives on the
    /// <c>CheckPoint</c> object; all of a land's rooms are built at land load
    /// (<c>Land.as:947</c>, indexed <c>locs[x][y][z]</c>) and swapped on a room change (<c>:1263</c>), so
    /// the object — and its re-pointed <c>inter.actFun</c> — survives a room change and dies with the
    /// land. Only <c>act</c>/<c>used</c> are restored on a later entry (<c>CheckPoint.as:110-122</c>).</para>
    ///
    /// <para><b>Every one of those is invisible from a running game in the failing direction.</b> An
    /// offer that lingers past a land rebuild looks like a working return; an offer that never appears
    /// looks like a checkpoint that authors no <c>tele</c>; and a re-derived offer looks like a
    /// convenience. The three read the same on screen, so they are separated here instead.</para>
    ///
    /// <para><b>The load-bearing case is the divergence.</b> <c>IsCurrentCheckpoint</c> and
    /// <c>OffersReturn</c> are two different questions about the same object, and the whole reason the
    /// return offer is its own piece of state is that they answer differently for a checkpoint activated
    /// earlier in the visit (current, and offering) and one restored on re-entry (current, and dead).
    /// Both are asserted below, so a future "just derive it from <c>active</c>" simplification has to
    /// break a test that says out loud why it cannot.</para>
    /// </summary>
    [TestFixture]
    public class CampaignManagerCheckpointReturnTests
    {
        /// <summary>Records what was published, so a test can tell "no build request" from "not wired".</summary>
        private sealed class RecordingPublisher<T> : IPublisher<T>
        {
            public readonly List<T> Published = new List<T>();

            public void Publish(T message)
            {
                Published.Add(message);
            }
        }

        /// <summary>The checkpoint of <c>rooms_surf/room_11_1.asset</c> — a real authored one, room cell
        /// and code included, so the identity rules are exercised against the shape the importer writes.
        /// </summary>
        private static readonly Vector3Int Room = new Vector3Int(24, 22, 0);

        private const string Code = "EWojhE8UK208iddU";

        /// <summary>
        /// A manager with <b>no catalogue</b>, which is the only kind constructible outside the engine:
        /// <c>CampaignCatalog</c> is a <c>ScriptableObject</c> and creating one offline is the ECall that
        /// throws. See <c>CampaignManagerLandBuildRequestTests</c> for the same setup.
        /// </summary>
        private static CampaignManager NewManager()
        {
            return new CampaignManager(
                transitionSubscriber: null,
                transitionPublisher: null,
                catalog: null,
                buildPublisher: new RecordingPublisher<LandBuildRequestMessage>());
        }

        private static CampaignManager EnterSurf()
        {
            CampaignManager manager = NewManager();
            manager.TransitionToLand("surf");
            return manager;
        }

        private static bool Activate(CampaignManager manager, string code, bool teleOn, bool main = false)
        {
            return manager.ActivateCheckpoint("surf", Room, code,
                isBegin: false, teleOn: teleOn, main: main, locked: false);
        }

        // ---- recording the offer (CheckPoint.as:236-238) ----

        [Test]
        public void ActivateCheckpoint_WithTheReturnArmed_OffersTheReturn()
        {
            CampaignManager manager = EnterSurf();

            Assert.IsTrue(Activate(manager, Code, teleOn: true), "a fresh, unlocked checkpoint activates");

            Assert.IsTrue(manager.OffersReturn("surf", Room, Code),
                "`mReturn && teleOn && !used` — the button became the return");
        }

        /// <summary>
        /// <b>The divergence this design exists for.</b> An activation that did not arm a return leaves
        /// the checkpoint <i>current</i> with a <b>dead</b> button (<c>CheckPoint.as:243</c> sets
        /// <c>inter.active = false</c>). Reading the button off "is this the current checkpoint" would
        /// offer a return the oracle refuses, which is why <c>OffersReturn</c> is not a synonym for
        /// <c>IsCurrentCheckpoint</c>.
        /// </summary>
        [Test]
        public void ActivateCheckpoint_WithoutTeleOn_IsCurrentButOffersNothing()
        {
            CampaignManager manager = EnterSurf();

            Activate(manager, "noTele", teleOn: false);

            Assert.IsTrue(manager.IsCurrentCheckpoint("surf", Room, "noTele"),
                "the checkpoint is still the one the player activated");
            Assert.IsFalse(manager.OffersReturn("surf", Room, "noTele"),
                "CheckPoint.as:243 — the button was removed, so there is no return to offer");
        }

        /// <summary>
        /// A second activation takes the offer off the first checkpoint — the port of
        /// <c>deactivate()</c> (<c>CheckPoint.as:321-324</c>), which the oracle runs from <c>step()</c>
        /// the moment <c>pers.currentCP</c> stops being that checkpoint.
        /// </summary>
        [Test]
        public void ActivateCheckpoint_TakesTheOfferOffThePreviousCheckpoint()
        {
            CampaignManager manager = EnterSurf();

            Activate(manager, "first", teleOn: true);
            Assert.IsTrue(manager.OffersReturn("surf", Room, "first"), "precondition");

            Activate(manager, "second", teleOn: false);

            Assert.IsFalse(manager.OffersReturn("surf", Room, "first"),
                "only one checkpoint can be the current one, so only one button can be the return");
            Assert.IsFalse(manager.OffersReturn("surf", Room, "second"),
                "and the new one did not arm a return either");
        }

        // ---- the offer's lifetime: it dies with the land's rooms ----

        /// <summary>
        /// Re-entering a land drops the offer while leaving the checkpoint current — the pair AS3
        /// produces, and the reason a <c>DoorPropPresenter</c> field could not hold this: the port
        /// rebuilds its presenters on every room change, so the offer would be lost by walking out of the
        /// room and back, while a field on the manager survives exactly as long as the oracle's object
        /// does.
        /// </summary>
        [Test]
        public void TransitionToLand_DropsTheOfferButKeepsTheCurrentCheckpoint()
        {
            CampaignManager manager = EnterSurf();
            Activate(manager, Code, teleOn: true);
            Assert.IsTrue(manager.OffersReturn("surf", Room, Code), "precondition");

            manager.TransitionToLand("surf");

            Assert.IsFalse(manager.OffersReturn("surf", Room, Code),
                "Land.as:947 builds a fresh Location set, and CheckPoint.as:110-122 restores only " +
                "act/used — so the re-pointed button does not come back");
            Assert.IsTrue(manager.IsCurrentCheckpoint("surf", Room, Code),
                "…while the checkpoint is still the current one: the button is its own piece of state");
        }

        /// <summary>
        /// <c>gotoNextLevel</c> regenerates the land through <c>gotoLand(missionId)</c>
        /// (<c>Game.as:471</c>), so it rebuilds the rooms too — and it also clears the current checkpoint
        /// outright (<c>:467-468</c>).
        /// </summary>
        [Test]
        public void GotoNextLevel_DropsTheOfferAndTheCurrentCheckpoint()
        {
            CampaignManager manager = EnterSurf();
            Activate(manager, Code, teleOn: true);
            Assert.IsTrue(manager.OffersReturn("surf", Room, Code), "precondition");

            manager.GotoNextLevel();

            Assert.IsFalse(manager.OffersReturn("surf", Room, Code), "the regenerated rooms have new buttons");
            Assert.IsFalse(manager.IsCurrentCheckpoint("surf", Room, Code),
                "Game.as:467-468 clears pers.currentCP on a descent");
        }

        // ---- what the offer is keyed on (Location.as:2065-2067) ----

        [Test]
        public void OffersReturn_MatchesTheLandAndTheCode()
        {
            CampaignManager manager = EnterSurf();
            Activate(manager, Code, teleOn: true);

            Assert.IsTrue(manager.OffersReturn("surf", Room, Code), "precondition");
            Assert.IsFalse(manager.OffersReturn("nio", Room, Code), "another land's checkpoint is not this one");
            Assert.IsFalse(manager.OffersReturn("surf", Room, "someOtherCode"), "another code is not this one");
            Assert.IsFalse(manager.OffersReturn("surf", Room, null), "a code-less checkpoint is not this one");

            Assert.IsTrue(manager.OffersReturn("surf", new Vector3Int(1, 1, 0), Code),
                "with a code on both sides the code IS the identity — the room is deliberately not " +
                "consulted, because a rebuilt land re-finds its checkpoint by code");
        }

        /// <summary>
        /// The exit room's checkpoint carries no code (<c>RoomsProb.as:4724</c>), so for it the room cell
        /// is the identity. The empty-code case must not read as a wildcard: that would let one code-less
        /// checkpoint answer for every other one in the land.
        /// </summary>
        [Test]
        public void OffersReturn_FallsBackToTheRoomWhenThereIsNoCode()
        {
            CampaignManager manager = EnterSurf();
            Activate(manager, null, teleOn: true);

            Assert.IsTrue(manager.OffersReturn("surf", Room, null), "precondition");
            Assert.IsFalse(manager.OffersReturn("surf", new Vector3Int(1, 1, 0), null),
                "no code on either side — a different room is a different checkpoint, not a match");
        }

        // ---- a main checkpoint's liveness (CheckPoint.as:300-310) ----

        /// <summary>
        /// The catalogue-dependent half of <c>MainIsLive</c> is pinned by
        /// <c>CheckpointRulesTests.MainIsLive_*</c>; this is the read that feeds it. Offline there is no
        /// catalogue, so <c>lands[missionId]</c> is absent and AS3's <c>else</c> applies — the refusing
        /// direction, which is the one worth pinning here: answering <c>true</c> would send the player to
        /// a land that does not exist.
        /// </summary>
        [Test]
        public void MainReturnIsLive_IsFalseWhenTheMissionLandCannotBeResolved()
        {
            CampaignManager manager = EnterSurf();

            Assert.IsFalse(manager.MainReturnIsLive(),
                "no catalogue and no mission — the oracle's `else`, i.e. a main checkpoint with no button");
        }
    }
}
