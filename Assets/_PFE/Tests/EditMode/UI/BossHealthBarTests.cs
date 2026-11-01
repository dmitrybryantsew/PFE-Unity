using NUnit.Framework;
using PFE.Entities.Enemies.Bosses;
using PFE.UI.HUD;
using UnityEngine;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins the boss health bar — the port of <c>vis.hpbarboss</c> (<c>GUI.hpBarBoss</c>,
    /// <c>GUI.as:1693-1705</c>). Three separate claims, each of which had a plausible wrong answer:
    ///
    /// <list type="number">
    /// <item><description><b>Where it sits.</b> The Oracle only ever writes <c>hpbarboss.x</c>
    /// (<c>GUI.as:324</c>); the y is the SWF's own placement of symbol 4360 inside <c>visualGUI</c>
    /// (4651) at <c>translateY = 1365</c> twips, less the 3.5 px the frame hangs above that point. The
    /// port drew it 20 px from the top for a while — a number nobody measured — so the value is pinned
    /// here rather than left to the next person's eye.</description></item>
    /// <item><description><b>How wide it is.</b> 401 × 13 frame, 399 × 11 fill: the fill is the frame
    /// less a 1 px stroke on each side. A typo in one of the four constants would silently give a bar
    /// whose fill pokes out of its frame.</description></item>
    /// <item><description><b>The fill shrinks about its centre.</b> <c>bar.scaleX</c> scales the SWF
    /// clip about the symbol origin, and the fill's shape spans −200..+200 about it, so the ends move
    /// inward together — measured on the owner's screenshot (frame 501..769, fill 535..734, both
    /// centred). A left-anchored fill is the obvious port and it is wrong.</description></item>
    /// </list>
    ///
    /// <para><b>What is deliberately NOT asserted.</b> Nothing that needs a live canvas —
    /// <c>BossHealthBarView.Build</c> creates GameObjects and <c>LateUpdate</c> only runs in play mode.
    /// The view's <i>decisions</i> are asserted through <see cref="BossHealthBarView.FillWidthFor"/> and
    /// the feed; its drawing belongs to the editor suite, the same split
    /// <c>HudWorldAnchorTests</c> documents for <c>WorldAnchoredHudElement.Apply</c>.</para>
    /// </summary>
    [TestFixture]
    public class BossHealthBarTests
    {
        [TearDown]
        public void TearDown()
        {
            // Static state, so a test that subscribes must not leak into the next one.
            BossHealthBarFeed.Reset();
        }

        // ── Placement ─────────────────────────────────────────────────────────

        [Test]
        public void TheBarSitsWhereTheSwfPlacesIt_NotTwentyPixelsFromTheTop()
        {
            // assets.swf, DefineSpriteTag 4651 (visualGUI): `hpbarboss` (4360) at (600.1, 68.25),
            // scale 1. Symbol 4360's own bounds are (-200.5, -3.5) 401x13, so the frame's top edge is
            // 68.25 - 3.5 = 64.75 below the top of the screen.
            Assert.AreEqual(64.75f, HudLayout.BossBarTopOffset, 0.0001f);
        }

        [Test]
        public void TheBarIsCentredHorizontally()
        {
            // GUI.as:324 `vis.hpbarboss.x = screenX / 2`.
            HudAnchor anchor = HudLayout.AnchorFor(HudSlot.BossHealthBar);

            Assert.AreEqual(0.5f, anchor.Point.x, 0.0001f, "not pinned to the horizontal centre");
            Assert.AreEqual(1f, anchor.Point.y, 0.0001f, "not pinned to the top edge");
        }

        [Test]
        public void TheBarHangsBelowTheTopEdge_NotAboveIt()
        {
            // A top-anchored pivot with a positive offset would push the bar off screen; the port's Y
            // runs up where the Oracle's runs down, so the sign is the whole contract.
            HudAnchor anchor = HudLayout.AnchorFor(HudSlot.BossHealthBar);

            Assert.AreEqual(-HudLayout.BossBarTopOffset, anchor.Offset.y, 0.0001f);
            Assert.AreEqual(0f, anchor.Offset.x, 0.0001f);
        }

        [Test]
        public void TheRowIsAuthoredData_NotAPortChoice()
        {
            // PortChoice means "nobody has measured this". This row has been measured, out of the SWF
            // matrix — a silent revert would let the next reader treat 64.75 as a taste value and
            // round it.
            Assert.AreEqual(HudAnchorSource.OracleAuthored, HudLayout.SourceFor(HudSlot.BossHealthBar));
        }

        // ── Geometry ──────────────────────────────────────────────────────────

        [Test]
        public void TheBarIsTheSymbolsOwnSize()
        {
            // Symbol 4360: bounds (-200.5, -3.5) 401x13.
            Assert.AreEqual(401f, BossHealthBarView.BarWidth);
            Assert.AreEqual(13f, BossHealthBarView.BarHeight);
        }

        [Test]
        public void TheFillIsTheFrameLessItsStroke()
        {
            // The frame is a 1 px stroke drawn OVER the fill (depth 3 over depth 1), so the fill's
            // visible extent is the frame less one pixel on every side.
            Assert.AreEqual(BossHealthBarView.BarWidth - 2f * BossHealthBarView.BorderThickness,
                BossHealthBarView.FillWidth);
            Assert.AreEqual(BossHealthBarView.BarHeight - 2f * BossHealthBarView.BorderThickness,
                BossHealthBarView.FillHeight);
        }

        [Test]
        public void TheFillShrinksAboutItsCentre_NotFromItsLeftEdge()
        {
            // The one-token difference that still looks like a working bar: `bar.scaleX` on the SWF
            // clip scales about the symbol origin, and the fill's shape spans -200..+200 about it, so
            // both ends move inward. Measured on the owner's screenshot: frame 501..769, fill
            // 535..734 — the gaps at each end are equal. A (0, 0.5) anchor drains from the left only
            // and would pass every other test in this file.
            Assert.AreEqual(0.5f, BossHealthBarView.FillAnchor.x, 0.0001f,
                "the fill is anchored to the frame's left edge, so it will drain the wrong way");
            Assert.AreEqual(0.5f, BossHealthBarView.FillAnchor.y, 0.0001f);
        }

        [Test]
        public void TheFillWidthIsProportionalToHealth()
        {
            Assert.AreEqual(0f, BossHealthBarView.FillWidthFor(0f), 0.0001f);
            Assert.AreEqual(BossHealthBarView.FillWidth, BossHealthBarView.FillWidthFor(1f), 0.0001f);
            Assert.AreEqual(BossHealthBarView.FillWidth * 0.5f, BossHealthBarView.FillWidthFor(0.5f), 0.0001f);
            Assert.AreEqual(BossHealthBarView.FillWidth * 0.745f,
                BossHealthBarView.FillWidthFor(0.745f), 0.0001f);
        }

        [Test]
        public void AFullerThanFullBar_DoesNotOverhangTheFrame()
        {
            // HP is clamped to the maximum everywhere upstream, so this is a guard rather than a case:
            // the Oracle would let `scaleX` run past 1 and paint over the frame's stroke.
            Assert.AreEqual(BossHealthBarView.FillWidth, BossHealthBarView.FillWidthFor(1.5f), 0.0001f);
            Assert.AreEqual(0f, BossHealthBarView.FillWidthFor(-0.2f), 0.0001f);
        }

        [Test]
        public void TheFrameIsTheImportedStrokeColour()
        {
            // Every border row and column of HUD_BossBar.png is exactly (255, 0, 0).
            Color expected = new Color32(255, 0, 0, 255);
            Assert.AreEqual(expected.r, BossHealthBarView.BorderColour.r, 0.0001f);
            Assert.AreEqual(expected.g, BossHealthBarView.BorderColour.g, 0.0001f);
            Assert.AreEqual(expected.b, BossHealthBarView.BorderColour.b, 0.0001f);
            Assert.AreEqual(expected.a, BossHealthBarView.BorderColour.a, 0.0001f);
        }

        // ── The feed: what a boss reports and what the bar is told ────────────

        [Test]
        public void ABossHealthReportReachesTheBar()
        {
            float received = -1f;
            BossHealthBarFeed.Reported += value => received = value;

            BossHealthBarFeed.Report(0.42f);

            Assert.AreEqual(0.42f, received, 0.0001f);
        }

        [Test]
        public void EveryReportReachesTheBar_NotJustTheFirst()
        {
            // The Oracle calls hpBarBoss() every frame, so a bar that only reacted to the first report
            // would freeze at whatever the boss had when the fight started.
            int count = 0;
            BossHealthBarFeed.Reported += _ => count++;

            BossHealthBarFeed.Report(1f);
            BossHealthBarFeed.Report(0.6f);
            BossHealthBarFeed.Report(0.2f);

            Assert.AreEqual(3, count);
        }

        [Test]
        public void ADeathReachesTheBar()
        {
            int cleared = 0;
            BossHealthBarFeed.Cleared += () => cleared++;

            BossHealthBarFeed.Clear();

            Assert.AreEqual(1, cleared);
        }

        [Test]
        public void ReportingWithNoBarBuilt_DoesNotThrow()
        {
            // A boss spawned into a scene with no HUD — an EditMode harness, a bare spawn — must not
            // fault on the seam. The tick calls this unconditionally.
            Assert.DoesNotThrow(() => BossHealthBarFeed.Report(0.5f));
            Assert.DoesNotThrow(() => BossHealthBarFeed.Clear());
        }

        [Test]
        public void ResetDropsEverySubscriber()
        {
            int hits = 0;
            BossHealthBarFeed.Reported += _ => hits++;
            BossHealthBarFeed.Cleared += () => hits++;

            BossHealthBarFeed.Reset();
            BossHealthBarFeed.Report(0.5f);
            BossHealthBarFeed.Clear();

            Assert.AreEqual(0, hits);
        }
    }
}
