using System;
using NUnit.Framework;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="WeaponHoldPointMath"/> — the port of AS3 <c>UnitPlayer.setWeaponPos</c>
    /// (<c>UnitPlayer.as:3514-3585</c>) and the base <c>Unit.setWeaponPos</c>
    /// (<c>Unit.as:3288-3294</c>).
    ///
    /// <para><b>The bug these guard.</b> "The weapon is levitating." The rig held the gun on a fixed
    /// marker child at local <c>(1, 1)</c> — a whole unit up from the feet, on a character 0.7 units
    /// tall. AS3 has no marker: the hold point is derived from the body box and the cursor every frame.
    /// Every expected value below is derived from the oracle's fractions, not from what the old code
    /// did, and the body numbers are <c>littlepip</c>'s real ones (<c>width: 0.5</c>, <c>height: 0.7</c>
    /// — the <c>&lt;phis sX='50' sY='70'&gt;</c> pair in <c>AllData.as</c>).</para>
    ///
    /// <para><b>The sign trap this pins hardest.</b> Flash's y grows downwards, so the oracle's
    /// <c>weaponY = Y - scY * 0.7</c> is a <i>lift</i>. The port's y grows upwards and its origin is
    /// still the feet, so the same point is <c>feetY + scY * 0.7</c>. A port that transcribed the minus
    /// sign literally would hang the gun 0.49 units below the floor, and
    /// <see cref="HoldHeight_IsSeventyPercentOfBodyUpFromTheFeet"/> is what catches that.</para>
    /// </summary>
    [TestFixture]
    public class WeaponHoldPointMathTests
    {
        private const float Tolerance = 1e-3f;

        // littlepip: UnitDefinition.width = 0.5, height = 0.7 (AS3 <phis sX='50' sY='70'>).
        private const float BodyW = 0.5f;
        private const float BodyH = 0.7f;

        /// <summary>0.7 of a body up from the feet — AS3's `Y - scY * 0.7`.</summary>
        private static float NormalHoldY => BodyH * 0.7f;

        /// <summary>A predicate that reports solid exactly at the listed points (and nowhere else).</summary>
        private static Func<Vector2, bool> SolidAt(params Vector2[] points)
            => p =>
            {
                foreach (Vector2 q in points)
                    if (Vector2.Distance(p, q) < 1e-3f) return true;
                return false;
            };

        private static WeaponHoldPointMath.Inputs Inputs(
            float ownerX = 0f, float feetY = 0f, float facing = 1f, float aimX = 0f,
            int tip = 0, float krep = 0f, bool onStairs = false, bool stay = false,
            bool weaponUp = false, bool swapping = false)
            => new WeaponHoldPointMath.Inputs
            {
                OwnerX = ownerX,
                OwnerFeetY = feetY,
                BodyWidth = BodyW,
                BodyHeight = BodyH,
                FacingSign = facing,
                AimX = aimX,
                Tip = tip,
                WeaponKrep = krep,
                OnStairs = onStairs,
                Stay = stay,
                WeaponUp = weaponUp,
                SwappingWeapon = swapping,
            };

        // ── Height ────────────────────────────────────────────────────────────

        [Test]
        public void HoldHeight_IsSeventyPercentOfBodyUpFromTheFeet()
        {
            Vector2 hold = WeaponHoldPointMath.Resolve(Inputs(aimX: 0f));
            Assert.AreEqual(0f, hold.x, Tolerance);
            Assert.AreEqual(NormalHoldY, hold.y, Tolerance, "AS3: weaponY = Y - scY * 0.7, a lift");
        }

        [Test]
        public void HoldHeight_FollowsTheOwnerUpAndDown()
        {
            Vector2 hold = WeaponHoldPointMath.Resolve(Inputs(feetY: 2f, aimX: 0f));
            Assert.AreEqual(2f + NormalHoldY, hold.y, Tolerance);
        }

        [Test]
        public void HoldHeight_TipOneUsesThePunchFraction()
        {
            // AS3: `if(tip == 1) weaponY = Y - scY * 0.4;` — the club/punch family is held lower.
            Vector2 hold = WeaponHoldPointMath.Resolve(Inputs(tip: 1));
            Assert.AreEqual(BodyH * 0.4f, hold.y, Tolerance);
        }

        [Test]
        public void HoldHeight_EveryOtherTipUsesTheNormalFraction()
        {
            // tip 5 (WMagic) must NOT be special-cased on height — only the swap override tests it.
            foreach (int tip in new[] { 0, 2, 3, 4, 5, 12 })
                Assert.AreEqual(NormalHoldY, WeaponHoldPointMath.Resolve(Inputs(tip: tip)).y, Tolerance,
                    $"tip={tip}");
        }

        // ── Reach-out ─────────────────────────────────────────────────────────

        [Test]
        public void NoReachOut_WhileTheCursorIsInsideTheBody()
        {
            // X2 = X + scX/2 = 0.25. A cursor inside the body leaves the gun on the body.
            Assert.AreEqual(0f, WeaponHoldPointMath.Resolve(Inputs(aimX: 0f)).x, Tolerance);
            Assert.AreEqual(0f, WeaponHoldPointMath.Resolve(Inputs(aimX: 0.2f)).x, Tolerance);
        }

        [Test]
        public void ReachOut_WhenTheCursorPassesTheSnout()
        {
            // AS3: `weaponX = X + scX * storona` — a whole body width, not a fraction of it.
            Assert.AreEqual(0.5f, WeaponHoldPointMath.Resolve(Inputs(aimX: 10f)).x, Tolerance);
        }

        [Test]
        public void ReachOut_BoundaryIsStrict()
        {
            // AS3 tests `celX > X2`, so exactly on the edge is not past it. An off-by-one here would
            // make the weapon twitch out and back as the cursor crossed the edge.
            Assert.AreEqual(0f, WeaponHoldPointMath.Resolve(Inputs(aimX: 0.25f)).x, Tolerance);
            Assert.AreEqual(0.5f, WeaponHoldPointMath.Resolve(Inputs(aimX: 0.2501f)).x, Tolerance);
        }

        [Test]
        public void ReachOut_MirrorsWhenFacingLeft()
        {
            // X1 = X - scX/2 = -0.25, and the reach is `scX * storona` = -0.5.
            Assert.AreEqual(-0.5f, WeaponHoldPointMath.Resolve(Inputs(facing: -1f, aimX: -10f)).x, Tolerance);
        }

        [Test]
        public void ReachOut_DoesNotHappenFacingAwayFromTheCursor()
        {
            // Facing left while aiming right (shooting behind you): AS3's left-facing test is
            // `celX < X1`, and a cursor to the right fails it, so the gun stays on the body.
            Assert.AreEqual(0f, WeaponHoldPointMath.Resolve(Inputs(facing: -1f, aimX: 10f)).x, Tolerance);
            Assert.AreEqual(0f, WeaponHoldPointMath.Resolve(Inputs(facing: 1f, aimX: -10f)).x, Tolerance);
        }

        // ── Stairs ────────────────────────────────────────────────────────────

        [Test]
        public void OnStairs_PinsTheWeaponToTheBody()
        {
            // AS3: `if(isLaz) weaponX = X;` — isLaz is the stair direction (Unit.as:2604), and on a
            // slope the reach-out is suppressed so the gun does not poke into the step ahead.
            Vector2 hold = WeaponHoldPointMath.Resolve(Inputs(aimX: 10f, onStairs: true));
            Assert.AreEqual(0f, hold.x, Tolerance);
        }

        // ── Tile clamps ───────────────────────────────────────────────────────

        [Test]
        public void SolidAtTheHoldPoint_PullsTheWeaponBackOntoTheBody()
        {
            var hold = new Vector2(0.5f, NormalHoldY);
            Vector2 result = WeaponHoldPointMath.Resolve(Inputs(aimX: 10f), SolidAt(hold));
            Assert.AreEqual(0f, result.x, Tolerance);
        }

        [Test]
        public void SolidFifteenPixelsAhead_PullsTheWeaponBackOntoTheBody()
        {
            // AS3 probes `weaponX + storona * 15` as well as the hold point itself. 15 px is 0.15 u.
            var ahead = new Vector2(0.5f + 0.15f, NormalHoldY);
            Vector2 result = WeaponHoldPointMath.Resolve(Inputs(aimX: 10f), SolidAt(ahead));
            Assert.AreEqual(0f, result.x, Tolerance, "the ahead probe alone must trigger the clamp");
        }

        [Test]
        public void SolidElsewhereDoesNotClamp()
        {
            // Control: the clamp is not simply "any solid tile anywhere".
            Vector2 result = WeaponHoldPointMath.Resolve(Inputs(aimX: 10f), SolidAt(new Vector2(9f, 9f)));
            Assert.AreEqual(0.5f, result.x, Tolerance);
        }

        [Test]
        public void NoPredicateMeansNoClamp()
        {
            // What the loadout passes today: it owns no room reference. AS3 in open ground does the
            // same, and the difference only ever shows against a wall.
            Assert.AreEqual(0.5f, WeaponHoldPointMath.Resolve(Inputs(aimX: 10f), isSolidAt: null).x, Tolerance);
        }

        // ── Weapon-up ─────────────────────────────────────────────────────────

        [Test]
        public void WeaponUp_LiftsFortyPixelsWhileStandingStill()
        {
            Vector2 hold = WeaponHoldPointMath.Resolve(Inputs(stay: true, weaponUp: true));
            Assert.AreEqual(NormalHoldY + 0.4f, hold.y, Tolerance, "AS3: weaponY -= 40 px");
        }

        [Test]
        public void WeaponUp_NeedsBothStayAndWeaponUp()
        {
            // AS3: `if(stay && this.weapUp)`. Either alone does nothing.
            Assert.AreEqual(NormalHoldY, WeaponHoldPointMath.Resolve(Inputs(stay: true)).y, Tolerance);
            Assert.AreEqual(NormalHoldY, WeaponHoldPointMath.Resolve(Inputs(weaponUp: true)).y, Tolerance);
        }

        [Test]
        public void WeaponUp_DoesNotLiftIntoACeiling()
        {
            var lifted = new Vector2(0f, NormalHoldY + 0.4f);
            Vector2 hold = WeaponHoldPointMath.Resolve(Inputs(stay: true, weaponUp: true), SolidAt(lifted));
            Assert.AreEqual(NormalHoldY, hold.y, Tolerance);
        }

        // ── krep and the swap override ────────────────────────────────────────

        [Test]
        public void Krep_SendsTheWholeRuleToTheBaseFallback()
        {
            // AS3: `if(weaponKrep == 0) { ...derived... } else { super.setWeaponPos(tip); }` — the base
            // class is `weaponX = X; weaponY = Y - scY * 0.5`. Note the reach-out is gone entirely.
            Vector2 hold = WeaponHoldPointMath.Resolve(Inputs(aimX: 10f, tip: 1, krep: 1f));
            Assert.AreEqual(0f, hold.x, Tolerance);
            Assert.AreEqual(BodyH * 0.5f, hold.y, Tolerance);
        }

        [Test]
        public void WeaponSwap_DropsToTheBaseHold()
        {
            // AS3: `if(this.work == "change" && t_work > changeWeaponTime3 && tip != 5)`
            Vector2 hold = WeaponHoldPointMath.Resolve(Inputs(aimX: 10f, swapping: true));
            Assert.AreEqual(0f, hold.x, Tolerance);
            Assert.AreEqual(BodyH * 0.5f, hold.y, Tolerance);
        }

        [Test]
        public void WeaponSwap_ExemptsTipFive()
        {
            // The oracle's `tip != 5` guard: a magic weapon keeps its derived hold through a swap.
            Vector2 hold = WeaponHoldPointMath.Resolve(Inputs(aimX: 10f, tip: 5, swapping: true));
            Assert.AreEqual(0.5f, hold.x, Tolerance);
            Assert.AreEqual(NormalHoldY, hold.y, Tolerance);
        }

        // ── Magic / horn fallback ─────────────────────────────────────────────

        [Test]
        public void MagicFallback_UsesTheBaseHoldWhenTheBoneLookupFails()
        {
            // AS3's `catch` branch: `magicX = X; magicY = Y - scY / 2;`
            Vector2 magic = WeaponHoldPointMath.ResolveMagicFallback(1.5f, 2f, BodyH);
            Assert.AreEqual(1.5f, magic.x, Tolerance);
            Assert.AreEqual(2f + BodyH * 0.5f, magic.y, Tolerance);
        }

        [Test]
        public void MagicFallback_UsesTheOraclePixelDropsWhenTheBoneIsInAWall()
        {
            // AS3: `magicY = Y - 75` standing, `Y - 35` seated. Absolute pixels, not body fractions —
            // which is why this is a separate method from Resolve rather than a flag on it.
            Assert.AreEqual(2f + 0.75f, WeaponHoldPointMath.ResolveMagicFallback(0f, 2f, BodyH, isSitting: false, blocked: true).y, Tolerance);
            Assert.AreEqual(2f + 0.35f, WeaponHoldPointMath.ResolveMagicFallback(0f, 2f, BodyH, isSitting: true,  blocked: true).y, Tolerance);
        }
    }
}
