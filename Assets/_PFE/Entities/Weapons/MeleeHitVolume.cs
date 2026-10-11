using UnityEngine;
using VContainer;
using PFE.Entities.Units;
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

        // ── Owner ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The unit swinging this volume, resolved from the hierarchy — the volume is a child of the
        /// wielder's weapon object on both paths (<c>PlayerWeaponLoadout</c>'s scene child, and the
        /// child <c>EnemyWeaponMount</c> builds).
        /// </summary>
        private UnitController _ownerUnit;

        /// <summary>Whether the hierarchy has been walked. Cached, because a miss would otherwise re-walk every trigger.</summary>
        private bool _ownerResolved;

        // ── Initialization ────────────────────────────────────────────────────

        private void Awake()
        {
            // Before SetActive(false): the owner walk is a hierarchy query, and it is clearer to do it
            // while this object is still active.
            ResolveOwner();

            _collider = GetComponent<CapsuleCollider2D>();
            _collider.isTrigger = true;
            SetActive(false);
        }

        private void ResolveOwner()
        {
            if (_ownerResolved) return;
            _ownerResolved = true;
            _ownerUnit = GetComponentInParent<UnitController>();
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
        /// Applies melee damage to anything the volume overlaps, <b>except units of the wielder's own
        /// faction</b>.
        ///
        /// <para><b>The faction filter is the oracle's, and this class used to say the opposite.</b>
        /// The old comment reasoned from <c>Unit.udarUnit</c> (<c>Unit.as:4125-4167</c>) and its caller
        /// <c>Unit.udar</c> (<c>:3277</c>) — both of which really do apply damage with no <c>fraction</c>
        /// test — and concluded that melee is faction-blind in AS3. But those are the <i>pair-level</i>
        /// functions; the thing that decides <i>which pairs are tested at all</i> is the loop in
        /// <c>Bullet.run()</c>, and melee goes through it: <c>WClub.as:161-162</c> sets
        /// <c>checkLine = true; b.checkLine = checkLine;</c> on its bullet, and the very next thing that
        /// loop does (<c>Bullet.as:515</c>) is</para>
        /// <code>
        /// if((this.targetObj || _loc2_.fraction != this.owner.fraction) &amp;&amp; X &gt;= _loc2_.X1 &amp;&amp; …)
        /// </code>
        /// <para>A swing carries no <c>targetObj</c>, so the fraction test is the whole rule — the same
        /// predicate <see cref="FactionRule.CanHitDirectly"/> already encodes for projectiles. Confirmed
        /// by <c>WClub.as:517</c>'s <c>this.weap.isLine(X,Y)</c>, which only a weapon (never a plain
        /// bullet) has.</para>
        ///
        /// <para><b>What the missing filter cost.</b> Two things, and the second is why this was fixed
        /// now: a player's swing damaged friendly NPCs, and — once enemy melee was enabled, since every
        /// ArmedShooter that rolls a <c>tip == 1</c> weapon now swings one — <b>an enemy damaged itself
        /// and its allies</b>. AS3's <c>weaponX = X; weaponY = Y - scY * 0.5</c> puts the muzzle inside
        /// the owner's own body, so the volume genuinely overlaps its wielder.</para>
        ///
        /// <para><b>Non-units stay unfiltered.</b> A crate or a barrel is not a <c>Unit</c> and has no
        /// <c>fraction</c> in AS3 either — those are handled by <c>udarBox</c> — so only a
        /// <see cref="UnitController"/> target is tested.</para>
        /// </summary>
        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_isActive || other.isTrigger) return;

            var damageable = other.GetComponent<IDamageable>();
            if (damageable == null || !damageable.IsAlive) return;

            ResolveOwner();

            if (_ownerUnit != null)
            {
                UnitController targetUnit = other.GetComponentInParent<UnitController>();
                if (targetUnit != null
                    && !FactionRule.CanHitDirectly(_ownerUnit.Faction, targetUnit.Faction))
                {
                    return;
                }
            }

            if (_hasDamageContext && _damageSystem != null)
            {
                // Report, do not resolve — DamageSystem owns the formula and the tick that runs it.
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
