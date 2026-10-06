using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Finds a weapon's barrel tip in its own artwork, and turns it into the sprite-local offset the
    /// presenter fires from — the stand-in for AS3's <c>vis.emit</c> marker.
    ///
    /// <para><b>Why it has to be measured.</b> AS3 reads the muzzle off a named child of the weapon
    /// symbol (<c>Weapon.getBulXY()</c>, <c>Weapon.as:1395-1420</c>, transforms <c>vis.emit</c> through
    /// <c>vis.localToGlobal</c>). That marker does not survive the SWF extraction: there is no
    /// <c>emit</c> in the exported data, no manifest recording where it was, and the source
    /// <c>pfe.swf</c> is not in the repository. So the muzzle has to be recovered from the pixels, and
    /// it is better to do that once, in the editor, than to guess per shot at runtime.</para>
    ///
    /// <para><b>What it measures.</b> PFE weapon art is drawn facing right, so the barrel tip is the
    /// art's rightmost column; its vertical centre within that column is the bore line. The rightmost
    /// column rather than the whole bounding box's centre because a gun's bounding box is dominated by
    /// the grip and magazine, which sit well below the bore — using the box centre would aim the
    /// muzzle out of the gun's belly.</para>
    ///
    /// <para><b>Known limitation, stated rather than hidden.</b> A weapon whose rightmost pixels are a
    /// front sight, a bayonet or a protruding magazine gets a muzzle a few pixels off the bore. It is a
    /// measurement, not a reconstruction: the baker logs every value it writes so a wrong one is
    /// visible in the console, and the field can be hand-corrected afterwards.</para>
    ///
    /// <para><b>Unity-free apart from <see cref="Color32"/> and <see cref="Vector2"/>.</b> The rule takes
    /// raw pixels and returns numbers, so it can be executed and pinned offline, the way
    /// <c>UnitSweepMath</c> and <c>UnitSpriteAnchor</c> are.</para>
    /// </summary>
    public static class WeaponMuzzleOffsetMath
    {
        /// <summary>
        /// Alpha at or above which a pixel counts as art. Matches
        /// <c>WeaponSpriteImporter</c>'s <c>alphaIsTransparency</c> import setting, which keeps the
        /// authored alpha channel rather than thresholding it, so a low value here still excludes the
        /// fully transparent padding that dominates these textures.
        /// </summary>
        public const byte OpaqueAlphaThreshold = 8;

        /// <summary>
        /// The barrel tip in <b>texture</b> pixels, or <c>false</c> when the region has no art at all.
        ///
        /// <para>Pixel space is Unity's texture convention: the origin is the texture's bottom-left and
        /// y grows upwards, which is the same space <see cref="Sprite.pivot"/> is expressed in (offset
        /// by the sprite's rect). <paramref name="pixels"/> is row-major with the <b>bottom</b> row
        /// first — the layout <see cref="Texture2D.GetPixels32"/> returns — so index
        /// <c>y * textureWidth + x</c>.</para>
        ///
        /// <para>The search is restricted to the sprite's own rect because these textures are larger
        /// than the sprite: a weapon frame's PNG is exported at the symbol's bounds while the sprite
        /// entry crops to the frame's bounds, so pixels outside the rect belong to no part of this
        /// sprite and could otherwise drag the tip outwards.</para>
        /// </summary>
        /// <param name="pixels">Texture pixels, bottom row first.</param>
        /// <param name="textureWidth">Texture width in pixels.</param>
        /// <param name="textureHeight">Texture height in pixels.</param>
        /// <param name="rect">The sprite's rect within the texture (Unity's <see cref="Sprite.rect"/>).</param>
        /// <param name="tip">The tip in texture pixels.</param>
        public static bool TryFindBarrelTipPixels(Color32[] pixels, int textureWidth, int textureHeight,
                                                  RectInt rect, out Vector2 tip)
        {
            tip = default;

            if (pixels == null || textureWidth <= 0 || textureHeight <= 0)
                return false;

            // Clamp the rect into the texture — a malformed meta should not read out of bounds.
            int x0 = Mathf.Max(0, rect.xMin);
            int x1 = Mathf.Min(textureWidth - 1, rect.xMax - 1);
            int y0 = Mathf.Max(0, rect.yMin);
            int y1 = Mathf.Min(textureHeight - 1, rect.yMax - 1);
            if (x1 < x0 || y1 < y0)
                return false;

            // Walk columns from the right; the first one holding art is the barrel tip.
            for (int x = x1; x >= x0; x--)
            {
                int lowest = int.MaxValue;
                int highest = int.MinValue;

                for (int y = y0; y <= y1; y++)
                {
                    int index = y * textureWidth + x;
                    if (index < 0 || index >= pixels.Length) continue;
                    if (pixels[index].a < OpaqueAlphaThreshold) continue;

                    if (y < lowest) lowest = y;
                    if (y > highest) highest = y;
                }

                if (highest < lowest) continue;   // nothing in this column, keep looking left

                // Bore line: the middle of the column's art. +0.5 puts the result at the centre of the
                // pixel rather than its corner, matching how Sprite.pivot is measured.
                tip = new Vector2(x + 0.5f, (lowest + highest) * 0.5f + 0.5f);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Convert a tip measured in <b>texture</b> pixels into the sprite-local offset the presenter
        /// wants — the port of AS3's <c>vis.emit</c> position, in the sprite's own units.
        ///
        /// <para><see cref="Sprite.pivot"/> is measured from the sprite rect's bottom-left, not the
        /// texture's, so the rect's origin has to be added before the two are subtracted. Skipping that
        /// shifts every offset by the rect's offset, which is zero only for a texture that happens to be
        /// exactly one uncropped sprite.</para>
        /// </summary>
        /// <param name="tipTexturePixels">Tip in texture pixels, from <see cref="TryFindBarrelTipPixels"/>.</param>
        /// <param name="spritePivotPixels">The sprite's pivot in pixels from its rect's bottom-left.</param>
        /// <param name="rectPositionPixels">The sprite rect's lower-left corner in texture pixels.</param>
        /// <param name="pixelsPerUnit">The sprite's pixels-per-unit; must be positive.</param>
        public static Vector2 ToLocalOffset(Vector2 tipTexturePixels, Vector2 spritePivotPixels,
                                            Vector2 rectPositionPixels, float pixelsPerUnit)
        {
            if (pixelsPerUnit <= 0f)
                return Vector2.zero;

            Vector2 pivotTexturePixels = rectPositionPixels + spritePivotPixels;
            return (tipTexturePixels - pivotTexturePixels) / pixelsPerUnit;
        }
    }
}
