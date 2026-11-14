using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Combat;

namespace PFE.Tests.EditMode.Sim
{
    /// <summary>
    /// Offline fixtures for <see cref="BossEnclRules"/> — the Enclave Commander's three-way loadout,
    /// its flight box, the shared firing gate and the difficulty multiply
    /// (<c>UnitBossEncl.as</c>, <c>Coord.as</c>).
    ///
    /// <para><b>Every expected value is written from the AS3 expression, and the line is named in the
    /// message.</b> The boss has no phase table and no state ladder, so almost nothing about it is
    /// structural — it is a handful of literals and four inequalities, and a port can get every one of
    /// them wrong while still compiling, running and looking plausible. That is the exact failure mode
    /// <c>90_Boss_Implementation.md</c> §13.1 records for the alicorn.</para>
    /// </summary>
    [TestFixture]
    public class BossEnclRulesTests
    {
        // ── the per-tr loadout (UnitBossEncl.as:50-73) ────────────────────────────────────────────

        /// <summary>
        /// <c>if(tr==1) "lmg"; if(tr==2) "quick"; if(tr==3) "mlau"</c> (<c>:52</c>, <c>:58</c>,
        /// <c>:65</c>) — and <c>null</c> for anything else.
        /// </summary>
        [Test]
        public void WeaponIdFor_IsThreeSeparateIfs_WithNoDefault()
        {
            Assert.AreEqual("lmg", BossEnclRules.WeaponIdFor(1), "tr1: Weapon.create(this,\"lmg\") (:52)");
            Assert.AreEqual("quick", BossEnclRules.WeaponIdFor(2), "tr2: Weapon.create(this,\"quick\") (:58)");
            Assert.AreEqual("mlau", BossEnclRules.WeaponIdFor(3), "tr3: Weapon.create(this,\"mlau\") (:65)");

            Assert.IsNull(BossEnclRules.WeaponIdFor(0),
                "tr0: no `if` matches, so currentWeapon stays null — the commander flies and never fires");
            Assert.IsNull(BossEnclRules.WeaponIdFor(4),
                "tr4: `coord.tr` only ever reaches 1..3, so this is unreachable rather than defaulted");
        }

        /// <summary>
        /// <c>armor = 20</c> (tr1, <c>:53</c>), <c>marmor = 20</c> (tr2, <c>:59</c>),
        /// <c>armor = marmor = 10</c> (tr3, <c>:70</c>).
        ///
        /// <para><b>The row supplies 0/0, so this is the only source of the ratings.</b>
        /// <c>bossencl.asset</c> reads <c>armor: 0</c> / <c>magicArmor: 0</c> because
        /// <c>AllData.as:637-647</c> authors neither attribute — the numbers exist only in the
        /// constructor, which is why every commander used to spawn unarmoured.</para>
        /// </summary>
        [Test]
        public void ArmourFor_MatchesTheThreeConstructorAssignments()
        {
            BossEnclRules.ArmourFor(1, out int p1, out int m1);
            Assert.AreEqual(20, p1, "tr1: armor = 20 (:53)");
            Assert.AreEqual(0, m1, "tr1 sets no marmor — the field keeps its 0");

            BossEnclRules.ArmourFor(2, out int p2, out int m2);
            Assert.AreEqual(0, p2, "tr2 sets no armor");
            Assert.AreEqual(20, m2, "tr2: marmor = 20 (:59)");

            BossEnclRules.ArmourFor(3, out int p3, out int m3);
            Assert.AreEqual(10, p3, "tr3: armor = marmor = 10 (:70)");
            Assert.AreEqual(10, m3, "tr3: armor = marmor = 10 (:70)");

            BossEnclRules.ArmourFor(0, out int p0, out int m0);
            Assert.AreEqual(0, p0, "no tr means no rating");
            Assert.AreEqual(0, m0, "no tr means no rating");
        }

