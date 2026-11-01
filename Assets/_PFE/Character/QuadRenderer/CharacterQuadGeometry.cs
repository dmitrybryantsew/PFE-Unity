using UnityEngine;

namespace PFE.Character
{
    /// <summary>
    /// Everything the quad builder needs to know about one imported sprite, flattened into numbers so
    /// the arithmetic can be exercised without a live <see cref="Sprite"/>.
    ///
    /// <para><b>Why the numbers are separate from the Sprite.</b> A <c>Sprite</c> cannot be constructed
    /// in a host that has no engine behind it, and even inside the editor the interesting cases are the
    /// <i>trimmed</i> ones, which would each need a texture on disk. Carrying
    /// <c>textureRect</c> + full texture size + PPU as floats lets the pivot table in
    /// <c>docs/RenderingPipeline_Research/07</c> §3 be reproduced by a plain NUnit fixture.</para>
    /// </summary>
    public readonly struct CharacterQuadSource
    {
        /// <summary>Sprite rect inside the texture, in pixels, Y up from the texture's bottom-left.</summary>
        public readonly float RectX;
        public readonly float RectY;
        public readonly float RectWidth;
        public readonly float RectHeight;

        /// <summary>Full texture size in pixels — the rectangle the pivot is normalised against.</summary>
        public readonly float TextureWidth;
        public readonly float TextureHeight;

        public readonly float PixelsPerUnit;

        public CharacterQuadSource(
            float rectX,
            float rectY,
            float rectWidth,
            float rectHeight,
            float textureWidth,
            float textureHeight,
            float pixelsPerUnit)
        {
            RectX = rectX;
            RectY = rectY;
            RectWidth = rectWidth;
            RectHeight = rectHeight;
            TextureWidth = textureWidth;
            TextureHeight = textureHeight;
            PixelsPerUnit = pixelsPerUnit;
        }

        /// <summary>
        /// Reads the four rectangles off an imported sprite. <see cref="Sprite.textureRect"/> is the
        /// <b>trimmed</b> sub-rect Unity's slicer produced; <c>texture.width/height</c> is the full PNG.
        /// The gap between the two is the whole subject of <see cref="CharacterQuadGeometry.PivotErrorPixels"/>.
        /// </summary>
        public static CharacterQuadSource FromSprite(Sprite sprite)
        {
            if (sprite == null)
            {
                return default;
            }

            Texture texture = sprite.texture;
            Rect rect = sprite.textureRect;

            return new CharacterQuadSource(
                rect.x,
                rect.y,
                rect.width,
                rect.height,
                texture != null ? texture.width : 0f,
                texture != null ? texture.height : 0f,
                sprite.pixelsPerUnit);
        }

        public bool IsValid =>
            RectWidth > 0f &&
            RectHeight > 0f &&
            TextureWidth > 0f &&
            TextureHeight > 0f &&
            PixelsPerUnit > 0f;
    }

    /// <summary>
    /// One quad in the sprite's own space: the four edges, in world units, measured from the
    /// registration point, plus the UV window that maps them onto the texture.
    /// </summary>
    public struct CharacterQuadLocal
    {
        public float XMin;
        public float XMax;
        public float YMin;
        public float YMax;

        public float UMin;
        public float UMax;
        public float VMin;
        public float VMax;

        public float Width => XMax - XMin;
        public float Height => YMax - YMin;
    }

