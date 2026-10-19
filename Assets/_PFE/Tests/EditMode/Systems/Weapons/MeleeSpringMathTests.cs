using NUnit.Framework;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins the melee weapon's <b>position model</b> — the levitating spring of
    /// <c>WClub.actions()</c> (<c>WClub.as:232-335</c>), ported 2026-10-20.
    ///
    /// <para><b>What this fixture exists to stop coming back.</b> The port used to snap the weapon
    /// to the hold point whenever <c>t_attack &lt;= 0</c> and chase the cursor clamped to
    /// <c>dlina</c> while swinging. Three separate things were wrong with that, and only the first
    /// was visible:</para>
    ///
    /// <list type="number">
    /// <item>the weapon <b>jumped back to the hand</b> the frame a swing ended — the reported
    ///   "launches at the target and is then set back";</item>
    /// <item>its reach was <c>2·dlina</c> and had nothing to do with <c>Pers.meleeR</c>, so the melee
    ///   <b>skill lengthened nothing</b> (100 → 250 px across five tiers);</item>
    /// <item>the damage lands at the blade tip, so both of the above moved <b>where melee damage is
    ///   applied</b>, not just where the sprite is drawn.</item>
    /// </list>
    ///
    /// <para><b>Why the arithmetic is a separate class.</b> <c>MeleeWeaponController</c> uses
    /// <c>UnityEngine.Vector2</c>, so a fixture that touches it dies with
    /// <c>SecurityException: ECall methods must be packaged into a system module</c> in the offline
    /// wall — measured on <c>MeleeHitWindowTests</c> and <c>MeleeOwnerStatTests</c>, which are
    /// 0-passed/26-failed there. Every assertion below therefore runs on plain floats.</para>
    /// </summary>
    [TestFixture]
    public class MeleeSpringMathTests
    {
        private const float Tol = 1e-4f;

        // ── norma (Obj.as:248-257) ────────────────────────────────────────────

        [Test]
        [Description("norma CLAMPS to the maximum length — it never lengthens a shorter vector")]
        public void ClampTo_LeavesAShorterVectorAlone_AndShrinksALongerOne()
        {
            float x = 0.3f, y = 0.4f;                 // length 0.5
            MeleeSpringMath.ClampTo(ref x, ref y, 1f);
            Assert.That(x, Is.EqualTo(0.3f).Within(Tol), "A vector inside the limit must not move.");
            Assert.That(y, Is.EqualTo(0.4f).Within(Tol));

            x = 3f; y = 4f;                           // length 5
            MeleeSpringMath.ClampTo(ref x, ref y, 1f);
            Assert.That(x, Is.EqualTo(0.6f).Within(Tol), "A vector past the limit must be scaled down.");
            Assert.That(y, Is.EqualTo(0.8f).Within(Tol), "…keeping its direction.");
        }

        [Test]
        [Description("norma with an infinite limit is a no-op — AS3's 1/0 for a row with no <phis massa>")]
        public void ClampTo_WithAnInfiniteLimit_DoesNothing()
        {
            float x = 1000f, y = -500f;
            MeleeSpringMath.ClampTo(ref x, ref y, float.PositiveInfinity);
            Assert.That(x, Is.EqualTo(1000f).Within(Tol));
            Assert.That(y, Is.EqualTo(-500f).Within(Tol));
        }

        // ── The per-frame travel limit ────────────────────────────────────────

        [Test]
        [Description("max(levitRun, 1/massa) in px: light weapons fly out, heavy ones levitate")]
        public void StepClampPx_MatchesTheOracleFormula()
        {
            // massa = @massa/50, so 1/massa = 50/@massa. WClub.as:330.
            Assert.That(MeleeSpringMath.StepClampPx(10f, 1f),  Is.EqualTo(50f).Within(Tol),  "bat (massa 1)");
            Assert.That(MeleeSpringMath.StepClampPx(10f, 2f),  Is.EqualTo(25f).Within(Tol),  "spear (2)");
            Assert.That(MeleeSpringMath.StepClampPx(10f, 10f), Is.EqualTo(10f).Within(Tol),  "ripper (10) ties meleeRun");
            Assert.That(MeleeSpringMath.StepClampPx(10f, 15f), Is.EqualTo(10f).Within(Tol),  "hammer (15): meleeRun wins");
            Assert.That(MeleeSpringMath.StepClampPx(25f, 1f),  Is.EqualTo(50f).Within(Tol),  "tier 5 raises meleeRun to 25");
            Assert.That(MeleeSpringMath.StepClampPx(25f, 2f),  Is.EqualTo(25f).Within(Tol),  "…and now the tie moves");
        }

        [Test]
        [Description("A row with no <phis massa> is UNCLAMPED — 1/0 is Infinity in AS3")]
        public void StepClampPx_WithNoMassaAttribute_IsUnclamped()
        {
            Assert.That(MeleeSpringMath.StepClampPx(10f, 0f), Is.EqualTo(float.PositiveInfinity));
        }

        // ── Aim cell, per sub-type ────────────────────────────────────────────

        [Test]
        [Description("mtip 0 pulls the aim cell back along the facing and down by the blade length")]
        public void SwingAimCell_PullsBackAndDown()
        {
            // WClub.as:256-257: celX = cel - dlina*0.8*storona, celY = cel + dlina*0.3 (y-down).
            MeleeSpringMath.SwingAimCell(5f, 2f, 1f, +1f, out float celX, out float celY);
            Assert.That(celX, Is.EqualTo(4.2f).Within(Tol), "Facing right: 5 - 0.8*1");
            Assert.That(celY, Is.EqualTo(1.7f).Within(Tol), "AS3 '+0.3*dlina' is DOWN, so -0.3 in the port's frame");

            MeleeSpringMath.SwingAimCell(5f, 2f, 1f, -1f, out celX, out celY);
            Assert.That(celX, Is.EqualTo(5.8f).Within(Tol), "Facing left: 5 + 0.8*1 — the pull-back mirrors");
            Assert.That(celY, Is.EqualTo(1.7f).Within(Tol), "The vertical offset does not depend on facing");
        }

        [Test]
        [Description("mtip 2 collapses a near cursor onto the hold point and pulls a far one back by the mean blade")]
        public void OverheadPullBack_ClampsTowardTheHoldPoint()
        {
            const float dlina = 0.7f, minDlina = 0.3f;      // mean = 0.5
            float celX = 0.2f, celY = 0.1f;                 // 0.22 from the anchor: inside the mean blade
            MeleeSpringMath.OverheadPullBack(ref celX, ref celY, 0f, 0f, dlina, minDlina);
            Assert.That(celX, Is.EqualTo(0f).Within(Tol), "A cursor inside (dlina+mindlina)/2 collapses to the anchor.");
            Assert.That(celY, Is.EqualTo(0f).Within(Tol));

            celX = 3f; celY = 4f;                           // 5 units away
            MeleeSpringMath.OverheadPullBack(ref celX, ref celY, 0f, 0f, dlina, minDlina);
            Assert.That(celX, Is.EqualTo(2.7f).Within(Tol), "A far cursor is pulled back by exactly (dlina+mindlina)/2.");
            Assert.That(celY, Is.EqualTo(3.6f).Within(Tol));
        }

        [Test]
        [Description("A swing keeps its facing until the cursor is 100 px behind the BODY's facing")]
        public void SwingFacing_HasHysteresis()
        {
            // WClub.as:259: storona = celX > owner.X - 100*owner.storona ? 1 : -1
            Assert.That(MeleeSpringMath.SwingFacing(1f, 0f, +1f, 1f),  Is.EqualTo(1f),  "In front: keep facing right");
            Assert.That(MeleeSpringMath.SwingFacing(-0.5f, 0f, +1f, 1f), Is.EqualTo(1f),
                "Half a unit behind the body is still 'right' — this is the hysteresis");
            Assert.That(MeleeSpringMath.SwingFacing(-1.5f, 0f, +1f, 1f), Is.EqualTo(-1f),
                "Past 100 px behind: turn round");
            Assert.That(MeleeSpringMath.SwingFacing(0.5f, 0f, -1f, 1f), Is.EqualTo(-1f),
                "Mirrored: facing left keeps 'left' until 100 px past the body's right side");
        }

        [Test]
        [Description("A thrust/overhead facing has NO hysteresis — it follows the aim cell")]
        public void AimFacing_FlipsOnTheBodyCentre()
        {
            Assert.That(MeleeSpringMath.AimFacing(0.01f, 0f), Is.EqualTo(1f));
            Assert.That(MeleeSpringMath.AimFacing(-0.01f, 0f), Is.EqualTo(-1f));
        }

        // ── The spring ────────────────────────────────────────────────────────

        [Test]
        [Description("The spring's radius is meleeR — a cursor past it does NOT extend the reach")]
        public void Resolve_ClampsTheTargetToTheMeleeRadius()
        {
            // Cursor 5 units away, meleeR 1, anchor at 0.4. WClub.as:262 / :291 clamp `del` to meleeR.
            var r = MeleeSpringMath.Resolve(
                celX: 5f, celY: 0f,
                anchorX: 0.4f, anchorY: 0f,
                x: 0.4f, y: 0f,
                meleeR: 1f, stepClamp: float.PositiveInfinity, storona: 1f, readyDistance: 0.1f);

            Assert.That(r.StepX, Is.EqualTo(0.5f).Within(Tol),
                "Target is anchor + 1.0, so the first half step is 0.5 — NOT (5 - 0.4)/2.");
            Assert.That(r.X, Is.EqualTo(0.9f).Within(Tol));
            Assert.That(r.Ready, Is.False, "ready measures against the CELL, so a distant cursor is never 'ready'");
        }

        [Test]
        [Description("A cursor INSIDE the melee radius is reached exactly, not pushed out to the radius")]
        public void Resolve_DoesNotPushAnInsideCursorOut()
        {
            var r = MeleeSpringMath.Resolve(
                celX: 0.9f, celY: 0f, anchorX: 0.4f, anchorY: 0f,
                x: 0.4f, y: 0f, meleeR: 1f, stepClamp: float.PositiveInfinity, storona: 1f, readyDistance: 0.1f);

            Assert.That(r.StepX, Is.EqualTo(0.25f).Within(Tol), "(0.9 - 0.4)/2 — the cell itself is the target");
            Assert.That(r.X, Is.EqualTo(0.65f).Within(Tol));
        }

        [Test]
        [Description("The half step is a spring, not a snap: a displaced weapon converges geometrically")]
        public void Resolve_TakesAHalfStep_SoTheWeaponNeverSnaps()
        {
            // This is the regression the port's old code caused: it assigned X = holdPoint whenever
            // t_attack <= 0, i.e. a snap. The oracle's `/2` means the same displacement takes several
            // frames to close, which is what makes the weapon look like it is floating.
            var r = MeleeSpringMath.Resolve(
                celX: 0.4f, celY: 0f, anchorX: 0.4f, anchorY: 0f,
                x: 2f, y: 0f, meleeR: 1f, stepClamp: float.PositiveInfinity, storona: 1f, readyDistance: 0.1f);

            Assert.That(r.X, Is.EqualTo(1.2f).Within(Tol), "(2 + (0.4-2)/2) = 1.2 — half the gap, NOT 0.4");
            Assert.That(r.StepX, Is.EqualTo(-0.8f).Within(Tol));
            Assert.That(r.Ready, Is.False, "1.6 units from the cell is not 'ready'");

            // …and it does converge: six halvings of a 1.6 gap is under 10 px.
            float x = 2f;
            for (int i = 0; i < 6; i++)
            {
                var s = MeleeSpringMath.Resolve(0.4f, 0f, 0.4f, 0f, x, 0f, 1f,
                                                float.PositiveInfinity, 1f, 0.1f);
                x = s.X;
            }
            Assert.That(x, Is.EqualTo(0.4f).Within(0.03f), "Geometric convergence, six frames");
        }

        [Test]
        [Description("The per-frame travel limit binds on a heavy weapon and leaves a light one alone")]
        public void Resolve_AppliesTheStepClamp()
        {
            // A hammer: 50/15 = 3.33 px = 0.0333 units per frame.
            float hammer = MeleeSpringMath.StepClampPx(10f, 15f) / 100f;
            var heavy = MeleeSpringMath.Resolve(5f, 0f, 0.4f, 0f, 0.4f, 0f, 1f, hammer, 1f, 0.1f);
            Assert.That(heavy.StepX, Is.EqualTo(hammer).Within(Tol),
                "The half step (0.5) is clamped to the hammer's levitation speed.");

            // A bat: 50/1 = 50 px, so the half step of 0.5 units passes through untouched.
            float bat = MeleeSpringMath.StepClampPx(10f, 1f) / 100f;
            var light = MeleeSpringMath.Resolve(5f, 0f, 0.4f, 0f, 0.4f, 0f, 1f, bat, 1f, 0.1f);
            Assert.That(light.StepX, Is.EqualTo(0.5f).Within(Tol), "A bat is effectively unclamped.");
        }

        [Test]
        [Description("blumR is (step.y - step.x*storona)/2 degrees — the oracle's value with the y-mirror applied")]
        public void Resolve_ComputesBlumR()
        {
            var right = MeleeSpringMath.Resolve(1.4f, 0f, 0.4f, 0f, 0.4f, 0f, 1f,
                                                float.PositiveInfinity, 1f, 0.1f);
            Assert.That(right.BlumRDeg, Is.EqualTo(-0.25f).Within(Tol),
                "Moving +x with storona +1: (0 - 0.5*1)/2. AS3 gives +0.25 in its y-down frame.");

            var left = MeleeSpringMath.Resolve(1.4f, 0f, 0.4f, 0f, 0.4f, 0f, 1f,
                                               float.PositiveInfinity, -1f, 0.1f);
            Assert.That(left.BlumRDeg, Is.EqualTo(0.25f).Within(Tol),
                "Same motion facing left flips the lean — the storona term.");
        }

        [Test]
        [Description("ready fires within 10 px of the cell, and only then")]
        public void Resolve_ReadyIsTenPixels()
        {
            var near = MeleeSpringMath.Resolve(0.45f, 0f, 0.4f, 0f, 0.4f, 0f, 1f,
                                               float.PositiveInfinity, 1f, 0.1f);
            Assert.That(near.Ready, Is.True, "0.05 units = 5 px < 10 px");

            var far = MeleeSpringMath.Resolve(0.55f, 0f, 0.4f, 0f, 0.4f, 0f, 1f,
                                              float.PositiveInfinity, 1f, 0.1f);
            Assert.That(far.Ready, Is.False, "0.15 units = 15 px > 10 px");
        }

        [Test]
        [Description("Effective melee reach is meleeR + dlina, so the skill moves the tip")]
        public void Reach_IsMeleeRadiusPlusBlade()
        {
            // The oracle's tip is X + cos(rot)*dlina, and X is at most meleeR from the anchor. At
            // tier 0 meleeR = 100 px and a sword's dlina = 80 px; at tier 5 meleeR = 250 px.
            const float dlina = 0.8f;

            float tier0 = MeleeSpringMath.MeleeRDefaultPx / 100f + dlina;
            float tier5 = (MeleeSpringMath.MeleeRDefaultPx + 5f * 30f) / 100f + dlina;

            Assert.That(tier0, Is.EqualTo(1.8f).Within(Tol), "100 px radius + 80 px blade");
            Assert.That(tier5, Is.EqualTo(3.3f).Within(Tol),
                "250 px radius + 80 px blade — the melee skill nearly doubles the weapon's reach");
        }
    }
}
