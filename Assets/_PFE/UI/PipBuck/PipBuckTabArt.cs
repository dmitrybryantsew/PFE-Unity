using UnityEngine;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The PipBuck tab button's art, rendered from the Oracle's own shape data.
    ///
    /// <para><b>Why the exported PNG cannot be used.</b> The button is SWF symbol 1143
    /// (<c>pfe_fla.ButPage</c>), and it is <i>not</i> a bare shape: it also holds two
    /// <c>DefineEditText</c> fields that the ActionScript fills in at runtime —
    /// <c>PipBuck.as:172</c> writes <c>text.text = Res.pipText("main" + n)</c> and <c>:173</c> writes
    /// <c>id.text = n</c>. Their <i>design-time</i> values are the author's Russian placeholders
    /// (character 1140 = <c>Инвентарь</c>, character 1141 = <c>1</c>). FFDec's sprite export
    /// composites the whole display list, so <c>PipBuck_TabButton_1/2.png</c> arrived with
    /// <b>"Инвентарь1" rasterised into them</b> — which is the wrong text the device was showing,
    /// and which no amount of correct labelling could cover.</para>
    ///
    /// <para><b>What this class draws instead.</b> The button's real background, which the Oracle
    /// does define on its own, as two plain shapes:
    /// <c>pfe/shapes/1139.svg</c> (frame 1, normal) and <c>pfe/shapes/1142.svg</c> (frame 2,
    /// active). Both are the same recipe — a rectangle filled with a radial gradient and outlined
    /// in <c>#00ff99</c> — and differ only in the gradient's outer stop and the stroke width. So
    /// the whole button is four numbers per frame, and this class evaluates them.</para>
    ///
    /// <para><b>Verified against the real art, not asserted.</b> Rendered through
    /// <c>.workbuddy-ai/tmp/regen_tab_button.py</c> and diffed against FFDec's own export outside
    /// the baked-text band: <b>99.24%</b> of the normal frame's and <b>99.91%</b> of the active
    /// frame's non-text pixels agree within ±3 per channel. Every remaining pixel is on the
    /// outermost 1-pixel stroke edge or on a clipped corner, where FFDec's coverage is a fraction
    /// higher than a 4x4 supersample. That is the control that says these parameters are read from
    /// the Oracle rather than invented — an invented button would not land on FFDec's pixels.</para>
    /// </summary>
    public static class PipBuckTabArt
    {
        /// <summary>
        /// The canvas, and the same size <see cref="PipBuckLayout.TabButtonSize"/> places the tab
        /// at. Frame 1's own SVG viewBox is 129.7x66; FFDec pads it to 132x69 for the stroke.
        /// </summary>
        public const int Width = 132;

        /// <inheritdoc cref="Width"/>
        public const int Height = 69;

        // ── The Oracle's shape parameters ─────────────────────────────────────
        //
        // All of these are read out of pfe/shapes/1139.svg and pfe/shapes/1142.svg. Nothing here
        // is measured off the exported PNG.

        /// <summary>
        /// Where the shape's origin sits inside the canvas. <b>Measured, and the same for both
        /// frames</b>: frame 1's 1 px stroke fills exactly pixel row/column 1 and frame 2's 3 px
        /// stroke fills rows/columns 0..2, which puts both rectangles' edges on the half-pixel
        /// boundary at 1.5.
        /// </summary>
        private const float ShapeOffset = 1.5f;

        /// <summary>The filled rectangle, in shape space. Both frames use the same one.</summary>
        private const float RectWidth = 129.7f;

        /// <inheritdoc cref="RectWidth"/>
        private const float RectHeight = 66f;

        /// <summary>
        /// The gradient's centre, in shape space. The SVG declares
        /// <c>radialGradient cx="0" cy="0" r="819.2"</c> with
        /// <c>gradientTransform="matrix(0.0809, 0, 0, 0.1973, 64.8, 33.0)"</c>, so the ellipse's
        /// centre is the transform's translation.
        /// </summary>
        private const float GradientCenterX = 64.8f;

        /// <inheritdoc cref="GradientCenterX"/>
        private const float GradientCenterY = 33f;

        /// <summary>
        /// The gradient's radii, in shape space: the SVG's <c>r="819.2"</c> scaled by the
        /// transform's <c>0.0809</c> across and <c>0.1973</c> down. The ellipse is far taller than
        /// it is wide (66.3 by 161.6 on a 129.7x66 rectangle), which is why the corners are
        /// noticeably greener than the middle.
        /// </summary>
        private const float GradientRadiusX = 819.2f * 0.0809f;

        /// <inheritdoc cref="GradientRadiusX"/>
        private const float GradientRadiusY = 819.2f * 0.1973f;

        /// <summary>The gradient's inner stop — black in both frames.</summary>
        private static readonly Color32 GradientInner = new Color32(0x00, 0x00, 0x00, 0xFF);

        /// <summary>Frame 1's outer stop, <c>#004629</c>.</summary>
        private static readonly Color32 GradientOuterNormal = new Color32(0x00, 0x46, 0x29, 0xFF);

        /// <summary>Frame 2's outer stop, <c>#006d40</c> — brighter, which is what "active" means.</summary>
        private static readonly Color32 GradientOuterActive = new Color32(0x00, 0x6D, 0x40, 0xFF);

        /// <summary>The outline, <c>#00ff99</c> in both frames.</summary>
        private static readonly Color32 Stroke = new Color32(0x00, 0xFF, 0x99, 0xFF);

        /// <summary>
        /// The outline colour, published so a content check can share this one definition instead of
        /// re-typing the triple. The importer uses it to assert that the flat backdrop carries no
        /// stroke-coloured pixel where a tab button used to be — see
        /// <see cref="PipBuckLayout.BackdropFootprintInterior"/>.
        /// </summary>
        public static Color32 StrokeColor => Stroke;

        /// <summary>
        /// How far the shape's own origin sits inside the exported canvas, on both axes.
        ///
        /// <para>The SVG's stroke is centred on the rectangle, so FFDec's canvas is the rect padded
        /// by half the stroke width on each side — which is why the shape is drawn at (1.5, 1.5) of a
        /// 132x69 canvas rather than at its corner. Published because the backdrop's tab footprints
        /// need it: a <c>ButPage</c> placement gives the symbol's <i>registration point</i>, which is
        /// 1.5 px in from the canvas's top-left, not at it.</para>
        /// </summary>
        public static float CanvasOriginInset => ShapeOffset;

        /// <summary>Frame 1's outline width. The SVG's <c>stroke-width="1.0"</c>.</summary>
        private const float StrokeWidthNormal = 1f;

        /// <summary>
        /// Frame 2's outline width — three times frame 1's, which is the whole visual difference
        /// between "this tab is not selected" and "this tab is". The SVG's
        /// <c>stroke-width="3.0"</c>.
        /// </summary>
        private const float StrokeWidthActive = 3f;

        /// <summary>
        /// Samples per axis per pixel. 4 gives 16 samples, which is enough for a 1-pixel stroke on
        /// a rectangle: the only coverage the eye can catch is the outline's two half-covered
        /// edges, and the diff against FFDec is confined to those.
        /// </summary>
        private const int Supersample = 4;

        // ── The model ─────────────────────────────────────────────────────────

        /// <summary>
        /// One pixel of the button, at <paramref name="x"/> across and <paramref name="y"/>
        /// <b>down from the top-left corner</b>. Pure: no Unity object is touched, which is what
        /// lets the offline tests exercise it.
        /// </summary>
        /// <param name="active">Frame 2 (the lit tab) rather than frame 1.</param>
        public static Color32 Sample(int x, int y, bool active)
        {
            float halfStroke = (active ? StrokeWidthActive : StrokeWidthNormal) * 0.5f;
            Color32 outer = active ? GradientOuterActive : GradientOuterNormal;

            const int n = Supersample;
            const float step = 1f / n;
            const float first = step * 0.5f;

            int sumR = 0, sumG = 0, sumB = 0, covered = 0;

            for (int iy = 0; iy < n; iy++)
            {
                float shapeY = y + first + iy * step - ShapeOffset;

                for (int ix = 0; ix < n; ix++)
                {
                    float shapeX = x + first + ix * step - ShapeOffset;
                    float signed = SignedDistanceToOutline(shapeX, shapeY);

                    if (signed < -halfStroke) continue;   // outside the button entirely

                    if (signed <= halfStroke)
                    {
                        // Inside the stroke band, which straddles the rectangle's outline.
                        sumR += Stroke.r;
                        sumG += Stroke.g;
                        sumB += Stroke.b;
                    }
                    else
                    {
                        float t = GradientT(shapeX, shapeY);
                        sumR += (int)(outer.r * t);
                        sumG += (int)(outer.g * t);
                        sumB += (int)(outer.b * t);
                    }

                    covered++;
                }
            }

            if (covered == 0) return new Color32(0, 0, 0, 0);

            const int samples = n * n;
            const int half = samples / 2;

            // Covered samples average over the whole pixel, so an uncovered sample already
            // contributes the transparent black it should.
            return new Color32(
                (byte)((sumR + half) / samples),
                (byte)((sumG + half) / samples),
                (byte)((sumB + half) / samples),
                (byte)((covered * 255 + half) / samples));
        }

        /// <summary>
        /// Distance from a shape-space point to the rectangle's outline: positive inside, negative
        /// outside. Outside it is the euclidean distance, which is what rounds the SVG's
        /// <c>stroke-linejoin="round"</c> corners; inside it is the distance to the nearest edge.
        /// </summary>
        private static float SignedDistanceToOutline(float shapeX, float shapeY)
        {
            if (shapeX >= 0f && shapeX <= RectWidth && shapeY >= 0f && shapeY <= RectHeight)
            {
                return Mathf.Min(Mathf.Min(shapeX, RectWidth - shapeX),
                                 Mathf.Min(shapeY, RectHeight - shapeY));
            }

            float outsideX = Mathf.Max(0f, Mathf.Max(-shapeX, shapeX - RectWidth));
            float outsideY = Mathf.Max(0f, Mathf.Max(-shapeY, shapeY - RectHeight));
            return -Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY);
        }

        /// <summary>Where a shape-space point falls in the gradient, 0 at the centre and 1 at the rim.</summary>
        private static float GradientT(float shapeX, float shapeY)
        {
            float dx = (shapeX - GradientCenterX) / GradientRadiusX;
            float dy = (shapeY - GradientCenterY) / GradientRadiusY;
            float t = Mathf.Sqrt(dx * dx + dy * dy);
            return t > 1f ? 1f : t;
        }

        // ── Building ──────────────────────────────────────────────────────────

        /// <summary>
        /// Renders the whole button into a texture. <see cref="Sample"/> is the model;
        /// this is only the plumbing that puts it in the right row order, because a Unity
        /// texture's rows run bottom-up while <see cref="Sample"/>'s y runs down.
        /// </summary>
        public static Texture2D CreateTexture(bool active)
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                name = active ? "PipBuckTabButtonActive" : "PipBuckTabButtonNormal",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };

            var pixels = new Color32[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                int row = (Height - 1 - y) * Width;
                for (int x = 0; x < Width; x++)
                {
                    pixels[row + x] = Sample(x, y, active);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>
        /// The button as a sprite, for an <c>Image</c>. Used when the composition has no tab-button
        /// sprite — a tree that has not had <c>PFE/Art/Import Game Interface</c> run over it — so
        /// that the tabs still render as buttons rather than as nothing at all.
        /// </summary>
        public static Sprite CreateSprite(bool active)
        {
            Texture2D texture = CreateTexture(active);
            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, Width, Height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            sprite.name = texture.name;
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }
    }
}
