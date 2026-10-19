using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// A unit part that owns where its held weapon is <b>drawn</b> — the port of an AS3
    /// <c>setWeaponPos</c> override that hangs the weapon off a bone instead of the body.
    ///
    /// <para><b>The gap this closes.</b> <see cref="WeaponHoldRule"/> models the overrides whose hold
    /// point is a constant or a simple function of the body (<c>protect</c> and <c>robobrain</c> at a
    /// flat <c>Y - 40</c>, <c>sentinel</c> at <c>Y - 90</c>, <c>spritebot</c> at <c>Y - 1</c>). It
    /// cannot model the classes whose hold point comes out of a <b>bone</b> — the ones that read a
    /// per-animation-frame <c>wPos</c> table and then an <c>emit</c> marker on a rotating limb. Those
    /// are <c>UnitMerc</c> (the griffin's arm), <c>UnitGutsy</c>, <c>UnitRaider</c> and
    /// <c>UnitAlicorn</c>, and they all do the same thing structurally: pose a node, then answer
    /// "where is the emit marker now".</para>
    ///
    /// <para><b>Why an interface rather than a reference to the griffin.</b>
    /// <see cref="EnemyWeaponMount"/> is shared by every armed archetype and lives in
    /// <c>Systems/Weapons</c>; a griffin type would be a dependency pointing the wrong way, and the
    /// next bone-table unit would either copy the mount or widen it again. The mount asks the
    /// question; whoever owns a bone answers it.</para>
    ///
    /// <para><b>The answer is a world point and it may be stale by one frame.</b> The oracle has the
    /// same ordering: <c>setVisPos</c> writes <c>arm.rotation</c> from <c>currentWeapon.rot</c>, and
    /// <c>setWeaponPos</c> reads <c>arm.emit</c> through that rotation, so the weapon position
    /// reflects the aim of the previous frame. Implementing this by re-posing on every call — rather
    /// than caching a transform written last <c>LateUpdate</c> — removes the lag the port can remove
    /// without inventing an order the oracle does not have.</para>
    /// </summary>
    public interface IWeaponHoldPointSource
    {
        /// <summary>
        /// The weapon's hold point in world space, or <c>false</c> when this source has nothing to
        /// say yet (no arm sprite, no animation data, hidden) — in which case the caller must fall
        /// back to <see cref="WeaponHoldPointMath.ResolveHold"/> rather than to zero.
        /// </summary>
        bool TryResolveHoldPoint(out Vector2 worldPoint);

        /// <summary>
        /// Bind the weapon whose aim this part follows — AS3's <c>currentWeapon</c>.
        ///
        /// <para>A bone-driven hold point is posed <i>by</i> the weapon's <c>rot</c> (a griffin's arm
        /// turns with the gun), so the part cannot answer "where is the hand" without it. Called by
        /// <see cref="EnemyWeaponMount"/> once it has built a controller; before that the part holds
        /// its rest pose, which is the oracle's own state for a unit whose weapon is not yet rolled.</para>
        /// </summary>
        void BindWeapon(WeaponRuntimeState weaponState);

        /// <summary>
        /// Tell the source where the weapon is aiming, before it is asked for the hold point.
        ///
        /// <para>The arm's rotation is the weapon's rotation, so a source cannot pose itself without
        /// knowing the aim — and the mount already resolves the aim for the presenter on the same
        /// tick. Passing it here is what lets one aim point drive the arm, the gun and the shot.</para>
        /// </summary>
        void SetAimWorldPoint(Vector2 worldPoint);
    }
}