        /// <summary>
        /// The three <c>vulner[...] = 0.7</c> triples, and — the part that is easy to get wrong — that
        /// <b>the sets do not overlap</b>: tr1 is bullet/phis/blade, tr2 is laser/plasma/spark, tr3 is
        /// expl/fire/cryo.
        ///
        /// <para><b>tr2 <i>raises</i> plasma from the row's 0.85.</b> <c>AllData.as:641</c> authors
        /// <c>plasma 0.85</c>, and the constructor writes the same slot again after the XML read, so the
        /// final value is 0.7. A port that "merged" the tables with a minimum, or that skipped the write
        /// because a value was already present, would leave the commander taking 21 % more plasma damage
        /// than the oracle — and nothing structural would notice.</para>
        /// </summary>
        [Test]
        public void ApplyVulnerabilities_WritesEachTrTriple_OnTopOfTheRow()
        {
            VulnerabilityData row = RowTable();

            VulnerabilityData tr1 = BossEnclRules.ApplyVulnerabilities(row, 1);
            Assert.AreEqual(0.7f, tr1.bullet, 1e-6f, "tr1: vulner[D_BUL] = 0.7 (:54)");
            Assert.AreEqual(0.7f, tr1.phis, 1e-6f, "tr1: vulner[D_PHIS] = 0.7 (:54)");
            Assert.AreEqual(0.7f, tr1.blade, 1e-6f, "tr1: vulner[D_BLADE] = 0.7 (:54)");
            Assert.AreEqual(0.7f, tr1.laser, 1e-6f, "tr1 does not touch laser — the row's 0.7 survives");
            Assert.AreEqual(0.85f, tr1.plasma, 1e-6f, "tr1 does not touch plasma — the row's 0.85 survives");
            Assert.AreEqual(1f, tr1.expl, 1e-6f, "tr1 does not touch expl");

            VulnerabilityData tr2 = BossEnclRules.ApplyVulnerabilities(RowTable(), 2);
            Assert.AreEqual(0.7f, tr2.laser, 1e-6f, "tr2: vulner[D_LASER] = 0.7 (:60)");
            Assert.AreEqual(0.7f, tr2.plasma, 1e-6f,
                "tr2: vulner[D_PLASMA] = 0.7 (:60) — RAISED from the row's 0.85, not merged");
            Assert.AreEqual(0.7f, tr2.spark, 1e-6f, "tr2: vulner[D_SPARK] = 0.7 (:60)");
            Assert.AreEqual(1f, tr2.bullet, 1e-6f, "tr2 does not touch bullet");
            Assert.AreEqual(0.5f, tr2.venom, 1e-6f, "tr2 leaves the row's venom at 0.5");

            VulnerabilityData tr3 = BossEnclRules.ApplyVulnerabilities(RowTable(), 3);
            Assert.AreEqual(0.7f, tr3.expl, 1e-6f, "tr3: vulner[D_EXPL] = 0.7 (:71)");
            Assert.AreEqual(0.7f, tr3.fire, 1e-6f, "tr3: vulner[D_FIRE] = 0.7 (:71)");
            Assert.AreEqual(0.7f, tr3.cryo, 1e-6f, "tr3: vulner[D_CRIO] = 0.7 (:71)");
            Assert.AreEqual(1f, tr3.blade, 1e-6f, "tr3 does not touch blade");
        }

        /// <summary>
        /// A <c>tr</c> outside 1..3 writes nothing at all — the row comes back untouched.
        /// </summary>
        [Test]
        public void ApplyVulnerabilities_UnknownTr_LeavesTheRowAlone()
        {
            VulnerabilityData result = BossEnclRules.ApplyVulnerabilities(RowTable(), 9);

            Assert.AreEqual(1f, result.bullet, 1e-6f, "no arm runs for tr=9");
            Assert.AreEqual(0.85f, result.plasma, 1e-6f, "no arm runs for tr=9");
            Assert.AreEqual(0.5f, result.venom, 1e-6f, "no arm runs for tr=9");
        }

        /// <summary>
        /// The row's own <c>&lt;vulner&gt;</c> table — <c>AllData.as:641</c>.
        ///
        /// <para><b>It starts from <see cref="VulnerabilityData.Neutral"/>, not from a bare struct.</b>
        /// This is a multiplier table and its neutral is <c>1</c>; a default-constructed
        /// <c>VulnerabilityData</c> is all zeros, which reads as <i>immunity to everything</i>. The
        /// imported row is "1 everywhere, with these three attributes overridden", and building it the
        /// other way would make every "the other fields are untouched" assertion below vacuous — which is
        /// exactly how this fixture caught its own first draft.</para>
        /// </summary>
        static VulnerabilityData RowTable()
        {
            VulnerabilityData row = VulnerabilityData.Neutral;
            row.laser = 0.7f;
            row.plasma = 0.85f;
            row.venom = 0.5f;
            return row;
        }

