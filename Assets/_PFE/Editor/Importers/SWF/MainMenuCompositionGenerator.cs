#if UNITY_EDITOR
using System.IO;
using System.Linq;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Generates a MainMenuCompositionDefinition ScriptableObject from parsed SWF data
    /// and imported sprites. Fills in positions from SWF placements and wires up sprite references.
    ///
    /// POSITIONING: All sprites use center (0.5, 0.5) pivot. To correctly position them,
    /// each stored position = SWF placement position + shape bounds center.
    /// This converts from "registration point position" (Flash) to "image center position" (Unity).
    /// Group/transform positions remain as raw Flash placement positions.
    /// </summary>
    public static class MainMenuCompositionGenerator
    {
        const string OutputPath = "Assets/_PFE/Data/Resources/MainMenu/MainMenuComposition.asset";
        const int VisMainMenuSymbolId = 559;

        /// <summary>
        /// Generate or update the MainMenuCompositionDefinition from import results.
        /// </summary>
        public static MainMenuCompositionDefinition Generate(
            SWFFile swfData,
            MainMenuSpriteImporter.ImportResult importResult)
        {
            // Ensure output directory
            string dir = Path.GetDirectoryName(OutputPath);
            if (!Directory.Exists(Path.GetFullPath(dir)))
                Directory.CreateDirectory(Path.GetFullPath(dir));

            // Load or create the asset
            var def = AssetDatabase.LoadAssetAtPath<MainMenuCompositionDefinition>(OutputPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<MainMenuCompositionDefinition>();
                AssetDatabase.CreateAsset(def, OutputPath);
            }

            Debug.Log($"[Generator] SWF Stage: {swfData.StageRect}, FrameRate: {swfData.FrameRate}");

            // ─── Background layers ──────────────────────────────────
            // Sprites: position = placement + bounds center (for center pivot)
            def.sky = GetSprite(importResult, 411);
            def.skyPosition = GetSpritePosition(swfData, VisMainMenuSymbolId, 411);

            def.cityScene = GetSprite(importResult, 425);
            def.cityScenePosition = GetSpritePosition(swfData, VisMainMenuSymbolId, 425);

            def.logo = GetSprite(importResult, 471);
            def.logoPosition = GetSpritePosition(swfData, VisMainMenuSymbolId, 471);

            Debug.Log($"[Generator] Sky pos: {def.skyPosition}, City pos: {def.cityScenePosition}, Logo pos: {def.logoPosition}");

            // ─── Pipka position ─────────────────────────────────────
            // Group position: raw Flash placement (no bounds offset)
            var pipkaPlacement = FindPlacement(swfData, VisMainMenuSymbolId, "pipka");
            if (pipkaPlacement != null)
                def.pipkaPosition = pipkaPlacement.Position;

            // ─── Pipka children positions ───────────────────────────
            int pipkaSymbolId = pipkaPlacement?.CharacterId ?? 469;

            // Eye: sprite position (placement + bounds center)
            var eyePlacement = FindPlacement(swfData, pipkaSymbolId, "eye");
            if (eyePlacement != null)
                def.eyeLocalPos = eyePlacement.Position + BoundsCenter(swfData, 432);

            // Horn group: transform position (raw placement)
            var hornPlacement = FindPlacement(swfData, pipkaSymbolId, "horn");
            if (hornPlacement != null)
                def.hornLocalPos = hornPlacement.Position;

            // Pistol group: transform position (raw placement)
            var pistolPlacement = FindPlacement(swfData, pipkaSymbolId, "pistol");
            if (pistolPlacement != null)
                def.pistolLocalPos = pistolPlacement.Position;

            // ─── Static body pieces ─────────────────────────────────
            // Sprite positions: placement + bounds center
            def.hornPiece = GetSprite(importResult, 437);
            def.hornPieceLocalPos = GetSpritePosition(swfData, pipkaSymbolId, 437);

            def.earPiece = GetSprite(importResult, 455);
            def.earPieceLocalPos = GetSpritePosition(swfData, pipkaSymbolId, 455);

            def.pistolBody = GetSprite(importResult, 457);

            Debug.Log($"[Generator] Pipka pos: {def.pipkaPosition}, Eye: {def.eyeLocalPos}, " +
                      $"Horn piece: {def.hornPieceLocalPos}, Ear piece: {def.earPieceLocalPos}");

            // ─── Displacement targets ───────────────────────────────
            // Shapes 434/451 are inside wrapper sprites 435/452, both placed at (0,0) in pipka
            // Position = wrapper placement (0,0) + shape bounds center
            def.displacementMane = GetSprite(importResult, 434);
            def.displacementManeLocalPos = BoundsCenter(swfData, 434);
            def.displacementTail = GetSprite(importResult, 451);
            def.displacementTailLocalPos = BoundsCenter(swfData, 451);

            // ─── Magic circles ──────────────────────────────────────
            def.magicKrug = GetSprite(importResult, 443);
            def.hornMagicGlow = GetSprite(importResult, 441);
            def.pistolMagicGlow = GetSprite(importResult, 462);
            def.pistolMagic2Glow = GetSprite(importResult, 465);

            // Horn krug scale from SWF: horn → magic → krug has scale=0.58
            def.hornKrugScale = 0.58f;

            // ─── Eye blink frames ───────────────────────────────────
            def.eyeFrames = GetFrames(importResult, 432);

            // ─── Spark frames and instances ─────────────────────────
            def.hornSparkFrames = GetFrames(importResult, 448);
            def.pistolSparkFrames = GetFrames(importResult, 467);

            // Horn spark instance positions (from SWF: 4 instances of symbol 448 inside horn)
            def.hornSparkInstances = new SparkInstance[]
            {
                new(new Vector2(-10.9f, -7.6f)),
                new(new Vector2(4.9f, -3.9f)),
                new(new Vector2(11.4f, 6.6f)),
                new(new Vector2(-2.1f, 3.9f)),
            };

            // Pistol spark instances (7 instances of symbol 467 inside pistol)
            def.pistolSparkInstances = new SparkInstance[]
            {
                new(new Vector2(7.5f, -25.6f)),
                new(new Vector2(-9.3f, 5.5f)),
                new(new Vector2(8.7f, 32.0f)),
                new(new Vector2(2.5f, 15.2f)),
                new(new Vector2(-4.6f, -20.3f)),
                new(new Vector2(7.5f, -1.3f)),
                new(new Vector2(-10.8f, -10.1f)),
            };

            // ─── Lightning ──────────────────────────────────────────
            def.lightningClouds = GetSprite(importResult, 415);
            def.lightningBoltFrames = GetFrames(importResult, 421);

            // Groza group: transform position (raw placement)
            var grozaPlacement = FindPlacement(swfData, VisMainMenuSymbolId, "groza");
            if (grozaPlacement != null)
                def.grozaDefaultPos = grozaPlacement.Position;

            // Lightning clouds: sprite position within groza (placement + bounds center)
            int grozaSymbolId = grozaPlacement?.CharacterId ?? 423;
            def.lightningCloudsLocalPos = GetSpritePosition(swfData, grozaSymbolId, 415);

            // Moln (bolt) position within groza
            var molnPlacement = FindPlacement(swfData, grozaSymbolId, "moln");
            if (molnPlacement != null)
                def.lightningBoltLocalPos = molnPlacement.Position;

            // Maska (cloud clip mask) — extract bounds for runtime clipping
            var maskaPlacement = FindPlacement(swfData, grozaSymbolId, "maska");
            if (maskaPlacement != null)
            {
                int maskaId = maskaPlacement.CharacterId;
                Rect maskBounds = default;
                if (swfData.ShapeBounds.TryGetValue(maskaId, out maskBounds) ||
                    swfData.Frame1Bounds.TryGetValue(maskaId, out maskBounds) ||
                    (swfData.Symbols.TryGetValue(maskaId, out var maskSym) && (maskBounds = maskSym.Bounds).width > 0))
                {
                    // Store mask rect in groza-local Flash coords
                    def.lightningMaskRect = new Rect(
                        maskaPlacement.Position.x + maskBounds.x,
                        maskaPlacement.Position.y + maskBounds.y,
                        maskBounds.width,
                        maskBounds.height);
                }
                Debug.Log($"[Generator] Maska: placement={maskaPlacement.Position}, bounds={maskBounds}, " +
                          $"maskRect={def.lightningMaskRect}");
            }

            Debug.Log($"[Generator] Groza pos: {def.grozaDefaultPos}, Clouds local: {def.lightningCloudsLocalPos}, " +
                      $"Bolt local: {def.lightningBoltLocalPos}");

            // ─── Animation parameters (from Displ.as) ───────────────
            def.frameRate = swfData.FrameRate > 0 ? swfData.FrameRate : 30f;
            def.blinkIntervalMin = 60f / def.frameRate;
            def.blinkIntervalMax = 170f / def.frameRate;
            def.pistolBobSpeed = 100f;
            def.pistolBobAmplitudeX = 2f;
            def.pistolBobAmplitudeY = 8f;
            def.magicRotSpeed1 = 1f;
            def.magicRotSpeed2 = 0.67f;
            def.lightningIntervalMin = 100f / def.frameRate;
            def.lightningIntervalMax = 300f / def.frameRate;
            def.lightningBurstFrames = 30;
            def.lightningSpawnWidth = 1800f;
            def.lightningSpawnHeight = 350f;

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();

            Debug.Log($"[MainMenuCompositionGenerator] Generated {OutputPath}");
            return def;
        }

        // ─── Helpers ───────────────────────────────────────────────

        /// <summary>
        /// Get the center-pivot-adjusted position for a shape/sprite placed within a parent.
        /// Returns placement.Position + BoundsCenter of the placed character.
        /// For center (0.5, 0.5) pivot, this gives the correct Unity position via FlashToUnity().
        /// </summary>
        static Vector2 GetSpritePosition(SWFFile swfData, int parentSymbolId, int characterId)
        {
            var placement = FindPlacementByCharId(swfData, parentSymbolId, characterId);
            Vector2 pos = placement?.Position ?? Vector2.zero;
            return pos + BoundsCenter(swfData, characterId);
        }

        /// <summary>
        /// Get the center of a shape/sprite's bounds in Flash coordinates.
        /// For center-pivot sprites, adding this to placement position gives the correct render position.
        /// Returns (0,0) for centered shapes (bounds.x=-half, bounds.y=-half) since center is at origin.
        /// </summary>
        static Vector2 BoundsCenter(SWFFile swfData, int symbolId)
        {
            Rect bounds;
            if (swfData.ShapeBounds.TryGetValue(symbolId, out bounds) && bounds.width > 0)
                return new Vector2(bounds.x + bounds.width * 0.5f, bounds.y + bounds.height * 0.5f);
            if (swfData.Frame1Bounds.TryGetValue(symbolId, out var f1b) && f1b.width > 0)
                return new Vector2(f1b.x + f1b.width * 0.5f, f1b.y + f1b.height * 0.5f);
            if (swfData.Symbols.TryGetValue(symbolId, out var sym) && sym.Bounds.width > 0)
            {
                bounds = sym.Bounds;
                return new Vector2(bounds.x + bounds.width * 0.5f, bounds.y + bounds.height * 0.5f);
            }
            return Vector2.zero;
        }

        static Sprite GetSprite(MainMenuSpriteImporter.ImportResult result, int symbolId)
        {
            if (result.SingleSprites.TryGetValue(symbolId, out var sprite))
                return sprite;
            if (result.FrameSequences.TryGetValue(symbolId, out var frames) && frames.Length > 0)
                return frames[0];
            return null;
        }

        static Sprite[] GetFrames(MainMenuSpriteImporter.ImportResult result, int symbolId)
        {
            if (result.FrameSequences.TryGetValue(symbolId, out var frames))
                return frames;
            return System.Array.Empty<Sprite>();
        }

        /// <summary>
        /// Find a named placement within a symbol's frame 1 display list.
        /// </summary>
        static SWFPlacement FindPlacement(SWFFile swfData, int parentSymbolId, string instanceName)
        {
            if (!swfData.Symbols.TryGetValue(parentSymbolId, out var symbol))
                return null;
            if (symbol.Frames.Count == 0)
                return null;

            return symbol.Frames[0].Placements
                .FirstOrDefault(p => p.InstanceName == instanceName);
        }

        /// <summary>
        /// Find a placement by character ID within a symbol's frame 1 display list.
        /// </summary>
        static SWFPlacement FindPlacementByCharId(SWFFile swfData, int parentSymbolId, int characterId)
        {
            if (!swfData.Symbols.TryGetValue(parentSymbolId, out var symbol))
                return null;
            if (symbol.Frames.Count == 0)
                return null;

            return symbol.Frames[0].Placements
                .FirstOrDefault(p => p.CharacterId == characterId);
        }
    }
}
#endif
