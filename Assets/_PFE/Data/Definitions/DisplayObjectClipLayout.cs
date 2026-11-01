using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// One labelled section of a DisplayObject clip's timeline — the AS3 <c>osn</c> MovieClip a
    /// <c>&lt;vis vclass='visualX'/&gt;</c> unit is drawn from.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a segment and not a frame.</b> Flash labels mark where a section <i>starts</i>, and
    /// the section runs until the next label (or the end of the clip). The two shapes this data
    /// actually contains are:</para>
    /// <list type="bullet">
    /// <item><b>a timeline</b> — no labels at all, so the whole clip is one segment that plays and
    /// loops (every <c>bloat0..10</c>, 3 frames each; <c>turret0</c>, 12; <c>vortex</c>, 3).</item>
    /// <item><b>a state selector</b> — every segment is exactly one frame, because the frame's
    /// <i>child</i> MovieClip is the animation and the label only chooses which child is on stage.
    /// <c>visualBloodwing</c>'s <c>osn</c> is <c>stay / fly / attack / die</c> over four one-frame
    /// segments, and <c>UnitBat.as:72-103</c> is <c>vis.osn.gotoAndStop("fly")</c> and friends.</item>
    /// </list>
    /// <para>The distinction is <b>measured from the clip, not guessed from its labels</b>: a label
    /// called <c>stay</c> means "one state" only when the segment is one frame long.</para>
    /// </remarks>
    public readonly struct DisplayClipSegment
    {
        /// <summary>The AS3 frame label that opens the segment, or empty when the clip has none.</summary>
        public readonly string Label;

        /// <summary>1-based frame number the segment starts on — AS3's own frame numbering.</summary>
        public readonly int FirstFrame;

        /// <summary>How many timeline frames the segment covers.</summary>
        public readonly int FrameCount;

        public DisplayClipSegment(string label, int firstFrame, int frameCount)
        {
            Label = label ?? string.Empty;
            FirstFrame = firstFrame;
            FrameCount = frameCount;
        }

        /// <summary>True when the segment is a single pose whose child supplies the animation.</summary>
        public bool IsSingleFrame => FrameCount == 1;
    }

    /// <summary>
    /// The arithmetic behind importing a unit's DisplayObject clip — the split, the canvas, the pixel
    /// compositing and the downscale — with no asset pipeline in it.
    ///
    /// <para><b>Why this is separate from the importer.</b> <c>UnitSpriteImporter</c> lives in
    /// <c>PFE.Editor</c>, which <c>PFE.Tests</c> does not reference, so anything decided there can only
    /// be checked by running Unity. Every rule here is the kind that fails <i>silently and plausibly</i>
    /// — an off-by-one segment boundary still yields frames, a paste offset that ignores the ink origin
    /// still yields a picture, a box filter that forgets to weight by alpha still yields a sprite — so
    /// they are pure functions over <see cref="Color32"/> arrays and are tested directly. Same split,
    /// and the same reason, as <see cref="UnitSheetLayout"/> and <c>UnitVulnerabilityParser</c>.</para>
    ///
    /// <para><b>The canvas is the OUTER symbol's frame, and that is what keeps the change invisible at
    /// draw time.</b> A unit is drawn with <c>vis.x = X; vis.y = Y</c> at scale 1
    /// (<c>Unit.as:2969-2977</c>), so the picture's size on screen is the imported PNG's pixel size. The
    /// importer has always written the outer symbol's frame — <c>visualBloodwing</c> is a 1-frame
    /// container over its <c>osn</c>, and the container's frame is the <c>osn</c> canvas scaled by the
    /// placement matrix (<c>0.287</c> for the bloodwing, <c>1.0</c> for every bloat). Writing the clip's
    /// frames into that same canvas means the sprite keeps the size, the pivot convention and the
    /// anchor rule it already had, and only its <i>contents</i> start moving.</para>
    /// </summary>
    public static class DisplayObjectClipLayout
    {
        /// <summary>
        /// Alpha above which a pixel counts as ink when the paste offset is measured.
        ///
        /// <para>Not zero: FFDec's PNGs carry a halo of near-transparent pixels around every edge
        /// (the exporter antialiases against the canvas), and a strict <c>&gt; 0</c> test would take the
        /// halo's outermost speck as the origin. 8/255 is below anything the eye resolves at these
        /// sprite sizes and well above the halo.</para>
        /// </summary>
        public const int InkThreshold = 8;

        /// <summary>
        /// Split a clip's per-frame labels into segments.
        /// </summary>
        /// <param name="labelByFrame">
        /// Frame number → label, 1-based, empty string for a frame with no label. Shorter than
        /// <paramref name="frameCount"/> is treated as "no label from there on".
        /// </param>
        /// <param name="frameCount">The clip's length in frames.</param>
        /// <remarks>
        /// An unlabelled clip yields <b>one</b> segment covering every frame — which is the whole point:
        /// a 3-frame bloat clip is one looping animation, not three states. A labelled clip yields one
        /// segment per label, and the last one runs to the end of the clip.
        /// </remarks>
        public static DisplayClipSegment[] Segments(IReadOnlyList<string> labelByFrame, int frameCount)
        {
            if (frameCount <= 0)
            {
                return Array.Empty<DisplayClipSegment>();
            }

            // The frames that OPEN a segment: frame 1 always, plus every labelled frame.
            var starts = new List<int> { 1 };
            for (int frame = 1; frame <= frameCount; frame++)
            {
                if (frame > 1 && LabelAt(labelByFrame, frame).Length > 0)
                {
                    starts.Add(frame);
                }
            }

            var segments = new DisplayClipSegment[starts.Count];
            for (int i = 0; i < starts.Count; i++)
            {
                int first = starts[i];
                int last = i + 1 < starts.Count ? starts[i + 1] - 1 : frameCount;
                segments[i] = new DisplayClipSegment(LabelAt(labelByFrame, first), first, last - first + 1);
            }

            return segments;
        }

        static string LabelAt(IReadOnlyList<string> labelByFrame, int frame)
        {
            if (labelByFrame == null || frame < 1 || frame > labelByFrame.Count)
            {
                return string.Empty;
            }

            return labelByFrame[frame - 1] ?? string.Empty;
        }

        /// <summary>
        /// True when every segment is a single frame — AS3's "the label picks a child clip" shape,
        /// <c>UnitBat.as:72-103</c>.
        ///
        /// <para>Only this shape is imported as a set of states. A clip whose segments are several
        /// frames long (<c>visualVendor</c>: <c>stay</c> 1, <c>move1</c> 2..50, <c>move2</c> 51..96) is
        /// a timeline with section markers, and nothing in the oracle calls
        /// <c>gotoAndStop("move1")</c> — the clip simply plays. Reading those markers as states would
        /// freeze a shopkeeper on its first frame, so the importer leaves such a unit on the frame it
        /// already has and says so.</para>
        /// </summary>
        public static bool IsStateSelector(DisplayClipSegment[] segments)
        {
            if (segments == null || segments.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < segments.Length; i++)
            {
                if (!segments[i].IsSingleFrame)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The AS3 animation id a segment's label names.
        ///
        /// <para>An empty label means "the clip has no states", and the whole clip is the unit's resting
        /// animation — AS3's <c>animState</c> starts at <c>"stay"</c> (<c>Unit.as:2860</c>), so that is
        /// what an unlabelled clip is filed under. A label that names no state (<c>move1</c>,
        /// <c>wake</c>, <c>opened</c>) is returned unchanged and the caller reports it; it is
        /// <b>not</b> coerced onto a nearby id.</para>
        /// </summary>
        public static string As3IdForLabel(string label)
        {
            return string.IsNullOrEmpty(label) ? "stay" : label;
        }

        /// <summary>
        /// Where a frame's first ink sits, or <c>(-1,-1)</c> for a frame with no ink at all.
        /// </summary>
        /// <remarks>
        /// <para><b>This is how a child clip is placed inside its parent's canvas, and it is measured
        /// rather than derived.</b> The obvious derivation — SWF placement matrix plus the child's
        /// symbol bounds — is wrong here: a <c>DefineShape</c> RECT covers fills only, while the render
        /// includes strokes, so the numbers do not line up (the bloodwing's <c>osn</c> computes to
        /// 374x371 and exports as 350x373). Measuring the ink instead needs nothing but the two PNGs,
        /// and it is <i>checkable</i>: pasting the child at the offset it yields reproduces the parent's
        /// own render of that frame to within antialiasing — residual 0.2-2.4 of 255 across every
        /// <c>osn</c> state measured on 2026-10-28, and the brute-force optimum is the same pixel.</para>
        ///
        /// <para>Rows are whatever order the caller's pixel buffer is in. Unity's
        /// <c>GetPixels32</c> is bottom-up and a PNG is top-down, but the offset is only ever used
        /// against a buffer in the <b>same</b> space, so the convention cancels and nothing here needs
        /// to know which end is up.</para>
        /// </remarks>
        public static Vector2Int InkTopLeft(Color32[] pixels, int width, int height)
        {
            if (pixels == null || width <= 0 || height <= 0 || pixels.Length < width * height)
            {
                return new Vector2Int(-1, -1);
            }

            int minX = int.MaxValue;
            int minY = int.MaxValue;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (pixels[row + x].a <= InkThreshold)
                    {
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;

                    // The row's first ink is all this row can contribute to minX.
                    break;
                }
            }

            return minX == int.MaxValue ? new Vector2Int(-1, -1) : new Vector2Int(minX, minY);
        }

        /// <summary>
        /// Draw <paramref name="source"/> over <paramref name="destination"/> at an offset, straight
        /// alpha, clipping at the destination's edges.
        /// </summary>
        /// <remarks>
        /// A clip's state frame is <b>one</b> child drawn on empty canvas, so "paste" and "composite"
        /// are the same operation here — there is no second layer to blend against, and no colour
        /// transform in any of the clips this importer reads.
        /// </remarks>
        public static void PasteOver(Color32[] destination, int destinationWidth, int destinationHeight,
                                     Color32[] source, int sourceWidth, int sourceHeight,
                                     int offsetX, int offsetY)
        {
            if (destination == null || source == null ||
                destinationWidth <= 0 || destinationHeight <= 0 ||
                sourceWidth <= 0 || sourceHeight <= 0 ||
                destination.Length < destinationWidth * destinationHeight ||
                source.Length < sourceWidth * sourceHeight)
            {
                return;
            }

            int sourceStartX = Math.Max(0, -offsetX);
            int sourceStartY = Math.Max(0, -offsetY);
            int destinationStartX = Math.Max(0, offsetX);
            int destinationStartY = Math.Max(0, offsetY);

            int columns = Math.Min(sourceWidth - sourceStartX, destinationWidth - destinationStartX);
            int rows = Math.Min(sourceHeight - sourceStartY, destinationHeight - destinationStartY);
            if (columns <= 0 || rows <= 0)
            {
                return;
            }

            for (int y = 0; y < rows; y++)
            {
                int sourceRow = (sourceStartY + y) * sourceWidth + sourceStartX;
                int destinationRow = (destinationStartY + y) * destinationWidth + destinationStartX;

                for (int x = 0; x < columns; x++)
                {
                    Color32 src = source[sourceRow + x];
                    if (src.a == 0)
                    {
                        continue;
                    }

                    if (src.a == 255)
                    {
                        destination[destinationRow + x] = src;
                        continue;
                    }

                    Color32 dst = destination[destinationRow + x];
                    float a = src.a / 255f;
                    float inverse = 1f - a;
                    int alpha = src.a + Mathf.RoundToInt(dst.a * inverse);

                    destination[destinationRow + x] = new Color32(
                        (byte)(src.r * a + dst.r * inverse),
                        (byte)(src.g * a + dst.g * inverse),
                        (byte)(src.b * a + dst.b * inverse),
                        (byte)Mathf.Clamp(alpha, 0, 255));
                }
            }
        }

        /// <summary>
        /// Box-filter <paramref name="source"/> down to <paramref name="width"/> x
        /// <paramref name="height"/>, weighting colour by alpha.
        /// </summary>
        /// <remarks>
        /// <para><b>Alpha-weighted, not a plain average, and that is the whole point.</b> A sprite frame
        /// is mostly transparent canvas around a small subject: averaging straight RGB pulls every edge
        /// pixel towards whatever colour the transparent pixels happen to carry (usually black), which
        /// draws a dark fringe around the subject — the "black outline" artefact. Weighting each sample
        /// by its own alpha and dividing by the total weight is the correct resample of premultiplied
        /// colour, and it leaves fully transparent blocks transparent instead of grey.</para>
        ///
        /// <para>A box filter rather than bilinear because the ratio here is not gentle: the bloodwing's
        /// <c>osn</c> canvas is 350x373 drawn at 0.287, a 3.5x reduction, where point sampling drops
        /// whole wings. It also cannot ring, which matters for art that is 1-bit black outline over
        /// flat colour.</para>
        /// </remarks>
        public static Color32[] Downscale(Color32[] source, int sourceWidth, int sourceHeight,
                                          int width, int height)
        {
            if (source == null || sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0 ||
                source.Length < sourceWidth * sourceHeight)
            {
                return Array.Empty<Color32>();
            }

            if (width == sourceWidth && height == sourceHeight)
            {
                var copy = new Color32[source.Length];
                Array.Copy(source, copy, source.Length);
                return copy;
            }

            var result = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                int y0 = (int)((long)y * sourceHeight / height);
                int y1 = (int)((long)(y + 1) * sourceHeight / height);
                if (y1 <= y0) y1 = Math.Min(sourceHeight, y0 + 1);

                for (int x = 0; x < width; x++)
                {
                    int x0 = (int)((long)x * sourceWidth / width);
                    int x1 = (int)((long)(x + 1) * sourceWidth / width);
                    if (x1 <= x0) x1 = Math.Min(sourceWidth, x0 + 1);

                    float red = 0f, green = 0f, blue = 0f, alphaSum = 0f;
                    int samples = 0;

                    for (int sy = y0; sy < y1; sy++)
                    {
                        int row = sy * sourceWidth;
                        for (int sx = x0; sx < x1; sx++)
                        {
                            Color32 pixel = source[row + sx];
                            float a = pixel.a / 255f;
                            red += pixel.r * a;
                            green += pixel.g * a;
                            blue += pixel.b * a;
                            alphaSum += a;
                            samples++;
                        }
                    }

                    if (samples == 0 || alphaSum <= 0f)
                    {
                        result[y * width + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    result[y * width + x] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(red / alphaSum), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(green / alphaSum), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(blue / alphaSum), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(alphaSum / samples * 255f), 0, 255));
                }
            }

            return result;
        }
    }
}
