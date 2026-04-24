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
    /// Imports UI sprites from the Flash SWF for menu buttons, dialogs, and controls.
    ///
    /// Sources:
    /// 1. DefineSprite folders — composite PNGs of dialogs, buttons, skins
    /// 2. DefineButton2 folders — Flash button state PNGs
    /// 3. Shape SVGs — vector shapes with optional embedded raster PNGs
    /// 4. SWF tree walk — discovers button background (.fon) child symbol IDs
    ///
    /// The original Flash UI uses:
    /// - MovieClip buttons with .fon child (background) + text label
    /// - fl.controls widgets: CheckBox, Slider, ColorPicker with skin classes
    /// - DefineButton2 for SimpleButton instances (language selector)
    /// - Dialog MovieClips with tiled dark backgrounds
    /// </summary>
    public static class UIButtonSpriteImporter
    {
        const int PixelsPerUnit = 100;
        const string UIArtRoot = "Assets/_PFE/Art/UI";

        // ─── Known sprite symbol IDs for direct import ──────────

        /// <summary>
        /// Known UI control skin symbols (from fl.controls skin classes).
        /// Discovered from the AS3 source: CheckBox_upIcon (530), etc.
        /// </summary>
        public static readonly Dictionary<int, string> KnownUISkins = new()
        {
            // Checkbox skins
            { 530, "Controls/checkbox_up" },
            { 532, "Controls/checkbox_selected_up" },
            { 533, "Controls/checkbox_over" },
            { 535, "Controls/checkbox_down" },
            { 538, "Controls/checkbox_disabled" },
            { 539, "Controls/checkbox_selected_disabled" },

            // ScrollBar arrow skins
            { 337, "Controls/scrollarrow_up" },
            { 335, "Controls/scrollarrow_disabled" },

            // Slider skins
            { 587, "Controls/slider_thumb_up" },
            { 589, "Controls/slider_thumb_over" },
            { 591, "Controls/slider_thumb_down" },
            { 593, "Controls/slider_thumb_disabled" },
            { 595, "Controls/slider_track" },
            { 597, "Controls/slider_tick" },
            { 599, "Controls/slider_track_disabled" },

            // ColorPicker skins
            { 562, "Controls/colorpicker_up" },
            { 564, "Controls/colorpicker_disabled" },
            { 566, "Controls/colorpicker_swatch" },
            { 571, "Controls/colorpicker_background" },
            { 573, "Controls/colorpicker_down" },
            { 575, "Controls/colorpicker_textfield" },
            { 576, "Controls/colorpicker_over" },
            { 578, "Controls/colorpicker_colorwell" },
            { 580, "Controls/colorpicker_swatch_selected" },

            // TextInput skins
            { 890, "Controls/textinput_disabled" },
            { 892, "Controls/textinput_up" },
        };

        /// <summary>
        /// DefineButton2 symbols — Flash native buttons (language selector, etc.).
        /// Found in JPEXS buttons/ folder.
        /// </summary>
        public static readonly Dictionary<int, string> KnownDefineButtons = new()
        {
            { 405, "Buttons/lang_btn_simple" },
            { 606, "Buttons/dialog_btn_1" },
            { 985, "Buttons/ingame_btn_1" },
            { 987, "Buttons/ingame_btn_2" },
            { 989, "Buttons/ingame_btn_3" },
        };

        /// <summary>
        /// Dialog and composite UI symbols — full dialog composites for reference.
        /// </summary>
        public static readonly Dictionary<int, string> KnownComposites = new()
        {
            { 554, "Dialogs/new_game_dialog" },
            { 607, "Dialogs/appearance_dialog" },
            { 407, "Buttons/lang_button_composite" },
        };

        // ─── SWF tree symbols for button discovery ──────────────

        const int VisMainMenuSymbolId = 559;

        /// <summary>
        /// Instance names of button MovieClips within visMainMenu (559).
        /// We walk the tree: visMainMenu → butXxx → find .fon child.
        /// </summary>
        static readonly Dictionary<string, string> MainMenuButtonInstances = new()
        {
            { "butNewGame", "Buttons/menu_new_game" },
            { "butLoadGame", "Buttons/menu_load_game" },
            { "butContGame", "Buttons/menu_continue" },
            { "butOpt", "Buttons/menu_options" },
            { "butAbout", "Buttons/menu_authors" },
        };

        /// <summary>
        /// Dialog symbol IDs to walk for sub-component discovery.
        /// </summary>
        static readonly Dictionary<int, string> DialogsToWalk = new()
        {
            { 554, "dialNew" },
            { 607, "dialVid" },
        };

        /// <summary>
        /// Instance names within dialogs that are buttons.
        /// </summary>
        static readonly HashSet<string> DialogButtonInstances = new()
        {
            "butOk", "butCancel", "butVid", "butDef",
        };

        // ─── Result types ────────────────────────────────────────

        public class DiscoveredUIElement
        {
            public string Category;      // "Buttons", "Controls", "Dialogs"
            public string Name;          // "menu_new_game_fon", "checkbox_up"
            public int SymbolId;
            public string SourceType;    // "sprite", "button", "shape", "tree_walk"
            public bool HasJpexsExport;
            public int FrameCount;
            public Vector2 Pivot;
        }

        public class ImportResult
        {
            public List<DiscoveredUIElement> Elements = new();

            /// <summary>symbolId → Sprite (first frame or single)</summary>
            public Dictionary<int, Sprite> Sprites = new();

            /// <summary>symbolId → all frame sprites (for multi-state buttons)</summary>
            public Dictionary<int, Sprite[]> FrameSprites = new();

            public int TotalSpritesImported;
            public int TotalElementsDiscovered;
            public List<string> Warnings = new();
            public List<string> Log = new();
        }

        // ─── Import entry point ─────────────────────────────────

        /// <summary>
        /// Import all UI sprites: known skins, DefineButton2, dialog composites,
        /// and tree-walked button backgrounds.
        /// </summary>
        public static ImportResult Import(string jpexsExportRoot, SWFFile swfData)
        {
            var result = new ImportResult();
            string spritesRoot = Path.Combine(jpexsExportRoot, "sprites");
            string buttonsRoot = Path.Combine(jpexsExportRoot, "buttons");
            string shapesRoot = Path.Combine(jpexsExportRoot, "shapes");

            EnsureDirectory(UIArtRoot);

            // Step 1: Discover button .fon symbols from SWF tree
            DiscoverMenuButtons(swfData, spritesRoot, result);

            // Step 2: Discover dialog sub-components from SWF tree
            DiscoverDialogButtons(swfData, spritesRoot, result);

            // Step 3: Catalog known UI skins
            foreach (var kvp in KnownUISkins)
                CatalogElement(kvp.Key, kvp.Value, "sprite", spritesRoot, swfData, result);

            // Step 4: Catalog known DefineButton2s
            foreach (var kvp in KnownDefineButtons)
                CatalogElement(kvp.Key, kvp.Value, "button", buttonsRoot, swfData, result, isButton: true);

            // Step 5: Catalog dialog composites
            foreach (var kvp in KnownComposites)
                CatalogElement(kvp.Key, kvp.Value, "sprite", spritesRoot, swfData, result);

            result.TotalElementsDiscovered = result.Elements.Count;
            result.Log.Add($"Discovered {result.Elements.Count} UI elements:");
            foreach (var elem in result.Elements)
                result.Log.Add($"  {elem.Category}/{elem.Name} = sym {elem.SymbolId} ({elem.SourceType}, {elem.FrameCount} frames, export={elem.HasJpexsExport})");

            // Step 6: Import all elements that have JPEXS exports
            ImportElements(spritesRoot, buttonsRoot, shapesRoot, result);

            return result;
        }

        // ─── Discovery: SWF tree walking ────────────────────────

        /// <summary>
        /// Walk visMainMenu → button instances → find .fon child symbol IDs.
        /// </summary>
        static void DiscoverMenuButtons(SWFFile swfData, string spritesRoot, ImportResult result)
        {
            if (!swfData.Symbols.TryGetValue(VisMainMenuSymbolId, out var menuSymbol))
            {
                result.Warnings.Add($"visMainMenu (symbol {VisMainMenuSymbolId}) not found in SWF");
                return;
            }

            if (menuSymbol.Frames.Count == 0) return;
            var frame1 = menuSymbol.Frames[0];

            foreach (var kvp in MainMenuButtonInstances)
            {
                string instanceName = kvp.Key;
                string outputName = kvp.Value;

                var btnPlacement = frame1.Placements.FirstOrDefault(p => p.InstanceName == instanceName);
                if (btnPlacement == null)
                {
                    result.Warnings.Add($"Menu button '{instanceName}' not found in visMainMenu frame 1");
                    continue;
                }

                int btnSymbolId = btnPlacement.CharacterId;
                result.Log.Add($"  Found {instanceName} → symbol {btnSymbolId}");

                // Walk into button symbol to find .fon child
                if (swfData.Symbols.TryGetValue(btnSymbolId, out var btnSymbol) && btnSymbol.Frames.Count > 0)
                {
                    var btnFrame1 = btnSymbol.Frames[0];

                    // Look for "fon" child (button background)
                    var fonPlacement = btnFrame1.Placements.FirstOrDefault(p => p.InstanceName == "fon");
                    if (fonPlacement != null)
                    {
                        int fonSymbolId = fonPlacement.CharacterId;
                        result.Log.Add($"    {instanceName}.fon → symbol {fonSymbolId}");
                        CatalogElement(fonSymbolId, $"{outputName}_fon", "tree_walk", spritesRoot, swfData, result);
                    }

                    // Also catalog the button composite itself
                    CatalogElement(btnSymbolId, outputName, "tree_walk", spritesRoot, swfData, result);
                }
            }
        }

        /// <summary>
        /// Walk dialog symbols to find butOk, butCancel, butVid, butDef and their .fon children.
        /// </summary>
        static void DiscoverDialogButtons(SWFFile swfData, string spritesRoot, ImportResult result)
        {
            // First, find the dialog symbol IDs from visMainMenu children
            if (!swfData.Symbols.TryGetValue(VisMainMenuSymbolId, out var menuSymbol) || menuSymbol.Frames.Count == 0)
                return;

            var frame1 = menuSymbol.Frames[0];

            foreach (var kvp in DialogsToWalk)
            {
                int dialogSymbolId = kvp.Key;
                string dialogName = kvp.Value;

                // Try direct symbol lookup first
                if (!swfData.Symbols.TryGetValue(dialogSymbolId, out var dialogSymbol))
                {
                    // Try finding via instance name in visMainMenu
                    var dialogPlacement = frame1.Placements.FirstOrDefault(p => p.InstanceName == dialogName);
                    if (dialogPlacement != null)
                    {
                        dialogSymbolId = dialogPlacement.CharacterId;
                        swfData.Symbols.TryGetValue(dialogSymbolId, out dialogSymbol);
                    }
                }

                if (dialogSymbol == null || dialogSymbol.Frames.Count == 0)
                {
                    result.Warnings.Add($"Dialog '{dialogName}' (symbol {dialogSymbolId}): not found or no frames");
                    continue;
                }

                var dialogFrame1 = dialogSymbol.Frames[0];

                // Walk all placements looking for button children
                foreach (var placement in dialogFrame1.Placements)
                {
                    string instName = placement.InstanceName;
                    if (string.IsNullOrEmpty(instName)) continue;

                    if (DialogButtonInstances.Contains(instName))
                    {
                        int childId = placement.CharacterId;
                        string childName = $"Dialogs/{dialogName}_{instName}";
                        result.Log.Add($"  Found {dialogName}.{instName} → symbol {childId}");

                        CatalogElement(childId, childName, "tree_walk", spritesRoot, swfData, result);

                        // Walk into button for .fon
                        if (swfData.Symbols.TryGetValue(childId, out var childSym) && childSym.Frames.Count > 0)
                        {
                            var fonPlacement = childSym.Frames[0].Placements
                                .FirstOrDefault(p => p.InstanceName == "fon");
                            if (fonPlacement != null)
                            {
                                CatalogElement(fonPlacement.CharacterId, $"{childName}_fon",
                                    "tree_walk", spritesRoot, swfData, result);
                            }
                        }
                    }

                    // Also look for "fon" (dialog background) at the dialog level
                    if (instName == "fon")
                    {
                        CatalogElement(placement.CharacterId, $"Dialogs/{dialogName}_background",
                            "tree_walk", spritesRoot, swfData, result);
                    }
                }
            }
        }

        /// <summary>
        /// Add a UI element to the catalog, checking if it has a JPEXS export.
        /// </summary>
        static void CatalogElement(int symbolId, string name, string sourceType,
            string searchRoot, SWFFile swfData, ImportResult result, bool isButton = false)
        {
            // Skip duplicates
            if (result.Elements.Any(e => e.SymbolId == symbolId))
                return;

            bool hasExport = false;
            int frameCount = 0;

            if (isButton)
            {
                // DefineButton2 — look in buttons/ folder
                string btnFolder = Path.Combine(searchRoot, $"DefineButton2_{symbolId}_symbol{symbolId}");
                if (Directory.Exists(btnFolder))
                {
                    frameCount = Directory.GetFiles(btnFolder, "*.png").Length;
                    hasExport = frameCount > 0;
                }
            }
            else
            {
                // DefineSprite — look in sprites/ folder
                string sprFolder = Path.Combine(searchRoot, $"DefineSprite_{symbolId}_symbol{symbolId}");
                if (Directory.Exists(sprFolder))
                {
                    frameCount = Directory.GetFiles(sprFolder, "*.png").Length;
                    hasExport = frameCount > 0;
                }
            }

            // Determine category from name path
            string category = "Other";
            if (name.StartsWith("Buttons/")) category = "Buttons";
            else if (name.StartsWith("Controls/")) category = "Controls";
            else if (name.StartsWith("Dialogs/")) category = "Dialogs";

            // Compute pivot
            Vector2 pivot = ComputePivot(symbolId, swfData);

            result.Elements.Add(new DiscoveredUIElement
            {
                Category = category,
                Name = name,
                SymbolId = symbolId,
                SourceType = sourceType,
                HasJpexsExport = hasExport,
                FrameCount = frameCount,
                Pivot = pivot,
            });
        }

        // ─── Import: copy PNGs and configure ────────────────────

        static void ImportElements(string spritesRoot, string buttonsRoot, string shapesRoot, ImportResult result)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var elem in result.Elements)
                {
                    if (!elem.HasJpexsExport)
                        continue;

                    string sourceFolder;
                    if (elem.SourceType == "button")
                    {
                        sourceFolder = Path.Combine(buttonsRoot, $"DefineButton2_{elem.SymbolId}_symbol{elem.SymbolId}");
                    }
                    else
                    {
                        sourceFolder = Path.Combine(spritesRoot, $"DefineSprite_{elem.SymbolId}_symbol{elem.SymbolId}");
                    }

                    if (!Directory.Exists(sourceFolder))
                        continue;

                    var pngFiles = Directory.GetFiles(sourceFolder, "*.png")
                        .OrderBy(f => GetFrameNumber(f))
                        .ToArray();

                    if (pngFiles.Length == 0) continue;

                    string destFolder = Path.Combine(UIArtRoot, Path.GetDirectoryName(elem.Name));
                    EnsureDirectory(destFolder);

                    string baseName = Path.GetFileName(elem.Name);

                    foreach (string pngPath in pngFiles)
                    {
                        int frameNum = GetFrameNumber(pngPath);
                        string destFileName = pngFiles.Length > 1
                            ? $"{baseName}_f{frameNum:D2}.png"
                            : $"{baseName}.png";
                        string destAssetPath = Path.Combine(destFolder, destFileName);
                        string destFullPath = Path.GetFullPath(destAssetPath);

                        if (!File.Exists(destFullPath) ||
                            File.GetLastWriteTimeUtc(pngPath) > File.GetLastWriteTimeUtc(destFullPath))
                        {
                            File.Copy(pngPath, destFullPath, true);
                        }

                        result.TotalSpritesImported++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh();

            // Configure import settings and collect sprite references
            ConfigureImportSettings(result);
        }

        static void ConfigureImportSettings(ImportResult result)
        {
            foreach (var elem in result.Elements)
            {
                if (!elem.HasJpexsExport) continue;

                string destFolder = Path.Combine(UIArtRoot, Path.GetDirectoryName(elem.Name));
                string baseName = Path.GetFileName(elem.Name);

                // Find all imported PNGs for this element
                string fullFolder = Path.GetFullPath(destFolder);
                if (!Directory.Exists(fullFolder)) continue;

                var pngPaths = Directory.GetFiles(fullFolder, $"{baseName}*.png")
                    .OrderBy(f => f)
                    .ToArray();

                var sprites = new List<Sprite>();

                foreach (string pngFullPath in pngPaths)
                {
                    // Convert full path to Unity asset path
                    string relativePath = pngFullPath
                        .Replace(Path.GetFullPath("Assets"), "Assets")
                        .Replace('\\', '/');

                    // Try to get asset path by finding relative to project
                    string projectRoot = Path.GetFullPath(".");
                    if (pngFullPath.StartsWith(projectRoot))
                        relativePath = pngFullPath.Substring(projectRoot.Length + 1).Replace('\\', '/');

                    var importer = AssetImporter.GetAtPath(relativePath) as TextureImporter;
                    if (importer == null) continue;

                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.spritePixelsPerUnit = PixelsPerUnit;
                    importer.filterMode = FilterMode.Bilinear;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;

                    var settings = new TextureImporterSettings();
                    importer.ReadTextureSettings(settings);
                    settings.spriteMode = (int)SpriteImportMode.Single;
                    settings.spriteAlignment = (int)SpriteAlignment.Custom;
                    settings.spritePivot = new Vector2(
                        Mathf.Clamp01(elem.Pivot.x),
                        Mathf.Clamp01(elem.Pivot.y));
                    importer.SetTextureSettings(settings);
                    importer.spritePackingTag = $"UI_{elem.Category}";
                    importer.SaveAndReimport();

                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(relativePath);
                    if (sprite != null)
                        sprites.Add(sprite);
                }

                if (sprites.Count > 0)
                {
                    result.Sprites[elem.SymbolId] = sprites[0];
                    if (sprites.Count > 1)
                        result.FrameSprites[elem.SymbolId] = sprites.ToArray();
                }
            }
        }

        // ─── Helpers ────────────────────────────────────────────

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
