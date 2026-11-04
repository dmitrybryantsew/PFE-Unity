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
        /// <summary>
        /// Arm the volume with the swing's damage payload — the context the sweep reads for
        /// <c>DestroyTiles</c>, <c>DamageType</c> and the weapon's decal.
        ///
        /// <para><b>Why this is on the interface and not just on the component.</b> A controller that
        /// sweeps in the <i>same tick</i> it publishes its plan — which the unarmed path does, because
        /// <c>WClub</c>/<c>WKick</c> bind their first line on the fire frame — was binding that first
        /// line before the loadout had forwarded the plan, so the first line of every strike carried
        /// the <b>previous</b> strike's tile damage (and the very first strike of a run carried none at
        /// all). For the back kick that line is the only one low enough to meet a wall, which is why it
        /// read as "the kick cannot break anything". The controller now stamps its own payload before
        /// it opens the window; the loadout's forward is a second, identical write.
        /// </para>
        ///
        /// <para>It is a payload write, not a swing boundary — call it as often as the plan changes
        /// inside one swing. <see cref="SetActive"/> is the only thing that resets per-swing state.</para>
        /// </summary>
        void SetDamageContext(DamageContext ctx);

        /// <summary>Enable or disable the hit volume.</summary>
        void SetActive(bool active);

        /// <summary>
        /// Move the hit volume so it covers the swept segment from <paramref name="prevTip"/> to
        /// <paramref name="currTip"/> for this flash frame.
        /// </summary>
        void BindMove(Vector2 prevTip, Vector2 currTip);
    }
}
