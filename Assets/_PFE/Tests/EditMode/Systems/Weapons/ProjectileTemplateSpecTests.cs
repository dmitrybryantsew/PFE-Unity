using NUnit.Framework;
using PFE.Systems.Combat;
using PFE.Systems.Map.Rendering;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="ProjectileTemplateSpec"/> — the values the code-built projectile template
    /// reproduces now that <c>Assets/_PFE/Prefabs/projectile.prefab</c> has been deleted.
    ///
    /// <para>Every expectation below is a <b>literal read off the prefab before it was removed</b>, not a
    /// restatement of the constant. That distinction is the whole point: a test whose expected value comes
    /// from the thing it tests asserts only that the constant equals itself (lesson #61), and a mutation
    /// of the constant would sail through it. These are real pins — change one and the test goes red.</para>
    ///
    /// <para>What is at stake: the collider numbers decide how every round collides (and the Stage C
    /// offline sweep reproduces the same shape in <c>RoomChainGeometry</c>), and the sorting layer decides
    /// whether rounds draw in front of the floor or behind it. Neither is visible offline any other way.</para>
    /// </summary>
    public class ProjectileTemplateSpecTests
    {
        // CapsuleCollider2D: m_Size {x: 0.93, y: 0.06}, m_Direction: 1 (Horizontal).
        [Test]
        public void ColliderSize_IsThe93PixelNeedle()
        {
            Assert.AreEqual(0.93f, ProjectileTemplateSpec.ColliderSize.x, 1e-6f);
            Assert.AreEqual(0.06f, ProjectileTemplateSpec.ColliderSize.y, 1e-6f);
        }

        // m_Offset {x: 0, y: -0.01}
        [Test]
        public void ColliderOffset_SitsAHairBelowTheTransformCentre()
        {
            Assert.AreEqual(0f,     ProjectileTemplateSpec.ColliderOffset.x, 1e-6f);
            Assert.AreEqual(-0.01f, ProjectileTemplateSpec.ColliderOffset.y, 1e-6f);
        }

        // m_SortingLayerID: -1379265165 → unsigned 2915702131 → Foreground in TagManager.
        // The prefab's cached m_SortingLayer: 8 was STALE — Weapons is index 8, Foreground is index 10.
        // The code path uses the NAME, so it cannot go stale again; this pins the name it must be.
        [Test]
        public void SortingLayer_IsForeground_NotTheStaleIndexEight()
        {
            Assert.AreEqual("Foreground", ProjectileTemplateSpec.SortingLayerName);
            Assert.AreEqual(MapSortingLayers.Foreground, ProjectileTemplateSpec.SortingLayerName);
        }

        // Rigidbody2D: m_Mass: 1, m_LinearDamping: 0, m_AngularDamping: 0.05, m_GravityScale: 1
        [Test]
        public void RigidbodyValues_MatchThePrefab()
        {
            Assert.AreEqual(1f,    ProjectileTemplateSpec.Mass, 1e-6f);
            Assert.AreEqual(0f,    ProjectileTemplateSpec.LinearDamping, 1e-6f);
            Assert.AreEqual(0.05f, ProjectileTemplateSpec.AngularDamping, 1e-6f);
            Assert.AreEqual(1f,    ProjectileTemplateSpec.GravityScale, 1e-6f);
        }

        // m_LocalPosition {x: 10000, y: 10000} — the off-map parking corner, which
        // Projectile.PoolParkingPosition now reads from here.
        [Test]
        public void ParkingPosition_IsTheOffMapCorner()
        {
            Assert.AreEqual(10000f, ProjectileTemplateSpec.ParkingPosition.x, 1e-6f);
            Assert.AreEqual(10000f, ProjectileTemplateSpec.ParkingPosition.y, 1e-6f);
        }

        // Root "projectile", one child "SpriteImage", m_SortingOrder: 0.
        [Test]
        public void NamesAndSortingOrder_MatchThePrefabHierarchy()
        {
            Assert.AreEqual("projectile",  ProjectileTemplateSpec.RootName);
            Assert.AreEqual("SpriteImage", ProjectileTemplateSpec.VisualChildName);
            Assert.AreEqual(0,             ProjectileTemplateSpec.SortingOrder);
        }
    }
}
