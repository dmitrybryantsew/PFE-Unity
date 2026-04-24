using NUnit.Framework;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Map
{
    [TestFixture]
    public class MapObjectDefinitionTests
    {
        [Test]
        public void Catalog_SetDefinitions_BuildsLookup()
        {
            MapObjectDefinition safe = ScriptableObject.CreateInstance<MapObjectDefinition>();
            safe.objectId = "safe";

            MapObjectDefinition term = ScriptableObject.CreateInstance<MapObjectDefinition>();
            term.objectId = "term1";

            MapObjectCatalog catalog = ScriptableObject.CreateInstance<MapObjectCatalog>();
            catalog.SetDefinitions(new[] { term, safe });

            Assert.AreEqual(2, catalog.Count);
            Assert.AreSame(safe, catalog.GetDefinition("safe"));
            Assert.AreSame(term, catalog.GetDefinition("term1"));
        }

        [Test]
        public void Classifier_DoorTip_ResolvesDoorPlacement()
        {
            var attributes = new System.Collections.Generic.Dictionary<string, string>
            {
                ["tip"] = "door"
            };

            MapObjectFamily family = MapObjectDefinitionClassifier.ResolveFamily(
                "custom_door",
                attributes,
                out string placementType,
                out string normalizedTip);

            Assert.AreEqual(MapObjectFamily.Door, family);
            Assert.AreEqual("door", placementType);
            Assert.AreEqual("door", normalizedTip);
        }

        [Test]
        public void Definition_GetResolvedVisualId_PrefersAssignedVisualAsset()
        {
            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "safe";
            definition.defaultVisualId = "fallback_visual";

            MapObjectVisualDefinition visual = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visual.visualId = "safe_visual";
            definition.visual = visual;

            Assert.AreEqual("safe_visual", definition.GetResolvedVisualId());
        }

        [Test]
        public void PhysicalCapability_BoxWithoutWall_ResolvesDynamicTelekinetic()
        {
            var attributes = new System.Collections.Generic.Dictionary<string, string>
            {
                ["tip"] = "box",
                ["wall"] = "0",
                ["size"] = "1",
                ["wid"] = "1"
            };

            MapObjectPhysicalCapability capability = MapObjectDefinitionClassifier.ResolvePhysicalCapability(
                "woodbox",
                MapObjectFamily.Container,
                "box",
                key => attributes.TryGetValue(key, out string value) ? value : string.Empty);

            Assert.AreEqual(MapObjectPhysicalCapability.DynamicTelekinetic, capability);
        }

        [Test]
        public void PhysicalCapability_HeavyBox_ResolvesDynamicThrowable()
        {
            var attributes = new System.Collections.Generic.Dictionary<string, string>
            {
                ["tip"] = "box",
                ["wall"] = "0",
                ["massa"] = "1000",
                ["size"] = "2",
                ["wid"] = "2"
            };

            MapObjectPhysicalCapability capability = MapObjectDefinitionClassifier.ResolvePhysicalCapability(
                "mcrate4",
                MapObjectFamily.Container,
                "box",
                key => attributes.TryGetValue(key, out string value) ? value : string.Empty);

            Assert.AreEqual(MapObjectPhysicalCapability.DynamicThrowable, capability);
        }
    }
}
