using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Tests for <see cref="ObjectInstance.GetHoldFrames"/> — how a hold duration is resolved from the
    /// two places AS3 looks.
    ///
    /// <para>AS3 reads <c>time</c> from the object <b>definition</b> row first
    /// (<c>Interact.as:308-311</c>) and then lets the <b>placed</b> node override it
    /// (<c>:399-402</c>), each guarded by <c>.length()</c>. So the placement wins only when the
    /// attribute is actually present — and "present" includes an explicit <c>0</c>, which is the
    /// authored way of saying "no hold on this one". Getting that backwards would hand a deliberately
    /// instant placement the definition's duration, which is why it is asserted directly.</para>
    /// </summary>
    [TestFixture]
    public class HoldFramesTests
    {
        private static ObjectInstance MakeObject(string placementTime, string definitionTime)
        {
            var obj = new ObjectInstance { objectId = "indoor2" };

            if (placementTime != null)
            {
                obj.attributes = new List<MapObjectAttributeData>
                {
                    new MapObjectAttributeData { key = "time", value = placementTime }
                };
            }

            if (definitionTime != null)
            {
                obj.definition = new MapObjectDefinition
                {
                    objectId = "indoor2",
                    legacyAttributes = new List<MapObjectAttributeData>
                    {
                        new MapObjectAttributeData { key = "time", value = definitionTime }
                    }
                };
            }

            return obj;
        }

        [Test]
        public void DefinitionSuppliesTheHoldWhenThePlacementDoesNot()
        {
            // The shipped case: every one of the seven Z doors is exactly this — a definition with
            // time='10' and room placements that say nothing.
            Assert.AreEqual(10, MakeObject(null, "10").GetHoldFrames());
        }

        [Test]
        public void PlacementOverridesTheDefinition()
        {
            // AS3 reads the placement last, so it wins.
            Assert.AreEqual(25, MakeObject("25", "10").GetHoldFrames());
        }

        [Test]
        public void ExplicitZeroOnThePlacementBeatsTheDefinition()
        {
            // The `.length()` rule: `time='0'` is present, so t_action becomes 0 and the action fires at
            // once. Treating 0 as "absent" would wrongly give this placement the definition's 10 frames.
            Assert.AreEqual(0, MakeObject("0", "10").GetHoldFrames());
        }

        [Test]
        public void NegativeIsClampedToZeroRatherThanInheriting()
        {
            // A negative can only be a typo for 0, and 0 is the safe reading — but it is still a present
            // value, so it must not fall through to the definition either.
            Assert.AreEqual(0, MakeObject("-4", "10").GetHoldFrames());
        }

        [Test]
        public void NeitherSourceAuthorsIt_ReturnsZero()
        {
            // The overwhelmingly common case: 19,439 of the 19,461 <obj> rows. This is what keeps every
            // plain door1 / door2 / hatch acting on the press.
            Assert.AreEqual(0, MakeObject(null, null).GetHoldFrames());
        }

        [Test]
        public void BlankAttributeIsTreatedAsAbsent()
        {
            Assert.AreEqual(10, MakeObject("   ", "10").GetHoldFrames());
        }

        [Test]
        public void UnparseablePlacementFallsThroughToTheDefinition()
        {
            // AS3 would assign the raw text and let it coerce; the port prefers the definition's real
            // number over a garbage placement, because a hold of an unknown length is worse than the
            // authored one.
            Assert.AreEqual(10, MakeObject("soon", "10").GetHoldFrames());
        }

        [Test]
        public void NoDefinitionAtAll_ReturnsZeroRatherThanThrowing()
        {
            var obj = new ObjectInstance { objectId = "door1" };

            Assert.AreEqual(0, obj.GetHoldFrames());
        }

        [Test]
        public void PlacementOnly_IsHonouredWithoutADefinition()
        {
            Assert.AreEqual(15, MakeObject("15", null).GetHoldFrames());
        }
    }
}
