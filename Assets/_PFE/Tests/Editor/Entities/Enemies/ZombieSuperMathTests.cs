using NUnit.Framework;
using PFE.Entities.Enemies;
using PFE.Entities.Enemies.Archetypes;

namespace PFE.Tests.Editor.Entities.Enemies
{
    /// <summary>
    /// Pins <see cref="ZombieSuperMath"/> — AS3 <c>UnitZombie</c>'s super-attack system,
    /// <c>setSuper()</c> / <c>findSuper()</c> / <c>superSila()</c> / <c>superSila2()</c>
    /// (<c>UnitZombie.as:954-1228</c>).
    ///
    /// <para><b>Why this fixture exists.</b> Nine variants share one brain and differ only by a
    /// <c>&lt;un ss='N'/&gt;</c> integer. Every one of them is a different wind-up length, a different
    /// duration, a different weapon and a different trigger threshold — and the failure mode is
    /// <i>silent</i>: the wrong variant simply plays a plausible-looking super at the wrong cadence.
    /// Nothing throws, nothing logs, and the only way to notice by hand is to spawn that exact variant
    /// and watch it for ninety ticks. Here all nine rows are asserted offline.</para>
    ///
    /// <para><b>What is deliberately NOT pinned here.</b> The wiring — that <c>ZombieBrain</c> enters
    /// <see cref="EnemyAIState.PrepareSuper"/> at the right moment, that the pounce's velocity is
    /// re-asserted every tick, that the spit weapon's trigger is released after one tick, that a
    /// resurrecting corpse gets back up — needs a <c>GameObject</c>, a room and a running sim. That half
    /// is owner-run.</para>
    /// </summary>
    [TestFixture]
    public sealed class ZombieSuperMathTests
    {
        private const float Tolerance = 1e-4f;

        // ── The nine dispatch rows ────────────────────────────────────────────────────────────

        /// <summary>
        /// Every <c>&lt;un ss='N'/&gt;</c> value resolves to the oracle's own six numbers, listed
        /// explicitly so growing the table cannot leave a stale hand-written row behind.
        /// </summary>
        /// <remarks>
        /// The values come from <c>setSuper()</c> (<c>UnitZombie.as:954-1037</c>) and are cross-checked
        /// against <c>AllData.as:768-904</c>, where the family is <c>&lt;unit id='zombie'&gt;</c> and the
        /// nine variants are <c>zombie1..zombie9</c>. <c>zombie0</c> carries no <c>ss</c> at all, which is
        /// why row 0 is in the table: it must be the untouched default, or the plain ghoul would gain a
        /// super it never had.
        /// </remarks>
        [Test]
        public void Resolve_EverySkillIndex_MatchesTheOraclesTable()
        {
            //                    ss  tPrep tSuper tZlo  vJump vDestroy optDist
            (int ss, int tPrep, int tSuper, int tZlo, float vJump, float vDestroy, float optDist)[] rows =
            {
                (0, 30, 75, 120, 30f, 20f, 200f), // no super — the plain ghoul (`zombie0`)
                (1, 30, 20,  90, 30f, 20f, 200f), // pounce
                (2, 10, 50, 120, 30f, 20f, 200f), // telekinesis
                (3, 18, 10, 150, 30f, 20f, 200f), // venom
                (4, 18, 10, 150, 30f, 20f, 200f), // acid
                (5, 30, 45, 140, 30f, 20f, 200f), // radiation
                (6, 15, 45, 220, 30f, 50f, 200f), // quake — the only row that raises vDestroy
                (7, 10, 50, 120, 30f, 20f, 400f), // necro — the only row that widens optDistAtt
                (8, 18, 10, 120, 30f, 20f, 200f), // pink
                (9, 30, 20,  75, 40f, 20f, 200f), // strong pounce — the ss-1 block, then two overrides
            };

            foreach ((int ss, int tPrep, int tSuper, int tZlo, float vJump, float vDestroy, float optDist) in rows)
            {
                ZombieSuperMath.Tuning t = ZombieSuperMath.Resolve(ss);

                Assert.That(t.TPrepSuper, Is.EqualTo(tPrep), $"ss {ss}: tPrepSuper");
                Assert.That(t.TSuper, Is.EqualTo(tSuper), $"ss {ss}: tSuper");
                Assert.That(t.TZlo, Is.EqualTo(tZlo), $"ss {ss}: tZlo");
                Assert.That(t.VJump, Is.EqualTo(vJump).Within(Tolerance), $"ss {ss}: vJump");
                Assert.That(t.VDestroy, Is.EqualTo(vDestroy).Within(Tolerance), $"ss {ss}: vDestroy");
                Assert.That(t.OptDistAttack, Is.EqualTo(optDist).Within(Tolerance), $"ss {ss}: optDistAtt");
            }
        }

