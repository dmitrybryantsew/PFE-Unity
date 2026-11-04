using NUnit.Framework;
using PFE.Systems.Particles;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins AS3 <c>Bullet.popadalo(mat)</c>'s spark arm (<c>fe/weapon/Bullet.as:339-365</c>) — the
    /// contact burst the port did not have at all until this fixture existed.
    ///
    /// <para><b>Why a fixture and not just a call.</b> Two of the rule's three inputs are easy to get
    /// backwards and neither fails loudly: the material gate covers <i>four of seven</i> indices, and
    /// the velocity is the <b>reverse</b> of the contact's motion. Emitting on the wrong materials, or
    /// spraying <i>along</i> the swing instead of away from it, both look like "particles appeared" —
    /// which is why the assertions below name the sign and the set rather than the count.</para>
    /// </summary>
    [TestFixture]
    public class BulletImpactRulesTests
    {
        // ── The gate ──────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>if(param1 == 1 || param1 == 2 || param1 == 5 || param1 == 7)</c> — metal, concrete,
        /// glass, pole. Wood (3) and heavy concrete (4) take the neighbouring arm, which emits nothing.
        /// </summary>
        [Test]
        [TestCase(1, true,  "metal")]
        [TestCase(2, true,  "concrete")]
        [TestCase(5, true,  "glass")]
        [TestCase(7, true,  "pole")]
        [TestCase(3, false, "wood takes the vis-scale arm, not the spark arm")]
        [TestCase(4, false, "heavy concrete takes the vis-scale arm")]
        [TestCase(6, false, "dirt has no arm at all")]
        [TestCase(0, false, "no material information")]
        [Description("Only AS3 materials 1, 2, 5 and 7 throw sparks — 3 and 4 are the neighbouring arm")]
        public void EmitsSparks_FollowsTheOracleMaterialSet(int material, bool expected, string why)
        {
            Assert.That(BulletImpactRules.EmitsSparks(tipDecal: 4, material), Is.EqualTo(expected), why);
        }

        /// <summary>
        /// AS3 <c>else if(this.tipDecal &gt; 0 &amp;&amp; this.tipDecal &lt;= 6)</c> — the decal gate is
        /// checked <i>before</i> the material, so a laser sword's decal 12 throws nothing off a metal
        /// wall however hard it hits.
        /// </summary>
        [Test]
        [TestCase(0,  false, "no decal authored")]
        [TestCase(1,  true,  "a bullet hole")]
        [TestCase(6,  true,  "the top of the window")]
        [TestCase(7,  false, "outside the window — scorch, not a hole")]
        [TestCase(12, false, "a laser sword's tipdec")]
        [Description("The weapon's decal must be in 1..6 before the material is even consulted")]
        public void EmitsSparks_GatesOnTheWeaponDecalFirst(int tipDecal, bool expected, string why)
        {
            Assert.That(BulletImpactRules.EmitsSparks(tipDecal, material: 1), Is.EqualTo(expected), why);
        }

        // ── The count ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>floor(roll * 5 + damage / 5)</c>, verbatim. The two terms are asserted separately so a
        /// dropped divisor (a very easy edit) cannot hide behind the roll.
        /// </summary>
        [Test]
        [Description("floor(roll*5 + damage/5) — the roll spans 5 and the damage is divided by 5")]
        public void SparkCount_IsRollTimesFivePlusDamageOverFive()
        {
            Assert.That(BulletImpactRules.SparkCount(damage: 0f,  roll: 0f),   Is.EqualTo(0), "floor(0 + 0)");
            Assert.That(BulletImpactRules.SparkCount(damage: 26f, roll: 0f),   Is.EqualTo(5), "floor(0 + 5.2) — a crowbar's 26 damage");
            Assert.That(BulletImpactRules.SparkCount(damage: 0f,  roll: 0.99f), Is.EqualTo(4), "floor(4.95 + 0)");
            Assert.That(BulletImpactRules.SparkCount(damage: 26f, roll: 0.99f), Is.EqualTo(10), "floor(4.95 + 5.2)");
        }

        /// <summary>
        /// The cap is 20 and it is reached by damage alone for anything at or above 100 — which several
        /// weapons in the roster exceed, so this is a live branch and not a theoretical one.
        /// </summary>
        [Test]
        [Description("The count is capped at 20 however much damage the contact carries")]
        public void SparkCount_CapsAtTwenty()
        {
            Assert.That(BulletImpactRules.SparkCount(damage: 100f, roll: 0f),  Is.EqualTo(20), "100/5 = 20 exactly");
            Assert.That(BulletImpactRules.SparkCount(damage: 500f, roll: 0.9f), Is.EqualTo(20), "well past the cap");
        }

        /// <summary>
        /// <c>if(World.w.alicorn) _loc2_ *= 0.2;</c> — applied to the count only. AS3's <c>_loc2_</c> is
        /// an <c>int</c>, so the multiply truncates; the oracle's endgame really does round a 9 down
        /// to 1.
        /// </summary>
        [Test]
        [Description("In the alicorn's world the count is multiplied by 0.2 and truncated, like AS3's int var")]
        public void SparkCount_ScalesDownInTheAlicornWorld()
        {
            Assert.That(BulletImpactRules.SparkCount(damage: 26f, roll: 0.99f, alicorn: true),
                Is.EqualTo(2), "floor(10 * 0.2) = 2 — the same roll that gives 10 in the ordinary world");
            Assert.That(BulletImpactRules.SparkCount(damage: 100f, roll: 0f, alicorn: true),
                Is.EqualTo(4), "floor(20 * 0.2) = 4");
        }

        // ── The emit ──────────────────────────────────────────────────────────

        [Test]
        [Description("A material outside the sparking set produces no emit at all, not an empty one")]
        public void PlanSparks_ReturnsNullForANonSparkingMaterial()
        {
            Assert.That(BulletImpactRules.PlanSparks(4, material: 3, damage: 30f, roll: 0.5f,
                    alicorn: false, as3DeltaX: 1f, as3DeltaY: 0f),
                Is.Null, "wood does not spark");

            Assert.That(BulletImpactRules.PlanSparks(12, material: 1, damage: 30f, roll: 0.5f,
                    alicorn: false, as3DeltaX: 1f, as3DeltaY: 0f),
                Is.Null, "a laser sword's decal is outside the window");
        }

        /// <summary>
        /// <c>dx: -dx / vel * 10</c> — the sparks fly <b>backwards</b> along the contact's motion. A
        /// swing that travels right-and-down therefore sprays left-and-up, which is the direction a
        /// player reads as "it hit there".
        /// </summary>
        [Test]
        [Description("The launch velocity is the reverse of the contact's motion, scaled to 10 px/frame")]
        public void PlanSparks_ThrowsTheSparksBackAlongTheMotion()
        {
            // A 3-4-5 step in AS3 pixels: right and down.
            ParticleEmit? planned = BulletImpactRules.PlanSparks(4, material: 2, damage: 26f, roll: 0.5f,
                alicorn: false, as3DeltaX: 3f, as3DeltaY: 4f);

            Assert.That(planned, Is.Not.Null);
            ParticleEmit emit = planned.Value;
            Assert.That(emit.Id, Is.EqualTo(BulletImpactRules.SparkId));
            Assert.That(emit.Spec.DX, Is.EqualTo(-6f).Within(1e-4f), "reversed X, times 10/5");
            Assert.That(emit.Spec.DY, Is.EqualTo(-8f).Within(1e-4f), "reversed Y, times 10/5");
        }

        /// <summary>
        /// A zero-length step is real: the strike window's first frame binds the same tip twice. AS3
        /// divides by that zero; the port leaves the overrides unsupplied so the row's own upward burst
        /// stands in. Asserting the zeros <i>and</i> a non-zero count is the point — the burst must
        /// still happen, just without a direction.
        /// </summary>
        [Test]
        [Description("A zero-length step leaves the velocity overrides unsupplied instead of producing NaN")]
        public void PlanSparks_SurvivesAZeroLengthStep()
        {
            ParticleEmit? planned = BulletImpactRules.PlanSparks(4, material: 1, damage: 26f, roll: 0.5f,
                alicorn: false, as3DeltaX: 0f, as3DeltaY: 0f);

            Assert.That(planned, Is.Not.Null);
            ParticleEmit emit = planned.Value;
            Assert.That(emit.Spec.DX, Is.EqualTo(0f), "0 means 'absent', so the row's own velocity applies");
            Assert.That(emit.Spec.DY, Is.EqualTo(0f));
            Assert.That(float.IsNaN(emit.Spec.DX), Is.False, "a NaN here would poison every particle");
            Assert.That(emit.Spec.Kol, Is.GreaterThan(0), "the burst still happens");
        }
    }
}
