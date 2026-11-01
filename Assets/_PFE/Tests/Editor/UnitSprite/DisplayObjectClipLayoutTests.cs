using NUnit.Framework;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.Editor.UnitSprite
{
    /// <summary>
    /// Guards <see cref="DisplayObjectClipLayout"/> — the split, canvas, compositing and downscale rules
    /// that decide what a <c>vclass</c> unit's animation frames look like.
    ///
    /// <para><b>Why every rule here is tested rather than eyeballed.</b> Each one fails
    /// <i>silently and plausibly</i>: an off-by-one segment boundary still produces frames, a paste
    /// offset that ignores the ink origin still produces a picture, a box filter that forgets to weight
    /// by alpha still produces a sprite. The result of any of them is "the bloat animates, badly", which
    /// is indistinguishable from "the bloat animates" unless the numbers are pinned. The importer itself
    /// lives in <c>PFE.Editor</c>, which this assembly cannot reference, so the arithmetic was moved here
    /// to be checkable at all.</para>
    ///
    /// <para>The two real clips the assertions are shaped after: <c>bloat1</c>'s <c>osn</c> is 3
    /// unlabelled frames (one looping animation), and <c>visualBloodwing</c>'s is <c>stay / fly / attack /
    /// die</c> over four one-frame frames whose children carry 1/6/1/20 frames.</para>
    /// </summary>
    public class DisplayObjectClipLayoutTests
    {
        // ── Segments ─────────────────────────────────────────────────────────────────────────────

        [Test]
        public void AnUnlabelledClip_IsOneSegmentOverEveryFrame()
        {
            // bloat1: three frames, no labels anywhere. This is the case that made "the bloat has no
            // animation" look like a missing animation rather than a missing state: the clip is one
            // looping sequence, so it has to be imported as ONE state with three frames, not as three
            // states of one frame.
            var labels = new[] { "", "", "" };

            DisplayClipSegment[] segments = DisplayObjectClipLayout.Segments(labels, 3);

            Assert.That(segments.Length, Is.EqualTo(1), "an unlabelled clip is one segment");
            Assert.That(segments[0].Label, Is.Empty);
            Assert.That(segments[0].FirstFrame, Is.EqualTo(1));
            Assert.That(segments[0].FrameCount, Is.EqualTo(3));
            Assert.That(segments[0].IsSingleFrame, Is.False);
            Assert.That(DisplayObjectClipLayout.IsStateSelector(segments), Is.False,
                "one three-frame segment is a timeline, not a state selector");
        }

        [Test]
        public void AFourLabelSelector_SplitsIntoFourOneFrameSegments()
        {
            // visualBloodwing's osn, exactly: the four labels sit on four consecutive frames, and each
            // frame holds one child clip. UnitBat.as:72-103 gotoAndStop()s on these names.
            var labels = new[] { "stay", "fly", "attack", "die" };

            DisplayClipSegment[] segments = DisplayObjectClipLayout.Segments(labels, 4);

            Assert.That(segments.Length, Is.EqualTo(4));
            Assert.That(segments[0].Label, Is.EqualTo("stay"));
            Assert.That(segments[1].Label, Is.EqualTo("fly"));
            Assert.That(segments[2].Label, Is.EqualTo("attack"));
            Assert.That(segments[3].Label, Is.EqualTo("die"));

            foreach (DisplayClipSegment segment in segments)
            {
                Assert.That(segment.FrameCount, Is.EqualTo(1), segment.Label + ": one frame each");
                Assert.That(segment.IsSingleFrame, Is.True, segment.Label);
            }

            Assert.That(DisplayObjectClipLayout.IsStateSelector(segments), Is.True);
        }

        [Test]
        public void ALabelOnFrameOne_DoesNotOpenASecondSegment()
        {
            // Frame 1 always opens a segment. A clip that ALSO labels frame 1 (every labelled clip in
            // the data does) must not produce an empty leading segment — that would shift every state's
            // first frame by one and silently drop the last frame of the clip.
            var labels = new[] { "stay", "", "fly", "", "" };

            DisplayClipSegment[] segments = DisplayObjectClipLayout.Segments(labels, 5);

            Assert.That(segments.Length, Is.EqualTo(2), "two labels, two segments");
            Assert.That(segments[0].Label, Is.EqualTo("stay"));
            Assert.That(segments[0].FirstFrame, Is.EqualTo(1));
            Assert.That(segments[0].FrameCount, Is.EqualTo(2), "stay covers 1..2");
            Assert.That(segments[1].Label, Is.EqualTo("fly"));
            Assert.That(segments[1].FirstFrame, Is.EqualTo(3));
            Assert.That(segments[1].FrameCount, Is.EqualTo(3), "the last segment runs to the end");
        }

        [Test]
        public void ATimelineWithSectionMarkers_IsNotAStateSelector()
        {
            // visualVendor: stay on 1, move1 on 2, move2 on 51, 96 frames. Nothing calls
            // gotoAndStop("move1") — the clip just plays — so importing these as states would freeze a
            // shopkeeper on its first frame. This is the shape the importer must refuse.
            var labels = new string[96];
            labels[0] = "stay";
            labels[1] = "move1";
            labels[50] = "move2";

            DisplayClipSegment[] segments = DisplayObjectClipLayout.Segments(labels, 96);

            Assert.That(segments.Length, Is.EqualTo(3));
            Assert.That(segments[0].FrameCount, Is.EqualTo(1));
            Assert.That(segments[1].FrameCount, Is.EqualTo(49), "move1 covers 2..50");
            Assert.That(segments[2].FrameCount, Is.EqualTo(46), "move2 covers 51..96");
            Assert.That(DisplayObjectClipLayout.IsStateSelector(segments), Is.False);
        }

        [Test]
        public void ALabelTableShorterThanTheClip_IsUnlabelledFromThereOn()
        {
            // The label table is sized from the SWF symbol's frame count and the PNG export can be
            // longer. Indexing past it must mean "no label", not an exception in the middle of an
            // import run.
            var labels = new[] { "stay" };

            DisplayClipSegment[] segments = DisplayObjectClipLayout.Segments(labels, 5);

            Assert.That(segments.Length, Is.EqualTo(1));
            Assert.That(segments[0].FrameCount, Is.EqualTo(5));
        }

        [Test]
        public void AZeroLengthClip_HasNoSegments()
        {
            Assert.That(DisplayObjectClipLayout.Segments(new[] { "stay" }, 0), Is.Empty);
            Assert.That(DisplayObjectClipLayout.Segments(null, 0), Is.Empty);
            Assert.That(DisplayObjectClipLayout.Segments(null, 4).Length, Is.EqualTo(1),
                "a null table is an unlabelled clip, not an empty one");
        }

        [Test]
        public void IsStateSelector_RejectsEmptyAndPartialInput()
        {
            Assert.That(DisplayObjectClipLayout.IsStateSelector(null), Is.False);
            Assert.That(DisplayObjectClipLayout.IsStateSelector(new DisplayClipSegment[0]), Is.False);

            // One multi-frame segment is enough to disqualify the clip — the mixed shape is a timeline
            // with a single-frame intro, which is still a timeline.
            var mixed = new[]
            {
                new DisplayClipSegment("stay", 1, 1),
                new DisplayClipSegment("move", 2, 10),
            };
            Assert.That(DisplayObjectClipLayout.IsStateSelector(mixed), Is.False);
        }

        // ── State ids ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void AnUnlabelledClip_IsFiledUnderStay()
        {
            // Unit.as:2860 initialises animState to "stay", so that is the id a looping clip has to
            // answer to or the animator finds no frames for the state the unit is actually in.
            Assert.That(DisplayObjectClipLayout.As3IdForLabel(null), Is.EqualTo("stay"));
            Assert.That(DisplayObjectClipLayout.As3IdForLabel(""), Is.EqualTo("stay"));
        }

        [Test]
        public void AnUnknownLabel_IsPassedThroughUnchanged()
        {
            // Not coerced onto a nearby id: the importer reports it, and a fabricated state would make
            // a unit appear to animate correctly while showing the wrong frames.
            Assert.That(DisplayObjectClipLayout.As3IdForLabel("move1"), Is.EqualTo("move1"));
            Assert.That(DisplayObjectClipLayout.As3IdForLabel("fly"), Is.EqualTo("fly"));
        }

        // ── Ink origin ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void InkTopLeft_IgnoresTheExportersHalo()
        {
            // FFDec antialiases every shape against the canvas, so the outermost pixels carry alpha 1-8.
            // Taking those as the origin shifts the child by a pixel or two, which is exactly the kind
            // of error that reads as "the sprite is slightly off" and gets blamed on the pivot.
            const int Width = 8;
            const int Height = 8;
            var pixels = new Color32[Width * Height];

            pixels[0] = new Color32(255, 0, 0, DisplayObjectClipLayout.InkThreshold);       // (0,0) halo
            pixels[2 * Width + 1] = new Color32(0, 0, 0, 4);                                // (1,2) halo
            pixels[5 * Width + 3] = new Color32(10, 20, 30, DisplayObjectClipLayout.InkThreshold + 1);

            Vector2Int ink = DisplayObjectClipLayout.InkTopLeft(pixels, Width, Height);

            Assert.That(ink.x, Is.EqualTo(3), "the first pixel above the threshold is the origin");
            Assert.That(ink.y, Is.EqualTo(5));
        }

        [Test]
        public void InkTopLeft_ReportsNoInkAsMinusOne()
        {
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, 0);
            }

            Assert.That(DisplayObjectClipLayout.InkTopLeft(pixels, 4, 4), Is.EqualTo(new Vector2Int(-1, -1)));
            Assert.That(DisplayObjectClipLayout.InkTopLeft(null, 4, 4), Is.EqualTo(new Vector2Int(-1, -1)));
        }

        [Test]
        public void InkTopLeft_FindsTheFirstColumnOfTheFirstInkedRow()
        {
            // minX must be the minimum over ALL rows, not the first row's — a subject whose lower rows
            // extend further left than its top row is the normal case for a sprite, and reading only the
            // first inked row would shift the paste.
            const int Width = 6;
            const int Height = 4;
            var pixels = new Color32[Width * Height];

            pixels[0 * Width + 4] = new Color32(0, 0, 0, 255);   // (4,0)
            pixels[2 * Width + 1] = new Color32(0, 0, 0, 255);   // (1,2) — further left, lower down

            Vector2Int ink = DisplayObjectClipLayout.InkTopLeft(pixels, Width, Height);

            Assert.That(ink.x, Is.EqualTo(1));
            Assert.That(ink.y, Is.EqualTo(0));
        }

        // ── Compositing ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void PasteOver_ClipsAtTheDestinationEdges()
        {
            // The child is often wider than its parent's canvas — the bloodwing's osn computes to
            // 374x371 and exports at 350x373 — so a paste with a negative offset is normal and must clip
            // rather than wrap or throw.
            const int Size = 4;
            var destination = new Color32[Size * Size];
            var source = new Color32[Size * Size];
            for (int i = 0; i < source.Length; i++)
            {
                source[i] = new Color32(255, 0, 0, 255);
            }

            DisplayObjectClipLayout.PasteOver(destination, Size, Size, source, Size, Size, -2, -2);

            Assert.That(destination[0].a, Is.EqualTo(255), "(0,0) received the source's (2,2)");
            Assert.That(destination[0].r, Is.EqualTo(255));
            Assert.That(destination[1 * Size + 1].a, Is.EqualTo(255), "(1,1) is the last covered pixel");
            Assert.That(destination[1 * Size + 2].a, Is.EqualTo(0), "(2,1) is outside the 2x2 overlap");
            Assert.That(destination[2 * Size + 0].a, Is.EqualTo(0), "(0,2) is outside the 2x2 overlap");
        }

        [Test]
        public void PasteOver_LeavesTransparentSourcePixelsAlone()
        {
            const int Size = 2;
            var destination = new Color32[Size * Size];
            destination[0] = new Color32(0, 0, 255, 255);
            var source = new Color32[Size * Size];   // all fully transparent

            DisplayObjectClipLayout.PasteOver(destination, Size, Size, source, Size, Size, 0, 0);

            Assert.That(destination[0], Is.EqualTo(new Color32(0, 0, 255, 255)),
                "a transparent source pixel must not erase what is underneath");
        }

        [Test]
        public void PasteOver_BlendsAHalfAlphaPixelOverAnOpaqueOne()
        {
            const int Size = 1;
            var destination = new[] { new Color32(0, 0, 0, 255) };
            var source = new[] { new Color32(255, 255, 255, 128) };

            DisplayObjectClipLayout.PasteOver(destination, Size, Size, source, Size, Size, 0, 0);

            Assert.That(destination[0].r, Is.EqualTo(128));
            Assert.That(destination[0].a, Is.EqualTo(255), "half alpha over opaque stays opaque");
        }

        // ── Downscale ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Downscale_WeightsColourByAlpha_SoTheCanvasDoesNotFringe()
        {
            // The black-outline artefact, as arithmetic. A 2x2 block holding ONE opaque red pixel and
            // three transparent black ones must average to red, not to a quarter-red (64,0,0) — a plain
            // average pulls every edge towards whatever colour the empty canvas carries, which is the
            // dark fringe that makes a downscaled sprite look outlined in black.
            var source = new[]
            {
                new Color32(255, 0, 0, 255),
                new Color32(0, 0, 0, 0),
                new Color32(0, 0, 0, 0),
                new Color32(0, 0, 0, 0),
            };

            Color32[] result = DisplayObjectClipLayout.Downscale(source, 2, 2, 1, 1);

            Assert.That(result.Length, Is.EqualTo(1));
            Assert.That(result[0].r, Is.EqualTo(255), "the colour is the inked pixel's, not the block's average");
            Assert.That(result[0].g, Is.EqualTo(0));
            Assert.That(result[0].b, Is.EqualTo(0));
            Assert.That(result[0].a, Is.EqualTo(64), "coverage is the mean alpha: 1 of 4 opaque");
        }

        [Test]
        public void Downscale_LeavesAFullyTransparentBlockTransparent()
        {
            // Otherwise the sprite gets a grey rectangle around the subject instead of nothing.
            var source = new Color32[16];
            for (int i = 0; i < source.Length; i++)
            {
                source[i] = new Color32(255, 255, 255, 0);
            }

            Color32[] result = DisplayObjectClipLayout.Downscale(source, 4, 4, 1, 1);

            Assert.That(result[0], Is.EqualTo(new Color32(0, 0, 0, 0)));
        }

        [Test]
        public void Downscale_AtTheSameSize_ReturnsACopy()
        {
            // The 1.0-scale case (every bloat) must not be a no-op that aliases the source: the caller
            // writes the result to a PNG and reuses the source buffer for the next frame.
            var source = new[] { new Color32(1, 2, 3, 255) };

            Color32[] result = DisplayObjectClipLayout.Downscale(source, 1, 1, 1, 1);

            Assert.That(result, Is.Not.SameAs(source));
            Assert.That(result[0], Is.EqualTo(new Color32(1, 2, 3, 255)));
        }

        [Test]
        public void Downscale_RejectsDegenerateInput()
        {
            Assert.That(DisplayObjectClipLayout.Downscale(null, 2, 2, 1, 1), Is.Empty);
            Assert.That(DisplayObjectClipLayout.Downscale(new Color32[4], 2, 2, 0, 1), Is.Empty);
            Assert.That(DisplayObjectClipLayout.Downscale(new Color32[2], 2, 2, 1, 1), Is.Empty,
                "a buffer shorter than width*height is refused rather than read past");
        }
    }
}