    /// <summary>
    /// Turns an imported sprite + a placement into four vertices — the geometry Remains bakes offline
    /// and PFE re-derives at runtime.
    ///
    /// <para><b>Which registration convention, and how that was decided.</b> The shipped path calls
    /// <c>Sprite.Create(texture, textureRect, pivotNormalized, ppu)</c>, so Unity reads the pivot as a
    /// fraction of the <i>trimmed rect</i>. The original claim here was that the importer had written it
    /// as a fraction of the <i>full PNG</i> instead (<c>(-bounds.x)/bounds.width</c>), so every trimmed
    /// part landed a little off and resolving the pivot against the full texture was the fix.</para>
    ///
    /// <para><b>That claim was tested on 10-25 and is false.</b> The sprite side was reported correct by
    /// eye and the quad side displaced; measured, the two conventions differ by up to <b>0.325 units</b>
    /// on <c>rwing</c> and <b>0.043</b> on <c>fleg1</c>, while the parts whose rect covers the whole PNG
    /// (<c>lwing</c>, <c>fleg2</c>, <c>pip</c>, <c>morda_base</c>, <c>horn</c>) differ by exactly zero —
    /// the positive control. The reason is structural: the sprite rect is the <i>frame's</i> alpha-trim
    /// box, and the pivot was derived from that same frame's bounds, so pivot × rect is the point the
    /// importer meant. The full-PNG canvas is the symbol's <i>union</i> over all frames, which is not
    /// the box the pivot came from. <see cref="Build"/> therefore defaults to the rect convention, and
    /// <c>compensatePivot: true</c> is kept only as an A/B switch for
    /// <c>docs/CharacterImportPipeline/08</c>.</para>
    ///
    /// <para><b>The quad is the full rect, never the alpha contour.</b> Remains draws a
    /// <c>FullRect</c> quad; PFE's imported <c>spriteMeshType: 1</c> (Tight) cuts the mesh along the
    /// alpha edge and erodes the anti-aliased rim the vector rasteriser produced. Nothing here reads
    /// alpha, so the rim survives.</para>
    /// </summary>
    public static class CharacterQuadGeometry
    {
        /// <summary>
        /// Corner order used by every buffer written here: bottom-left, top-left, top-right,
        /// bottom-right. That order is clockwise seen from the camera, which is Unity's front face,
        /// and it makes the two triangles <c>(0,1,2)</c> and <c>(0,2,3)</c>.
        /// </summary>
        public const int VertexCount = 4;

        /// <summary>
        /// Builds the quad for one sprite.
        /// </summary>
        /// <param name="compensatePivot">
        /// <c>false</c> (the default, and what the renderers use) — treat <paramref name="pivotNormalized"/>
        /// as a fraction of the trimmed rect, which is how <c>Sprite.Create</c> reads it, so the two
        /// renderers place a part identically. <c>true</c> — treat it as a fraction of the full PNG.
        /// That was the original design and it is <b>wrong</b>: it displaces every trimmed part, by up to
        /// 0.325 units on <c>rwing</c>. Kept only so the preview window can show the difference.
        /// </param>
        public static CharacterQuadLocal Build(
            in CharacterQuadSource source,
            Vector2 pivotNormalized,
            bool compensatePivot = false)
        {
            if (!source.IsValid)
            {
                return default;
            }

            Vector2 pivotPixels = RegistrationPointPixels(source, pivotNormalized, compensatePivot);
            float inversePixelsPerUnit = 1f / source.PixelsPerUnit;

            return new CharacterQuadLocal
            {
                XMin = (source.RectX - pivotPixels.x) * inversePixelsPerUnit,
                XMax = (source.RectX + source.RectWidth - pivotPixels.x) * inversePixelsPerUnit,
                YMin = (source.RectY - pivotPixels.y) * inversePixelsPerUnit,
                YMax = (source.RectY + source.RectHeight - pivotPixels.y) * inversePixelsPerUnit,
                UMin = source.RectX / source.TextureWidth,
                UMax = (source.RectX + source.RectWidth) / source.TextureWidth,
                VMin = source.RectY / source.TextureHeight,
                VMax = (source.RectY + source.RectHeight) / source.TextureHeight,
            };
        }

        /// <summary>
        /// The registration point, in pixels inside the full texture.
        ///
        /// <para>The default branch is <c>rect.xy + pivotNormalized * rect.size</c> — the point
        /// <c>Sprite.Create</c> would pick, which is the point the importer meant, because the rect is
        /// the frame's alpha-trim box and the pivot was derived from that same frame's bounds.</para>
        ///
        /// <para>The compensating branch (<c>pivotNormalized * textureSize</c>) is retained only as an
        /// A/B: it resolves the pivot against the symbol's union canvas rather than the frame box, so it
        /// moves every trimmed part by a different amount. <c>docs/CharacterImportPipeline/08</c>.</para>
        /// </summary>
        public static Vector2 RegistrationPointPixels(
            in CharacterQuadSource source,
            Vector2 pivotNormalized,
            bool compensatePivot)
        {
            if (!compensatePivot)
            {
                return new Vector2(
                    source.RectX + pivotNormalized.x * source.RectWidth,
                    source.RectY + pivotNormalized.y * source.RectHeight);
            }

            return new Vector2(
                pivotNormalized.x * source.TextureWidth,
                pivotNormalized.y * source.TextureHeight);
        }

