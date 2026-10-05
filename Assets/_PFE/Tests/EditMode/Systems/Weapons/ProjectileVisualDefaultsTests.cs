using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Combat;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="ProjectileVisualDefaults"/> — AS3's <c>visualBullet</c> fallback
    /// (<c>Weapon.as:521-525</c>) and the importer predicate that decides which weapons need it.
    ///
    /// <para><b>The defect these guard.</b> Six real guns — <c>robominigun</c>, <c>turret1</c>,
    /// <c>turret6</c>, <c>ttweap1</c>, <c>paint</c>, <c>punch</c> — were left with a null
    /// <c>projectileVisual</c>. A null definition reaches <c>Projectile.ApplyVisual</c>, which restores the
    /// template's own defaults, and the template's sprite is null: an enabled renderer drawing nothing, on
    /// a round that still collides and still damages. The importer's predicate required
    /// <c>WeaponType.Guns</c> or <c>BigGun</c> and so excluded <c>WeaponType.Internal</c> (tip='0') —
    /// the base <b>ranged</b> class, and the trap the surrounding code warns about by name.</para>
    ///
    /// <para><b>Positive and absent controls are paired on purpose.</b> A predicate that answers
    /// <c>true</c> for everything is indistinguishable from a correct one, so each "must wire" case has a
    /// "must not wire" case beside it — and the negative cases are the ones that catch an over-eager
    /// widening of the rule.</para>
    /// </summary>
    public class ProjectileVisualDefaultsTests
    {
        static bool Wire(string vbul, ProjectileArchetype archetype, WeaponType type, bool unarmed = false)
            => ProjectileVisualDefaults.ShouldWireDefaultVisual(vbul, archetype, type, unarmed);

        // ── the regression: tip='0' is ranged and needs the default ───────────

        [Test]
        public void ShouldWire_InternalIsRanged_SoItNeedsTheDefault()
        {
            Assert.IsTrue(
                Wire(null, ProjectileArchetype.Ballistic, WeaponType.Internal),
                "WeaponType.Internal (tip='0') is the base RANGED class — this is the arm that was missing.");
        }

        [Test]
        public void ShouldWire_GunsAndBigGun_KeepWorking()
        {
            Assert.IsTrue(Wire(null, ProjectileArchetype.Ballistic, WeaponType.Guns));
            Assert.IsTrue(Wire(null, ProjectileArchetype.Ballistic, WeaponType.BigGun));
        }

        // ── the absent controls ──────────────────────────────────────────────

        [Test]
        public void ShouldNotWire_WhenTheWeaponDeclaresItsOwnVbul()
        {
            Assert.IsFalse(Wire("gren40", ProjectileArchetype.Ballistic, WeaponType.Internal));
        }

        [Test]
        public void ShouldNotWire_ForTheFamiliesThatNeverSpawnABullet()
        {
            Assert.IsFalse(
                Wire(null, ProjectileArchetype.Ballistic, WeaponType.Melee),
                "A melee weapon spawns no projectile; wiring it would be inert data.");

            Assert.IsFalse(
                Wire(null, ProjectileArchetype.Ballistic, WeaponType.Thrown),
                "A thrown object draws the WEAPON's art (WThrow.as:38 `vBullet = vWeapon`), not the bullet default.");

            Assert.IsFalse(
                Wire(null, ProjectileArchetype.Ballistic, WeaponType.Internal, unarmed: true),
                "punch > 0 is the WPunch/WKick family (punch, paint, scorppunch) — no bullet.");
        }

        [Test]
        public void ShouldNotWire_ForANonBallisticArchetype()
        {
            Assert.IsFalse(Wire(null, ProjectileArchetype.Laser, WeaponType.Internal));

            // The nine `sp_*` spells are weaponType 5 / archetype 8, which is why they are out of scope
            // here rather than being a second instance of the same defect.
            Assert.IsFalse(Wire(null, ProjectileArchetype.Magic, WeaponType.Magic));
        }

        // ── the trigger and the id ───────────────────────────────────────────

        [Test]
        public void NeedsFallback_OnlyForAMissingDefinition()
        {
            Assert.IsTrue(ProjectileVisualDefaults.NeedsFallback(null));
        }

        [Test]
        public void Resolve_WithNothingToFallBackTo_StaysNull()
        {
            // A missing art asset must not take the weapon offline: the factory warns and carries on.
            Assert.IsNull(ProjectileVisualDefaults.Resolve(null, null));
        }

        [Test]
        public void ResourcePath_PointsAtTheImportedVisualBullet()
        {
            Assert.AreEqual("default_ballistic", ProjectileVisualDefaults.VisualId);
            Assert.AreEqual("ProjectileVisuals/default_ballistic", ProjectileVisualDefaults.ResourcePath);
        }
    }
}
