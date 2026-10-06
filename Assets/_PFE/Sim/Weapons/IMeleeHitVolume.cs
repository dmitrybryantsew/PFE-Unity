using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// The sweep surface a melee controller drives: a trigger volume that is enabled for the
    /// duration of the strike window and moved along the blade path each flash frame.
    ///
    /// <para><b>Why this interface exists.</b> <see cref="PFE.Entities.Weapons.MeleeHitVolume"/> is a
    /// MonoBehaviour that caches its <c>CapsuleCollider2D</c> in <c>Awake</c>. EditMode tests never
    /// run <c>Awake</c> for <c>AddComponent</c>, so the real component cannot be exercised in a unit
    /// test at all — which is how "Thrust and Overhead never enable the hit volume" stayed invisible.
    /// A recording stub implementing this interface makes the controller's *decisions* (when to
    /// enable, what path to sweep) assertable with no GameObject and no engine.</para>
    ///
    /// Implemented by: <c>MeleeHitVolume</c>.
    /// </summary>
    public interface IMeleeHitVolume
    {
        /// <summary>Enable or disable the hit volume.</summary>
        void SetActive(bool active);

        /// <summary>
        /// Move the hit volume so it covers the swept segment from <paramref name="prevTip"/> to
        /// <paramref name="currTip"/> for this flash frame.
        /// </summary>
        void BindMove(Vector2 prevTip, Vector2 currTip);
    }
}
