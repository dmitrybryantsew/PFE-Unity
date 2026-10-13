using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;
using PFE.Systems.Combat;
using PFE.Systems.Map.Rendering;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins the fallback <see cref="Projectile"/> uses when the chain mirror reports a surface
    /// that no <see cref="TileCollider"/> owns.
    ///
    /// <para><b>Why this fixture exists.</b> A closed door stamps <c>TilePhysicsType.Wall</c> over
    /// the cells it covers (<c>DoorPropPresenter.ApplyTileCollision</c>), so
    /// <c>RoomChainGeometry</c> emits a chain surface there. But those cells are air in the shipped
    /// room maps, so <c>TileVisualManager.CreateAllTiles</c> added no <c>TileCollider</c> for them.
    /// <c>FindTileCollider</c> asked only for a <c>TileCollider</c>, found nothing, and
    /// <c>ResolveTileImpact</c> fell through to <c>StartImpactAnimation()</c> — the round stopped,
    /// played no blast, damaged no door and made no sound. That is the owner's "the grenade
    /// launcher doesn't hit some doors, the grenade just disappears".</para>
    ///
    /// <para>It is reachable only after something else in the room triggers the rebuild —
    /// <c>RoomInstance.TilesMutated</c> from a destroyed tile, which a <c>destroy='600'</c>
    /// explosive does on its first shot — because the chain is built when the room activates,
    /// <b>before</b> the doors stamp (RoomTransitionManager activates, then RoomVisualController
    /// initializes the objects). Hence "some doors": the doors in a room you have already blown a
    /// tile out of.</para>
    ///
    /// <para>The fix widens the probe to any <see cref="IDestructibleTile"/>, asked
    /// <b>second</b> so a real tile still resolves to exactly the collider it always did. That
    /// ordering is the second case here; without it the fix would be able to resolve a wall hit to
    /// some other object overlapping the same 6 px box, and a rename of the tile layers would
    /// stop mattering in the other direction.</para>
    ///
    /// <para><b>Runs in the editor only.</b> <c>FindTileCollider</c> calls
    /// <c>Physics2D.OverlapBoxAll</c>, an ECall, so the offline wall cannot JIT it — same venue
    /// and same reason as <see cref="ProjectileImpactStopTests"/>.</para>
    /// </summary>
    [TestFixture]
    public class ProjectileTileIdentityFallbackTests
    {
        private GameObject _projectileGo;
        private Projectile _projectile;
        private List<GameObject> _spawned;

        /// <summary>
        /// A stand-in for a door: a non-trigger collider carrying <see cref="IDestructibleTile"/>
        /// and no <see cref="TileCollider"/>. The interface is the only thing
        /// <c>FindTileCollider</c>'s fallback may accept, so the double implements it.
        /// </summary>
        private class DestructiblePropStub : MonoBehaviour, IDestructibleTile
        {
            public int DirectCalls;
            public int RadiusCalls;

            public void ApplyDestruction(Vector3 worldPosition, float destroyAmount, DamageType damageType)
                => DirectCalls++;

            public void ApplyDestructionRadius(Vector3 worldPosition, float radius,
                                               float destroyAmount, DamageType damageType)
                => RadiusCalls++;
        }

        [SetUp]
        public void SetUp()
        {
            _spawned   = new List<GameObject>();
            _projectileGo = new GameObject("ProjectileTileIdentityFallback");
            _projectile   = _projectileGo.AddComponent<Projectile>();

            // No normal, so the probe sits on the contact point itself rather than 2 px behind it.
            // The fixture places each candidate collider exactly where it probes to.
            SetField("_lastContactNormal", Vector2.zero);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            }

            if (_projectileGo != null) Object.DestroyImmediate(_projectileGo);
        }

        /// <summary>
        /// The regression. A surface the chain reports but no <c>TileCollider</c> owns — a closed
        /// door's stamped cell — must still resolve to something, or the round dies silently.
        /// </summary>
        [Test]
        public void AChainSurfaceNoTileColliderOwns_FallsBackToTheDestructibleProp()
        {
            GameObject door = NewStub("doorLikeSurface", withTileCollider: false);
            Collider2D found = ProbeAtDoor(door);

            Assert.IsNotNull(found,
                "The chain reports the door's stamped cell as solid, so a round aimed at it stops " +
                "there — and stopping without an identified collider means HandleImpact never runs, " +
                "so there is no blast, no door damage and no impact sound. That is the reported " +
                "'the grenade disappears'. Do not remove the IDestructibleTile fallback in " +
                "FindTileCollider: it is the only reason this probe answers at all.");

            Assert.AreSame(door.GetComponent<IDestructibleTile>(),
                           found.GetComponent<IDestructibleTile>(),
                "The fallback resolves the hit to the prop that owns the surface, so " +
                "HandleImpact routes it through the same IDestructibleTile branch a wall takes.");
        }

        /// <summary>
        /// A real tile still wins. The fallback is asked second for exactly this reason: without
        /// the ordering, a wall hit inside a room with an overlapping door collider could resolve
        /// to the door, and the surface sound and tile destruction would move with it.
        /// </summary>
        [Test]
        public void ARealTileColliderIsStillPreferredOverTheFallback()
        {
            GameObject tile   = NewStub("realTileSurface",   withTileCollider: true);
            GameObject doorGo = NewStub("doorPropOverlappingSameProbe", withTileCollider: false);

            // Make the two candidates overlap the same probe box, so the only thing that can
            // decide between them is the order the probes are asked in.
            doorGo.transform.position = tile.transform.position;

            Collider2D found = ProbeAtDoor(tile);

            Assert.IsNotNull(found);
            Assert.IsNotNull(found.GetComponent<TileCollider>(),
                "A wall must still resolve to its TileCollider — the narrow probe is asked first " +
                "precisely so the door fallback can never move a wall hit. If this fails, the " +
                "fallback probe has been promoted above the TileCollider probe.");
        }

        /// <summary>
        /// The honest-empty case, so the fallback is not a blanket truth. It is what keeps the
        /// "stopping without surface sound" warning in <c>ResolveTileImpact</c> reachable — a
        /// fallback that always answers would turn that failure into a silent one.
        /// </summary>
        [Test]
        public void NothingThereAtAll_StillAnswersNothing()
        {
            Assert.IsNull(ProbeAtDoor(null));
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a candidate surface at world (1, 1) — the position a 100 px contact point maps to
        /// — and asks <c>FindTileCollider</c> for it.
        /// </summary>
        private Collider2D ProbeAtDoor(GameObject at)
        {
            Vector2 contactPx = new Vector2(100f, 100f);
            if (at != null)
            {
                at.transform.position = new Vector3(1f, 1f, 0f);
            }

            return (Collider2D)Invoke("FindTileCollider", contactPx);
        }

        private GameObject NewStub(string name, bool withTileCollider)
        {
            GameObject go = new GameObject(name);
            var box = go.AddComponent<BoxCollider2D>();
            box.isTrigger = false;
            box.size      = new Vector2(0.4f, 0.4f);   // one tile, well inside the 0.06 probe box

            if (withTileCollider)
            {
                // RequireComponent(TileRenderer) is satisfied by AddComponent.
                go.AddComponent<TileCollider>();
            }
            else
            {
                go.AddComponent<DestructiblePropStub>();
            }

            _spawned.Add(go);
            return go;
        }

        private object GetField(string name)
        {
            var field = typeof(Projectile).GetField(
                name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.IsNotNull(field, $"Projectile has no private instance field '{name}'.");

            return field.GetValue(_projectile);
        }

        private void SetField(string name, object value)
        {
            var field = typeof(Projectile).GetField(
                name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.IsNotNull(field, $"Projectile has no private instance field '{name}'.");

            field.SetValue(_projectile, value);
        }

        private object Invoke(string name, params object[] args)
        {
            var method = typeof(Projectile).GetMethod(
                name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.IsNotNull(method, $"Projectile has no private instance method '{name}'.");

            return method.Invoke(_projectile, args);
        }
    }
}
