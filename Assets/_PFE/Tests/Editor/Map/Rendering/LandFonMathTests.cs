using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;

namespace PFE.Tests.Editor.Map.Rendering
{
    /// <summary>
    /// The land <c>fon</c> backdrop's fit rule — AS3 <c>Grafon.setFonSize</c>
    /// (<c>Grafon.as:327-355</c>) and <c>Grafon.drawFon</c>'s fallback (<c>Grafon.as:310-315</c>).
    ///
    /// <para><b>Why these are pure and the drawing is not.</b> The plane itself needs a live
    /// <c>Camera</c> and a live <c>SpriteRenderer</c>, so anything that constructs it dies in the
    /// offline wall with <c>SecurityException: ECall methods must be packaged into a system module</c>
    /// — the same wall <c>CampTurretFactionTests</c> documents. The fit arithmetic needs neither, and
    /// it is the half that can be wrong invisibly: a backdrop that is the wrong <i>size</i>, or one
    /// cropped from the wrong <i>corner</i>, still looks like a backdrop in a screenshot. Every test
    /// below is written against that pair of silent failures.</para>
    ///
    /// <para>The room size is 1920x1000 source pixels (<c>WorldConstants.ROOM_SIZE_PIXELS</c>,
    /// <c>48 x 25</c> tiles at <c>40</c>), which is also the literal pair <c>Grafon.rectX</c> /
    /// <c>rectY</c> carry at <c>Grafon.as:135,137</c>. The stage used throughout is the same
    /// 1920x1000, because that is what the port's camera shows: orthographic size 5 at
    /// <c>CameraFollow.pixelsPerUnit = 100</c> is exactly 1000 px tall.</para>
    /// </summary>
    public class LandFonMathTests
    {
        private static readonly Vector2 Stage = new Vector2(1920f, 1000f);
        private static readonly Vector2 Room = new Vector2(1920f, 1000f);

        /// <summary>A 16:9 piece of art, which is what most of the nine <c>fon*</c> exports are.</summary>
        private static readonly Vector2 Art16By9 = new Vector2(1920f, 1080f);

        // ── drawFon's fallback ────────────────────────────────────────────────────────────────────

        [Test]
        public void ResolveFonId_AnEmptyOrNullId_TakesFonDefault()
        {
            // AS3 Grafon.as:312-315 collapses both: `if(param2 == "" || param2 == null)`.
            Assert.AreEqual("fonDefault", LandFonMath.ResolveFonId(string.Empty),
                "An empty fon row is the oracle's fallback case, not an error.");
            Assert.AreEqual("fonDefault", LandFonMath.ResolveFonId(null),
                "A null fon id is the same case as an empty one.");

            // The literal itself, not just the constant, so renaming the constant cannot quietly
            // rename the art id the importer has to look for.
            Assert.AreEqual("fonDefault", LandFonMath.FallbackFonId);
        }

        [Test]
        public void ResolveFonId_ANamedId_IsPassedThroughUnchanged()
        {
            Assert.AreEqual("fonWay", LandFonMath.ResolveFonId("fonWay"));
            Assert.AreEqual("fonDarkClouds", LandFonMath.ResolveFonId("fonDarkClouds"));
        }

        // ── the room rect, which is NOT the land ──────────────────────────────────────────────────

        [Test]
        public void RoomRect_IsOneRoom_AndMatchesThePortsOwnRoomSize()
        {
            // The whole reason this matters: setFonSize's threshold is a ROOM, so a 3x3 land does not
            // raise it. If this ever drifts from WorldConstants, the branch test below is measuring
            // something other than what the oracle measures.
            Assert.AreEqual(WorldConstants.ROOM_SIZE_PIXELS, LandFonMath.RoomRectPixels,
                "Grafon.as:135,137 declare rectX/rectY as 1920/1000 — one room, not a land.");
            Assert.AreEqual(1920f, LandFonMath.RoomRectPixels.x);
            Assert.AreEqual(1000f, LandFonMath.RoomRectPixels.y);
        }

        // ── the unreachable first branch ──────────────────────────────────────────────────────────

