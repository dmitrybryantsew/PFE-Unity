using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;

namespace PFE.Tests.EditMode.Systems.Map
{
    /// <summary>
    /// Pins <see cref="TileHitRules.Resolve"/> — the three gates that wrap <c>Tile.udar</c> and the
    /// two post-gate branches that decide whether a decal is drawn.
    ///
    /// <para>Every test creates its own <see cref="TileData"/> so the tests are independent and
    /// order-free. The oracle is <c>Location.as:2503-2572</c>.</para>
    /// </summary>
    public sealed class TileHitRulesTests
    {
        private static TileData Wall(int hp = 1000, int threshold = 0, bool indestruct = false)
        {
            return new TileData
            {
                gridPosition = new Vector2Int(0, 0),
                physicsType = TilePhysicsType.Wall,
                hitPoints = hp,
                damageThreshold = threshold,
                indestructible = indestruct,
                levelTwoSolid = false,
            };
        }

        private static TileData Stair()
        {
            return new TileData
            {
                gridPosition = new Vector2Int(0, 0),
                physicsType = TilePhysicsType.Stair,
                hitPoints = 100,
                levelTwoSolid = false,
            };
        }

        private static TileData Grate()
        {
            return new TileData
            {
                gridPosition = new Vector2Int(0, 0),
                physicsType = TilePhysicsType.Wall,
                hitPoints = 100,
                levelTwoSolid = true,   // phis == 2 in AS3
            };
        }

        // ── Null guard ────────────────────────────────────────────────────────

        [Test]
        public void Resolve_NullTile_ReturnsIgnored()
        {
            var d = TileHitRules.Resolve(null, damage: 10f, decalType: 1);
            Assert.AreEqual(TileHitOutcome.Ignored, d.Outcome);
            Assert.IsFalse(d.DrawDecal);
        }

        // ── Ram decal (type 100) ──────────────────────────────────────────────

        [Test]
        public void Resolve_RamAgainstThreshold_ReturnsIgnored()
        {
            var d = TileHitRules.Resolve(Wall(threshold: 5), damage: 1f, decalType: 100);
            Assert.AreEqual(TileHitOutcome.Ignored, d.Outcome);
            Assert.IsFalse(d.DrawDecal);
        }

        [Test]
        public void Resolve_RamAgainstIndestructible_ReturnsIgnored()
        {
            var d = TileHitRules.Resolve(Wall(indestruct: true), damage: 1f, decalType: 100);
            Assert.AreEqual(TileHitOutcome.Ignored, d.Outcome);
        }

        [Test]
        public void Resolve_RamNoThreshold_RewritesToBlade()
        {
            var d = TileHitRules.Resolve(Wall(), damage: 10f, decalType: 100);
            Assert.AreEqual(TileHitOutcome.Damaged, d.Outcome);
            Assert.AreEqual(4, d.DecalType, "ram rewrites to blade decal (4)");
        }

        // ── desoff rooms ──────────────────────────────────────────────────────

        [Test]
        public void Resolve_DesoffHighHpWall_ReturnsVisualOnlyAndDrawsDecal()
        {
            var d = TileHitRules.Resolve(Wall(hp: 1000), damage: 10f, decalType: 1,
                                         destroyOn: false, roomActive: true);
            Assert.AreEqual(TileHitOutcome.VisualOnly, d.Outcome);
            Assert.IsTrue(d.DrawDecal, "phis==1 wall in active room draws decal");
            Assert.IsTrue(d.DecalBlocked);
            Assert.AreEqual(1000, d.HitPointsAfter, "hp unchanged");
        }

        [Test]
        public void Resolve_DesoffHighHpGrate_ReturnsVisualOnlyButNoDecal()
        {
            var d = TileHitRules.Resolve(Grate(), damage: 10f, decalType: 1,
                                         destroyOn: false, roomActive: true);
            Assert.AreEqual(TileHitOutcome.VisualOnly, d.Outcome);
            Assert.IsFalse(d.DrawDecal, "grate (phis==2) draws nothing even when active");
        }

        [Test]
        public void Resolve_DesoffLowHpWall_GoesThroughNormalDamage()
        {
            var d = TileHitRules.Resolve(Wall(hp: 400), damage: 10f, decalType: 1,
                                         destroyOn: false, roomActive: true);
            Assert.AreEqual(TileHitOutcome.Damaged, d.Outcome);
            Assert.IsTrue(d.DrawDecal);
            Assert.AreEqual(390, d.HitPointsAfter);
        }

        [Test]
        public void Resolve_DesoffHighHpInactiveRoom_NoDecal()
        {
            var d = TileHitRules.Resolve(Wall(hp: 1000), damage: 10f, decalType: 1,
                                         destroyOn: false, roomActive: false);
            Assert.AreEqual(TileHitOutcome.VisualOnly, d.Outcome);
            Assert.IsFalse(d.DrawDecal, "inactive room does not draw");
        }

