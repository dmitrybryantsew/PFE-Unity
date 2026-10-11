using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Pins the <b>plumbing</b> half of the spike traps: that a <c>spikes</c>/<c>fspikes</c> object
    /// reaches <see cref="RoomObjectPhysicsLayer"/>'s trap registry, and that the query answers "nothing"
    /// rather than throwing when there is nothing to hit.
    ///
    /// <para><b>What this fixture cannot cover, and why.</b> The positive path needs a
    /// <c>MapObjectDefinition</c> to read <c>@size</c>/<c>@wid</c>/<c>@damage</c> from, and a
    /// ScriptableObject cannot be created outside the editor — so the box-building and damage rules are
    /// pinned by <c>TrapTriggerMathTests</c> instead, against the same numbers. What is left here is the
    /// part those tests cannot see: that <c>Register</c> is the hook, that a non-trap is refused, and
    /// that a trap without a definition is refused rather than registered as a trap that can never
    /// fire.</para>
    ///
    /// <para><b>The trap that drew and did nothing.</b> <c>RoomPopulator.CreateTrap</c> has always built
    /// an <c>ObjectInstance</c> with <c>objectType = "trap"</c> and nothing ever read it. These tests
    /// exist so that "the registry is empty" is a failure with a name rather than a room full of
    /// decorative spikes.</para>
    /// </summary>
    [TestFixture]
    public class RoomTrapRegistryTests
    {
        static ObjectInstance MakeObject(string objectType)
        {
            return new ObjectInstance
            {
                objectId = objectType,
                objectType = objectType,
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };
        }

        [Test]
        public void FreshLayer_HasNoTraps()
        {
            var layer = new RoomObjectPhysicsLayer();
            Assert.AreEqual(0, layer.TrapCount);
        }

        /// <summary>
        /// A trap-shaped object with <b>no definition</b> must not be registered: there is no
        /// <c>@damage</c> to read, so registering it would produce a trap that can never fire while the
        /// count claimed one existed — the silent no-op this whole feature was.
        /// </summary>
        [Test]
        public void TrapWithoutADefinition_IsRefused_RatherThanRegisteredAsADeadTrap()
        {
            var layer = new RoomObjectPhysicsLayer();

            layer.Register(MakeObject("trap"));

            Assert.AreEqual(0, layer.TrapCount,
                "A trap entry needs a definition for @size/@wid/@damage; without one it is refused.");
        }

        /// <summary>
        /// <c>objectType</c> is the marker <c>CreateTrap</c> stamps, so anything else must not enter the
        /// trap list — otherwise every crate and door in the room would be swept as a trap each step.
        /// </summary>
        [Test]
        public void NonTrapObjects_AreNotRegisteredAsTraps()
        {
            var layer = new RoomObjectPhysicsLayer();

            layer.Register(MakeObject("box"));
            layer.Register(MakeObject("door"));
            layer.Register(MakeObject("area"));

            Assert.AreEqual(0, layer.TrapCount);
        }

        /// <summary>
        /// Registering the same object twice must not double it. <c>RoomInstance.AddObject</c> calls
        /// <c>Register</c> directly and <c>Rebuild</c>'s loop calls it again for the same objects, so a
        /// unit standing on one trap would otherwise be hit by "two".
        /// </summary>
        [Test]
        public void RegisteringTheSameObjectTwice_DoesNotDuplicateIt()
        {
            var layer = new RoomObjectPhysicsLayer();
            ObjectInstance trap = MakeObject("trap");

            layer.Register(trap);
            layer.Register(trap);
            layer.Register(trap);

            Assert.AreEqual(0, layer.TrapCount,
                "Still zero — and this is the control that the count is not simply accumulating: the "
                + "same object three times, and an object with no definition is refused every time.");
        }

        [Test]
        public void Unregister_IsSafeForAnObjectThatWasNeverRegistered()
        {
            var layer = new RoomObjectPhysicsLayer();

            Assert.IsFalse(layer.Unregister(MakeObject("trap")));
            Assert.AreEqual(0, layer.TrapCount);
        }

        [Test]
        public void Rebuild_WithNoObjects_LeavesNoTraps()
        {
            var layer = new RoomObjectPhysicsLayer();

            layer.Register(MakeObject("trap"));
            layer.Rebuild(new System.Collections.Generic.List<ObjectInstance>());

            Assert.AreEqual(0, layer.TrapCount);
        }

        /// <summary>
        /// The query must answer "nothing here" for an empty registry instead of throwing — every unit in
        /// every room calls it once per step, and most rooms have no traps in reach.
        /// </summary>
        [Test]
        public void Query_OnAnEmptyRegistry_ReportsNoHit()
        {
            var layer = new RoomObjectPhysicsLayer();

            bool hit = layer.TryFindImpactingTrapFor(
                new Rect(0f, 0f, 40f, 70f),
                unitDyAs3: 20f,
                unitOsnDyAs3: 0f,
                massa: 10f,
                difficulty: 0f,
                isFlying: false,
                out RoomObjectPhysicsLayer.TrapEntry trap,
                out float damage);

            Assert.IsFalse(hit);
            Assert.IsNull(trap);
            Assert.AreEqual(0f, damage);
        }
    }
}
