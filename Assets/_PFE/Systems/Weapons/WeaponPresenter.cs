using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// MonoBehaviour that drives the weapon SpriteRenderer each frame from WeaponRuntimeState.
    ///
    /// Mirrors the visual side of Weapon.as, WClub.as, WMagic.as display-list manipulation:
    ///   - World position from State.X/Y (controller lerps ranged, snaps magic to horn point).
    ///   - Rotation faces the aim target via State.Rot (radians).
    ///   - Flip: localScale.x = -1 + rotation += 180° when aiming left (NOT scaleY — see AS3 parity note).
    ///   - RotUp: angular barrel lift applied on top of aim angle, decays each frame.
    ///   - Recoil push-back: State.TRet offset along negative aim axis.
    ///   - Frame animation FSM at 30fps: Reloading > Shooting > Prep/Ready > Idle.
    ///   - Magic: position snaps to horn point (done by controller), rotation still applied normally.
    ///   - Thrown override: sprite hidden (alpha 0) while projectile is in flight (TAttack > 0).
    ///   - Unarmed: renderer disabled entirely.
    ///
    /// AS3 flip parity note:
    ///   Old WeaponView used localScale.y = -1 which is WRONG.
    ///   AS3 Weapon.as sets scaleX = -1 + rotation += 180 when (X > owner.celX),
    ///   i.e. when the weapon's world X is right of the cursor X.
    ///   In Unity: when _aimTarget.x < State.X → facing left → flip.
    ///
    /// Execution order: 50 — after PlayerWeaponLoadout (-100), before animator-driven code.
    /// </summary>
    [DefaultExecutionOrder(50)]
    [DisallowMultipleComponent]
    public sealed class WeaponPresenter : MonoBehaviour
    {
        [Header("Renderer")]
        [SerializeField]
        [Tooltip("SpriteRenderer that displays the held weapon sprite. If null, searches children.")]
        private SpriteRenderer _spriteRenderer;

        [Header("Sorting")]
        [SerializeField] private string _sortingLayerName = "Weapons";
        [SerializeField] private int    _sortingOrder     = -1;

        // ── Runtime state (set by PlayerWeaponLoadout after Equip) ────────────

        private WeaponRuntimeState     _state;
        private WeaponDefinition       _def;
        private WeaponVisualDefinition _visual;
        private Vector2                _aimTarget;

        // ── Animation ────────────────────────────────────────────────────────

        // Flash-frame accumulator for animation tick (30fps matching controller).
        private float _frameAccum;
        private const float FlashFps = 30f;

        // ── Physics / recoil ─────────────────────────────────────────────────

        // Unity units of push-back per TRet frame. Tuned to match AS3 feel.
        private const float RecoilPosScale = 0.025f;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Called by PlayerWeaponLoadout when a weapon is equipped.
        /// Wires the presenter to the new controller's state.
        /// </summary>
        public void SetState(WeaponRuntimeState state, WeaponDefinition def)
        {
            _state         = state;
            _def           = def;
            _visual        = def?.weaponVisual;
            _frameAccum    = 0f;

            UpdateRendererEnabled();
            ApplySortingSettings();
            ShowIdle();
        }

        /// <summary>Called by PlayerWeaponLoadout when no weapon is equipped or weapon is unequipped.</summary>
        public void ClearState()
        {
            _state  = null;
            _def    = null;
            _visual = null;
            if (_spriteRenderer != null)
                _spriteRenderer.enabled = false;
        }

        /// <summary>World-space aim target. Called by PlayerWeaponLoadout each Update.</summary>
        public void SetAimTarget(Vector2 worldTarget) => _aimTarget = worldTarget;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            if (_spriteRenderer == null)
                _spriteRenderer = GetComponentInChildren<SpriteRenderer>();

            ApplySortingSettings();
        }

        private void LateUpdate()
        {
            if (_state == null || _def == null) return;

            ApplyPosition();
            ApplyRotation();

            // Advance animation at 30fps.
            _frameAccum += Time.deltaTime * FlashFps;
            int ticks = Mathf.FloorToInt(_frameAccum);
            _frameAccum -= ticks;
            for (int i = 0; i < ticks; i++)
                TickAnimation();
        }

        // ── Position ─────────────────────────────────────────────────────────

        private void ApplyPosition()
        {
            // Base position comes from the lerped State.X/Y (driven by controller).
            Vector3 pos = new Vector3(_state.X, _state.Y, transform.position.z);

            // Recoil push-back: offset along the negative aim direction.
            // AS3: weapon snaps back along its forward axis by t_ret pixels.
            if (_state.TRet > 0 && _state.TRet <= 10)
            {
                float aimAngle = _state.Rot;
                var aimDir = new Vector2(Mathf.Cos(aimAngle), Mathf.Sin(aimAngle));
                // Recoil pushes barrel back opposite to fire direction.
                // scaleX=-1 (facingLeft) reverses local X so negate for symmetry.
                float scaleSign = _aimTarget.x >= _state.X ? 1f : -1f;
                pos -= (Vector3)(aimDir * _state.TRet * RecoilPosScale * scaleSign);
            }

            transform.position = pos;
        }

        // ── Rotation / flip ───────────────────────────────────────────────────

        private void ApplyRotation()
        {
            // Magic weapons rotate normally (atan2 to aim is still calculated by controller).
            // The only magic-specific override is position (State.X/Y snaps to horn point
            // in MagicWeaponController — presenter doesn't need a special case here).

            float aimAngleDeg = _state.Rot * Mathf.Rad2Deg;

            // AS3 flip: scaleX = -1 + rotation += 180 when weapon X > cursor X.
            // Unity equivalent: when cursor is to the left of the weapon, flip.
            bool facingLeft = _aimTarget.x < _state.X;

            // RotUp: barrel lift.
            // AS3: facing right → rotation -= rotUp;  facing left → rotation += rotUp
            aimAngleDeg += facingLeft ? _state.RotUp : -_state.RotUp;

            if (facingLeft)
            {
                transform.localScale = new Vector3(-1f, 1f, 1f);
                transform.rotation   = Quaternion.Euler(0f, 0f, aimAngleDeg + 180f);
            }
            else
            {
                transform.localScale = Vector3.one;
                transform.rotation   = Quaternion.Euler(0f, 0f, aimAngleDeg);
            }
        }

        // ── Animation FSM ─────────────────────────────────────────────────────

        /// <summary>
        /// One 30fps tick of the animation FSM.
        /// Priority mirrors AS3 gotoAndStop/gotoAndPlay call order in Weapon.as:
        ///   Reloading > Shooting > Prep/Ready > Idle
        /// </summary>
        private void TickAnimation()
        {
            if (_visual == null || _spriteRenderer == null) return;

            // Thrown: sprite hides while the object is in flight.
            if (_def.weaponType == WeaponType.Thrown)
            {
                bool inFlight = _state.TAttack > 0;
                _spriteRenderer.color = inFlight
                    ? new Color(1f, 1f, 1f, 0f)
                    : Color.white;
                if (inFlight) return;
            }

            // Reloading.
            if (_state.TReload > 0 && _visual.reloadFrameStart >= 0 && _visual.reloadFrameCount > 0)
            {
                // Reload progress: 0 = just started, 1 = done.
                // Map directly to frame offset so the clip plays forward.
                float prog      = Mathf.Clamp01(_state.ReloadProgressRP.Value);
                int frameOffset = Mathf.FloorToInt(prog * _visual.reloadFrameCount);
                _spriteRenderer.sprite = _visual.GetReloadFrame(frameOffset);
                return;
            }

            // Shooting.
            if (_state.TShoot > 0 && _visual.shootFrameStart >= 0 && _visual.shootFrameCount > 0)
            {
                // TShoot counts down 3→0. Map to frame within shoot clip.
                int offsetInClip = Mathf.Clamp(
                    _visual.shootFrameCount - _state.TShoot,
                    0,
                    _visual.shootFrameCount - 1);
                _spriteRenderer.sprite = _visual.GetShootFrame(offsetInClip);
                return;
            }

            // Prep / Ready.
            if (_state.TPrep > 0 && _visual.prepFrameStart >= 0)
            {
                if (_state.TPrep >= _def.prepFrames && _visual.readyFrame >= 0)
                    _spriteRenderer.sprite = _visual.GetReadySprite();
                else
                    _spriteRenderer.sprite = _visual.GetPrepFrame(_state.TPrep);
                return;
            }

            // Idle.
            ShowIdle();
        }

        private void ShowIdle()
        {
            if (_spriteRenderer == null || _visual == null) return;
            _spriteRenderer.sprite = _visual.IdleSprite;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void UpdateRendererEnabled()
        {
            if (_spriteRenderer == null) return;
            // Unarmed (Internal) weapons have no held sprite.
            bool isUnarmed = _def != null && _def.weaponType == WeaponType.Internal;
            _spriteRenderer.enabled = !isUnarmed;
        }

        private void ApplySortingSettings()
        {
            if (_spriteRenderer == null) return;
            _spriteRenderer.sortingLayerName = _sortingLayerName;
            _spriteRenderer.sortingOrder     = _sortingOrder;
        }
    }
}
