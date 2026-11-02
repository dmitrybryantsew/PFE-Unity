using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the Super Alicorn's blades — <c>fe/unit/UnitScythe.as:9-45</c> (the constructor literals),
    /// <c>:95-115</c> (<c>run()</c>, the orbit and the bounds death), <c>:82-93</c> (<c>getNapr()</c>)
    /// and <c>:117-138</c> (<c>control()</c>).
    ///
    /// <para><b>Why a fixture and not the controller.</b> <c>ScytheController</c> is a
    /// <c>MonoBehaviour</c>; touching it needs <c>AddComponent</c>, which is a Unity <c>ECall</c> and
    /// throws where the native engine is absent. The arithmetic was extracted to
    /// <see cref="ScytheRules"/> for exactly this reason.</para>
    ///
    /// <para><b>Every expected value below is a literal read off <c>UnitScythe.as</c></b>, not a number
    /// copied back out of the implementation. The test names carry the line numbers.</para>
    /// </summary>
    [TestFixture]
    public class ScytheRulesTests
    {
        const float Tolerance = 1e-3f;

        // ── the orbit (:95-115) ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>t * 0.05 + PI * 2 * bindN / 6</c> (<c>:99-100</c>) — the six blades are 60° apart, and the
        /// whole ring advances 0.05 rad per tick.
        /// </summary>
        [Test]
        public void OrbitPhase_SpacesSixBladesSixtyDegreesApart()
        {
            // At t = 0 the phases are 0, 2π/6, 4π/6 … — i.e. k * π/3.
            for (int n = 0; n < ScytheRules.BindCount; n++)
            {
                Assert.AreEqual(n * System.Math.PI / 3.0, ScytheRules.OrbitPhase(n, 0), 1e-9,
                    $"slot {n} is not at {n * 60} degrees at t = 0");
            }

            // And the ring itself turns: one tick adds bindKoef to every slot, unchanged relative spacing.
            Assert.AreEqual(System.Math.PI * 2.0 / 6.0,
                ScytheRules.OrbitPhase(1, 0) - ScytheRules.OrbitPhase(0, 0), 1e-9);
            Assert.AreEqual(0.05,
                ScytheRules.OrbitPhase(3, 1) - ScytheRules.OrbitPhase(3, 0), 1e-9);
        }

        /// <summary>
        /// <c>X = bind.X - sin(φ) * 200</c>, <c>Y = bind.Y - bind.scY/2 - cos(φ) * 200</c>
        /// (<c>:99-100</c>), with the Y mirrored into the port's y-up space.
        /// </summary>
        /// <remarks>
        /// <b>This is the discriminating fixture for the one sign the mirror changes.</b> Slot 0 at
        /// <c>t = 0</c> has <c>φ = 0</c>, so <c>sin = 0</c> and <c>cos = 1</c>: the blade sits directly
        /// <i>above</i> the boss's centre in AS3 (cos is subtracted from a y-down coordinate) and
        /// therefore directly above it in the port too — which means <c>+200</c> in y-up. Copying the
        /// minus sign across would put it 200 px <i>below</i> her, and because the other five slots
        /// still form a circle the mistake is invisible in a screenshot.
        /// </remarks>
        [Test]
        public void OrbitPixels_SlotZeroAtTickZeroSitsAboveTheHost()
        {
            BossAlicornRules.Vector2Pixels p = ScytheRules.OrbitPixels(
                bossCentreX: 960f, bossCentreY: 500f, bindN: 0, t: 0);

            Assert.AreEqual(960f, p.X, Tolerance, "slot 0 must not be displaced horizontally at φ = 0");
            Assert.AreEqual(700f, p.Y, Tolerance, "slot 0 must sit +200 px (up) from the host's centre");
        }

        /// <summary>
        /// Slot 3 is half a revolution round, so it mirrors slot 0 exactly — and slot 1/2 land on the
        /// circle rather than on an axis, which is what catches a swapped sin/cos.
        /// </summary>
        [Test]
        public void OrbitPixels_AreOnTheRingAndOppositeSlotsMirror()
        {
            const float cx = 400f;
            const float cy = 300f;

            BossAlicornRules.Vector2Pixels s0 = ScytheRules.OrbitPixels(cx, cy, 0, 0);
            BossAlicornRules.Vector2Pixels s3 = ScytheRules.OrbitPixels(cx, cy, 3, 0);

            // φ = 0 → (cx, cy + R);  φ = π → (cx, cy - R).
            Assert.AreEqual(cx, s3.X, Tolerance);
            Assert.AreEqual(cy - 200f, s3.Y, Tolerance);

            // Every slot is exactly bindRad from the centre, at every tick.
            for (int n = 0; n < ScytheRules.BindCount; n++)
            {
                for (int t = 0; t <= 200; t += 37)
                {
                    BossAlicornRules.Vector2Pixels p = ScytheRules.OrbitPixels(cx, cy, n, t);
                    float dx = p.X - cx;
                    float dy = p.Y - cy;
                    Assert.AreEqual(200f, (float)System.Math.Sqrt(dx * dx + dy * dy), 1e-2,
                        $"slot {n} at t = {t} left the ring");
                }
            }
        }

        /// <summary>
        /// <c>φ = π/2</c> puts the blade at <c>(cx − R, cy)</c> — the <c>sin</c> half of the pair, which
        /// a swapped implementation gets right by accident at <c>φ = 0</c> and wrong here.
        /// </summary>
        [Test]
        public void OrbitPixels_QuarterTurnUsesSinForX()
        {
            // Slot 1 at t = 0 is φ = π/3, not π/2 — so drive t until the phase is a quarter turn:
            // t * 0.05 = π/2  →  t ≈ 31.42, and 314 ticks lands on exactly 15.7 rad ≈ 5π. Use slot 0
            // and an exact multiple instead: 0.05 * t = π/2 has no integer solution, so assert the
            // general relation instead of a round number.
            double phase = ScytheRules.OrbitPhase(0, 20);
            BossAlicornRules.Vector2Pixels p = ScytheRules.OrbitPixels(0f, 0f, 0, 20);

            Assert.AreEqual(-(float)System.Math.Sin(phase) * 200f, p.X, Tolerance);
            Assert.AreEqual((float)System.Math.Cos(phase) * 200f, p.Y, Tolerance);
            Assert.IsTrue(System.Math.Abs(phase - 1.0) < 1e-9, "20 ticks at 0.05 rad is 1.0 rad");
        }

        // ── the free flight (:82-93, :117-138) ────────────────────────────────────────────────────

        /// <summary>
        /// <c>Math.atan2(cel.X - X, cel.Y - Y)</c> (<c>:88</c>) — AS3 passes ΔX as atan2's <c>y</c>
        /// argument. A blade aimed at a target 30 px right and 40 px up must therefore come out at the
        /// angle whose <c>sin</c> is <c>0.6</c> and whose <c>cos</c> is <c>0.8</c>.
        /// </summary>
        [Test]
        public void AngleToTarget_FeedsDeltaXAsTheFirstArgument()
        {
            float napr = ScytheRules.AngleToTarget(deltaX: 30f, deltaY: 40f);

            Assert.AreEqual(0.6f, (float)System.Math.Sin(napr), 1e-5);
            Assert.AreEqual(0.8f, (float)System.Math.Cos(napr), 1e-5);
        }

        /// <summary>
        /// <c>dx = sin(napr) * skor; dy = cos(napr) * skor</c> (<c>:127-128</c>) — the round trip through
        /// <see cref="ScytheRules.AngleToTarget"/> must point back at the target, scaled by the speed.
        /// </summary>
        [Test]
        public void VelocityFromAngle_PointsBackAtTheTarget()
        {
            float napr = ScytheRules.AngleToTarget(deltaX: -60f, deltaY: 80f);
            BossAlicornRules.Vector2Pixels v = ScytheRules.VelocityFromAngle(napr, 2.5f);

            // 3-4-5 triangle: the unit direction is (-0.6, 0.8), so the velocity is (-1.5, 2.0).
            Assert.AreEqual(-1.5f, v.X, 1e-4);
            Assert.AreEqual(2.0f, v.Y, 1e-4);
        }

        /// <summary>
        /// <c>napr = -1</c> is a sentinel, not a direction (<c>:9</c>, <c>:89</c>): the aim is latched
        /// once, on the first call, and never re-aimed.
        /// </summary>
        [Test]
        public void NeedsAngle_OnlyForTheMinusOneSentinel()
        {
            Assert.IsTrue(ScytheRules.NeedsAngle(ScytheRules.NoAngle));
            Assert.IsTrue(ScytheRules.NeedsAngle(-1f));

            // Zero is a real angle — straight up in the port's y-up space — and must not be re-latched.
            Assert.IsFalse(ScytheRules.NeedsAngle(0f));
            Assert.IsFalse(ScytheRules.NeedsAngle(1.5f));
        }

        /// <summary>
        /// <c>skor *= 1.05</c> (<c>:126</c>), compounded with no cap: sixty ticks of it is
        /// <c>1.5 * 1.05^60 ≈ 28.0</c> px per frame — faster than anything else in the game.
        /// </summary>
        [Test]
        public void AdvanceSpeed_CompoundsFivePercentPerTick()
        {
            float speed = ScytheRules.InitialSpeed;
            Assert.AreEqual(1.5f, speed, Tolerance);

            for (int i = 0; i < ScytheRules.ContactDelayTicks; i++)
            {
                speed = ScytheRules.AdvanceSpeed(speed);
            }

            // 1.5 * 1.05^60 — the literal, computed independently.
            Assert.AreEqual(1.5f * (float)System.Math.Pow(1.05, 60), speed, 1e-3);
            Assert.IsTrue(speed > 28f && speed < 28.1f,
                $"sixty ticks should be ~28.0 px/frame, got {speed}");
        }

        /// <summary>
        /// <c>if(this.t &gt; 60) attKorp(cel)</c> (<c>:134-137</c>) — <b>a blade cannot hurt anything for
        /// its first sixty ticks.</b> This is the single most surprising line in the class and the one a
        /// "sensible" port would get wrong in the dangerous direction.
        /// </summary>
        [Test]
        public void CanDamage_OnlyAfterSixtyTicks()
        {
            for (int t = 0; t <= ScytheRules.ContactDelayTicks; t++)
            {
                Assert.IsFalse(ScytheRules.CanDamage(t), $"t = {t} must still be harmless");
            }

            Assert.IsTrue(ScytheRules.CanDamage(ScytheRules.ContactDelayTicks + 1));
        }

        /// <summary>
        /// <c>if(X &gt;= loc.limX || X &lt;= 0 || Y &gt;= loc.limY || Y &lt;= 0) die()</c>
        /// (<c>:106-109</c>) — the same expression in both engines, because each room edge is 0 and the
        /// opposite edge is the limit in both spaces.
        /// </summary>
        [Test]
        public void OutOfBounds_KillsAtEitherEdgeOfEitherAxis()
        {
            const float limX = 1920f;
            const float limY = 1000f;

            Assert.IsFalse(ScytheRules.OutOfBounds(960f, 500f, limX, limY), "the middle is inside");

            Assert.IsTrue(ScytheRules.OutOfBounds(0f, 500f, limX, limY), "x <= 0");
            Assert.IsTrue(ScytheRules.OutOfBounds(-1f, 500f, limX, limY), "x < 0");
            Assert.IsTrue(ScytheRules.OutOfBounds(limX, 500f, limX, limY), "x >= limX");

            Assert.IsTrue(ScytheRules.OutOfBounds(960f, 0f, limX, limY), "y <= 0 is the floor in the port");
            Assert.IsTrue(ScytheRules.OutOfBounds(960f, limY, limX, limY), "y >= limY is the ceiling");
        }

        /// <summary>
        /// <c>if(bind &amp;&amp; (bind as Unit).sost &gt;= 3) die()</c> (<c>:130-133</c>) — a bound blade
        /// dies with its host, and a free one has no host to lose.
        /// </summary>
        [Test]
        public void HostIsDead_OnlyMattersForABoundBlade()
        {
            Assert.IsFalse(ScytheRules.HostIsDead(hasHost: false, hostAlive: false),
                "a free blade has no host and must not die of one");
            Assert.IsFalse(ScytheRules.HostIsDead(hasHost: true, hostAlive: true));
            Assert.IsTrue(ScytheRules.HostIsDead(hasHost: true, hostAlive: false));
        }

        // ── the visual (:64-80) ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(this.dr &lt; 30) this.dr += 0.5</c> (<c>:66-69</c>) — the spin accelerates to 30°/frame
        /// in 60 frames and then stops accelerating.
        /// </summary>
        [Test]
        public void SpinStep_ReachesThirtyDegreesAndStops()
        {
            float dr = 0f;
            for (int i = 0; i < 59; i++)
            {
                dr = ScytheRules.SpinStep(dr);
            }

            Assert.AreEqual(29.5f, dr, 1e-4, "59 steps of 0.5 from 0");

            dr = ScytheRules.SpinStep(dr);
            Assert.AreEqual(30f, dr, 1e-4);

            // Already at the cap: stays there.
            Assert.AreEqual(30f, ScytheRules.SpinStep(dr), 1e-4);
        }

        /// <summary>
        /// <c>if(vis.osn.alpha &lt; 1) vis.osn.alpha += 0.05</c> and <c>+= 0.02</c> (<c>:72-79</c>) —
        /// both clamp at one, so a blade does not overshoot into a wraparound.
        /// </summary>
        [Test]
        public void FadeStep_ClampsAtOne()
        {
            float blade = 0f;
            for (int i = 0; i < 19; i++)
            {
                blade = ScytheRules.FadeStep(blade, ScytheRules.BladeFadeStep);
            }

            Assert.AreEqual(0.95f, blade, 1e-4);
            Assert.AreEqual(1f, ScytheRules.FadeStep(blade, ScytheRules.BladeFadeStep), 1e-4);

            float trail = 0f;
            for (int i = 0; i < 49; i++)
            {
                trail = ScytheRules.FadeStep(trail, ScytheRules.TrailFadeStep);
            }

            Assert.AreEqual(0.98f, trail, 1e-4);
            Assert.AreEqual(1f, ScytheRules.FadeStep(trail, ScytheRules.TrailFadeStep), 1e-4);
        }

        // ── the emit-side constants the rules file depends on ─────────────────────────────────────

        /// <summary>
        /// The spawn box and the bound-blade multiplier are the <i>boss's</i> numbers, so they live in
        /// <see cref="BossAlicornRules"/> — but they are what makes a blade what it is, so they are
        /// pinned here where a reader looking at the blade will find them.
        /// </summary>
        [Test]
        public void EmitConstants_MatchTheOracleLiterals()
        {
            // `Math.random() * 1600 + 160` / `Math.random() * 500 + 100` (UnitBossAlicorn.as:296).
            Assert.AreEqual(160f, BossAlicornRules.ScytheSpawnMinXPixels);
            Assert.AreEqual(1760f, BossAlicornRules.ScytheSpawnMaxXPixels);
            Assert.AreEqual(100f, BossAlicornRules.ScytheSpawnMinYAs3Pixels);
            Assert.AreEqual(600f, BossAlicornRules.ScytheSpawnMaxYAs3Pixels);

            // `_loc2_.maxhp *= 8` (:303).
            Assert.AreEqual(8f, BossAlicornRules.BoundScytheHealthMultiplier);

            // `while(_loc2_ < 6)` (:728) and `bindN`'s six slots — the same count, two files.
            Assert.AreEqual(6, BossAlicornRules.ScythesOnShieldBreak);
            Assert.AreEqual(ScytheRules.BindCount, BossAlicornRules.ScythesOnShieldBreak);
        }

        /// <summary>
        /// <see cref="BossAlicornRules.ScytheSpawnPixels"/> returns <b>y-up</b> room-local pixels: the
        /// AS3 box is measured from the ceiling, so its low end lands high in the port.
        /// </summary>
        [Test]
        public void ScytheSpawnPixels_MirrorsTheAs3BoxIntoYUpSpace()
        {
            // roll 0 → the AS3 minimum (160, 100 from the ceiling) → port (160, 1000 - 100 = 900).
            BossAlicornRules.Vector2Pixels low = BossAlicornRules.ScytheSpawnPixels(0f, 0f);
            Assert.AreEqual(160f, low.X, Tolerance);
            Assert.AreEqual(900f, low.Y, Tolerance);

            // roll 1 → the AS3 maximum (1760, 600 from the ceiling) → port (1760, 400).
            BossAlicornRules.Vector2Pixels high = BossAlicornRules.ScytheSpawnPixels(1f, 1f);
            Assert.AreEqual(1760f, high.X, Tolerance);
            Assert.AreEqual(400f, high.Y, Tolerance);

            // A 20-row room moves the whole box with it: 800 - 100 = 700.
            Assert.AreEqual(700f, BossAlicornRules.ScytheSpawnPixels(0f, 0f, 20).Y, Tolerance);
        }
    }
}