        /// <summary>
        /// <c>ss 9</c> is not a tenth effect: <c>setSuper()</c> runs the <c>ss 1</c> block and then
        /// overwrites two of its three values (<c>:962-969</c>). The <b>order</b> is the rule, and folding
        /// the two blocks into one arm is the mutation this test exists to catch.
        /// </summary>
        [Test]
        public void Resolve_StrongPounce_KeepsTheSsOneBlockThenOverwritesIt()
        {
            ZombieSuperMath.Tuning one = ZombieSuperMath.Resolve(ZombieSuperMath.Pounce);
            ZombieSuperMath.Tuning nine = ZombieSuperMath.Resolve(ZombieSuperMath.PounceStrong);

            Assert.That(nine.TPrepSuper, Is.EqualTo(one.TPrepSuper),
                "ss 9 does not touch tPrepSuper, so it keeps the ss-1 value of 30.");

            Assert.That(nine.TSuper, Is.EqualTo(one.TSuper),
                "ss 9 does not touch tSuper either — a strong pounce still lasts 20 ticks, not the " +
                "default 75. A single-arm implementation that only set tZlo/vJump would leave this at 75.");

            Assert.That(nine.TZlo, Is.EqualTo(75),
                "UnitZombie.as:963 — `tZlo = 75`, overriding the ss-1 block's 90.");

            Assert.That(nine.TZlo, Is.Not.EqualTo(one.TZlo),
                "If these were equal the override was lost: ss 9 would fire 15 chase ticks LATER than " +
                "the oracle, and the variant would feel strictly weaker than the one it upgrades.");

            Assert.That(nine.VJump, Is.EqualTo(40f).Within(Tolerance),
                "UnitZombie.as:964 — `vJump = 40`, a third faster than the ss-1 launch.");

            Assert.That(nine.VJump, Is.GreaterThan(one.VJump),
                "The stronger variant must launch faster, not slower.");
        }

        /// <summary>
        /// <c>EffectiveSkill</c> folds 9 into 1, and leaves every other index alone. Everything
        /// downstream (<c>findSuper</c>, <c>superSila</c>, <c>superSila2</c>) tests this value, so applying
        /// it in the wrong place silently turns a strong pounce into an unknown skill.
        /// </summary>
        [Test]
        public void EffectiveSkill_FoldsNineIntoOne_AndLeavesTheRest()
        {
            Assert.That(ZombieSuperMath.EffectiveSkill(ZombieSuperMath.PounceStrong),
                Is.EqualTo(ZombieSuperMath.Pounce), "9 → 1, the rewrite at UnitZombie.as:962-969.");

            for (int ss = 0; ss <= 8; ss++)
            {
                Assert.That(ZombieSuperMath.EffectiveSkill(ss), Is.EqualTo(ss),
                    $"ss {ss} is already an effect index and must pass through unchanged.");
            }
        }

        // ── The weapon each skill fires ──────────────────────────────────────────────────────

        /// <summary>
        /// <c>WeaponId</c> — the four spit weapons (<c>UnitZombie.as:984, 995, 1022, 1034</c>), and
        /// <c>null</c> for every skill that fights with something other than a projectile.
        /// </summary>
        [Test]
        public void WeaponId_MapsTheFourSpits_AndNothingElse()
        {
            Assert.That(ZombieSuperMath.WeaponId(ZombieSuperMath.VenomSpit), Is.EqualTo("zombivenom"));
            Assert.That(ZombieSuperMath.WeaponId(ZombieSuperMath.AcidSpit), Is.EqualTo("zombiacid"));
            Assert.That(ZombieSuperMath.WeaponId(ZombieSuperMath.NecroSpit), Is.EqualTo("zombinecro"));
            Assert.That(ZombieSuperMath.WeaponId(ZombieSuperMath.PinkSpit), Is.EqualTo("zombipink"));

            // The non-spitting skills. `null` is the contract: `ZombieController.FireSuperWeapon`
            // treats it as a no-op, so a pounce or a quake never touches a weapon mount.
            Assert.That(ZombieSuperMath.WeaponId(0), Is.Null, "no super");
            Assert.That(ZombieSuperMath.WeaponId(ZombieSuperMath.Pounce), Is.Null, "pounce");
            Assert.That(ZombieSuperMath.WeaponId(ZombieSuperMath.Telekinesis), Is.Null, "telekinesis");
            Assert.That(ZombieSuperMath.WeaponId(ZombieSuperMath.Radiation), Is.Null, "radiation");
            Assert.That(ZombieSuperMath.WeaponId(ZombieSuperMath.Quake), Is.Null, "quake");

            Assert.That(ZombieSuperMath.WeaponId(ZombieSuperMath.PounceStrong), Is.Null,
                "ss 9 folds to ss 1 first, so it inherits the pounce's `null` rather than a weapon. " +
                "Reading the raw index here would return null anyway, but only by accident.");
        }

