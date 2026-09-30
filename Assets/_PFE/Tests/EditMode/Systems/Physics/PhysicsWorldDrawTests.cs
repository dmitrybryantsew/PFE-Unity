using NUnit.Framework;
using UnityEngine.LowLevelPhysics2D;
using PFE.Core;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Pins the switch that silences Unity's own LowLevelPhysics2D debug draw.
    ///
    /// <para><b>The defect these tests exist for.</b> <see cref="PhysicsWorld"/> debug-draws every world
    /// by default — <c>PhysicsWorldDefinition.defaultDefinition.drawOptions</c> is not
    /// <see cref="PhysicsWorld.DrawOptions.Off"/>. <see cref="PhysicsWorldService"/> overrode
    /// <c>simulateType</c>, <c>simulationWorkers</c>, <c>continuousAllowed</c> and <c>gravity</c> and
    /// nothing else, so the chain mirror was rendered by the engine: the "connected grey circles" along
    /// every mirrored surface. It is a <c>PhysicsWorld</c> field, not one of our
    /// <c>DebugOverlayChannel</c>s, which is why <c>col off</c> could never silence it.</para>
    ///
    /// <para><see cref="ServiceWorld_ReflectsTheDiagnosticChannel"/> is the one that fails against the
    /// pre-fix code; the rest pin the mapping so the toggle cannot become a silent no-op.</para>
    /// </summary>
    [TestFixture]
    public class PhysicsWorldDrawTests
    {
        /// <summary>
        /// The shipping default: with diagnostics off the engine draws nothing. This is the value the
        /// user asked for — the game and nothing else.
        /// </summary>
        [Test]
        public void For_Off_IsTheEngineDrawSwitchedOff()
        {
            Assert.AreEqual(PhysicsWorld.DrawOptions.Off, PhysicsWorldDraw.For(false));
        }

        /// <summary>
        /// The toggle must actually turn something on. A mapping that returned <c>Off</c> for both
        /// inputs would pass every "is it off" assertion and leave the diagnostic unreachable — the
        /// "toggle is on and the screen shows nothing" failure this project has hit before.
        /// </summary>
        [Test]
        public void For_On_IsNotOff()
        {
            Assert.AreNotEqual(PhysicsWorld.DrawOptions.Off, PhysicsWorldDraw.For(true));
        }

        /// <summary>
        /// <see cref="PhysicsWorldDraw.Enabled"/> must never resolve to <c>Off</c>, even on a Unity
        /// version whose <c>defaultDefinition</c> ships the draw disabled — otherwise deriving the
        /// "on" value from the engine default would quietly make the switch inert.
        /// </summary>
        [Test]
        public void Enabled_IsNeverOff()
        {
            Assert.AreNotEqual(PhysicsWorld.DrawOptions.Off, PhysicsWorldDraw.Enabled);
        }

        /// <summary>The two states must differ, or the channel is decorative.</summary>
        [Test]
        public void For_On_DiffersFromForOff()
        {
            Assert.AreNotEqual(PhysicsWorldDraw.For(false), PhysicsWorldDraw.For(true));
        }

        /// <summary>
        /// <c>For(true)</c> is <see cref="PhysicsWorldDraw.Enabled"/> and nothing else — so there is
        /// one definition of "on", not two that can drift.
        /// </summary>
        [Test]
        public void For_On_IsExactlyEnabled()
        {
            Assert.AreEqual(PhysicsWorldDraw.Enabled, PhysicsWorldDraw.For(true));
        }

        /// <summary>
        /// The fix, end to end at the seam that mattered: the world the service owns must carry the
        /// <b>resolved diagnostic state</b>, not whatever the engine's default happened to be.
        ///
        /// <para>The shipped <c>PfeDebugSettings.asset</c> has no <c>enabledOverlays</c> key, so the
        /// resolved state is <c>None</c> and the expectation is <c>Off</c>. The assertion is written
        /// against the resolved value rather than a literal so that turning the channel on in the
        /// asset does not make this test lie.</para>
        /// </summary>
        [Test]
        public void ServiceWorld_ReflectsTheDiagnosticChannel()
        {
            PhysicsWorld.DrawOptions expected = PhysicsWorldDraw.For(
                DebugOverlays.IsOn(DebugOverlayChannel.LowLevelPhysics));

            var service = new PhysicsWorldService();
            try
            {
                Assert.IsTrue(service.IsWorldValid, "precondition: the service built a world");
                Assert.AreEqual(expected, service.World.drawOptions,
                    "the world must carry the resolved draw state; inheriting " +
                    "defaultDefinition.drawOptions is the 'connected grey circles' bug");
            }
            finally
            {
                // PhysicsConstants.MaxWorlds caps concurrency — a leaked world would fail a later
                // test for a reason unrelated to anything under test.
                service.Dispose();
            }
        }
    }
}
