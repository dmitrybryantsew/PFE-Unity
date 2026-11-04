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
            public Vector2 BodyScale = Vector2.one;
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

            // The body's own placement inside the outer visual symbol. Without it the whole rig draws
            // at 1/1.3 of its AS3 size — see CharacterAnimationDefinition.bodyScale.
            definition.bodyScale = ResolveBodyScale(swfData, result.Warnings);
            result.BodyScale = definition.bodyScale;

            // Build armor sets using SWF frame labels for correct per-part mapping
            // Include head sub-part overrides from morda_armor(273) frame analysis
            var armorSets = BuildArmorSets(swfData, spriteImport, partIndexLookup, partsList, headVariants);
            definition.armorSets = armorSets.ToArray();

            // Build overlay clips — the siblings of the body sprite (vis.shit and friends).
            // These are NOT parts: they run on their own timeline and are drawn above the body.
            var overlays = BuildOverlays(swfData, spriteImport, result.Warnings);
            definition.overlays = overlays.ToArray();
            if (overlays.Count > 0)
            {
                Debug.Log($"[AnimDataGen] Overlay clips: " +
                    $"[{string.Join(", ", overlays.Select(o => $"{o.overlayName}({o.frames.Length}f)"))}]");
            }

            EditorUtility.SetDirty(definition);
            AssetDatabase.SaveAssets();

            result.Asset = definition;
            result.Warnings.AddRange(spriteImport.Warnings);
            return result;
        }

        /// <summary>
        /// The scale the game draws the body clip at — <c>visualPlayer</c>(3690) placing its
        /// <c>osn</c> child (3679) at <b>1.2999878</b>.
        ///
        /// <para><b>Why the import has to read one symbol up.</b> Every state clip the rig plays lives
        /// inside 3679, and 3679 places all 26 of them at identity — so the only matrix between the
        /// player's art and the world is the single one on 3690's <c>osn</c>. The importer reads
        /// 3651 and its parts, which is correct for the art and silent about the size: without this
        /// the rig draws at 1/1.3 of what AS3 draws, with no part individually wrong. The personas
        /// have the same fact stored per persona (<c>NpcDefinition.visualScaleX</c>); the player had
        /// nowhere to put it, which is exactly why it was the only character drawn at 1.0.</para>
        ///
        /// <para><b>Absence is a warning and a fallback, never a silent 1.</b> A stale parse, or a SWF
        /// whose 3690 has been renamed, leaves the rig at 1.0 — the current, wrong size. Saying so is
        /// the difference between "the import could not find it" and "the game draws it at 1".</para>
        /// </summary>
        static Vector2 ResolveBodyScale(SWFFile swfData, List<string> warnings)
        {
            const string osnInstanceName = "osn";

            if (swfData?.Symbols == null ||
                !swfData.Symbols.TryGetValue(AS3StateMapping.VisualPlayerSymbolId, out SWFSymbol visual) ||
                visual.Frames == null || visual.Frames.Count == 0)
            {
                warnings.Add(
                    $"body scale: visualPlayer({AS3StateMapping.VisualPlayerSymbolId}) is not in the parsed " +
                    "SWF, so the body keeps scale 1 — which draws the player at 1/1.3 of its AS3 size.");
                return Vector2.one;
            }

            SWFFrame frame = visual.Frames[0];
            if (frame.Placements != null)
            {
                foreach (SWFPlacement placement in frame.Placements)
                {
                    if (string.Equals(placement.InstanceName, osnInstanceName, StringComparison.Ordinal))
                    {
                        return placement.Scale;
                    }
                }
            }

            warnings.Add(
                $"body scale: visualPlayer({AS3StateMapping.VisualPlayerSymbolId}) frame 1 has no " +
                $"'{osnInstanceName}' placement, so the body keeps scale 1 — which draws the player at " +
                "1/1.3 of its AS3 size.");
            return Vector2.one;
        }

        /// <summary>
        /// The overlay clips this character has, from the symbols the sprite importer brought in.
        ///
        /// <para><b>Why the frames all come through here.</b> A body part keeps only frame 1
        /// (<c>baseSprite</c>, assigned in <see cref="BuildPartDefinitions"/>), which is right for a
        /// part — the state clip decides what a part looks like on any given frame. An overlay has no
        /// state clip behind it, so its whole timeline has to be carried, in order, and it is the
        /// overlay's own playhead that walks it. Dropping to frame 1 here would produce a shield that
        /// never materialises and a build with nothing to say about it.</para>
        ///
        /// <para><b>Absent symbols are skipped, not faked.</b> If the importer did not bring symbol
        /// 3625 in — because the source root is stale, or the import step was unchecked — the overlay
        /// is left out entirely, so <c>OverlayClip.ShouldDraw</c> sees zero frames and the assembler
        /// draws nothing. The alternative, emitting a row with an empty <c>frames</c> array, looks the
        /// same at runtime but claims in the asset that the shield exists.</para>
        /// </summary>
        static List<CharacterOverlayDefinition> BuildOverlays(
            SWFFile swfData,
            CharacterSpriteImporter.ImportResult spriteImport,
            List<string> warnings)
        {
            var overlays = new List<CharacterOverlayDefinition>();

            AddOverlay(overlays, swfData, spriteImport, ShieldSymbolId, ShieldOverlayName, warnings);

            return overlays;
        }

        /// <summary>
        /// <c>visShit</c>, the <c>sp_mshit</c> dome. From <c>visShit.as:5</c> —
        /// <c>[Embed(source="/_assets/assets.swf", symbol="symbol3625")]</c> — and 20 frames, matching
        /// <c>addFrameScript(0,frame1,19,frame20)</c> and the export's 20 PNGs.
        ///
        /// <para>The boss variants <c>visShit2</c> (1903) and <c>visShit3</c> (1900) are deliberately
        /// not wired: they belong to <c>UnitAlicorn</c>/<c>UnitBossAlicorn</c>, which are units rather
        /// than the player, and they differ in scale (1.5-1.7) and offset (<c>y = -50</c>). They are
        /// also exported, so adding them is a row here plus a gate case, not an export.</para>
        /// </summary>
        const int ShieldSymbolId = 3625;
        const string ShieldOverlayName = "shit";

        static void AddOverlay(
            List<CharacterOverlayDefinition> overlays,
            SWFFile swfData,
            CharacterSpriteImporter.ImportResult spriteImport,
            int symbolId,
            string overlayName,
            List<string> warnings)
        {
            if (!spriteImport.SpritesBySymbol.TryGetValue(symbolId, out var framesByFrameNum) ||
                framesByFrameNum == null ||
                framesByFrameNum.Count == 0)
            {
                Debug.LogWarning($"[AnimDataGen] Overlay '{overlayName}' (symbol {symbolId}) was not " +
                    "imported, so it is omitted from the definition. Re-run the sprite import step.");
                return;
            }

            // Frame numbers are 1-based in the export; order by them rather than by the dictionary's
            // insertion order, so f002 cannot land before f001.
            Sprite[] frames = framesByFrameNum
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Value)
                .Where(s => s != null)
                .ToArray();

            if (frames.Length == 0)
            {
                Debug.LogWarning($"[AnimDataGen] Overlay '{overlayName}' (symbol {symbolId}) imported " +
                    "no loadable sprites; omitted.");
                return;
            }

            var overlay = new CharacterOverlayDefinition
            {
                overlayName = overlayName,
                frames = frames,
                // The clip's own registration point, same source as a part's pivot.
                pivotNormalized = spriteImport.PivotsBySymbol.TryGetValue(symbolId, out var pivot)
                    ? pivot
                    : new Vector2(0.5f, 0.5f),
                localPosition = ResolveOverlayLocalPosition(swfData, symbolId, overlayName, warnings),
                localScale = 1f,
                sortingOrder = 1000,
                // visShit's frame script stops on frames 1 and 20 (visShit.as:14-21), so the clip plays
                // once and holds on the full dome while shithp lasts.
                loopMode = AnimationLoopMode.ClampForever,
            };

            overlays.Add(overlay);
        }

        /// <summary>
        /// Where the overlay clip sits inside <c>visualPlayer</c>(3690) — the matrix the SWF gives
        /// <c>vis.shit</c>(3625), read rather than assumed.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this cannot be zero.</b> 3690 places 3625 at <c>(0, -800)</c> twips — 40 px
        /// above the registration point in Flash's y-down space — so the port's y-up answer is
        /// <c>(0, +0.4)</c>. It was hardcoded to <see cref="Vector2.zero"/>, which put the dome's
        /// centre on the player's <b>feet</b>: gate, frames, colour, pivot and sorting were all
        /// correct and the whole graphic sat 40 px too low.</para>
        ///
        /// <para><b>Not the alicorns' <c>y = -50</c>.</b> <c>UnitAlicorn.as:202</c> assigns that to a
        /// <c>visShit</c> it adds to the <i>unit's</i> container at runtime; the player's clip is
        /// pre-placed inside 3690 by the symbol itself, and the two containers' origins are not the
        /// same point. Reusing the alicorn number here would be an invented value that happens to be
        /// close, which is the shape of defect this project keeps paying for.</para>
        ///
        /// <para><b>Absence is a warning, not a silent zero.</b> A parse that lost 3690, or a symbol
        /// renumbered by an export, would otherwise put the dome back on the feet with nothing said.</para>
        /// </remarks>
        static Vector2 ResolveOverlayLocalPosition(
            SWFFile swfData, int symbolId, string overlayName, List<string> warnings)
        {
            if (swfData?.Symbols == null ||
                !swfData.Symbols.TryGetValue(AS3StateMapping.VisualPlayerSymbolId, out SWFSymbol visual) ||
                visual.Frames == null || visual.Frames.Count == 0 ||
                visual.Frames[0].Placements == null)
            {
                warnings.Add(
                    $"overlay '{overlayName}': visualPlayer({AS3StateMapping.VisualPlayerSymbolId}) is not " +
                    "in the parsed SWF, so its position falls back to (0,0) — which draws the dome on the " +
                    "character's feet instead of over the body.");
                return Vector2.zero;
            }

            foreach (SWFPlacement placement in visual.Frames[0].Placements)
            {
                if (placement != null && placement.CharacterId == symbolId)
                {
                    return FlashToUnityPosition(placement.Position);
                }
            }

            warnings.Add(
                $"overlay '{overlayName}': symbol {symbolId} is not a child of " +
                $"visualPlayer({AS3StateMapping.VisualPlayerSymbolId}), so its position falls back to " +
                "(0,0) — which draws the dome on the character's feet instead of over the body.");
            return Vector2.zero;
        }

        /// <summary>
        /// Every renderable part, listed in draw order.
        /// </summary>
        /// <remarks>
        /// <para>This is not a hint. The renderers draw in the order they meet
        /// <c>partPlacements</c> and set <c>sortingOrder = slotIdx * 2</c>, and nothing sorts that
        /// array — so a part's position here decides what covers what. The SWF's placement order is
        /// therefore reproduced once, here, instead of being inherited from whatever order the
        /// exporter walked the display list in.</para>
        ///
        /// <para>The head block is in ascending <c>morda_armor</c>(273) placement depth, which is
        /// Flash's back-to-front order. The depths themselves are in <c>MapInstanceNameToSlot</c>;
        /// this list and that table must not disagree.</para>
        ///
        /// <para>The previous order was not this one: it put <c>morda_overlay</c> before <c>eye</c>
        /// and <c>konec</c> before <c>helm</c>. Neither was visible in the shipped art — none of the
        /// eye's 12 677 opaque pixels fall inside the overlay's rect, and <c>konec</c> and
        /// <c>helm</c> are empty on the unarmoured head — but the order is the premise of the
        /// decomposition, and it became load-bearing the moment a layer was inserted between two of
        /// them (the depth-3 shell sits between the face and the eye).</para>
        /// </remarks>
        static readonly (int symbolId, string name, int sortOrder)[] OrderedParts =
        {
            (3640, "lwing", 0),
            (24, "sleg3a", 1),
            (319, "sleg3", 2),
            (55, "sleg1", 3),      // hind leg upper (back)
            (78, "fleg1", 4),      // front leg 1 (back)
            (101, "fleg2", 5),     // front leg 2 (back)
            (124, "fleg3", 6),     // front hoof (back)
            (297, "fleg3a", 7),    // front hoof variant (back)
            (136, "tail", 8),
            (692, "tail_run", 9),
            (159, "korpus", 10),
            (182, "neck", 11),
            (195, "mane", 12),
            (734, "mane_run", 13),
            // ── head sub-model, in morda_armor(273) depth order ──────────────────
            //   depth  1/2  morda_base + morda_base_back (whatever is behind the face)
            //          3    shell
            //          6    eye        15  forelock
            //         19    morda_overlay
            //         21    horn       23  magic
            //         26    helm       27  helm2
            //         29    konec
            (274, "head", 14),             // head composite (hidden when sub-parts active)
            (262, "morda_base_back", 15),  // depth-1/2 layer behind the face (tre's hat); no default art
            (199, "morda_base", 16),       // face base shape (3 frames: mouth expressions)
            (242, "shell", 17),            // depth-3 layer between the face and the eye; no default art
            (220, "eye", 18),              // eyes (4 frames: expressions)
            (233, "forelock", 19),         // front hair tuft
            (235, "morda_overlay", 20),    // face detail overlay
            (237, "horn", 21),             // unicorn horn
            (239, "magic", 22),            // horn aura disc, alpha-driven — see HornAura
            (31, "helm", 23),              // helmet overlay (1x1, empty by default)
            (266, "helm2", 24),            // depth-27 second helmet layer; no default art
            (240, "konec", 25),            // horn tip effect (1x1, empty by default)
            (273, "morda_armor", 26),      // whole 273 frame, only for a frame that cannot be taken apart
            // The PipBuck is drawn above everything the body places — depth 63 in 514, 67 in the
            // twenty-three bodies that also place a mirror copy at 25, 70 in 3656 — and below
            // rwing(3650) at 69. It used to sit at 8, between the back hooves and the tail, which
            // put the torso and the head in front of it. See 12_compound_parts_and_the_pipbuck.md.
            (513, "pip", 27),
            // Note: front-side legs reuse the same symbols at a higher depth in the body sprite.
            (3650, "rwing", 28),
        };

        /// <summary>symbolId → draw order, derived from <see cref="OrderedParts"/>.</summary>
        static readonly Dictionary<int, int> PartSortOrderBySymbol = BuildPartSortOrder();

        static Dictionary<int, int> BuildPartSortOrder()
        {
            var map = new Dictionary<int, int>(OrderedParts.Length);
            foreach (var (symbolId, _, sortOrder) in OrderedParts)
                map[symbolId] = sortOrder;
            return map;
        }

        static List<CharacterPartDefinition> BuildPartDefinitions(
            SWFFile swfData,
            CharacterSpriteImporter.ImportResult spriteImport)
        {
            var parts = new List<CharacterPartDefinition>();
            var orderedParts = OrderedParts;

            // Parts that should NOT show baseSprite by default:
            // - Head composite (274): replaced by individual sub-parts
            // - morda_armor (273): composite with baked-in eyes/hair, only for armor mode
            // - helm (31), konec (240): 1x1, genuinely empty art
            //
            // 239 (magic) used to be listed here with the same "1x1 or invisible" reasoning, and that
            // was wrong: its PNG is 53x59 with 2 212 ink pixels and a dominant alpha of 175 — the
            // horn's aura disc. It is invisible in the game because its *alpha* starts at 0, not
            // because there is no art (UnitPlayer.as:4851 writes alpha = aMC / 100 and :4852 hides it
            // while that is 0). Leaving the sprite unassigned threw away the only copy of the art, so
            // no amount of driving the alpha could ever have shown it. The alpha is now the
            // renderer's business — see HornAura — and the sprite is assigned like every other part.
            // 262, 242 and 266 are the anchor symbols of the three slots that have no art on the
            // unarmoured head (morda_base_back, shell, helm2 — see ExtraHeadSlots). They are keys,
            // not defaults: the sprite belongs to one armour, so assigning it here would draw that
            // armour's shell on every outfit.
            var noBaseSpriteIds = new HashSet<int> { 274, 273, 31, 240, 262, 242, 266 };

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

                        int zf = spriteImport.EffectiveZoomBySymbol.TryGetValue(symbolId, out int diagZoom)
                            ? diagZoom
                            : spriteImport.ZoomFactor;
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
        ///
        /// <para>262, 242 and 266 are not frame-1 layers — they are the anchor symbols of the three
        /// slots that only an armour frame places (see <see cref="ExtraHeadSlots"/>). They are listed
        /// here so their parts receive a placement on every state frame;
        /// <c>SeedExtraHeadSlotOffsets</c> supplies the offset, because the walk over frame 1 cannot
        /// find a placement that frame 1 does not have.</para>
        /// </remarks>
        static readonly HashSet<int> HeadSubPartIds = new()
        {
            199, 220, 233, 235, 237, 239, 240, 273, 31, 262, 242, 266
        };

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

                // ...and the three slots frame 1 does not place, which the walk above cannot see.
                SeedExtraHeadSlotOffsets(swfData, headSymbol, headSubPartOffsets);

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

                        // ── The PipBuck rides a foreleg; the frame does not place it ────────
                        // UnitPlayer.as:4828-4849:
                        //
                        //     if (vis.scaleX > 0) { pip1.visible = true;  pip2.visible = false;
                        //                           pip1.x/y/rotation = frleg3.x/y/rotation; }
                        //     else                { pip2.visible = true;  pip1.visible = false;
                        //                           pip2.x/y/rotation = flleg3.x/y/rotation; }
                        //
                        // So the device is never drawn where 513's own placement puts it: it is
                        // snapped onto the near foreleg every frame, and exactly one of the two
                        // instances is visible. Drawn as authored, both stay at their frame-1
                        // positions — measured over `stay`: each placement's position is constant to
                        // four decimals across all 76 frames while the foreleg it should ride spans
                        // 0.258 units in x, so the device drifts up to 0.164 away (more than half the
                        // torso's own width) and a second copy sits behind the body.
                        //
                        // The port has no horizontal mirror — nothing in the codebase flips the
                        // character — so only the `scaleX > 0` branch is reachable: pip1 on frleg3.
                        // pip2 is kept in the data but emitted invisible rather than dropped, so the
                        // mirror branch is a renderer change and not a re-import when facing lands.
                        // Both names exist on all 27 body symbols that place 513 (26 have pip2).
                        bool pipHidden = placement.InstanceName == "pip2";
                        SWFPlacement posed = placement;
                        if (placement.InstanceName == "pip1")
                        {
                            SWFPlacement foreleg = frame.Placements
                                .FirstOrDefault(q => q.InstanceName == "frleg3");
                            if (foreleg != null)
                            {
                                // Position and rotation only — AS3 copies x/y/rotation, never scale.
                                posed = new SWFPlacement
                                {
                                    Position = foreleg.Position,
                                    Rotation = foreleg.Rotation,
                                    Scale = placement.Scale,
                                };
                            }
                            else
                            {
                                Debug.LogWarning($"[AnimDataGen] state '{stateName}' frame " +
                                    $"{frame.FrameNumber}: pip1 has no frleg3 to follow; the device " +
                                    "stays at its authored position and will drift from the leg");
                            }
                        }

                        placements.Add(new PartFramePlacement
                        {
                            partIndex = partIndex,
                            localPosition = FlashToUnityPosition(posed.Position),
                            localRotation = -posed.Rotation, // Flash CW → Unity CCW
                            localScale = posed.Scale,
                            visible = !pipHidden,
                        });
                    }

                    // Compose head sub-part placements from head transform + pre-built offsets.
                    //
                    // Ordered by the part table, NOT by the order the offsets were discovered. The
                    // renderers draw in array order (sortingOrder = slotIdx * 2) and nothing sorts,
                    // so this loop's order is the draw order. CollectSubPartOffsets walks frame 1 of
                    // morda_armor(273) — a frame that has no shell, no second helm layer and no hat —
                    // and SeedExtraHeadSlotOffsets appends those three afterwards, so insertion order
                    // would draw the depth-3 shell and the depth-1 hat ON TOP of the eye and the face.
                    if (headPlacement != null && headSubPartOffsets.Count > 0)
                    {
                        foreach (var kvp2 in headSubPartOffsets
                                     .OrderBy(kv => PartSortOrderBySymbol.TryGetValue(kv.Key, out int so)
                                         ? so
                                         : int.MaxValue))
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
        /// Adds placements for the head slots that <c>morda_armor</c>(273) frame 1 does not place —
        /// <c>morda_base_back</c>, <c>shell</c> and <c>helm2</c> (see <see cref="ExtraHeadSlots"/>).
        /// </summary>
        /// <remarks>
        /// <para>Each is anchored at the position of the first armour frame that puts something in
        /// that slot, composed through head(274) → morda(273) exactly as
        /// <see cref="CollectSubPartOffsets"/> composes the frame-1 layers. That position then serves
        /// as the slot's default, so an armour placing it elsewhere gets the difference as
        /// <c>localOffset</c>. In the shipped data none does: both shells sit at the face's
        /// <c>(2, −222)</c> and every shape layer at the container's origin.</para>
        ///
        /// <para>Without this the parts would have no <c>PartFramePlacement</c> on any state frame,
        /// and the renderers iterate placements — so the layer would be invisible no matter what
        /// override it carried.</para>
        /// </remarks>
        static void SeedExtraHeadSlotOffsets(
            SWFFile swfData,
            SWFSymbol headSymbol,
            Dictionary<int, (Vector2 position, float rotation, Vector2 scale)> results)
        {
            if (!swfData.Symbols.TryGetValue(273, out var mordaSymbol) || mordaSymbol.Frames.Count == 0)
                return;

            SWFPlacement mordaInHead = headSymbol.Frames[0].Placements
                .FirstOrDefault(p => p.CharacterId == 273);
            if (mordaInHead == null)
                return;

            foreach (var (slot, anchorSymbol, depth) in ExtraHeadSlots)
            {
                if (results.ContainsKey(anchorSymbol))
                    continue;

                SWFPlacement anchor = null;
                foreach (var frame in mordaSymbol.Frames)
                {
                    // Depths 1/2 hold the face in every frame but `tre`; the slot's anchor is the
                    // other layer, so the face is not a candidate.
                    anchor = frame.Placements.FirstOrDefault(p =>
                        p.Depth == depth && p.CharacterId != 199);
                    if (anchor != null) break;
                }

                if (anchor == null)
                {
                    Debug.LogWarning($"[AnimDataGen] Head slot '{slot}': no frame places anything at " +
                        $"depth {depth}, so the slot has no placement and will never be drawn");
                    continue;
                }

                var composed = ComposeFlashPlacements(mordaInHead, anchor);
                results[anchorSymbol] = composed;

                Debug.Log($"[AnimDataGen] Extra head slot '{slot}' keyed by sym{anchorSymbol}: " +
                    $"first placement sym{anchor.CharacterId} at depth {depth} -> " +
                    $"pos=({composed.position.x:F1},{composed.position.y:F1}) rot={composed.rotation:F1} " +
                    $"scale=({composed.scale.x:F2},{composed.scale.y:F2})");
            }
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
                // The three slots with no default art, by their anchor symbols.
                262 => "morda_base_back", 242 => "shell", 266 => "helm2",
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
        /// Which head slot a placement in <c>morda_armor</c>(273) belongs to.
        /// </summary>
        /// <remarks>
        /// <para><b>Why depth and not the order the placements appear in.</b> The old rule counted
        /// the placements named <c>morda</c> and called the first one the base face and every later
        /// one the overlay. That is only right when the frame places both. <c>power</c> places just
        /// one — the <i>overlay</i>, at depth 19 — so the rule called it <c>morda_base</c>, recorded
        /// it as a variant, and the power armour lost its face entirely. Depth is stable across all
        /// 16 frames: 1 and 2 are the base face, 3 is a shell layer drawn over the face (the battle
        /// and intel helmets), 15 is the hair layer, 26 the helm, 27 a second helm layer.</para>
        ///
        /// <para>Name still wins where it is unambiguous, because <c>ali</c> puts a <i>horn</i> at
        /// depth 15 — the hair layer — and it is a horn, not hair.</para>
        ///
        /// <para><b>Depths 1 and 2 are one slot with an exception.</b> Every frame but one puts the
        /// face (199) in the base depths and nothing else. <c>tre</c> puts its hat (262) at depth 1
        /// and the face at depth 2, so the face keeps <c>morda_base</c> and the hat goes to
        /// <c>morda_base_back</c>, which sorts below it — which is what the depths say, since the hat
        /// is at the lower one. A frame that put a second layer <i>in front</i> of the face would be
        /// misread by this rule; no such frame exists in the shipped data.</para>
        /// </remarks>
        static string MapInstanceNameToSlot(string instanceName, int depth, int characterId)
        {
            switch (instanceName)
            {
                case "helm": return "helm";
                case "eye": return "eye";
                case "hair": return "forelock";
                case "horn": return "horn";
                case "magic": return "magic";
                case "konec": return "konec";
            }

            return depth switch
            {
                1 or 2 => characterId == 199 ? "morda_base" : "morda_base_back",
                3 => "shell",
                19 => "morda_overlay",
                6 => "eye",
                15 => "forelock",
                21 => "horn",
                23 => "magic",
                26 => "helm",
                27 => "helm2",
                29 => "konec",
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
        /// Head slots the unarmoured head does not draw, each keyed by the symbol that anchors it.
        /// </summary>
        /// <remarks>
        /// <para><c>morda_armor</c>(273) frame 1 places eight layers. Three armour frames place a
        /// ninth, tenth or eleventh: a shell at depth 3 (battle, intel), a second helm layer at
        /// depth 27 (polic, spec, encl), and a layer behind the face at depth 1 (tre). None of them
        /// has default art, and that is the picture rather than a gap — the unarmoured head has no
        /// shell. Until now these were the reason eight armours could not be taken apart, so the
        /// whole 273 frame was drawn instead and the eye inside it was frozen at whatever frame the
        /// exporter happened to bake.</para>
        ///
        /// <para>A part still needs a stable key so it can exist, receive a placement and carry an
        /// override, so each slot is keyed by the first symbol the data places in it. That symbol is
        /// <b>only a key</b>: its art is never drawn as a default (it is in <c>noBaseSpriteIds</c>),
        /// because it belongs to one armour and would otherwise appear on every outfit. The art
        /// always arrives as an override.</para>
        ///
        /// <para>Positions come from the same placements — see <c>SeedExtraHeadSlotOffsets</c>: both
        /// shells sit exactly where the face sits, <c>(2, −222)</c> inside 273, and every shape layer
        /// sits at the container's origin with an identity matrix.</para>
        /// </remarks>
        static readonly (string slot, int anchorSymbol, int depth)[] ExtraHeadSlots =
        {
            ("morda_base_back", 262, 1),
            ("shell", 242, 3),
            ("helm2", 266, 27),
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
            /// Consumed as <c>ArmorPartOverride.localOffset</c> — the delta is in the head
            /// container's space, so it is <b>not</b> a root-space offset (see ArmorOffsets).
            /// </summary>
            public Dictionary<string, Dictionary<string, Vector2>> PositionDeltasByArmor = new();

            /// <summary>
            /// Armours whose <c>morda_armor</c>(273) frame cannot be taken apart into our slots: it
            /// places a child at a depth with no slot, or a child with no art at any address.
            /// </summary>
            /// <remarks>
            /// These armours fall back to the frame as a single baked sprite. That is the original's
            /// picture — right geometry, right layers — but with the eye, the mane and the aura glow
            /// baked at their authored colours, so the player's tints do not reach them. It is still
            /// better than decomposing halfway: a frame that loses one of its layers is a *wrong*
            /// picture, while a flat frame is merely an untinted one.
            ///
            /// <para>This set is <b>empty for the shipped data</b>. It used to hold eight armours —
            /// two because their shells sat at depth 3 and six because a layer was a bare
            /// DefineShape — and those two reasons are gone: the depths have slots and the shapes
            /// have PNGs. A non-empty set now means the SWF changed, and the log says which child
            /// caused it.</para>
            /// </remarks>
            public HashSet<string> CompositeArmours = new();

            /// <summary>armorLabel → why it could not be decomposed, for the log.</summary>
            public Dictionary<string, List<string>> UnmappedByArmor = new();
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
                foreach (var p in frame1.Placements.OrderBy(p => p.Depth))
                {
                    string slot = MapInstanceNameToSlot(p.InstanceName, p.Depth, p.CharacterId);
                    if (slot != null)
                    {
                        defaultSlots[slot] = p.CharacterId;
                        defaultPositions[slot] = p.Position;
                    }
                }
            }

            // The three slots with no default art still need an anchor position, so that a frame
            // which moves one of them has a delta to report. Read each from the first frame that
            // places anything there: in the shipped data every shell sits at the face's position and
            // every shape layer at the container's origin, so the first occurrence is the only one.
            foreach (var (slot, anchorSymbol, depth) in ExtraHeadSlots)
            {
                foreach (var frame in mordaArmorSymbol.Frames)
                {
                    var anchor = frame.Placements.FirstOrDefault(p =>
                        p.Depth == depth && p.CharacterId != 199);
                    if (anchor == null) continue;

                    defaultPositions[slot] = anchor.Position;

                    if (anchor.CharacterId != anchorSymbol)
                    {
                        Debug.LogWarning($"[AnimDataGen] Head slot '{slot}' is keyed by sym{anchorSymbol} " +
                            $"but the first frame that places it uses sym{anchor.CharacterId}; the key " +
                            "stays, the position is taken from the data.");
                    }
                    break;
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
                var unmapped = new List<string>();

                foreach (var p in frame.Placements.OrderBy(p => p.Depth))
                {
                    string slot = MapInstanceNameToSlot(p.InstanceName, p.Depth, p.CharacterId);

                    if (slot == null)
                    {
                        unmapped.Add($"depth {p.Depth} sym{p.CharacterId} (no slot)");
                        continue;
                    }

                    // A bare DefineShape has no timeline, but it does have bounds and — since the
                    // eight per-armour layers were exported as shapes — a PNG under
                    // Head/Variants/symbol{id}. Only a child we have neither a sprite nor bounds for
                    // is unimportable; that one has no art at any address.
                    if (!swfData.Symbols.ContainsKey(p.CharacterId) &&
                        !swfData.ShapeBounds.ContainsKey(p.CharacterId))
                    {
                        unmapped.Add($"depth {p.Depth} sym{p.CharacterId} ({slot}, no sprite and no bounds)");
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

                    // Check if this uses a different symbol than the default. A slot with no default
                    // has no "unchanged" case: whatever it places is the variant, because there is
                    // no unarmoured art to fall back to (see ExtraHeadSlots).
                    if (!defaultSlots.TryGetValue(slot, out int defaultSymId) || p.CharacterId != defaultSymId)
                    {
                        variants[slot] = p.CharacterId;
                    }
                }

                if (unmapped.Count > 0)
                {
                    // Nothing partial: a frame that cannot be drawn completely is drawn as one
                    // sprite, which is exactly what the original shows.
                    result.CompositeArmours.Add(armorLabel);
                    result.UnmappedByArmor[armorLabel] = unmapped;
                    Debug.Log($"[AnimDataGen] Armor '{armorLabel}': flat frame — {string.Join("; ", unmapped)}");
                    continue;
                }

                foreach (int symbolId in variants.Values)
                {
                    result.AllVariantSymbolIds.Add(symbolId);
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

            // Slots with no default art are keyed by their anchor symbol (see ExtraHeadSlots).
            foreach (var (extraSlot, anchorSymbol, _) in ExtraHeadSlots)
            {
                if (partIndexLookup.TryGetValue(anchorSymbol, out int extraIdx))
                    slotToPartIndex[extraSlot] = extraIdx;
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

                // Parts and head symbols that have no frame for this armour. Expected, not a fault:
                // `pip` (the starting outfit) has no head frame at all, so the original keeps the
                // unarmoured head and only the body changes. Collected so the log states which parts
                // fell back to frame 1 rather than leaving it to be discovered on screen.
                var labelMisses = new List<string>();

                // Parts whose armour frame exists but draws frame 1's display list unchanged. Not a
                // miss and not an override: the outfit simply does not touch that part.
                var unchangedFrames = new List<string>();

                // Set when a head layer's art is missing from the import. A partial decomposition
                // draws a *wrong* head, so it is turned back into the baked frame below.
                bool headIncomplete = false;

                // --- Body part armor overrides (korpus, neck, legs, etc.) ---
                foreach (var part in parts)
                {
                    if (!part.isArmorAware) continue;
                    if (!partIndexLookup.TryGetValue(part.symbolId, out int partIndex)) continue;

                    // Skip head sub-parts handled below and morda_armor(273) which is
                    // a layout container, not a renderable body part
                    if (DefaultHeadSymbolToSlot.ContainsKey(part.symbolId) || part.symbolId == 273)
                        continue;

                    // Determine the correct sprite frame for this armor on this part.
                    //
                    // ── Label, or nothing. Never an ordinal. ──────────────────────────────
                    // The original picks these frames the same way (fe/Obj.as:67-96):
                    //
                    //     try { m.gotoAndStop(aid); } catch (err) { m.gotoAndStop(1); }
                    //
                    // `aid` is the armour id, i.e. a *frame label*, and a symbol that has no frame
                    // with that label shows frame 1 — the unarmoured art. Every one of these symbols
                    // is called from its own frame1() (`pfe_fla/korpus_18.as:20`,
                    // `morda_26.as:32`, `fleg1_9.as:18`, …), so this is the only selection rule.
                    //
                    // The frame orders are NOT the same across symbols, which is why an ordinal
                    // fallback is not a harmless approximation: `neck`(182) puts kombu on 4 and
                    // chitin on 5 while `korpus`(159) has them the other way round, `fleg1`(78) puts
                    // chitin on 2 and tre on 16, and the five leg symbols have no `tre` frame at
                    // all. `swfbounds labels` prints the whole matrix.
                    if (!TryResolveArmorFrame(partLabelToFrame, part.symbolId, armorId, out int spriteFrame))
                    {
                        // Only worth reporting when the symbol *has* per-armour frames at all. A
                        // symbol with no labels anywhere (helm(31) is one: a single frame, no label)
                        // simply has no armour art, which is not a fallback worth naming.
                        if (partLabelToFrame.ContainsKey(part.symbolId))
                        {
                            labelMisses.Add($"{part.partName}({part.symbolId})");
                        }
                        continue;
                    }

                    // ── A frame that changes nothing is not an override ────────────────────
                    // `Obj.setArmor` is `m.gotoAndStop(aid)` (fe/Obj.as:67-96). A Flash frame with
                    // no PlaceObject of its own *inherits* the previous frame's display list, so a
                    // labelled frame can exist and change nothing at all — and that is exactly what
                    // the armour labels do on parts the outfit does not touch: `sleg1`(55) frame 2 is
                    // the `pip` frame and places nothing, `sleg3`(319) and `sleg3a`(24) the same.
                    //
                    // Drawing such a frame as an override is not a no-op, it is a regression. The
                    // clip keeps its *instance* when the frame changes nothing, so the colour
                    // transform `Obj.setColor(this,0)` put on it (`pfe_fla/sleg10_6.as:18`) survives
                    // and the leg stays fur-coloured. An override is drawn untinted — armour art
                    // carries its own colour — so the pip suit's hind legs came out white on a grey
                    // pony. Four overrides in the whole asset are this: pip/sleg1, pip/sleg3,
                    // pip/sleg3a, chitin/fleg2.
                    //
                    // Not to be confused with `ali`'s aura: that override points at the default
                    // symbol 239 as well, but it is emitted by the head-variant "the armour frame
                    // moves this slot" path further down, to carry a (0.123, -0.157) position delta.
                    // Same sprite, and it must stay.
                    if (FramePlacesTheSameAsFirst(swfData, part.symbolId, spriteFrame))
                    {
                        unchangedFrames.Add($"{part.partName}({part.symbolId})");
                        continue;
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

                // --- morda_armor(273) as a single baked head, for an armour it has a frame for ---
                // Only reached when morda_armor has a frame labelled with this armour but the frame
                // swaps no symbols (so there is nothing to decompose). It must NOT invent a frame:
                // five armours — pip, chitin, kombu, skin, metal — have no frame in symbol 273 at all,
                // and the old `armorIdx + 1` fallback gave them somebody else's head. That is the
                // whole pip bug: `pip` is armour index 1, so it took frame 2, which is labelled
                // 'battle', and the starting outfit wore the battle helmet. In the original,
                // `Obj.setArmor` throws on the missing label, catches, and shows frame 1 — the
                // unarmoured head — which is exactly what "no override" means here.
                bool hasHeadVariants = headVariants != null &&
                    headVariants.VariantsByArmor.ContainsKey(armorId);

                if (!hasHeadVariants && partIndexLookup.TryGetValue(273, out int mordaPartIndex))
                {
                    if (TryResolveArmorFrame(partLabelToFrame, 273, armorId, out int mordaFrame))
                    {
                        if (spriteImport.SpritesBySymbol.TryGetValue(273, out var mordaFrames) &&
                            mordaFrames.TryGetValue(mordaFrame, out var mordaSprite) &&
                            mordaSprite != null)
                        {
                            var mordaPart = parts.FirstOrDefault(p => p.symbolId == 273);
                            Vector2 compositePivot = CompositeFramePivot(
                                swfData, spriteImport, mordaPart?.pivotNormalized ?? new Vector2(0.5f, 0.5f),
                                mordaFrame);
                            overrides.Add(new ArmorPartOverride
                            {
                                partIndex = mordaPartIndex,
                                armorSprite = mordaSprite,
                                pivotOverride = compositePivot,
                            });
                            Debug.Log($"[AnimDataGen] Armor '{armorId}': using morda_armor(273) composite " +
                                $"frame {mordaFrame} pivot ({compositePivot.x:F6},{compositePivot.y:F6}) " +
                                "(no sub-part variants for this armor)");
                        }
                    }
                    else
                    {
                        labelMisses.Add("morda_armor(273)");
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
                                // The zoom the variant's pixels actually measure, not the one the import
                                // window was set to — they differ when the art was re-exported at a
                                // reserve and the slider was left alone (CharacterSpriteImporter.ResolveZoom).
                                int zf = spriteImport.EffectiveZoomBySymbol.TryGetValue(variantSymbolId, out int variantZoom)
                                    ? variantZoom
                                    : spriteImport.ZoomFactor;
                                if (swfBounds.width > 0 && pngW > 1 && pngH > 1)
                                {
                                    // SWF origin is in Flash units; the PNG is rasterised at `zf`
                                    // pixels per Flash unit, so the offset has to be scaled into PNG
                                    // pixels before it is divided by the PNG size. Dividing the raw
                                    // Flash offset by the zoomed PNG size scales every variant pivot
                                    // by 1/zf — invisible at the current zoom 1 export, and wrong by
                                    // 4x the moment the art is re-exported at 4x to fix pixelation.
                                    float correctedPivotX = (0f - swfBounds.x) * zf / pngW;
                                    float correctedPivotY = 1f - ((0f - swfBounds.y) * zf / pngH);
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
                                    localOffset = LocalOffsetFor(headVariants, armorId, slotName),
                                });
                            }
                            else
                            {
                                // Recorded, not just warned: a layer with no sprite would otherwise
                                // disappear without a trace on screen. See the composite fallback
                                // below, which is what this flag buys.
                                headIncomplete = true;
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

                    // Slots the armour frame MOVES without changing their symbol. `ali` is the one
                    // case in the shipped data: its aura is both displaced and scaled up 1.374×, and
                    // because the symbol is the default 239 nothing above would emit an override for
                    // it, so the aura stayed at the unarmoured position. The scale is still not
                    // carried — ArmorPartOverride has no scale field — so only the position is right.
                    // Runs after the hidden list so a moved-and-hidden slot is not resurrected.
                    if (headVariants.PositionDeltasByArmor.TryGetValue(armorId, out var movedSlots))
                    {
                        foreach (var kvp in movedSlots)
                        {
                            string slotName = kvp.Key;
                            if (headVariants.VariantsByArmor.TryGetValue(armorId, out var already) &&
                                already.ContainsKey(slotName))
                            {
                                continue;
                            }

                            if (!slotToPartIndex.TryGetValue(slotName, out int partIndex)) continue;
                            if (set.hiddenHeadParts != null && Array.IndexOf(set.hiddenHeadParts, slotName) >= 0) continue;
                            // Only the slots with default art can reach this: a slot with no default
                            // always records a variant, so it is handled above and skipped here.
                            if (!SlotToDefaultSymbol.TryGetValue(slotName, out int defaultSymbolId)) continue;

                            if (spriteImport.SpritesBySymbol.TryGetValue(defaultSymbolId, out var defaultFrames) &&
                                defaultFrames.TryGetValue(1, out var defaultSprite) &&
                                defaultSprite != null)
                            {
                                var part = parts.FirstOrDefault(p => p.symbolId == defaultSymbolId);
                                overrides.Add(new ArmorPartOverride
                                {
                                    partIndex = partIndex,
                                    armorSprite = defaultSprite,
                                    pivotOverride = part?.pivotNormalized ?? new Vector2(0.5f, 0.5f),
                                    localOffset = LocalOffsetFor(headVariants, armorId, slotName),
                                });
                            }
                        }
                    }
                }

                // ── A head we could not draw completely is drawn as one sprite ────────────
                // The same rule the exporter applies to a frame with an unmappable child, moved
                // here because "the art is missing" is only knowable once the sprites are in hand.
                // Reached only when a variant's PNG is absent from the import; without this the
                // layer would simply not be drawn, and a missing layer and a missing feature look
                // identical on screen.
                if (headIncomplete)
                {
                    bool composited = false;

                    if (partIndexLookup.TryGetValue(273, out int fallbackPartIndex) &&
                        TryResolveArmorFrame(partLabelToFrame, 273, armorId, out int fallbackFrame) &&
                        spriteImport.SpritesBySymbol.TryGetValue(273, out var fallbackFrames) &&
                        fallbackFrames.TryGetValue(fallbackFrame, out var fallbackSprite) &&
                        fallbackSprite != null)
                    {
                        // Drop the head overrides already added: the baked frame draws all of them.
                        overrides.RemoveAll(o => slotToPartIndex.ContainsValue(o.partIndex));

                        var mordaPart = parts.FirstOrDefault(p => p.symbolId == 273);
                        overrides.Add(new ArmorPartOverride
                        {
                            partIndex = fallbackPartIndex,
                            armorSprite = fallbackSprite,
                            pivotOverride = CompositeFramePivot(swfData, spriteImport,
                                mordaPart?.pivotNormalized ?? new Vector2(0.5f, 0.5f), fallbackFrame),
                        });
                        composited = true;

                        Debug.LogWarning($"[AnimDataGen] Armor '{armorId}': a head layer has no " +
                            $"sprite, so morda_armor(273) frame {fallbackFrame} is drawn whole " +
                            "(right picture, frozen eye)");
                    }

                    if (!composited)
                    {
                        Debug.LogError($"[AnimDataGen] Armor '{armorId}': a head layer has no sprite " +
                            "and morda_armor(273) has no frame to fall back to — the head will be " +
                            "drawn without that layer");
                    }
                }

                set.partOverrides = overrides.ToArray();
                armorSets.Add(set);

                if (labelMisses.Count > 0)
                {
                    Debug.Log($"[AnimDataGen] Armor '{armorId}': no frame for [{string.Join(", ", labelMisses)}] " +
                        "-> those keep frame 1 (unarmoured), as Obj.setArmor's catch does (fe/Obj.as:90-95)");
                }

                if (unchangedFrames.Count > 0)
                {
                    Debug.Log($"[AnimDataGen] Armor '{armorId}': frame changes nothing on " +
                        $"[{string.Join(", ", unchangedFrames)}] -> no override, so the part keeps its tint " +
                        "(the frame inherits frame 1's display list; the clip's colour transform survives)");
                }
            }

            return armorSets;
        }

        /// <summary>
        /// The frame a symbol draws for <paramref name="armorId"/>, resolved by <b>label only</b>.
        ///
        /// <para><b>Why there is no ordinal fallback.</b> The original resolves armour art with
        /// <c>m.gotoAndStop(aid)</c> inside a try/catch (<c>fe/Obj.as:67-96</c>), where <c>aid</c> is
        /// the armour id used as a frame label. A symbol with no such label throws, and the catch
        /// shows frame 1 — the unarmoured art. So "no label" is a real, meaningful answer, and the
        /// frame numbers are not interchangeable between symbols: <c>korpus</c>(159) has pip=2, tre=3,
        /// chitin=4, while <c>neck</c>(182) has kombu=4, chitin=5 and <c>fleg1</c>(78) has chitin=2,
        /// pip=3, tre=16. An <c>armourIndex + 1</c> guess therefore silently draws a different
        /// armour's art — which is what put the <c>battle</c> helmet on the <c>pip</c> outfit, since
        /// <c>morda_armor</c>(273) has no pip frame and frame 2 is labelled 'battle'.</para>
        /// </summary>
        static bool TryResolveArmorFrame(
            Dictionary<int, Dictionary<string, int>> partLabelToFrame,
            int symbolId,
            string armorId,
            out int frame)
        {
            frame = 0;
            return partLabelToFrame.TryGetValue(symbolId, out var labelMap) &&
                   labelMap.TryGetValue(armorId, out frame);
        }

        /// <summary>
        /// Whether frame <paramref name="frameNumber"/> of <paramref name="symbolId"/> draws exactly
        /// what frame 1 draws — i.e. the armour frame changed nothing.
        ///
        /// <para><b>This is the rule, not a shortcut.</b> Flash frames inherit the display list, so a
        /// labelled frame may place no objects at all and still be a valid <c>gotoAndStop</c> target;
        /// <c>sleg1</c>(55) frame 2 (<c>pip</c>) is one. The clip keeps its instance across such a
        /// frame, so the tint its art set in <c>frame1()</c> is still on it, and the original draws the
        /// unarmoured leg in the pony's coat colour. The exporter still writes that frame as a PNG, so
        /// "the armour has a frame here" is true and "the armour changes anything" is false — and only
        /// the second one justifies an override.</para>
        ///
        /// <para><b>Compared field by field, not by count.</b> A frame that re-places the same child
        /// at a different transform changes the picture just as much as a different child does, so
        /// depth, character, position, rotation, scale and colour transform all have to match before
        /// the frame counts as unchanged. Colour is included because an override cannot express a
        /// tint — it is drawn at <c>Color.white</c> — so a frame that only re-tints must still count
        /// as a change, or the tint would be dropped rather than re-applied.</para>
        /// </summary>
        static bool FramePlacesTheSameAsFirst(SWFFile swfData, int symbolId, int frameNumber)
        {
            if (frameNumber <= 1) return true;
            if (swfData?.Symbols == null || !swfData.Symbols.TryGetValue(symbolId, out SWFSymbol symbol))
            {
                return false;
            }

            SWFFrame first = FindFrame(symbol, 1);
            SWFFrame other = FindFrame(symbol, frameNumber);
            if (first?.Placements == null || other?.Placements == null) return false;
            if (first.Placements.Count != other.Placements.Count) return false;

            for (int i = 0; i < first.Placements.Count; i++)
            {
                SWFPlacement a = first.Placements[i];
                SWFPlacement b = other.Placements[i];

                if (a.Depth != b.Depth || a.CharacterId != b.CharacterId) return false;
                if ((a.Position - b.Position).sqrMagnitude > 1e-6f) return false;
                if (Mathf.Abs(a.Rotation - b.Rotation) > 1e-3f) return false;
                if ((a.Scale - b.Scale).sqrMagnitude > 1e-6f) return false;

                // A frame that only re-tints a child still changes the picture, and an override
                // cannot express a tint (it is drawn at Color.white), so treat it as a change and
                // keep the override rather than silently dropping the frame.
                if (a.HasColorTransform != b.HasColorTransform) return false;
                if (a.HasColorTransform &&
                    (ColorDiffers(a.ColorMultiply, b.ColorMultiply) ||
                     ColorDiffers(a.ColorAdd, b.ColorAdd)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether two colours differ by more than a rounding error on any channel.</summary>
        static bool ColorDiffers(Color a, Color b)
        {
            const float epsilon = 1e-4f;
            return Mathf.Abs(a.r - b.r) > epsilon ||
                   Mathf.Abs(a.g - b.g) > epsilon ||
                   Mathf.Abs(a.b - b.b) > epsilon ||
                   Mathf.Abs(a.a - b.a) > epsilon;
        }

        /// <summary>The frame whose <c>FrameNumber</c> is <paramref name="frameNumber"/>, or null.</summary>
        static SWFFrame FindFrame(SWFSymbol symbol, int frameNumber)
        {
            if (symbol?.Frames == null) return null;

            foreach (SWFFrame frame in symbol.Frames)
            {
                if (frame.FrameNumber == frameNumber) return frame;
            }

            return null;
        }

        /// <summary>
        /// The pivot for one frame of <c>morda_armor</c>(273), normalised inside that frame's own
        /// exported crop.
        ///
        /// <para><b>Why frame 1's pivot cannot be reused.</b> Part 273's stored pivot is the
        /// origin's position inside <i>frame 1's</i> content bounds: <c>0.7191358</c> is exactly
        /// <c>93.2 / 129.6</c> and <c>0.19850056</c> is <c>1 - 112.25 / 140.05</c>, both read
        /// straight off the SWF. Every armour frame is a different picture with its own bounds, so
        /// the origin sits at a different normalised point in each. Reusing frame 1's value pins
        /// the sprite by the wrong point and slides the whole head: measured against the shipped
        /// frames the error reaches 10.4 px on battle/polic/spec and 7.3 px vertically on intel —
        /// about 0.021 world units, the same size as the per-armour helmet offsets this file also
        /// emits, so it is a real defect and not a rounding detail.</para>
        ///
        /// <para><b>How frame N's bounds are recovered.</b> The exporter crops each frame to its own
        /// content, so the difference between two frames' sprite rects equals the difference
        /// between their bounds and the constant padding cancels. Only frame 1's bounds have to
        /// come from the SWF, and frame 1 must reproduce the stored pivot — it does, exactly.</para>
        /// </summary>
        static Vector2 CompositeFramePivot(
            SWFFile swfData,
            CharacterSpriteImporter.ImportResult spriteImport,
            Vector2 defaultPivot,
            int frame)
        {
            if (!swfData.Frame1Bounds.TryGetValue(273, out Rect bounds1) ||
                bounds1.width <= 0f || bounds1.height <= 0f)
            {
                return defaultPivot;
            }

            if (!spriteImport.SpritesBySymbol.TryGetValue(273, out var frames) ||
                !frames.TryGetValue(1, out Sprite sprite1) || sprite1 == null ||
                !frames.TryGetValue(frame, out Sprite spriteN) || spriteN == null)
            {
                return defaultPivot;
            }

            Rect rect1 = sprite1.textureRect;
            Rect rectN = spriteN.textureRect;
            if (rectN.width <= 0f || rectN.height <= 0f) return defaultPivot;

            float zoom = spriteImport.EffectiveZoomBySymbol.TryGetValue(273, out int z)
                ? z
                : spriteImport.ZoomFactor;
            if (zoom <= 0f) zoom = 1f;

            float x = bounds1.x + (rectN.x - rect1.x) / zoom;
            float width = bounds1.width + (rectN.width - rect1.width) / zoom;
            // textureRect.y counts up from the canvas bottom, while bounds.y is the top edge, so the
            // top of the crop is rect.y + rect.height.
            float y = bounds1.y + ((rect1.y + rect1.height) - (rectN.y + rectN.height)) / zoom;
            float height = bounds1.height + (rectN.height - rect1.height) / zoom;

            if (width <= 0f || height <= 0f) return defaultPivot;

            return new Vector2(-x / width, 1f + y / height);
        }

        /// <summary>
        /// The head-container-space offset this armour gives a slot, or zero if it does not move it.
        /// </summary>
        /// <remarks>
        /// Only the Flash→Unity axis flip happens here. The head's rotation and scale are applied by
        /// the renderers (<c>ArmorOffsets.LocalToRoot</c>) because they are per-frame: folding them
        /// in would bake the stay pose into every frame, which is the fault that made the old
        /// root-space <c>positionOffset</c> unusable for head sub-parts.
        /// </remarks>
        static Vector2 LocalOffsetFor(HeadArmorVariantData headVariants, string armorId, string slotName)
        {
            if (!headVariants.PositionDeltasByArmor.TryGetValue(armorId, out var deltas) ||
                !deltas.TryGetValue(slotName, out Vector2 flashDelta))
            {
                return Vector2.zero;
            }

            return new Vector2(flashDelta.x / 100f, -flashDelta.y / 100f);
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
