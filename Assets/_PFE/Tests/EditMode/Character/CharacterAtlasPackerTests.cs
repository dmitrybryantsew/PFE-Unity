using NUnit.Framework;
using PFE.Character;
using UnityEngine;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// The atlas packer's placement rules.
    ///
    /// <para><b>Why this is a fixture and not a screenshot.</b> The packer is the only part of the
    /// atlas that can be wrong in a way that still draws: a rect placed one pixel too far left samples
    /// its neighbour's art, and a rect left at the origin samples nothing and the part disappears with
    /// no error in the console. Both are pure arithmetic over sizes, so both can be pinned here — the
    /// GPU half (<c>Graphics.CopyTexture</c>) can only be checked by looking, and that is the preview
    /// window's job.</para>
    ///
    /// <para><b>The overlap check is the one that matters.</b> Every other assertion here can pass
    /// while two parts share texels; the sweep in <see cref="PackedRects_NeverOverlap"/> is what makes
    /// "one atlas" a claim about distinct art rather than about a texture that happens to be full.</para>
    /// </summary>
    [TestFixture]
    public class CharacterAtlasPackerTests
    {
        const int Padding = CharacterQuadAtlas.DefaultPadding;

        static CharacterAtlasPacker Packed(out int usedWidth, out int usedHeight, params Vector2Int[] sizes)
        {
            var packer = new CharacterAtlasPacker();
            foreach (Vector2Int size in sizes)
            {
                packer.Add(size.x, size.y);
            }

            Assert.IsTrue(packer.TryPack(1024, 1024, Padding, out usedWidth, out usedHeight),
                "1024² must hold a hand-written list of part-sized rects.");
            return packer;
        }

        [Test]
        public void EmptyPacker_SucceedsAndPlacesNothing()
        {
            var packer = new CharacterAtlasPacker();

            Assert.IsTrue(packer.TryPack(64, 64, Padding, out int usedWidth, out int usedHeight));
            Assert.AreEqual(0, packer.Count);
            Assert.AreEqual(0, usedWidth);
            Assert.AreEqual(0, usedHeight);
        }

        [Test]
        public void SingleRect_StartsAtThePadding()
        {
            CharacterAtlasPacker packer = Packed(out _, out _, new Vector2Int(7, 10));
            CharacterAtlasRect rect = packer.GetRect(0);

            // Not at (0,0): bilinear sampling reads half a texel outside the rect, so the border has to
            // be empty or the part picks up whatever the atlas was cleared to.
            Assert.AreEqual(Padding, rect.X);
            Assert.AreEqual(Padding, rect.Y);
            Assert.AreEqual(7, rect.Width);
            Assert.AreEqual(10, rect.Height);
        }

        [Test]
        public void PackedRects_NeverOverlap()
        {
            // The real character: a 160x173 head down to a 5x7 front leg, plus the wings.
            CharacterAtlasPacker packer = Packed(out _, out _,
                new Vector2Int(7, 10), new Vector2Int(5, 7), new Vector2Int(9, 18), new Vector2Int(9, 18),
                new Vector2Int(53, 85), new Vector2Int(10, 21), new Vector2Int(11, 21),
                new Vector2Int(7, 7), new Vector2Int(86, 101), new Vector2Int(66, 87),
                new Vector2Int(114, 81), new Vector2Int(73, 148), new Vector2Int(51, 129),
                new Vector2Int(77, 103), new Vector2Int(124, 177), new Vector2Int(215, 178),
                new Vector2Int(160, 173), new Vector2Int(160, 173), new Vector2Int(102, 99),
                new Vector2Int(30, 69), new Vector2Int(68, 62), new Vector2Int(127, 82),
                new Vector2Int(26, 34), new Vector2Int(53, 59));

            for (int a = 0; a < packer.Count; a++)
            {
                CharacterAtlasRect first = packer.GetRect(a);
                Assert.IsTrue(first.IsValid, $"rect {a} was not placed");

                for (int b = a + 1; b < packer.Count; b++)
                {
                    CharacterAtlasRect second = packer.GetRect(b);
                    bool disjoint =
                        first.X + first.Width + Padding <= second.X ||
                        second.X + second.Width + Padding <= first.X ||
                        first.Y + first.Height + Padding <= second.Y ||
                        second.Y + second.Height + Padding <= first.Y;

                    Assert.IsTrue(disjoint,
                        $"rects {a} ({first.X},{first.Y} {first.Width}x{first.Height}) and " +
                        $"{b} ({second.X},{second.Y} {second.Width}x{second.Height}) touch or overlap " +
                        $"with less than {Padding} px between them");
                }
            }
        }

        [Test]
        public void PackedRects_StayInsideTheAtlas()
        {
            const int size = 1024;
            CharacterAtlasPacker packer = Packed(out int usedWidth, out int usedHeight,
                new Vector2Int(215, 178), new Vector2Int(160, 173), new Vector2Int(124, 177),
                new Vector2Int(114, 81), new Vector2Int(86, 101), new Vector2Int(5, 7));

            for (int i = 0; i < packer.Count; i++)
            {
                CharacterAtlasRect rect = packer.GetRect(i);
                Assert.GreaterOrEqual(rect.X, Padding);
                Assert.GreaterOrEqual(rect.Y, Padding);
                Assert.LessOrEqual(rect.X + rect.Width + Padding, size);
                Assert.LessOrEqual(rect.Y + rect.Height + Padding, size);
            }

            Assert.LessOrEqual(usedWidth, size);
            Assert.LessOrEqual(usedHeight, size);
        }

        [Test]
        public void RectTallerThanTheAtlas_FailsAndPlacesNothing()
        {
            var packer = new CharacterAtlasPacker();
            packer.Add(10, 10);
            packer.Add(10, 500);

            Assert.IsFalse(packer.TryPack(64, 64, Padding, out _, out _),
                "a 500 px rect cannot fit a 64 px atlas");

            // All-or-nothing: a partial pack would leave the caller believing every part is placed.
            Assert.IsFalse(packer.GetRect(0).IsValid);
            Assert.IsFalse(packer.GetRect(1).IsValid);
        }

        [Test]
        public void RectWiderThanTheAtlas_FailsAndPlacesNothing()
        {
            // The width half of the oversized guard, which the tall case above does NOT exercise: a
            // 500x10 rect passes the vertical shelf check (it is only 10 tall), so without an explicit
            // width guard it gets placed at x = padding and hangs 436 px outside the texture. Found by
            // mutation M5, which the tall case failed to kill.
            var packer = new CharacterAtlasPacker();
            packer.Add(500, 10);

            Assert.IsFalse(packer.TryPack(64, 64, Padding, out _, out _),
                "a 500 px wide rect cannot fit a 64 px atlas");
            Assert.IsFalse(packer.GetRect(0).IsValid);
        }

        [Test]
        public void ShelvesRunningOutOfHeight_FailsAndPlacesNothing()
        {
            // Ten 50x20 rects in a 64x64 atlas: each is legal on its own, one fits per shelf, and three
            // shelves is all the height there is. This is the case that reaches the "shelf is full"
            // return rather than the oversized guard — the only route by which a caller could observe a
            // partial pack. Found by mutation M4, which the tall case could not reach.
            var packer = new CharacterAtlasPacker();
            for (int i = 0; i < 10; i++)
            {
                packer.Add(50, 20);
            }

            Assert.IsFalse(packer.TryPack(64, 64, Padding, out _, out _),
                "ten 20 px shelves cannot fit 64 px of height");

            for (int i = 0; i < packer.Count; i++)
            {
                Assert.IsFalse(packer.GetRect(i).IsValid,
                    $"rect {i} was left placed by a failed pack");
            }
        }

        [Test]
        public void RectNeverSitsFlushAgainstTheRightEdge()
        {
            // Two rects that exactly fill a 64 px shelf if the wrap test ignores the trailing gap:
            // 2 + 50 + 2 = 54, then 54 + 10 = 64 <= 64, so a gap-less wrap test keeps them on one shelf
            // and the second one's right edge lands on the texture border with no margin. Bilinear
            // sampling then reads the clamped edge column instead of empty space.
            var packer = new CharacterAtlasPacker();
            packer.Add(50, 10);
            packer.Add(10, 10);

            Assert.IsTrue(packer.TryPack(64, 64, Padding, out _, out _));

            for (int i = 0; i < packer.Count; i++)
            {
                CharacterAtlasRect rect = packer.GetRect(i);
                Assert.LessOrEqual(rect.X + rect.Width + Padding, 64,
                    $"rect {i} sits {64 - (rect.X + rect.Width)} px from the right edge, want >= {Padding}");
            }
        }

        [Test]
        public void FailedPack_CanBeRetriedWithALargerAtlas()
        {
            var packer = new CharacterAtlasPacker();
            for (int i = 0; i < 8; i++)
            {
                packer.Add(100, 100);
            }

            Assert.IsFalse(packer.TryPack(128, 128, Padding, out _, out _));
            Assert.IsTrue(packer.TryPack(512, 512, Padding, out _, out _));

            for (int i = 0; i < packer.Count; i++)
            {
                Assert.IsTrue(packer.GetRect(i).IsValid, $"rect {i} missing after the successful retry");
            }
        }

        [Test]
        public void TallestRect_IsPlacedOnTheFirstShelf()
        {
            // Two rects too wide to share a shelf, added short-first, so insertion order and height
            // order give different layouts. This is the case the ordering exists for: a shelf is as
            // tall as its tallest member, so putting the 100 px rect first lets the 10 px rect sit
            // beside it for free, while the reverse wastes the first shelf on a 10 px band.
            //
            // Added:  (0) 100x10  then  (1) 100x100      atlas 128 wide
            // Tall-first: (1) at (2, 2), (0) wraps to y = 2 + 100 + 2 = 104
            // Insertion order would have put (0) at (2, 2) and (1) at y = 14.
            var packer = new CharacterAtlasPacker();
            packer.Add(100, 10);
            packer.Add(100, 100);

            Assert.IsTrue(packer.TryPack(128, 128, Padding, out _, out _));

            Assert.AreEqual(Padding, packer.GetRect(1).Y, "the tallest rect belongs on the first shelf");
            Assert.AreEqual(Padding, packer.GetRect(1).X);
            Assert.Greater(packer.GetRect(0).Y, Padding, "the short rect wrapped to the next shelf");
        }

        [Test]
        public void SameInput_ProducesTheSameLayout()
        {
            // Determinism is what lets the atlas be rebuilt (on a filter or atlas toggle) without the
            // character's parts appearing to move between builds.
            Vector2Int[] sizes =
            {
                new(160, 173), new(114, 81), new(7, 10), new(9, 18), new(86, 101), new(5, 7)
            };

            CharacterAtlasPacker first = Packed(out _, out _, sizes);
            CharacterAtlasPacker second = Packed(out _, out _, sizes);

            for (int i = 0; i < first.Count; i++)
            {
                Assert.AreEqual(first.GetRect(i).X, second.GetRect(i).X, $"rect {i} x drifted");
                Assert.AreEqual(first.GetRect(i).Y, second.GetRect(i).Y, $"rect {i} y drifted");
            }
        }

        [Test]
        public void ToUv_MapsCornersAndInterior()
        {
            // A rect covering the whole atlas is the identity window — the positive control that says
            // ToUv is not applying some extra offset.
            var full = new CharacterAtlasRect(0, 0, 512, 512);
            Assert.AreEqual(new Vector4(0f, 0f, 1f, 1f), full.ToUv(512, 512));

            // A quarter-tile at the origin. V is bottom-up, matching CharacterQuadGeometry.Build, so
            // the rect at y=0 is the BOTTOM of the texture, not the top.
            var quarter = new CharacterAtlasRect(0, 0, 256, 256);
            Assert.AreEqual(new Vector4(0f, 0f, 0.5f, 0.5f), quarter.ToUv(512, 512));

            // Offset interior rect: 64..320 in x, 128..384 in y.
            var inner = new CharacterAtlasRect(64, 128, 256, 256);
            Vector4 uv = inner.ToUv(512, 512);
            Assert.AreEqual(0.125f, uv.x, 1e-6f);
            Assert.AreEqual(0.25f, uv.y, 1e-6f);
            Assert.AreEqual(0.625f, uv.z, 1e-6f);
            Assert.AreEqual(0.75f, uv.w, 1e-6f);
        }
    }
}