        /// <summary>
        /// <c>FiresWeaponOnSuper</c> is <b>not</b> the same set as <c>WeaponId != null</c>: <c>ss 7</c>
        /// owns a weapon but never fires it from <c>superSila()</c>.
        /// </summary>
        /// <remarks>
        /// <para>This is the one asymmetry in the system and it is easy to "fix" into a bug. The necro
        /// variant's <c>zombinecro</c> is fired by the <i>contact-attack</i> path — a 10 % roll between
        /// supers (<c>:947-950</c>) — not by the super itself. Its state 7 is the telekinetic pull, which
        /// lives entirely in <c>superSila2</c>.</para>
        ///
        /// <para>Collapsing the two predicates would make every necro super also spit, which is a
        /// different attack at a different moment.</para>
        /// </remarks>
        [Test]
        public void FiresWeaponOnSuper_ExcludesNecro_BecauseItsWeaponBelongsToTheContactPath()
        {
            Assert.That(ZombieSuperMath.FiresWeaponOnSuper(ZombieSuperMath.VenomSpit), Is.True);
            Assert.That(ZombieSuperMath.FiresWeaponOnSuper(ZombieSuperMath.AcidSpit), Is.True);
            Assert.That(ZombieSuperMath.FiresWeaponOnSuper(ZombieSuperMath.PinkSpit), Is.True);

            Assert.That(ZombieSuperMath.FiresWeaponOnSuper(ZombieSuperMath.NecroSpit), Is.False,
                "ss 7 HAS a weapon (WeaponId returns 'zombinecro') but superSila does not fire it.");

            Assert.That(ZombieSuperMath.WeaponId(ZombieSuperMath.NecroSpit), Is.Not.Null,
                "The two predicates disagree on ss 7 on purpose — assert both sides so a tidy-up that " +
                "unifies them fails here rather than in play.");

            Assert.That(ZombieSuperMath.FiresWeaponOnSuper(ZombieSuperMath.Pounce), Is.False);
            Assert.That(ZombieSuperMath.FiresWeaponOnSuper(ZombieSuperMath.Radiation), Is.False);
            Assert.That(ZombieSuperMath.FiresWeaponOnSuper(ZombieSuperMath.Quake), Is.False);
            Assert.That(ZombieSuperMath.FiresWeaponOnSuper(0), Is.False);
        }

        /// <summary>
        /// <c>IsTelekinesis</c> — ss 2 and ss 7 only. ss 7 is the one that is also in
        /// <see cref="ZombieSuperMath.WeaponId"/>'s set, which is why both predicates are asked.
        /// </summary>
        [Test]
        public void IsTelekinesis_IsSsTwoAndSsSeven()
        {
            Assert.That(ZombieSuperMath.IsTelekinesis(ZombieSuperMath.Telekinesis), Is.True);
            Assert.That(ZombieSuperMath.IsTelekinesis(ZombieSuperMath.NecroSpit), Is.True);

            Assert.That(ZombieSuperMath.IsTelekinesis(ZombieSuperMath.Pounce), Is.False);
            Assert.That(ZombieSuperMath.IsTelekinesis(ZombieSuperMath.VenomSpit), Is.False);
            Assert.That(ZombieSuperMath.IsTelekinesis(ZombieSuperMath.AcidSpit), Is.False);
            Assert.That(ZombieSuperMath.IsTelekinesis(ZombieSuperMath.Radiation), Is.False);
            Assert.That(ZombieSuperMath.IsTelekinesis(ZombieSuperMath.Quake), Is.False);
            Assert.That(ZombieSuperMath.IsTelekinesis(ZombieSuperMath.PinkSpit), Is.False);
            Assert.That(ZombieSuperMath.IsTelekinesis(0), Is.False);

            Assert.That(ZombieSuperMath.IsTelekinesis(ZombieSuperMath.PounceStrong), Is.False,
                "ss 9 folds to the pounce first — a strong pounce must not drag the player.");
        }

        // ── findSuper: the destination ───────────────────────────────────────────────────────

        /// <summary>
        /// A target <b>level</b> with the unit: the pounce aims straight at its centre, and no air
        /// search runs at all (<c>:1088-1092</c>).
        /// </summary>
        [Test]
        public void FindSuper_PounceAtALevelTarget_AimsAtItsCentre()
        {
            // selfY - celY = 0, so the `> 120` branch is not taken.
            ZombieSuperMath.FindSuper(
                ZombieSuperMath.Pounce,
                celX: 500f, celY: 1000f,
                selfX: 300f, selfY: 1000f,
                bodyHeight: 70f, tileSize: 40f,
                isAirAtAs3Tile: (col, row) => false, // never consulted on this path
                out float superX, out float superY);

            Assert.That(superX, Is.EqualTo(500f).Within(Tolerance), "aimed at the target's X");
            Assert.That(superY, Is.EqualTo(1000f).Within(Tolerance),
                "UnitZombie.as:1090 — `superY = celY`, the target's own centre.");
        }

