using NUnit.Framework;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using UnityEngine;

namespace PFE.Tests.Editor.RPG
{
    [TestFixture]
    public class StatModifierApplierTests
    {
        private GameObject _go;
        private CharacterStats _stats;
        private LevelCurve _curve;

        [SetUp]
        public void Setup()
        {
            _curve = ScriptableObject.CreateInstance<LevelCurve>();
            _curve.baseHp = 100;
            _curve.hpPerLevel = 15;
            _curve.baseOrganHp = 200;
            _curve.organHpPerLevel = 40;
            _curve.xpDelta = 5000;
            _curve.skillPointsPerLevel = 5;

            _go = new GameObject("TestCharacterStats");
            _stats = _go.AddComponent<CharacterStats>();
            _stats.Initialize(_curve);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_curve != null) Object.DestroyImmediate(_curve);
        }

        [Test]
        [Description("StatModifier with linear scaling: v0 + tier * vd")]
        public void Evaluate_LinearScaling_ComputesAccurateValue()
        {
            var mod = new StatModifier
            {
                statId = "meleeDamMult",
                refType = "add",
                v0 = 0.05f,
                hasV0 = true,
                vd = 0.05f,
                hasVd = true
            };

            // Tier 0: v0
            Assert.AreEqual(0.05f, mod.Evaluate(0, 0), 1e-4f);
            // Tier 1: v0 + 1 * vd = 0.10
            Assert.AreEqual(0.10f, mod.Evaluate(1, 2), 1e-4f);
            // Tier 3: v0 + 3 * vd = 0.20
            Assert.AreEqual(0.20f, mod.Evaluate(3, 9), 1e-4f);
            // Tier 5: v0 + 5 * vd = 0.30
            Assert.AreEqual(0.30f, mod.Evaluate(5, 20), 1e-4f);
        }

        [Test]
        [Description("StatModifier with discrete tier values: v[level], the AS3 attribute('v'+level) rule")]
        public void Evaluate_DiscreteValues_PicksTierCorrectly()
        {
            // The array is built the way SkillAndPerkDataImporter builds it: v[0] = v0, v[i] = v_i.
            // AS3 indexes it directly by level (`attribute("v"+level)`), so there is no -1 shift.
            var mod = new StatModifier
            {
                statId = "critCh",
                refType = "add",
                hasV0 = true,
                v0 = 0f,
                v = new float[] { 0f, 0.02f, 0.04f, 0.07f, 0.10f, 0.15f }
            };

            // Tier 0: v0
            Assert.AreEqual(0f, mod.Evaluate(0, 0), 1e-4f);
            // Tier 1: v1
            Assert.AreEqual(0.02f, mod.Evaluate(1, 2), 1e-4f);
            // Tier 2: v2
            Assert.AreEqual(0.04f, mod.Evaluate(2, 5), 1e-4f);
            // Tier 5: v5
            Assert.AreEqual(0.15f, mod.Evaluate(5, 20), 1e-4f);
        }

        [Test]
        [Description("Past the end of a discrete table AS3 falls back to v1, not to v[0]")]
        public void Evaluate_DiscreteValues_OutOfRange_FallsBackToV1()
        {
            // No v0 attribute: the importer fabricates v[0] = 0. Returning that for every
            // out-of-range level is what zeroed maxTeleMassa/spellsDamMult at mana-trauma stage 1.
            var mod = new StatModifier
            {
                statId = "maxTeleMassa",
                refType = "mult",
                hasV0 = false,
                v = new float[] { 0f, 1f, 1f, 0.8f, 0.3f }
            };

            // Rank 1 is inside the table: v1 = 1 (unchanged, not 0).
            Assert.AreEqual(1f, mod.Evaluate(1, 0), 1e-4f);
            // Rank 4 is the last entry: v4 = 0.3.
            Assert.AreEqual(0.3f, mod.Evaluate(4, 0), 1e-4f);
            // Rank 5 is past the end: AS3's Number(v1) = 1.
            Assert.AreEqual(1f, mod.Evaluate(5, 0), 1e-4f);
        }

