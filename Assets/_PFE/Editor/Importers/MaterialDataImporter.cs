#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using PFE.Systems.Map.DataMigration;
using PFE.Systems.Map.Rendering;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports material rendering data (textures, masks, filters) from AllData.as.
    /// Creates a MaterialRenderDatabase ScriptableObject used by the tile renderer.
    /// 
    /// Only imports <mat> entries that have visual data (<main>, <border>, <floor> children).
    /// Entries with @vid > 0 are tile overlays (stairs/shelves), not materials — those are
    /// already handled by TileFormDatabase.
    /// </summary>
    public static class MaterialDataImporter
    {
        private static readonly string DefaultSourcePath = SourceImportPaths.AllDataAsPath;
        private static readonly string OutputPath =
            "Assets/_PFE/Data/MaterialRenderDatabase.asset";

        [MenuItem("PFE/Data/Import Material Render Data")]
        public static void ImportFromMenu()
        {
            Import(DefaultSourcePath, OutputPath);
        }

        public static void Import(string sourcePath, string outputAssetPath)
        {
            if (!File.Exists(sourcePath))
            {
                Debug.LogError($"[MaterialDataImporter] Source file not found: {sourcePath}");
                return;
            }

            // Parse AllData.as
            var parser = new AllDataParser();
            parser.LoadFile(sourcePath);

            if (!parser.IsLoaded)
            {
                Debug.LogError("[MaterialDataImporter] Failed to parse AllData.as");
                return;
            }

            // Get all mat entries
            var matEntries = parser.GetAllEntries("mat");
            Debug.Log($"[MaterialDataImporter] Found {matEntries.Count} <mat> entries");

            // Ensure output directory
            string dir = Path.GetDirectoryName(outputAssetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            // Create or load database
            var database = AssetDatabase.LoadAssetAtPath<MaterialRenderDatabase>(outputAssetPath);
            if (database == null)
            {
                database = ScriptableObject.CreateInstance<MaterialRenderDatabase>();
                AssetDatabase.CreateAsset(database, outputAssetPath);
            }

            database.Clear();

            int frontCount = 0;
            int backCount = 0;
            int skippedVid = 0;

            foreach (var mat in matEntries)
            {
                // Skip entries with vid > 0 — those are tile overlays (stairs, shelves),
                // not texture materials. They're handled by TileFormDatabase.
                if (mat.GetIntAttr("vid") > 0)
                {
                    skippedVid++;
                    continue;
                }

                // Must have a <main> child to be a renderable material
                var mainChild = mat.GetChild("main");
                if (mainChild == null)
                {
                    // Some mat entries are pure physics definitions without rendering
                    continue;
                }

                int ed = mat.GetIntAttr("ed", 0);
                bool isRear = ed == 2 || mat.GetIntAttr("rear") > 0;

                var entry = new MaterialRenderEntry
                {
                    id = mat.id,
                    displayName = mat.GetAttr("n", mat.id),
                    ed = ed,
                    isRear = isRear,

                    // Main layer
                    mainTexture = mainChild.GetAttr("tex"),
                    mainMask = mainChild.GetAttr("mask", "TileMask"),
                    altTexture = mainChild.GetAttr("alt"),
                };

                // Border layer
                var borderChild = mat.GetChild("border");
                if (borderChild != null)
                {
                    entry.borderTexture = borderChild.GetAttr("tex");
                    entry.borderMask = borderChild.GetAttr("mask");
                }

                // Floor layer
                var floorChild = mat.GetChild("floor");
                if (floorChild != null)
                {
                    entry.floorTexture = floorChild.GetAttr("tex");
                    entry.floorMask = floorChild.GetAttr("mask");
                }

                // Filter
                var filterChild = mat.GetChild("filter");
                if (filterChild != null)
                {
                    entry.filterType = filterChild.GetAttr("f");
                }

                // Categorize
                if (ed == 2)
                {
                    database.AddBackMaterial(entry);
                    backCount++;
                }
                else
                {
                    database.AddFrontMaterial(entry);
                    frontCount++;
                }
            }

            EditorUtility.SetDirty(database);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Log results
            Debug.Log($"[MaterialDataImporter] Imported: {frontCount} front + {backCount} back materials, {skippedVid} vid-overlays skipped");

            // Log texture/mask inventory
            var allTextures = database.GetAllTextureNames();
            var allMasks = database.GetAllMaskNames();
            Debug.Log($"[MaterialDataImporter] Unique textures referenced: {allTextures.Count}");
            Debug.Log($"[MaterialDataImporter] Textures: {string.Join(", ", allTextures)}");
            Debug.Log($"[MaterialDataImporter] Unique masks referenced: {allMasks.Count}");
            Debug.Log($"[MaterialDataImporter] Masks: {string.Join(", ", allMasks)}");

            // Select in editor
            Selection.activeObject = database;
            EditorGUIUtility.PingObject(database);
        }
    }
}
#endif
