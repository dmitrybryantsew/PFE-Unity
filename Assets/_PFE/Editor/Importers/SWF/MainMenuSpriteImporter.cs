#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Imports main menu sprites into Unity. Handles two source types:
    /// 1. Shapes with embedded PNGs inside SVG files (background layers, magic circles, etc.)
    /// 2. DefineSprite frame PNGs from JPEXS export (eye blink, sparks, lightning bolts)
    ///
    /// All sprites are imported into Assets/_PFE/Art/MainMenu/ with correct pivot settings.
    /// </summary>
    public static class MainMenuSpriteImporter
    {
        const int PixelsPerUnit = 100;
        const string ArtRoot = "Assets/_PFE/Art/MainMenu";

        /// <summary>
        /// Result of the import process.
        /// </summary>
        public class ImportResult
        {
            /// <summary>symbolId or shapeId → Sprite (single-frame) or first frame sprite.</summary>
            public Dictionary<int, Sprite> SingleSprites = new();

            /// <summary>symbolId → ordered frame sprites (for animated sequences).</summary>
            public Dictionary<int, Sprite[]> FrameSequences = new();

            /// <summary>shapeId → computed pivot (normalized 0-1).</summary>
            public Dictionary<int, Vector2> Pivots = new();

            public int TotalSpritesImported;
            public List<string> Warnings = new();
            public List<string> Log = new();
        }

        /// <summary>
        /// Known shape IDs that need embedded PNG extraction from SVG files.
        /// </summary>
        public static readonly Dictionary<int, string> ShapeImports = new()
        {
            { 411, "sky" },               // 1920x565 background sky
            { 425, "city_scene" },         // 1920x926 city + baked pony
            { 471, "logo" },              // 641x249 title treatment
            { 437, "horn_piece" },         // 54x93 horn tip
            { 455, "ear_piece" },          // 42x63 ear/body
            { 457, "pistol_body" },        // 40x98 pistol
            { 434, "displacement_mane" },  // 240x300 mane (displacement target)
            { 451, "displacement_tail" },  // 240x300 tail (displacement target)
            { 443, "magic_krug" },         // 200x200 green magic circle
            { 441, "horn_magic_glow" },    // 220x220 horn glow backdrop
            { 462, "pistol_magic_glow" },  // 220x220 pistol glow
            { 465, "pistol_magic2_glow" }, // 220x220 pistol glow 2
            { 427, "eye_open" },           // 33x43 eye frame 1 (open)
            { 415, "lightning_clouds" },   // 800x544 storm clouds
        };

        /// <summary>
        /// Known DefineSprite IDs that need frame sequence import from JPEXS sprite folders.
        /// Maps to the parent sprites folder naming convention.
        /// </summary>
        public static readonly Dictionary<int, (string name, string jpexsFolderPattern)> SpriteImports = new()
        {
            { 432, ("eye_blink", "DefineSprite_432_*") },               // 5 frames
            { 448, ("horn_sparks", "DefineSprite_448_*") },             // 50 frames
            { 467, ("pistol_sparks", "DefineSprite_467_*") },           // 50 frames
        };

        /// <summary>
        /// Import all main menu sprites from the JPEXS export.
        /// </summary>
        /// <param name="jpexsRoot">Path to the _assets/ folder (contains shapes/, sprites/, images/)</param>
        /// <param name="swfData">Parsed SWF data for pivot computation</param>
        public static ImportResult Import(string jpexsRoot, SWFFile swfData)
        {
            var result = new ImportResult();

            // The shapes/sprites can be in _assets/ or in the parent pfe/ folder
            string shapesDir = FindDirectory(jpexsRoot, "shapes");
            string spritesDir = FindDirectory(jpexsRoot, "sprites");

            if (shapesDir == null)
            {
                result.Warnings.Add("Shapes directory not found. Cannot extract background PNGs.");
                return result;
            }

            EnsureDirectory(ArtRoot);

            AssetDatabase.StartAssetEditing();
            try
            {
                // Step 1: Extract embedded PNGs from SVG shape files
                foreach (var kvp in ShapeImports)
                {
                    int shapeId = kvp.Key;
                    string name = kvp.Value;
                    string svgPath = FindSvgFile(shapesDir, jpexsRoot, shapeId);

                    if (svgPath == null)
                    {
                        result.Warnings.Add($"Shape {shapeId} ({name}): SVG not found in any shapes directory");
                        continue;
                    }

                    string destFolder = Path.Combine(ArtRoot, "Background");
                    EnsureDirectory(destFolder);
                    string destPath = Path.Combine(destFolder, $"{name}.png");
                    string destFull = Path.GetFullPath(destPath);

                    bool extracted = ExtractPngFromSvg(svgPath, destFull, shapeId);
                    if (!extracted)
                    {
                        result.Warnings.Add($"Shape {shapeId} ({name}): no embedded PNG in SVG (vector-only)");
                        continue;
                    }

                    // Compute pivot from SWF bounds
                    Vector2 pivot = ComputePivot(shapeId, swfData);
                    result.Pivots[shapeId] = pivot;
                    result.TotalSpritesImported++;
                    result.Log.Add($"Shape {shapeId} ({name}): extracted PNG, pivot=({pivot.x:F2},{pivot.y:F2})");
                }

                // Step 2: Import DefineSprite frame sequences
                if (spritesDir != null)
                {
                    foreach (var kvp in SpriteImports)
                    {
                        int spriteId = kvp.Key;
                        string name = kvp.Value.name;
                        string pattern = kvp.Value.jpexsFolderPattern;

                        var dirs = Directory.GetDirectories(spritesDir, pattern);
                        if (dirs.Length == 0)
                        {
                            result.Warnings.Add($"Sprite {spriteId} ({name}): JPEXS folder not found matching {pattern}");
                            continue;
                        }

                        string sourceDir = dirs[0];
                        var pngFiles = Directory.GetFiles(sourceDir, "*.png")
                            .OrderBy(f => GetFrameNumber(f))
                            .ToArray();

                        if (pngFiles.Length == 0)
                        {
                            result.Warnings.Add($"Sprite {spriteId} ({name}): no PNGs in {sourceDir}");
                            continue;
                        }

                        string destFolder = Path.Combine(ArtRoot, "Animated", name);
                        EnsureDirectory(destFolder);

                        foreach (string pngPath in pngFiles)
                        {
                            int frameNum = GetFrameNumber(pngPath);
                            string destPath = Path.Combine(destFolder, $"f{frameNum:D3}.png");
                            string destFull = Path.GetFullPath(destPath);

                            if (!File.Exists(destFull) ||
                                File.GetLastWriteTimeUtc(pngPath) > File.GetLastWriteTimeUtc(destFull))
                            {
                                File.Copy(pngPath, destFull, true);
                            }
                            result.TotalSpritesImported++;
                        }

                        // Compute pivot for this sprite
                        Vector2 pivot = ComputePivot(spriteId, swfData);
                        result.Pivots[spriteId] = pivot;

                        result.Log.Add($"Sprite {spriteId} ({name}): imported {pngFiles.Length} frames, pivot=({pivot.x:F2},{pivot.y:F2})");
                    }

                    // Step 3: Import lightning bolt frames from ThunderHeadMoln
                    ImportLightningBolts(spritesDir, result, swfData);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh();

            // Step 4: Configure import settings and collect sprite references
            ConfigureAndCollect(result, swfData);

            return result;
        }

        static void ImportLightningBolts(string spritesDir, ImportResult result, SWFFile swfData)
        {
            // Lightning bolt frames are in ThunderHeadMoln (symbol 1870 in the outer sprites)
            // But in the main menu SWF tree, moln.moln is symbol 421 with 4 frames
            // The JPEXS export for the menu-specific bolt is likely in the _assets sprites
            // Try both locations
            string[] searchPatterns = { "DefineSprite_421_*", "DefineSprite_1870_*" };
            string sourceDir = null;

            foreach (string pat in searchPatterns)
            {
                var dirs = Directory.GetDirectories(spritesDir, pat);
                if (dirs.Length > 0)
                {
                    sourceDir = dirs[0];
                    break;
                }
            }

            // Also search up parent directories for sprites folder
            if (sourceDir == null)
            {
                string current = spritesDir;
                for (int i = 0; i < 4 && sourceDir == null; i++)
                {
                    current = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(current)) break;
                    string candidate = Path.Combine(current, "sprites");
                    if (Directory.Exists(candidate) && candidate != spritesDir)
                    {
                        foreach (string pat in searchPatterns)
                        {
                            var dirs = Directory.GetDirectories(candidate, pat);
                            if (dirs.Length > 0)
                            {
                                sourceDir = dirs[0];
                                break;
                            }
                        }
                    }
                }
            }

            if (sourceDir == null)
            {
                result.Warnings.Add("Lightning bolts: no JPEXS folder found for moln/ThunderHeadMoln");
                return;
            }

            var pngFiles = Directory.GetFiles(sourceDir, "*.png")
                .OrderBy(f => GetFrameNumber(f))
                .ToArray();

            if (pngFiles.Length == 0)
            {
                result.Warnings.Add("Lightning bolts: no PNGs found");
                return;
            }

            string destFolder = Path.Combine(ArtRoot, "Animated", "lightning_bolts");
            EnsureDirectory(destFolder);

            foreach (string pngPath in pngFiles)
            {
                int frameNum = GetFrameNumber(pngPath);
                string destPath = Path.Combine(destFolder, $"f{frameNum:D3}.png");
                string destFull = Path.GetFullPath(destPath);

                if (!File.Exists(destFull) ||
                    File.GetLastWriteTimeUtc(pngPath) > File.GetLastWriteTimeUtc(destFull))
                {
                    File.Copy(pngPath, destFull, true);
                }
                result.TotalSpritesImported++;
            }

            result.Log.Add($"Lightning bolts: imported {pngFiles.Length} frames from {Path.GetFileName(sourceDir)}");
        }

        /// <summary>
        /// After AssetDatabase.Refresh(), configure TextureImporter settings and collect Sprite references.
        /// </summary>
        static void ConfigureAndCollect(ImportResult result, SWFFile swfData)
        {
            // Configure background shape sprites
            foreach (var kvp in ShapeImports)
            {
                int shapeId = kvp.Key;
                string name = kvp.Value;
                string assetPath = $"{ArtRoot}/Background/{name}.png".Replace('\\', '/');

                var sprite = ConfigureSpriteAsset(assetPath, shapeId, result);
                if (sprite != null)
                    result.SingleSprites[shapeId] = sprite;
            }

            // Configure animated frame sequences
            foreach (var kvp in SpriteImports)
            {
                int spriteId = kvp.Key;
                string name = kvp.Value.name;
                string folder = $"{ArtRoot}/Animated/{name}".Replace('\\', '/');

                var frames = ConfigureFrameSequence(folder, spriteId, result);
                if (frames != null && frames.Length > 0)
                    result.FrameSequences[spriteId] = frames;
            }

            // Configure lightning bolt frames
            {
                string folder = $"{ArtRoot}/Animated/lightning_bolts".Replace('\\', '/');
                // Use symbol 421 as the key (moln.moln in the SWF tree)
                var frames = ConfigureFrameSequence(folder, 421, result);
                if (frames != null && frames.Length > 0)
                    result.FrameSequences[421] = frames;
            }
        }

        static Sprite ConfigureSpriteAsset(string assetPath, int symbolId, ImportResult result)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                result.Warnings.Add($"TextureImporter not found at {assetPath}");
                return null;
            }

            Vector2 pivot = result.Pivots.TryGetValue(symbolId, out var p) ? p : new(0.5f, 0.5f);

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = PixelsPerUnit;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;

            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMode = (int)SpriteImportMode.Single;
            settings.spriteAlignment = (int)SpriteAlignment.Custom;
            settings.spritePivot = pivot;
            importer.SetTextureSettings(settings);
            importer.spritePackingTag = "MainMenu";
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        }

        static Sprite[] ConfigureFrameSequence(string folder, int symbolId, ImportResult result)
        {
            string fullFolder = Path.GetFullPath(folder);
            if (!Directory.Exists(fullFolder))
                return null;

            // Find all imported PNGs in this folder
            var assetGuids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
            if (assetGuids.Length == 0)
                return null;

            var sprites = new List<(int frame, Sprite sprite)>();
            Vector2 pivot = result.Pivots.TryGetValue(symbolId, out var p) ? p : new(0.5f, 0.5f);

            foreach (string guid in assetGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".png")) continue;

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spritePixelsPerUnit = PixelsPerUnit;
                    importer.filterMode = FilterMode.Bilinear;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;

                    var settings = new TextureImporterSettings();
                    importer.ReadTextureSettings(settings);
                    settings.spriteAlignment = (int)SpriteAlignment.Custom;
                    settings.spritePivot = pivot;
                    importer.SetTextureSettings(settings);
                    importer.spritePackingTag = "MainMenu";
                    importer.SaveAndReimport();
                }

                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite != null)
                {
                    int frame = GetFrameNumberFromAsset(path);
                    sprites.Add((frame, sprite));
                }
            }

            return sprites.OrderBy(s => s.frame).Select(s => s.sprite).ToArray();
        }

        // ─── SVG PNG extraction ────────────────────────────────────

        static readonly Regex Base64PngRegex = new(
            @"xlink:href=""data:image/PNG;base64,([^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Shape IDs whose extracted PNGs need alpha-from-brightness post-processing.
        /// Magic glow textures: additive blend in Flash (black = invisible, bright = visible).
        /// </summary>
        static readonly HashSet<int> GlowShapeIds = new() { 441, 462, 465 };

        /// <summary>
        /// Shape IDs that use screen blend + bitmap mask in Flash.
        /// Need aggressive quadratic alpha falloff so dark edges become fully transparent.
        /// </summary>
        static readonly HashSet<int> ScreenBlendShapeIds = new() { 415 };

        /// <summary>
        /// Extracts an embedded base64 PNG from an SVG file written by JPEXS/FFDec.
        /// Returns false if no embedded PNG was found (vector-only shape).
        /// For glow shapes, generates alpha channel from pixel brightness.
        /// For screen-blend shapes, uses quadratic falloff for softer edges.
        /// </summary>
        static bool ExtractPngFromSvg(string svgPath, string outputPngPath, int shapeId = 0)
        {
            string content = File.ReadAllText(svgPath);
            var match = Base64PngRegex.Match(content);
            if (!match.Success)
                return false;

            byte[] pngData = Convert.FromBase64String(match.Groups[1].Value);

            if (GlowShapeIds.Contains(shapeId))
            {
                // Post-process: generate alpha from brightness (max of RGB channels)
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(pngData);
                var pixels = tex.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte brightness = (byte)Mathf.Max(pixels[i].r, Mathf.Max(pixels[i].g, pixels[i].b));
                    pixels[i].a = brightness;
                }
                tex.SetPixels32(pixels);
                tex.Apply();
                File.WriteAllBytes(outputPngPath, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            else if (ScreenBlendShapeIds.Contains(shapeId))
            {
                // Screen blend + mask: quadratic alpha falloff for soft edges.
                // In Flash screen mode, dark pixels contribute nothing. Without the
                // bitmap mask, we fake it with aggressive alpha so edges disappear.
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(pngData);
                var pixels = tex.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    float b = Mathf.Max(pixels[i].r, Mathf.Max(pixels[i].g, pixels[i].b)) / 255f;
                    // Quadratic falloff: dark areas become fully transparent
                    pixels[i].a = (byte)(b * b * 255f);
                }
                tex.SetPixels32(pixels);
                tex.Apply();
                File.WriteAllBytes(outputPngPath, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            else
            {
                File.WriteAllBytes(outputPngPath, pngData);
            }
            return true;
        }

        // ─── Pivot computation ─────────────────────────────────────

        /// <summary>
        /// All sprites use center pivot. Position offsets from SWF bounds are handled
        /// by the composition generator (adds bounds center to placement position).
        /// Center pivot is guaranteed to work in Unity (no clamping issues).
        /// </summary>
        static Vector2 ComputePivot(int symbolId, SWFFile swfData)
        {
            return new Vector2(0.5f, 0.5f);
        }

        // ─── Helpers ───────────────────────────────────────────────

        /// <summary>
        /// Finds an SVG file for a given shape ID, trying multiple naming conventions
        /// across multiple shapes directories.
        /// JPEXS exports use two naming conventions:
        ///   _assets/shapes/ → {id}_symbol{id}.svg  (e.g. 411_symbol411.svg)
        ///   pfe/shapes/     → {id}.svg              (e.g. 411.svg)
        /// </summary>
        static string FindSvgFile(string shapesDir, string jpexsRoot, int shapeId)
        {
            // Collect all candidate shapes directories
            var shapesDirs = new List<string>();
            if (shapesDir != null) shapesDirs.Add(shapesDir);

            // Walk up from jpexsRoot to find other shapes/ folders
            string current = jpexsRoot;
            for (int i = 0; i < 5; i++)
            {
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current)) break;
                string candidate = Path.Combine(current, "shapes");
                if (Directory.Exists(candidate) && !shapesDirs.Contains(candidate))
                    shapesDirs.Add(candidate);
            }

            // Naming conventions to try in each directory
            string[] fileNames =
            {
                $"{shapeId}.svg",
                $"{shapeId}_symbol{shapeId}.svg",
            };

            foreach (string dir in shapesDirs)
            {
                foreach (string name in fileNames)
                {
                    string path = Path.Combine(dir, name);
                    if (File.Exists(path))
                        return path;
                }
            }

            return null;
        }

        static string FindDirectory(string root, string dirName)
        {
            string direct = Path.Combine(root, dirName);
            if (Directory.Exists(direct)) return direct;

            // Walk up parent directories looking for the folder.
            // _assets is inside pfe/scripts/_assets, but shapes/sprites may be in pfe/
            string current = root;
            for (int i = 0; i < 4; i++)
            {
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current)) break;
                string candidate = Path.Combine(current, dirName);
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
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

        static int GetFrameNumberFromAsset(string assetPath)
        {
            string fileName = Path.GetFileNameWithoutExtension(assetPath);
            // Format: f001, f002, etc.
            if (fileName.StartsWith("f") && int.TryParse(fileName.Substring(1), out int num))
                return num;
            if (int.TryParse(fileName, out num))
                return num;
            return 0;
        }
    }
}
#endif
