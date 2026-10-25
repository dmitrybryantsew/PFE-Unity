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
    /// <para><b>What this fixes, and why it needs no import change.</b> The shipped path calls
    /// <c>Sprite.Create(texture, textureRect, pivotNormalized, ppu)</c>. Unity reads that pivot as a
    /// fraction of the <i>trimmed</i> rect, but the importer computed it as a fraction of the
    /// <i>full PNG</i> (<c>CharacterSpriteImporter</c>: <c>(-bounds.x)/bounds.width</c>). Every part
    /// whose PNG was alpha-trimmed therefore lands a little off, and by a different amount per part.
    /// Here the pivot is resolved to <b>pixels in the full texture</b> and the rect is offset against
    /// it, so the two rectangles can never disagree — the compensation is a consequence of the
    /// arithmetic rather than a correction bolted on afterwards.</para>
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
        /// <c>true</c> — treat <paramref name="pivotNormalized"/> as a fraction of the full texture,
        /// which is how the importer wrote it. <c>false</c> — treat it as a fraction of the trimmed
        /// rect, which is how <c>Sprite.Create</c> reads it. The difference between the two is
        /// <see cref="PivotErrorPixels"/>, and the preview window exposes it as a toggle precisely so
        /// the hypothesis can be falsified instead of assumed.
        /// </param>
        public static CharacterQuadLocal Build(
            in CharacterQuadSource source,
            Vector2 pivotNormalized,
            bool compensatePivot)
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
        /// <para>The compensating branch is the whole fix: <c>pivotNormalized * textureSize</c> is a
        /// point in the PNG, and the quad's origin is then placed there even if that point lies outside
        /// the trimmed rect (which it does for the horn: the trim starts 38 px in).</para>
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
        /// How far the shipped pivot lands from where the importer meant it, in pixels.
        ///
        /// <para>This is the measured quantity in <c>docs/RenderingPipeline_Research/07</c> §3 —
        /// <c>x0 − p·(W − w)</c> and <c>y0 − p·(H − h)</c> — expressed as the difference between the
        /// two registration points rather than a separate formula. A part that was not trimmed reads
        /// exactly <c>(0, 0)</c>; that is the positive control which says the arithmetic is not
        /// inventing numbers.</para>
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
