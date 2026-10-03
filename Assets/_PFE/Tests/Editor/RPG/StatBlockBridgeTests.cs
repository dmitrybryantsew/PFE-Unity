using NUnit.Framework;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using UnityEngine;

namespace PFE.Tests.Editor.RPG
{
    [TestFixture]
    public class StatBlockBridgeTests
    {
        private GameObject _go;
        private CharacterStats _charStats;
        private UnitStats _unitStats;
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

            _go = new GameObject("TestPlayerObject");
            _charStats = _go.AddComponent<CharacterStats>();
            _charStats.Initialize(_curve);

            _unitStats = new UnitStats(100f, 400f);
            _charStats.BindUnitStats(_unitStats);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_curve != null) Object.DestroyImmediate(_curve);
        }

        [Test]
        [Description("The mana ORGAN and the mana BUDGET are two pools, not one")]
        public void ManaPool_OrganAndBudget_AreSeparatePools()
        {
            // This test used to be `ManaPool_IsSynchronizedBothWays` and asserted that
            // `charStats.manaHp` and `unitStats.Mana` were literally the same number. That was the
            // merged-pool design, and it was unfaithful: AS3 keeps them apart.
            //
            //   Unit.as:138,140  mana / maxmana = 1000   <- the regenerating BUDGET
            //   Pers.as:141,540  manaHP = inMaxMana      <- the WOUND organ (400 here, 1000 default)
            //
            // So the correct contract is that they are DISTINCT: charStats.manaHp follows the organ
            // and unitStats.Mana follows the budget. They coincide only when inMaxMana happens to
            // equal inMaxMagic, which is coincidence, not identity.
            Assert.AreEqual(400f, _charStats.manaHp, 1e-4f,
                "manaHp is the mana ORGAN, seeded from inMaxMana (Pers.as:540)");
            Assert.AreEqual(1000f, _unitStats.Mana.Value, 1e-4f,
                "UnitStats.Mana is the BUDGET, seeded from its own ceiling (Unit.as:138)");

            // The organ is damaged independently and must not drag the budget with it.
            _charStats.manaHp -= 50f;
            Assert.AreEqual(350f, _charStats.manaHp, 1e-4f, "Organ takes the wound");
            Assert.AreEqual(1000f, _unitStats.Mana.Value, 1e-4f,
                "Damaging the organ must NOT spend the budget");

            // ...and vice versa: spending the budget does not wound the organ.
            _unitStats.Mana.Value = 200f;
            Assert.AreEqual(200f, _charStats.MagicMana, 1e-4f,
                "MagicMana reads the budget when a UnitStats is bound");
            Assert.AreEqual(350f, _charStats.manaHp, 1e-4f,
                "Spending the budget must NOT wound the organ");
        }

        [Test]
        [Description("Combat stats are pushed from CharacterStats to UnitStats upon recalculation")]
        public void CombatStats_PushedToUnitStats()
        {
            _charStats.critCh = 0.15f;
            _charStats.critDamMult = 2.5f;
            _charStats.allDamMult = 1.3f;
            _charStats.skin = 5f;
            _charStats.dexter = 1.4f;

            _charStats.SyncUnitStats();

            Assert.AreEqual(0.15f, _unitStats.critChanceBonus, 1e-4f);
            Assert.AreEqual(0.5f, _unitStats.critDamageBonus, 1e-4f); // 2.5 - 2.0
            Assert.AreEqual(1.3f, _unitStats.damageMultiplier, 1e-4f);
            Assert.AreEqual(5f, _unitStats.skinResistance, 1e-4f);
            Assert.AreEqual(1.4f, _unitStats.dexterity, 1e-4f);
        }

        [Test]
        [Description("Level progression scales both CharacterStats and UnitStats vitals")]
        public void LevelUp_ScalesBothStatBlocks()
        {
            int initialHp = (int)_charStats.MaxHp;
            Assert.AreEqual(100, initialHp);
            Assert.AreEqual(100f, _unitStats.MaxHp.Value, 1e-4f);

            // Grant 5000 XP to level up to 2
            _charStats.AddXp(5000);

            Assert.AreEqual(2, _charStats.Level);
            Assert.AreEqual(115f, _charStats.MaxHp, 1e-4f);
            Assert.AreEqual(115f, _unitStats.MaxHp.Value, 1e-4f);
            Assert.AreEqual(240f, _charStats.InMaxHP, 1e-4f);
            Assert.AreEqual(5, _charStats.SkillPoints);
            Assert.AreEqual(1, _charStats.PerkPoints);
        }

        [Test]
        [Description("PlayerLocomotionAbilities reads ability flags from CharacterStats")]
        public void LocomotionAbilities_ReadsCharacterStats()
        {
            var abilities = _go.AddComponent<PlayerLocomotionAbilities>();

            // Initially false
            Assert.IsFalse(abilities.CanDoubleJump);
            Assert.IsFalse(abilities.CanLevitate);
            Assert.IsFalse(abilities.CanTeleport);

            // Set flags via CharacterStats
            _charStats.isDJ = 1;
            _charStats.levitOn = 1;
            _charStats.portPoss = 1;
            _charStats.allSpeedMult = 1.25f;

            Assert.IsTrue(abilities.CanDoubleJump);
            Assert.IsTrue(abilities.CanLevitate);
            Assert.IsTrue(abilities.CanTeleport);
            Assert.AreEqual(1.25f, abilities.MoveSpeedMultiplier, 1e-4f);
        }

        [Test]
        [Description("Mana stage 4 trauma disables levitation, double jump, and spells")]
        public void TraumaMana_Stage4_DisablesMagicalAbilities()
        {
            _charStats.isDJ = 1;
            _charStats.levitOn = 1;
            _charStats.spellsPoss = 1;

            // Damage mana to 0 -> stage 4
            _charStats.ApplyManaDamage(450f);

            Assert.AreEqual(4, _charStats.manaSt);
            Assert.AreEqual(0, _charStats.isDJ);
            Assert.AreEqual(0, _charStats.levitOn);
            Assert.AreEqual(0, _charStats.spellsPoss);
        }

        [Test]
        [Description("HealAll restores all organs, vitals, and clears trauma stages")]
        public void HealAll_RestoresAllOrgansAndVitals()
        {
            _charStats.ApplyOrganDamage(50f);
            _charStats.ApplyBloodDamage(60f);
            _charStats.ApplyManaDamage(100f);

            _charStats.HealAll();

            Assert.AreEqual(_charStats.InMaxHP, _charStats.headHp);
            Assert.AreEqual(_charStats.InMaxHP, _charStats.torsHp);
            Assert.AreEqual(_charStats.InMaxHP, _charStats.legsHp);
            Assert.AreEqual(_charStats.InMaxHP, _charStats.bloodHp);
            Assert.AreEqual(_charStats.MaxMana, _charStats.manaHp);
            Assert.AreEqual(0, _charStats.headSt);
            Assert.AreEqual(0, _charStats.torsSt);
            Assert.AreEqual(0, _charStats.legsSt);
            Assert.AreEqual(0, _charStats.bloodSt);
            Assert.AreEqual(0, _charStats.manaSt);
        }

        [Test]
        [Description("AddXp emits floating +Xxp text to DamageEventFeed")]
        public void AddXp_EmitsFloatingXpText()
        {
            PFE.Systems.Combat.DamageEventFeed.Default.Clear();

            _charStats.AddXp(250);

            var list = new System.Collections.Generic.List<PFE.Systems.Combat.DamageNumber>();
            int live = PFE.Systems.Combat.DamageEventFeed.Default.AdvanceAndSnapshot(0f, list);

            Assert.GreaterOrEqual(live, 1);
            Assert.AreEqual("+250xp", PFE.Systems.Combat.DamageEventFeed.TextFor(list[list.Count - 1]));
        }

        [Test]
        [Description("GetSkillTierForWeapon maps AS3 skill codes 1..7 to proper skill tiers")]
        public void GetSkillTierForWeapon_MapsCorrectly()
        {
            _charStats.SetSkillLevel("melee", 5);      // Tier 2 (>=5)
            _charStats.SetSkillLevel("smallguns", 14);  // Tier 4 (>=14)
            _charStats.SetSkillLevel("repair", 2);     // Tier 1 (>=2)
            _charStats.SetSkillLevel("energy", 20);    // Tier 5 (>=20)

            Assert.AreEqual(2, _charStats.GetSkillTierForWeapon(1)); // melee
            Assert.AreEqual(4, _charStats.GetSkillTierForWeapon(2)); // smallguns
            Assert.AreEqual(1, _charStats.GetSkillTierForWeapon(3)); // repair
            Assert.AreEqual(5, _charStats.GetSkillTierForWeapon(4)); // energy
            Assert.AreEqual(0, _charStats.GetSkillTierForWeapon(5)); // explosives (0)
        }

        [Test]
        [Description("CharacterStats save and load roundtrips level, XP, skills, and organ vitals")]
        public void SaveAndLoad_RoundtripsAccurately()
        {
            _charStats.AddXp(1200);
            _charStats.SetSkillLevel("tele", 4);
            _charStats.headHp = 180f;
            _charStats.torsHp = 160f;

            var saveData = _charStats.GetSaveData();
            Assert.IsNotNull(saveData);
            Assert.AreEqual(_charStats.Level, saveData.level);
            Assert.AreEqual(_charStats.Xp, saveData.xp);

            var newGo = new GameObject("LoadedPlayer");
            var newStats = newGo.AddComponent<CharacterStats>();
            newStats.Initialize(_curve);

            newStats.LoadSaveData(saveData);

            Assert.AreEqual(_charStats.Level, newStats.Level);
            Assert.AreEqual(_charStats.Xp, newStats.Xp);
            Assert.AreEqual(4, newStats.GetSkillLevel("tele"));
            Assert.AreEqual(180f, newStats.headHp, 1e-2f);
            Assert.AreEqual(160f, newStats.torsHp, 1e-2f);

            Object.DestroyImmediate(newGo);
        }
    }
}
