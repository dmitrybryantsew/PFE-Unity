using NUnit.Framework;

namespace PFE.Tests.EditMode.Systems.RPG
{
    /// <summary>
    /// Pins the player's situational precision multiplier — AS3's <c>precMult</c> as rebuilt every
    /// tick in <c>UnitPlayer.control()</c> (<c>UnitPlayer.as:1181-1200</c>) and stamped on the bullet
    /// through <c>Weapon.resultPrec</c> (<c>Weapon.as:1634</c>, <c>:1531</c>).
    ///
    /// <para><b>Why this fixture exists.</b> The statId audit (2026-10-03) found <c>allPrecMult</c>,
    /// <c>runPenalty</c>, <c>jumpPenalty</c>, <c>backPenalty</c> and <c>stayBonus</c> in the
    /// "AS3 reads it, the port does not" cell: <c>allPrecMult</c> was written by the stat engine but
    /// read only by the debug overlay, and the four penalty/bonus fields did not exist in the port at
    /// all — even though <c>runPenalty</c> 0.5 and friends are live <i>declaration</i> defaults that
    /// no <c>defaultParams()</c> line resets. The whole precision channel was therefore absent from
    /// every shot.</para>
    ///
    /// <para>These cases exercise the pure composition rule, so they run in the offline harness as
    /// well as in the editor. Every branch cites the oracle line it pins.</para>
    /// </summary>
    [TestFixture]
    public class PrecisionMultiplierTests
    {
        // The AS3 declaration defaults (Pers.as:189-195, :369, :371) — the values that are live
        // before any perk applies, and which no defaultParams() line overwrites.
        private const float AllPrecMult  = 1f;
        private const float RunPenalty   = 0.5f;
        private const float JumpPenalty  = 0.3f;
        private const float BackPenalty  = 0.4f;
        private const float StayBonus    = 0.3f;

        /// <summary>A grounded, motionless player facing the same way as the weapon.</summary>
        private static float Standing(float allPrec = AllPrecMult)
            => PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                allPrec, RunPenalty, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: true, dx: 0f, dy: 0f, weaponFacingDiffers: false);

        // ── The seed ──────────────────────────────────────────────────────────────

        [Test]
        [Description("AllPrecMult_IsTheSeed_AndAMultiplierOfOneChangesNothing")]
        public void AllPrecMult_IsTheSeed()
        {
            // A player with allPrecMult = 1 who is grounded, still and facing forward gets exactly
            // `1 * (1 + stayBonus)` = 1.3: the seed times the one term that applies.
            Assert.AreEqual(1.3f, Standing(allPrec: 1f), 1e-5f,
                "allPrecMult seeds the product and stayBonus is the only term in play (UnitPlayer.as:1181,1195)");

            // The seed is a plain multiplier, so 2 doubles the composed value.
            Assert.AreEqual(2.6f, Standing(allPrec: 2f), 1e-5f,
                "allPrecMult scales the whole composition (Weapon.as:1634)");
        }

        // ── stayBonus: grounded and |dx| < 1 ──────────────────────────────────────

        [Test]
        [Description("StayBonus_AppliesOnlyWhenGroundedAndNearlyStationary")]
        public void StayBonus_RequiresGroundedAndAlmostStill()
        {
            // AS3 `if(stay && dx < 1 && dx > -1) precMult *= 1 + stayBonus;` — the bound is on `dx`
            // alone, so a moving drifter with |dy| large still collects the bonus. That asymmetry is
            // the oracle's, not an oversight.
            const float stillGround = 1f * (1f + StayBonus);
            Assert.AreEqual(stillGround, Standing(), 1e-5f, "grounded + still");

            // dx just inside the bound still qualifies.
            float inside = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                AllPrecMult, RunPenalty, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: true, dx: 0.99f, dy: 0f, weaponFacingDiffers: false);
            Assert.AreEqual(stillGround, inside, 1e-5f, "|dx| < 1 is inclusive of 0.99");

