using NUnit.Framework;
using PFE.Systems.Magic;

namespace PFE.Tests.EditMode.Systems.Magic
{
    /// <summary>
    /// The <c>shithp</c>/<c>shitArmor</c> shield layer, read from <c>Unit.damage()</c>.
    ///
    /// <para>Pure static, so these run offline — unlike the armour path's own fixtures, which are
    /// editor-only because they drive a live unit. That is the point of extracting the rule: the shield
    /// is the one part of the damage block that can be pinned without an editor.</para>
    /// </summary>
    [TestFixture]
    public class SpellShieldTests
    {
        // ── ArmourRating (Unit.as:3583 wear subtraction, :3636 reduction bonus) ─────────────────

        [Test]
        public void ArmourRating_IsTheRatingWhenTheShieldIsUp_AndZeroWhenItIsDown()
        {
            Assert.AreEqual(25f, SpellShield.ArmourRating(shitHp: 500f, shitArmor: 25f), 1e-4f,
                "a live shield contributes its full rating");
            Assert.AreEqual(0f, SpellShield.ArmourRating(shitHp: 0f, shitArmor: 25f), 1e-4f,
                "a downed shield contributes nothing, even with a non-zero rating");
        }

        [Test]
        public void AZeroRatedShield_ContributesNothing_ButIsStillTrackedAsUp()
        {
            // UnitTurret.as:507/512 sets shitArmor = 0 while shithp > 0. The rating is then 0 — the
            // same value a downed shield contributes — so ArmourRating alone cannot tell the two
            // apart, and reading "rating 0" as "no shield" would be wrong. The difference is in the
            // other two rules: the gate still refuses a zero-size hit, and a real hit still wears the
            // shield down.
            Assert.AreEqual(0f, SpellShield.ArmourRating(shitHp: 100f, shitArmor: 0f), 1e-4f);
            Assert.AreEqual(0f, SpellShield.ArmourRating(shitHp: 0f, shitArmor: 0f), 1e-4f,
                "indistinguishable from a downed shield by rating alone — which is the point");

            Assert.IsFalse(SpellShield.PermitsArmourPoolWear(shitHp: 100f, shitArmor: 0f, incomingDamage: 0f),
                "a live shield refuses even a zero-size hit (`0 <= 0`)");
            Assert.IsTrue(SpellShield.PermitsArmourPoolWear(shitHp: 0f, shitArmor: 0f, incomingDamage: 0f),
                "a downed shield has no gate at all");

            Assert.AreEqual(60f, SpellShield.AfterAbsorb(shitHp: 100f, incomingDamage: 40f), 1e-4f,
                "the live shield is still worn down, rating or not");
        }

        // ── PermitsArmourPoolWear (Unit.as:3578 gate term) ──────────────────────────────────────

        [Test]
        public void PermitsArmourPoolWear_WithNoShield_AlwaysPermits()
        {
            Assert.IsTrue(SpellShield.PermitsArmourPoolWear(shitHp: 0f, shitArmor: 30f, incomingDamage: 1f));
            Assert.IsTrue(SpellShield.PermitsArmourPoolWear(shitHp: 0f, shitArmor: 30f, incomingDamage: 999f));
        }

        [Test]
        public void PermitsArmourPoolWear_WhileShielded_OnlyAHitAboveTheRatingReachesThePool()
        {
            // `(shithp <= 0 || param1 > shitArmor)` — the comparison is strict `>`, so a hit exactly
            // equal to the rating is shrugged off. This is the boundary a `>=` rewrite would move.
            Assert.IsFalse(SpellShield.PermitsArmourPoolWear(shitHp: 500f, shitArmor: 30f, incomingDamage: 29f),
                "below the rating -> the shield takes it");
            Assert.IsFalse(SpellShield.PermitsArmourPoolWear(shitHp: 500f, shitArmor: 30f, incomingDamage: 30f),
                "exactly the rating -> still the shield (strict >)");
            Assert.IsTrue(SpellShield.PermitsArmourPoolWear(shitHp: 500f, shitArmor: 30f, incomingDamage: 31f),
                "above the rating -> the hit passes through to the pool");
        }

        // ── AfterAbsorb (Unit.as:3629-3637) ─────────────────────────────────────────────────────

        [Test]
        public void AfterAbsorb_DecrementsByTheIncomingDamage_NotTheReducedDamage()
        {
            // The shield is worn by the whole hit, even though it also subtracts its rating from that
            // hit's damage. 500 hp, 40 damage -> 460, independent of the rating.
            Assert.AreEqual(460f, SpellShield.AfterAbsorb(shitHp: 500f, incomingDamage: 40f), 1e-4f);
        }