        /// <summary>
        /// How far the full-PNG convention lands from the rect convention, in pixels — i.e. exactly how
        /// far <c>compensatePivot: true</c> moves a part, and in which direction.
        ///
        /// <para>This is the measured quantity in <c>docs/RenderingPipeline_Research/07</c> §3 —
        /// <c>x0 − p·(W − w)</c> and <c>y0 − p·(H − h)</c> — expressed as the difference between the
        /// two registration points rather than a separate formula. A part that was not trimmed reads
        /// exactly <c>(0, 0)</c>; that is the positive control which says the arithmetic is not
        /// inventing numbers, and on 10-25 it was also the row that showed the <i>compensated</i>
        /// branch to be the wrong one.</para>
        /// </summary>
        public static Vector2 PivotErrorPixels(in CharacterQuadSource source, Vector2 pivotNormalized)
        {
            if (!source.IsValid)
            {
                return Vector2.zero;
            }

            return RegistrationPointPixels(source, pivotNormalized, false)
                 - RegistrationPointPixels(source, pivotNormalized, true);
        }

        /// <summary>
        /// Writes the four corners in the sprite's parent space: scale, then rotate, then translate —
        /// the same TRS composition Unity's transform performs, done here so the result is four baked
        /// vertices rather than a hierarchy.
        /// </summary>
        public static void WriteCorners(
            in CharacterQuadLocal quad,
            Vector2 position,
            float rotationDegrees,
            Vector2 scale,
            Vector3[] buffer,
            int offset = 0)
        {
            if (buffer == null || buffer.Length < offset + VertexCount)
            {
                return;
            }

            float radians = rotationDegrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);

            WriteCorner(quad.XMin, quad.YMin, position, scale, cos, sin, buffer, offset + 0);
            WriteCorner(quad.XMin, quad.YMax, position, scale, cos, sin, buffer, offset + 1);
            WriteCorner(quad.XMax, quad.YMax, position, scale, cos, sin, buffer, offset + 2);
            WriteCorner(quad.XMax, quad.YMin, position, scale, cos, sin, buffer, offset + 3);
        }

        /// <summary>Writes the UVs matching <see cref="WriteCorners"/>'s corner order.</summary>
        public static void WriteUvs(in CharacterQuadLocal quad, Vector2[] buffer, int offset = 0)
        {
            if (buffer == null || buffer.Length < offset + VertexCount)
            {
                return;
            }

            buffer[offset + 0] = new Vector2(quad.UMin, quad.VMin);
            buffer[offset + 1] = new Vector2(quad.UMin, quad.VMax);
            buffer[offset + 2] = new Vector2(quad.UMax, quad.VMax);
            buffer[offset + 3] = new Vector2(quad.UMax, quad.VMin);
        }

        static void WriteCorner(
            float x,
            float y,
            Vector2 position,
            Vector2 scale,
            float cos,
            float sin,
            Vector3[] buffer,
            int index)
        {
            float scaledX = x * scale.x;
            float scaledY = y * scale.y;

            buffer[index] = new Vector3(
                position.x + scaledX * cos - scaledY * sin,
                position.y + scaledX * sin + scaledY * cos,
                0f);
        }
    }

    /// <summary>
    /// "How dark is this room?" — one number, in one place, for the character.
    ///
    /// <para><b>Why a new class rather than a call into the backdrop.</b> Three layers already answer
    /// this question three different ways (backwall <c>Lerp(1, 0.7, α)</c>, background tiles
    /// <c>1 − α</c>, main walls <c>Color.white</c>), and the renderer that draws the character is a
    /// fourth. Flash's <c>hardlight</c> over black is <c>1 − α</c>, which is the <i>background tile</i>
    /// row, so that is the number reproduced here. The formula is duplicated rather than shared because
    /// <c>RoomBackdropRenderer.ResolveTileDarknessMultiplier</c> is private and this is presentation-only
    /// code; <see cref="PFE.Tests.EditMode.Character.CharacterQuadGeometryTests"/> pins the two to the
    /// same values so the duplication cannot drift silently.</para>
    /// </summary>
    public static class CharacterRenderAmbient
    {
        /// <summary>
        /// The base darkness every room carries before its own <c>darkness</c> is added —
        /// <c>RoomBackdropRenderer.BackdropBaseDarkness</c>.
        /// </summary>
        public const int BaseDarkness = 170;

        public static float FromDarkness(int darkness)
        {
            return 1f - Mathf.Clamp01((BaseDarkness + darkness) / 255f);
        }
    }
}
