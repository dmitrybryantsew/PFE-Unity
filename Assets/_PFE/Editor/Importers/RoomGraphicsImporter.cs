#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;

namespace PFE.Editor.Importers
{
    public static class RoomGraphicsImporter
    {
        private static readonly Dictionary<string, string> MaskImportAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "maskBare", "TileMaskBare" },
            { "maskDamaged", "TileMaskDamaged" },
            { "maskSimple", "BorderMask" },
            { "maskStoneBorder", "BorderMask" },
            { "maskMetalBorder", "BorderMask" },
            { "maskDirtBorder", "BorderMask" },
            { "maskBorderBare", "BorderMask" },
            { "maskSkol", "SkolMask" },
            { "maskFloor", "FloorMask" }
        };

        private static readonly string[] DefaultExportRoots = SourceImportPaths.RoomGraphicsExportRoots;
        private static readonly string Texture1ImagesRoot = SourceImportPaths.TextureImagesRoot("texture1");
        private const string TileAssetDatabasePath = "Assets/_PFE/Data/Map/TileAssetDatabase.asset";
        private const string MaterialDatabasePath = "Assets/_PFE/Data/MaterialRenderDatabase.asset";
        private const string RoomBackgroundLookupPath = "Assets/_PFE/Data/RoomBackgroundLookup.asset";
        private const string TileMaskLookupPath = "Assets/_PFE/Data/TileMaskLookup.asset";
        private const string RoomTemplateRoot = "Assets/_PFE/Data/Resources/Rooms";
        private const string ImportedRoot = "Assets/_PFE/Art/Imported";

        [MenuItem("PFE/Art/Import Room Graphics")]
        public static void ImportFromMenu()
        {
            Import(DefaultExportRoots);
        }

        public static void Import(string exportRoot)
        {
            Import(new[] { exportRoot });
        }

        public static void Import(IEnumerable<string> exportRoots)
        {
            var roots = exportRoots?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (roots.Count == 0)
            {
                Debug.LogError("[RoomGraphicsImporter] No export roots supplied.");
                return;
            }

            var existingRoots = roots.Where(Directory.Exists).ToList();
            if (existingRoots.Count == 0)
            {
                Debug.LogError($"[RoomGraphicsImporter] None of the export roots exist: {string.Join(", ", roots)}");
                return;
            }

            var resolver = BuildResolver(existingRoots);
            var tileAssetDatabase = LoadOrCreateAsset<TileAssetDatabase>(TileAssetDatabasePath);
            var roomBackgroundLookup = LoadOrCreateAsset<RoomBackgroundLookup>(RoomBackgroundLookupPath);
            var tileMaskLookup = LoadOrCreateAsset<TileMaskLookup>(TileMaskLookupPath);
            var materialDatabase = AssetDatabase.LoadAssetAtPath<MaterialRenderDatabase>(MaterialDatabasePath);

            ImportTileFront(resolver, tileAssetDatabase);
            ImportTileVoda(resolver, roomBackgroundLookup);
            ImportMasks(resolver, tileMaskLookup, materialDatabase);
            ImportRoomBackgrounds(resolver, roomBackgroundLookup);

            EditorUtility.SetDirty(tileAssetDatabase);
            EditorUtility.SetDirty(roomBackgroundLookup);
            EditorUtility.SetDirty(tileMaskLookup);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (Directory.Exists(Texture1ImagesRoot))
            {
                Debug.Log($"[RoomGraphicsImporter] Note: object/background bitmap fallback images exist at {Texture1ImagesRoot}");
            }

            Debug.Log($"[RoomGraphicsImporter] Import complete. Background entries={roomBackgroundLookup.Count}, mask entries={tileMaskLookup.Count}, tile entries={tileAssetDatabase.GetCount()}");
        }

        private static void ImportTileFront(Dictionary<string, string> resolver, TileAssetDatabase tileAssetDatabase)
        {
            if (!resolver.TryGetValue("tileFront", out var folder))
            {
                Debug.LogWarning("[RoomGraphicsImporter] tileFront export not found.");
                return;
            }

            var sprites = ImportFolderFrames(folder, Path.Combine(ImportedRoot, "TileOverlays/tileFront"), FilterMode.Point);
            for (int i = 0; i < sprites.Count; i++)
            {
                tileAssetDatabase.AddTileSprite($"tile_{i + 1}", sprites[i], TilePhysicsType.Air, MaterialType.Default);
            }

            Debug.Log($"[RoomGraphicsImporter] Imported {sprites.Count} tileFront overlay frames.");
        }

        private static void ImportTileVoda(Dictionary<string, string> resolver, RoomBackgroundLookup roomBackgroundLookup)
        {
            if (!resolver.TryGetValue("tileVoda", out var folder))
            {
                Debug.LogWarning("[RoomGraphicsImporter] tileVoda export not found.");
                return;
            }

            var sprites = ImportFolderFrames(folder, Path.Combine(ImportedRoot, "Water/tileVoda"), FilterMode.Point);
            roomBackgroundLookup.SetEntry("tileVoda", sprites);
            Debug.Log($"[RoomGraphicsImporter] Imported {sprites.Count} tileVoda frames.");
        }

        private static void ImportMasks(Dictionary<string, string> resolver, TileMaskLookup tileMaskLookup, MaterialRenderDatabase materialDatabase)
        {
            var requiredMasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BorderMask",
                "FloorMask",
                "TileMask"
            };

            if (materialDatabase != null)
            {
                foreach (var maskName in materialDatabase.GetAllMaskNames())
                {
                    if (!string.IsNullOrWhiteSpace(maskName))
                        requiredMasks.Add(maskName);
                }
            }

            foreach (var maskName in requiredMasks.OrderBy(x => x))
            {
                string importName = ResolveMaskImportName(maskName, resolver);
                if (string.IsNullOrWhiteSpace(importName) || !resolver.TryGetValue(importName, out var folder))
                {
                    Debug.LogWarning($"[RoomGraphicsImporter] Mask export not found for {maskName}");
                    continue;
                }

                var sprites = ImportFolderFrames(folder, Path.Combine(ImportedRoot, $"TileMasks/{maskName}"), FilterMode.Point);
                tileMaskLookup.SetEntry(maskName, sprites);
            }
        }

        private static string ResolveMaskImportName(string maskName, Dictionary<string, string> resolver)
        {
            if (string.IsNullOrWhiteSpace(maskName))
            {
                return maskName;
            }

            if (MaskImportAliases.TryGetValue(maskName, out string alias) && resolver.ContainsKey(alias))
            {
                return alias;
            }

            return maskName;
        }

        private static void ImportRoomBackgrounds(Dictionary<string, string> resolver, RoomBackgroundLookup roomBackgroundLookup)
        {
            foreach (var id in CollectRoomBackgroundIds())
            {
                if (!resolver.TryGetValue(id, out var folder))
                    continue;

                var sprites = ImportFolderFrames(folder, Path.Combine(ImportedRoot, $"RoomBackgrounds/{id}"), FilterMode.Bilinear);
                if (sprites.Count > 0)
                {
                    roomBackgroundLookup.SetEntry(id, sprites);
                }
            }
        }

        private static HashSet<string> CollectRoomBackgroundIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guids = AssetDatabase.FindAssets("t:RoomTemplate", new[] { RoomTemplateRoot });
            for (int i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var template = AssetDatabase.LoadAssetAtPath<RoomTemplate>(path);
                if (template == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(template.backgroundRoomId))
                    ids.Add(template.backgroundRoomId);

                if (template.backgroundDecorations != null)
                {
                    for (int j = 0; j < template.backgroundDecorations.Count; j++)
                    {
                        var decoration = template.backgroundDecorations[j];
                        if (decoration != null && !string.IsNullOrWhiteSpace(decoration.id))
                            ids.Add(decoration.id);
                    }
                }
            }

            return ids;
        }

        private static Dictionary<string, string> BuildResolver(IEnumerable<string> exportRoots)
        {
            var resolver = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var exportRoot in exportRoots)
            {
                var directories = Directory.GetDirectories(exportRoot, "*", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < directories.Length; i++)
                {
                    var directory = directories[i];
                    var folderName = Path.GetFileName(directory);
                    foreach (var candidate in GetCandidateNames(folderName))
                    {
                        if (!resolver.ContainsKey(candidate))
                            resolver[candidate] = directory;
                    }
                }
            }

            return resolver;
        }

        private static IEnumerable<string> GetCandidateNames(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
                yield break;

            yield return folderName;

            if (!folderName.StartsWith("DefineSprite_", StringComparison.OrdinalIgnoreCase))
                yield break;

            int secondUnderscore = folderName.IndexOf('_', "DefineSprite_".Length);
            if (secondUnderscore < 0 || secondUnderscore >= folderName.Length - 1)
                yield break;

            string suffix = folderName.Substring(secondUnderscore + 1);
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                yield return suffix;
                yield return NormalizeCandidateName(suffix);
            }
        }

        private static string NormalizeCandidateName(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return candidate;

            string normalized = candidate;
            if (normalized.StartsWith("back_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("back_".Length);
            if (normalized.EndsWith("_t", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - 2);

            return normalized;
        }

        private static List<Sprite> ImportFolderFrames(string sourceFolder, string relativeTargetFolder, FilterMode filterMode)
        {
            string projectAbsoluteTargetFolder = Path.Combine(Directory.GetCurrentDirectory(), relativeTargetFolder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(projectAbsoluteTargetFolder);

            var sourceFiles = Directory.GetFiles(sourceFolder, "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(ParseFrameOrder)
                .ThenBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var assetPaths = new List<string>(sourceFiles.Count);
            for (int i = 0; i < sourceFiles.Count; i++)
            {
                string sourceFile = sourceFiles[i];
                string fileName = Path.GetFileName(sourceFile);
                string destAbsolute = Path.Combine(projectAbsoluteTargetFolder, fileName);
                File.Copy(sourceFile, destAbsolute, true);
                assetPaths.Add($"{relativeTargetFolder.Replace('\\', '/')}/{fileName}");
            }

            AssetDatabase.Refresh();

            var sprites = new List<Sprite>(assetPaths.Count);
            for (int i = 0; i < assetPaths.Count; i++)
            {
                ConfigureSpriteImport(assetPaths[i], filterMode);
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPaths[i]);
                if (sprite != null)
                    sprites.Add(sprite);
            }

            return sprites;
        }

        private static int ParseFrameOrder(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return int.TryParse(name, out int value) ? value : int.MaxValue;
        }

        private static void ConfigureSpriteImport(string assetPath, FilterMode filterMode)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return;

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.filterMode = filterMode;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        private static T LoadOrCreateAsset<T>(string assetPath) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset != null)
                return asset;

            string directory = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, assetPath);
            return asset;
        }
    }
}
#endif
