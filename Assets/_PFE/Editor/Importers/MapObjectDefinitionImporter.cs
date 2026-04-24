#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.DataMigration;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports shared map object definitions from the original AllData.as <obj> block.
    /// Emits one MapObjectDefinition asset per original object id plus a catalog asset.
    /// </summary>
    public static class MapObjectDefinitionImporter
    {
        static readonly string DefaultSourcePath = SourceImportPaths.AllDataAsPath;

        static readonly string OutputDirectory =
            "Assets/_PFE/Data/Resources/MapObjects/Definitions";

        static readonly string CatalogAssetPath =
            "Assets/_PFE/Data/MapObjects/Catalog/MapObjectCatalog.asset";

        [MenuItem("PFE/Data/Import Map Object Definitions")]
        public static void ImportFromMenu()
        {
            Import(DefaultSourcePath, OutputDirectory, CatalogAssetPath);
        }

        public static void Import(string sourcePath, string outputDirectory, string catalogAssetPath)
        {
            if (!File.Exists(sourcePath))
            {
                Debug.LogError($"[MapObjectDefinitionImporter] Source file not found: {sourcePath}");
                return;
            }

            EnsureDirectoryExists(outputDirectory);
            EnsureDirectoryExists(Path.GetDirectoryName(catalogAssetPath));

            AllDataParser parser = new AllDataParser();
            parser.LoadFile(sourcePath);
            if (!parser.IsLoaded)
            {
                Debug.LogError("[MapObjectDefinitionImporter] Failed to parse AllData.as");
                return;
            }

            List<ParsedEntry> objectEntries = parser.GetAllEntries("obj")
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.id))
                .OrderBy(entry => entry.id, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<MapObjectDefinition> importedDefinitions = new List<MapObjectDefinition>(objectEntries.Count);
            int createdCount = 0;
            int updatedCount = 0;

            for (int i = 0; i < objectEntries.Count; i++)
            {
                ParsedEntry entry = objectEntries[i];
                string assetPath = $"{outputDirectory}/{SanitizeFileName(entry.id)}.asset";
                MapObjectDefinition definition = AssetDatabase.LoadAssetAtPath<MapObjectDefinition>(assetPath);
                bool isNew = definition == null;

                if (isNew)
                {
                    definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
                }

                ApplyEntry(definition, entry);

                if (isNew)
                {
                    AssetDatabase.CreateAsset(definition, assetPath);
                    createdCount++;
                }
                else
                {
                    EditorUtility.SetDirty(definition);
                    updatedCount++;
                }

                importedDefinitions.Add(definition);
            }

            MapObjectCatalog catalog = AssetDatabase.LoadAssetAtPath<MapObjectCatalog>(catalogAssetPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<MapObjectCatalog>();
                AssetDatabase.CreateAsset(catalog, catalogAssetPath);
            }

            catalog.sourceFilePath = sourcePath.Replace('\\', '/');
            catalog.SetDefinitions(importedDefinitions);
            EditorUtility.SetDirty(catalog);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"[MapObjectDefinitionImporter] Imported {importedDefinitions.Count} definitions " +
                $"({createdCount} created, {updatedCount} updated)");

            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }

        static void ApplyEntry(MapObjectDefinition definition, ParsedEntry entry)
        {
            MapObjectFamily family = MapObjectDefinitionClassifier.ResolveFamily(
                entry.id,
                entry.attributes,
                out string placementType,
                out string normalizedTip);

            definition.objectId = entry.id;
            definition.displayName = ResolveDisplayName(entry);
            definition.legacyTip = entry.GetAttr("tip", "");
            definition.family = family;
            definition.defaultPlacementType = placementType;

            definition.size = Mathf.Max(1, entry.GetIntAttr("size", entry.GetIntAttr("sz", 1)));
            definition.width = Mathf.Max(1, entry.GetIntAttr("wid", entry.GetIntAttr("w", 1)));
            definition.wallMounted = entry.GetIntAttr("stena", 0) > 0 || entry.GetIntAttr("wall", 0) > 0;
            definition.blocksMovement = entry.GetIntAttr("phis", 0) > 0 ||
                definition.family == MapObjectFamily.Door ||
                definition.family == MapObjectFamily.Platform;
            definition.blocksVisibility = entry.GetIntAttr("neprozr", 0) > 0 ||
                entry.GetIntAttr("opaque", 0) > 0;

            definition.containerId = entry.GetAttr("cont", "");
            definition.interactionMode = entry.GetAttr("inter", "");
            definition.allAct = entry.GetAttr("allact", "");
            definition.allId = entry.GetAttr("allid", "");
            definition.autoClose = entry.GetIntAttr("autoclose", 0) > 0 || entry.HasAttr("autoclose");
            definition.isInteractive =
                definition.family == MapObjectFamily.Container ||
                definition.family == MapObjectFamily.Device ||
                definition.family == MapObjectFamily.Door ||
                definition.family == MapObjectFamily.Checkpoint ||
                definition.family == MapObjectFamily.AreaTrigger ||
                definition.family == MapObjectFamily.Transition ||
                !string.IsNullOrWhiteSpace(definition.containerId) ||
                !string.IsNullOrWhiteSpace(definition.allAct) ||
                !string.IsNullOrWhiteSpace(definition.interactionMode);
            definition.isSaveRelevant =
                definition.family != MapObjectFamily.Unknown &&
                definition.family != MapObjectFamily.GenericObject &&
                definition.family != MapObjectFamily.Furniture &&
                definition.family != MapObjectFamily.SpawnMarker;

            definition.hitPoints = entry.GetFloatAttr("hp", 0f);
            definition.threshold = entry.GetFloatAttr("thre", entry.GetFloatAttr("porog", entry.GetFloatAttr("tr", 0f)));
            definition.shield = entry.GetFloatAttr("shield", 0f);
            definition.materialId = entry.GetIntAttr("mat", 0);
            definition.mass = entry.GetFloatAttr("massa", 0f);
            definition.massMultiplier = entry.GetFloatAttr("massaMult", 1f);
            definition.buoyancyFactor = entry.GetFloatAttr("plav", 0f);
            definition.physicalCapability = MapObjectDefinitionClassifier.ResolvePhysicalCapability(
                entry.id,
                family,
                definition.legacyTip,
                key => entry.GetAttr(key, ""));
            definition.defaultVisualId = entry.GetAttr("vis", entry.GetAttr("blit", ""));
            definition.legacyAttributes = MapObjectDataUtility.BuildAttributes(entry.attributes, "id");
        }

        static string ResolveDisplayName(ParsedEntry entry)
        {
            string attributeName = entry.GetAttr("n", "");
            if (!string.IsNullOrWhiteSpace(attributeName))
            {
                return attributeName.Trim();
            }

            ParsedEntry nameChild = entry.GetChild("n");
            if (nameChild != null && !string.IsNullOrWhiteSpace(nameChild.innerText))
            {
                return nameChild.innerText.Trim();
            }

            return entry.id;
        }

        static void EnsureDirectoryExists(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return;
            }

            if (Directory.Exists(directoryPath))
            {
                return;
            }

            Directory.CreateDirectory(directoryPath);
            AssetDatabase.Refresh();
        }

        static string SanitizeFileName(string rawId)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = rawId;

            for (int i = 0; i < invalidChars.Length; i++)
            {
                sanitized = sanitized.Replace(invalidChars[i], '_');
            }

            return sanitized;
        }
    }
}
#endif