        /// <summary>
        /// A target at least 120 px <b>above</b> the unit: the destination is the bottom edge of the
        /// target's tile row plus a body height (<c>:1046</c>), and the X comes from the air-column search.
        /// </summary>
        /// <remarks>
        /// <c>superY = row * tileY + tileY + scY</c> is deliberately <b>below</b> the sampled row's bottom
        /// edge by a full body height. Read as "land on the target's head" it would be wrong; it is a slam
        /// that puts the zombie's box overlapping the target's.
        /// </remarks>
        [Test]
        public void FindSuper_PounceAtATargetAbove_LandsABodyHeightBelowTheRow()
        {
            const float tileSize = 40f;
            const float bodyHeight = 70f;
            const float celY = 800f;
            const float selfY = 1000f; // 200 px below the target's centre → past the 120 gate

            // row = floor((celY + 40) / 40) = floor(840 / 40) = 21
            const int expectedRow = 21;
            float expectedSuperY = expectedRow * tileSize + tileSize + bodyHeight; // 840 + 40 + 70 = 950

            ZombieSuperMath.FindSuper(
                ZombieSuperMath.Pounce,
                celX: 500f, celY: celY,
                selfX: 300f, selfY: selfY,
                bodyHeight: bodyHeight, tileSize: tileSize,
                // The target's column and its left neighbour are both air.
                isAirAtAs3Tile: (col, row) => row == expectedRow && (col == 12 || col == 11),
                out float superX, out float superY);

            Assert.That(superY, Is.EqualTo(expectedSuperY).Within(Tolerance),
                "UnitZombie.as:1046 — `superY = _loc2_ * tileY + tileY + scY` = 21*40 + 40 + 70 = 950. " +
                "Omitting the `+ tileY` gives 910, and omitting `+ scY` gives 880 — both plausible-looking " +
                "and both put the zombie a body height off.");

            Assert.That(superX, Is.EqualTo(480f).Within(Tolerance),
                "col = floor(500/40) = 12, and both column 12 and column 11 are air, so the destination " +
                "is the target's own column: 12 * 40 = 480.");
        }

        /// <summary>
        /// The air search's nested order: the destination is the <b>nearer</b> column, and the nearer one
        /// is tested <i>inside</i> the outer test. Getting the nesting wrong picks a column two tiles away
        /// when the adjacent one was free.
        /// </summary>
        [Test]
        public void FindSuper_PrefersTheNearestAirColumn()
        {
            const float tileSize = 40f;
            const int row = 21;

            // Target column 12 is air, its left neighbour 11 is NOT, its right neighbour 13 IS.
            ZombieSuperMath.FindSuper(
                ZombieSuperMath.Pounce,
                celX: 500f, celY: 800f, selfX: 300f, selfY: 1000f,
                bodyHeight: 70f, tileSize: tileSize,
                isAirAtAs3Tile: (col, r) => r == row && (col == 12 || col == 13),
                out float superX, out _);

            Assert.That(superX, Is.EqualTo(520f).Within(Tolerance),
                "UnitZombie.as:1058-1062 — with the left neighbour blocked, the right one wins: " +
                "(col + 1) * tileY = 13 * 40 = 520.");
        }

        /// <summary>
        /// No air column anywhere → <c>superX</c> stays at its <c>-1</c> initialiser. That is the oracle's
        /// own outcome, and it is why the pounce's gate is <c>superX &gt; 0</c> rather than a null check.
        /// </summary>
        [Test]
        public void FindSuper_WithNoAirColumn_LeavesTheSentinel()
        {
            ZombieSuperMath.FindSuper(
                ZombieSuperMath.Pounce,
                celX: 500f, celY: 800f, selfX: 300f, selfY: 1000f,
                bodyHeight: 70f, tileSize: 40f,
                isAirAtAs3Tile: (col, row) => false,
                out float superX, out float superY);

            Assert.That(superX, Is.EqualTo(-1f).Within(Tolerance),
                "A failed search is a real outcome, not a crash: the wind-up plays, state 7 runs for " +
                "tSuper ticks, and nothing happens. Do not fall back to the target's position.");

            Assert.That(superY, Is.Not.EqualTo(0f),
                "superY is still computed (950) even though superX failed — the oracle's field " +
                "arithmetic does the same, and the -1 sentinel is what refuses the launch.");
        }

        /// <summary>
        /// Telekinesis, target level: the anchor is <b>where the zombie stands</b>, not where the target
        /// is (<c>:1093-1097</c>) — the pull drags the player to the zombie.
        /// </summary>
        [Test]
        public void FindSuper_TelekinesisAtALevelTarget_AnchorsOnTheUnitItself()
        {
            ZombieSuperMath.FindSuper(
                ZombieSuperMath.Telekinesis,
                celX: 500f, celY: 1000f, selfX: 300f, selfY: 1000f,
                bodyHeight: 70f, tileSize: 40f,
                isAirAtAs3Tile: (col, row) => false,
                out float superX, out float superY);

            Assert.That(superX, Is.EqualTo(300f).Within(Tolerance),
                "UnitZombie.as:1095 — `superX = X`, the unit's own centre.");

            Assert.That(superY, Is.EqualTo(1000f).Within(Tolerance),
                "UnitZombie.as:1096 — `superY = Y`, the unit's own feet. This OVERWRITES the " +
                "`celY + 70` set by the earlier telekinesis arm, so an implementation that returned " +
                "1070 here has the two branches in the wrong order.");
        }

