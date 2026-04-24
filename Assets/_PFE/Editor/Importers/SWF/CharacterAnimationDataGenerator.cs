#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Generates CharacterAnimationDefinition ScriptableObject from parsed SWF data
    /// and imported sprite references.
    /// </summary>
    public static class CharacterAnimationDataGenerator
    {
        const string OutputPath = "Assets/_PFE/Data/Resources/Characters";
        const string AssetName = "PlayerAnimationDefinition.asset";

        /// <summary>
        /// Known states that use run mane/tail instead of idle variants.
        /// Confirmed from SWF: run uses run mane+tail on all frames,
        /// roll uses run mane on frames 1-13 then switches back.
        /// </summary>
        static readonly HashSet<string> RunManeStates = new() { "run", "roll" };
        static readonly HashSet<string> RunTailStates = new() { "run" };

        /// <summary>
        /// Loop mode overrides per state, derived from AS3 wrapper classes and UnitPlayer.as behavior.
        /// </summary>
        static readonly Dictionary<string, (AnimationLoopMode mode, int loopStart, int loopEnd)> LoopOverrides = new()
        {
            { "stay",    (AnimationLoopMode.Manual, 0, -1) },       // Pose bank, code picks frame
            { "walk",    (AnimationLoopMode.Loop, 0, 26) },         // Loops at frame 26
            { "trot",    (AnimationLoopMode.Loop, 0, 18) },         // Loops at frame 18
            { "trot_up", (AnimationLoopMode.Loop, 0, 18) },
            { "trot_down", (AnimationLoopMode.Loop, 0, 18) },
            { "run",     (AnimationLoopMode.Loop, 0, -1) },         // Full 8-frame loop
            { "jump",    (AnimationLoopMode.LoopRange, 10, 25) },   // Loops frames 11-25 (0-based: 10-24)
            { "levit",   (AnimationLoopMode.LoopRange, 16, 50) },   // Main hover loop 17-50
            { "laz",     (AnimationLoopMode.Manual, 1, 13) },       // Ladder: manually stepped
            { "plav",    (AnimationLoopMode.Loop, 0, -1) },         // Swim loop
            { "punch",   (AnimationLoopMode.ClampForever, 0, -1) }, // Stops at end
            { "kick",    (AnimationLoopMode.ClampForever, 0, -1) },
            { "die",     (AnimationLoopMode.ClampForever, 0, -1) },
            { "dieali",  (AnimationLoopMode.ClampForever, 0, -1) },
            { "res",     (AnimationLoopMode.ClampForever, 0, -1) },
            { "roll",    (AnimationLoopMode.ClampForever, 0, -1) }, // Transitions to polz via code
            { "polz",    (AnimationLoopMode.Loop, 0, -1) },
            { "pinok",   (AnimationLoopMode.ClampForever, 0, -1) },
            { "free1",   (AnimationLoopMode.Loop, 0, -1) },
            { "free2",   (AnimationLoopMode.Loop, 0, -1) },
            { "free3",   (AnimationLoopMode.Loop, 0, -1) },
            { "lurk1",   (AnimationLoopMode.Loop, 0, -1) },
            { "lurk2",   (AnimationLoopMode.Loop, 0, -1) },
            { "lurk3",   (AnimationLoopMode.Loop, 0, -1) },
        };

        public class GenerationResult
        {
            public CharacterAnimationDefinition Asset;
            public int StatesGenerated;
            public int PartsRegistered;
            public List<string> Warnings = new();
        }

        public static GenerationResult Generate(
            SWFFile swfData,
            CharacterSpriteImporter.ImportResult spriteImport,
            HeadArmorVariantData headVariants = null)
        {
            var result = new GenerationResult();

            // Ensure output directory
            string fullOutputPath = System.IO.Path.GetFullPath(OutputPath);
            if (!System.IO.Directory.Exists(fullOutputPath))
                System.IO.Directory.CreateDirectory(fullOutputPath);

            string assetPath = $"{OutputPath}/{AssetName}";
            var definition = AssetDatabase.LoadAssetAtPath<CharacterAnimationDefinition>(assetPath);
            if (definition == null)
            {
                definition = ScriptableObject.CreateInstance<CharacterAnimationDefinition>();
                AssetDatabase.CreateAsset(definition, assetPath);
            }

            definition.characterId = "player";
            definition.frameRate = 30f;
            definition.hairStyleCount = 5;
            definition.eyeStyleCount = 6;

            // Build parts list from known body parts
            var partsList = BuildPartDefinitions(swfData, spriteImport);
            definition.parts = partsList.ToArray();
            result.PartsRegistered = partsList.Count;

            // Build part index lookup for frame data
            var partIndexLookup = new Dictionary<int, int>(); // symbolId → index in parts array
            for (int i = 0; i < partsList.Count; i++)
                partIndexLookup[partsList[i].symbolId] = i;

            // Build state clips from osn timeline
            var stateClips = BuildStateClips(swfData, partIndexLookup);
            definition.stateClips = stateClips.ToArray();
            result.StatesGenerated = stateClips.Count;

            // Build armor sets using SWF frame labels for correct per-part mapping
            // Include head sub-part overrides from morda_armor(273) frame analysis
            var armorSets = BuildArmorSets(swfData, spriteImport, partIndexLookup, partsList, headVariants);
            definition.armorSets = armorSets.ToArray();

            EditorUtility.SetDirty(definition);
            AssetDatabase.SaveAssets();

            result.Asset = definition;
            result.Warnings.AddRange(spriteImport.Warnings);
            return result;
        }

        static List<CharacterPartDefinition> BuildPartDefinitions(
            SWFFile swfData,
            CharacterSpriteImporter.ImportResult spriteImport)
        {
            var parts = new List<CharacterPartDefinition>();

            // Add parts in the confirmed depth order from SWF research
            var orderedParts = new (int symbolId, string name, int sortOrder)[]
            {
                (3640, "lwing", 0),
                (24, "sleg3a", 1),
                (319, "sleg3", 2),
                (55, "sleg1", 3),      // hind leg upper (back)
                (78, "fleg1", 4),      // front leg 1 (back)
                (101, "fleg2", 5),     // front leg 2 (back)
                (124, "fleg3", 6),     // front hoof (back)
                (297, "fleg3a", 7),    // front hoof variant (back)
                (513, "pip", 8),       // pip marker
                (136, "tail", 9),
                (692, "tail_run", 10),
                (159, "korpus", 11),
                (182, "neck", 12),
                (195, "mane", 13),
                (734, "mane_run", 14),
                // Head sub-model: decomposed into individual sub-parts
                // The head composite (274) is kept for reference but sub-parts render individually
                (274, "head", 15),         // head composite (hidden when sub-parts active)
                (199, "morda_base", 16),   // face base shape (3 frames: mouth expressions)
                (273, "morda_armor", 17),  // armor face overlay (16 frames: composites per armor)
                (235, "morda_overlay", 18),// face detail overlay
                (220, "eye", 19),          // eyes (4 frames: expressions)
                (233, "forelock", 20),     // front hair tuft
                (237, "horn", 21),         // unicorn horn
                (239, "magic", 22),        // magic aura effect (invisible by default)
                (240, "konec", 23),        // horn tip effect (1x1, empty by default)
                (31, "helm", 24),          // helmet overlay (1x1, empty by default)
                // Note: front-side legs reuse same symbols at higher sort order
                // The actual sort order per-frame comes from the SWF placement depth
                (3650, "rwing", 25),
            };

            // Parts that should NOT show baseSprite by default:
            // - Head composite (274): replaced by individual sub-parts
            // - morda_armor (273): composite with baked-in eyes/hair, only for armor mode
            // - helm (31), konec (240), magic (239): 1x1 or invisible by default
            var noBaseSpriteIds = new HashSet<int> { 274, 273, 31, 240, 239 };

            foreach (var (symbolId, name, sortOrder) in orderedParts)
            {
                var part = new CharacterPartDefinition
                {
                    partName = name,
                    symbolId = symbolId,
                    sortOrder = sortOrder,
                    isArmorAware = AS3StateMapping.ArmorAwarePartIds.Contains(symbolId),
                };

                // Set tint category
                if (AS3StateMapping.PartTintCategories.TryGetValue(name, out var tintCat))
                    part.tintCategory = (TintCategory)(int)tintCat;
                else
                    part.tintCategory = TintCategory.None;

                // Set pivot from import data
                if (spriteImport.PivotsBySymbol.TryGetValue(symbolId, out var pivot))
                {
                    part.pivotNormalized = pivot;

                    // Diagnostic for head sub-parts: compare SWF bounds with PNG dimensions
                    if (HeadSubPartIds.Contains(symbolId) &&
                        spriteImport.SpritesBySymbol.TryGetValue(symbolId, out var diagFrames) &&
                        diagFrames.TryGetValue(1, out var diagSprite) && diagSprite != null)
                    {
                        var spriteRect = diagSprite.textureRect;
                        Rect swfBounds = default;
                        string boundsSource = "none";
                        if (swfData.ShapeBounds.TryGetValue(symbolId, out var sb))
                        { swfBounds = sb; boundsSource = "ShapeBounds"; }
                        else if (swfData.Frame1Bounds.TryGetValue(symbolId, out var f1b) && f1b.width > 0)
                        { swfBounds = f1b; boundsSource = "Frame1Bounds"; }
                        else if (swfData.Symbols.TryGetValue(symbolId, out var sym) && sym.Bounds.width > 0)
                        { swfBounds = sym.Bounds; boundsSource = "ComputedBounds(union)"; }

                        int zf = spriteImport.ZoomFactor;
                        Debug.Log($"[AnimDataGen] DEFAULT PIVOT DIAG: {name}(sym{symbolId}) " +
                            $"png={spriteRect.width}x{spriteRect.height} " +
                            $"swfBounds=({swfBounds.x:F1},{swfBounds.y:F1},{swfBounds.width:F1},{swfBounds.height:F1}) " +
                            $"source={boundsSource} zoom={zf} " +
                            $"expectedPng={swfBounds.width * zf:F0}x{swfBounds.height * zf:F0} " +
                            $"pivot=({pivot.x:F3},{pivot.y:F3})");
                    }
                }

                // Set base sprite
                if (!noBaseSpriteIds.Contains(symbolId) &&
                    spriteImport.SpritesBySymbol.TryGetValue(symbolId, out var frames))
                {
                    if (frames.TryGetValue(1, out var sprite))
                        part.baseSprite = sprite;

                    // Build style variants for multi-frame head parts
                    // eye (220): 4 frames = eye expressions/styles
                    // morda_base (199): 3 frames = mouth expressions
                    if ((symbolId == 220 || symbolId == 199) && frames.Count > 1)
                    {
                        var variants = new List<Sprite>();
                        foreach (int frameNum in frames.Keys.OrderBy(k => k))
                        {
                            if (frames[frameNum] != null)
                                variants.Add(frames[frameNum]);
                        }
                        if (variants.Count > 1)
                            part.styleVariants = variants.ToArray();
                    }
                }

                parts.Add(part);
            }

            return parts;
        }

        /// <summary>Head sub-part symbol IDs that get composed with the head placement.</summary>
        /// <remarks>
        /// 273 (morda_armor) is both a target (for armor overlays) AND a container
        /// (head(274) → morda(273) → [morda_base(199), eye(220), forelock(233), etc.]).
        /// CollectSubPartOffsets handles this by storing AND recursing into containers.
        /// </remarks>
        static readonly HashSet<int> HeadSubPartIds = new() { 199, 220, 233, 235, 237, 239, 240, 273, 31 };

        static List<CharacterStateClip> BuildStateClips(
            SWFFile swfData,
            Dictionary<int, int> partIndexLookup)
        {
            var clips = new List<CharacterStateClip>();

            // The osn root (symbol3679) maps frame labels to body-state symbols
            if (!swfData.Symbols.TryGetValue(AS3StateMapping.OsnRootSymbolId, out var osnSymbol))
            {
                Debug.LogError("[AnimDataGen] Could not find osn root symbol 3679 in SWF data");
                return clips;
            }

            // Pre-build flattened head sub-part placements by recursively traversing
            // the head DefineSprite (274) hierarchy. Sub-parts may be nested inside
            // container sprites (e.g., head → morda_container → morda_base, eye, etc.)
            var headSubPartOffsets = new Dictionary<int, (Vector2 position, float rotation, Vector2 scale)>();
            if (swfData.Symbols.TryGetValue(274, out var headSymbol) && headSymbol.Frames.Count > 0)
            {
                // Dump the full recursive tree structure of head(274) for diagnostics
                Debug.Log($"[AnimDataGen] === Head(274) Full Tree Dump ===");
                DumpSpriteTree(swfData, 274, "", 0);

                // Dump ALL frames of morda_armor(273) to understand per-armor sub-part composition
                if (swfData.Symbols.TryGetValue(273, out var mordaSymbol))
                {
                    Debug.Log($"[AnimDataGen] === morda_armor(273) ALL FRAMES: {mordaSymbol.Frames.Count} frames ===");
                    for (int fi = 0; fi < mordaSymbol.Frames.Count; fi++)
                    {
                        var frame = mordaSymbol.Frames[fi];
                        Debug.Log($"[AnimDataGen]   Frame {fi + 1} label=\"{frame.Label ?? ""}\" ({frame.Placements.Count} placements):");
                        foreach (var p in frame.Placements)
                        {
                            bool isSpr = swfData.Symbols.ContainsKey(p.CharacterId);
                            bool isShp = swfData.ShapeBounds.ContainsKey(p.CharacterId);
                            string name = p.InstanceName ?? "";
                            // Check if this child has sub-frames
                            int subFrames = 0;
                            if (isSpr && swfData.Symbols.TryGetValue(p.CharacterId, out var cs))
                                subFrames = cs.Frames.Count;
                            Debug.Log($"[AnimDataGen]     depth={p.Depth} id={p.CharacterId} name=\"{name}\" " +
                                $"type={(isSpr ? $"Sprite({subFrames}f)" : isShp ? "Shape" : "???")} " +
                                $"pos=({p.Position.x:F1},{p.Position.y:F1}) " +
                                $"rot={p.Rotation:F1} scale=({p.Scale.x:F2},{p.Scale.y:F2})");
                        }
                    }
                }

                // Dump helm(31) frames to see where helmet graphics come from
                if (swfData.Symbols.TryGetValue(31, out var helmSymbol))
                {
                    Debug.Log($"[AnimDataGen] === helm(31) ALL FRAMES: {helmSymbol.Frames.Count} frames ===");
                    for (int fi = 0; fi < helmSymbol.Frames.Count; fi++)
                    {
                        var frame = helmSymbol.Frames[fi];
                        Debug.Log($"[AnimDataGen]   Frame {fi + 1} label=\"{frame.Label ?? ""}\" ({frame.Placements.Count} placements):");
                        foreach (var p in frame.Placements)
                        {
                            bool isSpr = swfData.Symbols.ContainsKey(p.CharacterId);
                            bool isShp = swfData.ShapeBounds.ContainsKey(p.CharacterId);
                            Debug.Log($"[AnimDataGen]     depth={p.Depth} id={p.CharacterId} name=\"{p.InstanceName ?? ""}\" " +
                                $"type={(isSpr ? "Sprite" : isShp ? "Shape" : "???")} " +
                                $"pos=({p.Position.x:F1},{p.Position.y:F1})");
                        }
                    }
                }
                else
                {
                    Debug.Log($"[AnimDataGen] helm(31) NOT in Symbols (is shape={swfData.ShapeBounds.ContainsKey(31)})");
                }

                // Also dump eye(220) and forelock(233) frames for style variant understanding
                foreach (int checkId in new[] { 220, 233, 199, 237 })
                {
                    string checkName = GetSubPartName(checkId);
                    if (swfData.Symbols.TryGetValue(checkId, out var checkSym))
                    {
                        Debug.Log($"[AnimDataGen] {checkName}({checkId}): {checkSym.Frames.Count} frames, " +
                            $"labels=[{string.Join(", ", checkSym.Frames.Where(f => f.Label != null).Select(f => $"\"{f.Label}\"@{f.FrameNumber}"))}]");
                    }
                }

                // Recursively collect sub-part transforms
                CollectSubPartOffsets(swfData, headSymbol.Frames[0].Placements,
                    Vector2.zero, 0f, Vector2.one, headSubPartOffsets);

                Debug.Log($"[AnimDataGen] Found {headSubPartOffsets.Count} head sub-parts: " +
                    string.Join(", ", headSubPartOffsets.Keys.Select(id =>
                    {
                        string name = HeadSubPartIds.Contains(id) ? GetSubPartName(id) : $"sym{id}";
                        var offset = headSubPartOffsets[id];
                        return $"{name}({id}) pos=({offset.position.x:F1},{offset.position.y:F1})";
                    })));
            }
            else
            {
                bool inSymbols = swfData.Symbols.ContainsKey(274);
                bool inShapes = swfData.ShapeBounds.ContainsKey(274);
                Debug.LogWarning($"[AnimDataGen] Head(274) NOT found as expected. " +
                    $"InSymbols={inSymbols}, InShapes={inShapes}, " +
                    $"TotalSymbols={swfData.Symbols.Count}, TotalShapes={swfData.ShapeBounds.Count}");
            }

            foreach (var kvp in AS3StateMapping.StateLabels)
            {
                string stateName = kvp.Key;
                int bodySymbolId = kvp.Value;

                if (!swfData.Symbols.TryGetValue(bodySymbolId, out var bodySymbol))
                {
                    Debug.LogWarning($"[AnimDataGen] Body symbol {bodySymbolId} for state '{stateName}' not found in SWF");
                    continue;
                }

                var clip = new CharacterStateClip
                {
                    stateName = stateName,
                    frameCount = bodySymbol.Frames.Count,
                    useRunMane = RunManeStates.Contains(stateName),
                    useRunTail = RunTailStates.Contains(stateName),
                };

                // Apply loop mode override
                if (LoopOverrides.TryGetValue(stateName, out var loopInfo))
                {
                    clip.loopMode = loopInfo.mode;
                    clip.loopStartFrame = loopInfo.loopStart;
                    clip.loopEndFrame = loopInfo.loopEnd;
                }

                // Build per-frame placement data
                var frameDataList = new List<CharacterFrameData>();
                foreach (var frame in bodySymbol.Frames)
                {
                    var frameData = new CharacterFrameData();
                    var placements = new List<PartFramePlacement>();

                    SWFPlacement headPlacement = null;

                    foreach (var placement in frame.Placements)
                    {
                        // Track head placement for sub-part composition
                        if (placement.CharacterId == 274)
                            headPlacement = placement;

                        // Try to find this placement's character in our parts list
                        if (!partIndexLookup.TryGetValue(placement.CharacterId, out int partIndex))
                            continue;

                        placements.Add(new PartFramePlacement
                        {
                            partIndex = partIndex,
                            localPosition = FlashToUnityPosition(placement.Position),
                            localRotation = -placement.Rotation, // Flash CW → Unity CCW
                            localScale = placement.Scale,
                            visible = true,
                        });
                    }

                    // Compose head sub-part placements from head transform + pre-built offsets
                    if (headPlacement != null && headSubPartOffsets.Count > 0)
                    {
                        foreach (var kvp2 in headSubPartOffsets)
                        {
                            int subPartSymId = kvp2.Key;
                            if (!HeadSubPartIds.Contains(subPartSymId))
                                continue;
                            if (!partIndexLookup.TryGetValue(subPartSymId, out int subPartIndex))
                                continue;

                            var offset = kvp2.Value;

                            // Create a virtual placement for composition
                            var virtualChild = new SWFPlacement
                            {
                                Position = offset.position,
                                Rotation = offset.rotation,
                                Scale = offset.scale,
                            };

                            var composed = ComposeFlashPlacements(headPlacement, virtualChild);

                            placements.Add(new PartFramePlacement
                            {
                                partIndex = subPartIndex,
                                localPosition = FlashToUnityPosition(composed.position),
                                localRotation = -composed.rotation,
                                localScale = composed.scale,
                                visible = true,
                            });
                        }
                    }

                    frameData.partPlacements = placements.ToArray();
                    frameDataList.Add(frameData);
                }

                clip.frames = frameDataList.ToArray();
                clips.Add(clip);
            }

            return clips;
        }

        /// <summary>
        /// Compose a parent Flash placement with a child placement.
        /// Returns the child's position/rotation/scale in the parent's parent space.
        /// All values in Flash coordinates (Y-down, rotation in degrees CW).
        /// </summary>
        static (Vector2 position, float rotation, Vector2 scale) ComposeFlashPlacements(
            SWFPlacement parent, SWFPlacement child)
        {
            float parentRotRad = parent.Rotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(parentRotRad);
            float sin = Mathf.Sin(parentRotRad);

            // Transform child position through parent's scale + rotation + translation
            float cx = child.Position.x * parent.Scale.x;
            float cy = child.Position.y * parent.Scale.y;
            Vector2 composedPos = new Vector2(
                parent.Position.x + cx * cos - cy * sin,
                parent.Position.y + cx * sin + cy * cos
            );

            float composedRot = parent.Rotation + child.Rotation;
            Vector2 composedScale = new Vector2(
                parent.Scale.x * child.Scale.x,
                parent.Scale.y * child.Scale.y
            );

            return (composedPos, composedRot, composedScale);
        }

        /// <summary>
        /// Recursively traverse a DefineSprite's display list to find target sub-parts.
        /// Composes transforms through any intermediate container sprites.
        /// </summary>
        static void CollectSubPartOffsets(
            SWFFile swfData,
            List<SWFPlacement> placements,
            Vector2 parentPos, float parentRot, Vector2 parentScale,
            Dictionary<int, (Vector2 position, float rotation, Vector2 scale)> results)
        {
            foreach (var placement in placements)
            {
                // Compose this placement with the parent transform
                float parentRotRad = parentRot * Mathf.Deg2Rad;
                float cos = Mathf.Cos(parentRotRad);
                float sin = Mathf.Sin(parentRotRad);

                float cx = placement.Position.x * parentScale.x;
                float cy = placement.Position.y * parentScale.y;
                Vector2 composedPos = new Vector2(
                    parentPos.x + cx * cos - cy * sin,
                    parentPos.y + cx * sin + cy * cos
                );
                float composedRot = parentRot + placement.Rotation;
                Vector2 composedScale = new Vector2(
                    parentScale.x * placement.Scale.x,
                    parentScale.y * placement.Scale.y
                );

                // Store if it's a target sub-part
                if (HeadSubPartIds.Contains(placement.CharacterId))
                {
                    results[placement.CharacterId] = (composedPos, composedRot, composedScale);
                }

                // Also recurse into any DefineSprite container to find nested sub-parts
                // (not mutually exclusive — a symbol like morda_armor(273) is both a target AND a container)
                if (swfData.Symbols.TryGetValue(placement.CharacterId, out var childSymbol)
                    && childSymbol.Frames.Count > 0
                    && childSymbol.Frames[0].Placements.Count > 0)
                {
                    CollectSubPartOffsets(swfData, childSymbol.Frames[0].Placements,
                        composedPos, composedRot, composedScale, results);
                }
            }
        }

        /// <summary>
        /// Recursively dump the DefineSprite tree structure for diagnostics.
        /// Shows all children at each level, whether they're shapes, sprites, or target sub-parts.
        /// </summary>
        static void DumpSpriteTree(SWFFile swfData, int symbolId, string indent, int depth)
        {
            if (depth > 10) return; // safety

            if (!swfData.Symbols.TryGetValue(symbolId, out var symbol))
            {
                bool isShape = swfData.ShapeBounds.ContainsKey(symbolId);
                Debug.Log($"{indent}sym{symbolId}: {(isShape ? "DefineShape" : "UNKNOWN (not in Symbols or ShapeBounds)")}");
                return;
            }

            Debug.Log($"{indent}sym{symbolId}: DefineSprite, {symbol.Frames.Count} frames, " +
                $"{symbol.Frames.Sum(f => f.Placements.Count)} total placements across frames");

            // Show frame 1 placements (the default state)
            if (symbol.Frames.Count > 0)
            {
                var frame = symbol.Frames[0];
                Debug.Log($"{indent}  Frame 1 ({frame.Placements.Count} placements):");
                foreach (var p in frame.Placements)
                {
                    string tag = HeadSubPartIds.Contains(p.CharacterId) ? " *** TARGET ***" : "";
                    bool isSprite = swfData.Symbols.ContainsKey(p.CharacterId);
                    bool isShape = swfData.ShapeBounds.ContainsKey(p.CharacterId);
                    string type = isSprite ? "Sprite" : (isShape ? "Shape" : "???");
                    Debug.Log($"{indent}    depth={p.Depth} id={p.CharacterId} name=\"{p.InstanceName ?? ""}\" " +
                        $"type={type} pos=({p.Position.x:F1},{p.Position.y:F1}) " +
                        $"rot={p.Rotation:F1} scale=({p.Scale.x:F2},{p.Scale.y:F2}){tag}");

                    // Recurse into all child sprites (including targets that are also containers)
                    if (isSprite)
                        DumpSpriteTree(swfData, p.CharacterId, indent + "      ", depth + 1);
                }
            }
        }

        static string GetSubPartName(int symbolId)
        {
            return symbolId switch
            {
                199 => "morda_base", 220 => "eye", 233 => "forelock",
                235 => "morda_overlay", 237 => "horn", 239 => "magic",
                240 => "konec", 273 => "morda_armor", 31 => "helm",
                _ => $"sym{symbolId}"
            };
        }

        /// <summary>
        /// Default symbol IDs for head sub-parts on frame 1 of morda_armor(273).
        /// Instance names in SWF → our part names.
        /// Note: "morda" appears twice (base at lower depth, overlay at higher depth).
        /// </summary>
        static readonly Dictionary<int, string> DefaultHeadSymbolToSlot = new()
        {
            { 199, "morda_base" },
            { 220, "eye" },
            { 233, "forelock" },
            { 235, "morda_overlay" },
            { 237, "horn" },
            { 239, "magic" },
            { 31, "helm" },
            { 240, "konec" },
        };

        /// <summary>
        /// Maps SWF instance names to our head sub-part slot names.
        /// "morda" maps to morda_base by default; the second occurrence (by depth) maps to morda_overlay.
        /// </summary>
        static string MapInstanceNameToSlot(string instanceName, int depthRank)
        {
            return instanceName switch
            {
                "helm" => "helm",
                "eye" => "eye",
                "hair" => "forelock",
                "horn" => "horn",
                "magic" => "magic",
                "konec" => "konec",
                "morda" => depthRank == 0 ? "morda_base" : "morda_overlay",
                _ => null,
            };
        }

        /// <summary>
        /// Maps our head sub-part slot names to their default symbol IDs.
        /// </summary>
        static readonly Dictionary<string, int> SlotToDefaultSymbol = new()
        {
            { "morda_base", 199 },
            { "eye", 220 },
            { "forelock", 233 },
            { "morda_overlay", 235 },
            { "horn", 237 },
            { "magic", 239 },
            { "helm", 31 },
            { "konec", 240 },
        };

        /// <summary>
        /// Result of scanning morda_armor(273) frames for per-armor head sub-part variants.
        /// </summary>
        public class HeadArmorVariantData
        {
            /// <summary>armorLabel → (slotName → variantSymbolId). Only entries where symbolId differs from default.</summary>
            public Dictionary<string, Dictionary<string, int>> VariantsByArmor = new();

            /// <summary>All variant symbol IDs that need to be imported (not in the default set).</summary>
            public HashSet<int> AllVariantSymbolIds = new();

            /// <summary>armorLabel → set of slot names that are HIDDEN on that armor frame.</summary>
            public Dictionary<string, HashSet<string>> HiddenSlotsByArmor = new();

            /// <summary>
            /// armorLabel → (slotName → position delta in Flash coords).
            /// Delta = variant_position - default_position within morda_armor(273).
            /// Applied to correct for sub-parts that are repositioned per armor.
            /// </summary>
            public Dictionary<string, Dictionary<string, Vector2>> PositionDeltasByArmor = new();
        }

        /// <summary>
        /// Scan all frames of morda_armor(273) to discover per-armor head sub-part variant symbols.
        /// Each frame corresponds to an armor type and may swap in different DefineSprite symbols
        /// for the helm, morda, horn etc. slots.
        /// </summary>
        public static HeadArmorVariantData ExtractHeadArmorVariants(SWFFile swfData)
        {
            var result = new HeadArmorVariantData();

            if (!swfData.Symbols.TryGetValue(273, out var mordaArmorSymbol))
                return result;

            // Build the default slot layout and positions from frame 1
            var defaultSlots = new Dictionary<string, int>(); // slotName → symbolId
            var defaultPositions = new Dictionary<string, Vector2>(); // slotName → position in Flash coords
            if (mordaArmorSymbol.Frames.Count > 0)
            {
                var frame1 = mordaArmorSymbol.Frames[0];
                int mordaCount = 0;
                foreach (var p in frame1.Placements.OrderBy(p => p.Depth))
                {
                    int mordaRank = (p.InstanceName == "morda") ? mordaCount++ : 0;
                    string slot = MapInstanceNameToSlot(p.InstanceName, mordaRank);
                    if (slot != null)
                    {
                        defaultSlots[slot] = p.CharacterId;
                        defaultPositions[slot] = p.Position;
                    }
                }
            }

            Debug.Log($"[AnimDataGen] Head armor defaults: {string.Join(", ", defaultSlots.Select(kv => $"{kv.Key}=sym{kv.Value}"))}");

            // Scan armor frames (frame 2+)
            for (int fi = 1; fi < mordaArmorSymbol.Frames.Count; fi++)
            {
                var frame = mordaArmorSymbol.Frames[fi];
                if (string.IsNullOrEmpty(frame.Label)) continue;

                string armorLabel = frame.Label;
                var variants = new Dictionary<string, int>();
                var presentSlots = new HashSet<string>();

                var positionDeltas = new Dictionary<string, Vector2>();

                int mordaCount = 0;
                foreach (var p in frame.Placements.OrderBy(p => p.Depth))
                {
                    int mordaRank = (p.InstanceName == "morda") ? mordaCount++ : 0;
                    string slot = MapInstanceNameToSlot(p.InstanceName, mordaRank);

                    if (slot == null)
                    {
                        // Unknown instance - might be a shape-based overlay unique to this armor
                        // We can't map it to a slot, skip it
                        continue;
                    }

                    presentSlots.Add(slot);

                    // Compute position delta from default
                    if (defaultPositions.TryGetValue(slot, out var defaultPos))
                    {
                        var delta = p.Position - defaultPos;
                        if (delta.sqrMagnitude > 0.01f) // Only store meaningful deltas
                            positionDeltas[slot] = delta;
                    }

                    // Check if this uses a different symbol than the default
                    if (defaultSlots.TryGetValue(slot, out int defaultSymId) && p.CharacterId != defaultSymId)
                    {
                        variants[slot] = p.CharacterId;
                        result.AllVariantSymbolIds.Add(p.CharacterId);
                    }
                }

                if (variants.Count > 0)
                    result.VariantsByArmor[armorLabel] = variants;

                if (positionDeltas.Count > 0)
                    result.PositionDeltasByArmor[armorLabel] = positionDeltas;

                // Check for hidden slots (present in defaults but missing on this frame)
                var hiddenSlots = new HashSet<string>();
                foreach (var slot in defaultSlots.Keys)
                {
                    if (!presentSlots.Contains(slot))
                        hiddenSlots.Add(slot);
                }
                if (hiddenSlots.Count > 0)
                    result.HiddenSlotsByArmor[armorLabel] = hiddenSlots;

                if (variants.Count > 0 || hiddenSlots.Count > 0 || positionDeltas.Count > 0)
                {
                    Debug.Log($"[AnimDataGen] Armor '{armorLabel}': " +
                        $"variants=[{string.Join(", ", variants.Select(kv => $"{kv.Key}→sym{kv.Value}"))}] " +
                        $"hidden=[{string.Join(", ", hiddenSlots)}] " +
                        $"posDeltas=[{string.Join(", ", positionDeltas.Select(kv => $"{kv.Key}=({kv.Value.x:F1},{kv.Value.y:F1})"))}]");
                }
            }

            Debug.Log($"[AnimDataGen] Total variant symbols to import: {result.AllVariantSymbolIds.Count} " +
                $"[{string.Join(", ", result.AllVariantSymbolIds.OrderBy(id => id))}]");

            return result;
        }

        static List<ArmorVisualSet> BuildArmorSets(
            SWFFile swfData,
            CharacterSpriteImporter.ImportResult spriteImport,
            Dictionary<int, int> partIndexLookup,
            List<CharacterPartDefinition> parts,
            HeadArmorVariantData headVariants = null)
        {
            var armorSets = new List<ArmorVisualSet>();
            var armorLabels = AS3StateMapping.ArmorLabelsForKorpus;

            // Pre-build per-part label→frameNumber lookup from SWF data
            // This handles parts that have different frame orderings (e.g. fleg2 has 22 frames vs 21)
            var partLabelToFrame = new Dictionary<int, Dictionary<string, int>>(); // symbolId → (label → frameNum)
            foreach (var part in parts)
            {
                if (!part.isArmorAware) continue;
                if (swfData.Symbols.TryGetValue(part.symbolId, out var partSymbol))
                {
                    var labelMap = new Dictionary<string, int>();
                    foreach (var frame in partSymbol.Frames)
                    {
                        if (!string.IsNullOrEmpty(frame.Label))
                            labelMap[frame.Label] = frame.FrameNumber;
                    }
                    if (labelMap.Count > 0)
                        partLabelToFrame[part.symbolId] = labelMap;
                }
            }

            // Build slotName → partIndex lookup for head sub-part overrides
            var slotToPartIndex = new Dictionary<string, int>();
            foreach (var kvp in SlotToDefaultSymbol)
            {
                if (partIndexLookup.TryGetValue(kvp.Value, out int partIdx))
                    slotToPartIndex[kvp.Key] = partIdx;
            }

            for (int armorIdx = 1; armorIdx < armorLabels.Length; armorIdx++)
            {
                string armorId = armorLabels[armorIdx];

                var set = new ArmorVisualSet
                {
                    armorId = armorId,
                    hidesMane = IsManHidingArmor(armorId),
                };

                var overrides = new List<ArmorPartOverride>();

                // --- Body part armor overrides (korpus, neck, legs, etc.) ---
                foreach (var part in parts)
                {
                    if (!part.isArmorAware) continue;
                    if (!partIndexLookup.TryGetValue(part.symbolId, out int partIndex)) continue;

                    // Skip head sub-parts handled below and morda_armor(273) which is
                    // a layout container, not a renderable body part
                    if (DefaultHeadSymbolToSlot.ContainsKey(part.symbolId) || part.symbolId == 273)
                        continue;

                    // Determine the correct sprite frame for this armor on this part
                    int spriteFrame;
                    if (partLabelToFrame.TryGetValue(part.symbolId, out var labelMap) &&
                        labelMap.TryGetValue(armorId, out int labelFrame))
                    {
                        spriteFrame = labelFrame;
                    }
                    else
                    {
                        spriteFrame = armorIdx + 1;
                    }

                    if (spriteImport.SpritesBySymbol.TryGetValue(part.symbolId, out var frames) &&
                        frames.TryGetValue(spriteFrame, out var sprite) &&
                        sprite != null)
                    {
                        overrides.Add(new ArmorPartOverride
                        {
                            partIndex = partIndex,
                            armorSprite = sprite,
                            pivotOverride = part.pivotNormalized, // Same symbol, same pivot
                        });
                    }
                }

                // --- morda_armor(273) as body-part override for late armors ---
                // Late armors (power, polic, spec, encl, ali) don't have morda_armor(273) sub-part
                // variant frames. For these, treat morda_armor as a renderable body part with its
                // per-armor composite frame, instead of decomposing into individual sub-parts.
                bool hasHeadVariants = headVariants != null &&
                    headVariants.VariantsByArmor.ContainsKey(armorId);

                if (!hasHeadVariants && partIndexLookup.TryGetValue(273, out int mordaPartIndex))
                {
                    // Try to get morda_armor's composite sprite for this armor
                    int mordaFrame;
                    if (partLabelToFrame.TryGetValue(273, out var mordaLabelMap) &&
                        mordaLabelMap.TryGetValue(armorId, out int mordaLabelFrame))
                    {
                        mordaFrame = mordaLabelFrame;
                    }
                    else
                    {
                        mordaFrame = armorIdx + 1;
                    }

                    if (spriteImport.SpritesBySymbol.TryGetValue(273, out var mordaFrames) &&
                        mordaFrames.TryGetValue(mordaFrame, out var mordaSprite) &&
                        mordaSprite != null)
                    {
                        var mordaPart = parts.FirstOrDefault(p => p.symbolId == 273);
                        overrides.Add(new ArmorPartOverride
                        {
                            partIndex = mordaPartIndex,
                            armorSprite = mordaSprite,
                            pivotOverride = mordaPart?.pivotNormalized ?? new Vector2(0.5f, 0.5f),
                        });
                        Debug.Log($"[AnimDataGen] Armor '{armorId}': using morda_armor(273) composite " +
                            $"frame {mordaFrame} (no sub-part variants for this armor)");
                    }
                }

                // --- Head sub-part armor overrides from morda_armor(273) variants ---
                if (headVariants != null)
                {
                    // Check for variant sprites (different symbol IDs per armor)
                    if (headVariants.VariantsByArmor.TryGetValue(armorId, out var variants))
                    {
                        foreach (var kvp in variants)
                        {
                            string slotName = kvp.Key;
                            int variantSymbolId = kvp.Value;

                            if (!slotToPartIndex.TryGetValue(slotName, out int partIndex))
                                continue;

                            // Try to get sprite for the variant symbol (frame 1)
                            if (spriteImport.SpritesBySymbol.TryGetValue(variantSymbolId, out var varFrames) &&
                                varFrames.TryGetValue(1, out var varSprite) &&
                                varSprite != null)
                            {
                                // Correct variant pivot using actual PNG dimensions.
                                // SWF bounds give us where (0,0) is relative to content origin,
                                // but bounds width/height may not match JPEXS PNG dimensions.
                                // Using real PNG dims with SWF origin gives accurate pivots.
                                // Only variants need this — default parts already work with SWF-bounds pivots
                                // because their position data was calibrated against those pivots.
                                var spriteRect = varSprite.textureRect;
                                float pngW = spriteRect.width;
                                float pngH = spriteRect.height;

                                Rect swfBounds = default;
                                string boundsSource = "none";
                                if (swfData.ShapeBounds.TryGetValue(variantSymbolId, out var sb))
                                {
                                    swfBounds = sb;
                                    boundsSource = "ShapeBounds";
                                }
                                else if (swfData.Frame1Bounds.TryGetValue(variantSymbolId, out var f1b) &&
                                         f1b.width > 0)
                                {
                                    swfBounds = f1b;
                                    boundsSource = "Frame1Bounds";
                                }
                                else if (swfData.Symbols.TryGetValue(variantSymbolId, out var sym) &&
                                         sym.Bounds.width > 0)
                                {
                                    swfBounds = sym.Bounds;
                                    boundsSource = "ComputedBounds(union)";
                                }

                                Vector2 varPivot;
                                int zf = spriteImport.ZoomFactor;
                                if (swfBounds.width > 0 && pngW > 1 && pngH > 1)
                                {
                                    // Use SWF origin (xMin, yMin) but actual PNG dimensions
                                    float correctedPivotX = (0f - swfBounds.x) / pngW;
                                    float correctedPivotY = 1f - ((0f - swfBounds.y) / pngH);
                                    varPivot = new Vector2(correctedPivotX, correctedPivotY);
                                }
                                else
                                {
                                    varPivot = spriteImport.PivotsBySymbol.TryGetValue(variantSymbolId, out var vp)
                                        ? vp : new Vector2(0.5f, 0.5f);
                                }

                                Debug.Log($"[AnimDataGen] VARIANT PIVOT DIAG: armor='{armorId}' slot='{slotName}' " +
                                    $"sym{variantSymbolId} png={pngW}x{pngH} " +
                                    $"swfBounds=({swfBounds.x:F1},{swfBounds.y:F1},{swfBounds.width:F1},{swfBounds.height:F1}) " +
                                    $"source={boundsSource} zoom={zf} " +
                                    $"expectedPngSize={swfBounds.width * zf:F0}x{swfBounds.height * zf:F0} " +
                                    $"pivot=({varPivot.x:F3},{varPivot.y:F3})");

                                overrides.Add(new ArmorPartOverride
                                {
                                    partIndex = partIndex,
                                    armorSprite = varSprite,
                                    pivotOverride = varPivot,
                                });
                            }
                            else
                            {
                                Debug.LogWarning($"[AnimDataGen] Armor '{armorId}' slot '{slotName}': " +
                                    $"variant sym{variantSymbolId} sprite not found in import");
                            }
                        }
                    }

                    // Build hidden head sub-parts list for this armor
                    if (headVariants.HiddenSlotsByArmor.TryGetValue(armorId, out var hiddenSlots))
                    {
                        var hiddenPartNames = new List<string>();
                        foreach (string slotName in hiddenSlots)
                            hiddenPartNames.Add(slotName);

                        if (hiddenPartNames.Count > 0)
                            set.hiddenHeadParts = hiddenPartNames.ToArray();
                    }
                }

                set.partOverrides = overrides.ToArray();
                armorSets.Add(set);
            }

            return armorSets;
        }

        /// <summary>
        /// Convert Flash position (Y-down) to Unity position (Y-up).
        /// Flash uses pixels, Unity uses world units at PPU=100.
        /// </summary>
        static Vector2 FlashToUnityPosition(Vector2 flashPos)
        {
            return new Vector2(flashPos.x / 100f, -flashPos.y / 100f);
        }

        /// <summary>
        /// Armors known to hide the mane from AS3 Armor.hideMane.
        /// This list may need expansion based on further AS3 research.
        /// </summary>
        static bool IsManHidingArmor(string armorId)
        {
            return armorId switch
            {
                "battle" or "assault" or "magus" or "antirad" or "antihim" or
                "intel" or "astealth" or "moon" or "sapper" or "power" or
                "polic" or "spec" or "encl" or "ali" => true,
                _ => false,
            };
        }
    }
}
#endif
