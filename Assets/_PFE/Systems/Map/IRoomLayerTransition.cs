using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The one capability an <c>allact</c> script needs in order to move the player between a land
    /// cell's two z layers.
    ///
    /// <para><b>Why this is a seam and not a direct call to <c>RoomTransitionManager</c>.</b> The
    /// manager is a <c>MonoBehaviour</c> that runs the swap inside a coroutine, so an action that
    /// called it directly could only be exercised from a live scene. The action logic itself is pure
    /// decision-making (is there a room on the other side? where does the player arrive?), and that is
    /// what the tests need to pin. The manager is the only production implementor; a test double is the
    /// only other one.</para>
    ///
    /// <para>It also keeps the direction of the dependency honest: the action layer depends on an
    /// abstraction of "the map can flip layers", not on the streaming layer's concrete type.</para>
    /// </summary>
    public interface IRoomLayerTransition
    {
        /// <summary>
        /// Whether a layer toggle is possible right now: a current room exists, no transition is
        /// already running, and the opposite layer of that cell actually holds a room.
        ///
        /// <para>The last clause is AS3's <c>locs[x][y][z] == null</c> check
        /// (<c>Land.as:1335-1346</c>), which returns <c>null</c> and leaves the player where they
        /// are rather than moving them into an empty cell.</para>
        /// </summary>
        bool CanToggleLayer { get; }

        /// <summary>
        /// Flip the player to the opposite z layer of the cell they are standing in, arriving at
        /// <paramref name="worldPosition"/>.
        /// </summary>
        /// <param name="player">The player to move. Null is refused.</param>
        /// <param name="worldPosition">
        /// Where to arrive. AS3 passes the trigger object's own X/Y (<c>Interact.as:1620</c>), not the
        /// player's position, so the caller supplies it rather than the manager deriving it.
        /// </param>
        /// <returns><c>false</c> when the toggle was refused and nothing changed.</returns>
        bool ToggleLayer(GameObject player, Vector3 worldPosition);
    }
}
