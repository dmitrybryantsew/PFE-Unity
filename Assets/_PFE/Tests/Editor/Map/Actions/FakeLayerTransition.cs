using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map.Actions
{
    /// <summary>
    /// Test double for <see cref="IRoomLayerTransition"/>.
    ///
    /// <para>It records what it was asked to do, which is the point: the interesting assertion about
    /// <c>comein</c> is not "a toggle happened" but "the toggle was asked for the <i>trigger's</i>
    /// position, not the player's". A double that only returned <c>true</c> could not tell the two
    /// apart, and the real manager is a MonoBehaviour running a coroutine, so it cannot be used here.
    /// </para>
    /// </summary>
    internal sealed class FakeLayerTransition : IRoomLayerTransition
    {
        /// <summary>What <see cref="CanToggleLayer"/> reports.</summary>
        public bool CanToggleLayerValue = true;

        /// <summary>What <see cref="ToggleLayer"/> returns.</summary>
        public bool ToggleResult = true;

        public int ToggleCalls;

        public GameObject LastPlayer;

        public Vector3 LastWorldPosition;

        public bool CanToggleLayer => CanToggleLayerValue;

        public bool ToggleLayer(GameObject player, Vector3 worldPosition)
        {
            ToggleCalls++;
            LastPlayer = player;
            LastWorldPosition = worldPosition;
            return ToggleResult;
        }
    }
}
