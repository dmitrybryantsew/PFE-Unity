using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// MonoBehaviour component for rendering a single tile.
    /// Attached to each tile GameObject in the scene.
    /// </summary>
    [DisallowMultipleComponent]
    public class TileRenderer : MonoBehaviour
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

            EnsureChildRenderers();
            ApplyVisualData(assetDatabase, visualData, useProvidedSprites);

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
            Rect tileBounds = tileData != null
                ? tileData.GetBounds()
                : WorldCoordinates.TileToRect(gridPosition);
            Vector3 unityPos = WorldCoordinates.PixelToUnity(tileBounds.center);

            // Set Z for layering
            unityPos.z = gridPosition.y * 0.01f; // Small Z offset for depth

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
        /// </summary>
        public void UpdateSprite(TileAssetDatabase assetDatabase, TileVisualData visualData, bool useProvidedSprites = true)
        {
            if (spriteRenderer == null || tileData == null)
                return;

            EnsureChildRenderers();
            ApplyVisualData(assetDatabase, visualData, useProvidedSprites);
            ApplyScale(visualData != null ? visualData.HeightScale : GetHeightScale());
            ApplySorting();
            ApplyColor();
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
                spriteRenderer.enabled = visible && spriteRenderer.sprite != null;
            }
            if (secondarySpriteRenderer != null)
            {
                secondarySpriteRenderer.enabled = visible && secondarySpriteRenderer.sprite != null;
            }
            SetRendererVisibility(rearOverlaySpriteRenderer, visible);
            SetRendererVisibility(rearOverlaySpriteRenderer2, visible);
            SetRendererVisibility(frontOverlaySpriteRenderer, visible);
            SetRendererVisibility(frontOverlaySpriteRenderer2, visible);
        }

        /// <summary>
        /// Flash tile (for damage, etc.).
        /// </summary>
        public void Flash(Color flashColor, float duration = 0.1f)
        {
            if (spriteRenderer == null)
                return;

            StartCoroutine(FlashCoroutine(flashColor, duration));
        }

        private System.Collections.IEnumerator FlashCoroutine(Color flashColor, float duration)
        {
            Color originalColor = spriteRenderer.color;
            spriteRenderer.color = flashColor;

            yield return new WaitForSeconds(duration);

            spriteRenderer.color = originalColor;
        }

        /// <summary>
        /// Destroy this tile (play effect, then disable).
        /// </summary>
        public void DestroyTile()
        {
            // TODO: Play destruction effect

            SetVisible(false);

            // Destroy after effect
            Destroy(gameObject, 0.5f);
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
                secondarySpriteRenderer.sprite = visualData.BackSprite;
                rearOverlaySpriteRenderer.sprite = visualData.RearOverlaySprite1;
                rearOverlaySpriteRenderer2.sprite = visualData.RearOverlaySprite2;
                frontOverlaySpriteRenderer.sprite = visualData.FrontOverlaySprite1;
                frontOverlaySpriteRenderer2.sprite = visualData.FrontOverlaySprite2;

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
                spriteRenderer.enabled = isVisible && sprite != null;
            }

            secondarySpriteRenderer.sprite = null;
            rearOverlaySpriteRenderer.sprite = null;
            rearOverlaySpriteRenderer2.sprite = null;
            frontOverlaySpriteRenderer.sprite = null;
            frontOverlaySpriteRenderer2.sprite = null;
            SetVisible(isVisible);
        }

        private void EnsureChildRenderers()
        {
            secondarySpriteRenderer = EnsureChildRenderer("BackgroundSprite", secondarySpriteRenderer);
            rearOverlaySpriteRenderer = EnsureChildRenderer("RearOverlaySprite", rearOverlaySpriteRenderer);
            rearOverlaySpriteRenderer2 = EnsureChildRenderer("RearOverlaySprite2", rearOverlaySpriteRenderer2);
            frontOverlaySpriteRenderer = EnsureChildRenderer("FrontOverlaySprite", frontOverlaySpriteRenderer);
            frontOverlaySpriteRenderer2 = EnsureChildRenderer("FrontOverlaySprite2", frontOverlaySpriteRenderer2);
            ApplyChildRendererTransforms();
        }

        private SpriteRenderer EnsureChildRenderer(string childName, SpriteRenderer existingRenderer)
        {
            if (existingRenderer != null)
            {
                return existingRenderer;
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
                : secondarySpriteRenderer.sprite;

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
            if (tileData == null)
            {
                return 1f;
            }

            return Mathf.Clamp01(1f - tileData.heightLevel * 0.25f);
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

            renderer.enabled = visible && renderer.sprite != null;
        }
    }
}