        // ── setLevel (UnitBossEncl.as:113-132) ────────────────────────────────────────────────────

        /// <summary>
        /// <c>_loc3_ = 1; if(globalDif == 3) 1.2; if(globalDif == 4) 1.5;</c> (<c>:117-125</c>) —
        /// <b>two independent <c>if</c>s with exact equality</b>, so 5 is 1.
        ///
        /// <para>The <c>5 → 1</c> case is the whole reason this test exists. A <c>&gt;= 4</c> ladder
        /// returns the same answer for every difficulty the game ships, so the difference is invisible in
        /// play and only ever shows up as a source that disagrees with the oracle.</para>
        /// </summary>
        [Test]
        public void DifficultyScale_IsExactEquality_NotALadder()
        {
            Assert.AreEqual(1f, BossEnclRules.DifficultyScale(1), 1e-6f, "easy: _loc3_ = 1 (:117)");
            Assert.AreEqual(1f, BossEnclRules.DifficultyScale(2), 1e-6f, "normal: _loc3_ = 1 (:117)");
            Assert.AreEqual(1.2f, BossEnclRules.DifficultyScale(3), 1e-6f, "if(globalDif == 3) 1.2 (:118-121)");
            Assert.AreEqual(1.5f, BossEnclRules.DifficultyScale(4), 1e-6f, "if(globalDif == 4) 1.5 (:122-125)");
            Assert.AreEqual(1f, BossEnclRules.DifficultyScale(5), 1e-6f,
                "5 matches neither `if`, so _loc3_ stays 1 — this is not a >= ladder");
        }

        // ── the firing gate (UnitBossEncl.as:306, Coord.as:16-18) ─────────────────────────────────

        /// <summary>
        /// <c>coord.tr == this.tr &amp;&amp; coord.t1 &gt; 45</c> (<c>:306</c>) — <b>strictly</b> greater.
        ///
        /// <para><c>t1 == 45</c> is the boundary, and the oracle's rhythm depends on it: a 150..209 tick
        /// window gives the focused commander 105..164 ticks of fire and then 45 ticks of silence before
        /// the right moves on. A <c>&gt;=</c> port fires for one extra tick per window, which no number a
        /// reviewer checks would reveal.</para>
        /// </summary>
        [Test]
        public void ShouldFire_GatesOnTheRightAndStrictlyAbove45()
        {
            Assert.IsTrue(BossEnclRules.ShouldFire(coordTr: 2, coordT1: 46, commanderTr: 2),
                "46 > 45 and the right is this commander's (:306)");
            Assert.IsFalse(BossEnclRules.ShouldFire(2, 45, 2),
                "45 is NOT > 45 — the 45-tick silence before the right moves on (:306)");
            Assert.IsFalse(BossEnclRules.ShouldFire(2, 0, 2), "0 > 45 is false");
            Assert.IsFalse(BossEnclRules.ShouldFire(1, 200, 2),
                "the right belongs to tr1, so tr2 must not fire even with a full window (:306)");
        }

        /// <summary>
        /// <c>++tr; if(tr &gt; 3) tr = 1; if(liv[tr]) break;</c> (<c>Coord.as:64-77</c>) — the rotation
        /// always moves on, and a dead commander is skipped.
        /// </summary>
        [Test]
        public void NextFocus_AdvancesAndSkipsTheDead()
        {
            Assert.AreEqual(2, BossEnclRules.NextFocus(1, true, true, true), "1 -> 2 (:67-71)");
            Assert.AreEqual(3, BossEnclRules.NextFocus(2, true, true, true), "2 -> 3");
            Assert.AreEqual(1, BossEnclRules.NextFocus(3, true, true, true), "3 -> 1: `if(tr > 3) tr = 1` (:68-71)");

            Assert.AreEqual(3, BossEnclRules.NextFocus(1, true, false, true),
                "tr2 is dead, so the first increment fails `if(liv[tr])` and the loop takes a second (:72-76)");
            Assert.AreEqual(2, BossEnclRules.NextFocus(3, false, true, true),
                "tr1 is dead, so 3 -> 1 fails and 1 -> 2 succeeds");
        }

