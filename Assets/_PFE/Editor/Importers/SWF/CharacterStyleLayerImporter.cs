#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Imports individual color-tintable layers (h0/h1) and style variants
    /// from composite body part symbols (mane, tail, forelock, eye).
    ///
    /// The original Flash character uses nested MovieClips:
    ///   mane_22 (symbol 195) → h0 (primary, 5 styles), h1 (secondary, 5 styles)
    ///   tail_15 (symbol 136) → h0, h1
    ///   hair_31 (symbol 233) → h0, h1
    ///   eye_28  (symbol 220) → eye (iris, 6 styles), zrak (pupil, 1 frame)
    ///
    /// Each layer gets a separate color tint at runtime (Obj.setColor).
    /// Style variants are frame-based (Appear.fHair = 1..5, Appear.fEye = 1..6).
    ///
    /// This importer walks the SWF symbol tree to discover child symbol IDs,
    /// then copies their per-frame PNGs from the JPEXS export.
    /// </summary>
    public static class CharacterStyleLayerImporter
    {
        const int PixelsPerUnit = 100;
        const string StyleArtRoot = "Assets/_PFE/Art/Character/Styles";

        /// <summary>
        /// Composite symbols that contain style layer children.
        /// Key = symbol ID, Value = human-readable part name.
        /// </summary>
        public static readonly Dictionary<int, string> CompositeSymbols = new()
        {
            { 195, "Mane" },      // mane_22 → h0 (primary), h1 (secondary)
            { 136, "Tail" },      // tail_15 → h0, h1
            { 233, "Forelock" },  // hair_31 → h0, h1
            { 220, "Eye" },       // eye_28 → eye (iris), zrak (pupil)
        };

        /// <summary>
        /// Known instance names within composites and their role labels.
        /// </summary>
        static readonly Dictionary<string, string> LayerRoles = new()
        {
            { "h0", "Primary" },
            { "h1", "Secondary" },
            { "eye", "Iris" },
            { "zrak", "Pupil" },
        };

        /// <summary>
        /// Tint channel indices matching Obj.setColor in the original AS3.
        /// 0=Fur, 1=PrimaryHair, 2=SecondaryHair, 3=Eye, 4=Magic, -1=None
        /// </summary>
        static readonly Dictionary<string, int> LayerTintChannels = new()
        {
            { "h0", 1 },    // primary mane/hair color
            { "h1", 2 },    // secondary mane/hair color
            { "eye", 3 },   // eye color
            { "zrak", -1 }, // pupil, not tinted
        };

        /// <summary>
        /// A discovered layer within a composite symbol.
        /// </summary>
        public class DiscoveredLayer
        {
            public string PartName;        // "Mane", "Tail", "Forelock", "Eye"
            public string LayerRole;       // "Primary", "Secondary", "Iris", "Pupil"
            public string InstanceName;    // "h0", "h1", "eye", "zrak"
            public int ParentSymbolId;     // composite symbol ID (195, 136, etc.)
            public int ChildSymbolId;      // discovered child symbol ID
            public int FrameCount;         // number of style frames in JPEXS export
            public int TintChannel;        // color channel index (-1 = none)
            public Vector2 Pivot;          // computed from SWF bounds
            public Vector2 PlacementPos;   // position within parent (Flash coords)
        }

        /// <summary>
        /// Result of the discovery + import process.
        /// </summary>
        public class ImportResult
        {
            public List<DiscoveredLayer> Layers = new();

            /// <summary>childSymbolId → (frameNumber → Sprite)</summary>
            public Dictionary<int, Dictionary<int, Sprite>> SpritesBySymbol = new();

            /// <summary>childSymbolId → pivot</summary>
            public Dictionary<int, Vector2> Pivots = new();

            public int TotalSpritesImported;
            public int TotalLayersDiscovered;
            public int ZoomFactor = 1;
            public List<string> Warnings = new();
            public List<string> Log = new();
        }

        /// <summary>
        /// Discover layer children within composite symbols using SWF tree,
        /// then import their frame PNGs from the JPEXS export.
        /// </summary>
        public static ImportResult Import(string jpexsExportRoot, SWFFile swfData, int zoomFactor = 1)
        {
            var result = new ImportResult { ZoomFactor = zoomFactor };
            string spritesRoot = Path.Combine(jpexsExportRoot, "sprites");

            if (!Directory.Exists(spritesRoot))
            {
                result.Warnings.Add($"Sprites folder not found: {spritesRoot}");
                return result;
            }

            // Step 1: Discover layer children by walking the SWF tree
            DiscoverLayers(swfData, spritesRoot, result);

            if (result.Layers.Count == 0)
            {
                result.Warnings.Add("No style layers discovered from SWF tree. Check that composite symbols exist.");
                return result;
            }

            result.TotalLayersDiscovered = result.Layers.Count;
            result.Log.Add($"Discovered {result.Layers.Count} style layers:");
            foreach (var layer in result.Layers)
                result.Log.Add($"  {layer.PartName}/{layer.LayerRole} = symbol {layer.ChildSymbolId} ({layer.FrameCount} frames, tint ch.{layer.TintChannel})");

            // Step 2: Copy PNGs from JPEXS and configure import settings
            EnsureDirectory(StyleArtRoot);
            ImportLayerSprites(spritesRoot, result);

            return result;
        }

        /// <summary>
        /// Walk each composite symbol's frame 1 placements to find named children.
        /// </summary>
        static void DiscoverLayers(SWFFile swfData, string spritesRoot, ImportResult result)
        {
            foreach (var kvp in CompositeSymbols)
            {
                int compositeId = kvp.Key;
                string partName = kvp.Value;

                if (!swfData.Symbols.TryGetValue(compositeId, out var symbol))
                {
                    result.Warnings.Add($"{partName} (symbol {compositeId}): not found in SWF");
                    continue;
                }

                if (symbol.Frames.Count == 0)
                {
                    result.Warnings.Add($"{partName} (symbol {compositeId}): no frames");
                    continue;
                }

                var frame1 = symbol.Frames[0];

                foreach (var placement in frame1.Placements)
                {
                    string instanceName = placement.InstanceName;
                    if (string.IsNullOrEmpty(instanceName))
                        continue;

                    if (!LayerRoles.TryGetValue(instanceName, out string layerRole))
                        continue;

                    int childId = placement.CharacterId;

                    // Check if JPEXS has an export for this child
                    string childFolder = Path.Combine(spritesRoot, $"DefineSprite_{childId}_symbol{childId}");
                    int frameCount = 0;
                    if (Directory.Exists(childFolder))
                    {
                        frameCount = Directory.GetFiles(childFolder, "*.png").Length;
                    }

                    // Also check ShapeBounds in case it's a DefineShape (single image)
                    if (frameCount == 0 && swfData.ShapeBounds.ContainsKey(childId))
                    {
                        // It's a shape, check shapes/ SVG
                        frameCount = -1; // marker: shape, not sprite
                    }

                    // Compute pivot from SWF bounds
                    Vector2 pivot = ComputePivot(childId, swfData);

                    int tintChannel = LayerTintChannels.TryGetValue(instanceName, out int ch) ? ch : -1;

                    var layer = new DiscoveredLayer
                    {
                        PartName = partName,
                        LayerRole = layerRole,
                        InstanceName = instanceName,
                        ParentSymbolId = compositeId,
                        ChildSymbolId = childId,
                        FrameCount = frameCount,
                        TintChannel = tintChannel,
                        Pivot = pivot,
                        PlacementPos = placement.Position,
                    };
                    result.Layers.Add(layer);
                    result.Pivots[childId] = pivot;
                }

                // If no children were found via instance names, log it
                bool foundAny = result.Layers.Any(l => l.ParentSymbolId == compositeId);
                if (!foundAny)
                {
                    var names = string.Join(", ", frame1.Placements
                        .Select(p => string.IsNullOrEmpty(p.InstanceName) ? $"unnamed(sym{p.CharacterId})" : p.InstanceName));
                    result.Warnings.Add($"{partName} (symbol {compositeId}): no known layer children found. Placements: [{names}]");
                }
            }
        }

        /// <summary>
        /// Copy frame PNGs from JPEXS export and configure TextureImporter settings.
        /// </summary>
        static void ImportLayerSprites(string spritesRoot, ImportResult result)
        {
            int effectivePPU = PixelsPerUnit * result.ZoomFactor;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var layer in result.Layers)
                {
                    if (layer.FrameCount <= 0)
                    {
                        result.Warnings.Add($"{layer.PartName}/{layer.LayerRole} (symbol {layer.ChildSymbolId}): no JPEXS export");
                        continue;
                    }

                    string sourceFolder = Path.Combine(spritesRoot, $"DefineSprite_{layer.ChildSymbolId}_symbol{layer.ChildSymbolId}");
                    string destFolder = Path.Combine(StyleArtRoot, layer.PartName, layer.LayerRole);
                    EnsureDirectory(destFolder);

                    var pngFiles = Directory.GetFiles(sourceFolder, "*.png")
                        .OrderBy(f => GetFrameNumber(f))
                        .ToArray();

                    var spritesByFrame = new Dictionary<int, Sprite>();

                    foreach (string pngPath in pngFiles)
                    {
                        int frameNum = GetFrameNumber(pngPath);
                        string destFileName = pngFiles.Length > 1
                            ? $"style_{frameNum}.png"
                            : "base.png";
                        string destAssetPath = Path.Combine(destFolder, destFileName);
                        string destFullPath = Path.GetFullPath(destAssetPath);

                        if (!File.Exists(destFullPath) ||
                            File.GetLastWriteTimeUtc(pngPath) > File.GetLastWriteTimeUtc(destFullPath))
                        {
                            File.Copy(pngPath, destFullPath, true);
                        }

                        spritesByFrame[frameNum] = null; // placeholder
                        result.TotalSpritesImported++;
                    }

                    result.SpritesBySymbol[layer.ChildSymbolId] = spritesByFrame;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh();

            // Configure import settings and collect sprite references
            foreach (var layer in result.Layers)
            {
                if (!result.SpritesBySymbol.ContainsKey(layer.ChildSymbolId))
                    continue;

                var frameMap = result.SpritesBySymbol[layer.ChildSymbolId];
                string destFolder = Path.Combine(StyleArtRoot, layer.PartName, layer.LayerRole);
                Vector2 pivot = layer.Pivot;
                int frameCount = frameMap.Count;

                var updated = new Dictionary<int, Sprite>();
                foreach (int frameNum in frameMap.Keys.OrderBy(k => k))
                {
                    string destFileName = frameCount > 1
                        ? $"style_{frameNum}.png"
                        : "base.png";
                    string assetPath = Path.Combine(destFolder, destFileName).Replace('\\', '/');

                    var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (importer != null)
                    {
                        importer.textureType = TextureImporterType.Sprite;
                        importer.spriteImportMode = SpriteImportMode.Single;
                        importer.spritePixelsPerUnit = effectivePPU;
                        importer.filterMode = FilterMode.Point;
                        importer.textureCompression = TextureImporterCompression.Uncompressed;

                        var settings = new TextureImporterSettings();
                        importer.ReadTextureSettings(settings);
                        settings.spriteMode = (int)SpriteImportMode.Single;
                        settings.spriteAlignment = (int)SpriteAlignment.Custom;
                        settings.spritePivot = new Vector2(
                            Mathf.Clamp01(pivot.x),
                            Mathf.Clamp01(pivot.y));
                        importer.SetTextureSettings(settings);
                        importer.spritePackingTag = $"CharStyle_{layer.PartName}";
                        importer.SaveAndReimport();
                    }
                    else
                    {
                        result.Warnings.Add($"{layer.PartName}/{layer.LayerRole} frame {frameNum}: TextureImporter not found at {assetPath}");
                    }

                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                    if (sprite != null)
                        updated[frameNum] = sprite;
                    else
                        result.Warnings.Add($"{layer.PartName}/{layer.LayerRole} frame {frameNum}: sprite load failed at {assetPath}");
                }

                result.SpritesBySymbol[layer.ChildSymbolId] = updated;
            }
        }

        /// <summary>
        /// Compute pivot from SWF bounds (same logic as CharacterSpriteImporter).
        /// Registration point (0,0) in Flash → pivot in Unity sprite space.
        /// </summary>
        static Vector2 ComputePivot(int symbolId, SWFFile swfData)
        {
            Rect bounds = default;
            bool hasBounds = false;

            if (swfData.ShapeBounds.TryGetValue(symbolId, out bounds) && bounds.width > 0 && bounds.height > 0)
                hasBounds = true;
            else if (swfData.Frame1Bounds.TryGetValue(symbolId, out var f1b) && f1b.width > 0 && f1b.height > 0)
            {
                bounds = f1b;
                hasBounds = true;
            }
            else if (swfData.Symbols.TryGetValue(symbolId, out var sym) && sym.Bounds.width > 0 && sym.Bounds.height > 0)
            {
                bounds = sym.Bounds;
                hasBounds = true;
            }

            if (!hasBounds)
                return new Vector2(0.5f, 0.5f);

            float pivotX = (0f - bounds.x) / bounds.width;
            float pivotY = 1f - ((0f - bounds.y) / bounds.height);
            return new Vector2(pivotX, pivotY);
        }

        static void EnsureDirectory(string assetPath)
        {
            string fullPath = Path.GetFullPath(assetPath);
            if (!Directory.Exists(fullPath))
                Directory.CreateDirectory(fullPath);
        }

        static int GetFrameNumber(string pngPath)
        {
            string fileName = Path.GetFileNameWithoutExtension(pngPath);
            if (int.TryParse(fileName, out int num))
                return num;
            return 0;
        }
    }
}
#endif
