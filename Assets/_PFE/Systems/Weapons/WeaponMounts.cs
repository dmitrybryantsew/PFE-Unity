using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Exposes the three weapon mount-point Transforms on a character.
    ///
    /// Add this component to the player (or enemy) root GameObject.
    /// Assign child empty GameObjects in the Inspector:
    ///
    ///   _weaponHoldPoint  — mouth / magic-grip position (AS3: weaponX/Y)
    ///   _magicHoldPoint   — horn tip for spell weapons   (AS3: magicX/Y)
    ///   _throwPoint       — optional throw origin; falls back to hold point
    ///
    /// WeaponMounts is a pure data component — no logic. Controllers and
    /// PlayerWeaponLoadout read the world-space positions every Tick().
    ///
    /// <para><b>The one rule that is not optional: the mount must not live inside the weapon.</b> The
    /// presenter positions the weapon <i>from</i> the hold point, so a mount that is a child of the
    /// weapon moves with the weapon and is chased forever. The shipped <c>Player.prefab</c> violates
    /// this — all three fields point at the vis's <c>muzzle</c> child — which is why the gun drifts off
    /// the character. See <see cref="WeaponHoldPointTransform"/>.</para>
    ///
    /// Setup:
    ///   1. Add two empty child GameObjects under the character body layer.
    ///      Name them "WeaponHoldPoint" and "MagicHoldPoint" for clarity.
    ///   2. Position them to match the character art:
    ///        WeaponHoldPoint ≈ snout/mouth (slightly in front of face).
    ///        MagicHoldPoint  ≈ horn tip (top-front of head).
    ///   3. Drag both into this component's fields in the Inspector. They must be children of the
    ///      character body — never of the weapon — or the weapon will chase them.
    ///   4. If the character has no horn, leave _magicHoldPoint unassigned —
    ///      MagicHoldPoint property falls back to WeaponHoldPoint automatically.
    ///   5. Note that AS3 has no such markers at all: <c>UnitPlayer.setWeaponPos</c> derives the point
    ///      every frame. Assigning these fields is an override, and only a rig that cannot derive a
    ///      point (no <c>UnitController</c>) needs one.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WeaponMounts : MonoBehaviour
    {
        [Header("Mount points (assign child empty GameObjects)")]
        [SerializeField]
        [Tooltip("Hold point for all normal ranged/melee weapons. Corresponds to weaponX/Y in AS3.")]
        internal Transform _weaponHoldPoint;

        [SerializeField]
        [Tooltip("Horn tip for magic/spell weapons (tip==5). Corresponds to magicX/Y in AS3. " +
                 "Leave empty for non-unicorn characters — falls back to WeaponHoldPoint.")]
        internal Transform _magicHoldPoint;

        [SerializeField]
        [Tooltip("Optional throw origin for WThrow weapons. Leave empty to use WeaponHoldPoint.")]
        internal Transform _throwPoint;

        // ── Public accessors ───────────────────────────────────────────────────

        /// <summary>
        /// Whether an explicit weapon hold Transform was assigned.
        ///
        /// <para>The hold point is normally <i>derived</i> from the owner's body and the cursor —
        /// <see cref="WeaponHoldPointMath"/> is the port of AS3 <c>UnitPlayer.setWeaponPos</c>, which
        /// computes it every frame and has no marker object at all. This flag exists so a caller can
        /// tell "the author pinned one deliberately" from "the field is empty and the property is
        /// silently falling back to <c>transform.position</c>", which is the only case where the
        /// fallback is wrong rather than merely unauthored.</para>
        /// </summary>
        public bool HasWeaponHoldPoint => _weaponHoldPoint != null;

        /// <summary>Whether an explicit horn/magic Transform was assigned (see <see cref="MagicHoldPoint"/>).</summary>
        public bool HasMagicHoldPoint => _magicHoldPoint != null;

        /// <summary>Whether an explicit throw-origin Transform was assigned (see <see cref="ThrowPoint"/>).</summary>
        public bool HasThrowPoint => _throwPoint != null;

        /// <summary>
        /// The authored weapon hold Transform, or null. Exposed for the one check a caller must make
        /// before trusting <see cref="WeaponHoldPoint"/>.
        ///
        /// <para><b>A mount must not be a descendant of the weapon it positions.</b> The presenter
        /// writes the weapon's world position <i>from</i> the hold point, so a hold point that is a
        /// child of the weapon moves when the weapon moves and the weapon chases it — an unbounded
        /// drift, not a mount. <c>Player.prefab</c> is wired exactly that way: all three fields point at
        /// the <c>muzzle</c> child of the vis. A caller that reads <see cref="WeaponHoldPoint"/>
        /// unguarded therefore makes the gun walk off the character. Use
        /// <c>PlayerWeaponLoadout</c>'s descendant check, or derive the point instead
        /// (<see cref="WeaponHoldPointMath"/>).</para>
        /// </summary>
        public Transform WeaponHoldPointTransform => _weaponHoldPoint;

        /// <summary>The authored horn/magic Transform, or null. Carries the same warning as
        /// <see cref="WeaponHoldPointTransform"/> — the shipped rig points it at the same
        /// inside-the-weapon node, so magic snaps to a point that the weapon itself drags around.</summary>
        public Transform MagicHoldPointTransform => _magicHoldPoint;

        /// <summary>The authored throw-origin Transform, or null. Same warning as
        /// <see cref="WeaponHoldPointTransform"/>.</summary>
        public Transform ThrowPointTransform => _throwPoint;

        /// <summary>
        /// World-space position of the normal weapon hold point.
        /// AS3 equivalent: owner.weaponX / owner.weaponY.
        ///
        /// <para><b>Prefer <see cref="WeaponHoldPointMath"/>.</b> AS3 has no hold-point marker: the
        /// point is derived per frame from the body box and the cursor. This property is the
        /// hand-authored override for a rig that genuinely needs one.</para>
        /// </summary>
        public Vector2 WeaponHoldPoint =>
            _weaponHoldPoint != null ? (Vector2)_weaponHoldPoint.position : (Vector2)transform.position;

        /// <summary>
        /// World-space position of the magic / horn mount point.
        /// AS3 equivalent: owner.magicX / owner.magicY.
        /// Falls back to WeaponHoldPoint if no horn Transform is assigned.
        /// </summary>
        public Vector2 MagicHoldPoint =>
            _magicHoldPoint != null ? (Vector2)_magicHoldPoint.position : WeaponHoldPoint;

        /// <summary>
        /// World-space position of the throw origin.
        /// Falls back to WeaponHoldPoint if not assigned.
        /// </summary>
        public Vector2 ThrowPoint =>
            _throwPoint != null ? (Vector2)_throwPoint.position : WeaponHoldPoint;

        /// <summary>
        /// Returns the correct mount position for a given weapon family.
        /// Magic weapons use MagicHoldPoint; thrown weapons use ThrowPoint;
        /// everything else uses WeaponHoldPoint.
        /// </summary>
        public Vector2 GetMountFor(ShotOrigin origin)
        {
            return origin switch
            {
                ShotOrigin.HornPoint  => MagicHoldPoint,
                ShotOrigin.ThrowPoint => ThrowPoint,
                _                     => WeaponHoldPoint,
            };
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_weaponHoldPoint != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(_weaponHoldPoint.position, 0.05f);
                UnityEditor.Handles.Label(_weaponHoldPoint.position + Vector3.up * 0.1f, "WeaponHold");
            }
            if (_magicHoldPoint != null)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(_magicHoldPoint.position, 0.05f);
                UnityEditor.Handles.Label(_magicHoldPoint.position + Vector3.up * 0.1f, "MagicHold");
            }
            if (_throwPoint != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(_throwPoint.position, 0.05f);
                UnityEditor.Handles.Label(_throwPoint.position + Vector3.up * 0.1f, "ThrowPoint");
            }
        }
#endif
    }
}