        [Test]
        public void IsStretchedToRoomRect_IsStrictOnBothAxes()
        {
            // A stage exactly one room (what this port's camera produces) must NOT take the branch:
            // AS3 tests `param1 > rectX && param2 > rectY`, and 1000 > 1000 is false.
            Assert.IsFalse(LandFonMath.IsStretchedToRoomRect(Stage, Room),
                "A stage exactly one room tall is not `larger` than one room.");

            // One pixel over on ONE axis is still not enough — the oracle requires both.
            Assert.IsFalse(LandFonMath.IsStretchedToRoomRect(new Vector2(1921f, 1000f), Room),
                "Wider alone must not take the branch; the test is a conjunction.");
            Assert.IsFalse(LandFonMath.IsStretchedToRoomRect(new Vector2(1920f, 1001f), Room),
                "Taller alone must not take the branch.");

            // Both, and it does.
            Assert.IsTrue(LandFonMath.IsStretchedToRoomRect(new Vector2(1921f, 1001f), Room),
                "Larger on both axes is the branch AS3 takes (Grafon.as:332).");
        }

        // ── the cover fit ─────────────────────────────────────────────────────────────────────────

        [Test]
        public void CoverFit_WhenTheStageIsRelativelyWider_FitsTheWidthAndOverflowsTheHeight()
        {
            // 1920 >= 1000 * (1920/1080) = 1777.8 -> true, so the WIDTH is fitted.
            Vector2 fit = LandFonMath.ComputeCoverFit(Stage, Art16By9);

            Assert.AreEqual(1920f, fit.x, 0.001f, "The fitted axis takes the stage dimension exactly.");
            Assert.AreEqual(1080f, fit.y, 0.001f, "1920 / (1920/1080) is the art's own height.");
            Assert.Greater(fit.y, Stage.y,
                "Cover means overflow: the art is taller than the stage, so nothing shows the clear colour.");
        }

        [Test]
        public void CoverFit_WhenTheStageIsRelativelyTaller_FitsTheHeightAndOverflowsTheWidth()
        {
            // 1000 >= 1000 * 1.7778 = 1777.8 -> false, so the HEIGHT is fitted and the width overflows.
            var narrowStage = new Vector2(1000f, 1000f);
            Vector2 fit = LandFonMath.ComputeCoverFit(narrowStage, Art16By9);

            float artRatio = Art16By9.x / Art16By9.y;

            Assert.AreEqual(1000f, fit.y, 0.001f, "The fitted axis takes the stage dimension exactly.");
            Assert.AreEqual(1000f * artRatio, fit.x, 0.001f,
                "The unfitted axis is stageHeight * the ART's ratio -- 1777.8, not the art's own 1920 width.");
            Assert.Greater(fit.x, narrowStage.x, "Cover means overflow on the unfitted axis.");
        }

        [Test]
        public void CoverFit_NeverLeavesAGapOnEitherAxis_AndNeverDistortsTheRatio()
        {
            // The two properties that define "cover", asserted across shapes rather than at one point.
            // A fit that is merely *close* is the failure this catches: it leaves a sliver of the
            // camera's clear colour at one edge, which reads as a rendering artefact, not as a bug.
            //
            // Note which ratio is preserved: the ART's, not the stage's. A fit that matched the stage
            // ratio would be a stretch, and that is the bug this pins -- an earlier draft of this test
            // asserted the stage's ratio and was wrong.
            var stages = new[]
            {
                new Vector2(1920f, 1000f),   // this port's camera
                new Vector2(1280f, 720f),
                new Vector2(3840f, 2160f),
                new Vector2(1000f, 2000f),   // portrait, which the game never uses but the maths must not care
                new Vector2(2560f, 1080f)
            };

            var arts = new[]
            {
                new Vector2(1920f, 1080f),   // 16:9
                new Vector2(1920f, 1000f),   // 48:25
                new Vector2(1600f, 833f),    // fonClear
                new Vector2(1920f, 1276f),   // the tallest export
                new Vector2(276f, 276f)      // the 1:1 placeholder
            };

            for (int s = 0; s < stages.Length; s++)
            {
                for (int a = 0; a < arts.Length; a++)
                {
                    Vector2 fit = LandFonMath.ComputeCoverFit(stages[s], arts[a]);

                    Assert.GreaterOrEqual(fit.x, stages[s].x - 0.01f,
                        $"stage {stages[s]} art {arts[a]}: a gap on X shows the clear colour.");
                    Assert.GreaterOrEqual(fit.y, stages[s].y - 0.01f,
                        $"stage {stages[s]} art {arts[a]}: a gap on Y shows the clear colour.");

                    float artRatio = arts[a].x / arts[a].y;
                    float fitRatio = fit.x / fit.y;
                    Assert.AreEqual(artRatio, fitRatio, 0.0001f,
                        $"stage {stages[s]} art {arts[a]}: the ART's own ratio must survive.");

                    // One axis is fitted EXACTLY and the other overflows. A fit that overflows both is
                    // a stretch by another name and would crop art that AS3 shows.
                    bool xExact = Mathf.Abs(fit.x - stages[s].x) < 0.01f;
                    bool yExact = Mathf.Abs(fit.y - stages[s].y) < 0.01f;
                    Assert.IsTrue(xExact || yExact,
                        $"stage {stages[s]} art {arts[a]}: cover must fit one axis exactly.");
                }
            }
        }

