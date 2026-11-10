using NUnit.Framework;
using PFE.Sim.Combat.Sats;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// The fixture table for <see cref="SatsRules"/> — S.A.T.S.'s arithmetic pinned against numbers
    /// computed by hand from <c>fe/inter/Sats.as</c>, not against the port's own output.
    ///
    /// <para><b>Why every expected value is a literal.</b> A test that compares
    /// <c>HitChance(x)</c> to <c>HitChance(x)</c> cannot see the formula change. A test that
    /// compares it to <c>320/200/1.1</c> is only marginally better — it restates the expression, so a
    /// reader cannot tell whether the arithmetic was checked or copied. The literals below were
    /// evaluated from the Oracle's own source by hand, and each one carries the line it comes from.</para>
    /// </summary>
    [TestFixture]
    public sealed class SatsRulesTests
    {
        private const float Tol = 1e-5f;

        /// <summary>
        /// A neutral input: a 320 px-precision weapon, no spread, no minimum range, a standing target
        /// with dexterity 1 and an 80 px body. Individual tests override one field at a time so a
        /// failure names its own cause.
        /// </summary>
        private static SatsPrecInputs Neutral(
            float distancePx = 200f,
            float precisionPx = 320f,
            float antiPrecisionPx = 0f,
            float deviationDeg = 0f,
            float skillConf = 1f,
            float skillMultiplier = 1f,
            float mazil = 0f,
            float dexterity = 1f,
            float bodyHeightPx = 80f,
            float precMult = 1f)
        {
            return new SatsPrecInputs(
                precisionPx: precisionPx,
                precMult: precMult,
                skillMultiplier: skillMultiplier,
                precMultCont: 1f,
                antiPrecisionPx: antiPrecisionPx,
                deviationDeg: deviationDeg,
                mazil: mazil,
                skillConf: skillConf,
                distancePx: distancePx,
                targetDexterity: dexterity,
                targetBodyHeightPx: bodyHeightPx);
        }

        // ── getPrec: the precision term (Sats.as:332-335) ──────────────────────────────────

        [Test]
        public void Precision_IsCappedAt95()
        {
            // 320 / 200 / (1 + 0.1) = 1.4545…, so the cap is the only thing that decides the answer.
            float p = SatsRules.HitChance(Neutral(distancePx: 200f));

            Assert.AreEqual(0.95f, p, Tol,
                "Sats.as:350-353 clamps to 0.95. Without the clamp a pistol at two tiles reports 145%.");
        }

        [Test]
        public void Precision_FallsOffWithDistance()
        {
            // 320 / 400 / 1.1 = 0.727272…
            float p = SatsRules.HitChance(Neutral(distancePx: 400f));

            Assert.AreEqual(0.7272727f, p, Tol,
                "Sats.as:334 divides resultPrec by the distance in pixels and by (dexter + 0.1).");
        }

        [Test]
        public void Precision_WithNoPrecisionStat_FallsThroughTo95()
        {
            // `if (weapon.precision > 0)` is false, so p keeps its initial 1 and only the caps apply.
            float p = SatsRules.HitChance(Neutral(precisionPx: 0f));

            Assert.AreEqual(0.95f, p, Tol,
                "Sats.as:332-335: a weapon with no @prec skips the term entirely; p stays at its " +
                "initial 1 and the 0.95 cap decides.");
        }

        [Test]
        public void Precision_IncludesTheSkillMultiplierRamp()
        {
            // (1 + (1.5 - 1) * 0.5) = 1.25, so resultPrec = 400 and p = 400/400/1.1 = 0.909090…
            float p = SatsRules.HitChance(Neutral(distancePx: 400f, skillMultiplier: 1.5f));

            Assert.AreEqual(0.9090909f, p, Tol,
                "Weapon.as:1634's `(1 + (skill - 1) * 0.5)` is a 50%-per-multiplier ramp. Dropping it " +
                "makes every weapon read as if the player had no points in the skill.");
        }

        // ── getPrec: the antiprec branch (Sats.as:336-339) ─────────────────────────────────

        [Test]
        public void AntiPrecision_ReplacesTheTermInsideTheThreshold()
        {
            // (160/320 * 0.75 + 0.25) / 1.1 = 0.625 / 1.1 = 0.568181…
            float p = SatsRules.HitChance(Neutral(distancePx: 160f, antiPrecisionPx: 320f));

            Assert.AreEqual(0.5681818f, p, Tol,
                "Sats.as:338 ASSIGNS to p, it does not multiply: inside the minimum range the " +
                "long-range term is discarded and replaced by the ramp.");
        }

        [Test]
        public void AntiPrecision_IsWorsePointBlankThanAtTheThreshold()
        {
            float pointBlank = SatsRules.HitChance(Neutral(distancePx: 40f, antiPrecisionPx: 320f));
            float atThreshold = SatsRules.HitChance(Neutral(distancePx: 319f, antiPrecisionPx: 320f));

            Assert.Less(pointBlank, atThreshold,
                "The ramp runs 0.25 → 1.0 as distance grows to the threshold, so point-blank must be " +
                "the WORSE case. A port that multiplied instead of assigning would invert this.");
        }

        [Test]
        public void AntiPrecision_DoesNothingOutsideTheThreshold()
        {
            // 320 / 400 / 1.1 — identical to the plain precision case.
            float p = SatsRules.HitChance(Neutral(distancePx: 400f, antiPrecisionPx: 320f));

            Assert.AreEqual(0.7272727f, p, Tol,
                "`if (antiprec > 0 && d < antiprec)` — the branch is skipped at and beyond the " +
                "threshold, so the precision term survives.");
        }

        // ── getPrec: the deviation term (Sats.as:340-348) ──────────────────────────────────

        [Test]
        public void Deviation_ScalesTheResultWhenTheConeIsWiderThanTheBody()
        {
            // cone  = 8 / (1 + 0.01)          = 7.920792°
            // angle = atan2(80, 1000) in deg  = 4.573921°
            // p     = 320/1000/1.1 * 4.573921/7.920792
            float p = SatsRules.HitChance(Neutral(distancePx: 1000f, deviationDeg: 8f));

            Assert.AreEqual(0.1679869f, p, Tol,
                "Sats.as:342-347 multiplies by the ratio of the body's angular size to the cone. " +
                "This is the term that makes a distant shot worse than the precision line alone says.");
        }

        [Test]
        public void Deviation_IsSkippedWhenTheBodyIsWiderThanTheCone()
        {
            // atan2(80, 100) = 38.66° > 7.92°, so `if (dev > ang)` is false and p is untouched.
            float p = SatsRules.HitChance(Neutral(distancePx: 100f, deviationDeg: 8f));

            Assert.AreEqual(0.95f, p, Tol,
                "At close range the body subtends more than the cone, so the branch is skipped — but " +
                "320/100/1.1 is 2.9, which the 0.95 cap then catches. The cap is what this asserts.");
        }

        [Test]
        public void Deviation_UsesTheSkillMultiplierNotTheTier()
        {
            // cone at multiplier 1.0 = 7.920792°, at 2.0 = 3.980099°.
            float low = SatsRules.HitChance(Neutral(distancePx: 1000f, deviationDeg: 8f, skillMultiplier: 1f));
            float high = SatsRules.HitChance(Neutral(distancePx: 1000f, deviationDeg: 8f, skillMultiplier: 2f));

            Assert.Less(low, high,
                "Sats.as:344 divides by `weaponSkills[skill]`, the multiplier. Passing the TIER here " +
                "(0..5) would divide by 0.01 at tier 0 and report a 0% hit chance for every new player.");
        }

        [Test]
        public void Mazil_AloneEnablesTheDeviationBranch()
        {
            // cone = 0/1.01 + 4 = 4°; angle = atan2(80,1000) = 4.573921° > 4 → branch skipped.
            float skipped = SatsRules.HitChance(Neutral(distancePx: 1000f, mazil: 4f));
            // cone = 4 + 4 = 8° > 4.573921° → p *= 0.571740…
            float applied = SatsRules.HitChance(Neutral(distancePx: 1000f, mazil: 8f));

            Assert.AreEqual(0.2909091f, skipped, Tol,
                "`if (deviation > 0 || mazil > 0)` — mazil opens the branch even with no deviation.");
            Assert.AreEqual(0.1663244f, applied, Tol,
                "mazil is a flat addend to the cone, in degrees, applied AFTER the skill division.");
        }

        // ── getPrec: the two caps (Sats.as:349-356) ───────────────────────────────────────

        [Test]
        public void SkillConfidenceCap_BitesAfterTheNinetyFiveCap()
        {
            // 320/200/1.1 * 0.6 = 0.872727… → not above 0.95, so only the skillConf cap decides.
            float p = SatsRules.HitChance(Neutral(distancePx: 200f, skillConf: 0.6f));

            Assert.AreEqual(0.6f, p, Tol,
                "Sats.as:354-356 caps at skillConf as well as at 0.95. The two caps commute " +
                "(min(p, 0.95, skillConf) either way), so this test pins that the confidence cap " +
                "EXISTS — a port that kept only the 0.95 ceiling reports 95% for a gap-2 weapon.");
        }

        [Test]
        public void SkillConfidenceCap_DoesNotRaiseALowResult()
        {
            // 320/1000/1.1 * 0.6 = 0.174545…, well under both caps.
            float p = SatsRules.HitChance(Neutral(distancePx: 1000f, skillConf: 0.6f));

            Assert.AreEqual(0.1745455f, p, Tol,
                "skillConf is a ceiling, not a floor. A port that assigned skillConf instead of " +
                "capping would report 60% for a shot that cannot hit.");
        }

        [Test]
        public void PercentForLabel_RoundsHalfAwayFromZero()
        {
            Assert.AreEqual(17, SatsRules.PercentForLabel(0.1679869f));
            Assert.AreEqual(13, SatsRules.PercentForLabel(0.125f),
                "AS3 Math.round is half-up. .NET's default is banker's rounding, which gives 12 here — " +
                "the label would read one point low at exactly the boundaries a 0.05 AP price produces.");
        }

        // ── Cost (Sats.as:241, :288, :309; SatsCel.as:40) ─────────────────────────────────

        [Test]
        public void FullCellCost_IsTheOracleExpression()
        {
            // 40 * 1 / 0.8 * 1 = 50
            Assert.AreEqual(50f, SatsRules.FullCellCost(40f, 1f, 0.8f, 1f), Tol);
        }

        [Test]
        public void FullCellCost_FoldsInThePerkAndTheMathlogicPerk()
        {
            // 40 * 0.75 / 1 * 0.85 = 25.5
            Assert.AreEqual(25.5f, SatsRules.FullCellCost(40f, 0.75f, 1f, 0.85f), Tol);
        }

        [Test]
        public void PerShotCost_DividesTheCellByItsRounds()
        {
            // 50 / 3 = 16.666…
            Assert.AreEqual(16.666667f, SatsRules.PerShotCost(40f, 1f, 0.8f, 1f, 3), Tol,
                "SatsCel.as:40 — `cons = param4 / param5`. A cell covering three shots charges a " +
                "third of the cell price per shot, and the cell price is what the queue reserves.");
        }

        [Test]
        public void PerShotCost_TreatsAZeroQueueAsOne()
        {
            Assert.AreEqual(50f, SatsRules.PerShotCost(40f, 1f, 0.8f, 1f, 0), Tol,
                "satsQue defaults to 1 and no data row carries 0, but a division by zero would be " +
                "silent infinity in a HUD.");
        }

        // ── Affordability (Sats.as:239, :209) ────────────────────────────────────────────

        [Test]
        public void CanQueue_RefusesWhenThePriceExceedsTheProjectedPool()
        {
            Assert.IsFalse(SatsRules.CanQueue(50f, 49.9f));
            Assert.IsTrue(SatsRules.CanQueue(50f, 50f),
                "The Oracle tests `price > odv`, so equality is affordable. A `>=` here refuses the " +
                "exact-full-pool case a player can see.");
        }

        [Test]
        public void CanAffordNextShot_MultipliesConsMultTwice()
        {
            // perShot 25, consMult 0.75 → 18.75. At od 19 the plan survives although the pool is
            // below the 25 the cell actually charges — the quirk at Sats.as:209.
            Assert.IsTrue(SatsRules.CanAffordNextShot(25f, 0.75f, 19f),
                "Sats.as:209 applies consMult on top of an already-discounted price. For a pistol with " +
                "the pistol perk the check is laxer than the charge, and od can go negative.");

            // Same pool, consMult 1: 25 > 19, so the plan is cancelled.
            Assert.IsFalse(SatsRules.CanAffordNextShot(25f, 1f, 19f),
                "With consMult 1 the doubled and single forms agree — this is the control that proves " +
                "the previous assertion is about the doubling and not about the comparison.");
        }

        // ── Regeneration (Sats.as:214-219) ───────────────────────────────────────────────

        [Test]
        public void Regen_StopsAtTheCap()
        {
            Assert.AreEqual(75f, SatsRules.RegenStep(75f, 75f, 0.1f, queueEmpty: true), Tol);
        }

        [Test]
        public void Regen_StepsByOddBelowTheCap()
        {
            Assert.AreEqual(74.95f + 0.1f, SatsRules.RegenStep(74.95f, 75f, 0.1f, queueEmpty: true), Tol,
                "The test is on od BEFORE the add, so the last step overshoots to 75.05. Reproduced, " +
                "not clamped — the clamp is invisible in play and changes the fixture.");
        }

        [Test]
        public void Regen_IsSuspendedWhileTheQueueIsNotEmpty()
        {
            Assert.AreEqual(20f, SatsRules.RegenStep(20f, 75f, 0.1f, queueEmpty: false), Tol,
                "`que.length == 0` is the first condition. A queued plan owns its AP, so regenerating " +
                "under it would let a player queue more than the pool allows.");
        }

        // ── The queue (Sats.as:297-321) ──────────────────────────────────────────────────

        [Test]
        public void Ready_FreeAimCell_AlwaysFires()
        {
            Assert.IsTrue(SatsRules.ShouldFireFront(hasUnitTarget: false, burstBegun: false, targetAlive: false),
                "A free-aim cell has no unit, so `que[0].un == null` short-circuits the whole test.");
        }

        [Test]
        public void Ready_UnitCell_DropsWhenTheTargetDiedBeforeTheBurst()
        {
            Assert.IsFalse(SatsRules.ShouldFireFront(hasUnitTarget: true, burstBegun: false, targetAlive: false),
                "Sats.as:290 — a dead target with an unstarted burst is dropped from the FRONT " +
                "(unsetCel(true)), not from the back.");
        }

        [Test]
        public void Ready_UnitCell_KeepsFiringOnceTheBurstHasBegun()
        {
            Assert.IsTrue(SatsRules.ShouldFireFront(hasUnitTarget: true, burstBegun: true, targetAlive: false),
                "`begined` is checked before the aliveness test, so the remaining rounds of a started " +
                "burst still leave the barrel at a corpse.");
        }

        [Test]
        public void Ready_UnitCell_FiresWhileTheTargetLives()
        {
            Assert.IsTrue(SatsRules.ShouldFireFront(hasUnitTarget: true, burstBegun: false, targetAlive: true));
        }

        [Test]
        public void Act_DecrementsWhileRoundsRemain()
        {
            Assert.IsFalse(SatsRules.ShotPopsFrontCell(remainingRounds: 3, weaponStatus: 0),
                "Sats.as:311 — `kol > 1 && status() <= 1` decrements and sets begined; the cell stays.");
        }

        [Test]
        public void Act_PopsOnTheLastRound()
        {
            Assert.IsTrue(SatsRules.ShotPopsFrontCell(remainingRounds: 1, weaponStatus: 0));
        }

        [Test]
        public void Act_PopsEarlyWhenTheWeaponCouldNotFire()
        {
            Assert.IsTrue(SatsRules.ShotPopsFrontCell(remainingRounds: 3, weaponStatus: 2),
                "status 2 is jammed / no round in the magazine. The Oracle pops the cell rather than " +
                "waiting for a reload the plan does not perform.");
            Assert.IsTrue(SatsRules.ShotPopsFrontCell(remainingRounds: 3, weaponStatus: 5),
                "status 5 is a broken weapon — the whole queue is cleared by the caller anyway, but " +
                "act()'s own rule pops the cell.");
        }

        // ── Target admission: Unit.getTileVisi() (Unit.as:4527-4530) ─────────────────────

        [Test]
        public void Fog_NoMaskMeansFullyVisible()
        {
            // RoomBackdropRenderer.TryGetFogVisibility returns false with `visibility = 1` when fog
            // is off, the mask has not been built, or the room is collision-only. Every one of those
            // states draws nothing over the unit, so all of them must ADMIT the target. Pinning the
            // value alongside the flag stops a caller from "fixing" the seam to return 0 here.
            Assert.IsTrue(SatsRules.FogAdmitsTarget(fogMaskReadBack: false, visibility: 1f, threshold: 0.3f));
            Assert.IsTrue(SatsRules.FogAdmitsTarget(fogMaskReadBack: false, visibility: 0f, threshold: 0.3f),
                "With no mask the sampled value is meaningless; the unit is visible regardless of it.");
        }

        [Test]
        public void Fog_MaskRejectsDeepShadowButAdmitsLitTiles()
        {
            Assert.IsTrue(SatsRules.FogAdmitsTarget(true, 1f, 0.3f));
            Assert.IsTrue(SatsRules.FogAdmitsTarget(true, 0.31f, 0.3f));
            Assert.IsFalse(SatsRules.FogAdmitsTarget(true, 0f, 0.3f));
            Assert.IsFalse(SatsRules.FogAdmitsTarget(true, 0.29f, 0.3f));
        }

        [Test]
        public void Fog_ThresholdComparisonIsStrict()
        {
            // `visi > param1`, not `>=`. The exact boundary is reachable because the mask is a
            // smoothed float, and admitting it would add a ring of half-lit targets the Oracle skips.
            Assert.IsFalse(SatsRules.FogAdmitsTarget(true, 0.3f, 0.3f));
        }

        // ── Presentation geometry ────────────────────────────────────────────────────────

        [Test]
        public void Bracket_GrowsTenPixelsPerQueuedCell()
        {
            Assert.AreEqual(0.6f, SatsRules.BracketScale(50f, 1), Tol,
                "SatsCel.as:35 increments un.n BEFORE the scale is computed, so the first bracket is " +
                "already body + 10.");
            Assert.AreEqual(0.8f, SatsRules.BracketScale(50f, 3), Tol);
        }

        [Test]
        public void OdBar_IsScaledByFifty()
        {
            Assert.AreEqual(1f, SatsRules.OdBarScale(50f), Tol);
            Assert.AreEqual(1.6f, SatsRules.OdBarScale(80f), Tol,
                "The initial pool of 80 scales to 1.6 — the fill is masked, so it saturates rather " +
                "than stretching. GUI.as:885-899.");
        }

        // ── Charging an automatic weapon's burst ──────────────────────────────────────────

        [Test]
        public void Charges_MinigunCellIsFifteenRoundsNotOneShot()
        {
            // AllData.txt: `<weapon id='minigun' …><sats que='15' cons='25'/><ammo holder='500'/>`.
            // One queued cell owes 15 act() calls. Collapsing the burst to a single bool charge per
            // tick is the defect that made an accepted cell empty the whole 500-round magazine.
            Assert.AreEqual(15, SatsRules.ChargesForShots(shotsNow: 15, baseline: 0, roundsOwed: 15));
            Assert.AreEqual(1, SatsRules.ChargesForShots(shotsNow: 1, baseline: 0, roundsOwed: 15),
                "The steady case: one round left the barrel since the previous tick, one charge.");
            Assert.AreEqual(25f / 15f, SatsRules.PerShotCost(25f, 1f, 1f, 1f, 15), Tol,
                "SatsCel.as:40 — each of the 15 rounds debits price / 15 = 1.6667 AP, not the " +
                "full 25. The cell price is what the queue reserves up front.");
            Assert.AreEqual(25f, SatsRules.PerShotCost(25f, 1f, 1f, 1f, 15) * 15f, Tol,
                "Charging all 15 rounds spends exactly one cell price — no more, no less.");
        }

        [Test]
        public void Charges_OneRoundPerChargeAcrossThreeCells()
        {
            // Three minigun cells = 45 rounds. The counter difference is the charge count, so a
            // Tick() that ran more than one flash frame still drains the plan correctly.
            Assert.AreEqual(45, SatsRules.ChargesForShots(45, 0, 45));
            Assert.AreEqual(30, SatsRules.ChargesForShots(45, 15, 30),
                "A mid-plan sample: 30 rounds still owed after 15 were already charged.");
        }

        [Test]
        public void Charges_EmptyQueueAndBackwardsCounterChargeNothing()
        {
            Assert.AreEqual(0, SatsRules.ChargesForShots(0, 0, 0),
                "No queue: nothing to charge, even if the counter moved.");
            Assert.AreEqual(0, SatsRules.ChargesForShots(5, 5, 15),
                "No new round since the last check.");
            Assert.AreEqual(0, SatsRules.ChargesForShots(0, 500, 15),
                "A fresh weapon's counter restarted at zero. A negative delta is a swap, not a burst.");
        }

        [Test]
        public void Charges_NeverExceedsWhatThePlanOwes()
        {
            Assert.AreEqual(3, SatsRules.ChargesForShots(500, 0, 3),
                "A stale baseline must not let unrelated shots empty a brand-new plan: clamp to " +
                "the rounds the queue actually owes.");
            Assert.AreEqual(15, SatsRules.ChargesForShots(16, 0, 15),
                "One extra round past a 15-round cell must not spill into a plan that is already spent.");
        }

        [Test]
        public void Burst_AMinigunCellPopsOnTheFifteenthShot()
        {
            // A pure replay of Sats.act() over one cell, with weapon.status() == 0 (ready).
            // Sats.as:311: `if(kol > 1 && status <= 1) { --kol; begined = true; } else { shift(); }`.
            int kol = 15;
            int shots = 0;

            while (true)
            {
                shots++;
                if (SatsRules.ShotPopsFrontCell(kol, 0)) break;
                kol--;
                Assert.Less(shots, 100, "The burst never terminated — Sats.act() would never empty the queue.");
            }

            Assert.AreEqual(15, shots,
                "One minigun cell must fire exactly 15 rounds and then be popped. Firing a 16th " +
                "means the plan outlived its cell; firing fewer means the burst was cut short.");
            Assert.AreEqual(1, kol,
                "The cell is popped when kol reaches 1, not when it reaches 0.");
        }

        [Test]
        public void Burst_ABrokenBurstPopsOnItsFirstShot()
        {
            // status 2 = jam / no round in the magazine (Sats.as:311's `status() <= 1` fails).
            // The remaining rounds are abandoned rather than waiting for a reload the plan skips.
            Assert.IsTrue(SatsRules.ShotPopsFrontCell(15, 2));
            Assert.IsFalse(SatsRules.ShotPopsFrontCell(15, 0));
        }
    }
}
