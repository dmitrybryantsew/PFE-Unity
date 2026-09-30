using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Tests for the two questions a room object has to answer before the port can act on it:
    /// <see cref="ObjectInstance.GetAllAct"/> ("does it carry a script?") and
    /// <see cref="ObjectInstance.IsDoorBox"/> ("does it have a solid closed state?").
    ///
    /// <para>They are separate questions and the Z doors sit on opposite sides of each. That is the
    /// whole point of this fixture: a single "is this a door?" flag was enough to route the camp's
    /// backroom door to a presenter that then could not dispatch its script, and to a tile stamp that
    /// would have sealed the doorway.</para>
    /// </summary>
    [TestFixture]
    public class AllActAndDoorBoxTests
    {
        private static ObjectInstance MakeObject(string placementAllAct, string definitionAllAct, string definitionDoor)
        {
            var obj = new ObjectInstance { objectId = "indoor2", definitionId = "indoor2" };

            if (placementAllAct != null)
            {
                obj.attributes = new List<MapObjectAttributeData>
                {
                    new MapObjectAttributeData { key = "allact", value = placementAllAct }
                };
            }

            if (definitionAllAct != null || definitionDoor != null)
            {
                var legacy = new List<MapObjectAttributeData>();
                if (definitionAllAct != null)
                {
                    legacy.Add(new MapObjectAttributeData { key = "allact", value = definitionAllAct });
                }

                if (definitionDoor != null)
                {
                    legacy.Add(new MapObjectAttributeData { key = "door", value = definitionDoor });
                }

                obj.definition = new MapObjectDefinition { objectId = "indoor2", legacyAttributes = legacy };
            }

            return obj;
        }

        [Test]
        public void DefinitionSuppliesTheScriptWhenThePlacementDoesNot()
        {
            // The shipped camp case: RoomsCamp.as:115 places indoor2 with no allact at all, and
            // AllData.as:4917 authors allact='comein' on the definition.
            Assert.AreEqual("comein", MakeObject(null, "comein", null).GetAllAct());
        }

        [Test]
        public void PlacementOverridesTheDefinition()
        {
            // AS3 assigns the definition first (Interact.as:287-289) and the placement last (:383-385).
            Assert.AreEqual("open", MakeObject("open", "comein", null).GetAllAct());
        }

        [Test]
        public void NeitherSourceGivesAnEmptyScript()
        {
            Assert.AreEqual(string.Empty, MakeObject(null, null, null).GetAllAct());
        }

        [Test]
        public void ABlankPlacementFallsThroughToTheDefinition()
        {
            // AS3's guard is `.length()`, so whitespace-only is not a value. Answering empty here would
            // lose the definition's script for a placement that merely has a stray attribute.
            Assert.AreEqual("comein", MakeObject("   ", "comein", null).GetAllAct());
        }

        [Test]
        public void AZDoorIsNotADoorBox()
        {
            // indoor1..4, instdoor, inbasedoor and inencldoor declare allact='comein' and inter='8' but
            // no door= (AllData.as:4916-4922), so AS3 never runs initDoor on them (Box.as:290-297).
            Assert.IsFalse(MakeObject(null, "comein", null).IsDoorBox());
        }

        [Test]
        public void ADoorBoxIsADoorBox()
        {
            // door1 authors door='1' (AllData.as); the definition asset carries it as legacyAttributes.
            Assert.IsTrue(MakeObject(null, null, "1").IsDoorBox());
        }

        [Test]
        public void ADoorBoxCanStillCarryAScript()
        {
            // door_st1/door_st2 are both: door='1' *and* allact='comein'. The two predicates are
            // independent, so neither may be implemented in terms of the other.
            var obj = MakeObject(null, "comein", "1");
            Assert.IsTrue(obj.IsDoorBox());
            Assert.AreEqual("comein", obj.GetAllAct());
        }

        [Test]
        public void AMissingDefinitionKeepsTheHistoricalAnswer()
        {
            // No definition row means no door= to read. The historical answer is returned rather than
            // the strict one, so a broken reference cannot silently un-stamp a real door — the same
            // "keep the incumbent" choice LandMap.AddRoom makes for a coordinate clash.
            var obj = new ObjectInstance { objectId = "door1", definitionId = "door1" };
            Assert.IsTrue(obj.IsDoorBox());
        }
    }
}