        // ── the anchor ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void TopLeftAnchoredCentre_PutsTheArtsTopLeftAtTheStagesTopLeft()
        {
            // AS3 writes `this.visFon.x = this.visFon.y = 0` (Grafon.as:342), and a SWF-exported
            // display object registers at its own top-left, so the art's top-left lands on the
            // stage's. Reconstructed here as the invariant rather than as the formula.
            Vector2 fit = LandFonMath.ComputeCoverFit(Stage, Art16By9);
            Vector2 centre = LandFonMath.TopLeftAnchoredCentre(Stage, fit);

            Vector2 topLeftOfArt = new Vector2(centre.x - fit.x * 0.5f, centre.y + fit.y * 0.5f);
            Vector2 topLeftOfStage = new Vector2(-Stage.x * 0.5f, Stage.y * 0.5f);

            Assert.AreEqual(topLeftOfStage.x, topLeftOfArt.x, 0.001f,
                "The art's left edge must sit on the stage's left edge.");
            Assert.AreEqual(topLeftOfStage.y, topLeftOfArt.y, 0.001f,
                "The art's top edge must sit on the stage's top edge.");
        }

        [Test]
        public void TopLeftAnchored_CropsTheOverflowOffTheBottom_NotTheTop()
        {
            // The observable consequence, and the reason centring would be wrong: a 1080-tall piece on
            // a 1000-tall stage loses its bottom 80 px, not 40 off each end. Centring would give
            // centre.y == 0; top-left anchoring gives -40.
            Vector2 fit = LandFonMath.ComputeCoverFit(Stage, Art16By9);
            Vector2 centre = LandFonMath.TopLeftAnchoredCentre(Stage, fit);

            Assert.AreEqual(-40f, centre.y, 0.001f,
                "(stageHeight - fitHeight) / 2 = (1000 - 1080) / 2 = -40, i.e. the art hangs BELOW the stage.");
            Assert.AreEqual(0f, centre.x, 0.001f,
                "On the fitted axis there is no overflow, so the art is centred on it.");
        }

        // ── the scale ─────────────────────────────────────────────────────────────────────────────

        [Test]
        public void ComputeScale_TurnsTheSpriteIntoExactlyTheFitSize()
        {
            Vector2 fit = LandFonMath.ComputeCoverFit(Stage, Art16By9);
            Vector2 scale = LandFonMath.ComputeScale(fit, Art16By9);

            Assert.AreEqual(1f, scale.x, 0.001f);
            Assert.AreEqual(1f, scale.y, 0.001f);

            // And for art that actually has to be scaled.
            var smallArt = new Vector2(960f, 540f);
            Vector2 fit2 = LandFonMath.ComputeCoverFit(Stage, smallArt);
            Vector2 scale2 = LandFonMath.ComputeScale(fit2, smallArt);

            Assert.AreEqual(fit2.x, smallArt.x * scale2.x, 0.001f);
            Assert.AreEqual(fit2.y, smallArt.y * scale2.y, 0.001f);
            Assert.Greater(scale2.x, 1f, "A 960-wide piece on a 1920 stage has to grow.");
        }
    }
}