        [Test]
        public void AfterAbsorb_ClampsAtZero()
        {
            Assert.AreEqual(0f, SpellShield.AfterAbsorb(shitHp: 10f, incomingDamage: 40f), 1e-4f,
                "a hit larger than the shield drops it to exactly 0, never negative");
            Assert.AreEqual(0f, SpellShield.AfterAbsorb(shitHp: 40f, incomingDamage: 40f), 1e-4f,
                "exactly depleted");
        }

        [Test]
        public void AfterAbsorb_LeavesADownedShieldUnchanged()
        {
            // The absent control for the clamp: the oracle skips the whole `if (shithp > 0)` block when
            // the shield is down, so a downed shield must not go negative on a hit.
            Assert.AreEqual(0f, SpellShield.AfterAbsorb(shitHp: 0f, incomingDamage: 40f), 1e-4f);
        }

        // ── The two roles together, as Unit.damage runs them ────────────────────────────────────

        [Test]
        public void AShieldedUnit_TakesTheShrugOffPath_ThenBreaksOnTheHittingBlow()
        {
            // Unit.as:3578 gate + :3629 absorb, in the order Unit.damage runs them.
            const float rating = 30f;
            float shield = 50f;

            // Hit 1 (20 damage): <= rating, so the pool is not worn, but the shield is depleted.
            Assert.IsFalse(SpellShield.PermitsArmourPoolWear(shield, rating, incomingDamage: 20f));
            Assert.AreEqual(30f, SpellShield.AfterAbsorb(shield, incomingDamage: 20f), 1e-4f);
            Assert.AreEqual(rating, SpellShield.ArmourRating(shield, rating), 1e-4f,
                "while up, the shield still contributes its rating to the reduction");

            // Hit 2 (40 damage): > rating, so the pool wears — with the rating subtracted from the wear
            // (ArmourWear.PoolIntegrityDamage's spellShieldAbsorb) — and the shield breaks.
            shield = 30f;
            Assert.IsTrue(SpellShield.PermitsArmourPoolWear(shield, rating, incomingDamage: 40f));
            Assert.AreEqual(0f, SpellShield.AfterAbsorb(shield, incomingDamage: 40f), 1e-4f,
                "the shield is spent");

            // Hit 3: shield down -> no rating, and the pool always wears.
            shield = 0f;
            Assert.AreEqual(0f, SpellShield.ArmourRating(shield, rating), 1e-4f);
            Assert.IsTrue(SpellShield.PermitsArmourPoolWear(shield, rating, incomingDamage: 1f));
        }

        // ── Decay (UnitPlayer.as:1298-1300) ─────────────────────────────────────────────────────
        //
        // The bleed that makes the shield temporary. It is player-only in the oracle — no Unit.step()
        // touches the field, and the four bosses keep theirs until it is destroyed — so the rate is a
        // parameter rather than a constant baked into the rule.

        [Test]
        public void Decay_LowersALiveShieldByTheRate()
        {
            Assert.AreEqual(149.95f, SpellShield.Decay(shitHp: 150f), 1e-4f,
                "one tick of the default rate");
            Assert.AreEqual(140f, SpellShield.Decay(shitHp: 150f, perTick: 10f), 1e-4f,
                "an explicit rate is honoured — this is the knob a boss caller sets to 0");
        }

        [Test]
        public void Decay_LeavesADownedShieldAlone()
        {
            // The absent control. `if(shithp > 0)` guards the whole statement, so a shield already at 0
            // is not dragged further down.
            Assert.AreEqual(0f, SpellShield.Decay(shitHp: 0f), 1e-4f);
        }

        [Test]
        public void Decay_AtAZeroRate_IsTheIdentity()
        {
            // The non-player case: bosses hold their shield until it is destroyed. A rule that always
            // applied the player's rate would bleed every boss shield away in seconds.
            Assert.AreEqual(500f, SpellShield.Decay(shitHp: 500f, perTick: 0f), 1e-4f);
        }

        [Test]
        public void Decay_DoesNotClampAtZero()
        {
            // AS3 writes `shithp -= 0.05` with no floor, so the last tick can undershoot. This is not a
            // bug to fix: every consumer gates on `shithp > 0`, so the negative reads as "down" in all
            // three rules, and `die()` (Unit.as:4369) / respawn (UnitPlayer.as:3427) clear it outright.
            // Clamping here would be a tidy-up that diverges from the oracle.
            Assert.AreEqual(-0.03f, SpellShield.Decay(shitHp: 0.02f), 1e-6f);
        }

        [Test]
        public void Decay_BleedsAShieldToNothing_AndItThenReadsAsDown()
        {
            // 150 hp at 0.05/tick is 3000 ticks. Run a margin past it: the float accumulation means the
            // exact landing point is not worth asserting, but "down" is — and "down" is what every other
            // rule in this class keys off.
            float shield = 150f;
            for (int i = 0; i < 3100; i++) shield = SpellShield.Decay(shield);

            Assert.LessOrEqual(shield, 0f, "the shield is spent, not merely small");
            Assert.AreEqual(0f, SpellShield.ArmourRating(shield, shitArmor: 20f), 1e-4f);
            Assert.IsTrue(SpellShield.PermitsArmourPoolWear(shield, shitArmor: 20f, incomingDamage: 1f),
                "and the ordinary armour pool takes hits again");
        }

