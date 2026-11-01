using NUnit.Framework;
using PFE.Character;
using UnityEngine;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// The atlas's per-mip copy arithmetic — <see cref="CharacterQuadAtlas.ComputeMipCopy"/>.
    ///
    /// <para><b>Why this is a fixture.</b> The atlas is allocated with a mip chain once the character
    /// art imports with one, and every level of every region is copied by
    /// <c>Graphics.CopyTexture</c>. That call throws when either rectangle leaves its texture, and the
    /// throw is caught by the atlas build — so the visible result of a one-pixel arithmetic slip is
    /// "the quad character silently falls back to 92 per-texture materials", not an exception anyone
    /// sees. The bounds are pure arithmetic over sizes, so they are pinned here; the copy itself can
    /// only be checked by looking, which is the A/B window's job.</para>
    ///
    /// <para><b>What the sweep does and does not prove.</b>
    /// <see cref="EveryLevel_StaysInsideBothTextures"/> walks the real part sizes, placed by the real
    /// packer, at every level down to 1×1. For <i>legal</i> input that invariant holds by construction
    /// — floor is subadditive, so <c>(x &gt;&gt; L) + (w &gt;&gt; L) ≤ (x + w) &gt;&gt; L</c> — which is
    /// exactly why it is worth asserting: it says the shipped path cannot throw, for every part, at
    /// every level. It is <b>not</b> evidence that the clamps work, because legal input never reaches
    /// them. The clamps are pinned separately by
    /// <see cref="MalformedSourceRect_IsShrunkInsteadOfCopiedOutOfBounds"/> and
    /// <see cref="MalformedDestinationRect_IsShrunkInsteadOfCopiedOutOfBounds"/>, which feed the
    /// overflow that <see cref="CharacterQuadAtlas"/>'s own <c>RoundRect</c> can produce by rounding a
    /// sprite's origin and extent independently.</para>
    ///
    /// <para><b>Level 0 is the positive control.</b> It must reproduce the packer's rects verbatim,
    /// otherwise "the mips are right" would be a claim about a shifted atlas and every part would be
    /// off by the same delta.</para>
    /// </summary>
    [TestFixture]
    public class CharacterAtlasMipTests
    {
        const int AtlasSize = 1024;

        /// <summary>
        /// The character's real part sizes. At zoom 1 the PNG canvas <i>is</i> the symbol's SWF bounds,
        /// so a part's sprite rect is its whole texture — origin (0,0), extent equal to the canvas.
        /// These are sizes only; the atlas <i>destinations</i> come from the packer below, because a
        /// packed position is not a source rect and confusing the two is the bug this file guards.
        /// </summary>
        static readonly Vector2Int[] PartSizes =
        {
            new(7, 10), new(5, 7), new(9, 18), new(53, 85), new(86, 101), new(66, 87),
            new(114, 81), new(73, 148), new(51, 129), new(77, 103), new(124, 177),
            new(215, 178), new(160, 173)
        };

        static CharacterAtlasPacker PackedParts()
        {
            var packer = new CharacterAtlasPacker();
            foreach (Vector2Int size in PartSizes)
            {
                packer.Add(size.x, size.y);
            }

            Assert.IsTrue(
                packer.TryPack(AtlasSize, AtlasSize, CharacterQuadAtlas.DefaultPadding, out _, out _),
                "the real part sizes must fit the atlas, or this fixture is testing a fantasy");
            return packer;
        }

        [Test]
        public void Level0_IsThePackedRectVerbatim()
        {
            // Positive control: no halving, no clamping, no offset. A region at (2, 43) of size 53x85
            // inside a 256x256 source, placed at (300, 40) in a 1024² atlas.
            var source = new CharacterAtlasRect(2, 43, 53, 85);
            var destination = new CharacterAtlasRect(300, 40, 53, 85);

            CharacterAtlasMipCopy copy =
                CharacterQuadAtlas.ComputeMipCopy(256, 256, AtlasSize, source, destination, 0);

            Assert.IsTrue(copy.IsValid);
            Assert.AreEqual(0, copy.Mip);
            Assert.AreEqual(2, copy.SourceX);
            Assert.AreEqual(43, copy.SourceY);
            Assert.AreEqual(300, copy.DestinationX);
            Assert.AreEqual(40, copy.DestinationY);
            Assert.AreEqual(53, copy.Width);
            Assert.AreEqual(85, copy.Height);
        }

        [Test]
        public void DestinationOrigin_IsNeverUsedAsTheSourceWindow()
        {
            // The slip this test exists for: the packer MOVES a region, so its source rect and its
            // destination rect are the same size at unrelated origins. Taking the window from the
            // destination rect copies a neighbour's pixels — and stays in bounds, so nothing throws.
            var source = new CharacterAtlasRect(4, 8, 20, 30);
            var destination = new CharacterAtlasRect(600, 700, 20, 30);

            CharacterAtlasMipCopy copy =
                CharacterQuadAtlas.ComputeMipCopy(64, 64, AtlasSize, source, destination, 0);

            Assert.AreEqual(4, copy.SourceX, "the window must come from the source texture");
            Assert.AreEqual(8, copy.SourceY);
            Assert.AreEqual(600, copy.DestinationX, "the write must land where the packer placed it");
            Assert.AreEqual(700, copy.DestinationY);
        }

        [Test]
        public void EveryLevel_HalvesTheExtent()
        {
            // A 160x173 head: 160, 80, 40, 20, 10, 5, 2, 1 in x and 173, 86, 43, 21, 10, 5, 2, 1 in y.
            var source = new CharacterAtlasRect(0, 0, 160, 173);
            var destination = new CharacterAtlasRect(0, 0, 160, 173);

            int[] expectedWidth = { 160, 80, 40, 20, 10, 5, 2, 1 };
            int[] expectedHeight = { 173, 86, 43, 21, 10, 5, 2, 1 };

            for (int mip = 0; mip < expectedWidth.Length; mip++)
            {
                CharacterAtlasMipCopy copy =
                    CharacterQuadAtlas.ComputeMipCopy(256, 256, AtlasSize, source, destination, mip);

                Assert.IsTrue(copy.IsValid, $"mip {mip} should still be copyable");
                Assert.AreEqual(expectedWidth[mip], copy.Width, $"mip {mip} width");
                Assert.AreEqual(expectedHeight[mip], copy.Height, $"mip {mip} height");
            }
        }

        [Test]
        public void EveryLevel_StaysInsideBothTextures()
        {
            // The shipped path cannot throw: every real part, placed where the packer puts it, at every
            // level from 0 down to 1x1. Asserted over the whole set rather than by spot check, because
            // one bad part is enough to send the whole atlas build into its catch block.
            CharacterAtlasPacker packer = PackedParts();

            for (int i = 0; i < PartSizes.Length; i++)
            {
                int canvasWidth = PartSizes[i].x;
                int canvasHeight = PartSizes[i].y;
                var source = new CharacterAtlasRect(0, 0, canvasWidth, canvasHeight);
                CharacterAtlasRect destination = packer.GetRect(i);

                Assert.IsTrue(destination.IsValid, $"part {i} was never placed");
                Assert.LessOrEqual(source.X + source.Width, canvasWidth, "fixture rect exceeds its canvas");

                for (int mip = 0; mip <= 10; mip++)
                {
                    CharacterAtlasMipCopy copy = CharacterQuadAtlas.ComputeMipCopy(
                        canvasWidth, canvasHeight, AtlasSize, source, destination, mip);

                    if (!copy.IsValid)
                    {
                        continue;
                    }

                    int sourceLevelWidth = Mathf.Max(1, canvasWidth >> mip);
                    int sourceLevelHeight = Mathf.Max(1, canvasHeight >> mip);
                    int atlasLevelSize = Mathf.Max(1, AtlasSize >> mip);

                    Assert.GreaterOrEqual(copy.SourceX, 0, $"part {i} mip {mip}: source x negative");
                    Assert.GreaterOrEqual(copy.SourceY, 0, $"part {i} mip {mip}: source y negative");
                    Assert.LessOrEqual(copy.SourceX + copy.Width, sourceLevelWidth,
                        $"part {i} mip {mip}: source window runs past its own level ({sourceLevelWidth} px)");
                    Assert.LessOrEqual(copy.SourceY + copy.Height, sourceLevelHeight,
                        $"part {i} mip {mip}: source window runs past its own level ({sourceLevelHeight} px)");

                    Assert.GreaterOrEqual(copy.DestinationX, 0, $"part {i} mip {mip}: destination x negative");
                    Assert.GreaterOrEqual(copy.DestinationY, 0, $"part {i} mip {mip}: destination y negative");
                    Assert.LessOrEqual(copy.DestinationX + copy.Width, atlasLevelSize,
                        $"part {i} mip {mip}: write runs past the atlas's level ({atlasLevelSize} px)");
                    Assert.LessOrEqual(copy.DestinationY + copy.Height, atlasLevelSize,
                        $"part {i} mip {mip}: write runs past the atlas's level ({atlasLevelSize} px)");

                    Assert.GreaterOrEqual(copy.Width, 1, $"part {i} mip {mip}: zero-width copy reported valid");
                    Assert.GreaterOrEqual(copy.Height, 1, $"part {i} mip {mip}: zero-height copy reported valid");
                }
            }
        }

        [Test]
        public void MalformedSourceRect_IsShrunkInsteadOfCopiedOutOfBounds()
        {
            // A rect that overruns its own texture by 4 px. Not a legal sprite rect, but a reachable
            // one: RoundRect rounds the origin and the extent independently, so float noise can put
            // x + width one or two pixels past the texture. Without the shrink, the window is
            // 250..260 of a 256 px texture and CopyTexture throws.
            var source = new CharacterAtlasRect(250, 250, 10, 10);
            var destination = new CharacterAtlasRect(2, 2, 10, 10);

            CharacterAtlasMipCopy copy =
                CharacterQuadAtlas.ComputeMipCopy(256, 256, AtlasSize, source, destination, 0);

            Assert.IsTrue(copy.IsValid, "the in-bounds part of the rect is still copyable");
            Assert.AreEqual(250, copy.SourceX);
            Assert.AreEqual(6, copy.Width, "256 - 250 leaves 6 px, not the 10 the rect claims");
            Assert.AreEqual(6, copy.Height);
        }

        [Test]
        public void MalformedDestinationRect_IsShrunkInsteadOfCopiedOutOfBounds()
        {
            // The same overflow on the destination side: a region whose packed origin plus extent runs
            // past the atlas. Shrinking keeps the copy legal; pushing the origin back would shift the
            // part, which is a worse failure than a clipped edge.
            var source = new CharacterAtlasRect(0, 0, 10, 10);
            var destination = new CharacterAtlasRect(1020, 1020, 10, 10);

            CharacterAtlasMipCopy copy =
                CharacterQuadAtlas.ComputeMipCopy(256, 256, AtlasSize, source, destination, 0);

            Assert.IsTrue(copy.IsValid);
            Assert.AreEqual(1020, copy.DestinationX, "the origin must not be moved");
            Assert.AreEqual(4, copy.Width, "1024 - 1020 leaves 4 px");
            Assert.AreEqual(4, copy.Height);
        }

        [Test]
        public void RegionNearTheAtlasEdge_KeepsOneTexelAtDeepLevels()
        {
            // A 4x4 region whose destination sits at the very edge of a 256² atlas. At level 7 the
            // atlas is 2x2 and the region's origin halves to x = 1, so one texel is left — the extent
            // must shrink to it rather than claim the 1 px the level-0 rect would halve to.
            var source = new CharacterAtlasRect(250, 250, 4, 4);
            var destination = new CharacterAtlasRect(250, 250, 4, 4);

            CharacterAtlasMipCopy copy =
                CharacterQuadAtlas.ComputeMipCopy(256, 256, 256, source, destination, 7);

            Assert.IsTrue(copy.IsValid, "one texel is still a legal copy");
            Assert.AreEqual(1, copy.DestinationX);
            Assert.AreEqual(1, copy.DestinationY);
            Assert.AreEqual(1, copy.Width);
            Assert.AreEqual(1, copy.Height);
        }

        [Test]
        public void AtlasSmallerThanTheSource_StillClamps()
        {
            // A 64² atlas with a region taken from a 256² source. The destination level is the tighter
            // constraint, so the extent follows the atlas, not the source.
            var source = new CharacterAtlasRect(0, 0, 60, 60);
            var destination = new CharacterAtlasRect(2, 2, 60, 60);

            CharacterAtlasMipCopy copy =
                CharacterQuadAtlas.ComputeMipCopy(256, 256, 64, source, destination, 0);

            Assert.IsTrue(copy.IsValid);
            Assert.AreEqual(60, copy.Width);
            Assert.LessOrEqual(copy.DestinationX + copy.Width, 64);

            CharacterAtlasMipCopy deep =
                CharacterQuadAtlas.ComputeMipCopy(256, 256, 64, source, destination, 4);

            Assert.IsTrue(deep.IsValid);
            Assert.LessOrEqual(deep.DestinationX + deep.Width, 4, "level 4 of a 64² atlas is 4x4");
        }

        [Test]
        public void ImpossibleInputs_ReturnAnInvalidCopy()
        {
            // Every one of these would otherwise reach Graphics.CopyTexture as a malformed rectangle.
            var valid = new CharacterAtlasRect(0, 0, 8, 8);

            Assert.IsFalse(CharacterQuadAtlas.ComputeMipCopy(8, 8, 64, valid, valid, -1).IsValid,
                "a negative level is not a level");
            Assert.IsFalse(CharacterQuadAtlas.ComputeMipCopy(0, 8, 64, valid, valid, 0).IsValid,
                "a zero-width source has no window");
            Assert.IsFalse(CharacterQuadAtlas.ComputeMipCopy(8, 8, 0, valid, valid, 0).IsValid,
                "a zero-size atlas has nowhere to write");
            Assert.IsFalse(
                CharacterQuadAtlas.ComputeMipCopy(8, 8, 64, default, valid, 0).IsValid,
                "an unplaced region has no source window");
            Assert.IsFalse(
                CharacterQuadAtlas.ComputeMipCopy(8, 8, 64, valid, default, 0).IsValid,
                "an unplaced destination would write at the origin");
        }

        [Test]
        public void ASmallPartReachesItsLastLevelAsOneTexel()
        {
            // The 5x7 front-leg rect: 5, 2, 1 in x and 7, 3, 1 in y, so it is one texel from level 2
            // on. Note the chain depth is NOT set by this rect — it is set by the texture that holds it
            // (a 9x18 leg PNG has five levels), because a texture's mip count is a property of the
            // whole canvas. This test is about the rect's own extent, which is what the copy uses.
            var leg = new CharacterAtlasRect(2, 2, 5, 7);
            var placed = new CharacterAtlasRect(2, 2, 5, 7);

            CharacterAtlasMipCopy level0 = CharacterQuadAtlas.ComputeMipCopy(9, 18, AtlasSize, leg, placed, 0);
            CharacterAtlasMipCopy level1 = CharacterQuadAtlas.ComputeMipCopy(9, 18, AtlasSize, leg, placed, 1);
            CharacterAtlasMipCopy level2 = CharacterQuadAtlas.ComputeMipCopy(9, 18, AtlasSize, leg, placed, 2);

            Assert.AreEqual(5, level0.Width);
            Assert.AreEqual(7, level0.Height);
            Assert.AreEqual(2, level1.Width);
            Assert.AreEqual(3, level1.Height);
            Assert.AreEqual(1, level2.Width);
            Assert.AreEqual(1, level2.Height);
        }

        [Test]
        public void RegionInsideItsTexture_IsUnchangedByTheClamp()
        {
            // Positive control: the clamp must be a no-op for every legal sprite rect, or it would
            // silently crop art.
            var legal = new CharacterAtlasRect(4, 8, 40, 60);

            CharacterAtlasRect clamped = CharacterQuadAtlas.ClampToTexture(legal, 64, 128);

            Assert.AreEqual(legal.X, clamped.X);
            Assert.AreEqual(legal.Y, clamped.Y);
            Assert.AreEqual(legal.Width, clamped.Width);
            Assert.AreEqual(legal.Height, clamped.Height);
        }

        [Test]
        public void RegionOverhangingItsTexture_IsShrunkToTheEdge()
        {
            // What RoundRect can produce: origin rounded up while the extent was rounded from the
            // unrounded value, so the rect ends one pixel past the texture. Unclamped, this is the
            // rectangle that makes Graphics.CopyTexture throw and the whole atlas get abandoned.
            var overhanging = new CharacterAtlasRect(60, 124, 8, 8);

            CharacterAtlasRect clamped = CharacterQuadAtlas.ClampToTexture(overhanging, 64, 128);

            Assert.IsTrue(clamped.IsValid);
            Assert.AreEqual(60, clamped.X);
            Assert.AreEqual(124, clamped.Y);
            Assert.AreEqual(4, clamped.Width, "64 - 60 leaves 4 px");
            Assert.AreEqual(4, clamped.Height, "128 - 124 leaves 4 px");
        }

        [Test]
        public void RegionEntirelyOutsideItsTexture_IsRejected()
        {
            // A skip, not a zero-size copy: the caller counts it and the part keeps its own material.
            Assert.IsFalse(
                CharacterQuadAtlas.ClampToTexture(new CharacterAtlasRect(70, 4, 10, 10), 64, 128).IsValid);
            Assert.IsFalse(
                CharacterQuadAtlas.ClampToTexture(new CharacterAtlasRect(4, 140, 10, 10), 64, 128).IsValid);
            Assert.IsFalse(
                CharacterQuadAtlas.ClampToTexture(new CharacterAtlasRect(-20, 4, 10, 10), 64, 128).IsValid,
                "a rect entirely left of the texture has no intersection");
        }

        [Test]
        public void RegionPartlyLeftOfItsTexture_IsShiftedToZero()
        {
            var straddling = new CharacterAtlasRect(-6, 2, 10, 10);

            CharacterAtlasRect clamped = CharacterQuadAtlas.ClampToTexture(straddling, 64, 128);

            Assert.IsTrue(clamped.IsValid);
            Assert.AreEqual(0, clamped.X);
            Assert.AreEqual(4, clamped.Width);
        }

        /// <summary>
        /// The depth the atlas may copy is derived from the region's <b>dimensions</b>, not from
        /// <see cref="Texture.mipmapCount"/>.
        ///
        /// <para><b>Why the edges are written down.</b> The atlas build used to ask the texture
        /// property how deep it could go, and the editor log caught it over-reporting:
        /// <c>Graphics.CopyTexture called with invalid source mip level: got 4, have 4 mips</c>. That
        /// call logs and returns rather than throwing, so the region stayed recorded as packed while
        /// its deepest levels stayed transparent — and a part that shrank on screen sampled them and
        /// faded out. A level's extent is <c>max(w, h) &gt;&gt; mip</c>, so the arithmetic below is the
        /// only depth a copy can be held to.</para>
        /// </summary>
        [Test]
        public void MipDepth_IsTheNumberOfLevelsTheDimensionsAllow()
        {
            Assert.AreEqual(1, CharacterQuadAtlas.ComputeMipDepth(1, 1), "1x1 has level 0 only");
            Assert.AreEqual(2, CharacterQuadAtlas.ComputeMipDepth(2, 2), "2x2 -> 2, 1");
            Assert.AreEqual(3, CharacterQuadAtlas.ComputeMipDepth(5, 7), "7 -> 3 -> 1 is three levels");
            Assert.AreEqual(4, CharacterQuadAtlas.ComputeMipDepth(8, 8), "8 -> 4 -> 2 -> 1");
            Assert.AreEqual(5, CharacterQuadAtlas.ComputeMipDepth(21, 29), "29 -> 14 -> 7 -> 3 -> 1");
            Assert.AreEqual(7, CharacterQuadAtlas.ComputeMipDepth(86, 101), "101 -> 50 -> 25 -> 12 -> 6 -> 3 -> 1");
            Assert.AreEqual(11, CharacterQuadAtlas.ComputeMipDepth(1024, 1024), "the atlas itself");

            // The positive control: a non-square texture is governed by its longer side, and a zero
            // or negative dimension must not produce 0 or spin.
            Assert.AreEqual(
                CharacterQuadAtlas.ComputeMipDepth(200, 50),
                CharacterQuadAtlas.ComputeMipDepth(50, 200),
                "the longer side decides, whichever axis it is on");
            Assert.AreEqual(1, CharacterQuadAtlas.ComputeMipDepth(0, 0));
            Assert.AreEqual(1, CharacterQuadAtlas.ComputeMipDepth(-4, -4), "a negative size must not spin");
        }

        // ─── The pixel read-back ─────────────────────────────────
        //
        // Everything above checks rectangles. These check pixels, because a rectangle can be right
        // and the copy into it still land wrong — which is what the mip chain did on this driver.
        // `MeasureRegionFill` is pure and takes a Color32[], so the corruption can be reproduced
        // here exactly, with no GPU and no texture.

        static Color32[] Blank(int width, int height) => new Color32[width * height];

        static void Paint(Color32[] pixels, int width, int x0, int y0, int w, int h)
        {
            for (int y = y0; y < y0 + h; y++)
            {
                for (int x = x0; x < x0 + w; x++)
                {
                    pixels[y * width + x] = new Color32(255, 255, 255, 255);
                }
            }
        }

        /// <summary>
        /// A correct copy: the ink fills the rect edge to edge, because the rect is the frame's
        /// alpha-trim box. This is the positive control — if it did not read
        /// <see cref="CharacterAtlasRegionFill.Filled"/>, every other test here would be measuring a
        /// detector that fires on healthy art.
        /// </summary>
        [Test]
        public void ARegionWhoseInkFillsIt_IsFilled()
        {
            const int size = 32;
            Color32[] pixels = Blank(size, size);
            var rect = new CharacterAtlasRect(4, 4, 10, 10);
            Paint(pixels, size, rect.X, rect.Y, rect.Width, rect.Height);

            CharacterAtlasRegionFill fill =
                CharacterQuadAtlas.MeasureRegionFill(pixels, size, size, rect, out string missing);

            Assert.AreEqual(CharacterAtlasRegionFill.Filled, fill);
            Assert.AreEqual(string.Empty, missing, "a filled region has no missing edge to name");
        }

        /// <summary>
        /// The corruption signature, reproduced: the copy landed in the bottom-left corner of the rect
        /// and left the top and right edges empty. This is region 10 in the measured dump — a 114×75
        /// <c>korpus</c> whose ink box was <c>(0,38,56,70)</c> against a source box of
        /// <c>(0,1,113,73)</c>.
        /// </summary>
        [Test]
        public void ACornerFragment_IsPartialAndNamesTheEdges()
        {
            const int size = 32;
            Color32[] pixels = Blank(size, size);
            var rect = new CharacterAtlasRect(4, 4, 10, 10);

            // Ink in the lower-left quadrant only: x 4..8, y 4..8 of a rect spanning 4..14.
            Paint(pixels, size, rect.X, rect.Y, rect.Width / 2, rect.Height / 2);

            CharacterAtlasRegionFill fill =
                CharacterQuadAtlas.MeasureRegionFill(pixels, size, size, rect, out string missing);

            Assert.AreEqual(CharacterAtlasRegionFill.Partial, fill,
                "ink that stops short of an edge cannot come from a correct copy");
            Assert.AreEqual("top+right", missing,
                "the empty edges are what tells a reader which way the copy fell short");
        }

        /// <summary>
        /// A blank frame is not damage. <c>lwing/f001</c> really is 86×101 of pure transparent and
        /// <c>helm/base</c> really is a 1×1 placeholder, and the sources are <c>isReadable: 0</c> so the
        /// atlas cannot tell those from a region a bad copy left empty. That is why the verdict is
        /// <see cref="CharacterAtlasRegionFill.Empty"/> and not
        /// <see cref="CharacterAtlasRegionFill.Partial"/>: it gets counted, never warned about.
        /// </summary>
        [Test]
        public void ARegionWithNoInk_IsEmptyRatherThanPartial()
        {
            const int size = 32;
            Color32[] pixels = Blank(size, size);
            var rect = new CharacterAtlasRect(4, 4, 10, 10);

            CharacterAtlasRegionFill fill =
                CharacterQuadAtlas.MeasureRegionFill(pixels, size, size, rect, out string missing);

            Assert.AreEqual(CharacterAtlasRegionFill.Empty, fill);
            Assert.AreEqual(string.Empty, missing);
        }

        /// <summary>
        /// The band, pinned from both sides. Every one of the project's 330 part rects is a pixel or
        /// two wider than its alpha-trim ink, so an exact-edge test would flag 276 of them; with two
        /// pixels of gap allowed, none are flagged. Both halves are asserted so shrinking the constant
        /// to an exact-edge test (noisy) or growing it to swallow a real fragment (blind) fails here.
        ///
        /// <para>The gap is measured in pixels of <i>gap</i>, not in the constant's own units: three
        /// lines examine the edge and the two lines inside it, so a rect 2 px wider than its ink still
        /// reads as filled and one 3 px wider does not.</para>
        /// </summary>
        [Test]
        public void TheEdgeBand_ToleratesTwoPixelsOfGapAndNoMore()
        {
            const int size = 32;
            var rect = new CharacterAtlasRect(4, 4, 10, 10);

            // 2 px of gap on every side: still healthy.
            Color32[] slack = Blank(size, size);
            Paint(slack, size, rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4);
            Assert.AreEqual(
                CharacterAtlasRegionFill.Filled,
                CharacterQuadAtlas.MeasureRegionFill(slack, size, size, rect, out _),
                "two pixels of margin is ordinary art, not a failed copy");

            // 3 px: past the band, so it is a fragment.
            Color32[] gap = Blank(size, size);
            Paint(gap, size, rect.X + 3, rect.Y + 3, rect.Width - 6, rect.Height - 6);
            Assert.AreEqual(
                CharacterAtlasRegionFill.Partial,
                CharacterQuadAtlas.MeasureRegionFill(gap, size, size, rect, out _),
                "three pixels of margin is what a half-size copy looks like");
        }

        /// <summary>
        /// A rect that runs past the atlas must be measured on the part that exists, not read out of
        /// bounds. <see cref="CharacterQuadAtlas.ClampToTexture"/> normally prevents this, but the
        /// read-back runs on whatever the packer produced, so it cannot assume the clamp happened.
        /// </summary>
        [Test]
        public void ARegionOverhangingTheAtlas_IsMeasuredWithoutThrowing()
        {
            const int size = 32;
            Color32[] pixels = Blank(size, size);
            var rect = new CharacterAtlasRect(28, 28, 10, 10);
            Paint(pixels, size, 28, 28, 4, 4);

            CharacterAtlasRegionFill fill =
                CharacterQuadAtlas.MeasureRegionFill(pixels, size, size, rect, out _);

            Assert.AreEqual(CharacterAtlasRegionFill.Filled, fill,
                "the visible 4x4 of the rect is filled, so nothing is wrong with the copy");
        }

        /// <summary>
        /// Impossible input degrades to <see cref="CharacterAtlasRegionFill.Empty"/> — the quiet
        /// verdict — rather than to a warning. A diagnostic that fires when it cannot measure is a
        /// diagnostic that gets ignored.
        /// </summary>
        [Test]
        public void UnmeasurableInput_IsQuiet()
        {
            var valid = new CharacterAtlasRect(0, 0, 4, 4);

            Assert.AreEqual(CharacterAtlasRegionFill.Empty,
                CharacterQuadAtlas.MeasureRegionFill(null, 8, 8, valid, out _));
            Assert.AreEqual(CharacterAtlasRegionFill.Empty,
                CharacterQuadAtlas.MeasureRegionFill(Blank(8, 8), 0, 8, valid, out _));
            Assert.AreEqual(CharacterAtlasRegionFill.Empty,
                CharacterQuadAtlas.MeasureRegionFill(Blank(8, 8), 8, 8, default, out _));
            Assert.AreEqual(CharacterAtlasRegionFill.Empty,
                CharacterQuadAtlas.MeasureRegionFill(Blank(4, 4), 8, 8, valid, out _),
                "an array too small for the declared atlas is not measurable");
        }
    }
}
