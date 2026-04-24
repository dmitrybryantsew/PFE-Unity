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
    /// Setup:
    ///   1. Add two empty child GameObjects under the character body layer.
    ///      Name them "WeaponHoldPoint" and "MagicHoldPoint" for clarity.
    ///   2. Position them to match the character art:
    ///        WeaponHoldPoint ≈ snout/mouth (slightly in front of face).
    ///        MagicHoldPoint  ≈ horn tip (top-front of head).
    ///   3. Drag both into this component's fields in the Inspector.
    ///   4. If the character has no horn, leave _magicHoldPoint unassigned —
    ///      MagicHoldPoint property falls back to WeaponHoldPoint automatically.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WeaponMounts : MonoBehaviour
    {
        [Header("Mount points (assign child empty GameObjects)")]
        [SerializeField]
        [Tooltip("Hold point for all normal ranged/melee weapons. Corresponds to weaponX/Y in AS3.")]
        private Transform _weaponHoldPoint;

        [SerializeField]
        [Tooltip("Horn tip for magic/spell weapons (tip==5). Corresponds to magicX/Y in AS3. " +
                 "Leave empty for non-unicorn characters — falls back to WeaponHoldPoint.")]
        private Transform _magicHoldPoint;

        [SerializeField]
        [Tooltip("Optional throw origin for WThrow weapons. Leave empty to use WeaponHoldPoint.")]
        private Transform _throwPoint;

        // ── Public accessors ───────────────────────────────────────────────────

        /// <summary>
        /// World-space position of the normal weapon hold point.
        /// AS3 equivalent: owner.weaponX / owner.weaponY.
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
