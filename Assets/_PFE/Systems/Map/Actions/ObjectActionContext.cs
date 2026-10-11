using UnityEngine;

namespace PFE.Systems.Map.Actions
{
    /// <summary>
    /// Everything an <see cref="IObjectAction"/> is allowed to know when it runs.
    ///
    /// <para>A <c>readonly struct</c> rather than a bag of parameters so the interface stays stable as
    /// actions are added — AS3's <c>Interact</c> reaches its owner, its location and the world through
    /// fields, and a struct is the honest port of "the context is already on hand".</para>
    /// </summary>
    public readonly struct ObjectActionContext
    {
        /// <summary>The room the trigger object stands in.</summary>
        public readonly RoomInstance Room;

        /// <summary>The placed object that carries the <c>allact</c>.</summary>
        public readonly ObjectInstance Object;

        /// <summary>Who triggered it — the player, for a key press.</summary>
        public readonly GameObject User;

        /// <summary>
        /// The trigger object's own world position.
        ///
        /// <para>AS3 passes the interactable's own <c>X</c>/<c>Y</c> as the arrival point of a layer
        /// toggle (<c>Interact.as:1620</c> calls <c>outLoc(5, this.X, this.Y)</c>), and
        /// <c>Land.gotoLoc</c> uses those coordinates verbatim for <c>param1 == 5</c>
        /// (<c>Land.as:1367-1371</c>). So a <c>comein</c> action needs the <i>object's</i> position,
        /// not the player's — the two differ by the interaction reach.</para>
        /// </summary>
        public readonly Vector3 WorldPosition;

        public ObjectActionContext(
            RoomInstance room,
            ObjectInstance obj,
            GameObject user,
            Vector3 worldPosition)
        {
            Room = room;
            Object = obj;
            User = user;
            WorldPosition = worldPosition;
        }

        /// <summary>
        /// The object's <c>allact</c>, or empty when it has none. Empty means "this object has no
        /// script" — the caller's own behaviour applies.
        ///
        /// <para>Resolved by <see cref="ObjectInstance.GetAllAct"/>, which reads the placement first and
        /// falls back to the definition — AS3's order (<c>Interact.as:287-289</c>, then
        /// <c>:383-385</c>). It must not read the placement alone: the Z doors author
        /// <c>allact='comein'</c> on the definition (<c>AllData.as:4916-4922</c>) and nothing on the
        /// placement, so a placement-only read reports "no script" for the very doors this action
        /// exists to serve.</para>
        /// </summary>
        public string ActionId => Object != null ? Object.GetAllAct() : string.Empty;

        /// <summary>
        /// The object's <c>prob</c> id — the detached room its interaction enters — or empty.
        ///
        /// <para><b>This is tested before <see cref="ActionId"/>, not alongside it.</b>
        /// <c>Interact.allAct</c> opens with <c>if(this.prob != null) …gotoProb…</c> and reaches its
        /// <c>allact</c> chain only in the <c>else</c> (<c>Interact.as:1558-1565</c>), so an object that
        /// carries both does <b>not</b> run its <c>allact</c>. And the one that carries both is the one
        /// the whole descent loop turns on: the bottom-row <c>exit</c> box gets
        /// <c>prob='exit_&lt;land&gt;'</c> from its placement (<c>Location.as:2119</c>) and
        /// <c>allact='exit'</c> from its definition row (<c>AllData.as:5016</c>), so the ordering is what
        /// sends it into the exit room instead of straight to <c>gotoNextLevel</c>. The other two prob
        /// carriers are single-sided: <c>doorprob</c>/<c>doorboss</c> have no <c>allact</c> at all
        /// (<c>:5018-5019</c>), and <c>doorout</c>'s <c>prob</c> is authored empty on purpose so its
        /// <c>allact='probreturn'</c> (<c>:5017</c>) is what runs.</para>
        ///
        /// <para>Placement-only and present-and-non-empty: see
        /// <see cref="ObjectInstance.GetProb"/> for both rules and the oracle lines.</para>
        /// </summary>
        public string ProbId => Object != null ? Object.GetProb() : string.Empty;

        /// <summary>The object's id, for logging.</summary>
        public string ObjectId => Object != null ? Object.GetResolvedDefinitionId() : string.Empty;
    }

    /// <summary>
    /// A handler for one <c>allact</c> value — the port of a branch of AS3's <c>Interact.act()</c>
    /// switch (<c>Interact.as:889</c>).
    /// </summary>
    public interface IObjectAction
    {
        /// <summary>The <c>allact</c> value this handles, e.g. <c>"comein"</c>.</summary>
        string ActionId { get; }

        /// <summary>
        /// Whether the action can run right now.
        ///
        /// <para>Separate from <see cref="Execute"/> because the answer is also what the HUD needs:
        /// AS3 gates the whole thing on the guard chain in <c>UnitPlayer.outLoc()</c>
        /// (<c>UnitPlayer.as:540-559</c>), and a refusal there produces no visible change at all.</para>
        /// </summary>
        bool CanExecute(in ObjectActionContext context);

        /// <summary>Run it. Only called when <see cref="CanExecute"/> returned true.</summary>
        /// <returns><c>true</c> when something happened.</returns>
        bool Execute(in ObjectActionContext context);
    }
}