        [Test]
        [Description("ref='mult' with vd is linear (base + level*vd), never compounded")]
        public void Evaluate_MultWithVd_IsLinear()
        {
            // AS3 Pers.as:1503 computes _loc7_ + _loc6_ * vd for every ref type; only the
            // application differs (*= vs +=). Compounding gave 0.99^p instead of 1 - 0.01p.
            var mod = new StatModifier
            {
                statId = "allVulnerMult",
                refType = "mult",
                hasV0 = false,
                vd = -0.01f,
                hasVd = true
            };

            // Level 0: identity for mult, because there is no v0.
            Assert.AreEqual(1f, mod.Evaluate(0, 0), 1e-4f);
            // Level 3: 1 - 0.03 = 0.97 (the oracle), not 0.99^3 = 0.9703.
            Assert.AreEqual(0.97f, mod.Evaluate(3, 3), 1e-4f);
            // Level 20: 1 - 0.20 = 0.80 (the oracle), not 0.99^20 = 0.8179.
            Assert.AreEqual(0.80f, mod.Evaluate(20, 20), 1e-4f);
        }

        [Test]
        [Description("StatModifier with dop flag uses raw points instead of tier")]
        public void Evaluate_DopFlag_UsesRawPoints()
        {
            var mod = new StatModifier
            {
                statId = "skin",
                refType = "add",
                dop = true,
                vd = 1f,
                hasVd = true
            };

            // 7 raw skill points => 7 * 1 = 7
            Assert.AreEqual(7f, mod.Evaluate(2, 7), 1e-4f);
        }

        [Test]
        [Description("A tip='weap' modifier keys weaponSkills by the AS3 numeric CODE and is read " +
                     "back by that same code -- mirroring the shipped Resources/Skills/smallguns.asset")]
        public void Apply_WeaponModifier_SetsWeaponSkill_ByCode()
        {
            // The shipped asset is exactly:
            //   statId: 2   tip: weap   dop: 1   v0: 1   vd: 0.05   v: []
            // The importer copies the AS3 `id` attribute verbatim into `statId`
            // (SkillAndPerkDataImporter.cs:297-298), so the key is "2", NOT "smallguns".
            //
            // The previous version of this test hand-built `statId = "smallguns"` and read it back
            // by the same name -- so it passed while production silently wrote "2" and the reader
            // looked up "smallguns". It tested the writer with an id the importer never produces.
            var mod = new StatModifier
            {
                statId = "2",
                tip = "weap",
                dop = true,
                hasV0 = true,
                v0 = 1f,
                hasVd = true,
                vd = 0.05f,
                v = new float[0]
            };

            // dop='1' -> AS3 reads param3, the raw POINTS (Pers.as:1477-1484), not the tier.
            StatModifierApplier.Apply(_stats, mod, 3, 9, "smallguns");
            Assert.AreEqual(1.45f, _stats.GetWeaponSkillMultiplier(2), 1e-4f,
                "9 points -> 1 + 9*0.05");

            // Positive control: the same code at the 20-point cap.
            StatModifierApplier.Apply(_stats, mod, 5, 20, "smallguns");
            Assert.AreEqual(2.0f, _stats.GetWeaponSkillMultiplier(2), 1e-4f,
                "20 points -> 2.0 (double damage)");

            // Absent control: a code nothing ever wrote must stay at the identity, so a reader
            // that returned a constant would fail here.
            Assert.AreEqual(1.0f, _stats.GetWeaponSkillMultiplier(5), 1e-4f,
                "unwritten code -> 1.0");
        }

        [Test]
        [Description("Apply allDamMult modifier correctly adjusts stat")]
        public void Apply_AllDamMultModifier_AdjustsStat()
        {
            float baseDam = _stats.AllDamMult;
            var mod = new StatModifier
            {
                statId = "allDamMult",
                refType = "add",
                v0 = 0.15f,
                hasV0 = true,
                vd = 0f,
                hasVd = false
            };

            StatModifierApplier.Apply(_stats, mod, 1, 1, "test_perk");
            Assert.AreEqual(baseDam + 0.15f, _stats.AllDamMult, 1e-4f);
        }
    }
}
