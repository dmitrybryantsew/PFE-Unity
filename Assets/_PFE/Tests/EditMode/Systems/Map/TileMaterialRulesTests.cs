using NUnit.Framework;
using PFE.Systems.Audio;
using PFE.Systems.Map;

namespace PFE.Tests.EditMode.Systems.Map
{
    /// <summary>
    /// Pins the AS3 tile-material → <see cref="SurfaceMaterial"/> table, which is what decides the
    /// <b>only</b> wall-impact sound a melee weapon has.
    ///
    /// <para><b>Why this fixture is not a formality.</b> The port used to feed
    /// <c>TileData.material</c> — which holds the raw AS3 <c>mat</c> index, written by a straight cast
    /// from <c>TileForm.mat</c> — into a converter that read it as a <see cref="MaterialType"/>. The two
    /// numberings agree only at 0 and 1, so five of the seven AS3 materials were wrong or silent. A
    /// test that only asserted "some surface came back" would have passed throughout; every assertion
    /// below names the sound the oracle plays (<c>Bullet.as:616-637</c>).</para>
    ///
    /// <para>Executable offline: the rule takes an <c>int</c> and returns an enum, so no
    /// <c>MonoBehaviour</c> and no <c>ECall</c> stands between the fixture and the answer.</para>
    /// </summary>
    [TestFixture]
    public class TileMaterialRulesTests
    {
        /// <summary>
        /// The five indices the old cast got wrong. Each row is the AS3 index, the surface the oracle
        /// sounds, and — as the message — the sound the port produced instead.
        /// </summary>
        [Test]
        [TestCase(TileMaterialRules.MetalMat,          SurfaceMaterial.Metal,    "was already right")]
        [TestCase(TileMaterialRules.ConcreteMat,       SurfaceMaterial.Concrete, "was Wood → hit_wood on concrete")]
        [TestCase(TileMaterialRules.WoodMat,           SurfaceMaterial.Wood,     "was Concrete → hit_concrete on wood")]
        [TestCase(TileMaterialRules.HeavyConcreteMat,  SurfaceMaterial.Concrete, "was Glass → hit_glass on concrete")]
        [TestCase(TileMaterialRules.GlassMat,          SurfaceMaterial.Glass,    "was Default → silent")]
        [TestCase(TileMaterialRules.DirtMat,           SurfaceMaterial.Concrete, "was Default → silent")]
        [TestCase(TileMaterialRules.PoleMat,           SurfaceMaterial.Pole,     "was Default → silent")]
        [Description("Every AS3 material index maps to the surface whose sound Bullet.sound() plays for it")]
        public void SurfaceForAs3Material_MatchesTheOracleSoundSwitch(int as3Mat, SurfaceMaterial expected,
                                                                     string regression)
        {
            Assert.That(TileMaterialRules.SurfaceForAs3Material(as3Mat), Is.EqualTo(expected),
                $"AS3 mat {as3Mat} ({regression})");
        }

        /// <summary>
        /// A material the oracle gives no sound is silent, not "concrete by default". The old converter's
        /// <c>default</c> arm returned <c>Concrete</c>, which is why a pole or a glass pane produced a
        /// wall thud rather than nothing.
        /// </summary>
        [Test]
        [TestCase(0)]
        [TestCase(8)]
        [TestCase(-1)]
        [Description("An index Bullet.sound() has no arm for is silent — the old default was Concrete")]
        public void SurfaceForAs3Material_UnknownIndexIsSilent(int as3Mat)
        {
            Assert.That(TileMaterialRules.SurfaceForAs3Material(as3Mat),
                Is.EqualTo(SurfaceMaterial.Default),
                "Default is the resolver's silent arm, which is what AS3 does when no if() matches");
        }

        /// <summary>
        /// The three indices that share <c>hit_concrete</c> must actually share it. Splitting them would
        /// be an invented distinction; collapsing concrete onto wood is the bug this whole fixture is
        /// about, so the grouping is asserted rather than left to the individual rows above.
        /// </summary>
        [Test]
        [Description("2, 4 and 6 are all concrete — the oracle gives them one sound and different debris")]
        public void SurfaceForAs3Material_ConcreteGroupIsOneSound()
        {
            SurfaceMaterial a = TileMaterialRules.SurfaceForAs3Material(TileMaterialRules.ConcreteMat);
            SurfaceMaterial b = TileMaterialRules.SurfaceForAs3Material(TileMaterialRules.HeavyConcreteMat);
            SurfaceMaterial c = TileMaterialRules.SurfaceForAs3Material(TileMaterialRules.DirtMat);

            Assert.That(a, Is.EqualTo(SurfaceMaterial.Concrete));
            Assert.That(b, Is.EqualTo(a));
            Assert.That(c, Is.EqualTo(a));
        }
    }
}
