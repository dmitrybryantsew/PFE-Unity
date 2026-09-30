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
        public void Definition_BothCachesStale_FallsBackToTheDefinitionsOwnTip()
        {
            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "training";

            // This is byte-for-byte what the shipped training.asset holds: BOTH caches are stale,
            // because the importer that wrote them had no tip='unit' branch. Only legacyTip is right,
            // and legacyTip is the one field that is a verbatim copy of the oracle's own input —
            // Location.as:657 reads `xmll = AllData.d.obj.(@id == obj.@id)[0]` and :1012 branches on
            // `_loc1_.tip`, so `tip` is what AS3 decides from. Deriving from it last is what makes the
            // runtime independent of whether the definitions have been regenerated.
            definition.family = MapObjectFamily.GenericObject;
            definition.defaultPlacementType = MapObjectDefinition.GenericPlacementType;
            definition.legacyTip = "unit";

            Assert.AreEqual("unit", definition.GetResolvedPlacementType(),
                "a definition whose caches are both generic must fall back to its tip");

            Object.DestroyImmediate(definition);
        }

        [Test]
        public void Definition_GenericCachesAndNoUnitTip_StaysAGenericProp()
        {
            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "vkonstr";
            definition.family = MapObjectFamily.GenericObject;
            definition.defaultPlacementType = MapObjectDefinition.GenericPlacementType;
            definition.legacyTip = "box";

            // The complement to the test above. Without it, a fallback that returned "unit" for any
            // non-generic tip would pass there while turning 134 non-unit objects — 96 'box', 21
            // 'door', 6 'checkpoint', … — into enemies.
            Assert.AreEqual(MapObjectDefinition.GenericPlacementType, definition.GetResolvedPlacementType());

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
        /// No shipped definition that declares <c>legacyTip: unit</c> may resolve to a placement bucket
        /// other than <c>"unit"</c>.
        ///
        /// <para><b>Why this is a data test and not a code test.</b> The tests above prove the
        /// classifier and the resolver are right <i>in memory</i>, and they all passed while the game
        /// was broken, because the defect was that the shipped assets had been generated by a
        /// classifier that lacked the <c>tip='unit'</c> branch. That is the recurring shape in this
        /// project: correct code, stale data, and nothing goes red. Measured on 2026-09-30, all
        /// <b>68</b> unit definitions were in exactly that state — <c>family: 1</c> (GenericObject) with
        /// the generic <c>obj</c> bucket — and the camp's five training dummies instantiated as
        /// <i>static props with no visual</i>.</para>
        ///
        /// <para><b>What it asserts, and why it is the resolved bucket rather than the raw field.</b>
        /// This originally asserted <c>family == MapObjectFamily.Unit</c>, which made it fail until
        /// <c>PFE/Data/Import Map Object Definitions</c> was re-run. That was right while
        /// <c>family</c> was the decision. It no longer is: <c>GetResolvedPlacementType()</c> now
        /// derives the bucket from <c>legacyTip</c> — the definition's <c>tip</c>, which is what
        /// <c>Location.as:1012</c> actually branches on — so a stale <c>family</c> is a stale cache and
        /// not a defect. Asserting the cache would fail on a build whose behaviour is correct, which is
        /// how a suite trains people to ignore it. The re-import is still worth running for consistency
        /// (<c>family</c> also feeds <c>isSaveRelevant</c>), but it is no longer load-bearing.</para>
        ///
        /// <para>The check is still a real guard: it is red for all 68 rows before the resolver fix, and
        /// the paired control below keeps it from passing vacuously if <c>tip</c> ever stops being
        /// written.</para>
        /// </summary>
        [Test]
        public void ShippedDefinitions_UnitTip_ResolvesToTheUnitBucket()
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

                string resolved = definition.GetResolvedPlacementType();
                if (!string.Equals(resolved, "unit", System.StringComparison.Ordinal))
                {
                    stale.Add($"{definition.objectId} (resolved={resolved}, family={definition.family})");
                }
            }

            // Paired control: if the tip field itself stopped being written, the loop above would find
            // nothing and the test would pass for the wrong reason.
            Assert.Greater(unitTipCount, 0,
                "no definition declares legacyTip='unit'; the tip attribute is no longer being " +
                "imported, which would make the check below vacuous");

            Assert.IsEmpty(stale,
                $"{stale.Count} definition(s) declare legacyTip='unit' but do not resolve to the " +
                $"unit bucket, so they spawn as static props with no visual. " +
                $"Stale: {string.Join(", ", stale)}");
        }
    }
}
