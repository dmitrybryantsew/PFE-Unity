using UnityEngine;
using VContainer;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Core.Messages;
using MessagePipe;

namespace PFE.Entities.Weapons
{
    /// <summary>
    /// Trigger collider that moves between two world positions each flash frame
    /// during a melee weapon's strike window.
    ///
    /// MeleeWeaponController calls BindMove(prevTip, currTip) each flash frame
    /// while the strike is active. This moves the collider to the midpoint and
    /// scales it to cover the swept arc, so Unity trigger detection fires for
    /// anything the blade passes through.
    ///
    /// Outside the strike window SetActive(false) disables the collider entirely.
    ///
    /// On trigger enter: reads DamageContext from the controller's last ShotPlan
    /// and reports the hit to DamageSystem — same path as projectile hits.
    ///
    /// Setup:
    ///   1. Add to a child GameObject of the player's weapon object.
    ///   2. Add a CapsuleCollider2D (trigger, small size — BindMove resizes it).
    ///   3. MeleeWeaponController.HitVolume = this  (assigned by PlayerWeaponLoadout).
    ///
    /// AS3 equivalent: the vzz array sweep in WClub.actions() that updates hit
    /// points from mindlina to dlina along the weapon arc each frame.
    /// </summary>
    [RequireComponent(typeof(CapsuleCollider2D))]
    public sealed class MeleeHitVolume : MonoBehaviour, IMeleeHitVolume
    {
        // ── State ─────────────────────────────────────────────────────────────

        // Current damage context — set by PlayerWeaponLoadout from the latest ShotPlan.
        private bool          _hasDamageContext;
        private DamageContext _damageContext;

        private CapsuleCollider2D _collider;
        private bool _isActive;

        // ── Injected ─────────────────────────────────────────────────────────

#pragma warning disable CS0649
        [Inject] private PFE.Systems.Combat.DamageSystem _damageSystem;
#pragma warning restore CS0649

        // ── Initialization ────────────────────────────────────────────────────

        private void Awake()
        {
            _collider = GetComponent<CapsuleCollider2D>();
            _collider.isTrigger = true;
            SetActive(false);
        }

        /// <summary>Update the damage payload for this hit. Call from PlayerWeaponLoadout
        /// whenever a MeleeSweep ShotPlan is received.</summary>
        public void SetDamageContext(DamageContext ctx)
        {
            _damageContext    = ctx;
            _hasDamageContext = true;
        }

        // ── BindMove API ──────────────────────────────────────────────────────

        /// <summary>
        /// Move the hit volume to sweep between prevTip and currTip.
        /// AS3 equivalent: updating vzz[] positions from mindlina to dlina each frame.
        ///
        /// Positions the collider at the midpoint and sets its size to cover the arc.
        /// </summary>
        public void BindMove(Vector2 prevTip, Vector2 currTip)
        {
            Vector2 mid    = (prevTip + currTip) * 0.5f;
            float   length = Vector2.Distance(prevTip, currTip);
            float   angle  = Mathf.Atan2(currTip.y - prevTip.y, currTip.x - prevTip.x) * Mathf.Rad2Deg;

            transform.position = new Vector3(mid.x, mid.y, transform.position.z);
            transform.rotation = Quaternion.Euler(0f, 0f, angle);

            // Resize capsule to span the swept distance, minimum width 0.15 units.
            _collider.size      = new Vector2(Mathf.Max(length, 0.15f), 0.2f);
            _collider.direction = CapsuleDirection2D.Horizontal;
        }

        /// <summary>Enable or disable the hit volume.</summary>
        public void SetActive(bool active)
        {
            _isActive              = active;
            _collider.enabled      = active;
            gameObject.SetActive(active);
        }

        // ── Trigger detection ─────────────────────────────────────────────────

        /// <summary>
        /// Applies melee damage to anything the volume overlaps.
        ///
        /// <para><b>Deliberately not faction-filtered.</b> AS3 melee has no <c>fraction</c> test:
        /// <c>Unit.udarUnit</c> (<c>fe/unit/Unit.as:4125-4167</c>) applies damage unconditionally, and
        /// neither does its caller <c>Unit.udar</c> (<c>:3277</c>). Of the 38 <c>fraction</c>
        /// comparisons in AS3, none is on the melee path — so a club really does hit an ally, and
        /// adding a <c>FactionRule</c> check here would be a gameplay change dressed up as a port fix.
        /// Ranged and thrown weapons are filtered (see <see cref="PFE.Systems.Weapons.FactionRule"/>);
        /// melee is not, and that asymmetry is the oracle's.</para>
        /// </summary>
        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_isActive || other.isTrigger) return;

            var damageable = other.GetComponent<IDamageable>();
            if (damageable == null || !damageable.IsAlive) return;

            if (_hasDamageContext && _damageSystem != null)
            {
                // Report, do not resolve — DamageSystem owns the formula and the tick that runs it.
                // Melee stays unfiltered by faction, as it is in AS3; that decision belongs to the
                // caller, not here. See the remarks on OnTriggerEnter2D.
                _damageSystem.Report(PendingDamage.Direct(
                    _damageContext, damageable, other.transform.position));
            }
            else
            {
                damageable.TakeDamage(1f);
            }
        }

        // ── Debug ─────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (!_isActive) return;
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.4f);
            Gizmos.DrawWireCube(transform.position, _collider.size);
        }
    }
}