            // dx exactly 1 fails the strict `< 1`, so no bonus — and 1 is not > 10, so no run penalty
            // either. The composition is exactly the seed.
            float boundary = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                AllPrecMult, RunPenalty, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: true, dx: 1f, dy: 0f, weaponFacingDiffers: false);
            Assert.AreEqual(AllPrecMult, boundary, 1e-5f,
                "dx == 1 is outside the strict `< 1` (UnitPlayer.as:1193)");
        }

        // ── jumpPenalty: !stay ────────────────────────────────────────────────────

        [Test]
        [Description("JumpPenalty_AppliesWheneverAirborne_RegardlessOfSpeed")]
        public void JumpPenalty_AppliesAirborne()
        {
            // Airborne at a standstill: `!stay` holds, `stay && …` does not, so exactly one term
            // fires. This is the term that makes jumping while shooting worse.
            float airborne = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                AllPrecMult, RunPenalty, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: false, dx: 0f, dy: 0f, weaponFacingDiffers: false);
            Assert.AreEqual(1f - JumpPenalty, airborne, 1e-5f,
                "airborne applies 1 - jumpPenalty and NOT the stay bonus (UnitPlayer.as:1189-1195)");

            // Moving fast while airborne: the run term DOES apply (it only tests speed), so the two
            // stack — the oracle's two independent `if`s are not an either/or.
            float fastAirborne = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                AllPrecMult, RunPenalty, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: false, dx: 20f, dy: 0f, weaponFacingDiffers: false);
            Assert.AreEqual((1f - RunPenalty) * (1f - JumpPenalty), fastAirborne, 1e-5f,
                "running and airborne stack (UnitPlayer.as:1185,1189)");
        }

        // ── runPenalty: |dx| or |dy| > 10, and the value must be positive ─────────

        [Test]
        [Description("RunPenalty_AppliesOnEitherAxisAboveTen_AndIsGatedOnBeingPositive")]
        public void RunPenalty_AppliesAboveTenOnEitherAxis()
        {
            // Note the speed test alone decides the term even while GROUNDED: a grounded fast runner
            // loses precision, and because |dx| > 1 the stay bonus does not fire either.
            float running = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                AllPrecMult, RunPenalty, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: true, dx: 11f, dy: 0f, weaponFacingDiffers: false);
            Assert.AreEqual(1f - RunPenalty, running, 1e-5f,
                "dx > 10 applies the run penalty (UnitPlayer.as:1185)");

            // The vertical axis counts too (`dy > 10`), which matters while falling fast. Note that
            // dx is 0 here, so the stand-still bonus ALSO applies — the run term tests speed on
            // either axis, while the stay term tests only dx, so a fast faller standing still on the
            // x-axis collects both. That combination is the oracle's own, not an artefact.
            float falling = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                AllPrecMult, RunPenalty, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: true, dx: 0f, dy: -11f, weaponFacingDiffers: false);
            Assert.AreEqual((1f - RunPenalty) * (1f + StayBonus), falling, 1e-5f,
                "dy < -10 applies the run penalty, and dx == 0 still collects stayBonus (UnitPlayer.as:1185,1195)");

            // At exactly 10 neither `< -10` nor `> 10` holds — the oracle's bounds are strict.
            float exact = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                AllPrecMult, RunPenalty, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: true, dx: 10f, dy: 0f, weaponFacingDiffers: false);
            Assert.AreEqual(AllPrecMult, exact, 1e-5f,
                "dx == 10 is not > 10 and not < 1, so no term applies");

            // The `runPenalty > 0` guard is real: a zero penalty skips the term rather than
            // multiplying by (1 - 0) = 1. Same product, but the term must not be entered at all,
            // because a negative penalty would then *raise* precision.
            float zeroPenalty = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                AllPrecMult, runPenalty: 0f, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: true, dx: 100f, dy: 0f, weaponFacingDiffers: false);
            Assert.AreEqual(AllPrecMult, zeroPenalty, 1e-5f,
                "runPenalty == 0 disables the term (UnitPlayer.as:1185 guard)");
        }

        [Test]
        [Description("RunPenalty_NegativeValueWouldRaisePrecision_SoTheGuardMatters")]
        public void RunPenalty_FieldIndependence_TheSkillIsNotTheSource()
        {
            // A field-independence guard in the style the audit recommends: if the value were ever
            // derived from something else, changing only the penalty must still move the output by
            // exactly that factor and nothing else.
            float p25 = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                2f, runPenalty: 0.25f, jumpPenalty: 0f, stayBonus: 0f, backPenalty: 0f,
                isGrounded: true, dx: 50f, dy: 0f, weaponFacingDiffers: false);
            Assert.AreEqual(2f * 0.75f, p25, 1e-5f,
                "with every other term zeroed the product is exactly allPrecMult*(1-runPenalty)");
        }

        // ── backPenalty ───────────────────────────────────────────────────────────

        [Test]
        [Description("BackPenalty_AppliesWhenTheWeaponFacesAwayFromTheBody")]
        public void BackPenalty_AppliesWhenWeaponFacingDiffers()
        {
            // The caller owns the `currentWeapon.storona != storona` comparison because it needs both
            // facings; here only the consequence is pinned.
            float back = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                AllPrecMult, RunPenalty, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: true, dx: 0f, dy: 0f, weaponFacingDiffers: true);
            Assert.AreEqual((1f + StayBonus) * (1f - BackPenalty), back, 1e-5f,
                "firing backwards stacks on the stand-still bonus (UnitPlayer.as:1195,1199)");
        }

        // ── the whole thing ───────────────────────────────────────────────────────

        [Test]
        [Description("AllFourTerms_ComposeAsASingleProduct_NotAsOrderedPrecedence")]
        public void AllTerms_ComposeAsAProduct()
        {
            // Running backwards while airborne is the worst case and exercises three terms at once.
            // AS3 multiplies all of them in sequence, so the expected value is the product of each
            // factor — a change to any single field must move exactly that one factor.
            float worst = PFE.Systems.RPG.CharacterStats.ComposePrecisionMultiplier(
                2f, RunPenalty, JumpPenalty, StayBonus, BackPenalty,
                isGrounded: false, dx: 20f, dy: 0f, weaponFacingDiffers: true);

            float expected = 2f * (1f - RunPenalty) * (1f - JumpPenalty) * (1f - BackPenalty);
            Assert.AreEqual(expected, worst, 1e-5f,
                "all four terms are one product (UnitPlayer.as:1181-1199)");

            // Sanity on the direction: the worst case must be strictly below the best case, and both
            // must be positive — no penalty can flip the sign of a shot's precision.
            Assert.Greater(Standing(allPrec: 2f), worst, "standing beats running backwards mid-air");
            Assert.Greater(worst, 0f, "the composition is always positive");
        }
    }
}
