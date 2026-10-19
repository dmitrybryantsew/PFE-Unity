using PFE.Entities.Enemies;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Draws a turret mount as the three pieces the oracle draws — body, barrel, state lamp — instead
    /// of the single baked composite the sprite importer produced.
    ///
    /// <para><b>The defect this fixes.</b> <c>UnitTurret</c>'s art is a MovieClip tree
    /// (<c>UnitTurret.as:97-119</c>) and <c>animate()</c> drives two of its three children every
    /// frame:</para>
    ///
    /// <code>
    /// vis.osn.puha.rotation = currentWeapon.rot * 180 / Math.PI;   // :309  the barrel tracks the aim
    /// vis.osn.puha.gotoAndStop(this.tr);                           // :298  the barrel art follows tr
    /// vis.osn.light.gotoAndStop(aiState + 1);                      // :310  the lamp shows the state
    /// </code>
    ///
    /// <para><c>UnitSpriteImporter</c> copies the <i>outer</i> symbol's frames, and that symbol has
    /// exactly one, so the port drew a frozen composite: a turret that tracked you with its cone and
    /// fired at you while its barrel stayed pointed at the wall, and no lamp at all. The art is now
    /// baked into parts by <c>docs/Diagnostics/scripts/bake_turret_parts.py</c> and this class puts
    /// them back together with the oracle's own transforms.</para>
    ///
    /// <para><b>Three renderers, three transforms, and the reasons they are separate.</b></para>
    /// <list type="bullet">
    /// <item><b>Body</b> — the mount plate and neck, on the same canvas and the same registration
    /// point as the old composite, so <see cref="UnitSpriteAnchor"/> and
    /// <see cref="TurretSpriteOrigin"/> place it unchanged.</item>
    /// <item><b>Gun</b> — the barrel, under a pivot node at the gun's registration point so
    /// <c>rotation</c> sweeps it about the trunnion. AS3 rotates the symbol about its own origin; a
    /// centre pivot would swing the barrel clean out of its mount.</item>
    /// <item><b>Lamp</b> — the state light, drawn <i>above</i> the gun because the oracle places it at
    /// the higher depth (<c>puha</c> depth 3, <c>light</c> depth 5) and a lamp under the barrel would
    /// be hidden by it.</item>
    /// </list>
    ///
    /// <para><b>The mirror the mount does not have.</b> <c>UnitTurret</c> never writes
    /// <c>vis.scaleX</c>: a mount faces its half-plane by changing its aim cone and its rotation, not by
    /// flipping its art. The port's unit root <i>does</i> flip (<c>UnitController.ApplyFacingToTransform</c>
    /// writes <c>localScale.x = _facingDirection</c>), and <c>TrapBrain</c> turns a turret to face its
    /// target on every engagement — so without a cancellation the whole mount mirrors every time the
    /// player walks past it, and a mirrored barrel drawn at an aim angle points the wrong way. Both
    /// nodes below therefore carry the parent's sign as their own scale.</para>
    ///
    /// <para><b>Why the parts are not children of the body renderer.</b> The body renderer is not at
    /// the mount's origin: <see cref="UnitSpriteAnchor.ApplyTo"/> puts it at the <i>anchor offset</i>
    /// (29 px right and 19 px up for <c>turret0</c>) so that the sprite's registration pixel lands on
    /// the unit origin. Parenting the gun to it would inherit that offset, and the barrel would be
    /// drawn 29 px right and 19 px above the trunnion it is supposed to turn about — while the rotation
    /// still looked right, so it reads as "the turret is misplaced" rather than as "the gun is
    /// detached". The parts therefore hang off their own root, a sibling of the body renderer at the
    /// unit origin.</para>
    ///
    /// <code>
    /// unit root            localScale.x = facing        (UnitController)
    ///   Visual             the body renderer + UnitAnimator + this presenter
    ///     turretParts      parts root, localScale.x = sign   ← the cancellation
    ///       gun  → gunArt  pivot at the placement, art corrected to the registration pixel
    ///       lamp → lampArt
    /// </code>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TurretPartPresenter : MonoBehaviour
    {
        SpriteRenderer _body;
        SpriteRenderer _gun;
        SpriteRenderer _lamp;
        Transform _partsRoot;
        Transform _gunPivot;
        Transform _lampPivot;
        Sprite[] _gunFrames;
        Sprite[] _lampFrames;
        Sprite _bodySprite;
        Vector2Int _bodyOrigin;

        WeaponRuntimeState _weapon;
        EnemyBrain _brain;
        int _tr = 1;

        int _drawnGunFrame = -1;
        int _drawnLampFrame = -1;
        float _drawnRotation = float.NaN;

        /// <summary>Session-wide, because a room can hold a dozen mounts and the warning is the same
        /// sentence for all of them.</summary>
        static bool _reportedMissingArt;

        /// <summary>True when the barrel art was found — i.e. the bake has been run.</summary>
        public bool HasParts => _gunFrames != null && _gunFrames.Length > 0;

        /// <summary>The node the barrel turns about. Null until the parts exist; exposed so a test can
        /// assert <i>where</i> it hangs rather than only what angle it holds.</summary>
        public Transform GunPivot => _gunPivot;

        /// <inheritdoc cref="GunPivot"/>
        public Transform LampPivot => _lampPivot;

        /// <summary>
        /// Build the two extra renderers under <paramref name="body"/>'s node and bind them to the
        /// mount's art. Returns null for a unit that is not one of the six mounts.
        ///
        /// <para>Called from the spawner's sprite pass, so it runs before the controller exists; the
        /// weapon state and the AI state are bound later by <see cref="Bind"/>, which is why this can
        /// be a pure art step.</para>
        /// </summary>
        public static TurretPartPresenter Attach(SpriteRenderer body, string unitId, int sortingOrder,
                                                 string sortingLayer)
        {
            if (body == null || !TurretParts.TryGetPlacements(unitId, out Vector2 gunPlace, out Vector2 lampPlace))
            {
                return null;
            }

            TurretPartPresenter presenter = body.GetComponent<TurretPartPresenter>();
            if (presenter == null)
            {
                presenter = body.gameObject.AddComponent<TurretPartPresenter>();
            }

            presenter._body = body;
            presenter._bodyOrigin = TurretSpriteOrigin.Resolve(unitId, new Vector2Int(-1, -1));
            presenter._bodySprite = Resources.Load<Sprite>(TurretParts.BodyResourcePath(unitId));
            presenter._gunFrames = LoadFrames(TurretParts.GunFrameCount, TurretParts.GunResourcePath);
            presenter._lampFrames = LoadFrames(TurretParts.LampFrameCount, TurretParts.LampResourcePath);

            if (presenter._bodySprite != null)
            {
                // The bake writes a body on the composite's own canvas, so the anchor is unchanged; a
                // missing body leaves the imported composite in place, which draws the mount correctly
                // in its resting pose -- just with the barrel baked into it.
                UnitSpriteAnchor.ApplyTo(body, presenter._bodySprite, presenter._bodyOrigin);
            }

            presenter.BuildParts(gunPlace, lampPlace, sortingOrder, sortingLayer);
            return presenter;
        }

        static Sprite[] LoadFrames(int count, System.Func<int, string> path)
        {
            var frames = new Sprite[count];
            int found = 0;
            for (int i = 0; i < count; i++)
            {
                frames[i] = Resources.Load<Sprite>(path(i + 1));
                if (frames[i] != null) found++;
            }

            // All-or-nothing: a partial set would draw some `tr` values and leave others blank, which
            // reads as "the barrel disappears on the hard turrets" rather than as a missing bake.
            return found == count ? frames : System.Array.Empty<Sprite>();
        }

        void BuildParts(Vector2 gunPlace, Vector2 lampPlace, int sortingOrder, string sortingLayer)
        {
            if (!HasParts)
            {
                if (!_reportedMissingArt)
                {
                    _reportedMissingArt = true;
                    Debug.LogWarning(
                        $"[TurretPartPresenter] '{name}': no baked turret parts under Resources/" +
                        $"'{TurretParts.GunResourcePath(1)}'. The mount draws as the imported composite, " +
                        "so its barrel will not aim and its lamp will not light. Run " +
                        "docs/Diagnostics/scripts/bake_turret_parts.py to produce them.");
                }

                return;
            }

            // A sibling of this renderer, at the unit origin -- see the class note for why it cannot be
            // a child of it. `transform.parent` is the unit's `Visual` object, whose own parent carries
            // the facing scale; falling back to `transform` would put the parts back on the anchor
            // offset, so a detached renderer is left without parts rather than drawn in the wrong place.
            if (transform.parent == null) return;

            // Attach is idempotent on the component but this method is not: a second sprite pass would
            // build a second gun and a second lamp, and the stale pair would keep the old aim.
            if (_partsRoot != null) return;

            var root = new GameObject("turretParts");
            root.transform.SetParent(transform.parent, false);
            _partsRoot = root.transform;

            _gunPivot = BuildPart("gun", gunPlace, TurretParts.GunPivotPx, _gunFrames[0],
                                  sortingOrder + 1, sortingLayer, out _gun);

            // Frame 1 is the unlit pose and is an EMPTY frame in the symbol, so the lamp is invisible
            // until the first state that has art -- the oracle's behaviour, not a missing asset.
            _lampPivot = BuildPart("lamp", lampPlace, TurretParts.LampPivotPx, _lampFrames[0],
                                   sortingOrder + 2, sortingLayer, out _lamp);
            _lamp.enabled = _lampFrames[0] != null;
        }

        /// <summary>
        /// A pivot node at the part's placement, with the renderer on a child of it.
        ///
        /// <para><b>Parented to the parts root, not to this renderer.</b> The placement is measured
        /// from the mount's origin, and this renderer does not sit there — it sits at the anchor offset
        /// (<c>UnitSpriteAnchor.PixelOffset</c>, 29 px right and 19 px up for <c>turret0</c>) so the
        /// sprite's registration pixel lands on the origin. A child at <c>(0, 13.8)</c> of it would be
        /// drawn at <c>(29, 32.8)</c> from the trunnion.</para>
        ///
        /// <para><b>The renderer must not be the pivot.</b> The pivot node's origin is the point AS3
        /// rotates about; the renderer's local position is the correction that puts the sprite's own
        /// registration pixel on that point. Putting the renderer on the pivot node would make the two
        /// the same transform, so writing the correction would move the pivot instead of the art — the
        /// barrel would rotate about the right point but be drawn 33 px away from it.</para>
        /// </summary>
        Transform BuildPart(string partName, Vector2 flashPlacement, Vector2 pivotPx, Sprite sprite,
                            int sortingOrder, string sortingLayer, out SpriteRenderer renderer)
        {
            var pivot = new GameObject(partName);
            pivot.transform.SetParent(_partsRoot, false);
            pivot.transform.localPosition = WorldCoordinates.PixelToUnity(TurretParts.LocalPixels(flashPlacement));

            var art = new GameObject(partName + "Art");
            art.transform.SetParent(pivot.transform, false);

            renderer = art.AddComponent<SpriteRenderer>();
            renderer.sortingLayerName = sortingLayer;
            renderer.sortingOrder = sortingOrder;
            renderer.sprite = sprite;

            if (sprite != null)
            {
                art.transform.localPosition = WorldCoordinates.PixelToUnity(
                    TurretParts.PivotCorrection(sprite.pivot, sprite.rect.size, pivotPx));
            }

            Material material = SpritePresenterMaterial.Get();
            if (material != null) renderer.sharedMaterial = material;

            return pivot.transform;
        }

        /// <summary>
        /// Bind the live aim and AI state. Called by <see cref="TurretController"/> as soon as the
        /// mount's weapon exists — the art pass runs first, so the two are bound at different times by
        /// necessity rather than by preference.
        /// </summary>
        /// <param name="weapon">
        /// The mount's weapon state. Its <c>Rot</c> is the angle AS3 calls <c>currentWeapon.rot</c>, and
        /// it is what <c>RangedWeaponController</c> advances from the aim the brain publishes each tick.
        /// </param>
        /// <param name="brain">The brain whose <c>CurrentState</c> is the oracle's <c>aiState</c>.</param>
        /// <param name="tr">The weapon <c>tr</c>, which <c>gotoAndStop</c> uses as a frame number.</param>
        public void Bind(WeaponRuntimeState weapon, EnemyBrain brain, int tr)
        {
            _weapon = weapon;
            _brain = brain;
            _tr = tr;
        }

        void LateUpdate()
        {
            ApplyMountMirror();
            KeepBodySprite();

            // `HasParts` says the art was found; `_gunPivot` says the nodes were built. They differ
            // when the renderer had no parent, and driving a null pivot would throw every frame.
            if (_gunPivot == null) return;

            ApplyGun();
            ApplyLamp();
        }

        /// <summary>
        /// Hold the body at the baked art.
        ///
        /// <para><c>UnitAnimator</c> draws from the unit's <c>spriteSheet</c>, which for a mount is the
        /// composite — so a mount that ever gains an animation cell would put the barrel back on top of
        /// the barrel. The oracle's own body animation is the shutter
        /// (<c>vis.osn.gotoAndPlay(2)</c>, <c>UnitTurret.as:319-327</c>), which is a separate unported
        /// feature and has no art here, so there is nothing to play.</para>
        /// </summary>
        void KeepBodySprite()
        {
            if (_bodySprite != null && _body != null && _body.sprite != _bodySprite)
            {
                _body.sprite = _bodySprite;
            }
        }

        /// <summary>
        /// Cancel the unit root's facing mirror — see the class note. Also re-applies the anchor's x so
        /// the body does not slide when the two scales cancel.
        /// </summary>
        void ApplyMountMirror()
        {
            float sign = WeaponVisMath.ParentScaleSign(transform.parent);

            if (!Mathf.Approximately(transform.localScale.x, sign))
            {
                transform.localScale = new Vector3(sign, 1f, 1f);
            }

            // The parts cancel the same mirror, independently: they are siblings of this renderer, so
            // its scale does not reach them. Without this the barrel and lamp would flip with the unit
            // while the aim angle already encodes the side (RangedWeaponController's `|dx| * storona`),
            // and the barrel would point away from the target on one side.
            if (_partsRoot != null && !Mathf.Approximately(_partsRoot.localScale.x, sign))
            {
                _partsRoot.localScale = new Vector3(sign, 1f, 1f);
            }

            if (_body != null && _body.sprite != null)
            {
                // PixelOffset answers in room pixels already -- it is the anchor's own unit, not a
                // Unity distance, so it must not be scaled again on the way in.
                Vector2 anchor = UnitSpriteAnchor.PixelOffset(_body.sprite, _bodyOrigin);
                Vector3 desired = WorldCoordinates.PixelToUnity(new Vector2(sign * anchor.x, anchor.y));
                if (!Mathf.Approximately(transform.localPosition.x, desired.x) ||
                    !Mathf.Approximately(transform.localPosition.y, desired.y))
                {
                    transform.localPosition = new Vector3(desired.x, desired.y, transform.localPosition.z);
                }
            }
        }

        void ApplyGun()
        {
            float rotation = _weapon != null ? TurretParts.GunRotationDeg(_weapon.Rot) : 0f;
            int frame = TurretParts.GunFrame(_tr);

            // Written only on change: a rotation set every frame on a transform that does not need it
            // still dirties the transform hierarchy, and this runs for every turret in the room.
            if (!Mathf.Approximately(rotation, _drawnRotation))
            {
                _drawnRotation = rotation;
                _gunPivot.localRotation = Quaternion.Euler(0f, 0f, rotation);
            }

            if (frame != _drawnGunFrame)
            {
                _drawnGunFrame = frame;
                _gun.sprite = _gunFrames[frame - 1];
            }
        }

        void ApplyLamp()
        {
            int frame = TurretParts.LampFrame(_brain != null ? (int)_brain.CurrentState : 0);
            if (frame == _drawnLampFrame) return;

            _drawnLampFrame = frame;
            Sprite sprite = _lampFrames[frame - 1];
            _lamp.sprite = sprite;
            _lamp.enabled = sprite != null;
        }
    }
}