        // ── tryPounce: the launch ────────────────────────────────────────────────────────────

        /// <summary>
        /// The gate is <c>superX &gt; 0 &amp;&amp; superY &gt; 0</c> — a failed <c>findSuper</c> refuses, and
        /// every output is zeroed rather than left at whatever the caller had.
        /// </summary>
        [Test]
        public void TryPounce_RefusesAFailedSearch_AndZeroesItsOutputs()
        {
            Assert.That(
                ZombieSuperMath.TryPounce(-1f, 950f, 0f, 100f, 30f,
                    out float vx1, out float vy1, out int ticks1),
                Is.False, "superX == -1 is the no-destination sentinel.");

            Assert.That((vx1, vy1, ticks1), Is.EqualTo((0f, 0f, 0)),
                "A refused launch must not leave a velocity behind — the brain reads these unconditionally.");

            Assert.That(
                ZombieSuperMath.TryPounce(480f, 0f, 0f, 100f, 30f,
                    out _, out _, out _),
                Is.False, "superY == 0 is refused too, because the same field doubles as 'unset'.");

            Assert.That(
                ZombieSuperMath.TryPounce(0f, 950f, 0f, 100f, 30f,
                    out _, out _, out _),
                Is.False, "superX == 0 is refused — the gate is `> 0`, not `>= 0`.");
        }

        /// <summary>
        /// The launch is a unit vector scaled by <c>vJump</c>, and its duration is the distance over
        /// <c>vJump</c> — <b>overwriting</b> the table's <c>tSuper</c> (<c>:1116-1120</c>).
        /// </summary>
        [Test]
        public void TryPounce_ScalesToVJump_AndDerivesItsDurationFromTheDistance()
        {
            // A 3-4-5 triangle: dx 300, dy 400, distance 500.
            Assert.That(
                ZombieSuperMath.TryPounce(300f, 500f, 0f, 100f, 30f,
                    out float vx, out float vy, out int ticks),
                Is.True);

            Assert.That(vx, Is.EqualTo(18f).Within(Tolerance), "300/500 * 30 = 18");
            Assert.That(vy, Is.EqualTo(24f).Within(Tolerance), "400/500 * 30 = 24");

            Assert.That(
                System.Math.Sqrt(vx * vx + vy * vy), Is.EqualTo(30f).Within(Tolerance),
                "The launch speed is exactly vJump in every direction — a launch that scaled the " +
                "components instead of normalising would travel faster on the diagonal.");

            Assert.That(ticks, Is.EqualTo(17),
                "UnitZombie.as:1116 — `tSuper = round(500 / 30)` = round(16.67) = 17. This is NOT the " +
                "table's 20 for ss 1; the pounce's duration is a function of how far it has to go.");
        }

        /// <summary>
        /// The duration is capped at 20 ticks, and the cap is a <c>&gt;</c> so a distance that lands
        /// exactly on 20 is not clamped.
        /// </summary>
        [Test]
        public void TryPounce_CapsItsDurationAtTwentyTicks()
        {
            // Exactly 600 px at vJump 30 → 20 ticks, unchanged.
            Assert.That(
                ZombieSuperMath.TryPounce(600f, 100f, 0f, 100f, 30f, out _, out _, out int exact),
                Is.True);
            Assert.That(exact, Is.EqualTo(20),
                "600 / 30 = 20 exactly. The guard is `if(tSuper > 20)`, so this must pass through " +
                "untouched — a `>=` would shorten it to 20 by luck and hide the difference, so the " +
                "next case is the real test.");

            // 1000 px at vJump 30 → 33 ticks, clamped to 20.
            Assert.That(
                ZombieSuperMath.TryPounce(600f, 900f, 0f, 100f, 30f, out _, out _, out int clamped),
                Is.True);
            Assert.That(clamped, Is.EqualTo(20),
                "UnitZombie.as:1118-1120 — a far target still stops after 20 ticks (600 px at vJump 30). " +
                "Without the cap the flight would outlive the state and the zombie would keep the " +
                "pounce velocity into the chase.");
        }

