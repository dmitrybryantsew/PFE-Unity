#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports the three shield domes — <c>visShit</c> (3625), <c>visShit2</c> (1903) and
    /// <c>visShit3</c> (1900) — into <c>Art/Units/ShieldOverlays</c> and (re)builds the
    /// <see cref="UnitShieldOverlayDefinition"/> asset that <c>UnitShieldOverlay</c> reads.
    ///
    /// <para><b>Why this is a separate command from the character import.</b> The player's
    /// <c>PFE/Art/Import Character From SWF</c> brings in symbol 3625 only, as the <c>shit</c> overlay of
    /// the player's body composition — and it is the <i>same symbol</i> the alicorn's ordinary dome uses,
    /// which is why the ordinary dome already draws today. The other two symbols are instantiated by
    /// unit classes rather than by the player, so the character importer has no reason to know them, and
    /// routing them through it would put an enemy's art in the player's asset.</para>
    ///
    /// <para><b>Frames are configured as single sprites</b> (PPU 100, centre pivot, point filter) — the
    /// same shape <c>UnitSpriteImporter</c> uses for a DisplayObject visual, and the shape the existing
    /// <c>Overlays/shit</c> frames are read back through. A dome is a standalone clip with one frame per
    /// PNG, so there is no sheet to slice and no grid to keep in step.</para>
    ///
    /// <para><b>Re-running is safe and is the point.</b> The frames are copied over, each clip's
    /// presentation values (<c>localPosition</c>, <c>localScale</c>, <c>sortingOrder</c>,
    /// <c>loopMode</c>) are preserved from the existing asset when it already carried frames, and a
    /// symbol whose source folder is missing is skipped with a warning rather than emptying a clip that
    /// used to work.</para>
    /// </summary>
    public static class UnitShieldOverlayImporter
    {
        /// <summary>Where the dome frames land. Deliberately outside <c>Character/Overlays</c>, which is
        /// the player composition's art root.</summary>
        public const string OverlayArtRoot = "Assets/_PFE/Art/Units/ShieldOverlays";

        /// <summary>The asset the runtime loads — <c>Resources/Characters/UnitShieldOverlayDefinition</c>.</summary>
        public const string DefinitionAssetPath =
            "Assets/_PFE/Data/Resources/Characters/UnitShieldOverlayDefinition.asset";

        const int MaxTextureSize = 8192;
        const int PixelsPerUnit = 100;

        /// <summary>
        /// The three symbols, from <c>visShit.as</c>/<c>visShit2.as</c>/<c>visShit3.as</c>
        /// (<c>[Embed(source="/_assets/assets.swf", symbol="symbol…")]</c>) and
        /// <c>pfe/symbolClass/symbols.csv</c> (<c>3625;"visShit"</c>, <c>1903;"visShit2"</c>,
        /// <c>1900;"visShit3"</c>).
        /// </summary>
        static readonly (int SymbolId, string Name)[] Symbols =
        {
            (3625, "visShit"),
            (1903, "visShit2"),
            (1900, "visShit3"),
        };

        [MenuItem("PFE/Art/Import Unit Shield Overlays")]
        public static void Import()
        {
            if (!SourceImportPaths.IsResolved)
            {
                Debug.LogError("[UnitShieldOverlayImporter] " +
                               SourceImportPaths.MissingSourceMessage(
                                   SourceImportPaths.SourceProjectRoot, "the AS3 source root"));
                return;
            }

            var definition = LoadOrCreateDefinition();

            int imported = 0;
            int skipped = 0;

            foreach ((int symbolId, string name) in Symbols)
            {
                Sprite[] frames = ImportSymbol(symbolId, name);

                if (frames == null || frames.Length == 0)
                {
                    // Deliberately does NOT clear the clip. A missing source folder means the export is
                    // not where this command expects it, and emptying a clip that used to draw would turn
                    // "the import path moved" into "the shield silently stopped rendering".
                    Debug.LogWarning(
                        $"[UnitShieldOverlayImporter] {name} (symbol {symbolId}) imported no frames; " +
                        "leaving its clip untouched. Expected a folder at " +
                        SourceImportPaths.PfeSpriteFolder(symbolId, name));
                    skipped++;
                    continue;
                }

                AssignClip(definition, name, frames);
                Debug.Log($"[UnitShieldOverlayImporter] {name} (symbol {symbolId}): {frames.Length} frames");
                imported++;
            }

            EditorUtility.SetDirty(definition);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[UnitShieldOverlayImporter] Done — {imported} dome(s) imported, {skipped} skipped. " +
                      $"Asset: {DefinitionAssetPath}");
        }

        /// <summary>
        /// Copy one symbol's frames in, configure them, and return them in frame order.
        /// </summary>
        /// <returns>Null when the source folder is absent or holds no PNGs.</returns>
        static Sprite[] ImportSymbol(int symbolId, string name)
        {
            string sourceFolder = SourceImportPaths.PfeSpriteFolder(symbolId, name);
            if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            {
                return null;
            }

            // Frame files are 1.png, 2.png, … — sorted numerically, not lexically, or frame 10 lands
            // between 1 and 2.
            string[] pngs = Directory.GetFiles(sourceFolder, "*.png")
                .OrderBy(UnitSheetLayout.FrameNumberOf)
                .ToArray();

            if (pngs.Length == 0)
            {
                return null;
            }

            string targetFolder = $"{OverlayArtRoot}/{name}";
            EnsureDirectory(targetFolder);

            foreach (string png in pngs)
            {
                int frame = UnitSheetLayout.FrameNumberOf(png);
                if (frame <= 0)
                {
                    continue;
                }

                CopyIfNewer(png, $"{targetFolder}/f{frame:D3}.png");
            }

            AssetDatabase.ImportAsset(targetFolder, ImportAssetOptions.ImportRecursive);

            var frames = new List<Sprite>();

            string[] assets = AssetDatabase.FindAssets("t:Sprite", new[] { targetFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(UnitSheetLayout.FrameNumberOf)
                .ToArray();

            foreach (string assetPath in assets)
            {
                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer == null)
                {
                    continue;
                }

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = PixelsPerUnit;
                importer.filterMode = FilterMode.Point;          // pixel art: no bilinear blur
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.maxTextureSize = MaxTextureSize;

                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteAlignment = (int)SpriteAlignment.Center;
                importer.SetTextureSettings(settings);

                importer.SaveAndReimport();

                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                if (sprite != null)
                {
                    frames.Add(sprite);
                }
                else
                {
                    Debug.LogWarning($"[UnitShieldOverlayImporter] {assetPath} did not load as a Sprite.");
                }
            }

            return frames.Count > 0 ? frames.ToArray() : null;
        }

        /// <summary>
        /// Point one of the definition's three clips at freshly imported frames, keeping whatever
        /// presentation the asset already had.
        /// </summary>
        /// <remarks>
        /// <para><b>The presentation defaults are the oracle's own numbers, not guesses.</b>
        /// <c>UnitAlicorn.as:202-203</c> sets <c>visshit.y = -50</c> and
        /// <c>scaleX = scaleY = 1.5</c> — 50 px above the feet at 1.5×, which in the port's units is
        /// <c>+0.5</c> on the Y axis (the port's up is positive, AS3's is negative). <c>loopMode</c> is
        /// <c>ClampForever</c> because <c>visShit</c>'s own frame script calls <c>stop()</c> on frame 20
        /// (<c>visShit.as:14-21</c>): the dome materialises and then holds.</para>
        ///
        /// <para><b>Existing values win.</b> The clip's <c>localPosition</c>/<c>localScale</c>/
        /// <c>sortingOrder</c>/<c>loopMode</c> are only written when the clip had no frames — i.e. when
        /// this run is the one bringing it into existence. A re-import after someone tuned the dome in
        /// the inspector must not silently undo that tuning, and the frames are the only part of the clip
        /// this command is authoritative about.</para>
        /// </remarks>
        static void AssignClip(UnitShieldOverlayDefinition definition, string name, Sprite[] frames)
        {
            CharacterOverlayDefinition existing = name switch
            {
                "visShit" => definition.normal,
                "visShit2" => definition.large,
                "visShit3" => definition.boss,
                _ => null,
            };

            bool fresh = existing == null || existing.frames == null || existing.frames.Length == 0;

            var clip = fresh ? new CharacterOverlayDefinition() : existing;

            clip.frames = frames;

            if (fresh)
            {
                clip.overlayName = name switch
                {
                    "visShit" => "shit",
                    "visShit2" => "shit2",
                    "visShit3" => "shit3",
                    _ => name,
                };
                clip.pivotNormalized = new Vector2(0.5f, 0.5f);
                clip.localPosition = new Vector2(0f, 0.5f);
                clip.localScale = 1.5f;
                clip.sortingOrder = 1000;
                clip.loopMode = AnimationLoopMode.ClampForever;
            }

            switch (name)
            {
                case "visShit": definition.normal = clip; break;
                case "visShit2": definition.large = clip; break;
                case "visShit3": definition.boss = clip; break;
            }
        }

        static UnitShieldOverlayDefinition LoadOrCreateDefinition()
        {
            var definition = AssetDatabase.LoadAssetAtPath<UnitShieldOverlayDefinition>(DefinitionAssetPath);
            if (definition != null)
            {
                return definition;
            }

            EnsureDirectory(Path.GetDirectoryName(DefinitionAssetPath).Replace('\\', '/'));

            definition = ScriptableObject.CreateInstance<UnitShieldOverlayDefinition>();
            AssetDatabase.CreateAsset(definition, DefinitionAssetPath);
            return definition;
        }

        static void CopyIfNewer(string source, string destAssetPath)
        {
            string destFullPath = Path.GetFullPath(destAssetPath);
            EnsureDirectory(Path.GetDirectoryName(destFullPath).Replace('\\', '/'));

            if (!File.Exists(destFullPath) ||
                File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(destFullPath))
            {
                File.Copy(source, destFullPath, true);
            }
        }

        static void EnsureDirectory(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || AssetDatabase.IsValidFolder(assetPath))
            {
                return;
            }

            string fullPath = Path.GetFullPath(assetPath);
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }
        }
    }
}
#endif
