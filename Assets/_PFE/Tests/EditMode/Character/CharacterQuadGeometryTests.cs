using NUnit.Framework;
using PFE.Character;
using UnityEngine;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// The quad renderer's arithmetic — registration, the full-rect quad, the TRS composition and the
    /// ambient number.
    ///
    /// <para><b>Why the numbers are literals.</b> Every row below is read off the imported assets
    /// (PNG size from the file, sprite rect from the <c>.meta</c>) and off
    /// <c>docs/RenderingPipeline_Research/07</c> §3, which is the table this code was written to
    /// reproduce. A test that recomputed the expectation from the same formula it is testing would
    /// pass for any formula; these do not.</para>
    ///
    /// <para><b>The untrimmed parts are the control.</b> The formula predicts exactly zero for a part
    /// whose sprite rect equals its PNG, and those parts measure exactly zero. Without that, a
    /// function that returned a constant would look plausible on the trimmed rows.</para>
    /// </summary>
    [TestFixture]
    public class CharacterQuadGeometryTests
    {
        const float Tolerance = 1e-3f;

        static CharacterQuadSource Source(
            float rectX,
            float rectY,
            float rectWidth,
            float rectHeight,
            float textureWidth,
            float textureHeight,
            float pixelsPerUnit = 100f)
        {
            return new CharacterQuadSource(
                rectX, rectY, rectWidth, rectHeight, textureWidth, textureHeight, pixelsPerUnit);
        }

        // ── PivotErrorPixels: the measured table ────────────────────────────────────────────────

        [Test]
        public void UntrimmedParts_ReportNoError_WhichIsTheControl()
        {
            // lwing: 86x101 PNG, sprite rect 86x101 — nothing was trimmed, so there is nothing to be
            // wrong. If this ever reads non-zero, the formula has started inventing numbers.
            CharacterQuadSource lwing = Source(0, 0, 86, 101, 86, 101);
            Vector2 error = CharacterQuadGeometry.PivotErrorPixels(lwing, new Vector2(0.695f, 0.239f));
            Assert.AreEqual(0f, error.x, Tolerance);
            Assert.AreEqual(0f, error.y, Tolerance);

            // sleg3 and pip are the other two untrimmed parts in the documented table.
            CharacterQuadSource sleg3 = Source(0, 0, 10, 21, 10, 21);
            error = CharacterQuadGeometry.PivotErrorPixels(sleg3, new Vector2(0.736f, 0.948f));
            Assert.AreEqual(0f, error.x, Tolerance);
            Assert.AreEqual(0f, error.y, Tolerance);

            CharacterQuadSource pip = Source(0, 0, 7, 7, 7, 7);
            error = CharacterQuadGeometry.PivotErrorPixels(pip, new Vector2(0.496f, 0.979f));
            Assert.AreEqual(0f, error.x, Tolerance);
            Assert.AreEqual(0f, error.y, Tolerance);
        }

        [Test]
        public void KorpusRow_ReproducesTheDocumentedError()
        {
            // PNG 114x81, rect (0,5,114,75), pivot (0.5,0.5) → documented (+0.0, +2.0).
            CharacterQuadSource korpus = Source(0, 5, 114, 75, 114, 81);
            Vector2 error = CharacterQuadGeometry.PivotErrorPixels(korpus, new Vector2(0.5f, 0.5f));
            Assert.AreEqual(0f, error.x, Tolerance);
            Assert.AreEqual(2f, error.y, Tolerance);
        }

        [Test]
        public void HornRow_ReproducesTheDocumentedError_WhichIsTheLargestOne()
        {
            // PNG 66x87, rect (38,13,28,18), pivot (0.876,0.660) → documented (+4.7, -32.5).
            // 38 - 0.876*(66-28) = 4.712 ; 13 - 0.660*(87-18) = -32.54
            CharacterQuadSource horn = Source(38, 13, 28, 18, 66, 87);
            Vector2 error = CharacterQuadGeometry.PivotErrorPixels(horn, new Vector2(0.876f, 0.660f));
            Assert.AreEqual(4.712f, error.x, 0.01f);
            Assert.AreEqual(-32.54f, error.y, 0.01f);
        }

        [Test]
        public void NeckRow_ReproducesTheDocumentedError()
        {
            // PNG 73x148, rect (2,5,62,121), pivot (0.982,0.410) → documented (-8.8, -6.1).
            CharacterQuadSource neck = Source(2, 5, 62, 121, 73, 148);
            Vector2 error = CharacterQuadGeometry.PivotErrorPixels(neck, new Vector2(0.982f, 0.410f));
            Assert.AreEqual(-8.802f, error.x, 0.01f);
            Assert.AreEqual(-6.07f, error.y, 0.01f);
        }

        [Test]
        public void ManeAndHeadRows_ReproduceTheDocumentedErrors()
        {
            // mane: PNG 51x129, rect (4,0,47,118), pivot (0.143,0.967) → (+3.4, -10.6).
            CharacterQuadSource mane = Source(4, 0, 47, 118, 51, 129);
            Vector2 maneError = CharacterQuadGeometry.PivotErrorPixels(mane, new Vector2(0.143f, 0.967f));
            Assert.AreEqual(3.428f, maneError.x, 0.01f);
            Assert.AreEqual(-10.637f, maneError.y, 0.01f);

            // head: PNG 127x82, rect (16,0,109,76), pivot (0.927,0.684) → (-0.7, -4.1).
            CharacterQuadSource head = Source(16, 0, 109, 76, 127, 82);
            Vector2 headError = CharacterQuadGeometry.PivotErrorPixels(head, new Vector2(0.927f, 0.684f));
            Assert.AreEqual(-0.686f, headError.x, 0.01f);
            Assert.AreEqual(-4.104f, headError.y, 0.01f);
        }

        [Test]
        public void ErrorIsExactlyTheDifferenceBetweenTheTwoRegistrationPoints()
        {
            // The definition of the measurement, not a second implementation of it: this pins that
            // PivotErrorPixels cannot drift from Build's two branches.
            CharacterQuadSource source = Source(38, 13, 28, 18, 66, 87);
            Vector2 pivot = new(0.876f, 0.660f);

            Vector2 shipped = CharacterQuadGeometry.RegistrationPointPixels(source, pivot, false);
            Vector2 compensated = CharacterQuadGeometry.RegistrationPointPixels(source, pivot, true);

            Assert.AreEqual(shipped.x - compensated.x, CharacterQuadGeometry.PivotErrorPixels(source, pivot).x, Tolerance);
            Assert.AreEqual(shipped.y - compensated.y, CharacterQuadGeometry.PivotErrorPixels(source, pivot).y, Tolerance);
        }

        // ── Build: the quad itself ─────────────────────────────────────────────────────────────

        [Test]
        public void CompensatedBuild_PlacesTheOriginAtTheFullPngPivot()
        {
            // korpus: the origin must land on pixel (57, 40.5) of the PNG, not on the centre of the
            // trimmed rect at (57, 42.5). That 2 px is the whole bug.
            CharacterQuadSource korpus = Source(0, 5, 114, 75, 114, 81);
            CharacterQuadLocal quad = CharacterQuadGeometry.Build(korpus, new Vector2(0.5f, 0.5f), true);

            Assert.AreEqual(-0.57f, quad.XMin, Tolerance);
            Assert.AreEqual(0.57f, quad.XMax, Tolerance);
            Assert.AreEqual(-0.355f, quad.YMin, Tolerance);   // (5 - 40.5) / 100
            Assert.AreEqual(0.395f, quad.YMax, Tolerance);    // (80 - 40.5) / 100
        }

        [Test]
        public void UncompensatedBuild_IsOffByExactlyTheError_InTheOppositeDirection()
        {
            CharacterQuadSource korpus = Source(0, 5, 114, 75, 114, 81);
            CharacterQuadLocal compensated = CharacterQuadGeometry.Build(korpus, new Vector2(0.5f, 0.5f), true);
            CharacterQuadLocal shipped = CharacterQuadGeometry.Build(korpus, new Vector2(0.5f, 0.5f), false);

            Vector2 error = CharacterQuadGeometry.PivotErrorPixels(korpus, new Vector2(0.5f, 0.5f));

            // NEGATED, and that is the fact rather than a sign convention. `PivotErrorPixels` is
            // `registrationShipped - registrationIntended`, and `Build` places the rect at
            // `rect - registration`. So a positive error (the shipped origin sits HIGHER in the PNG)
            // makes the art hang LOWER on screen: the quad's YMin moves DOWN by exactly that much.
            // This test asserted `+error` until the fixture was first executed; the X half had passed
            // vacuously because korpus is not trimmed horizontally, and a zero expectation cannot see a
            // sign error. See the row below, which cannot be vacuous.
            Assert.AreEqual(-error.y / 100f, shipped.YMin - compensated.YMin, Tolerance);

            // And the sign is not a coin flip: the shipped path is 2 px lower, i.e. its YMin is smaller.
            Assert.Less(shipped.YMin, compensated.YMin);
        }

        [Test]
        public void UncompensatedBuild_MovesHorizontallyByTheErrorToo()
        {
            // The horn is trimmed on the left (rect x = 38 of a 66 px PNG), so its error has a
            // non-zero X: 38 - 0.876*(66-28) = +4.712 px. A horizontally-untrimmed part like korpus
            // gives X = 0, and an assertion of `0 == 0` passes whatever the sign convention is — which
            // is exactly how the sign error above survived until the fixture was executed.
            CharacterQuadSource horn = Source(38, 13, 28, 18, 66, 87);
            CharacterQuadLocal compensated = CharacterQuadGeometry.Build(horn, new Vector2(0.876f, 0.660f), true);
            CharacterQuadLocal shipped = CharacterQuadGeometry.Build(horn, new Vector2(0.876f, 0.660f), false);

            Vector2 error = CharacterQuadGeometry.PivotErrorPixels(horn, new Vector2(0.876f, 0.660f));
            Assert.AreEqual(4.712f, error.x, 0.01f);

            Assert.AreEqual(-error.x / 100f, shipped.XMin - compensated.XMin, Tolerance);
            Assert.Less(shipped.XMin, compensated.XMin);
        }

        [Test]
        public void Build_DrawsTheWholeRect_NotTheAlphaContour()
        {
            // A FullRect quad's size is the sprite rect's size, always — the horn's rect is 28x18 px,
            // so the quad must be 0.28 x 0.18 units even though the alpha inside is a thin spike.
            CharacterQuadSource horn = Source(38, 13, 28, 18, 66, 87);
            CharacterQuadLocal quad = CharacterQuadGeometry.Build(horn, new Vector2(0.876f, 0.660f), true);

            Assert.AreEqual(0.28f, quad.Width, Tolerance);
            Assert.AreEqual(0.18f, quad.Height, Tolerance);
        }

        [Test]
        public void Build_MapsUvsToTheSpriteRectInsideTheFullTexture()
        {
            CharacterQuadSource korpus = Source(0, 5, 114, 75, 114, 81);
            CharacterQuadLocal quad = CharacterQuadGeometry.Build(korpus, new Vector2(0.5f, 0.5f), true);

            Assert.AreEqual(0f, quad.UMin, Tolerance);
            Assert.AreEqual(1f, quad.UMax, Tolerance);
            Assert.AreEqual(5f / 81f, quad.VMin, Tolerance);
            Assert.AreEqual(80f / 81f, quad.VMax, Tolerance);
        }

        [Test]
        public void Build_RejectsAnInvalidSource()
        {
            CharacterQuadLocal empty = CharacterQuadGeometry.Build(default, new Vector2(0.5f, 0.5f), true);
            Assert.AreEqual(0f, empty.Width, Tolerance);
            Assert.AreEqual(0f, empty.Height, Tolerance);

            // A zero PPU would divide by zero and produce infinities that render as nothing at all —
            // silently, which is the failure mode this guard exists for.
            CharacterQuadSource noPpu = Source(0, 0, 10, 10, 10, 10, 0f);
            Assert.IsFalse(noPpu.IsValid);
            Assert.AreEqual(0f, CharacterQuadGeometry.Build(noPpu, Vector2.one * 0.5f, true).Width, Tolerance);
        }

        // ── WriteCorners ───────────────────────────────────────────────────────────────────────

        static CharacterQuadLocal UnitQuad()
        {
            return new CharacterQuadLocal { XMin = -1f, XMax = 1f, YMin = -1f, YMax = 1f };
        }

        [Test]
        public void WriteCorners_WithNoRotationOrScale_IsTheQuadItself()
        {
            var buffer = new Vector3[CharacterQuadGeometry.VertexCount];
            CharacterQuadGeometry.WriteCorners(UnitQuad(), Vector2.zero, 0f, Vector2.one, buffer);

            // Corner order is bl, tl, tr, br — clockwise seen from the camera.
            Assert.AreEqual(-1f, buffer[0].x, Tolerance);
            Assert.AreEqual(-1f, buffer[0].y, Tolerance);
            Assert.AreEqual(-1f, buffer[1].x, Tolerance);
            Assert.AreEqual(1f, buffer[1].y, Tolerance);
            Assert.AreEqual(1f, buffer[2].x, Tolerance);
            Assert.AreEqual(1f, buffer[2].y, Tolerance);
            Assert.AreEqual(1f, buffer[3].x, Tolerance);
            Assert.AreEqual(-1f, buffer[3].y, Tolerance);
        }

        [Test]
        public void WriteCorners_RotatesAboutTheRegistrationPoint()
        {
            var buffer = new Vector3[CharacterQuadGeometry.VertexCount];
            CharacterQuadGeometry.WriteCorners(UnitQuad(), Vector2.zero, 90f, Vector2.one, buffer);

            // (bl) (-1,-1) rotated 90° CCW is (1,-1); (tr) (1,1) is (-1,1).
            Assert.AreEqual(1f, buffer[0].x, Tolerance);
            Assert.AreEqual(-1f, buffer[0].y, Tolerance);
            Assert.AreEqual(-1f, buffer[2].x, Tolerance);
            Assert.AreEqual(1f, buffer[2].y, Tolerance);
        }

        [Test]
        public void WriteCorners_ScalesBeforeRotating()
        {
            // Unity's TRS order is scale → rotate → translate. Scaling after the rotation gives a
            // different point here, so this discriminates the two orders rather than merely being
            // consistent with either.
            var buffer = new Vector3[CharacterQuadGeometry.VertexCount];
            CharacterQuadGeometry.WriteCorners(UnitQuad(), Vector2.zero, 90f, new Vector2(2f, 1f), buffer);

            // tl = (-1, 1) → scaled (-2, 1) → rotated 90° CCW → (-1, -2)
            Assert.AreEqual(-1f, buffer[1].x, Tolerance);
            Assert.AreEqual(-2f, buffer[1].y, Tolerance);
        }

        [Test]
        public void WriteCorners_TranslatesLast()
        {
            var buffer = new Vector3[CharacterQuadGeometry.VertexCount];
            CharacterQuadGeometry.WriteCorners(UnitQuad(), new Vector2(10f, -4f), 0f, Vector2.one, buffer);

            Assert.AreEqual(9f, buffer[0].x, Tolerance);
            Assert.AreEqual(-5f, buffer[0].y, Tolerance);
        }

        [Test]
        public void WriteCorners_RefusesAShortBuffer_InsteadOfThrowing()
        {
            // A pool slot whose buffer was resized is a plausible future edit; a silent no-op is a
            // better failure than an IndexOutOfRange in a per-frame path.
            var tooSmall = new Vector3[2];
            Assert.DoesNotThrow(() => CharacterQuadGeometry.WriteCorners(UnitQuad(), Vector2.zero, 0f, Vector2.one, tooSmall));
        }

        [Test]
        public void WriteUvs_FollowsTheCornerOrder()
        {
            var quad = new CharacterQuadLocal { UMin = 0.1f, UMax = 0.9f, VMin = 0.2f, VMax = 0.8f };
            var uvs = new Vector2[CharacterQuadGeometry.VertexCount];
            CharacterQuadGeometry.WriteUvs(quad, uvs);

            Assert.AreEqual(new Vector2(0.1f, 0.2f), uvs[0]); // bl
            Assert.AreEqual(new Vector2(0.1f, 0.8f), uvs[1]); // tl
            Assert.AreEqual(new Vector2(0.9f, 0.8f), uvs[2]); // tr
            Assert.AreEqual(new Vector2(0.9f, 0.2f), uvs[3]); // br
        }

        // ── Ambient ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Ambient_MatchesTheBackgroundTileMultiplier()
        {
            // RoomBackdropRenderer.ResolveTileDarknessMultiplier is 1 - clamp01((170 + d) / 255).
            // These are that formula's values, so the character and the tiles cannot drift apart.
            Assert.AreEqual(85f / 255f, CharacterRenderAmbient.FromDarkness(0), Tolerance);
            Assert.AreEqual(0f, CharacterRenderAmbient.FromDarkness(85), Tolerance);
            Assert.AreEqual(0f, CharacterRenderAmbient.FromDarkness(255), Tolerance);

            // Below the base darkness the room is brighter than the base curve, and the value rises
            // above 1/3 rather than clamping at it.
            Assert.AreEqual(1f - 70f / 255f, CharacterRenderAmbient.FromDarkness(-100), Tolerance);
        }
    }
}
