using PFE.Data.Definitions;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Net
{
    /// <summary>
    /// The held-weapon sprite on a remote player's body — the display-only stand-in for the full
    /// <see cref="WeaponPresenter"/> + weapon-controller stack, which a remote body must never run:
    /// that stack fires projectiles, spends ammo and reads the LOCAL owner's input.
    ///
    /// <para><b>What it draws, and what it deliberately does not.</b> It resolves
    /// <c>SimUnitState.WeaponIdHash</c> through <see cref="PfeRemoteWeaponCatalog"/>, shows the
    /// weapon's idle sprite (hidden for unarmed/punch weapons), and points the barrel along the
    /// replicated aim angle using the SAME mirror conversion the local presenter uses
    /// (<see cref="WeaponVisMath"/>), so a remote gun cannot drift from the local flip rule. It does
    /// not simulate: no recoil, no reload/shoot frame FSM, no muzzle, no melee arc, no magic
    /// horn-point snap — the oracle's eases and one-shots are host-only truth. A remote viewer sees
    /// the right gun, held at the right angle, idling.</para>
    ///
    /// <para><b>One input, everything else derived.</b> The only replicated input is the aim angle.
    /// Facing and the parent mirror are read per-frame: facing-left from the aim angle's horizontal
    /// half-plane (the same test AS3's <c>X &gt; owner.celX</c> performs), the mirror sign from the
    /// body root's own <c>localScale.x</c>, which the unit's facing write already maintains on both
    /// body shapes. No second writer to fight.</para>
    /// </summary>
    public sealed class PfeRemoteWeaponView : MonoBehaviour
    {
        private SpriteRenderer _renderer;
        private WeaponDefinition _definition;
        private ulong _weaponHash;
        private float _aimAngleDeg;
        private float _bodyWidth = 0.5f;
        private float _bodyHeight = 0.7f;

        /// <summary>How far along the replicated aim ray the virtual cursor sits, world units. The
        /// hold rule only tests the cursor's X against the body box edges, so any point past the
        /// snout on the aim ray answers the reach-out test exactly like the local one.</summary>
        private const float AimProbeDistance = 10f;

        /// <summary>How many times the sprite changed definition. The play-test readback for "weapon
        /// state reached the wire": equip a weapon on the peer and this must move on the other side.</summary>
        public int SpriteChanges { get; private set; }

        /// <summary>The hash currently drawn. Lets the owner skip redundant SetWeapon calls from
        /// per-tick snapshot application.</summary>
        public ulong CurrentHash => _weaponHash;

        /// <summary>Bind the renderer drawn for the weapon. Called once by the body build — the
        /// renderer is created at build time precisely so <c>AddComponent</c> ordering cannot leave
        /// this component searching for a sprite that does not exist yet.</summary>
        public void Init(SpriteRenderer renderer)
        {
            _renderer = renderer;
            ApplySprite();
        }

        /// <summary>The owner body box in world units (AS3 <c>scX</c>/<c>scY</c>) — the hold-height
        /// fractions read them. Copied from the rig template the body was built from; without them
        /// the view falls back to the shipped player's 0.5×0.7.</summary>
        public void SetBodySize(float width, float height)
        {
            if (width > 0f) _bodyWidth = width;
            if (height > 0f) _bodyHeight = height;
        }

        /// <summary>Point the barrel along the replicated aim (degrees, world <c>atan2</c>
        /// convention). Written every snapshot; the transform work happens in <c>LateUpdate</c> so
        /// one write per tick is not one matrix rebuild per tick.</summary>
        public void SetAim(float aimAngleDeg)
        {
            _aimAngleDeg = aimAngleDeg;
        }

        /// <summary>Swap to the weapon <paramref name="weaponHash"/> names (0 = bare hooves). A hash
        /// that resolves to nothing draws nothing — an unknown weapon is hidden, never a fallback
        /// sprite that would name the wrong gun.</summary>
        public bool SetWeapon(ulong weaponHash)
        {
            if (weaponHash == _weaponHash) return false;

            _weaponHash = weaponHash;
            _definition = PfeRemoteWeaponCatalog.Resolve(weaponHash);
            SpriteChanges++;
            ApplySprite();
            return true;
        }

        private void LateUpdate()
        {
            if (_renderer == null || _definition == null) return;

            float aimRad = _aimAngleDeg * Mathf.Deg2Rad;
            // AS3 flips when the aim point is left of the weapon (Weapon.as:1996-2008). With only the
            // angle on the wire, the horizontal half-plane of the aim direction IS that test.
            bool facingLeft = Mathf.Cos(aimRad) < 0f;
            // The body root mirrors itself on facing (localScale.x), on both body shapes — folding the
            // parent's sign per frame is what keeps this view correct without a facing input.
            float parentSign = WeaponVisMath.ParentScaleSign(_renderer.transform.parent);

            ApplyHoldPosition(aimRad, parentSign);

            _renderer.transform.localScale =
                new Vector3(WeaponVisMath.VisLocalScaleX(facingLeft, parentSign), 1f, 1f);
            _renderer.transform.localRotation = Quaternion.Euler(0f, 0f,
                WeaponVisMath.VisLocalRotationDeg(aimRad, rotUpDeg: 0f, facingLeft, parentSign));
        }

        /// <summary>
        /// Hold the weapon where the local rig holds it. The local <c>WeaponPresenter</c> derives the
        /// position every frame from the body box and the cursor; this view has the same two inputs
        /// replicated (the body root transform and the aim angle), so it runs the same derivation.
        /// The old build copied the rig Weapon child's static localPosition — which the local
        /// presenter overwrites per frame, so the copy was always the origin — and the remote gun
        /// sat at the body root, i.e. the pony's feet.
        /// </summary>
        /// <para><b>Deliberately unknown inputs stay at the shipped player's defaults.</b> A remote
        /// body reports neither stair-contact, stand-still, weapon-up nor a mid-swap holster, so
        /// those arms of the oracle rule stay off; the wall probes get no tile query, which is AS3's
        /// own no-walls answer. Position is written in world space, exactly as the presenter writes
        /// it, so the parent's mirror cannot double-apply.</para>
        private void ApplyHoldPosition(float aimRad, float parentSign)
        {
            Transform parent = _renderer.transform.parent;
            if (parent == null) return;

            Vector2 origin = parent.position;
            var inputs = new WeaponHoldPointMath.Inputs
            {
                OwnerX = origin.x,
                OwnerFeetY = origin.y,
                BodyWidth = _bodyWidth,
                BodyHeight = _bodyHeight,
                FacingSign = parentSign,
                AimX = origin.x + Mathf.Cos(aimRad) * AimProbeDistance,
                Tip = _definition != null && _definition.IsUnarmed ? 1 : 0,
                WeaponKrep = 0f,
                OnStairs = false,
                Stay = false,
                WeaponUp = false,
                SwappingWeapon = false,
            };
            Vector2 hold = WeaponHoldPointMath.Resolve(in inputs, isSolidAt: null);

            Vector3 pos = _renderer.transform.position;
            _renderer.transform.position = new Vector3(hold.x, hold.y, pos.z);
        }

        private void ApplySprite()
        {
            if (_renderer == null) return;

            WeaponVisualDefinition visual = _definition != null ? _definition.weaponVisual : null;
            // Punch/kick "weapons" have no held sprite (AS3 WPunch/WKick) — the local presenter uses
            // def.IsUnarmed for exactly this gate; the remote view must agree or bare hooves grow a gun.
            bool draw = _definition != null && !_definition.IsUnarmed;
            _renderer.enabled = draw;
            _renderer.sprite = draw && visual != null ? visual.IdleSprite : null;
            _renderer.color = Color.white;
        }
    }
}