        /// <summary>
        /// The strong pounce's <c>vJump = 40</c> shortens the same distance's flight — which is the whole
        /// point of <c>ss 9</c>, and the only place the table's <c>vJump</c> reaches the arithmetic.
        /// </summary>
        [Test]
        public void TryPounce_AStrongerJump_ReachesTheSameDistanceInFewerTicks()
        {
            ZombieSuperMath.TryPounce(300f, 500f, 0f, 100f,
                ZombieSuperMath.Resolve(ZombieSuperMath.Pounce).VJump, out _, out _, out int normal);

            ZombieSuperMath.TryPounce(300f, 500f, 0f, 100f,
                ZombieSuperMath.Resolve(ZombieSuperMath.PounceStrong).VJump, out _, out _, out int strong);

            Assert.That(normal, Is.EqualTo(17), "500 / 30 = 16.67 → 17");

            // This assertion is the one that found a real divergence, and it is worth saying so: the
            // first version of TryPounce used `Mathf.RoundToInt`, which defers to .NET's `Math.Round`
            // and therefore rounds an exact .5 to EVEN. 500 / 40 is exactly 12.5, so the strong pounce
            // came out 12 ticks and flew one tick short of the oracle. AS3's `Math.round` rounds a
            // positive .5 UP, so the port now uses `floor(x + 0.5)`.
            Assert.That(strong, Is.EqualTo(13),
                "500 / 40 = 12.5 exactly → 13, NOT 12. An even-rounding implementation returns 12 here " +
                "and 13 everywhere else, so only an exact tie distinguishes the two rules.");

            Assert.That(strong, Is.Not.EqualTo(12),
                "The tie rule is the whole point: 12 is what banker's rounding produces, and it is " +
                "wrong against the oracle.");

            Assert.That(strong, Is.LessThan(normal),
                "ss 9 must arrive sooner, or the 'stronger' variant is slower than the one it upgrades.");
        }

        // ── telekinesis: the pull ────────────────────────────────────────────────────────────

        /// <summary>
        /// One tick of the pull accelerates by <c>teleAccel</c> toward the anchor — it does not set a
        /// velocity. Setting it would make the first tick of the pull a teleport.
        /// </summary>
        [Test]
        public void TelekinesisStep_AcceleratesTowardTheAnchor()
        {
            ZombieSuperMath.TelekinesisStep(
                anchorX: 100f, anchorY: 0f,
                targetX: 0f, targetY: 0f,
                targetVelocity: UnityEngine.Vector2.zero,
                out UnityEngine.Vector2 delta);

            Assert.That(delta.x, Is.EqualTo(2f).Within(Tolerance),
                "UnitZombie.as:1163-1192 — the whole 2 px/frame accel, because the axis is at rest.");

            Assert.That(delta.y, Is.EqualTo(0f).Within(Tolerance),
                "No vertical offset, so no vertical push.");

            Assert.That(delta.x, Is.EqualTo(ZombieSuperMath.TelekinesisAccel).Within(Tolerance),
                "Pinned to the constant rather than the literal 2, so a change to teleAccel is caught here.");
        }

        /// <summary>
        /// The speed cap is <b>per axis</b>, and a target already at the cap is not pushed — it is not
        /// slowed either. That asymmetry is the oracle's <c>dx &lt; teleSpeed</c> condition.
        /// </summary>
        [Test]
        public void TelekinesisStep_CapsPerAxis_AndNeverBrakes()
        {
            const float cap = ZombieSuperMath.TelekinesisSpeed;

            // Right at the cap: no push.
            ZombieSuperMath.TelekinesisStep(100f, 0f, 0f, 0f, new UnityEngine.Vector2(cap, 0f), out UnityEngine.Vector2 atCap);
            Assert.That(atCap.x, Is.EqualTo(0f).Within(Tolerance),
                "`dx < teleSpeed` is strict — at exactly 10 the pull stops pushing.");

            // A hair under: still pushed.
            ZombieSuperMath.TelekinesisStep(100f, 0f, 0f, 0f, new UnityEngine.Vector2(cap - 0.1f, 0f), out UnityEngine.Vector2 underCap);
            Assert.That(underCap.x, Is.EqualTo(2f).Within(Tolerance),
                "9.9 < 10, so the axis is still under the cap and gets the full accel.");

            // Far over the cap: still no push, and crucially not a NEGATIVE delta.
            ZombieSuperMath.TelekinesisStep(100f, 0f, 0f, 0f, new UnityEngine.Vector2(50f, 0f), out UnityEngine.Vector2 overCap);
            Assert.That(overCap.x, Is.EqualTo(0f).Within(Tolerance),
                "A fast target is NOT slowed by the pull. A delta of -2 here would mean the code " +
                "braked a player who was escaping, which is a different mechanic entirely.");

            // The negative direction mirrors: the cap is symmetric.
            ZombieSuperMath.TelekinesisStep(-100f, 0f, 0f, 0f, new UnityEngine.Vector2(-cap, 0f), out UnityEngine.Vector2 negAtCap);
            Assert.That(negAtCap.x, Is.EqualTo(0f).Within(Tolerance),
                "The same cap in the negative direction — an implementation that only tested " +
                "`velocity < teleSpeed` without the sign would push forever leftward.");
        }

        /// <summary>
        /// A zero-length pull produces no delta — the anchor and the target coincide, so there is no
        /// direction to accelerate in. Without the guard the normalisation would divide by zero.
        /// </summary>
        [Test]
        public void TelekinesisStep_WithNoOffset_ProducesNoDelta()
        {
            ZombieSuperMath.TelekinesisStep(100f, 100f, 100f, 100f, UnityEngine.Vector2.zero,
                out UnityEngine.Vector2 delta);

            Assert.That(delta, Is.EqualTo(UnityEngine.Vector2.zero),
                "Zero distance must yield zero, not NaN — a NaN here would propagate into the " +
                "victim's velocity and never recover.");
        }

