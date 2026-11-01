using NUnit.Framework;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins the two bare-hoof strikes against <c>WKick.as</c> — the front punch and the back kick the
    /// player throws with <b>F</b>, and with <b>F + Shift</b>.
    ///
    /// <para><b>Why this fixture exists.</b> The port had an unarmed controller that modelled the
    /// <i>wrong</i> weapon: it reproduced <c>WPunch</c>'s aim-based back-hit (whose gate,
    /// <c>WPunch.zadok</c>, is never assigned anywhere in the oracle — dead code) and fired both
    /// branches at <c>rapid-5</c>. The live rule is <c>WKick</c>'s: a run-key flag, <c>rapid-8</c>, a
    /// fixed horizontal sweep at two heights for the kick and five for the punch, damage ×2 and
    /// <c>Pers.kickDestroy</c> on the kick only. Every number below is copied from the oracle, not
    /// from the port — the two disagreed on all of them.</para>
    ///
    /// <para><b>Order is asserted, not just membership.</b> AS3 feeds each <c>bindMove</c> into the
    /// bullet's waypoint list and the bullet walks it in order, so the first segment is the first tile
    /// it can break. A fixture that compared sets would pass while a low wall stopped the wrong line
    /// first.</para>
    ///
    /// <para>AS3 authority: <c>fe/weapon/WKick.as:24-108</c>; the flag's source
    /// <c>UnitPlayer.as:2379</c>; the weapon's row <c>AllData.as:25-30</c> (<c>rapid='13'</c>,
    /// <c>damage='10'</c>, <c>knock='5'</c>, <c>destroy='4'</c>).</para>
    /// </summary>
    [TestFixture]
    public class HoofStrikeRulesTests
    {
        // A body at a round position so every expected coordinate is exact.
        private const float X      = 1000f;
        private const float FeetY  = 2000f;

        // ── Timing ────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(t_attack == rapid - 8)</c> (<c>WKick.as:37</c>). WKick has ONE fire offset; the punch
        /// is not WPunch's <c>rapid - 5</c>.
        /// </summary>
        [Test]
        public void FireFrame_IsEightBeforeTheEnd_ForBothModes()
        {
            Assert.AreEqual(8, HoofStrikeRules.FireFrameOffset,
                "WKick.as:37 fires at t_attack == rapid - 8 — for the front strike too.");

            // The player's punch row is rapid='13' (AllData.as:25), so the strike lands when 5 frames
            // remain. Stated as a number so a change to either side shows up here.
            const int rapid = 13;
            Assert.AreEqual(5, rapid - HoofStrikeRules.FireFrameOffset);
        }

        // ── Front strike ──────────────────────────────────────────────────────

        /// <summary>
        /// The front strike binds <b>five</b> horizontal lines at 60 px reach, from 60 px in to 20 px
        /// in, in the oracle's order: the 30 px line first (bound explicitly), then the 10/20/40/50
        /// ladder with the 30 px rung skipped (<c>WKick.as:91-104</c>).
        /// </summary>
        [Test]
        public void Punch_BindsFiveLines_SixtyToTwenty_InLadderOrder()
        {
            HoofSegment[] segs = HoofStrikeRules.Segments(
                kick: false, overLowTile: false, x: X, feetY: FeetY, storona: 1f);

            Assert.AreEqual(5, segs.Length, "WKick.as:93-104 — five bindMove calls, not one.");

            CollectionAssert.AreEqual(
                new[] { 30f, 10f, 20f, 40f, 50f }, Heights(segs),
                "30 px first, then the ladder skipping the rung it already bound.");

            foreach (HoofSegment s in segs)
            {
                Assert.AreEqual(X + 60f, s.FromX, 0.001f, "Outer end is X + storona*60 (WKick.as:95).");
                Assert.AreEqual(X + 20f, s.ToX,   0.001f, "Inner end is X + storona*20.");
                Assert.AreEqual(s.FromY,  s.ToY,  0.001f, "Every line is horizontal — Y is never varied.");
            }

            // One exact segment, so the pixel arithmetic is pinned and not merely its shape.
            Assert.AreEqual(1060f, segs[0].FromX, 0.001f);
            Assert.AreEqual(1020f, segs[0].ToX,   0.001f);
            Assert.AreEqual(1970f, segs[0].FromY, 0.001f, "Y - 30 with Y down.");
        }

        /// <summary>
        /// With a low obstacle in front the high line is bound <b>first</b> and the ladder drops its
        /// 50 px rung — still five lines, different order (<c>WKick.as:91-104</c>).
        /// </summary>
        [Test]
        public void Punch_OverLowTile_BindsTheHighLineFirst_AndStillFiveLines()
        {
            HoofSegment[] segs = HoofStrikeRules.Segments(
                kick: false, overLowTile: true, x: X, feetY: FeetY, storona: 1f);

            Assert.AreEqual(5, segs.Length);
            CollectionAssert.AreEqual(new[] { 50f, 30f, 10f, 20f, 40f }, Heights(segs));
        }

        // ── Back strike ───────────────────────────────────────────────────────

        /// <summary>
        /// The kick binds <b>two</b> lines at 70 px reach, on the side <i>opposite</i> the body's
        /// facing (<c>storona = -owner.storona</c>, <c>WKick.as:59</c>), low line first.
        /// </summary>
        [Test]
        public void Kick_BindsTwoLines_SeventyToTwenty_BehindTheBody()
        {
            HoofSegment[] segs = HoofStrikeRules.Segments(
                kick: true, overLowTile: false, x: X, feetY: FeetY, storona: 1f);

            Assert.AreEqual(2, segs.Length, "WKick.as:73-79 — two bindMove calls.");
            CollectionAssert.AreEqual(new[] { 30f, 50f }, Heights(segs));

            foreach (HoofSegment s in segs)
            {
                Assert.AreEqual(X - 70f, s.FromX, 0.001f,
                    "Facing right, the kick reaches LEFT: storona is mirrored at WKick.as:59.");
                Assert.AreEqual(X - 20f, s.ToX, 0.001f);
            }
        }

        /// <summary>The kick's two lines swap order on the probe (<c>WKick.as:71-80</c>).</summary>
        [Test]
        public void Kick_OverLowTile_SwapsTheTwoLines()
        {
            HoofSegment[] segs = HoofStrikeRules.Segments(
                kick: true, overLowTile: true, x: X, feetY: FeetY, storona: 1f);

            Assert.AreEqual(2, segs.Length);
            CollectionAssert.AreEqual(new[] { 50f, 30f }, Heights(segs));
        }

        /// <summary>The kick reaches 10 px further than the punch — 70 vs 60 (<c>:73</c> vs <c>:95</c>).</summary>
        [Test]
        public void Kick_ReachesTenPixelsFurtherThanThePunch()
        {
            Assert.AreEqual(70f, HoofStrikeRules.KickReachPx);
            Assert.AreEqual(60f, HoofStrikeRules.PunchReachPx);
            Assert.AreEqual(20f, HoofStrikeRules.InsetPx, "Both end at X + storona*20.");
        }

        /// <summary>Facing left mirrors every segment; the kick stays behind, i.e. to the right.</summary>
        [Test]
        public void Segments_MirrorWithFacing_AndTheKickStaysBehind()
        {
            HoofSegment[] punch = HoofStrikeRules.Segments(false, false, X, FeetY, -1f);
            HoofSegment[] kick  = HoofStrikeRules.Segments(true,  false, X, FeetY, -1f);

            Assert.AreEqual(X - 60f, punch[0].FromX, 0.001f, "Punch follows the body's facing.");
            Assert.AreEqual(X + 70f, kick[0].FromX,  0.001f, "Kick is always the opposite side.");
        }

        // ── The probe ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>Boolean(_loc2_) &amp;&amp; Boolean(_loc3_) &amp;&amp; _loc3_.thre &lt; _loc2_.thre</c>
        /// (<c>WKick.as:62</c>, <c>:87</c>): both tiles must exist, and the comparison is strict.
        /// </summary>
        [Test]
        public void OverLowTile_NeedsBothTiles_AndAStrictlyWeakerTop()
        {
            Assert.IsTrue(HoofStrikeRules.OverLowTile(true, true, 10f, 5f),
                "A weaker top over a solid body is the case this exists for.");

            Assert.IsFalse(HoofStrikeRules.OverLowTile(true, true, 10f, 10f),
                "Equal thresholds take the normal path — the oracle compares with <, not <=.");
            Assert.IsFalse(HoofStrikeRules.OverLowTile(true, true, 5f, 10f),
                "A stronger top is not a low obstacle.");
            Assert.IsFalse(HoofStrikeRules.OverLowTile(false, true, 10f, 5f),
                "No tile at the lower probe ⇒ no flag (Boolean(_loc2_)).");
            Assert.IsFalse(HoofStrikeRules.OverLowTile(true, false, 10f, 5f),
                "No tile at the upper probe ⇒ no flag (Boolean(_loc3_)).");
        }

        // ── Damage ────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>b.damage *= 2</c>, <c>b.otbros *= 1.5</c> on the kick only (<c>WKick.as:67-68</c>), and
        /// the stun payload each branch carries (<c>:55</c>, <c>:70</c>).
        /// </summary>
        [Test]
        public void Kick_DoublesDamage_AndThePunchDoesNot()
        {
            Assert.AreEqual(2f,   HoofStrikeRules.KickDamageScale,     0.0001f);
            Assert.AreEqual(1.5f, HoofStrikeRules.KickKnockbackScale,  0.0001f);
            Assert.AreEqual(60f,  HoofStrikeRules.KickStunDamage,      0.0001f);
            Assert.AreEqual(30f,  HoofStrikeRules.PunchStunDamage,     0.0001f);

            // The punch's own multipliers are the oracle's silence: it writes neither, so the weapon's
            // row values stand (damage 10, knock 5 — AllData.as:26).
            Assert.AreEqual(10f * HoofStrikeRules.KickDamageScale, 20f, 0.0001f,
                "The player's 10-damage punch row becomes 20 on a kick.");
        }

        /// <summary>
        /// The two sounds are distinct in the oracle — <c>m_big</c> for the kick, <c>m_med</c> for the
        /// punch (<c>WKick.as:81</c>, <c>:105</c>) — and a single shared cue would make the back strike
        /// silent-but-wrong rather than obviously different.
        /// </summary>
        [Test]
        public void TheTwoStrikes_CarryDifferentSounds()
        {
            Assert.AreEqual("m_big", HoofStrikeRules.KickSound);
            Assert.AreEqual("m_med", HoofStrikeRules.PunchSound);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static float[] Heights(HoofSegment[] segs)
        {
            var heights = new float[segs.Length];
            for (int i = 0; i < segs.Length; i++) heights[i] = segs[i].HeightAboveFeet(FeetY);
            return heights;
        }
    }
}
