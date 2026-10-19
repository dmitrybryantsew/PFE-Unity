using NUnit.Framework;
using PFE.Entities.Units;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Tests.Editor.UnitSprite
{
    /// <summary>
    /// Guards the one thing about <see cref="TurretPartPresenter"/> that is a <b>hierarchy</b> fact
    /// rather than a number: where the barrel and the lamp hang.
    ///
    /// <para><b>What is at stake.</b> The body renderer is not at the mount's origin.
    /// <see cref="UnitSpriteAnchor.ApplyTo"/> moves it by the anchor offset — 29 px right and 19 px up
    /// for <c>turret0</c> — so that the sprite's registration pixel lands on the unit origin. Parenting
    /// the gun to that renderer therefore shifts the barrel by the same offset, and the mount is drawn
    /// with its barrel floating up and to the side of the trunnion it turns about. Nothing about the
    /// rotation looks wrong, so it presents as "the turret is misplaced", which is the symptom that
    /// sent the previous pass looking at the origin table instead of the hierarchy.</para>
    ///
    /// <para><b>Why this is an EditMode test and not arithmetic.</b> <see cref="TurretParts"/> can be
    /// asserted offline because it only returns numbers; the offset being guarded here exists only in
    /// the transform tree, and a number that is right in isolation and applied to the wrong parent is
    /// exactly the defect. GameObjects can be built in the editor, so the tree can be read.</para>
    ///
    /// <para><b>It needs the bake.</b> <see cref="TurretPartPresenter.Attach"/> returns a presenter with
    /// no parts when <c>Resources/TurretParts</c> is absent, and the test reports itself as ignored
    /// rather than passing vacuously. The precondition is asserted, not assumed: if the anchor offset is
    /// zero the body art did not load, and the test would pass for a reason that has nothing to do with
    /// the hierarchy.</para>
    /// </summary>
    public class TurretPartPresenterTests
    {
        const float Tolerance = 1e-4f;

        GameObject _unit;
        GameObject _visual;

        [SetUp]
        public void SetUp()
        {
            // The same shape RoomUnitSpawner builds: a unit root that carries the facing scale, and a
            // `Visual` child holding the SpriteRenderer, the animator and this presenter.
            _unit = new GameObject("unit");
            _visual = new GameObject("Visual");
            _visual.transform.SetParent(_unit.transform, false);
        }

        [TearDown]
        public void TearDown()
        {
            if (_unit != null) Object.DestroyImmediate(_unit);
        }

        [Test]
        public void TheBarrel_HangsOffTheMountOrigin_NotOffTheBodyRenderer()
        {
            TurretPartPresenter presenter = AttachToVisual("turret0");
            if (presenter.GunPivot == null) Assert.Ignore(MissingParts);

            var body = _visual.GetComponent<SpriteRenderer>();
            Assert.That(body.transform.localPosition, Is.Not.EqualTo(Vector3.zero),
                "precondition: the anchor offsets the body renderer away from the origin. It is at zero, " +
                "so the body art did not load and this test cannot distinguish the two hierarchies.");

            // The rule, stated as the tree: the pivot is under a node that is a sibling of the body
            // renderer, so its position is measured from the unit origin and not from the anchor offset.
            Assert.That(presenter.GunPivot.parent, Is.Not.SameAs(body.transform),
                "the barrel is parented to the body renderer, so it inherits the anchor offset");
            Assert.That(presenter.GunPivot.parent.parent, Is.SameAs(_unit.transform),
                "the barrel's root must be a sibling of the body renderer, under the unit root");

            Assert.That(TurretParts.TryGetPlacements("turret0", out Vector2 gun, out _), Is.True);
            Vector3 expected = WorldCoordinates.PixelToUnity(TurretParts.LocalPixels(gun));
            Vector3 actual = presenter.GunPivot.position - _unit.transform.position;

            // `+ 0` rather than a direct compare: a Vector3 equality against a tolerance is what the
            // failure message needs to read, and Unity's Vector3 == is approximate.
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(Tolerance), "barrel x, from the mount origin");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(Tolerance), "barrel y, from the mount origin");
        }

        [Test]
        public void TheLamp_IsOnTheSameOriginAsTheBarrel()
        {
            TurretPartPresenter presenter = AttachToVisual("turret1");
            if (presenter.LampPivot == null) Assert.Ignore(MissingParts);

            Assert.That(presenter.LampPivot.parent.parent, Is.SameAs(_unit.transform),
                "the lamp's root must be a sibling of the body renderer, under the unit root");

            Assert.That(TurretParts.TryGetPlacements("turret1", out _, out Vector2 lamp), Is.True);
            Vector3 expected = WorldCoordinates.PixelToUnity(TurretParts.LocalPixels(lamp));
            Vector3 actual = presenter.LampPivot.position - _unit.transform.position;

            Assert.That(actual.x, Is.EqualTo(expected.x).Within(Tolerance), "lamp x, from the mount origin");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(Tolerance), "lamp y, from the mount origin");
        }

        [Test]
        public void ANonMount_GetsNoPresenter()
        {
            var body = _visual.AddComponent<SpriteRenderer>();
            Assert.That(TurretPartPresenter.Attach(body, "zombie0", 0, "Default"), Is.Null);
            Assert.That(body.GetComponent<TurretPartPresenter>(), Is.Null,
                "a non-mount must not even get the component, or every unit pays for a LateUpdate");
        }

        const string MissingParts =
            "Resources/TurretParts is absent -- run docs/Diagnostics/scripts/bake_turret_parts.py. " +
            "The presenter is then a no-op and this test cannot check the hierarchy it exists for.";

        TurretPartPresenter AttachToVisual(string unitId)
        {
            var body = _visual.AddComponent<SpriteRenderer>();
            TurretPartPresenter presenter = TurretPartPresenter.Attach(body, unitId, 0, "Default");
            Assert.That(presenter, Is.Not.Null, $"{unitId}: Attach returned null for a mount");
            return presenter;
        }
    }
}
