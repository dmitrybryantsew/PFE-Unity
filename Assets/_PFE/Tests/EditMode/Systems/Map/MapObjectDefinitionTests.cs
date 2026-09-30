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
        public void Classifier_UnitTip_ResolvesUnitPlacement()
        {
            var attributes = new System.Collections.Generic.Dictionary<string, string>
            {
                ["tip"] = "unit"
            };

            MapObjectFamily family = MapObjectDefinitionClassifier.ResolveFamily(
                "training",
                attributes,
                out string placementType,
                out string normalizedTip);

            // AllData.as:5048 — <obj ed='13' ico='pon' tip='unit' id='training' cl='UnitTrain' .../>
            Assert.AreEqual(MapObjectFamily.Unit, family);
            Assert.AreEqual("unit", placementType);
            Assert.AreEqual("unit", normalizedTip);
        }

        [Test]
        public void Classifier_NonUnitTip_DoesNotResolveAsUnit()
        {
            var attributes = new System.Collections.Generic.Dictionary<string, string>
            {
                ["tip"] = "box"
            };

            MapObjectFamily family = MapObjectDefinitionClassifier.ResolveFamily(
                "woodbox",
                attributes,
                out string placementType,
                out _);

            // The complement to Classifier_UnitTip_ResolvesUnitPlacement: without it, a branch that
            // returned Unit for *every* tip would still pass that test. 134 of the 202 AllData <obj>
            // rows are not units (86 'box', 21 'door', 8 '2', 6 'checkpoint', ...) and must not
            // become units.
            Assert.AreNotEqual(MapObjectFamily.Unit, family);
            Assert.AreNotEqual("unit", placementType);
        }

        [Test]
        public void Definition_GenericStoredBucket_LetsTheTypedFamilySpeak()
        {
            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "training";
            definition.family = MapObjectFamily.Unit;

            // Exactly what training.asset holds today: the stale import value, because the classifier
            // had no tip='unit' branch when it was written. The generic bucket carries no information,
            // so it must not mask a family that does.
            definition.defaultPlacementType = MapObjectDefinition.GenericPlacementType;

            Assert.AreEqual("unit", definition.GetResolvedPlacementType());

            Object.DestroyImmediate(definition);
        }

        [Test]
        public void Definition_SpecificStoredBucket_IsNotOverriddenByFamily()
        {
            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "mapped_enemy";
            definition.family = MapObjectFamily.Container;
            definition.defaultPlacementType = "unit";

            // Pins the precedence the other way round: a specific stored bucket is an explicit
            // classification and must survive. DefaultAS3ObjectMapping hand-maps objectIds onto
            // buckets (it is what rescues 'tarakan', whose definition is still family GenericObject
            // with the generic bucket), so a rule that always returned the family would silently
            // turn every mapping-rescued enemy back into a prop.
            Assert.AreEqual("unit", definition.GetResolvedPlacementType());

            Object.DestroyImmediate(definition);
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

        /// <summary>
        /// No shipped definition may carry <c>legacyTip: unit</c> while its <see cref="MapObjectFamily"/>
        /// is something else.
        ///
        /// <para><b>Why this is a data test and not a code test.</b> The tests above prove the
        /// classifier is right <i>in memory</i>, and they all pass while the game is still broken,
        /// because the defect is that the assets were never regenerated after the classifier gained its
        /// <c>tip='unit'</c> branch. That is the recurring shape in this project: correct code, stale
        /// data, and nothing goes red. Measured on 2026-09-30, all <b>68</b> unit definitions were in
        /// exactly that state — <c>family: 1</c> (GenericObject) with the generic <c>obj</c> bucket — so
        /// <c>GetResolvedPlacementType()</c> returned <c>"obj"</c>, <c>RoomPopulator</c> took its
        /// <c>default:</c> branch, and every authored enemy, NPC and turret in the game was instantiated
        /// as a <i>static prop with no visual</i>. The camp's five training dummies were simply the
        /// first ones anyone stood next to.</para>
        ///
        /// <para><b>This test is expected to fail until the definitions are re-imported.</b> Run
        /// <c>PFE/Data/Import Map Object Definitions</c>; it load-or-creates and rewrites every existing
        /// asset in place, so one run fixes all of them. A green build proves nothing here — that is the
        /// whole reason the check exists.</para>
        /// </summary>
        [Test]
        public void ShippedDefinitions_UnitTip_IsStoredAsFamilyUnit()
        {
            MapObjectDefinition[] definitions =
                Resources.LoadAll<MapObjectDefinition>("MapObjects/Definitions");

            Assert.Greater(definitions.Length, 0,
                "no MapObjectDefinition assets loaded from Resources/MapObjects/Definitions — " +
                "this test would pass vacuously, so it fails instead");

            var stale = new System.Collections.Generic.List<string>();
            int unitTipCount = 0;

            foreach (MapObjectDefinition definition in definitions)
            {
                if (definition == null || definition.legacyTip != "unit")
                {
                    continue;
                }

                unitTipCount++;

                if (definition.family != MapObjectFamily.Unit)
                {
                    stale.Add($"{definition.objectId} (family={definition.family})");
                }
            }

            // Paired control: if the tip field itself stopped being written, the loop above would find
            // nothing and the test would pass for the wrong reason.
            Assert.Greater(unitTipCount, 0,
                "no definition declares legacyTip='unit'; the tip attribute is no longer being " +
                "imported, which would make the stale check below vacuous");

            Assert.IsEmpty(stale,
                $"{stale.Count} definition(s) declare legacyTip='unit' but are not stored as " +
                $"MapObjectFamily.Unit, so they spawn as static props with no visual. " +
                $"Run PFE/Data/Import Map Object Definitions. Stale: {string.Join(", ", stale)}");
        }
    }
}
