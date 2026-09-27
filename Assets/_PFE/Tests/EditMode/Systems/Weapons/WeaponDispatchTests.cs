using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using PFE.Data.Definitions;
using PFE.Systems.Weapons;
using PFE.Systems.Weapons.Controllers;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins AS3's weapon *class* dispatch — which <see cref="IWeaponController"/> a
    /// <see cref="WeaponDefinition"/> is handed.
    ///
    /// <para><b>Why this fixture exists.</b> Nothing asserted tip → controller, and that is precisely
    /// how the port shipped 57 mis-routed weapons. <c>WeaponDataImporter</c> copied AS3's <c>tip</c>
    /// into <c>WeaponType</c> and never read the separate <c>punch</c> attribute, while the factory
    /// read <c>WeaponType.Internal</c> (tip 0) as "unarmed". But <c>Weapon.create()</c> dispatches
    /// tip==1/12/4/5 → WClub/WPaint/WThrow/WMagic, <b>then punch &gt; 0 → WPunch</b>, and everything
    /// else to the base <c>Weapon</c> class — which is <i>ranged</i>. So every turret, drone/robot
    /// laser, zombie spitter, <c>alimray</c>, <c>robominigun</c> and <c>ttweap1..6</c> was driven by
    /// the punch controller: no projectile spawned, no held sprite drawn.</para>
    ///
    /// <para>The last test is the content contract — it reads the real imported assets, so it fails if
    /// the importer ever stops reading <c>punch</c> (the data half of the same defect).</para>
    ///
    /// <para>AS3 authority: <c>fe/weapon/Weapon.as:345-394</c>.
    /// Audit: <c>docs/OnWeaponsSystemImplementation/13_WeaponTypeBehaviourAudit_2026-09-27.md</c> §1.</para>
    /// </summary>
    [TestFixture]
    public class WeaponDispatchTests
    {
        private const string WeaponsPath = "Assets/_PFE/Data/Resources/Weapons";

        private readonly WeaponControllerFactory _factory = new WeaponControllerFactory();
        private readonly List<UnityEngine.Object> _spawned = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned) UnityEngine.Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        private WeaponDefinition MakeDef(WeaponType tip, int punch = 0)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId   = $"test_tip{(int)tip}_punch{punch}";
            def.weaponType = tip;
            def.punch      = punch;
            _spawned.Add(def);
            return def;
        }

        private Type ControllerFor(WeaponType tip, int punch = 0)
        {
            var def = MakeDef(tip, punch);
            var controller = _factory.Create(def);
            Assert.IsNotNull(controller, $"Factory returned null for tip={(int)tip}, punch={punch}.");
            Type type = controller.GetType();
            controller.Dispose();
            return type;
        }

        // ── The regression guard ─────────────────────────────────────────────────

        [Test]
        public void Tip0_WithNoPunchAttribute_IsRanged_NotUnarmed()
        {
            Assert.AreEqual(typeof(RangedWeaponController), ControllerFor(WeaponType.Internal),
                "tip='0' is AS3's base Weapon class, which is a RANGED weapon. Reading it as " +
                "'unarmed' is the defect that mis-routed 57 of the 60 tip==0 weapons (audit §1).");
        }

        [Test]
        public void PunchGreaterThanZero_IsUnarmed()
        {
            Assert.AreEqual(typeof(UnarmedWeaponController), ControllerFor(WeaponType.Internal, punch: 1),
                "AS3 reaches WPunch only through punch > 0 (Weapon.as:383-385); tip alone never selects it.");
        }

        [Test]
        public void TipIsTestedBeforePunch_SoTip1WithPunchStaysMelee()
        {
            Assert.AreEqual(typeof(MeleeWeaponController), ControllerFor(WeaponType.Melee, punch: 1),
                "Weapon.create() tests tip==1 before punch, so WClub wins when a node carries both.");
        }

        // ── The full tip table ───────────────────────────────────────────────────

        [TestCase(WeaponType.Internal, typeof(RangedWeaponController))]
        [TestCase(WeaponType.Melee,    typeof(MeleeWeaponController))]
        [TestCase(WeaponType.Guns,     typeof(RangedWeaponController))]
        [TestCase(WeaponType.BigGun,   typeof(RangedWeaponController))]
        [TestCase(WeaponType.Thrown,   typeof(ThrownWeaponController))]
        [TestCase(WeaponType.Magic,    typeof(MagicWeaponController))]
        public void Tip_RoutesToTheAs3Class(WeaponType tip, Type expected)
        {
            Assert.AreEqual(expected, ControllerFor(tip),
                $"tip={(int)tip} must map to {expected.Name} — see Weapon.as:345-394.");
        }

        [Test]
        public void Tip12_WPaint_HasNoCounterpart_SoItWarnsAndFallsBackToRanged()
        {
            var def = MakeDef((WeaponType)12);

            LogAssert.Expect(LogType.Warning, new Regex(@"tip=12"));

            var controller = _factory.Create(def);
            Assert.AreEqual(typeof(RangedWeaponController), controller.GetType(),
                "WPaint has no Unity class, so the fallback is Ranged — nothing silently becomes a punch.");
            controller.Dispose();
        }

        // ── The predicate both call sites share ──────────────────────────────────

        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(2, true)]
        public void IsUnarmed_IsExactlyPunchGreaterThanZero(int punch, bool expected)
        {
            Assert.AreEqual(expected, MakeDef(WeaponType.Internal, punch).IsUnarmed,
                "IsUnarmed is the single source of truth for the factory AND the sprite presenter. " +
                "It must never be 'weaponType == Internal', which is tip 0 = ranged.");
        }

        // ── The imported-content contract ────────────────────────────────────────

        private static List<WeaponDefinition> LoadAllWeaponDefs()
        {
            var defs = new List<WeaponDefinition>();
            foreach (string guid in AssetDatabase.FindAssets("t:WeaponDefinition", new[] { WeaponsPath }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
                if (def != null) defs.Add(def);
            }
            return defs;
        }

        [Test]
        public void ImportedContent_OnlyTheThreeScorpionPunches_AreUnarmed()
        {
            var unarmed = LoadAllWeaponDefs()
                .Where(d => d.IsUnarmed)
                .Select(d => d.weaponId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(
                new[] { "scorp2punch", "scorp3punch", "scorppunch" },
                unarmed,
                "AllData.as carries punch='1' on exactly these three weapons (UnitMonstrik.as:49/56/63). " +
                "An empty list means the importer is not reading `punch`; a longer list means tip==0 has " +
                "been mistaken for unarmed again. Re-run PFE/Data/Import Weapons from AllData.as.");
        }
    }
}
