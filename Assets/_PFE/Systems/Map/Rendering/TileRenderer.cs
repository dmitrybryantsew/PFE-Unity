using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// MonoBehaviour component for rendering a single tile.
    /// Attached to each tile GameObject in the scene.
    /// </summary>
    [DisallowMultipleComponent]
    public class TileRenderer : MonoBehaviour, ITileVisual
    {
        [Header("Tile Data")]
        [SerializeField] private TileData tileData = null;
        [SerializeField] private Vector2Int gridPosition = Vector2Int.zero;

        [Header("Rendering")]
        [SerializeField] private SpriteRenderer spriteRenderer = null;
        [SerializeField] private SpriteRenderer secondarySpriteRenderer = null; // For back layer
        [SerializeField] private SpriteRenderer rearOverlaySpriteRenderer = null;
        [SerializeField] private SpriteRenderer rearOverlaySpriteRenderer2 = null;
        [SerializeField] private SpriteRenderer frontOverlaySpriteRenderer = null;
        [SerializeField] private SpriteRenderer frontOverlaySpriteRenderer2 = null;

        [Header("Visual State")]
        [SerializeField] private bool isVisible = true;

        /// <summary>
        /// The sprite side is off, but the tile object — and therefore its
        /// <see cref="TileCollider"/> — is not.
        ///
        /// <para><b>Currently unreachable from the game, and kept on purpose.</b> It was the mechanism
        /// that let the quarter renderer draw the room while the shipped tiles still carried the
        /// colliders: build both, hide one. The room now builds <i>either</i> sprites and colliders
        /// (<c>TileVisualManager</c>, the fallback) or colliders alone (<c>TileCollisionBuilder</c>, the
        /// default), so nothing suppresses anything — there is no second picture to hide. It stays
        /// because it is the only way to switch renderers inside a live room, and because removing it
        /// would mean re-deriving the same reasoning the day that becomes possible.</para>
        ///
        /// <para>Separate from <see cref="isVisible"/> on purpose: <c>isVisible</c> is the tile's own
        /// state and is written by <see cref="Reset"/>, <see cref="DestroyTile"/> and every
        /// <see cref="UpdateSprite"/> pass. A suppression folded into it would be undone by the next
        /// destroyed-tile refresh, which is the kind of "the old renderer came back" that only shows up
        /// after someone shoots a wall.</para>
        /// </summary>
        [SerializeField] private bool visualsSuppressed = false;

        // Change detection for UpdateSprite — see SpritesMatch. Not serialized: a domain reload must
        // re-apply, not trust a stale snapshot. _hasAppliedProvidedSprites starts false so the first
        // provided-sprite pass always runs.
        private bool _hasAppliedProvidedSprites = false;
        private float _appliedHeightScale = 1f;

        // The one flash in flight, so a new Flash stops it and DestroyTile can cancel it — see Flash.
        private Coroutine _flashCoroutine;

        [SerializeField] private float currentOpacity = 1f;
        [SerializeField] private Color currentColor = Color.white;
        [SerializeField] private Color secondaryColorTint = Color.white;
        [SerializeField] private int sortingOrderOffset = 0;
        [SerializeField] private string baseSortingLayerName = MapSortingLayers.MainTiles;
        [SerializeField] private string frontSortingLayerName = MapSortingLayers.Foreground;

        /// <summary>
        /// Get the tile data this renderer displays.
        /// </summary>
        public TileData TileData => tileData;

        /// <summary>
        /// Get the grid position of this tile.
        /// </summary>
        public Vector2Int GridPosition => gridPosition;

        /// <summary>
        /// Is this tile currently visible?
        /// </summary>
        public bool IsVisible => isVisible;

        /// <summary>
        /// Initialize the tile renderer with data.
        /// </summary>
        public void Initialize(TileData data, TileAssetDatabase assetDatabase)
        {
            Initialize(data, assetDatabase, null, false);
        }

        /// <summary>
        /// Initialize the tile renderer with precomputed visuals.
        /// </summary>
        public void Initialize(TileData data, TileAssetDatabase assetDatabase, TileVisualData visualData, bool useProvidedSprites = true)
        {
            tileData = data;
            gridPosition = data.gridPosition;

            // Get or add SpriteRenderer
            if (spriteRenderer == null)
            {
                spriteRenderer = GetComponent<SpriteRenderer>();
                if (spriteRenderer == null)
                {
                    spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
                }
            }

            EnsureChildRenderers(useProvidedSprites ? visualData : null);
            ApplyVisualData(assetDatabase, visualData, useProvidedSprites);
            RememberAppliedSprites(visualData, useProvidedSprites);

            // Tile sprites use centered pivots, so place them at the tile bounds center.
            transform.localPosition = GetLocalPosition();

            ApplyScale(visualData != null ? visualData.HeightScale : GetHeightScale());

            // Set opacity
            currentOpacity = data.opacity;
            currentColor = Color.white;
            secondaryColorTint = Color.white;
            ApplyColor();
            UpdateOpacity();

            // Set sorting order (based on Y position for depth sorting)
            ApplySorting();
        }

        /// <summary>
        /// Get local position for this tile within the room transform.
        /// </summary>
        private Vector3 GetLocalPosition()
        {
            // Delegates to TileLayoutMath.LocalPosition — see that class for why the position is the
            // solid-rect centre and not the cell centre. The fallback for a null tileData keeps the
            // old behaviour (the whole-cell rect for the serialized gridPosition).
            if (tileData != null)
            {
                return TileLayoutMath.LocalPosition(tileData);
            }

            Vector3 unityPos = WorldCoordinates.PixelToUnity(WorldCoordinates.TileToRect(gridPosition).center);
            unityPos.z = gridPosition.y * TileLayoutMath.DepthPerRow;
            return unityPos;
        }

        /// <summary>
        /// Update the sprite based on current tile data.
        /// </summary>
        public void UpdateSprite(TileAssetDatabase assetDatabase)
        {
            UpdateSprite(assetDatabase, null, false);
        }

        /// <summary>
        /// Update the sprite based on current tile data or provided overrides.
        ///
        /// <para><b>No-op when nothing changed.</b> <c>TileVisualManager.ResolveVisualData</c> allocates
        /// a fresh <see cref="TileVisualData"/> every call, so the comparison is by value — the six
        /// sprite references and the height scale — not by reference. A refresh that resolves to the
        /// sprites already on the renderers skips <see cref="EnsureChildRenderers"/>,
        /// <see cref="ApplyVisualData"/> and <see cref="ApplyScale"/> entirely: those rewrite child
        /// transforms and re-run the bottom-left-anchored overlay layout for every slot, which is the
        /// visible cost of <c>RefreshSprites</c> over a room. Sorting and colour are still applied —
        /// they are cheap, and a sorting-layer or tint change can arrive on the same call that reuses
        /// last frame's sprites.</para>
        /// </summary>
        public void UpdateSprite(TileAssetDatabase assetDatabase, TileVisualData visualData, bool useProvidedSprites = true)
        {
            if (spriteRenderer == null || tileData == null)
                return;

            bool spritesUnchanged = useProvidedSprites && SpritesMatch(visualData);
            if (!spritesUnchanged)
            {
                EnsureChildRenderers(useProvidedSprites ? visualData : null);
                ApplyVisualData(assetDatabase, visualData, useProvidedSprites);
                ApplyScale(visualData != null ? visualData.HeightScale : GetHeightScale());
                RememberAppliedSprites(visualData, useProvidedSprites);
            }

            ApplySorting();
            ApplyColor();
        }

        /// <summary>
        /// Whether <paramref name="visualData"/> carries the same sprites and height scale as the last
        /// provided-sprite pass applied. A null or empty <paramref name="visualData"/> matches only when
        /// every slot is currently empty, so the first real update after a clear still runs.
        /// </summary>
        private bool SpritesMatch(TileVisualData visualData)
        {
            if (!_hasAppliedProvidedSprites)
                return false;

            return spriteRenderer.sprite == (visualData?.MainSprite)
                && SpriteOf(secondarySpriteRenderer) == (visualData?.BackSprite)
                && SpriteOf(rearOverlaySpriteRenderer) == (visualData?.RearOverlaySprite1)
                && SpriteOf(rearOverlaySpriteRenderer2) == (visualData?.RearOverlaySprite2)
                && SpriteOf(frontOverlaySpriteRenderer) == (visualData?.FrontOverlaySprite1)
                && SpriteOf(frontOverlaySpriteRenderer2) == (visualData?.FrontOverlaySprite2)
                && Mathf.Approximately(_appliedHeightScale, visualData != null ? visualData.HeightScale : 1f);
        }

        /// <summary>The sprite on a possibly-null child renderer; null slot and null sprite both read as null.</summary>
        private static Sprite SpriteOf(SpriteRenderer renderer)
        {
            return renderer != null ? renderer.sprite : null;
        }

        /// <summary>
        /// Remembers what a provided-sprite pass just applied, so the next <see cref="UpdateSprite"/>
        /// can tell a re-resolve of the same tile from a real change. Cleared on the asset-database
        /// path (<paramref name="useProvidedSprites"/> false), which resolves its own sprite and so has
        /// no provided set to compare against.
        /// </summary>
        private void RememberAppliedSprites(TileVisualData visualData, bool useProvidedSprites)
        {
            _hasAppliedProvidedSprites = useProvidedSprites;
            _appliedHeightScale = visualData != null ? visualData.HeightScale : 1f;
        }

        /// <summary>
        /// Update visual state (opacity, color).
        /// </summary>
        public void UpdateVisuals()
        {
            if (tileData == null)
                return;

            currentOpacity = tileData.opacity;
            ApplyColor();
            UpdateOpacity();
        }

        public void SetSortingOrderOffset(int offset)
        {
            sortingOrderOffset = offset;
            ApplySorting();
        }

        public void SetSortingLayers(string baseLayerName, string frontLayerName = null)
        {
            baseSortingLayerName = string.IsNullOrWhiteSpace(baseLayerName)
                ? MapSortingLayers.MainTiles
                : baseLayerName;
            frontSortingLayerName = string.IsNullOrWhiteSpace(frontLayerName)
                ? baseSortingLayerName
                : frontLayerName;
            ApplySorting();
        }

        public void SetColorTint(Color tint)
        {
            currentColor = tint;
            ApplyColor();
            UpdateOpacity();
        }

        public void SetSecondaryColorTint(Color tint)
        {
            secondaryColorTint = tint;
            ApplyColor();
            UpdateOpacity();
        }

        /// <summary>
        /// Update opacity.
        /// </summary>
        private void UpdateOpacity()
        {
            if (spriteRenderer == null)
                return;

            Color color = spriteRenderer.color;
            color.a = currentOpacity;
            spriteRenderer.color = color;

            if (secondarySpriteRenderer != null)
            {
                Color secondaryColor = secondarySpriteRenderer.color;
                secondaryColor.a = currentOpacity;
                secondarySpriteRenderer.color = secondaryColor;
            }

            UpdateRendererOpacity(rearOverlaySpriteRenderer);
            UpdateRendererOpacity(rearOverlaySpriteRenderer2);
            UpdateRendererOpacity(frontOverlaySpriteRenderer);
            UpdateRendererOpacity(frontOverlaySpriteRenderer2);
        }

        /// <summary>
        /// Set visibility.
        /// </summary>
        public void SetVisible(bool visible)
        {
            isVisible = visible;
            if (spriteRenderer != null)
            {
                spriteRenderer.enabled = ShouldEnable(spriteRenderer, visible);
            }
            if (secondarySpriteRenderer != null)
            {
                secondarySpriteRenderer.enabled = ShouldEnable(secondarySpriteRenderer, visible);
            }
            SetRendererVisibility(rearOverlaySpriteRenderer, visible);
            SetRendererVisibility(rearOverlaySpriteRenderer2, visible);
            SetRendererVisibility(frontOverlaySpriteRenderer, visible);
            SetRendererVisibility(frontOverlaySpriteRenderer2, visible);
        }

        /// <summary>
        /// Suppress (or restore) this tile's sprites without touching its GameObject, its collider or its
        /// own <see cref="IsVisible"/> state.
        ///
        /// <para>Used by <c>RoomVisualController</c> when the quarter terrain renderer is drawing the
        /// room instead: the shipped tile objects stay because they own the collision, and only their
        /// renderers go quiet. Every <c>enabled</c> write in this class goes through
        /// <see cref="ShouldEnable"/>, so a later <see cref="UpdateSprite"/>, <see cref="Reset"/> or
        /// <see cref="DestroyTile"/> cannot turn the suppressed sprites back on.</para>
        /// </summary>
        public void SetVisualsSuppressed(bool suppressed)
        {
            visualsSuppressed = suppressed;
            SetVisible(isVisible);
        }

        /// <summary>Whether this tile's sprites are being suppressed in favour of another renderer.</summary>
        public bool VisualsSuppressed => visualsSuppressed;

        /// <summary>
        /// Flash tile (for damage, etc.).
        ///
        /// <para><b>Restores the authoritative colour, not a captured one.</b> The old coroutine cached
        /// <c>spriteRenderer.color</c> on entry and wrote it back on exit, so a
        /// <see cref="SetColorTint"/> or <see cref="UpdateVisuals"/> landing mid-flash was silently
        /// undone, and two overlapping flashes restored their stale snapshots in arbitrary order. The
        /// renderer's true colour is derived state — <see cref="ApplyColor"/> from
        /// <c>currentColor</c>/<c>secondaryColorTint</c>, alpha from <see cref="UpdateOpacity"/> — so
        /// the restore re-applies that instead of a snapshot, and only one flash ever runs at a time:
        /// a new <see cref="Flash"/> stops the one in flight.</para>
        /// </summary>
        public void Flash(Color flashColor, float duration = 0.1f)
        {
            if (spriteRenderer == null)
                return;

            StopFlash();
            _flashCoroutine = StartCoroutine(FlashCoroutine(flashColor, duration));
        }

        private System.Collections.IEnumerator FlashCoroutine(Color flashColor, float duration)
        {
            ApplyFlashColor(flashColor);

            yield return new WaitForSeconds(duration);

            _flashCoroutine = null;
            // Re-derive the colour from currentColor/secondaryColorTint/currentOpacity rather than
            // restore a snapshot — whatever the tint became during the flash is the colour to keep.
            ApplyColor();
            UpdateOpacity();
        }

        /// <summary>Stops the flash in flight, if any, leaving the flash colour until the next apply.</summary>
        private void StopFlash()
        {
            if (_flashCoroutine != null)
            {
                StopCoroutine(_flashCoroutine);
                _flashCoroutine = null;
            }
        }

        /// <summary>Writes the flash colour to every active slot, preserving each slot's alpha.</summary>
        private void ApplyFlashColor(Color flashColor)
        {
            SetRendererRgb(spriteRenderer, flashColor);
            SetRendererRgb(secondarySpriteRenderer, flashColor);
            SetRendererRgb(rearOverlaySpriteRenderer, flashColor);
            SetRendererRgb(rearOverlaySpriteRenderer2, flashColor);
            SetRendererRgb(frontOverlaySpriteRenderer, flashColor);
            SetRendererRgb(frontOverlaySpriteRenderer2, flashColor);
        }

        /// <summary>Sets RGB on a possibly-null renderer without touching its alpha.</summary>
        private static void SetRendererRgb(SpriteRenderer renderer, Color color)
        {
            if (renderer == null)
                return;

            Color c = renderer.color;
            c.r = color.r;
            c.g = color.g;
            c.b = color.b;
            renderer.color = c;
        }

        /// <summary>
        /// Destroy this tile (play effect, then disable).
        /// </summary>
        public void DestroyTile()
        {
            // TODO: Play destruction effect

            // A flash still in flight would re-apply colour to a tile that is already hidden and about
            // to be destroyed; stop it before the 0.5 s destroy delay, not after.
            StopFlash();

            SetVisible(false);

            // Destroy after effect
            if (Application.isPlaying)
            {
                Destroy(gameObject, 0.5f);
            }
            else
            {
                DestroyImmediate(gameObject);
            }
        }

        /// <summary>
        /// Reset tile to initial state.
        /// </summary>
        public void Reset()
        {
            if (tileData == null)
                return;

            tileData.Reset();
            UpdateVisuals();
            SetVisible(true);
        }

        public void ApplyEditedPreviewTileAndRerender()
        {
            if (tileData == null)
            {
                return;
            }

            RoomVisualController controller = GetComponentInParent<RoomVisualController>();
            controller?.ApplyEditedPreviewTile(tileData);
        }

        private void ApplyVisualData(TileAssetDatabase assetDatabase, TileVisualData visualData, bool useProvidedSprites)
        {
            if (useProvidedSprites)
            {
                if (visualData == null)
                {
                    visualData = new TileVisualData();
                }
                spriteRenderer.sprite = visualData.MainSprite;
                AssignChildSprite(secondarySpriteRenderer, visualData.BackSprite);
                AssignChildSprite(rearOverlaySpriteRenderer, visualData.RearOverlaySprite1);
                AssignChildSprite(rearOverlaySpriteRenderer2, visualData.RearOverlaySprite2);
                AssignChildSprite(frontOverlaySpriteRenderer, visualData.FrontOverlaySprite1);
                AssignChildSprite(frontOverlaySpriteRenderer2, visualData.FrontOverlaySprite2);

                SetVisible(isVisible);
                return;
            }

            if (assetDatabase != null)
            {
                // Try to get sprite by graphic name first
                string graphicName = tileData.GetFrontGraphic();
                Sprite sprite = !string.IsNullOrEmpty(graphicName) ? assetDatabase.GetSprite(graphicName) : null;

                // Fallback to visual ID
                if (sprite == null && tileData.visualId > 0)
                {
                    sprite = assetDatabase.GetSpriteByVisualId(tileData.visualId);
                }

                // Final fallback to default
                if (sprite == null)
                {
                    sprite = assetDatabase.GetDefaultSprite(tileData.physicsType);
                }

                spriteRenderer.sprite = sprite;
                spriteRenderer.enabled = ShouldEnable(spriteRenderer, isVisible);
            }

            AssignChildSprite(secondarySpriteRenderer, null);
            AssignChildSprite(rearOverlaySpriteRenderer, null);
            AssignChildSprite(rearOverlaySpriteRenderer2, null);
            AssignChildSprite(frontOverlaySpriteRenderer, null);
            AssignChildSprite(frontOverlaySpriteRenderer2, null);
            SetVisible(isVisible);
        }

        /// <summary>
        /// Ensure the child renderers this tile's visuals actually need exist.
        ///
        /// The five overlay/background slots used to be created unconditionally, so a tile showing a
        /// single sprite still carried 6 GameObjects and 6 SpriteRenderers. At ~1136 tiles per room
        /// that is ~6800 of each, all transform-updated, culled and sorted every frame, while the
        /// statistics show ~1.13 quads per draw call — i.e. almost every one of them was empty. A
        /// slot is now created only when it is about to receive a non-null sprite.
        /// </summary>
        private void EnsureChildRenderers(TileVisualData visualData)
        {
            secondarySpriteRenderer = EnsureChildRenderer("BackgroundSprite", secondarySpriteRenderer, visualData != null ? visualData.BackSprite : null);
            rearOverlaySpriteRenderer = EnsureChildRenderer("RearOverlaySprite", rearOverlaySpriteRenderer, visualData != null ? visualData.RearOverlaySprite1 : null);
            rearOverlaySpriteRenderer2 = EnsureChildRenderer("RearOverlaySprite2", rearOverlaySpriteRenderer2, visualData != null ? visualData.RearOverlaySprite2 : null);
            frontOverlaySpriteRenderer = EnsureChildRenderer("FrontOverlaySprite", frontOverlaySpriteRenderer, visualData != null ? visualData.FrontOverlaySprite1 : null);
            frontOverlaySpriteRenderer2 = EnsureChildRenderer("FrontOverlaySprite2", frontOverlaySpriteRenderer2, visualData != null ? visualData.FrontOverlaySprite2 : null);
            ApplyChildRendererTransforms();
        }

        private SpriteRenderer EnsureChildRenderer(string childName, SpriteRenderer existingRenderer, Sprite sprite)
        {
            if (existingRenderer != null)
            {
                return existingRenderer;
            }

            // Nothing to show in this slot, so leave it uncreated. A slot created on an earlier pass
            // and now empty is kept — the caller clears its sprite and SetVisible disables it — so
            // this only ever skips the initial creation.
            if (sprite == null)
            {
                return null;
            }

            Transform child = transform.Find(childName);
            if (child == null)
            {
                GameObject childObj = new GameObject(childName);
                childObj.transform.SetParent(transform, false);
                child = childObj.transform;
            }

            SpriteRenderer renderer = child.GetComponent<SpriteRenderer>();
            if (renderer == null)
            {
                renderer = child.gameObject.AddComponent<SpriteRenderer>();
            }

            return renderer;
        }

        /// <summary>
        /// Assign a sprite to an optional child renderer. The slot may legitimately not exist — see
        /// <see cref="EnsureChildRenderers"/>.
        /// </summary>
        private static void AssignChildSprite(SpriteRenderer renderer, Sprite sprite)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.sprite = sprite;
        }

        private void ApplyChildRendererTransforms()
        {
            ApplyBackgroundRendererTransform();
            ApplyOverlayRendererTransform(rearOverlaySpriteRenderer);
            ApplyOverlayRendererTransform(rearOverlaySpriteRenderer2);
            ApplyOverlayRendererTransform(frontOverlaySpriteRenderer);
            ApplyOverlayRendererTransform(frontOverlaySpriteRenderer2);
        }

        private void ApplyBackgroundRendererTransform()
        {
            if (secondarySpriteRenderer == null)
            {
                return;
            }

            if (secondarySpriteRenderer.sprite == null || tileData == null)
            {
                secondarySpriteRenderer.transform.localScale = Vector3.one;
                secondarySpriteRenderer.transform.localPosition = Vector3.zero;
                return;
            }

            float targetWorldSize = WorldConstants.TILE_SIZE / 100f;
            Vector3 parentScale = transform.localScale;
            float parentScaleX = Mathf.Abs(parentScale.x) > 0.0001f ? parentScale.x : 1f;
            float parentScaleY = Mathf.Abs(parentScale.y) > 0.0001f ? parentScale.y : 1f;

            Vector3 localScale = Vector3.one;
            float spriteWidth = secondarySpriteRenderer.sprite.bounds.size.x;
            float spriteHeight = secondarySpriteRenderer.sprite.bounds.size.y;
            if (spriteWidth > 0f)
            {
                localScale.x = targetWorldSize / (spriteWidth * parentScaleX);
            }
            if (spriteHeight > 0f)
            {
                localScale.y = targetWorldSize / (spriteHeight * parentScaleY);
            }

            secondarySpriteRenderer.transform.localScale = localScale;

            float actualCenterYPixels = tileData.GetBounds().center.y;
            float baseCenterYPixels = (tileData.gridPosition.y + 0.5f) * WorldConstants.TILE_SIZE;
            float verticalOffsetUnits = (baseCenterYPixels - actualCenterYPixels) / 100f;
            secondarySpriteRenderer.transform.localPosition = new Vector3(
                0f,
                verticalOffsetUnits / parentScaleY,
                0f);
        }

        private void ApplyOverlayRendererTransform(SpriteRenderer overlayRenderer)
        {
            if (overlayRenderer == null)
            {
                return;
            }

            if (overlayRenderer.sprite == null)
            {
                overlayRenderer.transform.localScale = Vector3.one;
                overlayRenderer.transform.localPosition = Vector3.zero;
                return;
            }

            Vector3 parentScale = transform.localScale;
            if (!TileOverlayLayoutUtility.TryComputeBottomLeftAnchoredLayout(
                overlayRenderer.sprite.rect,
                new Vector2(overlayRenderer.sprite.texture.width, overlayRenderer.sprite.texture.height),
                new Vector2(parentScale.x, parentScale.y),
                out Vector3 localPosition,
                out Vector3 localScale))
            {
                overlayRenderer.transform.localScale = Vector3.one;
                overlayRenderer.transform.localPosition = Vector3.zero;
                return;
            }

            overlayRenderer.transform.localScale = localScale;
            overlayRenderer.transform.localPosition = localPosition;
        }

        private void ApplyScale(float heightScale)
        {
            float targetSize = WorldConstants.TILE_SIZE / 100f;
            Vector3 scale = Vector3.one;

            Sprite sizeReference = spriteRenderer.sprite != null
                ? spriteRenderer.sprite
                : (secondarySpriteRenderer != null ? secondarySpriteRenderer.sprite : null);

            if (sizeReference != null)
            {
                float spriteWidth = sizeReference.bounds.size.x;
                float spriteHeight = sizeReference.bounds.size.y;
                if (spriteWidth > 0f)
                {
                    scale.x = targetSize / spriteWidth;
                }
                if (spriteHeight > 0f)
                {
                    scale.y = targetSize * Mathf.Clamp01(heightScale) / spriteHeight;
                }
            }

            transform.localScale = scale;
            ApplyChildRendererTransforms();
        }

        private float GetHeightScale()
        {
            // Delegates to TileLayoutMath.HeightScale. That copy clamps heightLevel to [0, 3]; this one
            // did not, so an out-of-range level could produce a negative scale here while TileData and
            // the quarter renderer clamped. In-range values — every shipped tile — are identical.
            return TileLayoutMath.HeightScale(tileData);
        }

        private void ApplySorting()
        {
            int baseOrder = sortingOrderOffset - gridPosition.y;
            spriteRenderer.sortingLayerName = baseSortingLayerName;
            spriteRenderer.sortingOrder = baseOrder;

            ApplyRendererSorting(secondarySpriteRenderer, MapSortingLayers.BackgroundTiles, baseOrder - 3);
            ApplyRendererSorting(rearOverlaySpriteRenderer, baseSortingLayerName, baseOrder - 2);
            ApplyRendererSorting(rearOverlaySpriteRenderer2, baseSortingLayerName, baseOrder - 1);
            ApplyRendererSorting(frontOverlaySpriteRenderer, frontSortingLayerName, baseOrder + 1);
            ApplyRendererSorting(frontOverlaySpriteRenderer2, frontSortingLayerName, baseOrder + 2);
        }

        private void ApplyColor()
        {
            ApplyRendererColor(spriteRenderer, currentColor);
            ApplyRendererColor(secondarySpriteRenderer, secondaryColorTint);
            ApplyRendererColor(rearOverlaySpriteRenderer, currentColor);
            ApplyRendererColor(rearOverlaySpriteRenderer2, currentColor);
            ApplyRendererColor(frontOverlaySpriteRenderer, currentColor);
            ApplyRendererColor(frontOverlaySpriteRenderer2, currentColor);
        }

        private void ApplyRendererSorting(SpriteRenderer renderer, string sortingLayerName, int sortingOrder)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.sortingLayerName = sortingLayerName;
            renderer.sortingOrder = sortingOrder;
        }

        private void UpdateRendererOpacity(SpriteRenderer renderer)
        {
            if (renderer == null)
            {
                return;
            }

            Color color = renderer.color;
            color.a = currentOpacity;
            renderer.color = color;
        }

        private static void ApplyRendererColor(SpriteRenderer renderer, Color tint)
        {
            if (renderer == null)
            {
                return;
            }

            Color color = renderer.color;
            color.r = tint.r;
            color.g = tint.g;
            color.b = tint.b;
            renderer.color = color;
        }

        private void SetRendererVisibility(SpriteRenderer renderer, bool visible)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.enabled = ShouldEnable(renderer, visible);
        }

        /// <summary>
        /// The one place that decides whether a renderer may draw: the caller wants it, the tile is not
        /// suppressed, and there is a sprite to draw.
        ///
        /// <para>Centralised because <see cref="SetVisible"/> and <see cref="ApplyVisualData"/> used to
        /// write the expression twice; with a third input (the suppression) a copy that missed it would
        /// re-enable one sprite slot and leave the room with, say, its background layer still on the old
        /// renderer.</para>
        /// </summary>
        private bool ShouldEnable(SpriteRenderer renderer, bool visible)
        {
            return visible && !visualsSuppressed && renderer != null && renderer.sprite != null;
        }
    }
}
