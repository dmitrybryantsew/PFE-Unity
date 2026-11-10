using NUnit.Framework;
using PFE.Systems.Physics;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Row 0.3's rule half: the body box is a <b>function of content</b>, resolved by one shared rule,
    /// so every peer reads the same <c>width / height / sitHeight</c>.
    ///
    /// <para><b>Why this is not already covered.</b> The port stores the standing size on
    /// <c>UnitDefinition</c> and the crouched height as a third field, and nothing pins the relationship
    /// between them. The oracle derives it — <c>sitY = stayY / 2</c> when the attribute is absent
    /// (<c>Unit.as:1041-1048</c>) — so a port that stores a flat crouch height can drift from the oracle
    /// by editing one number, and two peers can drift from <i>each other</i> by shipping different
    /// content. That is the peer-divergence the row exists to prevent.</para>
    ///
    /// <para><b>The numbers are the oracle's, not invented.</b> littlepip's definition is
    /// <c>&lt;unit id='littlepip'&gt;&lt;phis sX='50' sY='70' massa='50'/&gt;</c> in
    /// <c>pfe/scripts/fe/AllData.as:20-24</c>. No <c>sitX</c>/<c>sitY</c> attributes → 50 × 70 standing,
    /// 50 × 35 crouched.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitBodyBoxRulesTests
    {
        // ── The oracle's two defaults ───────────────────────────────────────────────────────

        [Test]
        public void SitWidth_DefaultsToTheStandingWidth()
        {
            UnitBodyBox box = UnitBodyBoxRules.Resolve(50f, 70f);

            Assert.AreEqual(50f, box.SitWidthPx,
                "AS3 sitX defaults to stayX (Unit.as:1030-1036): crouching does not narrow the body. " +
                "A port that halves the width would let a crouched player fit gaps the oracle forbids.");
        }

        [Test]
        public void SitHeight_DefaultsToHalfTheStandingHeight()
        {
            UnitBodyBox box = UnitBodyBoxRules.Resolve(50f, 70f);

            Assert.AreEqual(35f, box.SitHeightPx,
                "AS3 sitY defaults to stayY / 2 (Unit.as:1041-1048).");
        }

        [Test]
        public void Littlepip_ResolvesToTheOracleBox()
        {
            UnitBodyBox box = UnitBodyBoxRules.Resolve(50f, 70f);

            Assert.AreEqual(50f, box.WidthPx);
            Assert.AreEqual(70f, box.HeightPx);
            Assert.AreEqual(50f, box.SitWidthPx);
            Assert.AreEqual(35f, box.SitHeightPx,
                "littlepip must crouch to 35 px. The serialized asset currently says 30, which is the " +
                "divergence UnitBodyBoxContentLintTests reports.");
        }

        [Test]
        public void AuthoredCrouchValues_WinOverTheDefaults()
        {
            // The oracle only derives when the attribute is ABSENT; an authored sitY/sitX is used as is.
            UnitBodyBox box = UnitBodyBoxRules.Resolve(60f, 75f, sitWidthPx: 44f, sitHeightPx: 30f);

            Assert.AreEqual(44f, box.SitWidthPx, "An authored sitX must not be overwritten by stayX.");
            Assert.AreEqual(30f, box.SitHeightPx, "An authored sitY must not be overwritten by stayY / 2.");
        }

        [Test]
        public void OnlyTheMissingHalf_IsDerived()
        {
            // Authored width, derived height — the two defaults are independent, and a rule that keyed
            // both off one presence flag would get this case wrong.
            UnitBodyBox box = UnitBodyBoxRules.Resolve(60f, 75f, sitWidthPx: 44f, sitHeightPx: 0f);

            Assert.AreEqual(44f, box.SitWidthPx);
            Assert.AreEqual(37.5f, box.SitHeightPx);
        }

        // ── The unit boundary ──────────────────────────────────────────────────────────────

        [Test]
        public void ResolveFromWorldUnits_ConvertsAtExactlyOneBoundary()
        {
            // 0.50 / 0.70 / 0.35 units — the oracle box in the form the definition stores it.
            UnitBodyBox box = UnitBodyBoxRules.ResolveFromWorldUnits(0.50f, 0.70f, 0.35f);

            Assert.AreEqual(50f, box.WidthPx, 1e-4f);
            Assert.AreEqual(70f, box.HeightPx, 1e-4f);
            Assert.AreEqual(35f, box.SitHeightPx, 1e-4f);
        }

        [Test]
        public void ResolveFromWorldUnits_DerivesWhenTheAuthoredCrouchIsNotPositive()
        {
            // sitHeight 0 = "not authored" — the fallback path, used by hand-authored content because
            // the importer normally writes stayY / 2 itself.
            UnitBodyBox box = UnitBodyBoxRules.ResolveFromWorldUnits(0.50f, 0.70f, 0f);

            Assert.AreEqual(35f, box.SitHeightPx, 1e-4f);
        }

        [Test]
        public void PixelsPerUnit_MatchesTheTileConstant()
        {
            Assert.AreEqual(100f, UnitBodyBoxRules.PixelsPerUnit,
                "The rule converts units to pixels; if this drifts from TileQueryConstants.UnitToPixel, " +
                "every box in the game is silently scaled wrong.");
        }

        // ── Refusals ───────────────────────────────────────────────────────────────────────

        [Test]
        public void AUnitWithNoStandingSize_IsRefusedRatherThanResolvedToZero()
        {
            // A zero box would make a unit collidable with nothing — a far quieter failure than a throw.
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => UnitBodyBoxRules.Resolve(0f, 70f),
                "A unit with no width must be refused, not resolved to a 0-wide body.");

            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => UnitBodyBoxRules.Resolve(50f, 0f),
                "A unit with no height must be refused, not resolved to a 0-tall body.");
        }

        // ── Mutation control ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The naive shape this rule exists to prevent: derive the crouch from the standing size but
        /// forget the halving, or forget that the width is <i>not</i> derived. Both variants are written
        /// here and shown to fail the assertions above, so those assertions are proven able to see the
        /// defect rather than merely passing.
        /// </summary>
        [Test]
        public void MutationControl_EachNaiveVariantIsCaughtByTheTestsAbove()
        {
            // Variant 1: sitY falls back to stayY (the "/ 2" dropped).
            UnitBodyBox noHalving = new UnitBodyBox(50f, 70f, 50f, 70f);
            Assert.AreNotEqual(35f, noHalving.SitHeightPx,
                "The mutation control did not mutate; the assertion below would be vacuous.");
            Assert.AreNotEqual(UnitBodyBoxRules.Resolve(50f, 70f).SitHeightPx, noHalving.SitHeightPx,
                "SitHeight_DefaultsToHalfTheStandingHeight cannot separate the rule from a variant that " +
                "forgets the halving, so it is not a guard.");

            // Variant 2: sitX halved like sitY.
            UnitBodyBox halvedWidth = new UnitBodyBox(50f, 70f, 25f, 35f);
            Assert.AreNotEqual(UnitBodyBoxRules.Resolve(50f, 70f).SitWidthPx, halvedWidth.SitWidthPx,
                "SitWidth_DefaultsToTheStandingWidth cannot separate the rule from a variant that halves " +
                "the crouched width too, so it is not a guard.");
        }
    }
}
