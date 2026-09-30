using System.Text.RegularExpressions;
using NUnit.Framework;
using R3;
using UnityEngine;
using UnityEngine.TestTools;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Inventory;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the join between the armour <b>item</b> and its <b>combat projection</b> — the design doc's
    /// steps 5 and 8.
    ///
    /// <para><b>What was broken before this fixture.</b> <c>GameArmorInstance</c> held
    /// <c>currentHealth</c>/<c>maxHealth</c>/<c>level</c> and a <c>ScriptableObject</c> definition, and
    /// nothing read it: no path projected its ratings into combat, and nothing wrote combat wear back
    /// into its condition. <c>Repair()</c> and <c>GameArmorSaveData.currentHealth</c> were therefore
    /// both dead — the plate could be "damaged" only inside a projection that was thrown away.</para>
    ///
    /// <para><b>The invariant these tests exist to protect:</b> the item is the single source of truth
    /// for condition. <see cref="ArmourState.integrity"/> is a snapshot, and every mutation writes
    /// through. A test that only asserted the projection would pass against an implementation that
    /// never touched the item — which is exactly the bug.</para>
    /// </summary>
    [TestFixture]
    public class ArmourEquipTests
    {
        // === Helpers ===

        private static ItemDefinition ArmourDefinition(
            int armor = 15,
            int magicArmor = 8,
            int armorHP = 100,
            float reliability = 0.65f,
            int armorTip = 1,
            string itemId = "test_plate",
            bool hideMane = false)
        {
            // ScriptableObject, not a plain object: ItemDefinition is an asset type. CreateInstance
            // is the EditMode-safe way to get one without touching the asset database.
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            definition.itemId = itemId;
            definition.armorHP = armorHP;
            definition.armorTip = armorTip;
            definition.armorHideMane = hideMane;
            definition.equipment = new EquipmentData
            {
                armor = armor,
                magicArmor = magicArmor,
                dexterity = 0f,
                reliability = reliability,
            };
            return definition;
        }

        private static GameArmorInstance Armour(float health = float.MaxValue, int armorHP = 100)
            => new GameArmorInstance(ArmourDefinition(armorHP: armorHP), health);

        /// <summary>
        /// A stand-in item, to prove the seam works without a ScriptableObject. Also the reason
        /// <see cref="IArmourItem"/> is an interface rather than a concrete reference.
        /// </summary>
        private sealed class FakeArmourItem : IArmourItem
        {
            public ArmourState State;
            public float WrittenIntegrity = -1f;
            public int WriteCount;

            /// <summary>Identity is part of the seam now, because the visual layer needs it.</summary>
            public string Id { get; set; } = "fake";

            public bool HideMane { get; set; }

            /// <summary>Defaults to repairable, so tests that do not care are unaffected.</summary>
            public bool CanRepair { get; set; } = true;

            public ArmourState ToArmourState() => State;

            public void SetIntegrity(float integrity)
            {
                WrittenIntegrity = integrity;
                WriteCount++;
                State.integrity = integrity;
            }
        }

        private static DamageOutcome Wear(float integrityDamage, float hpDamage = 0f)
            => new DamageOutcome(hpDamage: hpDamage, armourIntegrityDamage: integrityDamage);

        // === The projection: item → ArmourState ===

        [Test]
        public void ToArmourState_ReadsTheRatingsFromTheDefinition()
        {
            // The port's split: condition on the instance, ratings on the definition. AS3 keeps both
            // on the Armor object, but the ratings are parsed from XML and never vary per instance.
            var item = Armour();

            ArmourState state = item.ToArmourState();

            Assert.AreEqual(15f, state.physicalRating, 1e-4f, "EquipmentData.armor -> physical rating.");
            Assert.AreEqual(8f, state.energyRating, 1e-4f, "EquipmentData.magicArmor -> energy rating.");
            Assert.AreEqual(0.65f, state.reliability, 1e-4f, "EquipmentData.reliability -> the qual chance.");
            Assert.AreEqual(ArmourModel.EquippedItem, state.model,
                "An item projection must carry the item model, or the wear table is the wrong one.");
        }

        [Test]
        public void ToArmourState_CarriesTheItemsOwnCondition()
        {
            var item = Armour(health: 30f, armorHP: 100);

            ArmourState state = item.ToArmourState();

            Assert.AreEqual(30f, state.integrity, 1e-4f);
            Assert.AreEqual(100f, state.maxIntegrity, 1e-4f);
            Assert.AreEqual(0.8f, state.ConditionFactor, 1e-4f,
                "30/100 is below half, so AS3's factor is 0.5 + 0.3 = 0.8 (Armor.as:303-305).");
        }

        [Test]
        public void MaxHealth_DefaultsTo100_WhenTheDefinitionDeclaresNoArmorHP()
        {
            // Armor.as:74-76 defaults hp and maxhp to 100, and Armor.as:105-107 only overwrites them
            // if the item element carries @hp. So 100 is the oracle default, not an invented constant —
            // the old `maxHealth = 100f; // TODO` was right about the value and wrong about it being
            // unreachable.
            var item = Armour(armorHP: 0);

            Assert.AreEqual(100f, item.MaxHealth, 1e-4f);
        }

        [Test]
        public void MaxHealth_ReadsArmorHP_WhenTheDefinitionDeclaresIt()
        {
            // The half the TODO was actually about: a definition that declares durability must be read.
            var item = Armour(armorHP: 250);

            Assert.AreEqual(250f, item.MaxHealth, 1e-4f);
            Assert.AreEqual(250f, item.ToArmourState().maxIntegrity, 1e-4f);
        }

        [Test]
        public void ToArmourState_LeavesAnUndeclaredReliabilityAtZero()
        {
            // AS3's field default for armor_qual is 0 (Armor.as:30) and isrnd(0) is never true, so a
            // plate authored without `qual` applies its rating NEVER — not always, and not half.
            // Kept deliberately sharp because it is the oracle, and pinned here because a silent 0 on a
            // hand-authored asset is exactly the default somebody "fixes" to 1 on the assumption that
            // it is a bug. ArmourDataParser always writes `qual`, so imported armour never hits it.
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            definition.equipment = new EquipmentData { armor = 10 };

            var item = new GameArmorInstance(definition);

            Assert.AreEqual(0f, item.ToArmourState().reliability, 1e-4f,
                "Zero is AS3's default and means 'the rating never applies'. If this ever becomes 1, " +
                "hand-authored armour silently gains full reliability.");
        }

        [Test]
        public void ToArmourState_CarriesTheResistsAndTheIndestructibleFlag()
        {
            var definition = ArmourDefinition();
            definition.armorIndestructible = true;
            definition.equipment.resists = new ResistTable { bullet = 0.2f, spark = -0.3f };

            ArmourState state = new GameArmorInstance(definition).ToArmourState();

            Assert.AreEqual(0.2f, state.resists.bullet, 1e-4f);
            Assert.AreEqual(-0.3f, state.resists.spark, 1e-4f,
                "Negative resists are real: metal armour has spark='-0.3' — a vulnerability.");
            Assert.IsTrue(state.indestructible);
        }

        [Test]
        public void AnIndestructibleArmour_TakesNoWear()
        {
            // AS3 Armor.damage():315-318 returns early for `und`, before any of the wear multipliers.
            var definition = ArmourDefinition();
            definition.armorIndestructible = true;
            var state = new GameArmorInstance(definition).ToArmourState();

            Assert.AreEqual(0f, state.WearFrom(DamageType.Acid, 100f), 1e-4f,
                "Acid is x2 wear normally; indestructible means none at all.");
            Assert.AreEqual(0f, state.WearFrom(DamageType.PhysicalBullet, 100f), 1e-4f);
        }

        [Test]
        public void TheResistReducesWearWithoutAnyCallerPassingIt()
        {
            // The signature used to take `resist` with a default of 0, so a caller who had the data and
            // forgot to pass it silently got no resistance. The table now travels with the projection,
            // which is what makes this call shape possible.
            var definition = ArmourDefinition();
            definition.equipment.resists = new ResistTable { bullet = 0.25f };
            var state = new GameArmorInstance(definition).ToArmourState();

            Assert.AreEqual(75f, state.WearFrom(DamageType.PhysicalBullet, 100f), 1e-4f,
                "100 * (1 - 0.25) — no resist argument required.");
            Assert.AreEqual(100f, state.WearFrom(DamageType.Blade, 100f), 1e-4f,
                "Blade has no resist set, so the full damage wears.");
        }

        [Test]
        public void ToArmourState_ReadsTheRatingsAtTheItemsUpgradeLevel()
        {
            // AS3 Armor.getXmlParam(this.xml.upd[this.lvl]) — the ratings are per level, so reading the
            // base set regardless of level would report a level-2 plate with level-0 numbers.
            var definition = ArmourDefinition(armor: 4, magicArmor: 3, reliability: 0.6f);
            definition.armourLevels = new[]
            {
                definition.equipment,
                new EquipmentData { armor = 5, magicArmor = 4, reliability = 0.7f },
                new EquipmentData { armor = 6, magicArmor = 5, reliability = 0.8f },
            };

            Assert.AreEqual(4f, new GameArmorInstance(definition, armorLevel: 0).ToArmourState().physicalRating, 1e-4f);
            Assert.AreEqual(6f, new GameArmorInstance(definition, armorLevel: 2).ToArmourState().physicalRating, 1e-4f);
            Assert.AreEqual(0.8f, new GameArmorInstance(definition, armorLevel: 2).ToArmourState().reliability, 1e-4f);
        }

        [Test]
        public void AnOutOfRangeLevel_ClampsInsteadOfReturningZeroedStats()
        {
            // AS3 indexes upd[lvl] directly, so an out-of-range level there is `undefined` and every
            // field reads as 0 — an armour with no ratings at all. Clamping is the deliberate
            // difference, and it is asserted so nobody "restores" the oracle's failure mode.
            var definition = ArmourDefinition(armor: 12);
            definition.armourLevels = new[] { definition.equipment };

            Assert.AreEqual(12f, definition.EquipmentAtLevel(7).armor, 1e-4f);
            Assert.AreEqual(12f, definition.EquipmentAtLevel(-3).armor, 1e-4f);
        }

        // === Equip: the projection reaches UnitStats ===

        [Test]
        public void EquipArmour_FromAnItem_ProjectsAndRemembersIt()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var item = Armour(health: 60f);

            stats.EquipArmour(item);

            Assert.AreSame(item, stats.ArmourItem, "The item must be held, or nothing can be written back.");
            Assert.AreEqual(15f, stats.armour.physicalRating, 1e-4f);
            Assert.AreEqual(60f, stats.armour.integrity, 1e-4f);
        }

        [Test]
        public void EquipArmour_FromABareState_LeavesNoItem()
        {
            // The unit-pool path: an NPC's armour has no item behind it, so there is nothing to write
            // back to and the pool *is* the state.
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);

            stats.EquipArmour(ArmourState.FromUnitPool(50f, 50f, 20f, 0f, 1f));

            Assert.IsNull(stats.ArmourItem);
            Assert.AreEqual(ArmourModel.UnitPool, stats.armour.model);
        }

        [Test]
        public void EquippingASecondItem_ReplacesRatherThanStacks()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var first = Armour();
            var second = new GameArmorInstance(
                ArmourDefinition(armor: 40, magicArmor: 2, armorHP: 80, reliability: 0.4f));

            stats.EquipArmour(first);
            stats.EquipArmour(second);

            Assert.AreSame(second, stats.ArmourItem);
            Assert.AreEqual(40f, stats.armour.physicalRating, 1e-4f);
            Assert.AreEqual(80f, stats.armour.maxIntegrity, 1e-4f);
        }

        // === Write-back: combat wear → item condition (design step 8) ===

        [Test]
        public void ApplyDamage_WritesTheDepletedConditionBackToTheItem()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var item = Armour(health: 100f, armorHP: 100);
            stats.EquipArmour(item);

            stats.ApplyDamage(Wear(integrityDamage: 20f));

            Assert.AreEqual(80f, item.CurrentHealth, 1e-4f,
                "The item is the source of truth for condition — the projection must not be the only " +
                "place the wear exists, or Repair() and the save are both dead.");
            Assert.AreEqual(80f, stats.armour.integrity, 1e-4f, "And the projection agrees.");
        }

        [Test]
        public void ApplyDamage_AccumulatesAcrossHitsOnTheItem()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var item = Armour(health: 100f, armorHP: 100);
            stats.EquipArmour(item);

            stats.ApplyDamage(Wear(integrityDamage: 10f));
            stats.ApplyDamage(Wear(integrityDamage: 15f));
            stats.ApplyDamage(Wear(integrityDamage: 5f));

            Assert.AreEqual(70f, item.CurrentHealth, 1e-4f, "70 = 100 - 10 - 15 - 5.");
        }

        [Test]
        public void TheBreakingHit_ZeroesTheItemBeforeUnequipping()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var item = Armour(health: 10f, armorHP: 100);
            stats.EquipArmour(item);

            bool broke = stats.ApplyDamage(Wear(integrityDamage: 15f, hpDamage: 10f));

            Assert.IsTrue(broke);
            Assert.AreEqual(0f, item.CurrentHealth, 1e-4f,
                "The item records the hit that finished it, clamped at 0 — not -5.");
            Assert.IsFalse(stats.armour.IsEquipped, "AS3 changeArmor(\"off\") on break (Armor.as:331-334).");
            Assert.IsNull(stats.ArmourItem, "A broken plate is not still linked to the unit.");
            Assert.AreEqual(90f, stats.CurrentHp.Value, 1e-4f, "The health damage still lands.");
        }

        [Test]
        public void TheItemSurvivesTheBreak_SoItCanBeRepaired()
        {
            // The whole point of writing back: a broken plate is a *damaged object*, not a lost one.
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var item = Armour(health: 5f, armorHP: 100);
            stats.EquipArmour(item);

            stats.ApplyDamage(Wear(integrityDamage: 50f));

            Assert.AreEqual(0f, item.CurrentHealth, 1e-4f);
            item.Repair(40f);
            Assert.AreEqual(40f, item.CurrentHealth, 1e-4f, "Repair() now has something real to restore.");

            // Repair does not re-equip — AS3's setArmor() is guarded on `active`, which the break
            // cleared. The player must re-equip; that is the interaction layer's job (Phase 8).
            stats.EquipArmour(item);
            Assert.AreEqual(40f, stats.armour.integrity, 1e-4f,
                "Re-equipping re-reads the item, because the item is the truth.");
        }

        [Test]
        public void WithoutAnItem_TheWearStaysInTheProjectionOnly()
        {
            // Positive control for the write-back tests: the pool path has no item, so the same hit
            // must still deplete the projection and must not throw on a null ArmourItem.
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            stats.EquipArmour(ArmourState.FromUnitPool(100f, 100f, 20f, 0f, 1f));

            Assert.DoesNotThrow(() => stats.ApplyDamage(Wear(integrityDamage: 20f)));
            Assert.AreEqual(80f, stats.armour.integrity, 1e-4f);
        }

        [Test]
        public void TheInterfaceSeam_WorksWithoutAScriptableObject()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var fake = new FakeArmourItem
            {
                State = ArmourState.FromItem(100f, 100f, 12f, 4f, 1f),
            };

            stats.EquipArmour(fake);
            Assert.AreEqual(12f, stats.armour.physicalRating, 1e-4f);

            stats.ApplyDamage(Wear(integrityDamage: 25f));

            Assert.AreEqual(1, fake.WriteCount);
            Assert.AreEqual(75f, fake.WrittenIntegrity, 1e-4f);
        }

        [Test]
        public void ApplyingDamageWithNoArmour_DoesNotTouchTheItemPath()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);

            bool broke = stats.ApplyDamage(Wear(integrityDamage: 10f, hpDamage: 5f));

            Assert.IsFalse(broke);
            Assert.AreEqual(95f, stats.CurrentHp.Value, 1e-4f);
            Assert.IsNull(stats.ArmourItem);
        }

        // === Repair through UnitStats ===

        [Test]
        public void RepairArmour_RestoresBothTheProjectionAndTheItem()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var item = Armour(health: 100f, armorHP: 100);
            stats.EquipArmour(item);
            stats.ApplyDamage(Wear(integrityDamage: 40f));

            stats.RepairArmour(25f);

            Assert.AreEqual(85f, stats.armour.integrity, 1e-4f, "60 + 25.");
            Assert.AreEqual(85f, item.CurrentHealth, 1e-4f, "And the item moved with it.");
        }

        [Test]
        public void RepairArmour_ClampsAtMaxIntegrity()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var item = Armour(health: 100f, armorHP: 100);
            stats.EquipArmour(item);
            stats.ApplyDamage(Wear(integrityDamage: 10f));

            stats.RepairArmour(500f);

            Assert.AreEqual(100f, stats.armour.integrity, 1e-4f);
            Assert.AreEqual(1f, stats.armour.ConditionFactor, 1e-4f,
                "An over-repair must not push the condition factor above 1.");
        }

        [Test]
        public void RepairArmour_DoesNothingWhenNothingIsEquipped()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);

            Assert.DoesNotThrow(() => stats.RepairArmour(50f));
            Assert.IsFalse(stats.armour.IsEquipped);
        }

        [Test]
        public void UnequipArmour_ClearsTheItemLinkAndTheMirrors()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var item = Armour(health: 60f);
            stats.EquipArmour(item);

            stats.UnequipArmour();

            Assert.IsNull(stats.ArmourItem);
            Assert.IsFalse(stats.armour.IsEquipped);
            Assert.AreEqual(0f, stats.ArmourIntegrity.Value, 1e-4f);
            Assert.IsFalse(stats.HasArmour.Value);
        }

        // === The HUD's reactive mirror ===

        [Test]
        public void TheReactiveMirrors_TrackEquipWearAndUnequip()
        {
            // The HUD binds to these, and `armour` is a struct R3 cannot observe — so a mirror that
            // only moved on equip would leave the bar frozen at full while the plate wore away.
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);

            Assert.AreEqual(0f, stats.ArmourIntegrity.Value, 1e-4f);
            Assert.IsFalse(stats.HasArmour.Value, "No armour at boot: the bar starts hidden.");

            var item = Armour(health: 100f, armorHP: 100);
            stats.EquipArmour(item);

            Assert.AreEqual(1f, stats.ArmourIntegrity.Value, 1e-4f);
            Assert.IsTrue(stats.HasArmour.Value);

            stats.ApplyDamage(Wear(integrityDamage: 25f));

            Assert.AreEqual(0.75f, stats.ArmourIntegrity.Value, 1e-4f, "The bar must follow the wear.");
            Assert.IsTrue(stats.HasArmour.Value, "Still equipped, so still visible.");
        }

        [Test]
        public void TheMirrors_ZeroAndHideWhenTheArmourBreaks()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            stats.EquipArmour(Armour(health: 10f, armorHP: 100));

            stats.ApplyDamage(Wear(integrityDamage: 10f));

            Assert.AreEqual(0f, stats.ArmourIntegrity.Value, 1e-4f);
            Assert.IsFalse(stats.HasArmour.Value, "A broken plate hides its bar rather than showing an empty one.");
        }

        [Test]
        public void TheMirrors_FollowRepair()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            stats.EquipArmour(Armour(health: 40f, armorHP: 100));

            Assert.AreEqual(0.4f, stats.ArmourIntegrity.Value, 1e-4f,
                "Equipping a partly-worn plate must show its real condition, not a full bar.");

            stats.RepairArmour(60f);

            Assert.AreEqual(1f, stats.ArmourIntegrity.Value, 1e-4f);
        }

        // === The pink rule (Armor.as:180-183) ===

        [Test]
        public void BodyArmour_CarriesTheConstructorsPinkVulnerability()
        {
            // Not data: AS3 assigns it in the constructor, after zeroing the array, and it has no XML
            // attribute. `ArmourWear.ItemIntegrityDamage` already documented that it expects -0.5 here,
            // and until the projection supplied one the expectation could never be met.
            var body = Armour();
            var amulet = new GameArmorInstance(
                ArmourDefinition(armorTip: 3), health: 100f);

            Assert.AreEqual(-0.5f, body.ToArmourState().resists.GetResist(DamageType.Pink), 1e-4f,
                "tip == 1 -> resist[D_PINK] = -0.5.");
            Assert.AreEqual(0f, amulet.ToArmourState().resists.GetResist(DamageType.Pink), 1e-4f,
                "tip == 3 (amulet) is not body armour, so the constructor rule does not run.");
        }

        [Test]
        public void PinkWear_IsFourAndAHalfTimesOnBodyArmourAndThreeOnAnAmulet()
        {
            // The two rules compose: resist first, then pink's own x3 (Armor.as:321-329). A body plate
            // therefore takes 1.5 * 3 = x4.5, an amulet only x3 — which is the whole point of the
            // hardcoded -0.5, and it is invisible if the resist is not carried.
            var body = Armour().ToArmourState();
            var amulet = new GameArmorInstance(ArmourDefinition(armorTip: 3), health: 100f).ToArmourState();

            Assert.AreEqual(45f, body.WearFrom(DamageType.Pink, 10f), 1e-4f, "10 * 1.5 * 3.");
            Assert.AreEqual(30f, amulet.WearFrom(DamageType.Pink, 10f), 1e-4f, "10 * 1 * 3.");
        }

        [Test]
        public void ThePinkRule_DoesNotLeakIntoTheUnitPool()
        {
            // Armor.as is the *item* model. An NPC's pool goes through Unit.damage(), which has no
            // constructor rule and no tip — so the pool must keep pink at x0 wear, not inherit x3.
            var pool = ArmourState.FromUnitPool(100f, 100f, 20f, 0f, 1f);

            Assert.AreEqual(0f, pool.resists.GetResist(DamageType.Pink), 1e-4f);
            Assert.AreEqual(0f, pool.WearFrom(DamageType.Pink, 10f), 1e-4f,
                "Pink is not in the pool's wear range at all (Unit.as:3578).");
        }

        // === @norep (Armor.as:117-119, read by PipPageWork.as:258) ===

        [Test]
        public void ANoRepairArmour_RefusesToBeRepaired()
        {
            var definition = ArmourDefinition(armorHP: 100);
            definition.armorNoRepair = true;
            var item = new GameArmorInstance(definition, health: 20f);

            Assert.IsFalse(item.CanRepair);

            item.Repair(50f);

            Assert.AreEqual(20f, item.CurrentHealth, 1e-4f,
                "A no-op, not a clamp: AS3 never offers the action, so there is no partial repair.");
        }

        [Test]
        public void RepairArmourThroughUnitStats_AlsoRespectsNoRepair()
        {
            // The gate has to be on the item, not on one caller — UnitStats.RepairArmour() is the
            // production entry point and must not be able to route around it. This is the path that
            // DID route around it: it repairs the projection and then writes to the item through
            // SetIntegrity, which never passes through GameArmorInstance.Repair.
            //
            // Note AS3 does not gate inside repair() at all — Armor.as:340-348 is unconditional, and
            // PipPageWork.as:258 filters `!norep && !und && hp < maxhp` when building the workbench's
            // candidate list. So this asserts the port's deliberate structural choice, not AS3's shape.
            // What must match AS3 is the observable end: nothing repairs a `tre` plate.
            var definition = ArmourDefinition(armorHP: 100);
            definition.armorNoRepair = true;
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            var item = new GameArmorInstance(definition, health: 40f);
            stats.EquipArmour(item);

            stats.RepairArmour(30f);

            Assert.AreEqual(40f, item.CurrentHealth, 1e-4f);
            Assert.AreEqual(40f, stats.armour.integrity, 1e-4f);
        }

        [Test]
        public void AnOrdinaryArmour_StillRepairs()
        {
            // Positive control: the gate must be `norep`, not "repair is broken". 18 of the 35 armour
            // definitions in AllData are repairable.
            var item = Armour(health: 20f, armorHP: 100);

            Assert.IsTrue(item.CanRepair);
            item.Repair(50f);

            Assert.AreEqual(70f, item.CurrentHealth, 1e-4f);
        }

        // === The armour term stays inert with nothing equipped ===

        [Test]
        public void WithNoArmourEquipped_EveryArmourTermIsInert()
        {
            // The reason steps 1-5 were safe to land before any play-test: no unit has armour, so
            // nothing about damage changes until something equips a plate.
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);

            Assert.IsFalse(stats.armour.IsEquipped);
            Assert.AreEqual(0f, stats.armour.EffectivePhysicalRating, 1e-4f);
            Assert.AreEqual(0f, stats.armour.EffectiveEnergyRating, 1e-4f);
            Assert.AreEqual(0f, stats.armour.IntegrityPercent, 1e-4f);
        }

        // === The identity channel the character sprite binds to ===

        [Test]
        public void ArmourItem_CarriesItsDefinitionIdAndManeFlag()
        {
            var item = new GameArmorInstance(ArmourDefinition(itemId: "metal", hideMane: true), 100f);

            Assert.AreEqual("metal", item.Id, "AS3 Armor.id -> the visual layer's key.");
            Assert.IsTrue(item.HideMane, "AS3 Armor.hideMane -> Appear.hideMane.");
        }

        [Test]
        public void AnArmourWithNoDefinition_ReportsAnEmptyIdNotNull()
        {
            // Empty rather than null is a contract, not a detail: the visual layer compares the id
            // against "" to mean "no armour", which is what AS3 writes to Appear.ggArmorId.
            LogAssert.Expect(LogType.Error, new Regex("Cannot create instance with null definition"));

            var item = new GameArmorInstance(null, 100f);

            Assert.AreEqual(string.Empty, item.Id);
            Assert.IsFalse(item.HideMane);
        }

        [Test]
        public void EquippingAndUnequipping_PublishesTheId()
        {
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);

            Assert.AreEqual(string.Empty, stats.ArmourId.CurrentValue,
                "Nothing equipped -> empty id, so the sprite shows no armour set.");

            stats.EquipArmour(new GameArmorInstance(ArmourDefinition(itemId: "metal"), 100f));
            Assert.AreEqual("metal", stats.ArmourId.CurrentValue);

            stats.UnequipArmour();
            Assert.AreEqual(string.Empty, stats.ArmourId.CurrentValue);
        }

        [Test]
        public void SwappingArmour_NotifiesTheId_WhereHasArmourWouldNot()
        {
            // This is the whole reason ArmourId exists. HasArmour is a bool: plate A -> plate B leaves
            // it true, so R3 raises nothing and a view bound to it alone keeps rendering plate A.
            // AS3 sidesteps this by writing Appear.ggArmorId on every changeArmor() call.
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            stats.EquipArmour(new GameArmorInstance(ArmourDefinition(itemId: "pip"), 100f));

            int idNotifications = 0;
            int hasArmourNotifications = 0;
            var subs = new CompositeDisposable();
            stats.ArmourId.Subscribe(_ => idNotifications++).AddTo(subs);
            stats.HasArmour.Subscribe(_ => hasArmourNotifications++).AddTo(subs);

            // ReactiveProperty replays on subscribe, so both counters are 1 here — that is the state
            // before the swap. Zeroing them makes the assertions below about the swap alone.
            Assert.AreEqual(1, idNotifications);
            Assert.AreEqual(1, hasArmourNotifications);
            idNotifications = 0;
            hasArmourNotifications = 0;

            stats.EquipArmour(new GameArmorInstance(ArmourDefinition(itemId: "metal"), 100f));

            Assert.AreEqual("metal", stats.ArmourId.CurrentValue);
            Assert.AreEqual(1, idNotifications, "The id changed, so the sprite must be told.");
            Assert.AreEqual(0, hasArmourNotifications,
                "HasArmour stayed true — a view bound to it alone would never learn about the swap.");

            subs.Dispose();
        }

        [Test]
        public void BreakingArmour_ClearsTheId_SoTheSpriteComesOff()
        {
            // AS3 reaches this through changeArmor("off") inside Armor.damage() (Armor.as:331-334).
            // Here ApplyDamage routes the break through UnequipArmour, so the same notification that
            // clears HasArmour also clears ArmourId — which is what removes the sprite.
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);
            stats.EquipArmour(new GameArmorInstance(ArmourDefinition(itemId: "metal", armorHP: 100), 100f));

            bool broke = stats.ApplyDamage(Wear(500f));

            Assert.IsTrue(broke);
            Assert.AreEqual(string.Empty, stats.ArmourId.CurrentValue,
                "A broken plate is unequipped, so its sprite must go with it.");
            Assert.IsFalse(stats.HasArmour.CurrentValue);
        }

        [Test]
        public void AUnitPoolProjection_HasNoId()
        {
            // An NPC's own armour has no item behind it, and no sprite set either — so empty is the
            // right answer here, not a missing one.
            var stats = new UnitStats(maxHp: 100f, maxMana: 0f);

            stats.EquipArmour(ArmourState.FromUnitPool(50f, 50f, 20f, 0f, 1f));

            Assert.IsTrue(stats.HasArmour.CurrentValue);
            Assert.AreEqual(string.Empty, stats.ArmourId.CurrentValue);
        }
    }
}