        // ── radiation ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The aura's heal falls off linearly and reaches zero exactly at the edge
        /// (<c>:1134-1144</c>) — so the boundary is exclusive.
        /// </summary>
        [Test]
        public void RadiationHeal_FallsOffLinearly_AndIsZeroAtTheEdge()
        {
            const float heal = ZombieSuperMath.DefaultRadHeal; // 30
            const float radius = ZombieSuperMath.RadiationRadiusMax; // 1200

            Assert.That(ZombieSuperMath.RadiationHeal(heal, radius, 0f),
                Is.EqualTo(30f).Within(Tolerance), "At the centre the zombie gets the full heal.");

            Assert.That(ZombieSuperMath.RadiationHeal(heal, radius, radius * 0.5f),
                Is.EqualTo(15f).Within(Tolerance), "Half way out, half the heal.");

            Assert.That(ZombieSuperMath.RadiationHeal(heal, radius, radius),
                Is.EqualTo(0f).Within(Tolerance),
                "Exactly at the edge is zero — the test is `distance >= radius`, so the edge is outside.");

            Assert.That(ZombieSuperMath.RadiationHeal(heal, radius, radius + 1f),
                Is.EqualTo(0f).Within(Tolerance), "Beyond the edge is also zero, not negative.");

            Assert.That(ZombieSuperMath.RadiationHeal(heal, 0f, 0f),
                Is.EqualTo(0f).Within(Tolerance),
                "A zero radius heals nothing, even at distance zero — otherwise a degenerate aura " +
                "would divide by zero.");

            // Monotone: no distance heals more than a nearer one.
            float previous = float.PositiveInfinity;
            for (float d = 0f; d <= radius; d += 50f)
            {
                float amount = ZombieSuperMath.RadiationHeal(heal, radius, d);
                Assert.That(amount, Is.LessThanOrEqualTo(previous),
                    $"distance {d} healed more than a nearer one — the falloff is inverted.");
                previous = amount;
            }
        }

        /// <summary>
        /// The aura's strength bleeds from <c>radMax</c> to <c>radMin</c> across exactly
        /// <c>tSuper</c> ticks (<c>:1193-1196</c>), so one full state-7 drains it precisely.
        /// </summary>
        [Test]
        public void RadiationDecay_DrainsFromMaxToMinOverTheDuration()
        {
            const float radMax = ZombieSuperMath.RadiationRadiusMax; // 1200
            const float radMin = ZombieSuperMath.RadiationRadiusMin; // 120
            int tSuper = ZombieSuperMath.Resolve(ZombieSuperMath.Radiation).TSuper; // 45

            Assert.That(tSuper, Is.EqualTo(45), "ss 5's tSuper, which is the decay's denominator.");

            float perTick = (radMax - radMin) / tSuper;
            float current = radMax;
            for (int i = 0; i < tSuper; i++)
            {
                current = ZombieSuperMath.RadiationDecay(current, radMax, radMin, tSuper);
            }

            Assert.That(current, Is.EqualTo(radMin).Within(0.01f),
                "After exactly tSuper decrements the aura is at its minimum — the state's length and " +
                "the decay's denominator are the same number, so a mismatch would leave it either " +
                "half-charged or negative.");

            Assert.That(ZombieSuperMath.RadiationDecay(radMax, radMax, radMin, tSuper),
                Is.EqualTo(radMax - perTick).Within(Tolerance),
                "One tick removes exactly (radMax - radMin) / tSuper.");

            Assert.That(ZombieSuperMath.RadiationDecay(500f, radMax, radMin, 0),
                Is.EqualTo(radMin).Within(Tolerance),
                "tSuper 0 would divide by zero; the guard returns radMin instead.");
        }

        // ── quake ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The quake's beat is <c>aiTCh % 4 == 1</c> (<c>:1199</c>) — <b>1, 5, 9, …</b>, not 0, 4, 8. The
        /// off-by-one is the whole test: starting at 0 fires on the tick the state is entered, which is
        /// before <c>superSila</c> has done its work.
        /// </summary>
        [Test]
        public void IsQuakeBeat_FiresOnOneModuloFour()
        {
            (int tick, bool expected)[] cases =
            {
                (0, false),
                (1, true),   // the first beat
                (2, false),
                (3, false),
                (4, false),
                (5, true),   // the second, four ticks later
                (9, true),
                (45, true),  // ss 6's tSuper, so the last tick is a beat
                (46, false),
            };

            foreach ((int tick, bool expected) in cases)
            {
                Assert.That(ZombieSuperMath.IsQuakeBeat(tick), Is.EqualTo(expected),
                    $"tick {tick}: `aiTCh % 4 == 1` must be {expected}.");
            }

            Assert.That(ZombieSuperMath.QuakePeriod, Is.EqualTo(4), "The oracle's literal divisor.");

            // The cadence, not just the predicate: exactly one beat in every window of four.
            for (int start = 1; start < 20; start += 4)
            {
                int beats = 0;
                for (int t = start; t < start + ZombieSuperMath.QuakePeriod; t++)
                {
                    if (ZombieSuperMath.IsQuakeBeat(t)) beats++;
                }

                Assert.That(beats, Is.EqualTo(1), $"window starting at {start} had {beats} beats, not 1.");
            }
        }