        // ── Zero damage ───────────────────────────────────────────────────────

        [Test]
        public void Resolve_ZeroDamageOnDamageableWall_ReturnsDamagedAndDraws()
        {
            var d = TileHitRules.Resolve(Wall(), damage: 0f, decalType: 1);
            Assert.AreEqual(TileHitOutcome.Damaged, d.Outcome);
            Assert.IsTrue(d.DrawDecal);
            Assert.AreEqual(1000, d.HitPointsAfter, "hp unchanged");
        }

        [Test]
        public void Resolve_ZeroDamageOnStair_ReturnsIgnored()
        {
            var d = TileHitRules.Resolve(Stair(), damage: 0f, decalType: 1);
            Assert.AreEqual(TileHitOutcome.Ignored, d.Outcome);
            Assert.IsFalse(d.DrawDecal);
        }

        // ── Normal damage outcomes ────────────────────────────────────────────

        [Test]
        public void Resolve_StrongHitDestroys_ReturnsDestroyedNoDecal()
        {
            var d = TileHitRules.Resolve(Wall(hp: 10), damage: 50f, decalType: 1);
            Assert.AreEqual(TileHitOutcome.Destroyed, d.Outcome);
            Assert.IsFalse(d.DrawDecal, "death decal is caller's job");
        }

        [Test]
        public void Resolve_ModerateHitDamages_ReturnsDamagedUnblocked()
        {
            var d = TileHitRules.Resolve(Wall(hp: 1000), damage: 50f, decalType: 1);
            Assert.AreEqual(TileHitOutcome.Damaged, d.Outcome);
            Assert.IsTrue(d.DrawDecal);
            Assert.IsFalse(d.DecalBlocked);
            Assert.AreEqual(950, d.HitPointsAfter);
        }

        [Test]
        public void Resolve_WeakHitOnStair_ReturnsIgnored()
        {
            var d = TileHitRules.Resolve(Stair(), damage: 10f, decalType: 1);
            Assert.AreEqual(TileHitOutcome.Ignored, d.Outcome);
            Assert.IsFalse(d.DrawDecal);
        }

        [Test]
        public void Resolve_BelowThreshold_ReturnsRefusedBlockedDecal()
        {
            var d = TileHitRules.Resolve(Wall(hp: 1000, threshold: 50), damage: 10f, decalType: 1);
            Assert.AreEqual(TileHitOutcome.Refused, d.Outcome);
            Assert.IsTrue(d.DrawDecal);
            Assert.IsTrue(d.DecalBlocked);
            Assert.AreEqual(TileDamageOutcome.RefusedThreshold, d.Refusal);
        }

        [Test]
        public void Resolve_Indestructible_ReturnsRefusedBlockedDecal()
        {
            var d = TileHitRules.Resolve(Wall(indestruct: true), damage: 10f, decalType: 1);
            Assert.AreEqual(TileHitOutcome.Refused, d.Outcome);
            Assert.IsTrue(d.DrawDecal);
            Assert.IsTrue(d.DecalBlocked);
            Assert.AreEqual(TileDamageOutcome.RefusedIndestructible, d.Refusal);
        }

        // ── DecalType threading ───────────────────────────────────────────────

        [Test]
        public void Resolve_PreservesDecalTypeThroughAllBranches()
        {
            int tip = 7; // plasma
            var d1 = TileHitRules.Resolve(Wall(hp: 1000), damage: 50f, decalType: tip);
            Assert.AreEqual(tip, d1.DecalType);

            var d2 = TileHitRules.Resolve(Wall(hp: 10), damage: 50f, decalType: tip);
            Assert.AreEqual(tip, d2.DecalType);

            var d3 = TileHitRules.Resolve(Wall(indestruct: true), damage: 10f, decalType: tip);
            Assert.AreEqual(tip, d3.DecalType);
        }

        // ── Ratio sanity ──────────────────────────────────────────────────────

        [Test]
        public void Resolve_DamagedRatio_IsDamageOverRemainingHp()
        {
            var d = TileHitRules.Resolve(Wall(hp: 1000), damage: 250f, decalType: 1);
            Assert.AreEqual(TileHitOutcome.Damaged, d.Outcome);
            Assert.AreEqual(0.25f, d.DecalRatio, 0.001f);
        }

        [Test]
        public void Resolve_RefusedRatio_IsDamageOverRemainingHp()
        {
            var d = TileHitRules.Resolve(Wall(hp: 500, threshold: 100), damage: 50f, decalType: 1);
            Assert.AreEqual(TileHitOutcome.Refused, d.Outcome);
            Assert.AreEqual(0.1f, d.DecalRatio, 0.001f);
        }
    }
}
