#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PFE.Systems.Map.Rendering;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports tile texture images from extracted texture.swf into Unity.
    /// Copies files, configures import settings, and builds a lookup 
    /// that maps texture names (tConcrete, tMetal, etc.) to Texture2D assets.
    /// 
    /// Source: texture.swf/images/ (files named like "26_tConcrete.jpg")
    /// Output: Assets/_PFE/Art/TileTextures/
    /// 
    /// Menu: PFE > Art > Import Tile Textures
    /// </summary>
    public static class TileTextureImporter
    {
        private static readonly string DefaultSourceFolder =
            SourceImportPaths.TextureImagesRoot("texture");
        private static readonly string OutputFolder =
            "Assets/_PFE/Art/TileTextures";
        private static readonly string LookupAssetPath =
            "Assets/_PFE/Data/TileTextureLookup.asset";

        [MenuItem("PFE/Art/Import Tile Textures")]
        public static void ImportFromMenu()
        {
            Import(DefaultSourceFolder);
        }

        public static void Import(string sourceFolder)
        {
            if (!Directory.Exists(sourceFolder))
            {
                Debug.LogError($"[TileTextureImporter] Source folder not found: {sourceFolder}");
                return;
            }

            // Ensure output directory
            if (!Directory.Exists(OutputFolder))
            {
                Directory.CreateDirectory(OutputFolder);
            }

            // Get all image files
            var files = new List<string>();
            files.AddRange(Directory.GetFiles(sourceFolder, "*.jpg"));
            files.AddRange(Directory.GetFiles(sourceFolder, "*.png"));

            Debug.Log($"[TileTextureImporter] Found {files.Count} image files in {sourceFolder}");

            int imported = 0;
            int skipped = 0;
            var nameToPath = new Dictionary<string, string>();

            foreach (string sourceFile in files)
            {
                string fileName = Path.GetFileName(sourceFile);

                // Parse texture name from filename pattern: "26_tConcrete.jpg" -> "tConcrete"
                string textureName = ExtractTextureName(fileName);

                if (string.IsNullOrEmpty(textureName))
                {
                    // Files without texture name (like "81.jpg") are background images, not tiles
                    skipped++;
                    continue;
                }

                // Copy to Unity project
                string ext = Path.GetExtension(fileName);
                string destFileName = $"{textureName}{ext}";
                string destPath = Path.Combine(OutputFolder, destFileName);

                // Copy file
                if (!File.Exists(destPath))
                {
                    File.Copy(sourceFile, destPath, true);
                    imported++;
                }
                else
                {
                    skipped++;
                }

                nameToPath[textureName] = $"{OutputFolder}/{destFileName}";
            }

            AssetDatabase.Refresh();

            // Configure import settings for all imported textures
            ConfigureTextureImportSettings(nameToPath);

            // Create lookup asset
            CreateLookupAsset(nameToPath);

            Debug.Log($"[TileTextureImporter] Done: {imported} imported, {skipped} skipped, {nameToPath.Count} textures mapped");
        }

        /// <summary>
        /// Extract texture name from filename.
        /// "26_tConcrete.jpg" -> "tConcrete"
        /// "8_tWood.jpg" -> "tWood"
        /// "81.jpg" -> null (no texture name, it's a background)
        /// </summary>
        private static string ExtractTextureName(string fileName)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

            // Pattern: digits_textureName
            var match = Regex.Match(nameWithoutExt, @"^\d+_(t\w+)$");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return null;
        }

        /// <summary>
        /// Configure texture import settings for tiling textures.
        /// </summary>
        private static void ConfigureTextureImportSettings(Dictionary<string, string> nameToPath)
        {
            foreach (var kvp in nameToPath)
            {
                TextureImporter importer = AssetImporter.GetAtPath(kvp.Value) as TextureImporter;
                if (importer == null) continue;

                // Tile textures should tile seamlessly
                importer.textureType = TextureImporterType.Default;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
                importer.mipmapEnabled = true;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.isReadable = true; // Required for TileCompositor pixel sampling

                // Don't compress too aggressively — these are visible tiling textures
                importer.textureCompression = TextureImporterCompression.CompressedHQ;

                // Keep reasonable resolution
                importer.maxTextureSize = 512;

                importer.SaveAndReimport();
            }

            Debug.Log($"[TileTextureImporter] Configured import settings for {nameToPath.Count} textures");
        }

        /// <summary>
        /// Create a ScriptableObject that maps texture names to Texture2D assets.
        /// </summary>
        private static void CreateLookupAsset(Dictionary<string, string> nameToPath)
        {
            var lookup = AssetDatabase.LoadAssetAtPath<TileTextureLookup>(LookupAssetPath);
            if (lookup == null)
            {
                lookup = ScriptableObject.CreateInstance<TileTextureLookup>();

                string dir = Path.GetDirectoryName(LookupAssetPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                AssetDatabase.CreateAsset(lookup, LookupAssetPath);
            }

            lookup.Clear();

            foreach (var kvp in nameToPath)
            {
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(kvp.Value);
                if (tex != null)
                {
                    lookup.AddEntry(kvp.Key, tex);
                }
                else
                {
                    Debug.LogWarning($"[TileTextureImporter] Could not load texture at {kvp.Value}");
                }
            }

            EditorUtility.SetDirty(lookup);
            AssetDatabase.SaveAssets();

            Debug.Log($"[TileTextureImporter] Created lookup with {lookup.Count} entries");
            Selection.activeObject = lookup;
        }
    }
}
#endif
