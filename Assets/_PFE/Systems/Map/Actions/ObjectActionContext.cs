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
        /// </summary>
        public string ActionId => Object != null ? Object.GetAttribute("allact", string.Empty) : string.Empty;

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
