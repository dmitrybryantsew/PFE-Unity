using NUnit.Framework;
using PFE.UI.HUD;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins the two pure decisions behind the world-anchored interaction/reload UI:
    ///
    /// <list type="number">
    /// <item><description><b>Which elements are world-anchored.</b> The Oracle draws the reload bar on the
    /// player's own sprite (<c>UnitPlayer.as:338-340</c>, <c>:367</c>) and re-projects the hold bar and the
    /// interaction prompt onto their target every frame (<c>GUI.as:1302</c>, <c>:1461-1463</c>). Those three
    /// slots must stay tagged <see cref="HudAnchorSource.WorldAnchored"/> — a silent revert to
    /// <see cref="HudAnchorSource.PortChoice"/> would put them back on a fixed screen anchor, which is
    /// precisely the divergence this work removes, and nothing else in the suite would notice.</description></item>
    /// <item><description><b>The prompt's two-line composition</b> — <c>celObj.nazv</c> then the action text
    /// (<c>GUI.as:1286-1299</c>), with an empty half omitted.</description></item>
    /// </list>
    ///
    /// <para><b>What is deliberately NOT asserted.</b> The projection itself
    /// (<see cref="WorldAnchoredHudElement.Apply"/>) needs a <see cref="UnityEngine.RectTransform"/>, a
    /// <see cref="UnityEngine.Camera"/> and a real canvas, so it is an ECall in the offline runner and
    /// belongs to the editor suite. Asserting its <i>result</i> here would fail identically whether or not
    /// the code is right — the trap <c>HudLayoutTests</c> documents for its own <c>Apply_*</c> cases.</para>
    /// </summary>
    [TestFixture]
    public class HudWorldAnchorTests
    {
        // ── Provenance: the three elements are not screen furniture ────────────

        [Test]
        public void TheReloadBar_IsWorldAnchored_NotAScreenElement()
        {
            // UnitPlayer.as:338-340 `vis.addChild(this.reloadbar)`; :367 `reloadbar.y = -scY - 10`.
            Assert.AreEqual(HudAnchorSource.WorldAnchored,
                HudLayout.SourceFor(HudSlot.ReloadIndicator));
        }

        [Test]
        public void TheHoldBar_IsWorldAnchoredOntoItsTarget()
        {
            // GUI.as:1302 `cam.setKoord(this.pr_bar, this.gg.actionObj.owner.X, …)`.
            Assert.AreEqual(HudAnchorSource.WorldAnchored,
                HudLayout.SourceFor(HudSlot.HoldBar));
        }

        [Test]
        public void TheInteractionPrompt_IsWorldAnchoredOntoItsTarget()
        {
            // GUI.as:1461 `cam.setKoord(this.celobj, this.celObj.X, this.celObj.Y)`.
            Assert.AreEqual(HudAnchorSource.WorldAnchored,
                HudLayout.SourceFor(HudSlot.InteractionPrompt));
        }

        [Test]
        public void EveryWorldAnchoredSlot_StillHasATableRowAsAFallback()
        {
            // The scaler is not asked to register these, but a slot with no row would fall through to
            // the centred anchor if anything ever did place it — a silent top-left-of-nothing.
            foreach (HudSlot slot in new[]
                     { HudSlot.ReloadIndicator, HudSlot.HoldBar, HudSlot.InteractionPrompt })
            {
                Assert.IsTrue(HudLayout.HasAnchor(slot), $"no fallback row for {slot}");
                Assert.AreEqual(HudAnchorSource.WorldAnchored, HudLayout.SourceFor(slot));
            }
        }

        [Test]
        public void ScreenAnchoredElements_AreNotTaggedWorldAnchored()
        {
            // The control: the tag must not have swallowed the screen-anchored rows it sits beside.
            Assert.AreEqual(HudAnchorSource.OracleResizeScreen, HudLayout.SourceFor(HudSlot.WeaponBlock));
            Assert.AreEqual(HudAnchorSource.PortChoice, HudLayout.SourceFor(HudSlot.HealthBar));
        }

        // ── The prompt's composition ──────────────────────────────────────────

        [Test]
        public void Compose_PutsTheNameAboveTheAction()
        {
            // GUI.as:1286-1299: `s = owner.nazv; … s += "\n" + actionObj.actionText`.
            Assert.AreEqual("Door\nOpen", InteractionPromptView.Compose("Door", "Open"));
        }

        [Test]
        public void Compose_OmitsAnEmptyHalfRatherThanLeavingABlankLine()
        {
            Assert.AreEqual("Door", InteractionPromptView.Compose("Door", string.Empty));
            Assert.AreEqual("Door", InteractionPromptView.Compose("Door", null));
            Assert.AreEqual("Open", InteractionPromptView.Compose(string.Empty, "Open"));
            Assert.AreEqual("Open", InteractionPromptView.Compose(null, "Open"));
        }

        [Test]
        public void Compose_WithNeitherHalf_IsEmpty_SoThePromptHides()
        {
            // A looted container reports an empty ActionText; a prompt with no content must hide rather
            // than draw an empty plate over the object.
            Assert.AreEqual(string.Empty, InteractionPromptView.Compose(string.Empty, string.Empty));
            Assert.AreEqual(string.Empty, InteractionPromptView.Compose(null, null));
        }
    }
}
