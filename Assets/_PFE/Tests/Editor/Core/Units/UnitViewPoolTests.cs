using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Tests.Editor.Core.Units
{
    /// <summary>
    /// Thread J's UnitViewPool: the pooled <see cref="UnitView"/> that replaces the inline-built
    /// "Visual" child.
    ///
    /// <para>The claims pinned here, in the order they can break: the pool builds a correctly
    /// configured view; a released clean view is parked and reused; a decorated view (extra
    /// components or children — a shield dome, turret parts, an NPC persona animator) is destroyed
    /// rather than recycled; a reused view carries none of the previous occupant's state; and the
    /// spawner routes through the pool only when one is attached, releasing views on teardown so a
    /// room re-entry reuses the same instance. The inline path (no pool) is pinned as unchanged.</para>
    /// </summary>
    [TestFixture]
    public class UnitViewPoolTests
    {
        GameObject _root;
        Transform _parent;
        UnitViewPool _pool;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("UnitViewPoolTests");
            var parentObject = new GameObject("Units");
            parentObject.transform.SetParent(_root.transform, false);
            _parent = parentObject.transform;
            _pool = new UnitViewPool();
        }

        [TearDown]
        public void TearDown()
        {
            // The pool root is a scene-level object, not a child of _root — without Shutdown a test
            // leaves it (and every parked view) behind for the next fixture.
            _pool.Shutdown();

            if (_root != null)
            {
                UnityEngine.Object.DestroyImmediate(_root);
            }
        }

        // ── fixtures ─────────────────────────────────────────────────────────────────────────────

        private sealed class FakeUnitDefinitions : IUnitDefinitionProvider
        {
            private readonly Dictionary<string, UnitDefinition> _byId =
                new Dictionary<string, UnitDefinition>(StringComparer.OrdinalIgnoreCase);

            public FakeUnitDefinitions Add(UnitDefinition definition)
            {
                _byId[definition.id] = definition;
                return this;
            }

            public bool TryGetUnit(string unitId, out UnitDefinition definition)
            {
                definition = null;
                return !string.IsNullOrEmpty(unitId) && _byId.TryGetValue(unitId, out definition);
            }
        }

        private static UnitDefinition MakeDefinition(string id, int health = 500)
        {
            var definition = ScriptableObject.CreateInstance<UnitDefinition>();
            definition.id = id;
            definition.health = health;
            definition.width = 2f;
            definition.height = 2f;
            return definition;
        }

        private static RoomInstance MakeRoom(string id = "pool_room")
        {
            var room = new RoomInstance
            {
                id = id,
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
            };
            room.InitializeTiles();
            return room;
        }

        private static UnitInstance MakeUnit(string unitId, Vector2 position)
        {
            return new UnitInstance
            {
                unitId = unitId,
                unitType = unitId,
                entityId = unitId,
                position = position,
                facingDirection = 1,
                maxHealth = 100f,
                currentHealth = 100f
            };
        }

        private static FakeUnitDefinitions TrainingDummyProvider()
        {
            return new FakeUnitDefinitions().Add(MakeDefinition("training"));
        }

        // ── the pool on its own ──────────────────────────────────────────────────────────────────

        [Test]
        public void Acquire_BuildsConfiguredVisualUnderParent()
        {
            UnitView view = _pool.Acquire(MakeDefinition("training"), MakeUnit("training", new Vector2(100f, 200f)), _parent, 7);

            Assert.That(view, Is.Not.Null);
            Assert.That(view.name, Is.EqualTo("Visual"), "the inline build's name — ClimberBrain searches for it");
            Assert.That(view.gameObject.activeSelf, Is.True);
            Assert.That(view.transform.parent, Is.SameAs(_parent));
            Assert.That(view.BodyRenderer, Is.Not.Null);
            Assert.That(view.Animator, Is.Not.Null);
            Assert.That(view.BodyRenderer.sortingOrder, Is.EqualTo(7));
            Assert.That(view.BodyRenderer.sortingLayerName, Is.EqualTo(MapSortingLayers.BackgroundPhysicalObjects));
            Assert.That(_pool.ActiveCount, Is.EqualTo(1));
            Assert.That(_pool.IdleCount, Is.EqualTo(0));
        }

        [Test]
        public void Release_CleanView_IsParkedAndReused()
        {
            UnitView view = _pool.Acquire(MakeDefinition("training"), MakeUnit("training", Vector2.zero), _parent, 1);

            _pool.Release(view);

            Assert.That(_pool.ActiveCount, Is.EqualTo(0));
            Assert.That(_pool.IdleCount, Is.EqualTo(1));
            Assert.That(view.gameObject.activeSelf, Is.False, "a parked view is deactivated");
            Assert.That(view.transform.parent, Is.Not.SameAs(_parent), "a parked view leaves the unit root");

            UnitView reused = _pool.Acquire(MakeDefinition("training"), MakeUnit("training", Vector2.zero), _parent, 2);

            Assert.That(reused, Is.SameAs(view), "the whole point: the GameObject is reused, not rebuilt");
            Assert.That(reused.gameObject.activeSelf, Is.True);
            Assert.That(reused.transform.parent, Is.SameAs(_parent));
            Assert.That(reused.BodyRenderer.sortingOrder, Is.EqualTo(2), "re-configured for the new occupant");
            Assert.That(_pool.ActiveCount, Is.EqualTo(1));
            Assert.That(_pool.IdleCount, Is.EqualTo(0));
        }

        [Test]
        public void Release_ViewWithExtraComponent_IsDestroyedNotRecycled()
        {
            UnitView view = _pool.Acquire(MakeDefinition("training"), MakeUnit("training", Vector2.zero), _parent, 1);

            // What an NPC controller or a shield overlay does to the view GO: the component is
            // persona-specific, so the view must not be handed to the next unit.
            view.gameObject.AddComponent<BoxCollider2D>();

            _pool.Release(view);

            Assert.That(_pool.IdleCount, Is.EqualTo(0), "a decorated view is not parked");
            Assert.IsTrue(view == null, "a decorated view is destroyed on release");
        }

        [Test]
        public void Release_ViewWithChild_IsDestroyedNotRecycled()
        {
            UnitView view = _pool.Acquire(MakeDefinition("training"), MakeUnit("training", Vector2.zero), _parent, 1);

            // The shield dome is a child GO, not a component on the view — the other decoration shape.
            var dome = new GameObject("ShieldDome");
            dome.transform.SetParent(view.transform, false);

            _pool.Release(view);

            Assert.That(_pool.IdleCount, Is.EqualTo(0));
            Assert.IsTrue(view == null, "a view with children is destroyed on release");
        }

        [Test]
        public void Reacquire_ResetsThePreviousOccupantsState()
        {
            UnitView view = _pool.Acquire(MakeDefinition("training"), MakeUnit("training", Vector2.zero), _parent, 3);

            // The state a previous unit can leave behind: a corpse-dissolve fade, a persona sprite,
            // a disabled renderer, an anchor offset.
            var texture = new Texture2D(4, 4);
            view.BodyRenderer.sprite = Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            view.BodyRenderer.color = Color.red;
            view.BodyRenderer.enabled = true;
            view.transform.localPosition = new Vector3(9f, 9f, 0f);

            _pool.Release(view);
            UnitView reused = _pool.Acquire(MakeDefinition("training"), MakeUnit("training", Vector2.zero), _parent, 8);

            Assert.That(reused, Is.SameAs(view));
            Assert.That(reused.BodyRenderer.color, Is.EqualTo(Color.white), "the fade does not leak");
            Assert.That(reused.BodyRenderer.sprite, Is.Null, "the persona sprite does not leak");
            Assert.That(reused.BodyRenderer.enabled, Is.False, "no sprite on the new definition = disabled, as a fresh build");
            Assert.That(reused.BodyRenderer.sortingOrder, Is.EqualTo(8));

            UnityEngine.Object.DestroyImmediate(texture);
        }

        [Test]
        public void Release_Null_IsSafe()
        {
            Assert.DoesNotThrow(() => _pool.Release(null));
            Assert.That(_pool.ActiveCount, Is.EqualTo(0));
            Assert.That(_pool.IdleCount, Is.EqualTo(0));
        }

        [Test]
        public void Shutdown_DestroysParkedViewsAndTheRoot()
        {
            UnitView view = _pool.Acquire(MakeDefinition("training"), MakeUnit("training", Vector2.zero), _parent, 1);
            _pool.Release(view);

            _pool.Shutdown();

            Assert.That(_pool.IdleCount, Is.EqualTo(0));
            Assert.IsTrue(view == null, "Shutdown destroys the parked views");
        }

        // ── the spawner integration ──────────────────────────────────────────────────────────────

        [Test]
        public void Spawner_WithPool_AcquiresViewsAndReleasesThemOnTeardown()
        {
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f)));
            room.units.Add(MakeUnit("training", new Vector2(300f, 200f)));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider(), unitViewPool: _pool);
            spawner.RefreshAll();

            Assert.That(_pool.ActiveCount, Is.EqualTo(2), "one pooled view per unit");
            Assert.That(_pool.IdleCount, Is.EqualTo(0));

            var views = new List<UnitView>();
            for (int i = 0; i < _parent.childCount; i++)
            {
                UnitView view = _parent.GetChild(i).GetComponentInChildren<UnitView>(true);
                Assert.That(view, Is.Not.Null, "every unit root carries its pooled Visual child");
                Assert.That(view.name, Is.EqualTo("Visual"));
                Assert.That(view.transform.parent, Is.SameAs(_parent.GetChild(i)),
                    "the view stays a child of the unit root, so controller hierarchy searches keep working");
                views.Add(view);
            }

            spawner.DestroyAll();

            Assert.That(_pool.ActiveCount, Is.EqualTo(0));
            Assert.That(_pool.IdleCount, Is.EqualTo(2), "teardown releases the views back to the pool");
            foreach (UnitView view in views)
            {
                Assert.IsFalse(view == null, "the view outlives the unit root it was released from");
                Assert.That(view.gameObject.activeSelf, Is.False);
            }
        }

        [Test]
        public void Spawner_WithoutPool_KeepsTheInlineVisualPath()
        {
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f)));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider());
            spawner.RefreshAll();

            Assert.That(_pool.ActiveCount, Is.EqualTo(0), "no pool attached = the pool is never touched");
            Assert.That(_pool.IdleCount, Is.EqualTo(0));

            UnitView inline = _parent.GetChild(0).GetComponentInChildren<UnitView>(true);
            Assert.That(inline, Is.Not.Null, "the inline path builds the same UnitView shape");

            spawner.DestroyAll();

            Assert.IsTrue(inline == null, "an inline view dies with its unit root, exactly as before");
        }

        [Test]
        public void Spawner_RoomReEntry_ReusesTheSameViewInstance()
        {
            RoomInstance room = MakeRoom();
            room.units.Add(MakeUnit("training", new Vector2(100f, 200f)));

            var spawner = new RoomUnitSpawner(room, _parent, TrainingDummyProvider(), unitViewPool: _pool);

            spawner.RefreshAll();
            UnitView first = _parent.GetChild(0).GetComponentInChildren<UnitView>(true);
            Assert.That(first, Is.Not.Null);

            // The room-transition pair: teardown releases the view, the next entry's spawn re-acquires it.
            spawner.DestroyAll();
            spawner.RefreshAll();

            Assert.That(_pool.ActiveCount, Is.EqualTo(1));
            Assert.That(_pool.IdleCount, Is.EqualTo(0), "the parked view was reused, not left behind");

            UnitView second = _parent.GetChild(0).GetComponentInChildren<UnitView>(true);
            Assert.That(second, Is.SameAs(first),
                "the payoff: a room re-entry reuses the renderer/animator instead of rebuilding them");
        }
    }
}