        /// <summary>
        /// The loop is bounded by three increments (<c>Coord.as:65</c>), so a table where every commander
        /// is dead ends on the value it started from rather than spinning.
        ///
        /// <para><b>That is the oracle's behaviour, not a fallback.</b> The <c>while(_loc1_ &lt;= 3)</c>
        /// counter runs out and <c>tr</c> keeps whatever the last increment left it on — for a start of 1
        /// that is 1 again after three wraps. It is unreachable in a real fight (the room is over when the
        /// last commander dies) and is pinned so the bound cannot quietly become a fourth attempt.</para>
        /// </summary>
        [Test]
        public void NextFocus_AllDead_TerminatesAfterThreeIncrements()
        {
            Assert.AreEqual(1, BossEnclRules.NextFocus(1, false, false, false),
                "three failed increments from 1 land back on 1 (Coord.as:65-77)");
            Assert.AreEqual(2, BossEnclRules.NextFocus(2, false, false, false),
                "three failed increments from 2 land back on 2");
        }

        // ── the flight model (UnitBossEncl.as:258-294) ────────────────────────────────────────────

        const float MaxSpeed = 15f;   // bossencl speed='15' (AllData.as:639)
        const float Accel = 1.5f;     // bossencl accel='1.5' (AllData.as:639)

        /// <summary>
        /// <c>if(aiState != 0)</c> guards the whole block, so a stunned commander's phase does not even
        /// advance (<c>:258</c>).
        /// </summary>
        [Test]
        public void StepFlight_Inactive_FreezesPhaseAndVelocity()
        {
            var flight = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 500f, Dx = 3f, Dy = -2f,
                SinX = 1f, SinDelta = 0.05f, VerticalDirection = 1
            };

            BossEnclRules.StepFlight(ref flight, active: false, maxSpeed: MaxSpeed, accel: Accel);

            Assert.AreEqual(1f, flight.SinX, 1e-6f, "sinX += sinDX sits INSIDE the `if(aiState != 0)` (:259-260)");
            Assert.AreEqual(3f, flight.Dx, 1e-6f, "no horizontal push while stunned");
            Assert.AreEqual(-2f, flight.Dy, 1e-6f, "no vertical push while stunned");
        }

        /// <summary>
        /// Inside the box the latch decides the vertical push: <c>aiVNapr == 1</c> keeps accelerating
        /// <i>down</i>, <c>-1</c> keeps accelerating <i>up</i> (<c>:271-281</c>).
        /// </summary>
        [Test]
        public void StepFlight_InsideBox_PushesTheWayTheLatchPoints()
        {
            var down = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 500f, Dx = 0f, Dy = 0f,
                SinX = 0f, SinDelta = 0f, VerticalDirection = 1
            };
            BossEnclRules.StepFlight(ref down, true, MaxSpeed, Accel);
            Assert.AreEqual(Accel, down.Dy, 1e-6f, "aiVNapr == 1 -> dy += accel (:273-276)");

