using UnityEngine;

namespace PFE.Systems.Map.Actions
{
    /// <summary>
    /// "A prob room can be entered, and left again" — the runtime half of AS3
    /// <c>Land.gotoProb</c> (<c>Land.as:1391-1438</c>).
    ///
    /// <para><b>Why this is a seam and not a direct call to the transition manager.</b> The two halves
    /// live in different layers on purpose. <see cref="PFE.Systems.Map.Generation.ProbTransition"/> is the
    /// <i>decision</i> — what to remember, and where the return door lands — and is pure and offline
    /// testable. Activating a room is engine work: streaming, re-render, the physics world, the camera.
    /// <c>ObjectActionDispatcher</c> must be able to run <c>prob</c> and <c>probreturn</c> without
    /// acquiring a dependency on any of that, and a test must be able to assert the ordering with a
    /// double.</para>
    ///
    /// <para><b>Null is a state, not an error — but it is a <i>reported</i> state.</b> With no host the
    /// dispatcher leaves <c>probreturn</c> unregistered and reports the <c>prob</c> branch as unhandled,
    /// so a door that cannot move the player says so once by id instead of opening like an ordinary box.
    /// That is the same degradation <c>comein</c> takes when there is no
    /// <c>IRoomLayerTransition</c>.</para>
    /// </summary>
    public interface IProbRoomHost
    {
        /// <summary>
        /// Whether a prob room can be entered at all right now — the host has a room registry and a room
        /// to come back to. Mirrors <c>Land.ativateLoc</c>'s first guard
        /// (<c>if(this.prob != "" &amp;&amp; this.probs[this.prob] == null) return false</c>,
        /// <c>Land.as:1237-1240</c>), which is what makes a prob door on an unbuilt prob a refusal
        /// rather than a one-way trip.
        /// </summary>
        bool CanEnterProb { get; }

        /// <summary>
        /// Whether the player is currently inside a prob room — i.e. a return point has been saved and
        /// not yet spent. This is the port of AS3's <c>this.loc.landProb != ""</c>, the guard the
        /// <c>probreturn</c> branch tests (<c>Interact.as:1569</c>).
        ///
        /// <para><b>Deliberately a separate question from <see cref="CanEnterProb"/>.</b> They are two
        /// different states and the oracle tests them in two different places: entering needs a room to go
        /// <i>to</i>, returning needs a room to come <i>from</i>. Collapsing them into one flag would make
        /// the return door live in rooms that never had an entry, and the entry door dead in a prob room
        /// whose registry had been rebuilt.</para>
        /// </summary>
        bool IsInProbRoom { get; }

        /// <summary>
        /// Enter the detached room <paramref name="probId"/> names, remembering where to put the player
        /// back.
        ///
        /// <para>The return point is built by the host, not passed in: it needs the <i>current room's</i>
        /// land coordinate and the <i>player's</i> position, and only the host has either. The door's own
        /// world position is passed because AS3 prefers it over the player's
        /// (<c>Interact.as:1565</c> passes <c>this.owner.X, this.owner.Y</c> as <c>param2</c>/<c>param3</c>,
        /// which <c>Land.as:1417-1424</c> uses when both are non-negative).</para>
        /// </summary>
        /// <param name="probId">AS3 <c>Interact.prob</c> — the prob room name.</param>
        /// <param name="doorWorldPosition">The trigger's own world position.</param>
        /// <returns>
        /// <c>true</c> when the player was actually moved. <c>false</c> is a refusal: AS3 returns early
        /// from <c>ativateLoc</c> without touching the player (<c>Land.as:1427-1436</c>), so a failed
        /// entry must not teleport anyone.
        /// </returns>
        bool TryEnterProb(string probId, Vector3 doorWorldPosition);

        /// <summary>
        /// Leave the prob room and restore the saved room and position — AS3 <c>gotoProb("")</c>
        /// (<c>Land.as:1393-1410</c>), reached from the <c>doorout</c> box's <c>allact='probreturn'</c>
        /// (<c>AllData.as:5017</c>).
        /// </summary>
        /// <returns><c>false</c> when nothing was saved, i.e. the player is not in a prob room.</returns>
        bool TryReturnFromProb();
    }
}