        // ── The graphic — UnitPlayer.as:4916-4924 ───────────────────────────────────────────────
        //
        // Two rise/fall edges, not one level test. The oracle:
        //   if(vis.shit && !vis.shit.visible && shithp > 0) { visible = true;  gotoAndPlay(1); }
        //   if(vis.shit &&  vis.shit.visible && shithp <= 0) { visible = false; gotoAndStop(1); }

        [Test]
        public void ShouldShow_OnlyOnTheRise()
        {
            Assert.IsTrue(SpellShield.ShouldShow(shitHp: 150f, currentlyVisible: false),
                "hidden + shithp > 0 is the oracle's first block");
        }

        [Test]
        public void ShouldShow_IsFalse_WhileAlreadyVisible()
        {
            // The absent control, and the reason this is an edge and not a level: `!vis.shit.visible` is
            // part of the guard, so a shield that is up must not be re-shown every frame. A level test
            // (`shithp > 0`) would pass the test above and fail this one, restarting the loop 30x/s.
            Assert.IsFalse(SpellShield.ShouldShow(shitHp: 150f, currentlyVisible: true));
        }

        [Test]
        public void ShouldShow_IsFalse_AtZeroOrBelow()
        {
            Assert.IsFalse(SpellShield.ShouldShow(shitHp: 0f, currentlyVisible: false));
            Assert.IsFalse(SpellShield.ShouldShow(shitHp: -0.03f, currentlyVisible: false),
                "the decay undershoots on purpose, and a negative shield reads as down everywhere");
        }

        [Test]
        public void ShouldHide_OnlyOnTheFall()
        {
            Assert.IsTrue(SpellShield.ShouldHide(shitHp: 0f, currentlyVisible: true));
            Assert.IsTrue(SpellShield.ShouldHide(shitHp: -0.03f, currentlyVisible: true),
                "the oracle's second block is `shithp <= 0`, inclusive");
        }

        [Test]
        public void ShouldHide_IsFalse_WhileAlreadyHidden_AndWhileTheShieldIsUp()
        {
            Assert.IsFalse(SpellShield.ShouldHide(shitHp: 0f, currentlyVisible: false),
                "the absent control for the fall edge -- otherwise the hide would fire every frame");
            Assert.IsFalse(SpellShield.ShouldHide(shitHp: 150f, currentlyVisible: true),
                "a live shield must not be hidden");
        }

        [Test]
        public void ShouldBeVisible_IsTheLevel_ForCallersThatNeedNoTransition()
        {
            Assert.IsTrue(SpellShield.ShouldBeVisible(shitHp: 0.01f));
            Assert.IsFalse(SpellShield.ShouldBeVisible(shitHp: 0f));
            Assert.IsFalse(SpellShield.ShouldBeVisible(shitHp: -5f));
        }

        [Test]
        public void TheTwoEdges_AgreeWithTheLevel_AcrossAFullCastAndDecay()
        {
            // The whole life of a shield, driven only by the two edges -- which is what
            // PlayerCharacterVisual.Update does. This is the test that would catch the two edges
            // disagreeing with each other (e.g. a rise with no matching fall, leaving the graphic stuck
            // on after the shield is spent).
            bool visible = false;
            float shitHp = 0f;

            visible = Step(shitHp, visible);
            Assert.IsFalse(visible, "nothing cast yet");

            shitHp = 150f;                       // sp_mshit
            visible = Step(shitHp, visible);
            Assert.IsTrue(visible, "the cast raises it");

            // The shield's whole life: the player tick bleeds it, the view follows the two edges. The
            // loop stops the moment the graphic goes down, so a missing fall edge shows up as a loop
            // that runs to its bound instead of exiting early.
            int ticks = 0;
            while (visible && ticks < 5000)
            {
                shitHp = SpellShield.Decay(shitHp);
                visible = Step(shitHp, visible);
                ticks++;
            }

            Assert.IsFalse(visible, "the decay spends it and the fall edge clears the graphic");
            Assert.Less(ticks, 5000, "the fall edge fired; the loop was not cut off by its bound");

            // Re-cast: the rise must fire again, because the graphic was genuinely hidden.
            shitHp = 150f;
            visible = Step(shitHp, visible);
            Assert.IsTrue(visible);
        }

        /// <summary>One frame of the two-edge update, exactly as the view applies it.</summary>
        private static bool Step(float shitHp, bool visible)
        {
            if (SpellShield.ShouldShow(shitHp, visible)) return true;
            if (SpellShield.ShouldHide(shitHp, visible)) return false;
            return visible;
        }
    }
}
