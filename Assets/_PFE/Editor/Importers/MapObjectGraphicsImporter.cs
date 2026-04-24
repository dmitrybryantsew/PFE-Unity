#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports exported object graphics and creates MapObjectVisualDefinition assets.
    /// Links imported visuals back to MapObjectDefinition assets through the shared catalog.
    /// </summary>
    public static class MapObjectGraphicsImporter
    {
        static readonly string[] DefaultExportRoots = SourceImportPaths.MapObjectExportRoots;

        static readonly string[] DefaultSymbolInventoryPaths = SourceImportPaths.MapObjectSymbolInventoryPaths;

        static readonly string[] DefaultWrapperScriptRoots = SourceImportPaths.MapObjectWrapperScriptRoots;

        const string CatalogSearchFilter = "t:MapObjectCatalog";
        const string ImportedArtRoot = "Assets/_PFE/Art/Imported/MapObjects";
        const string VisualAssetRoot = "Assets/_PFE/Data/Resources/MapObjects/Visuals";
        const string ReportAssetPath = "Assets/_PFE/Data/MapObjects/ImportReports/MapObjectGraphicsImportReport.txt";
        const int PixelsPerUnit = 100;

        [MenuItem("PFE/Art/Import Map Object Graphics")]
        public static void ImportFromMenu()
        {
            Import(DefaultExportRoots);
        }

        public static void Import(IEnumerable<string> exportRoots)
        {
            MapObjectCatalog catalog = FindCatalog();
            if (catalog == null)
            {
                Debug.LogError("[MapObjectGraphicsImporter] MapObjectCatalog not found. Import definitions first.");
                return;
            }

            catalog.RebuildIndex();

            List<string> roots = exportRoots?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToList() ?? new List<string>();

            List<string> symbolInventoryPaths = DefaultSymbolInventoryPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToList();

            List<string> wrapperScriptRoots = DefaultWrapperScriptRoots
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToList();

            if (roots.Count == 0)
            {
                Debug.LogError("[MapObjectGraphicsImporter] No valid export roots found.");
                return;
            }

            EnsureDirectoryExists(ImportedArtRoot);
            EnsureDirectoryExists(VisualAssetRoot);
            EnsureDirectoryExists(Path.GetDirectoryName(ReportAssetPath));

            FolderResolverIndex resolver = BuildResolver(roots, symbolInventoryPaths, wrapperScriptRoots);
            List<string> imported = new List<string>();
            List<string> unresolved = new List<string>();
            List<string> skipped = new List<string>();
            int createdVisuals = 0;
            int updatedVisuals = 0;

            for (int i = 0; i < catalog.definitions.Count; i++)
            {
                MapObjectDefinition definition = catalog.definitions[i];
                if (definition == null || string.IsNullOrWhiteSpace(definition.objectId))
                {
                    continue;
                }

                VisualImportDisposition disposition = GetImportDisposition(definition);
                if (!disposition.ShouldImport)
                {
                    skipped.Add($"{definition.objectId} [{disposition.reason}]");
                    continue;
                }

                if (!TryResolveFolder(definition, disposition.route, resolver, out string resolvedKey, out string sourceFolder))
                {
                    unresolved.Add($"{definition.objectId} [{disposition.route}]");
                    continue;
                }

                string visualId = ResolveImportedVisualId(resolvedKey, sourceFolder);
                MapObjectVisualDefinition visual = LoadOrCreateVisual(visualId, out bool isNew);
                ImportVisual(definition, visual, visualId, sourceFolder);

                if (isNew)
                {
                    createdVisuals++;
                }
                else
                {
                    updatedVisuals++;
                }

                imported.Add(
                    $"{definition.objectId} <- {visualId} ({Path.GetFileName(sourceFolder)}) " +
                    $"[{disposition.route}: {resolvedKey}]");
            }

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            WriteReport(
                roots,
                symbolInventoryPaths,
                wrapperScriptRoots,
                imported,
                unresolved,
                skipped,
                createdVisuals,
                updatedVisuals);

            Debug.Log(
                $"[MapObjectGraphicsImporter] Imported visuals for {imported.Count} definitions " +
                $"({createdVisuals} created, {updatedVisuals} updated). " +
                $"Unresolved supported: {unresolved.Count}. Skipped: {skipped.Count}");
        }

        static MapObjectCatalog FindCatalog()
        {
            string[] guids = AssetDatabase.FindAssets(CatalogSearchFilter);
            if (guids.Length == 0)
            {
                return null;
            }

            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<MapObjectCatalog>(path);
        }

        static FolderResolverIndex BuildResolver(
            IEnumerable<string> exportRoots,
            IEnumerable<string> symbolInventoryPaths,
            IEnumerable<string> wrapperScriptRoots)
        {
            FolderResolverIndex resolver = new FolderResolverIndex();

            foreach (string exportRoot in exportRoots)
            {
                string[] directories = Directory.GetDirectories(exportRoot, "*", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < directories.Length; i++)
                {
                    string directory = directories[i];
                    string folderName = Path.GetFileName(directory);
                    foreach (string candidate in GetCandidateNames(folderName))
                    {
                        if (!resolver.ByName.ContainsKey(candidate))
                        {
                            resolver.ByName[candidate] = directory;
                        }
                    }

                    int symbolId = ParseSourceSymbolId(folderName);
                    if (symbolId > 0 && !resolver.BySymbolId.ContainsKey(symbolId))
                    {
                        resolver.BySymbolId[symbolId] = directory;
                    }
                }
            }

            foreach (VisualInventoryEntry inventoryEntry in LoadSymbolInventory(symbolInventoryPaths))
            {
                RegisterInventoryEntry(resolver, inventoryEntry);
            }

            foreach (VisualInventoryEntry inventoryEntry in LoadWrapperInventory(wrapperScriptRoots))
            {
                RegisterInventoryEntry(resolver, inventoryEntry);
            }

            return resolver;
        }

        static void RegisterInventoryEntry(FolderResolverIndex resolver, VisualInventoryEntry inventoryEntry)
        {
            if (resolver == null || inventoryEntry == null || string.IsNullOrWhiteSpace(inventoryEntry.className))
            {
                return;
            }

            if (inventoryEntry.symbolId <= 0 ||
                !resolver.BySymbolId.TryGetValue(inventoryEntry.symbolId, out string resolvedDirectory) ||
                string.IsNullOrWhiteSpace(resolvedDirectory))
            {
                return;
            }

            foreach (string candidate in GetCandidateNames(inventoryEntry.className))
            {
                if (!resolver.ByName.ContainsKey(candidate))
                {
                    resolver.ByName[candidate] = resolvedDirectory;
                }
            }
        }

        static IEnumerable<string> GetCandidateNames(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                yield break;
            }

            HashSet<string> emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string candidate in ExpandCandidateNames(folderName))
            {
                if (emitted.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        static IEnumerable<string> ExpandCandidateNames(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                yield break;
            }

            yield return rawName;

            string suffix = rawName;
            if (rawName.StartsWith("DefineSprite_", StringComparison.OrdinalIgnoreCase))
            {
                Match symbolMatch = Regex.Match(rawName, @"^DefineSprite_\d+_(.+)$", RegexOptions.IgnoreCase);
                if (symbolMatch.Success)
                {
                    suffix = symbolMatch.Groups[1].Value;
                    yield return suffix;
                }
            }

            foreach (string variant in ExpandNormalizedNameVariants(suffix))
            {
                yield return variant;
            }
        }

        static IEnumerable<string> ExpandNormalizedNameVariants(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            HashSet<string> variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                value
            };

            string normalized = value.Replace('\\', '/');
            variants.Add(normalized);

            int slashIndex = normalized.LastIndexOf('/');
            if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
            {
                variants.Add(normalized.Substring(slashIndex + 1));
            }

            int dotIndex = normalized.LastIndexOf('.');
            if (dotIndex >= 0 && dotIndex < normalized.Length - 1)
            {
                variants.Add(normalized.Substring(dotIndex + 1));
            }

            string withoutNumericSuffix = Regex.Replace(normalized, @"[_\-]\d+$", string.Empty);
            variants.Add(withoutNumericSuffix);

            if (withoutNumericSuffix.StartsWith("vis", StringComparison.OrdinalIgnoreCase) && withoutNumericSuffix.Length > 3)
            {
                variants.Add(withoutNumericSuffix.Substring(3));
            }

            foreach (string variant in variants)
            {
                if (string.IsNullOrWhiteSpace(variant))
                {
                    continue;
                }

                string cleaned = Regex.Replace(variant, "pfe_fla\\.", string.Empty, RegexOptions.IgnoreCase);
                cleaned = Regex.Replace(cleaned, "symbol", string.Empty, RegexOptions.IgnoreCase);
                cleaned = cleaned.Trim('_', '-', '.', ' ');

                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    yield return cleaned;
                }
            }
        }

        static bool TryResolveFolder(
            MapObjectDefinition definition,
            VisualImportRoute route,
            FolderResolverIndex resolver,
            out string resolvedKey,
            out string sourceFolder)
        {
            foreach (string candidate in GetDefinitionCandidates(definition, route))
            {
                if (resolver.ByName.TryGetValue(candidate, out sourceFolder))
                {
                    resolvedKey = candidate;
                    return true;
                }
            }

            resolvedKey = string.Empty;
            sourceFolder = string.Empty;
            return false;
        }

        static IEnumerable<string> GetDefinitionCandidates(MapObjectDefinition definition, VisualImportRoute route)
        {
            HashSet<string> emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string value, List<string> output)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                foreach (string variant in ExpandNormalizedNameVariants(value))
                {
                    if (emitted.Add(variant))
                    {
                        output.Add(variant);
                    }
                }

                if (emitted.Add(value))
                {
                    output.Add(value);
                }
            }

            void AddVisualHint(string value, List<string> output)
            {
                if (IsSafeVisualHint(value))
                {
                    AddCandidate(value, output);
                }
            }

            List<string> candidates = new List<string>();
            switch (route)
            {
                case VisualImportRoute.OrdinaryProp:
                    AddCandidate(definition.objectId, candidates);
                    AddVisualHint(definition.defaultVisualId, candidates);
                    AddVisualHint(definition.GetAttribute("vis"), candidates);
                    AddCandidate($"vis{definition.objectId}", candidates);
                    break;
                case VisualImportRoute.Trap:
                    AddVisualHint(definition.defaultVisualId, candidates);
                    AddVisualHint(definition.GetAttribute("vis"), candidates);
                    AddCandidate($"vistrap{definition.objectId}", candidates);
                    break;
                case VisualImportRoute.Checkpoint:
                    AddCandidate("vischeckpoint", candidates);
                    break;
                case VisualImportRoute.AreaOverlay:
                    AddCandidate("visArea", candidates);
                    break;
            }

            foreach (string sharedCandidate in GetSharedVisualCandidates(definition, route))
            {
                AddCandidate(sharedCandidate, candidates);
            }

            return candidates;
        }

        static bool IsSafeVisualHint(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            return trimmed.StartsWith("vis", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("DefineSprite_", StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<string> GetSharedVisualCandidates(MapObjectDefinition definition, VisualImportRoute route)
        {
            if (definition == null)
            {
                yield break;
            }

            switch (route)
            {
                case VisualImportRoute.OrdinaryProp:
                    if (UsesBoxFallback(definition))
                    {
                        yield return "visbox0";
                    }

                    break;
                case VisualImportRoute.Trap:
                    yield return "vistrapspikes";
                    break;
            }
        }

        static bool UsesBoxFallback(MapObjectDefinition definition)
        {
            string normalizedTip = NormalizeLegacyTip(definition.legacyTip);
            return normalizedTip == "box" || normalizedTip == "door";
        }

        static MapObjectVisualDefinition LoadOrCreateVisual(string visualId, out bool isNew)
        {
            string assetPath = $"{VisualAssetRoot}/{SanitizeFileName(visualId)}.asset";
            MapObjectVisualDefinition visual = AssetDatabase.LoadAssetAtPath<MapObjectVisualDefinition>(assetPath);
            isNew = visual == null;

            if (isNew)
            {
                visual = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
                AssetDatabase.CreateAsset(visual, assetPath);
            }

            return visual;
        }

        static void ImportVisual(
            MapObjectDefinition definition,
            MapObjectVisualDefinition visual,
            string visualId,
            string sourceFolder)
        {
            string importTargetFolder = $"{ImportedArtRoot}/{SanitizeFileName(visualId)}";
            EnsureDirectoryExists(importTargetFolder);

            List<Sprite> sprites = ImportFolderFrames(sourceFolder, importTargetFolder);

            visual.visualId = visualId;
            if (string.IsNullOrWhiteSpace(visual.objectId))
            {
                visual.objectId = definition.objectId;
            }

            if (visual.linkedObjectIds == null)
            {
                visual.linkedObjectIds = new List<string>();
            }

            if (!visual.linkedObjectIds.Contains(definition.objectId))
            {
                visual.linkedObjectIds.Add(definition.objectId);
                visual.linkedObjectIds.Sort(StringComparer.OrdinalIgnoreCase);
            }

            visual.sourceFolderName = Path.GetFileName(sourceFolder);
            visual.sourceSymbolId = ParseSourceSymbolId(visual.sourceFolderName);
            visual.frames = sprites.ToArray();
            visual.pixelsPerUnit = PixelsPerUnit;
            visual.pixelSize = GetPixelSize(visual.FirstFrame);
            visual.wallMounted = definition.wallMounted;
            visual.pivot = definition.wallMounted ? new Vector2(0.5f, 0.5f) : new Vector2(0.5f, 0f);
            visual.localOffset = Vector2.zero;
            visual.sortingOrder = definition.wallMounted ? 1 : 0;

            definition.visual = visual;
            definition.defaultVisualId = visualId;

            EditorUtility.SetDirty(visual);
            EditorUtility.SetDirty(definition);
        }

        static string ResolveImportedVisualId(string resolvedKey, string sourceFolder)
        {
            string folderName = Path.GetFileName(sourceFolder);
            string preferredFolderId = NormalizeVisualId(folderName);
            if (!string.IsNullOrWhiteSpace(preferredFolderId))
            {
                return preferredFolderId;
            }

            string preferredResolvedKey = NormalizeVisualId(resolvedKey);
            if (!string.IsNullOrWhiteSpace(preferredResolvedKey))
            {
                return preferredResolvedKey;
            }

            return SanitizeFileName(folderName);
        }

        static List<Sprite> ImportFolderFrames(string sourceFolder, string targetFolder)
        {
            List<string> sourceFiles = Directory.GetFiles(sourceFolder, "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(ParseFrameOrder)
                .ThenBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<Sprite> sprites = new List<Sprite>(sourceFiles.Count);

            for (int i = 0; i < sourceFiles.Count; i++)
            {
                string sourceFile = sourceFiles[i];
                string fileName = Path.GetFileName(sourceFile);
                string destinationAbsolute = Path.Combine(Directory.GetCurrentDirectory(), targetFolder.Replace('/', Path.DirectorySeparatorChar), fileName);
                File.Copy(sourceFile, destinationAbsolute, true);
            }

            AssetDatabase.Refresh();

            for (int i = 0; i < sourceFiles.Count; i++)
            {
                string fileName = Path.GetFileName(sourceFiles[i]);
                string assetPath = $"{targetFolder}/{fileName}";
                ConfigureSpriteImport(assetPath);
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                if (sprite != null)
                {
                    sprites.Add(sprite);
                }
            }

            return sprites;
        }

        static void ConfigureSpriteImport(string assetPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Point;
            importer.spritePixelsPerUnit = PixelsPerUnit;
            importer.SaveAndReimport();
        }

        static int ParseFrameOrder(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return int.TryParse(name, out int value) ? value : int.MaxValue;
        }

        static int ParseSourceSymbolId(string folderName)
        {
            Match match = Regex.Match(folderName ?? string.Empty, @"DefineSprite_(\d+)", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out int symbolId) ? symbolId : 0;
        }

        static string NormalizeVisualId(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return string.Empty;
            }

            string value = rawValue.Trim();
            Match spriteMatch = Regex.Match(value, @"^DefineSprite_\d+_(.+)$", RegexOptions.IgnoreCase);
            if (spriteMatch.Success)
            {
                value = spriteMatch.Groups[1].Value;
            }

            value = Regex.Replace(value, "pfe_fla\\._?", string.Empty, RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"[_\-]\d+$", string.Empty);
            value = value.Trim('_', '-', '.', ' ');

            return value;
        }

        static IEnumerable<VisualInventoryEntry> LoadSymbolInventory(IEnumerable<string> symbolInventoryPaths)
        {
            List<VisualInventoryEntry> entries = new List<VisualInventoryEntry>();
            HashSet<string> seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string inventoryPath in symbolInventoryPaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(inventoryPath) || !File.Exists(inventoryPath))
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(inventoryPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    VisualInventoryEntry entry = ParseSymbolInventoryEntry(lines[i], inventoryPath);
                    if (entry == null || string.IsNullOrWhiteSpace(entry.className))
                    {
                        continue;
                    }

                    if (seenClasses.Add(entry.className))
                    {
                        entries.Add(entry);
                    }
                }
            }

            return entries;
        }

        static IEnumerable<VisualInventoryEntry> LoadWrapperInventory(IEnumerable<string> wrapperScriptRoots)
        {
            List<VisualInventoryEntry> entries = new List<VisualInventoryEntry>();
            HashSet<string> seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string wrapperScriptRoot in wrapperScriptRoots ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(wrapperScriptRoot) || !Directory.Exists(wrapperScriptRoot))
                {
                    continue;
                }

                string[] scriptPaths = Directory.GetFiles(wrapperScriptRoot, "vis*.as", SearchOption.TopDirectoryOnly);
                Array.Sort(scriptPaths, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < scriptPaths.Length; i++)
                {
                    string scriptPath = scriptPaths[i];
                    string className = Path.GetFileNameWithoutExtension(scriptPath);
                    if (!IsSupportedWrapperName(className))
                    {
                        continue;
                    }

                    int symbolId = TryReadEmbedSymbolId(scriptPath);
                    if (symbolId <= 0 || !seenClasses.Add(className))
                    {
                        continue;
                    }

                    entries.Add(new VisualInventoryEntry
                    {
                        className = className,
                        sourcePath = scriptPath,
                        symbolId = symbolId
                    });
                }
            }

            return entries;
        }

        static VisualInventoryEntry ParseSymbolInventoryEntry(string csvLine, string inventoryPath)
        {
            if (string.IsNullOrWhiteSpace(csvLine))
            {
                return null;
            }

            Match match = Regex.Match(csvLine, "^\\s*(\\d+)\\s*;\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, out int symbolId) ||
                symbolId <= 0)
            {
                return null;
            }

            string className = match.Groups[2].Value.Trim();
            if (!IsSupportedWrapperName(className))
            {
                return null;
            }

            return new VisualInventoryEntry
            {
                className = className,
                sourcePath = inventoryPath,
                symbolId = symbolId
            };
        }

        static bool IsSupportedWrapperName(string className)
        {
            return !string.IsNullOrWhiteSpace(className) &&
                   className.StartsWith("vis", StringComparison.OrdinalIgnoreCase);
        }

        static int TryReadEmbedSymbolId(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return 0;
            }

            try
            {
                string sourceText = File.ReadAllText(sourcePath);
                Match symbolMatch = Regex.Match(sourceText, "symbol=\"symbol(\\d+)\"", RegexOptions.IgnoreCase);
                return symbolMatch.Success && int.TryParse(symbolMatch.Groups[1].Value, out int symbolId)
                    ? symbolId
                    : 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        static Vector2Int GetPixelSize(Sprite sprite)
        {
            if (sprite == null)
            {
                return Vector2Int.zero;
            }

            Rect rect = sprite.rect;
            return new Vector2Int(Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height));
        }

        static string SanitizeFileName(string rawId)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = rawId ?? string.Empty;
            for (int i = 0; i < invalidChars.Length; i++)
            {
                sanitized = sanitized.Replace(invalidChars[i], '_');
            }

            return sanitized;
        }

        static void EnsureDirectoryExists(string projectRelativeDirectory)
        {
            if (string.IsNullOrWhiteSpace(projectRelativeDirectory))
            {
                return;
            }

            string absoluteDirectory = Path.Combine(
                Directory.GetCurrentDirectory(),
                projectRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(absoluteDirectory))
            {
                Directory.CreateDirectory(absoluteDirectory);
            }
        }

        static void WriteReport(
            List<string> exportRoots,
            List<string> symbolInventoryPaths,
            List<string> wrapperScriptRoots,
            List<string> imported,
            List<string> unresolved,
            List<string> skipped,
            int createdVisuals,
            int updatedVisuals)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("Map Object Graphics Import Report");
            report.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            report.AppendLine();
            report.AppendLine("Export roots:");
            for (int i = 0; i < exportRoots.Count; i++)
            {
                report.AppendLine($"- {exportRoots[i]}");
            }

            report.AppendLine();
            report.AppendLine("Wrapper script roots:");
            if (wrapperScriptRoots.Count == 0)
            {
                report.AppendLine("- (none)");
            }
            else
            {
                for (int i = 0; i < wrapperScriptRoots.Count; i++)
                {
                    report.AppendLine($"- {wrapperScriptRoots[i]}");
                }
            }

            report.AppendLine();
            report.AppendLine("Symbol inventories:");
            if (symbolInventoryPaths.Count == 0)
            {
                report.AppendLine("- (none)");
            }
            else
            {
                for (int i = 0; i < symbolInventoryPaths.Count; i++)
                {
                    report.AppendLine($"- {symbolInventoryPaths[i]}");
                }
            }

            report.AppendLine();
            report.AppendLine($"Imported visuals: {imported.Count}");
            report.AppendLine($"Created visual assets: {createdVisuals}");
            report.AppendLine($"Updated visual assets: {updatedVisuals}");
            report.AppendLine($"Unresolved supported definitions: {unresolved.Count}");
            report.AppendLine($"Skipped definitions: {skipped.Count}");

            report.AppendLine();
            report.AppendLine("Resolved:");
            foreach (string line in imported.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                report.AppendLine($"- {line}");
            }

            report.AppendLine();
            report.AppendLine("Unresolved:");
            foreach (string id in unresolved.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                report.AppendLine($"- {id}");
            }

            report.AppendLine();
            report.AppendLine("Skipped:");
            foreach (string id in skipped.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                report.AppendLine($"- {id}");
            }

            File.WriteAllText(
                Path.Combine(Directory.GetCurrentDirectory(), ReportAssetPath.Replace('/', Path.DirectorySeparatorChar)),
                report.ToString(),
                Encoding.UTF8);
        }

        static string NormalizeLegacyTip(string rawTip)
        {
            return string.IsNullOrWhiteSpace(rawTip)
                ? string.Empty
                : rawTip.Trim().ToLowerInvariant();
        }

        static VisualImportDisposition GetImportDisposition(MapObjectDefinition definition)
        {
            if (definition == null)
            {
                return VisualImportDisposition.Skip("missing definition");
            }

            string normalizedTip = NormalizeLegacyTip(definition.legacyTip);
            switch (normalizedTip)
            {
                case "unit":
                    return VisualImportDisposition.Skip("handled by unit/enemy pipeline");
                case "up":
                case "enspawn":
                case "spawnpoint":
                    return VisualImportDisposition.Skip("spawn or encounter marker");
                case "bonus":
                    return VisualImportDisposition.Skip("bonus visuals handled separately");
                case "area":
                    return VisualImportDisposition.Import(VisualImportRoute.AreaOverlay, "area overlay");
                case "checkpoint":
                    return VisualImportDisposition.Import(VisualImportRoute.Checkpoint, "checkpoint");
                case "trap":
                    return VisualImportDisposition.Import(VisualImportRoute.Trap, "trap");
                case "box":
                case "door":
                    return VisualImportDisposition.Import(VisualImportRoute.OrdinaryProp, "ordinary prop");
            }

            if (definition.family == MapObjectFamily.AreaTrigger)
            {
                return VisualImportDisposition.Import(VisualImportRoute.AreaOverlay, "area overlay");
            }

            if (definition.family == MapObjectFamily.Checkpoint)
            {
                return VisualImportDisposition.Import(VisualImportRoute.Checkpoint, "checkpoint");
            }

            if (definition.family == MapObjectFamily.PlayerSpawn ||
                definition.family == MapObjectFamily.SpawnMarker)
            {
                return VisualImportDisposition.Skip("spawn or helper marker");
            }

            if (definition.family == MapObjectFamily.Bonus)
            {
                return VisualImportDisposition.Skip("bonus visuals handled separately");
            }

            if (definition.family == MapObjectFamily.Container ||
                definition.family == MapObjectFamily.Device ||
                definition.family == MapObjectFamily.Furniture ||
                definition.family == MapObjectFamily.Door ||
                definition.family == MapObjectFamily.Platform ||
                definition.family == MapObjectFamily.Transition ||
                definition.family == MapObjectFamily.GenericObject)
            {
                return VisualImportDisposition.Import(VisualImportRoute.OrdinaryProp, "ordinary prop");
            }

            if (definition.family == MapObjectFamily.Trap)
            {
                return VisualImportDisposition.Import(VisualImportRoute.Trap, "trap");
            }

            return VisualImportDisposition.Skip("unsupported visual route");
        }

        sealed class FolderResolverIndex
        {
            public readonly Dictionary<string, string> ByName =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public readonly Dictionary<int, string> BySymbolId =
                new Dictionary<int, string>();
        }

        sealed class VisualInventoryEntry
        {
            public string className;
            public string sourcePath;
            public int symbolId;
        }

        enum VisualImportRoute
        {
            Unsupported,
            OrdinaryProp,
            Trap,
            Checkpoint,
            AreaOverlay,
        }

        sealed class VisualImportDisposition
        {
            public VisualImportRoute route;
            public string reason;

            public bool ShouldImport => route != VisualImportRoute.Unsupported;

            public static VisualImportDisposition Import(VisualImportRoute route, string reason)
            {
                return new VisualImportDisposition
                {
                    route = route,
                    reason = reason
                };
            }

            public static VisualImportDisposition Skip(string reason)
            {
                return new VisualImportDisposition
                {
                    route = VisualImportRoute.Unsupported,
                    reason = reason
                };
            }
        }
    }
}
#endif
