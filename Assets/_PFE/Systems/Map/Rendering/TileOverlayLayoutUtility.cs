using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Reconstructs Flash-style tileFront placement from Unity's trimmed sprites.
    /// Flash positions these overlays by tile origin, while Unity imports trim the
    /// transparent margins away. We restore that lost anchor here.
    /// </summary>
    public static class TileOverlayLayoutUtility
    {
        public static bool TryComputeBottomLeftAnchoredLayout(
            Rect spriteRectPixels,
            Vector2 textureSizePixels,
            Vector2 parentScale,
            out Vector3 localPosition,
            out Vector3 localScale)
        {
            localPosition = Vector3.zero;
            localScale = Vector3.one;

            if (textureSizePixels.x <= 0f || textureSizePixels.y <= 0f)
            {
                return false;
            }

            float safeParentScaleX = Mathf.Abs(parentScale.x) > 0.0001f ? parentScale.x : 1f;
            float safeParentScaleY = Mathf.Abs(parentScale.y) > 0.0001f ? parentScale.y : 1f;
            localScale = new Vector3(
                1f / safeParentScaleX,
                1f / safeParentScaleY,
                1f);

            float spriteCenterXPixels = spriteRectPixels.x + spriteRectPixels.width * 0.5f;
            float spriteCenterYPixels = spriteRectPixels.y + spriteRectPixels.height * 0.5f;
            float textureCenterXPixels = textureSizePixels.x * 0.5f;
            float textureCenterYPixels = textureSizePixels.y * 0.5f;
            float offsetXPixels = spriteCenterXPixels - textureCenterXPixels;
            float offsetYPixels = textureCenterYPixels - spriteCenterYPixels;

            localPosition = new Vector3(
                offsetXPixels / 100f / safeParentScaleX,
                offsetYPixels / 100f / safeParentScaleY,
                0f);

            return true;
        }
    }
}