        /// <summary>
        /// ss 6 is the only skill that raises <c>vDestroy</c>, which is what lets the quake break walls.
        /// </summary>
        [Test]
        public void Resolve_OnlyTheQuake_RaisesVDestroy()
        {
            Assert.That(ZombieSuperMath.Resolve(ZombieSuperMath.Quake).VDestroy,
                Is.EqualTo(ZombieSuperMath.QuakeVDestroy).Within(Tolerance),
                "UnitZombie.as:1015 — ss 6 sets `vDestroy = 50`.");

            Assert.That(ZombieSuperMath.QuakeVDestroy, Is.GreaterThan(ZombieSuperMath.DefaultVDestroy),
                "The quake must destroy at a HIGHER speed than the default, or it breaks fewer walls " +
                "than an ordinary chase.");

            for (int ss = 0; ss <= 9; ss++)
            {
                if (ss == ZombieSuperMath.Quake) continue;

                Assert.That(ZombieSuperMath.Resolve(ss).VDestroy,
                    Is.EqualTo(ZombieSuperMath.DefaultVDestroy).Within(Tolerance),
                    $"ss {ss} must keep the default vDestroy of 20.");
            }
        }

        // ── the two coordinate systems ──────────────────────────────────────────────────────

        /// <summary>
        /// AS3's room-local Y runs <b>down</b> from 0 at the top; this port's runs <b>up</b> from 0 at the
        /// bottom. The conversion is a subtraction from the room height, and the negative control is the
        /// point of the test: the identity is the wrong answer that looks right in an origin room.
        /// </summary>
        [Test]
        public void As3YToPortY_FlipsTheAxis_AndIsNotTheIdentity()
        {
            const float roomHeight = 800f;

            Assert.That(ZombieSuperMath.As3YToPortY(0f, roomHeight), Is.EqualTo(800f).Within(Tolerance),
                "AS3's top (Y = 0) is this port's top (Y = roomHeight).");

            Assert.That(ZombieSuperMath.As3YToPortY(roomHeight, roomHeight), Is.EqualTo(0f).Within(Tolerance),
                "AS3's bottom (Y = roomHeight) is this port's bottom (Y = 0).");

            Assert.That(ZombieSuperMath.As3YToPortY(200f, roomHeight), Is.EqualTo(600f).Within(Tolerance),
                "200 px down from the top is 600 px up from the bottom.");

            // The negative control: for any non-zero, non-half input the conversion must move the value.
            Assert.That(ZombieSuperMath.As3YToPortY(200f, roomHeight), Is.Not.EqualTo(200f),
                "An identity would put every super destination 400 px below where the oracle wants it — " +
                "and in a room at the origin with a zero height it would look perfectly correct.");

            // Round trip, which is the property the call sites actually rely on.
            for (float y = 0f; y <= roomHeight; y += 40f)
            {
                Assert.That(ZombieSuperMath.PortYToAs3Y(ZombieSuperMath.As3YToPortY(y, roomHeight), roomHeight),
                    Is.EqualTo(y).Within(Tolerance), $"round trip at {y} lost the value.");
            }
        }

        /// <summary>
        /// The tile row flips too: AS3's row 0 is the top, this port's row 0 is the bottom. A one-row room
        /// makes the two agree, which is exactly why the fixture uses an 18-row room.
        /// </summary>
        [Test]
        public void As3RowToPortRow_FlipsTheRowIndex()
        {
            const int roomRows = 18;

            Assert.That(ZombieSuperMath.As3RowToPortRow(0, roomRows), Is.EqualTo(17),
                "AS3's top row is this port's LAST row.");

            Assert.That(ZombieSuperMath.As3RowToPortRow(17, roomRows), Is.EqualTo(0),
                "AS3's last row is this port's first.");

            Assert.That(ZombieSuperMath.As3RowToPortRow(8, roomRows), Is.EqualTo(9),
                "Middle-ish, and NOT symmetric with itself — 8 maps to 9, not to 8.");

            Assert.That(ZombieSuperMath.As3RowToPortRow(0, roomRows), Is.Not.EqualTo(0),
                "In a one-row room this would be 0 and the flip would be invisible, which is the trap " +
                "a fixture built on a default-sized room walks straight into.");

            // Every row maps into range, and the mapping is a bijection.
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int row = 0; row < roomRows; row++)
            {
                int port = ZombieSuperMath.As3RowToPortRow(row, roomRows);
                Assert.That(port, Is.InRange(0, roomRows - 1), $"AS3 row {row} mapped out of range.");
                Assert.That(seen.Add(port), Is.True, $"AS3 row {row} collided with another row.");
            }

            Assert.That(seen.Count, Is.EqualTo(roomRows), "The mapping must cover every row exactly once.");
        }
    }
}
