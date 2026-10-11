using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
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
    ///   - Recoil push-back: State.TRet px along world X, sign from the flip.
    ///   - Frame animation FSM at 30fps: Reloading > Shooting > Prep/Ready > Idle.
    ///   - Magic: position snaps to horn point (done by controller), rotation still applied normally.
    ///   - Thrown override: sprite hidden (alpha 0) while a throw is in progress (TAttack > 0) — and
    ///     explicitly restored for every other weapon. That restore is the reason the alpha write is
    ///     not nested inside the thrown-only branch; see <see cref="WeaponVisMath.HidesHeldSprite"/>.
    ///   - Unarmed: renderer disabled entirely.
    ///
    /// AS3 flip parity note:
    ///   The retired WeaponView (Phase 2.2) used localScale.y = -1, which is WRONG.
    ///   AS3 Weapon.as sets scaleX = -1 + rotation += 180 when (X > owner.celX),
    ///   i.e. when the weapon's world X is right of the cursor X.
    ///   In Unity: when _aimTarget.x < State.X → facing left → flip.
    ///   The unit root mirrors itself on facing and this weapon is its child, so the flip has to be
    ///   converted to local space — see <see cref="WeaponVisMath"/>.
    ///
    /// <para><b>The scale/rotation go on the VIS, not on this transform.</b> AS3 mutates <c>vis</c>
    /// — the display object whose registration point <i>is</i> the weapon's position — so the art
    /// mirrors about that point and the grip stays put. Writing them on this transform instead
    /// mirrors the vis child's own local offset too, which moved the sprite pivot by twice that
    /// offset (2 × 0.17 u in the shipped prefab) on every turn-around. <see cref="WeaponVisMath"/>
    /// owns the flip rule so this class and the ranged controller cannot disagree about which way
    /// the barrel points.</para>
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

        /// <summary>
        /// True when the owner is a <c>krep &gt; 0</c> unit — AS3 <c>Weapon.krep</c>, copied from
        /// <c>owner.weaponKrep</c> (<c>Weapon.as:499-501</c>). Selects the oracle's rigidly-mounted
        /// <c>animate()</c> branch instead of the aimed one.
        ///
        /// <para>The player is <b>always</b> <c>krep == 0</c> — <c>UnitPlayer.as:368</c> assigns it
        /// unconditionally — which is why this defaults to false and the player path is unchanged. It is
        /// set from <c>UnitDefinition.isStable</c> for an NPC.</para>
        /// </summary>
        private bool _fixedMeleeMount;

        /// <summary>
        /// The node that carries the flip and the aim rotation — the port's <c>vis</c>.
        /// It is the SpriteRenderer's own transform, not <c>transform</c>: see the class doc.
        /// </summary>
        private Transform _vis;

        // ── Animation ────────────────────────────────────────────────────────

        // Flash-frame accumulator for the animation tick, advanced at SimClock.FramesPerSecond so
        // it matches the weapon controllers.
        private float _frameAccum;

        // ── Physics / recoil ─────────────────────────────────────────────────

        // AS3 Weapon.animate() (:1973): `vis.x = X - t_ret * vis.scaleX * 2` — 2 Flash pixels per
        // TRet frame, and nothing on Y. Kept in pixels and divided by the visual's PPU so the
        // constant means what the oracle says it means.
        private const float RecoilPixelsPerFrame = 2f;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// World-space muzzle (barrel tip) of the weapon as it is <i>currently drawn</i>, or null
        /// before the first <see cref="LateUpdate"/>.
        ///
        /// <para>This is the port's <c>Weapon.getBulXY()</c> (<c>Weapon.as:1395-1420</c>), which
        /// takes the vis's <c>emit</c> child through <c>vis.localToGlobal()</c> — i.e. through the
        /// flip and the aim rotation. The ranged controller fires from this point, so the round
        /// leaves the barrel and follows the mirror instead of leaving the weapon's origin.
        /// Null means "no vis drawn yet" and the controller falls back to the weapon position,
        /// which is what AS3 does when <c>vis.emit</c> is absent.</para>
        /// </summary>
        public Vector2? MuzzleWorldPosition { get; private set; }

        /// <summary>
        /// Called by PlayerWeaponLoadout when a weapon is equipped.
        /// Wires the presenter to the new controller's state.
        /// </summary>
        /// <param name="fixedMeleeMount">
        /// AS3 <c>owner.weaponKrep &gt; 0</c> — the weapon is rigidly mounted, not aimed. Only a melee
        /// weapon changes appearance (see <see cref="WeaponVisMath.MeleeFixedVisRotationDeg"/>); a ranged
        /// weapon's <c>animate()</c> already reads <c>krep</c> the same way in both branches for the
        /// axis this port draws, so this is passed but only acted on for melee. Defaults to
        /// <c>false</c>, which is the player's value.
        /// </param>
        public void SetState(WeaponRuntimeState state, WeaponDefinition def, bool fixedMeleeMount = false)
        {
            _state           = state;
            _def             = def;
            _visual          = def?.weaponVisual;
            _fixedMeleeMount = fixedMeleeMount;
            _frameAccum      = 0f;
            MuzzleWorldPosition = null;   // do not let a stale muzzle outlive the old weapon

            UpdateRendererEnabled();
            ApplySortingSettings();

            // Undo the previous weapon's state immediately rather than waiting for the next tick. A
            // thrown weapon leaves the renderer fully transparent while its throw is in progress, and
            // the weapon being equipped now is never mid-throw — so a fresh equip must start opaque.
            // Without this the first frame after a switch is invisible even once TickAnimation is
            // correct, which is exactly the flicker a player reads as "the gun disappeared".
            if (_spriteRenderer != null)
                _spriteRenderer.color = Color.white;

            ShowIdle();
        }

        /// <summary>Called by PlayerWeaponLoadout when no weapon is equipped or weapon is unequipped.</summary>
        public void ClearState()
        {
            _state  = null;
            _def    = null;
            _visual = null;
            _fixedMeleeMount = false;
            MuzzleWorldPosition = null;
            if (_spriteRenderer != null)
            {
                // Restore the alpha as well as disabling: a thrown weapon may have left it at 0, and a
                // re-enable later (a new equip) must not inherit that.
                _spriteRenderer.color   = Color.white;
                _spriteRenderer.enabled = false;
            }
        }

        /// <summary>World-space aim target. Called by PlayerWeaponLoadout each Update.</summary>
        public void SetAimTarget(Vector2 worldTarget) => _aimTarget = worldTarget;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            if (_spriteRenderer == null)
                _spriteRenderer = GetComponentInChildren<SpriteRenderer>();

            _vis = _spriteRenderer != null ? _spriteRenderer.transform : null;

            ApplySortingSettings();
        }

        private void LateUpdate()
        {
            if (_state == null || _def == null) return;

            // One flip decision for the whole frame: position, rotation and muzzle all read it, so
            // they cannot disagree about which way the weapon points.
            bool facingLeft = WeaponVisMath.IsFacingLeft(_aimTarget.x, _state.X);

            ApplyPosition(facingLeft);
            ApplyRotation(facingLeft);
            UpdateMuzzle();

            // Advance animation at 30fps.
            _frameAccum += Time.deltaTime * SimClock.FramesPerSecond;
            int ticks = Mathf.FloorToInt(_frameAccum);
            _frameAccum -= ticks;
            for (int i = 0; i < ticks; i++)
                TickAnimation();
        }

        // ── Position ─────────────────────────────────────────────────────────

        private void ApplyPosition(bool facingLeft)
        {
            // Base position comes from the lerped State.X/Y (driven by controller).
            Vector3 pos = new Vector3(_state.X, _state.Y, transform.position.z);

            // Recoil push-back, exactly as AS3 writes it (Weapon.as:1973):
            //   vis.x = X - t_ret * vis.scaleX * 2;   vis.y = Y;
            // X only, 2 px per TRet frame, and the sign is the vis's own scaleX — so a weapon
            // facing left is pushed the other way, which is what makes it look like the gun is
            // being shoved back rather than sliding sideways. The old port code pushed along the
            // 2-D aim vector and negated the sign for facing-left, which reversed it.
            if (_state.TRet > 0 && _state.TRet <= 10)
            {
                float ppu = _visual != null && _visual.pixelsPerUnit > 0 ? _visual.pixelsPerUnit : 100f;
                pos.x -= _state.TRet * WeaponVisMath.VisScaleX(facingLeft)
                         * (RecoilPixelsPerFrame / ppu);
            }

            transform.position = pos;
        }

        // ── Rotation / flip ───────────────────────────────────────────────────

        private void ApplyRotation(bool facingLeft)
        {
            if (_vis == null) return;

            // AS3 writes scaleX + rotation on the vis (Weapon.as:1996-2008). Doing it here rather
            // than on `transform` keeps the vis's own local offset out of the mirror — otherwise
            // the sprite pivot jumps by twice that offset every time the character turns.
            //
            // The parent's own mirror has to be divided out. The unit root flips with facing
            // (UnitController.ApplyFacingToTransform sets `localScale.x = _facingDirection`) and this
            // weapon is its child, so Unity already renders a horizontal mirror when the character
            // turns left. Writing AS3's `scaleX = -1` on top of that gave +1 — the two cancelled —
            // and the rotation term then left the gun pointing the way the character *was* facing.
            // That is the reported "turned left and it fires from its back": the barrel was drawn on
            // the far side of the grip, so the round left the gun's rear.
            float parentSign = WeaponVisMath.ParentScaleSign(_vis.parent);

            // AS3 `WClub.animate`'s krep > 0 branch. A melee weapon on a rigidly-mounted unit
            // (`<comb krep='1'>`) is drawn at a fixed angle instead of the swing arc, and mirrors on
            // Y rather than X — a genuinely different transform, not a re-parameterisation of the
            // ranged one. `weaponR` is not ported (it lives in the unported `wPos` bone table), so it
            // is 0 here; see WeaponVisMath.MeleeFixedVisRotationDeg.
            if (_fixedMeleeMount && _def != null && _def.weaponType == WeaponType.Melee)
            {
                _vis.localScale = new Vector3(
                    WeaponVisMath.MeleeVisLocalScaleX(parentSign),
                    WeaponVisMath.MeleeVisLocalScaleY(facingLeft), 1f);
                _vis.localRotation = Quaternion.Euler(0f, 0f,
                    WeaponVisMath.MeleeVisLocalRotationDeg(
                        WeaponVisMath.MeleeFixedVisRotationDeg(facingLeft, 0f), parentSign));
                return;
            }

            _vis.localScale    = new Vector3(WeaponVisMath.VisLocalScaleX(facingLeft, parentSign), 1f, 1f);
            _vis.localRotation = Quaternion.Euler(0f, 0f,
                WeaponVisMath.VisLocalRotationDeg(_state.Rot, _state.RotUp, facingLeft, parentSign));
        }

        // ── Muzzle ───────────────────────────────────────────────────────────

        /// <summary>
        /// Publishes the barrel tip in world space, through the vis's own transform — the port of
        /// AS3's <c>vis.localToGlobal(emit)</c>. Runs after the flip/rotation are applied, so the
        /// point carries them.
        /// </summary>
        private void UpdateMuzzle()
        {
            if (_vis == null)
            {
                MuzzleWorldPosition = null;
                return;
            }

            Vector2 local = _visual != null ? _visual.muzzleLocalOffset : Vector2.zero;
            MuzzleWorldPosition = _vis.TransformPoint(local);
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

            // ── Visibility, recomputed every tick for EVERY weapon type ──────────────────────────
            //
            // This is the only write to the renderer's alpha in the class, so it is also the only
            // place that can restore it — and it must therefore run whether or not the current weapon
            // is thrown. When this lived inside the `WeaponType.Thrown` branch, throwing a grenade set
            // the alpha to 0, and switching to any other weapon then took the non-thrown path, never
            // touched the colour, and left the new weapon drawn fully transparent — permanently, since
            // only another thrown weapon would ever clear it. Reported as: "I select acidgr then
            // minigun (or any other weapon) — its graphics disappear."
            bool hideForThrow = WeaponVisMath.HidesHeldSprite(_def.weaponType, _state.TAttack);
            _spriteRenderer.color = hideForThrow
                ? new Color(1f, 1f, 1f, 0f)
                : Color.white;
            if (hideForThrow) return;

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
            // Punch/kick weapons (AS3 WPunch / WKick) have no held sprite.
            // Use def.IsUnarmed (punch > 0) — NOT weaponType == WeaponType.Internal, which is
            // tip==0 and means RANGED. That predicate hid the sprite for all 57 mis-routed
            // tip==0 ranged weapons; see 13_WeaponTypeBehaviourAudit_2026-09-27.md §1.5.
            bool isUnarmed = _def != null && _def.IsUnarmed;
            _spriteRenderer.enabled = !isUnarmed;
        }

        private void ApplySortingSettings()
        {
            if (_spriteRenderer == null) return;
            _spriteRenderer.sortingLayerName = _sortingLayerName;
            _spriteRenderer.sortingOrder     = _sortingOrder;
        }
    }

    /// <summary>
    /// The single place that decides which way the held weapon's vis points.
    ///
    /// <para><b>Why it exists.</b> The flip rule was written out twice — once in
    /// <see cref="WeaponPresenter"/> to draw the gun, and (in a different shape) in the ranged
    /// controller to aim the shot — and the two had already drifted: the presenter mirrored the
    /// sprite while the controller fired from the weapon origin, so the round left the gun's rear
    /// and the error flipped sign with the sprite. Anything that needs to know the weapon's facing
    /// reads it from here.</para>
    ///
    /// <para>AS3 oracle: <c>Weapon.animate()</c> (<c>Weapon.as:1996-2008</c>) and
    /// <c>Weapon.getBulXY()</c> (<c>:1395-1420</c>).</para>
    ///
    /// <para><b>The AS3 rule describes a WORLD transform, and the port has to convert it.</b> In the
    /// oracle the vis hangs off a container that is never scaled, so <c>vis.scaleX</c> and
    /// <c>vis.rotation</c> <i>are</i> its world transform. In the port the weapon is a child of the
    /// unit root, which mirrors itself on every facing change, so the same numbers written as
    /// <i>local</i> values are read back through that mirror. <see cref="ParentScaleSign"/> and the
    /// two <c>VisLocal*</c> adapters below are the conversion; <see cref="VisScaleX"/> and
    /// <see cref="VisRotationDeg"/> stay a line-by-line transcription of the oracle so it can still
    /// be diffed against it.</para>
    ///
    /// <para><b>Assumption, stated so it can be checked.</b> The conversion folds a mirror
    /// (<c>M = R(α)·S(±1, 1)</c>) and takes the parent's rotation <c>α</c> to be zero. That holds for
    /// the shipped rig — the unit root only ever writes <c>localScale.x</c>, never a rotation, and
    /// nothing above the weapon rotates. A rotating ancestor would need the <c>α</c> term added
    /// here, not a fix at the call site.</para>
    /// </summary>
    public static class WeaponVisMath
    {
        /// <summary>
        /// AS3 flips when the weapon's world X is right of the cursor X (<c>X &gt; owner.celX</c>),
        /// i.e. when the aim target is to the left of the weapon.
        /// </summary>
        public static bool IsFacingLeft(float aimWorldX, float visWorldX) => aimWorldX < visWorldX;

        /// <summary>
        /// Whether the held weapon's sprite must be drawn fully transparent this tick — AS3 hides the
        /// held <c>vis</c> while a thrown object is in flight (<c>WThrow</c>).
        ///
        /// <para><b>Both inputs are load-bearing, and the second is the one that gets dropped.</b>
        /// <c>tAttack &gt; 0</c> alone is <i>not</i> the rule: every weapon counts it down after a shot,
        /// so hiding on it alone would blink every gun out for a few frames per shot. The hide is gated
        /// on the weapon being <see cref="WeaponType.Thrown"/> as well.</para>
        ///
        /// <para><b>Why this is a named function and not an <c>if</c> at the call site.</b> The call
        /// site writes the renderer's alpha, and it is the <i>only</i> write to it in the class — so it
        /// is also the only place that can undo it. When that write lived inside the thrown-only branch,
        /// a throw left the alpha at 0 and the branch then never ran again for the next weapon: the new
        /// weapon was drawn fully transparent, permanently. Engine-free so the rule can be pinned
        /// offline — a <c>MonoBehaviour</c> cannot be.</para>
        /// </summary>
        /// <param name="weaponType">The <b>currently equipped</b> weapon's type.</param>
        /// <param name="tAttack">That weapon's attack countdown, in frames.</param>
        public static bool HidesHeldSprite(WeaponType weaponType, int tAttack)
            => weaponType == WeaponType.Thrown && tAttack > 0;

        /// <summary>AS3 <c>vis.scaleX</c>: <c>-1</c> facing left, <c>1</c> facing right.</summary>
        public static float VisScaleX(bool facingLeft) => facingLeft ? -1f : 1f;

        /// <summary>
        /// AS3 <c>vis.rotation</c> in degrees: facing right → <c>rot·180/π − rotUp</c>;
        /// facing left → <c>rot·180/π + 180 + rotUp</c>. The extra 180 compensates the mirror so the
        /// barrel still points along the aim instead of backwards.
        /// </summary>
        public static float VisRotationDeg(float rotRad, float rotUpDeg, bool facingLeft)
            => rotRad * Mathf.Rad2Deg + (facingLeft ? rotUpDeg + 180f : -rotUpDeg);

        // ── Melee (WClub.animate) ─────────────────────────────────────────────

        /// <summary>
        /// AS3 melee mirror: <c>WClub.animate</c> writes <c>vis.scaleY = storona</c> (and never
        /// <c>scaleX</c>), i.e. a <b>vertical</b> mirror — the opposite axis to the ranged rule's
        /// <c>vis.scaleX</c>.
        ///
        /// <para><b>Why the ranged rule is nevertheless correct for a <c>krep == 0</c> melee weapon</b>
        /// — and why this helper exists only for the <c>krep &gt; 0</c> case. The two are the same
        /// <i>world</i> transform, exactly:
        /// <c>R(rot+180)·S(−1,1) = R(rot)·S(−1,−1)·S(−1,1) = R(rot)·S(1,−1)</c>. So a melee weapon
        /// facing left drawn with the ranged flip (<c>scaleX = −1</c>, <c>rotation + 180</c>) lands on
        /// the same pixels as the oracle's <c>scaleY = −1</c> at the unmodified rotation. That identity
        /// is why <see cref="WeaponPresenter"/> needs no second rule for the common case — and it is
        /// asserted in <c>WeaponVisMathTests</c> so a future "fix" of one rule cannot silently break
        /// the other.</para>
        /// </summary>
        public static float MeleeVisScaleY(bool facingLeft) => facingLeft ? -1f : 1f;

        /// <summary>
        /// AS3 <c>WClub.animate</c>, <c>krep &gt; 0</c> branch (<c>WClub.as:753-758</c>):
        /// <c>vis.rotation = 90*storona - 90 + owner.weaponR*storona</c>.
        ///
        /// <para><b>This branch is why a "stable" unit (<c>&lt;comb krep='1'&gt;</c>, the port's
        /// <c>UnitDefinition.isStable</c>) does not visually swing.</b> The weapon is rigidly mounted:
        /// the rotation is a constant 0° facing right / −180° facing left (the port has no
        /// <c>weaponR</c> — it lives in the unported <c>wPos</c> bone table), regardless of
        /// <c>t_attack</c>. A <c>krep == 0</c> unit takes the other branch and sweeps the
        /// <c>anim</c>-driven arc instead.</para>
        /// </summary>
        /// <param name="weaponRDeg">AS3 <c>owner.weaponR</c> in degrees — 0 until the <c>wPos</c>
        /// table is ported.</param>
        public static float MeleeFixedVisRotationDeg(bool facingLeft, float weaponRDeg)
        {
            float storona = VisScaleX(facingLeft);
            return 90f * storona - 90f + weaponRDeg * storona;
        }

        /// <summary>
        /// AS3 <c>vis.scaleX</c> for a melee weapon, converted to the vis's <b>local</b> scale under a
        /// parent whose horizontal mirror is <paramref name="parentScaleSign"/>.
        ///
        /// <para>Melee is the case where the local X is the parent's sign and <i>not</i> the parent's
        /// sign times the oracle's — because the oracle's own <c>scaleX</c> is 1 (the mirror is on Y).
        /// See <see cref="MeleeVisLocalScaleY"/> and the class note on the fold.</para>
        /// </summary>
        public static float MeleeVisLocalScaleX(float parentScaleSign) => parentScaleSign;

        /// <summary>
        /// AS3 melee <c>vis.scaleY</c> converted to local. <b>Deliberately not multiplied by the
        /// parent's sign</b>: the world Y scale is <c>parentY · localY</c>, and a parent that mirrors on
        /// X does not touch Y, so the local Y is the oracle's value unchanged. Writing
        /// <c>parentSign · scaleY</c> here (copying the ranged X rule) would double the mirror on every
        /// turn-around — the melee twin of the pivot bug the ranged rule's doc records.</para>
        /// </summary>
        public static float MeleeVisLocalScaleY(bool facingLeft) => MeleeVisScaleY(facingLeft);

        /// <summary>
        /// AS3 melee <c>vis.rotation</c> (degrees) converted to local under a mirrored parent — the same
        /// <c>parentSign · φ</c> fold the ranged rule uses, because a mirror reverses the direction a
        /// rotation is applied in whatever the child's own scale is.
        /// </summary>
        public static float MeleeVisLocalRotationDeg(float oracleDeg, float parentScaleSign)
            => parentScaleSign * oracleDeg;

        /// <summary>
        /// Folds one link of a parent chain into the accumulated mirror sign: a negative local
        /// horizontal scale flips it, anything else leaves it.
        ///
        /// <para>This is the whole of the mirror arithmetic, and the only thing
        /// <see cref="ParentScaleSign"/> does per link — so it is what the tests pin. Deliberately free
        /// of engine types: a <see cref="Transform"/> cannot be constructed outside the editor, and a
        /// guard whose arrange step cannot run has no way to go red (the same trap
        /// <c>ActiveEffectSetTests</c> documents for <c>ScriptableObject</c>).</para>
        /// </summary>
        public static float ChainStep(float sign, float localScaleX) => localScaleX < 0f ? -sign : sign;

        /// <summary>
        /// The mirror sign of a whole chain of local horizontal scales — <c>-1</c> when an odd number
        /// of links mirror, <c>1</c> otherwise. Order does not matter; only the product's sign does.
        ///
        /// <para>Engine-free, so the offline harness executes the same fold
        /// <see cref="ParentScaleSign"/> runs over a live transform chain.</para>
        /// </summary>
        public static float ChainScaleSign(IEnumerable<float> localScaleXs)
        {
            float sign = 1f;
            foreach (float sx in localScaleXs)
                sign = ChainStep(sign, sx);
            return sign;
        }

        /// <summary>
        /// The sign of the mirror accumulated over an ancestor's chain of local horizontal scales —
        /// <c>-1</c> when it mirrors, <c>1</c> otherwise. A parentless transform counts as unmirrored.
        ///
        /// <para>Walks <see cref="Transform.parent"/> folding each link with <see cref="ChainStep"/>, so
        /// a mirror applied <i>anywhere</i> up the chain is seen — the shipped rig carries it on the
        /// unit root, one level above the weapon node.</para>
        ///
        /// <para><b>Why <c>localScale</c> and not <c>lossyScale</c>.</b> <c>lossyScale</c> also folds in
        /// ancestor rotations and non-uniform scales, which this conversion already assumes away (see
        /// the class note: the parent rotation α is taken as zero). Reading the local scales keeps the
        /// walk to the single operation the class actually reasons about — an odd number of horizontal
        /// mirrors — rather than borrowing a value that would move for reasons that mirror nothing.</para>
        /// </summary>
        public static float ParentScaleSign(Transform parent)
        {
            float sign = 1f;
            for (Transform t = parent; t != null; t = t.parent)
                sign = ChainStep(sign, t.localScale.x);
            return sign;
        }

        /// <summary>
        /// AS3 <c>vis.scaleX</c> converted to the vis's <b>local</b> scale given the parent's mirror:
        /// <c>parentSign · VisScaleX(facingLeft)</c>.
        ///
        /// <para>The world scale is the product, so a mirrored parent already contributes the
        /// <c>-1</c> the oracle asks for; writing another one on the child would cancel it. Facing
        /// left under a mirrored parent therefore yields <c>+1</c> locally — the mirror the player
        /// sees comes from the root, exactly as the character's own sprite is mirrored.</para>
        /// </summary>
        public static float VisLocalScaleX(bool facingLeft, float parentScaleSign)
            => parentScaleSign * VisScaleX(facingLeft);

        /// <summary>
        /// AS3 <c>vis.rotation</c> converted to the vis's <b>local</b> rotation given the parent's
        /// mirror: <c>parentSign · VisRotationDeg(…)</c>.
        ///
        /// <para>Mirroring reverses the direction a rotation is applied in — a parent with
        /// <c>scaleX = -1</c> renders a child's local rotation of <c>φ</c> as <c>-φ</c> — so the
        /// sign has to be flipped to land on the angle the oracle means.</para>
        /// </summary>
        public static float VisLocalRotationDeg(float rotRad, float rotUpDeg, bool facingLeft, float parentScaleSign)
            => parentScaleSign * VisRotationDeg(rotRad, rotUpDeg, facingLeft);
    }
}
