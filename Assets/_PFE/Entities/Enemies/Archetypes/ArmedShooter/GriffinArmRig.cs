using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Draws the griffin's separate aiming arm and publishes the point its <c>emit</c> bone puts the
    /// weapon at — the port of AS3 <c>UnitMerc</c>'s <c>arm</c> field and its overrides
    /// (<c>fe/unit/UnitMerc.as:29</c>, <c>:77-109</c>, <c>:158-231</c>).
    ///
    /// <para><b>What the oracle does, and what each line here corresponds to.</b></para>
    /// <list type="bullet">
    /// <item><description><c>arm = Res.getVis("visualGrifArm" + tr, …)</c> (<c>:29</c>) — one of five
    /// arm symbols, chosen by the same variant number as the body sheet. → <see cref="Attach"/>.</description></item>
    /// <item><description><c>arm.x/arm.y = X/Y + wPos[anim][frame] + visBmp</c> (<c>:229-230</c>) — the
    /// shoulder follows the animation. → <see cref="GriffinArmMath.ArmOffsetPixels"/>.</description></item>
    /// <item><description><c>arm.rotation = currentWeapon.rot*180/π + 90*(1-storona)</c>,
    /// <c>arm.scaleX = storona</c> (<c>:89-91</c>) — the arm turns with the gun. →
    /// <see cref="WeaponVisMath.VisLocalRotationDeg"/> / <see cref="WeaponVisMath.VisLocalScaleX"/>,
    /// which are that rule already converted for the port's mirrored root.</description></item>
    /// <item><description><c>setWeaponPos</c> → <c>arm.localToGlobal(emit)</c> (<c>:104-108</c>) — the
    /// weapon is drawn on the hand, not on the body. → <see cref="TryResolveHoldPoint"/>.</description></item>
    /// <item><description><c>arm.visible = false</c> on death and on loot (<c>:114</c>,
    /// <c>:164-166</c>) — a corpse has no arm. → <see cref="SetVisible"/>.</description></item>
    /// <item><description><c>arm.transform.colorTransform = cTransform</c> (<c>:56</c>) — the arm takes
    /// the same tint as the body. → <see cref="Pose"/>'s tint copy.</description></item>
    /// </list>
    ///
    /// <para><b>The hierarchy is AS3's, and it is load-bearing.</b> The oracle's <c>arm</c> clip has its
    /// art offset <i>inside</i> it and rotates about the clip's own registration point. So the port has
    /// two nodes: <c>Arm</c>, which carries the position, rotation and mirror, and <c>Art</c>, a child
    /// that holds the <see cref="SpriteRenderer"/> at the offset which puts the PNG's registration point
    /// on the parent's origin. Putting the offset on the rotating node instead would rotate the offset
    /// too, and the arm would swing around a point outside itself.</para>
    ///
    /// <para><b>It sits under the unit root, not under the weapon mount.</b> AS3's arm is a sibling of
    /// the unit's <c>vis</c> on an unscaled layer; the port's nearest equivalent is a child of the unit
    /// root, which is the node that carries <c>localScale.x = facing</c>. That is what makes the
    /// horizontal mirror come from the parent — and therefore what makes
    /// <see cref="WeaponVisMath.VisLocalScaleX"/>'s parent-sign fold the correct conversion rather than
    /// a double mirror.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GriffinArmRig : MonoBehaviour, IWeaponHoldPointSource
    {
        /// <summary>AS3's <c>arm</c> clip — carries position, rotation and mirror; rotates the art about the shoulder.</summary>
        private Transform _arm;

        /// <summary>AS3's <c>arm</c> clip's own children — the art, offset so the registration point is the pivot.</summary>
        private Transform _art;

        private SpriteRenderer _renderer;

        private UnitController  _owner;
        private UnitAnimator    _animator;
        private GriffinArmMath.GriffinArmVariant _variant;

        /// <summary>
        /// The weapon this arm is holding, or null while the unit is unarmed. Supplied by
        /// <c>EnemyWeaponMount</c> once it has built a controller: the arm reads the weapon's own
        /// <c>rot</c> off it, exactly as <c>UnitMerc.setVisPos</c> reads <c>currentWeapon.rot</c>.
        /// </summary>
        private WeaponRuntimeState _weaponState;

        /// <summary>AS3 <c>arm.visible</c>. Cleared on death and when the loot drops (<c>UnitMerc.as:114</c>, <c>:164-166</c>).</summary>
        private bool _visible = true;

        /// <summary>True once <see cref="Attach"/> found everything it needs to draw.</summary>
        public bool IsReady => _arm != null && _art != null && _renderer != null;

        /// <summary>The variant index this rig was built for, 0-based — AS3 <c>tr - 1</c>.</summary>
        public int VariantIndex { get; private set; } = -1;

        /// <summary>
        /// Build the rig on a griffin and return it, or null when the unit has no arm art.
        ///
        /// <para><b>A null return is the honest answer, not an error.</b> The five arm symbols are a
        /// separate import pass from the body sheets, so a project that has not run it has bodies and
        /// no arms. Falling back to the body hold point is what every non-merc unit does anyway, so the
        /// griffin still shoots — it just holds the gun at its chest, which is the state this rig was
        /// written to end.</para>
        /// </summary>
        /// <param name="owner">The unit. The arm node becomes its child, beside the body's <c>Visual</c>.</param>
        /// <param name="armSprite">The arm frame, or null when the import has not run.</param>
        /// <param name="variantIndex">0-based AS3 <c>tr</c>, i.e. <c>sprGriffonN</c> ⇒ <c>N - 1</c>.</param>
        public static GriffinArmRig Attach(UnitController owner, Sprite armSprite, int variantIndex)
        {
            if (owner == null || armSprite == null)
            {
                return null;
            }

            if (variantIndex < 0 || variantIndex >= GriffinArmMath.Variants.Length)
            {
                Debug.LogWarning(
                    $"[GriffinArmRig] variant {variantIndex} is outside the five arm symbols; " +
                    $"'{owner.name}' keeps the body hold point.");
                return null;
            }

            var armGo = new GameObject("Arm");
            armGo.transform.SetParent(owner.transform, false);

            var artGo = new GameObject("Art");
            artGo.transform.SetParent(armGo.transform, false);

            var renderer = artGo.AddComponent<SpriteRenderer>();

            var rig = armGo.AddComponent<GriffinArmRig>();
            rig._arm        = armGo.transform;
            rig._art        = artGo.transform;
            rig._renderer   = renderer;
            rig._owner      = owner;
            rig._variant    = GriffinArmMath.Variants[variantIndex];
            rig.VariantIndex = variantIndex;

            rig.BuildArt(armSprite);
            rig.CopyBodyPresentation();

            return rig;
        }

        /// <summary>
        /// Place the arm's art inside its node so the PNG's <b>registration point</b> — the shoulder —
        /// lands on the node's origin, and give it the arm sprite.
        /// </summary>
        private void BuildArt(Sprite armSprite)
        {
            _renderer.sprite = armSprite;

            Vector2 size  = armSprite.rect.size;
            Vector2 pivot = armSprite.pivot;   // pixels from the sprite's BOTTOM-left
            Vector2 offsetPx = GriffinArmMath.ArmArtOffsetPixels(pivot, size, _variant.OriginPixels);
            _art.localPosition = WorldCoordinates.PixelToUnity(offsetPx);
        }

        /// <summary>
        /// Match the body's sorting layer, sorting order and tint — AS3 draws the arm on the same layer
        /// as <c>vis</c> and colour-transforms it with the body's own matrix
        /// (<c>UnitMerc.as:52-56</c>).
        ///
        /// <para>Copied once at build rather than every frame: the sorting order is derived from the
        /// spawn position and does not change while the unit lives. The <b>tint</b> is the part that can
        /// change, so <see cref="Pose"/> re-reads it — a frozen or burning griffin must not have a
        /// normally-coloured arm.</para>
        /// </summary>
        private void CopyBodyPresentation()
        {
            SpriteRenderer body = BodyRenderer;
            if (body == null)
            {
                return;
            }

            _renderer.sortingLayerName = body.sortingLayerName;
            _renderer.sortingOrder     = body.sortingOrder;
        }

        /// <summary>
        /// Bind the weapon whose <c>rot</c> the arm mirrors. Called by <c>EnemyWeaponMount</c> after it
        /// has built a controller — before that there is nothing to aim and the arm holds its rest pose.
        /// </summary>
        public void BindWeapon(WeaponRuntimeState weaponState)
        {
            _weaponState = weaponState;
            Pose();
        }

        /// <summary>
        /// Show or hide the arm. AS3 hides it when the unit dies (<c>UnitMerc.as:164-166</c>) and when
        /// the loot drops (<c>:114</c>) — the death animation is drawn without the limb, so leaving the
        /// arm on leaves a gun-holding hand floating over a corpse.
        /// </summary>
        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_renderer != null)
            {
                _renderer.enabled = visible;
            }
        }

        // ── IWeaponHoldPointSource ────────────────────────────────────────────

        /// <summary>
        /// The aim point is <b>not</b> what mirrors the arm — AS3 uses the unit's <c>storona</c> for
        /// that (<c>UnitMerc.as:89-91</c>), while the weapon's own <c>vis</c> mirrors on the aim side.
        /// So this only records the point and poses; the facing decision reads
        /// <see cref="UnitController.FacingDirection"/>.
        ///
        /// <para>It is still called before the hold point is asked for, because the pose depends on
        /// the weapon's aim <i>through the controller's <c>rot</c></i>, and re-posing here rather than
        /// in <c>LateUpdate</c> is what keeps the arm and the hold point in the same frame.</para>
        /// </summary>
        public void SetAimWorldPoint(Vector2 worldPoint) => Pose();

        /// <summary>
        /// AS3 <c>UnitMerc.setWeaponPos</c> — the weapon hangs on the arm's <c>emit</c> marker
        /// (<c>UnitMerc.as:104-108</c>), which is the hand. Falls through to <c>false</c> when there is
        /// no arm or no animation data, so the caller keeps its own body hold point.
        /// </summary>
        public bool TryResolveHoldPoint(out Vector2 worldPoint)
        {
            worldPoint = default;
            if (!Pose())
            {
                return false;
            }

            Vector2 emitPx = GriffinArmMath.EmitLocalPixels(_variant.EmitPixels);
            worldPoint = _arm.TransformPoint(WorldCoordinates.PixelToUnity(emitPx));
            return true;
        }

        // ── Posing ───────────────────────────────────────────────────────────

        private void LateUpdate() => Pose();

        /// <summary>True once a pose has landed, so a transient bad cell does not blank the arm.</summary>
        private bool _hasEverPosed;

        /// <summary>
        /// The body's animator, resolved on first use rather than at <see cref="Attach"/> time — the
        /// same lazy lookup <c>EnemyBrain</c> uses, and for the same reason: a rig built by an
        /// archetype controller must not depend on the body's <c>Visual</c> child already existing.
        /// </summary>
        private UnitAnimator Animator
        {
            get
            {
                if (_animator == null && _owner != null)
                {
                    _animator = _owner.GetComponentInChildren<UnitAnimator>(true);
                }

                return _animator;
            }
        }

        /// <summary>
        /// The unit's definition, read through the owner on every use rather than captured at
        /// <see cref="Attach"/> time.
        ///
        /// <para>The rig is built from <c>MercController.Initialize</c> right after
        /// <c>base.Initialize</c>, and <c>RoomUnitSpawner</c> can call that a second time with a
        /// placement-adjusted definition — a captured copy would then describe a sheet the unit is no
        /// longer drawn from, and the arm would slide to a cell size that no longer matches its body.</para>
        /// </summary>
        private UnitDefinition Definition => _owner != null ? _owner.Stats : null;

        /// <summary>The renderer the body is drawn on — the arm copies its layer, order and tint.</summary>
        private SpriteRenderer BodyRenderer => Animator != null ? Animator.Renderer : null;

        /// <summary>
        /// Recompute the arm's transform for the current animation cell, facing and weapon aim.
        /// Idempotent and cheap, so it can be called from both the sim tick and <c>LateUpdate</c>
        /// without an ordering contract between them.
        /// </summary>
        /// <returns>False when the arm cannot be posed yet (no art, hidden, or no animation cell).</returns>
        private bool Pose()
        {
            if (!IsReady || !_visible)
            {
                return false;
            }

            UnitDefinition definition = Definition;
            UnitAnimator animator = Animator;
            if (animator == null || definition == null)
            {
                return false;
            }

            int columns = definition.spriteSheetColumns;
            if (!GriffinArmMath.TryResolveWPos(animator.CurrentCellIndex, columns, out Vector2Int wPos))
            {
                // AS3 would throw here (`_loc3_.x` on `undefined`); the port leaves the arm where the
                // last resolvable cell put it rather than snapping it to the unit's origin, which is a
                // visible jump across the body.
                return _hasEverPosed;
            }

            Vector2 offsetPx = GriffinArmMath.ArmOffsetPixels(
                wPos, definition.registrationPoint, definition.spriteDimensions);
            _arm.localPosition = WorldCoordinates.PixelToUnity(offsetPx);

            bool facingLeft = _owner != null && _owner.FacingDirection < 0;
            float rotRad = _weaponState != null ? _weaponState.Rot : 0f;

            // The same two helpers the held weapon's vis uses. `UnitMerc.as:89-91` is
            // `rotation = rot*180/π + 90*(1-storona)` and `scaleX = storona`, which is exactly
            // `WeaponVisMath.VisRotationDeg(rot, 0, facingLeft)` and `VisScaleX(facingLeft)` — so the
            // parent-sign fold is applied once, by the shared code, for both.
            float parentSign = WeaponVisMath.ParentScaleSign(_arm.parent);
            _arm.localRotation = Quaternion.Euler(0f, 0f,
                WeaponVisMath.VisLocalRotationDeg(rotRad, 0f, facingLeft, parentSign));
            _arm.localScale = new Vector3(
                WeaponVisMath.VisLocalScaleX(facingLeft, parentSign), 1f, 1f);

            SyncBodyTint();
            _hasEverPosed = true;
            return true;
        }

        /// <summary>
        /// Copy the body's colour onto the arm every pose. AS3 does it once, in <c>addVisual</c>
        /// (<c>UnitMerc.as:54-57</c>), because the oracle's tint is written when the visual is added; the
        /// port also tints for damage flashes, freezing and burning, so re-reading is what keeps the
        /// limb from staying normally coloured on a frozen griffin.
        /// </summary>
        private void SyncBodyTint()
        {
            SpriteRenderer body = BodyRenderer;
            if (body == null || _renderer.color == body.color)
            {
                return;
            }

            _renderer.color = body.color;
        }
    }
}
