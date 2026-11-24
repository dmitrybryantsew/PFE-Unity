using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// The visual half of a spawned unit — the <c>"Visual"</c> child <see cref="RoomUnitSpawner"/>
    /// used to build inline, extracted into one reconfigurable component so
    /// <see cref="UnitViewPool"/> can recycle it across room transitions instead of destroying and
    /// rebuilding a GameObject per unit per spawn.
    ///
    /// <para><b>What it owns.</b> Exactly what the inline build owned: the <see cref="SpriteRenderer"/>
    /// (resting frame, anchor, presenter material, sorting), the <see cref="UnitAnimator"/> that takes
    /// over from the resting frame, and the optional decorations a unit type adds on top
    /// (<c>TurretPartPresenter</c> parts, a <c>UnitShieldOverlay</c> dome, an NPC's
    /// <c>NpcVisualAnimator</c>). The controller still reaches all of it the way it always has —
    /// <c>SetVisualRenderer</c> and <c>GetComponentInChildren</c> — because the view stays a child of
    /// the unit root in both the pooled and the inline path.</para>
    ///
    /// <para><b>What it does not own: the pose.</b> The unit root's transform is still the simulation
    /// truth (the motor and the motor-less step write it, and <c>UnitRecordSyncSystem</c> stamps the
    /// <c>SimUnitState</c> record FROM it), so the view is parent-posed exactly as before. Reading the
    /// pose back out of <c>UnitWorld</c> is the later half of the UnitViewPool work — it becomes
    /// possible only when the sim writes the record first and the controllers' child-hierarchy visual
    /// lookups (<c>EnemyBrain</c>, <c>ClimberBrain</c>'s <c>Find("Visual")</c>, the boss rigs) have a
    /// seam that does not depend on parenting. Until then a sibling view would silently de-animate
    /// every brain that searches its own hierarchy.</para>
    ///
    /// <para><b>Reuse contract.</b> <see cref="Configure"/> is a full reset, not an overlay: a pooled
    /// view arrives carrying the previous occupant's sprite, anchor offsets, fade colour, sorting and
    /// animation state, and every one of them is rewritten before the new unit's art is applied. What
    /// Configure cannot reset — components and children other systems added (the shield dome, turret
    /// parts, the NPC animator) — is what <see cref="UnitViewPool"/>'s recyclability check looks for;
    /// such a view is destroyed rather than reused.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitView : MonoBehaviour
    {
        SpriteRenderer _renderer;
        UnitAnimator _animator;

        /// <summary>The body renderer — the same reference the spawner hands to
        /// <c>UnitController.SetVisualRenderer</c>. Never null after <see cref="Configure"/>.</summary>
        public SpriteRenderer BodyRenderer
        {
            get
            {
                EnsureComponents();
                return _renderer;
            }
        }

        /// <summary>The body animator, re-initialised by every <see cref="Configure"/>.</summary>
        public UnitAnimator Animator
        {
            get
            {
                EnsureComponents();
                return _animator;
            }
        }

        /// <summary>
        /// Whether the last <see cref="Configure"/> found no resting sprite on the definition — the
        /// condition <see cref="RoomUnitSpawner"/>'s warn-once reports. False on the villager-variant
        /// path, which resolves its own art and never warned, exactly as the inline build behaved.
        /// </summary>
        public bool RestingSpriteMissing { get; private set; }

        void Awake()
        {
            EnsureComponents();
        }

        /// <summary>
        /// (Re)build this view for a unit. Idempotent across pool reuse: every piece of per-unit
        /// state is reset first, then the new unit's art is applied in the same order the inline
        /// build used — sprite and anchor first, animator second, so a unit with no animation keeps
        /// its resting frame and one with animation does not flicker through an empty first frame.
        /// </summary>
        /// <param name="definition">The unit's definition; null is legal (a record with no row) and
        /// leaves the renderer disabled, as the inline build did.</param>
        /// <param name="unit">The placement record — the villager variant attribute and the turret
        /// unit id are read from it.</param>
        /// <param name="sortingOrder">The depth order, computed by the caller from the spawn row
        /// (lower on screen draws in front), same convention as props.</param>
        public void Configure(UnitDefinition definition, UnitInstance unit, int sortingOrder)
        {
            EnsureComponents();

            // The reset half of the reuse contract. A pooled view arrives with the previous
            // occupant's anchor offsets (localPosition/localScale), its corpse-dissolve fade
            // (color), its sprite and its sorting; a fresh inline view simply has defaults, so
            // resetting unconditionally is what makes the two paths indistinguishable.
            transform.localPosition = Vector3.zero;
            transform.localScale = Vector3.one;
            _renderer.color = Color.white;
            _renderer.flipX = false;
            _renderer.flipY = false;
            _renderer.sprite = null;
            _renderer.sharedMaterial = null;
            _renderer.sortingLayerName = MapSortingLayers.BackgroundPhysicalObjects;
            _renderer.sortingOrder = sortingOrder;

            RestingSpriteMissing = ApplySprite(_renderer, unit, definition);

            // The reader the importers were writing animation data for. Re-initialised on every
            // configure: UnitAnimator.Initialize resets its state, sheet override and visibility
            // flag itself, so a recycled view cannot inherit the previous unit's clip.
            _animator.Initialize(definition, _renderer);
        }

        void EnsureComponents()
        {
            if (_renderer == null)
            {
                _renderer = GetComponent<SpriteRenderer>();
            }

            if (_renderer == null)
            {
                _renderer = gameObject.AddComponent<SpriteRenderer>();
            }

            if (_animator == null)
            {
                _animator = GetComponent<UnitAnimator>();
            }

            if (_animator == null)
            {
                _animator = gameObject.AddComponent<UnitAnimator>();
            }
        }

        /// <summary>
        /// Draw the unit's sprite, or leave the renderer disabled when there is none — the body of
        /// the spawner's old <c>ApplySprite</c>, moved here so the pooled and inline paths share one
        /// construction. See <see cref="RoomUnitSpawner"/> for the importer workflow the missing-art
        /// warning points at; the warning itself stays with the spawner, which owns the warn-once
        /// sets.
        /// </summary>
        /// <returns>True when the resting sprite was missing (and the placement was not a villager
        /// variant, which resolves its own art).</returns>
        bool ApplySprite(SpriteRenderer renderer, UnitInstance unit, UnitDefinition definition)
        {
            Sprite sprite = definition != null ? definition.sprite : null;

            // A villager pony is not one sprite: `ponpon`'s `<vis>` names a symbol whose own timeline
            // is one frame over `pon`, and `pon` holds 27 different ponies selected by the placement's
            // `tr` (UnitPonPon.as:112). Falls through to the single sprite when the variant art
            // has not been imported, so an un-run import degrades to the old picture, not to nothing.
            if (PonPonVariants.TryResolve(definition, unit.GetAttribute(PonPonVariants.Attribute),
                                          out Sprite variantSprite, out Vector2 variantScale,
                                          out float variantFeetGap))
            {
                // The anchor comes from the art: a DisplayObject's origin is where the artist put it
                // (8.54 px above the frame's bottom for these frames), not the bitmap rule's flat 10.
                UnitSpriteAnchor.ApplyWithFeetGap(renderer, variantSprite, variantFeetGap, variantScale);
                renderer.enabled = variantSprite != null;
                ApplySpriteMaterial(renderer);
                return false;
            }

            // The registration point comes from the ART, not only from the definition — see
            // UnitSpriteAnchor.ApplyResolved for the DisplayObject units whose origin is neither the
            // frame's centre nor a 10 px feet gap (the turrets, `bossnecr`, `bloat*`, `dron*` …).
            UnitSpriteAnchor.ApplyResolved(renderer, sprite, definition, unit.unitId);
            renderer.enabled = sprite != null;

            // A turret is not one sprite: UnitTurret animates a gun and a lamp inside the mount's own
            // MovieClip (UnitTurret.as:298-313), and the importer flattened that tree into one baked
            // frame. Attach rebuilds the two missing pieces from the baked parts and, when they exist,
            // swaps the body for the gun-free version of the same canvas. Returns null for every unit
            // that is not one of the six mounts, so this is a no-op for the other ~290.
            TurretPartPresenter.Attach(renderer, unit.unitId, renderer.sortingOrder, renderer.sortingLayerName);

            ApplySpriteMaterial(renderer);
            return sprite == null;
        }

        /// <summary>
        /// The shared presenter material, which every unit sprite needs — a renderer left on Unity's
        /// default sprite material is drawn by the built-in sprite shader and ignores the room's light
        /// and fog passes. Applied on both exits of <see cref="ApplySprite"/> for the same reason the
        /// inline build did: a material applied at only one exit is how half the ponies in a room end
        /// up lit differently from the other half.
        /// </summary>
        static void ApplySpriteMaterial(SpriteRenderer renderer)
        {
            Material material = SpritePresenterMaterial.Get();
            if (material != null)
            {
                renderer.sharedMaterial = material;
            }
        }
    }
}