            var up = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 500f, Dx = 0f, Dy = 0f,
                SinX = 0f, SinDelta = 0f, VerticalDirection = -1
            };
            BossEnclRules.StepFlight(ref up, true, MaxSpeed, Accel);
            Assert.AreEqual(-Accel, up.Dy, 1e-6f, "aiVNapr == -1 -> dy -= accel (:277-280)");
        }

        /// <summary>
        /// Above the box's top edge (<c>Y &lt; 250</c>) the boss is pushed down and the latch is set to
        /// <c>1</c> (<c>:261-265</c>); below the bottom edge (<c>Y &gt; 850</c>) it is pushed up and the
        /// latch is set to <c>-1</c> (<c>:266-270</c>).
        /// </summary>
        [Test]
        public void StepFlight_OutsideTheBox_TurnsAroundAndReLatches()
        {
            var tooHigh = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 249f, Dx = 0f, Dy = 0f,
                SinX = 0f, SinDelta = 0f, VerticalDirection = -1
            };
            BossEnclRules.StepFlight(ref tooHigh, true, MaxSpeed, Accel);
            Assert.AreEqual(Accel, tooHigh.Dy, 1e-6f, "Y < minY -> dy += accel, i.e. downward (:261-265)");
            Assert.AreEqual(1, tooHigh.VerticalDirection, "and aiVNapr = 1 (:264)");

            var tooLow = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 851f, Dx = 0f, Dy = 0f,
                SinX = 0f, SinDelta = 0f, VerticalDirection = 1
            };
            BossEnclRules.StepFlight(ref tooLow, true, MaxSpeed, Accel);
            Assert.AreEqual(-Accel, tooLow.Dy, 1e-6f, "Y > maxY -> dy -= accel, i.e. upward (:266-270)");
            Assert.AreEqual(-1, tooLow.VerticalDirection, "and aiVNapr = -1 (:269)");
        }

        /// <summary>
        /// The boundary rows are <b>inclusive</b> on both sides: <c>Y &gt;= minY &amp;&amp; Y &lt;= maxY</c>
        /// (<c>:271</c>), so a boss sitting exactly on 250 or 850 still takes the in-box arm.
        /// </summary>
        [Test]
        public void StepFlight_BoxEdgesAreInclusive()
        {
            var top = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 250f, Dx = 0f, Dy = 0f,
                SinX = 0f, SinDelta = 0f, VerticalDirection = 1
            };
            BossEnclRules.StepFlight(ref top, true, MaxSpeed, Accel);
            Assert.AreEqual(Accel, top.Dy, 1e-6f,
                "Y == minY is inside the box (`Y >= minY`), so the latch arm runs, not the wall arm (:271)");

            var bottom = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 850f, Dx = 0f, Dy = 0f,
                SinX = 0f, SinDelta = 0f, VerticalDirection = -1
            };
            BossEnclRules.StepFlight(ref bottom, true, MaxSpeed, Accel);
            Assert.AreEqual(-Accel, bottom.Dy, 1e-6f, "Y == maxY is inside the box too (:271)");
        }

        /// <summary>
        /// Horizontal: the box walls accelerate the boss back inside (<c>:282-289</c>), and while inside
        /// it drifts by <c>Math.sin(sinX) * accel / 2</c> (<c>:290-293</c>).
        ///
        /// <para><b>The <c>/ 2</c> is real.</b> The drift is half an acceleration unit per frame, so the
        /// horizontal wander is gentler than the vertical bounce — a port that dropped the divisor would
        /// double the commander's side-to-side sweep and change how often it crosses the arena.</para>
        /// </summary>
        [Test]
        public void StepFlight_HorizontalWallsAndSineDrift()
        {
            var inside = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 500f, Dx = 0f, Dy = 0f,
                SinX = 0f, SinDelta = 0f, VerticalDirection = 1
            };
            BossEnclRules.StepFlight(ref inside, true, MaxSpeed, Accel);
            Assert.AreEqual(0f, inside.Dx, 1e-6f, "sin(0) == 0, so no drift this frame (:292)");

            // sinX advanced by sinDelta first, so pick a phase whose sine is known after the step.
            var drift = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 500f, Dx = 0f, Dy = 0f,
                SinX = 0f, SinDelta = 0f, VerticalDirection = 1
            };
            BossEnclRules.StepFlight(ref drift, true, MaxSpeed, Accel);
            Assert.AreEqual(0f, drift.Dx, 1e-6f, "phase 0 -> sin 0 -> no push");

            var quarter = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 500f, Dx = 0f, Dy = 0f,
                SinX = (float)(System.Math.PI / 2), SinDelta = 0f, VerticalDirection = 1
            };
            BossEnclRules.StepFlight(ref quarter, true, MaxSpeed, Accel);
            Assert.AreEqual(Accel / 2f, quarter.Dx, 1e-5f,
                "sin(pi/2) == 1 -> dx += accel / 2 (:292) — the divisor is in the oracle");

            var atLeftWall = new BossEnclRules.BossEnclFlight
            {
                X = 999f, Y = 500f, Dx = 0f, Dy = 0f,
                SinX = 0f, SinDelta = 0f, VerticalDirection = 1
            };
            BossEnclRules.StepFlight(ref atLeftWall, true, MaxSpeed, Accel);
            Assert.AreEqual(Accel, atLeftWall.Dx, 1e-6f,
                "X < minX -> dx += accel and NOT the drift arm (:282-285, :290)");

            var atRightWall = new BossEnclRules.BossEnclFlight
            {
                X = 1601f, Y = 500f, Dx = 0f, Dy = 0f,
                SinX = 0f, SinDelta = 0f, VerticalDirection = 1
            };
            BossEnclRules.StepFlight(ref atRightWall, true, MaxSpeed, Accel);
            Assert.AreEqual(-Accel, atRightWall.Dx, 1e-6f, "X > maxX -> dx -= accel (:286-289)");
        }

        /// <summary>
        /// Every arm is clamped at <c>maxSpeed</c> — <c>if(dy &lt; maxSpeed)</c> and friends — and the
        /// oracle applies <b>no damping at all</b>, so a boss already at the cap keeps it.
        /// </summary>
        [Test]
        public void StepFlight_ClampsAtMaxSpeed_AndNeverDamps()
        {
            var capped = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 500f, Dx = 0f, Dy = MaxSpeed,
                SinX = 0f, SinDelta = 0f, VerticalDirection = 1
            };
            BossEnclRules.StepFlight(ref capped, true, MaxSpeed, Accel);
            Assert.AreEqual(MaxSpeed, capped.Dy, 1e-6f,
                "dy == maxSpeed fails `dy < maxSpeed`, so it stays — there is no drag in control() (:273)");

            var overshoot = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 249f, Dx = 0f, Dy = MaxSpeed,
                SinX = 0f, SinDelta = 0f, VerticalDirection = 1
            };
            BossEnclRules.StepFlight(ref overshoot, true, MaxSpeed, Accel);
            Assert.AreEqual(MaxSpeed, overshoot.Dy, 1e-6f,
                "above the box with dy already at the cap: the wall arm's guard also refuses (:261)");
        }

        /// <summary>
        /// <c>sinX += sinDX</c> happens once per active frame, before the drift is read (<c>:260</c>,
        /// <c>:292</c>) — so the phase used by a frame is the one it just advanced to.
        /// </summary>
        [Test]
        public void StepFlight_AdvancesThePhaseBeforeReadingIt()
        {
            var flight = new BossEnclRules.BossEnclFlight
            {
                X = 1200f, Y = 500f, Dx = 0f, Dy = 0f,
                SinX = 0.25f, SinDelta = 0.05f, VerticalDirection = 1
            };

            BossEnclRules.StepFlight(ref flight, true, MaxSpeed, Accel);

            Assert.AreEqual(0.30f, flight.SinX, 1e-6f, "sinX += sinDX (:260)");
            Assert.AreEqual((float)System.Math.Sin(0.30f) * Accel / 2f, flight.Dx, 1e-6f,
                "the drift uses the ADVANCED phase (:292)");
        }

        /// <summary>
        /// The one axis mirror: AS3's <c>dy</c> is positive downward, the port's vertical speed is
        /// positive upward.
        /// </summary>
        [Test]
        public void PortVerticalSpeedFromAs3_FlipsTheSign()
        {
            Assert.AreEqual(-5f, BossEnclRules.PortVerticalSpeedFromAs3(5f), 1e-6f,
                "AS3 dy = +5 (down) is a NEGATIVE port speed");
            Assert.AreEqual(5f, BossEnclRules.PortVerticalSpeedFromAs3(-5f), 1e-6f,
                "AS3 dy = -5 (up) is a POSITIVE port speed");
            Assert.AreEqual(0f, BossEnclRules.PortVerticalSpeedFromAs3(0f), 1e-6f, "zero is zero");
        }

        /// <summary>
        /// The arena box, straight from the field initialisers (<c>:25-31</c>).
        /// </summary>
        [Test]
        public void ArenaBox_MatchesTheFieldInitialisers()
        {
            Assert.AreEqual(1000f, BossEnclRules.ArenaMinX, 1e-6f, "minX = 1000 (:29)");
            Assert.AreEqual(1600f, BossEnclRules.ArenaMaxX, 1e-6f, "maxX = 1600 (:31)");
            Assert.AreEqual(250f, BossEnclRules.ArenaMinYAs3, 1e-6f, "minY = 250 (:25)");
            Assert.AreEqual(850f, BossEnclRules.ArenaMaxYAs3, 1e-6f, "maxY = 850 (:27)");
        }

        // ── control()'s timers (UnitBossEncl.as:236-255) ──────────────────────────────────────────

        /// <summary>
        /// <c>if(aiTCh % 40 == 1)</c> (<c>:245</c>) — phase <b>1</b>, and the value tested is the
        /// countdown <i>after</i> the decrement, which is why the poll never lands on a multiple of 40.
        /// </summary>
        [Test]
        public void IsCelPollTick_UsesPhaseOne()
        {
            Assert.IsTrue(BossEnclRules.IsCelPollTick(1), "1 % 40 == 1 (:245)");
            Assert.IsTrue(BossEnclRules.IsCelPollTick(41), "41 % 40 == 1");
            Assert.IsTrue(BossEnclRules.IsCelPollTick(161), "161 % 40 == 1");
            Assert.IsFalse(BossEnclRules.IsCelPollTick(40), "40 % 40 == 0 — a port with phase 0 polls here");
            Assert.IsFalse(BossEnclRules.IsCelPollTick(0), "0 % 40 == 0");
            Assert.IsFalse(BossEnclRules.IsCelPollTick(42), "42 % 40 == 2");
        }

        /// <summary>
        /// <c>Math.floor(Math.random() * 60 + 150)</c> (<c>:243</c>) — 150..209 inclusive, which is what
        /// a draw of 0..59 plus 150 gives.
        /// </summary>
        [Test]
        public void AiTicksFor_Covers150To209()
        {
            Assert.AreEqual(150, BossEnclRules.AiTicksFor(0), "floor(0 * 60 + 150) (:243)");
            Assert.AreEqual(209, BossEnclRules.AiTicksFor(59), "floor(0.983 * 60 + 150) — 59 is the last draw (:243)");
        }

        // ── animate() (UnitBossEncl.as:134-164) ──────────────────────────────────────────────────

        /// <summary>
        /// <c>if(isFly) animState = "fly"; else "stay"</c>, with the port's corpse arm on top because the
        /// unit authors no <c>die</c> row.
        /// </summary>
        [Test]
        public void ResolveAnimState_IsFlyUnlessDead()
        {
            Assert.AreEqual("fly", BossEnclRules.ResolveAnimState(alive: true, isFly: true), "isFly -> \"fly\" (:139-142)");
            Assert.AreEqual("stay", BossEnclRules.ResolveAnimState(true, false), "!isFly -> \"stay\" (:143-146)");
            Assert.AreEqual("stay", BossEnclRules.ResolveAnimState(false, true),
                "a corpse holds the one-frame stay row — the port's convention, since no `die` row exists");
        }

        /// <summary>
        /// The body each commander draws — <c>blitId</c> is set only for tr2 and tr3
        /// (<c>:61</c>, <c>:72</c>); tr1 keeps the row's <c>sprEnclboss1</c>.
        /// </summary>
        [Test]
        public void BlitIdFor_NamesOnlyTheTwoCodeSideBodies()
        {
            Assert.IsNull(BossEnclRules.BlitIdFor(1),
                "tr1 authors nothing — AllData.as:642's blit='sprEnclboss1' is the row's own sheet");
            Assert.AreEqual("sprEnclboss2", BossEnclRules.BlitIdFor(2), "tr2: blitId = \"sprEnclboss2\" (:61)");
            Assert.AreEqual("sprEnclboss3", BossEnclRules.BlitIdFor(3), "tr3: blitId = \"sprEnclboss3\" (:72)");
        }

        // ── tr3's launcher tuning (UnitBossEncl.as:66-69) ─────────────────────────────────────────

        /// <summary>
        /// The four numbers the constructor stamps on tr3's <c>mlau</c> (<c>:66-69</c>).
        /// </summary>
        [Test]
        public void Tr3LauncherTuning_MatchesTheConstructor()
        {
            Assert.AreEqual(12f, BossEnclRules.Tr3ProjectileSpeed, 1e-6f, "currentWeapon.speed = 12 (:66)");
            Assert.AreEqual(0.6f, BossEnclRules.Tr3BulletAcceleration, 1e-6f, "currentWeapon.accel = 0.6 (:67)");
            Assert.AreEqual(30, BossEnclRules.Tr3ReloadFrames, "currentWeapon.reload = 30 (:68)");
            Assert.AreEqual(0.6f, BossEnclRules.Tr3ExplosiveDamageScale, 1e-6f,
                "currentWeapon.damageExpl *= 0.6 (:69) — 250 becomes 150");
        }
    }
}
