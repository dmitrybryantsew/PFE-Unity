using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using PFE.Entities.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins the one thing <see cref="Projectile.HandleImpact"/> must do on every landed hit: <b>stop the
    /// round</b>.
    ///
    /// <para><b>Why this fixture exists.</b> <c>HandleImpact</c> returning <c>true</c> is <i>not</i> the
    /// stop — it only tells the sweep loop to stop looking for the rest of this tick. The stop is
    /// <c>StartImpactAnimation()</c>, which zeroes the velocity, disables the trigger, and either plays
    /// the impact frames or hands the instance back to the pool. While the explosion visuals were being
    /// wired, that call was folded <i>inside</i> the <c>if (_explRadius &gt; 0f)</c> block, which took it
    /// out of the path of every kinetic round — and of every resolved tile hit, because
    /// <c>ResolveTileImpact</c> routes a found collider through the same method.</para>
    ///
    /// <para>The play-test symptoms were exactly what that predicts, and all three were reported at once:
    /// rounds passed through units while still dealing damage (the damage is dealt before the stop, so
    /// only the stop was missing), then through the whole line of them, and then through the wall —
    /// <c>TryTileContact</c>'s enter-only latch reads the next tick's contact as "already in contact", so
    /// the wall is never resolved and the round sails into the void. It also delivered its knockback to
    /// every unit in the line instead of only the first, which read as a much stronger push.</para>
    ///
    /// <para><b>No existing test failed</b>, because every one of them asserted the <i>return value</i>
    /// (which stayed <c>true</c>) rather than the stop. This fixture asserts the stop. That is the whole
    /// distinction it exists to record: a boolean that says "stop sweeping" is not a boolean that says
    /// "stop flying".</para>
    ///
    /// <para><b>Runs in the editor only.</b> <c>HandleImpact</c> reaches
    /// <c>ImpactSoundResolver.Resolve</c>, whose body mentions <c>Time.time</c>; the offline wall cannot
    /// JIT a method that mentions an <c>ECall</c>, so this fixture is exercised by the owner's EditMode
    /// run rather than by the offline harness. That is the same venue the bug would have been caught in,
    /// which is the point.</para>
    /// </summary>
    [TestFixture]
    public class ProjectileImpactStopTests
    {
        private GameObject _go;
        private Projectile _projectile;

        [SetUp]
        public void SetUp()
        {
            _go         = new GameObject("ProjectileImpactStop");
            _projectile = _go.AddComponent<Projectile>();

            // Awake/OnEnable do not run for AddComponent in EditMode, and this fixture calls the impact
            // path directly rather than through a tick, so only one piece of state matters:
            // `_registeredOnSimLoop` → FlipActive, so StartImpactAnimation DEFERS the pool release to
            // LateUpdate instead of destroying the GameObject from inside the call. That is what makes
            // the stop observable as a field rather than as a destroyed object.
            //
            // `_currentVisual` is left null on purpose: the stop then takes the "return to pool" branch
            // rather than the impact-frames branch, which is the branch a kinetic round takes.
            SetField("_registeredOnSimLoop", true);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        /// <summary>
        /// The regression. A hit on a plain, non-damageable, non-destructible surface with no blast
        /// radius is the ordinary kinetic case, and it must still stop the round.
        ///
        /// <para>Falsifiability: delete the <c>StartImpactAnimation()</c> call from
        /// <c>HandleImpact</c> and the second assertion goes red while the first stays green — which is
        /// precisely the shape of the shipped bug.</para>
        /// </summary>
        [Test]
        public void AnOrdinaryHit_StopsTheRound_NotMerelyTheSweep()
        {
            var surfaceGo = new GameObject("plainSurface");
            var surface   = surfaceGo.AddComponent<BoxCollider2D>();

            bool reportedStop;
            try
            {
                reportedStop = (bool)Invoke("HandleImpact", surface, Vector3.zero);
            }
            finally
            {
                Object.DestroyImmediate(surfaceGo);
            }

            Assert.IsTrue(reportedStop,
                "HandleImpact reports the stop so its caller stops sweeping for the rest of this tick. " +
                "This half stayed green throughout the bug, which is why it is not the assertion that " +
                "matters.");

            Assert.IsTrue((bool)GetField("_pendingReturnToPool"),
                "A landed hit must STOP the round. `return true` only ends the sweep for this tick — " +
                "StartImpactAnimation is what zeroes the velocity, disables the trigger and releases " +
                "the instance. Without it a bullet keeps flying after dealing damage, passes through " +
                "every unit in the line, and then sails through the wall (TryTileContact's enter-only " +
                "latch reads the next tick's contact as already-in-contact, so the wall is never " +
                "resolved). Do not fold StartImpactAnimation into the explosion branch — it is the " +
                "stop, not a visual.");
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────

        private object GetField(string name)
        {
            FieldInfo field = typeof(Projectile).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field,
                $"Projectile has no private instance field '{name}'. This fixture reflects over " +
                "runtime state, so a rename here is a test change, not a production change.");

            return field.GetValue(_projectile);
        }

        private void SetField(string name, object value)
        {
            FieldInfo field = typeof(Projectile).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field,
                $"Projectile has no private instance field '{name}'. This fixture reflects over " +
                "runtime state, so a rename here is a test change, not a production change.");

            field.SetValue(_projectile, value);
        }

        private object Invoke(string name, params object[] args)
        {
            MethodInfo method = typeof(Projectile).GetMethod(
                name, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(method,
                $"Projectile has no private instance method '{name}'. This fixture reflects over " +
                "runtime behaviour, so a rename here is a test change, not a production change.");

            return method.Invoke(_projectile, args);
        }
    }
}
