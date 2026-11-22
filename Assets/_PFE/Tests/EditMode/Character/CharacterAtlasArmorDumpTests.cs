using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using PFE.Character;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// The in-game report: a character with a worn armour set draws flat tan rectangles where the
    /// armoured parts should be, while an unarmoured character in the importer preview is correct.
    /// This fixture is the microscope for that report, not a regression gate — it builds the atlas
    /// the way the assembler builds it (same <see cref="CharacterQuadAssembler.Setup"/> entry, same
    /// editor <see cref="CharacterQuadAtlas.Build"/> route, because the cache is play-mode only) and
    /// writes the texture plus a region table to <c>Temp/quad_atlas_dump</c> so the pixels can be
    /// compared against the source PNGs instead of inferred from a screenshot.
    ///
    /// <para><b>The two failure modes it separates.</b> A visible quad can hold a window into the
    /// atlas (a) that was never rewritten from the sprite's own-texture UVs — in which case the quad
    /// samples the atlas at coordinates meant for a different texture, which is exactly the flat-
    /// block picture docs/CharacterImportPipeline/04 describes — or (b) whose region holds wrong
    /// pixels. The first is caught here by asserting every reachable sprite is packed and every drawn
    /// slot's window equals its region; the second is caught by looking at the dump.</para>
    /// </summary>
    [TestFixture]
    public class CharacterAtlasArmorDumpTests
    {
        const string DefinitionPath = "Characters/PlayerAnimationDefinition";
        const string StylePath = "Character/CharacterStyleData";

        /// <summary>Absolute on purpose: the test runner's working directory is not the project
        /// root in batch mode, and a relative <c>Temp/...</c> landed the dumps somewhere
        /// unfindable. <c>Application.dataPath</c> is the project-root-anchored path that exists in
        /// both the editor and batch mode. Overridable through <c>PFE_ATLAS_DUMP_DIR</c> because
        /// the editor deletes <c>Temp</c> on exit — a batch run that must be audited after the
        /// process ends points the variable at a directory that survives.</summary>
        static string DumpDir =>
            Environment.GetEnvironmentVariable("PFE_ATLAS_DUMP_DIR")
            ?? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Temp", "quad_atlas_dump");

        /// <summary>The sets named by <c>PlayerDebugEditorOverlay.VisualArmorIds</c>, minus the ones
        /// whose art is a recolour of another set — a spread is enough, one dump per set is not.</summary>
        static readonly string[] ArmorIds = { "battle", "metal", "pip", "kombu" };

        [Test]
        public void DumpArmoredAtlases_ForInspection()
        {
            var definition = Resources.Load<CharacterAnimationDefinition>(DefinitionPath);
            var style = Resources.Load<CharacterStyleData>(StylePath);
            Assert.IsNotNull(definition, $"no definition at Resources/{DefinitionPath}");
            Assert.IsNotNull(style, $"no style data at Resources/{StylePath}");

            Directory.CreateDirectory(DumpDir);

            var summaries = new List<string>();
            foreach (string armorId in ArmorIds)
            {
                summaries.Add(DumpOne(definition, style, armorId));
            }

            // Assert.Pass carries the per-set numbers into the XML result, which TestContext.Progress
            // does not reliably reach under batch mode.
            Assert.Pass(string.Join(" | ", summaries));
        }

        static string DumpOne(CharacterAnimationDefinition definition, CharacterStyleData style, string armorId)
        {
            var go = new GameObject($"atlas_dump_{armorId}");
            try
            {
                CharacterQuadAssembler assembler = go.AddComponent<CharacterQuadAssembler>();
                var context = new CharacterVisualContext { armorId = armorId };
                assembler.Setup(definition, style, CharacterAppearance.CreateDefault(), context);

                CharacterQuadAtlas atlas = assembler.Atlas;
                Assert.IsNotNull(atlas,
                    $"[{armorId}] the assembler built no atlas at all — SkippedCount and the per-texture " +
                    "fallback are the only reasons, and the fallback draws one material for the whole mesh.");
                Assert.IsTrue(atlas.IsReady, $"[{armorId}] atlas not ready: {atlas.Error}");

                // ── Invariant 1: every sprite the character can draw is in the atlas ──
                // A sprite that is not packed keeps its own-texture UV window while the whole mesh
                // binds the atlas material — that quad then samples the atlas at coordinates meant
                // for another texture, which is the flat-rectangle bug in one sentence.
                var reachable = new List<Sprite>();
                CollectReachable(definition, style, armorId, reachable);

                var missing = new List<string>();
                foreach (Sprite sprite in reachable)
                {
                    if (!atlas.TryGetUvRect(sprite, out _))
                    {
                        Texture tex = sprite != null ? sprite.texture : null;
                        missing.Add($"{tex?.name} rect={sprite?.textureRect}");
                    }
                }

                Assert.IsEmpty(missing,
                    $"[{armorId}] {missing.Count} of {reachable.Count} reachable sprites are NOT in the " +
                    "atlas but the mesh binds the atlas material — those parts draw flat garbage " +
                    "(first misses: " + string.Join("; ", missing.GetRange(0, Mathf.Min(4, missing.Count))) + ")");

                // ── Invariant 2: every drawn slot's UV window is its atlas region ──
                // A slot whose window was never rewritten samples the atlas with its source-texture
                // fractions — same bug as invariant 1, arrived at through the slot state instead of
                // the sprite set.
                var badWindows = new List<string>();
                if (definition.parts != null)
                {
                    foreach (CharacterPartDefinition part in definition.parts)
                    {
                        if (part == null || string.IsNullOrEmpty(part.partName)) continue;
                        CharacterQuadAssembler.QuadSlot slot = assembler.GetQuadSlot(part.partName);
                        if (slot == null || !slot.visible || slot.sprite == null) continue;

                        if (!atlas.TryGetUvRect(slot.sprite, out Vector4 uv))
                        {
                            badWindows.Add($"{part.partName}: sprite not packed");
                            continue;
                        }

                        const float tolerance = 0.002f;
                        if (Mathf.Abs(slot.quad.UMin - uv.x) > tolerance
                            || Mathf.Abs(slot.quad.VMin - uv.y) > tolerance
                            || Mathf.Abs(slot.quad.UMax - uv.z) > tolerance
                            || Mathf.Abs(slot.quad.VMax - uv.w) > tolerance)
                        {
                            badWindows.Add(
                                $"{part.partName}: slot window ({slot.quad.UMin},{slot.quad.VMin})-" +
                                $"({slot.quad.UMax},{slot.quad.VMax}) != atlas region {uv}");
                        }
                    }
                }

                Assert.IsEmpty(badWindows,
                    $"[{armorId}] drawn slots whose UV window is not their atlas region " +
                    "(first: " + string.Join("; ", badWindows.GetRange(0, Mathf.Min(4, badWindows.Count))) + ")");

                // ── The dump: pixels + region table, the evidence either way ──
                byte[] png = atlas.Texture.EncodeToPNG();
                string pngPath = Path.Combine(DumpDir, $"armor_{armorId}.png");
                File.WriteAllBytes(pngPath, png);

                var tsv = new StringBuilder();
                tsv.Append("index\ttexture\ttexW\ttexH\tsrcX\tsrcY\tsrcW\tsrcH\tdstX\tdstY\tdstW\tdstH\tuMin\tvMin\tuMax\tvMax\n");
                for (int i = 0; i < atlas.RegionCount; i++)
                {
                    CharacterAtlasRegionSource src = atlas.GetRegionSource(i);
                    CharacterAtlasRect dst = atlas.GetRegionRect(i);
                    Vector4 uv = atlas.GetRegionUv(i);
                    tsv.Append($"{i}\t{src.TextureName}\t{src.TextureWidth}\t{src.TextureHeight}");
                    tsv.Append($"\t{src.Rect.X}\t{src.Rect.Y}\t{src.Rect.Width}\t{src.Rect.Height}");
                    tsv.Append($"\t{dst.X}\t{dst.Y}\t{dst.Width}\t{dst.Height}");
                    tsv.Append($"\t{uv.x:F6}\t{uv.y:F6}\t{uv.z:F6}\t{uv.w:F6}\n");
                }
                File.WriteAllText(Path.Combine(DumpDir, $"armor_{armorId}.tsv"), tsv.ToString());

                return
                    $"[{armorId}] atlas {atlas.Width}², regions {atlas.RegionCount}, reachable {reachable.Count}, " +
                    $"skipped {atlas.SkippedCount}, missing-edge {atlas.RegionsMissingAnEdge}, " +
                    $"empty {atlas.RegionsEmpty}, route {atlas.CopyRoute}, mips {atlas.MipCount} -> {pngPath}";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>Every sprite <see cref="CharacterQuadAssembler"/> packs for this costume — the
        /// same four groups as its CollectSprites, so "reachable" cannot drift from what the build
        /// was asked for.</summary>
        static void CollectReachable(
            CharacterAnimationDefinition definition, CharacterStyleData style, string armorId, List<Sprite> into)
        {
            if (definition.parts != null)
            {
                foreach (CharacterPartDefinition part in definition.parts)
                {
                    if (part == null) continue;
                    if (part.baseSprite != null) into.Add(part.baseSprite);
                    if (part.styleVariants != null)
                    {
                        foreach (Sprite variant in part.styleVariants)
                        {
                            if (variant != null) into.Add(variant);
                        }
                    }
                }
            }

            if (definition.armorSets != null)
            {
                ArmorVisualSet worn = definition.GetArmorSet(armorId);
                if (worn?.partOverrides != null)
                {
                    foreach (ArmorPartOverride partOverride in worn.partOverrides)
                    {
                        if (partOverride.armorSprite != null) into.Add(partOverride.armorSprite);
                    }
                }
            }

            if (definition.overlays != null)
            {
                foreach (CharacterOverlayDefinition overlay in definition.overlays)
                {
                    if (overlay?.frames == null) continue;
                    foreach (Sprite frame in overlay.frames)
                    {
                        if (frame != null) into.Add(frame);
                    }
                }
            }

            if (style?.layers != null)
            {
                foreach (StyleLayerGroup group in style.layers)
                {
                    if (group?.sprites == null) continue;
                    foreach (Sprite styleSprite in group.sprites)
                    {
                        if (styleSprite != null) into.Add(styleSprite);
                    }
                }
            }
        }
    }
}
