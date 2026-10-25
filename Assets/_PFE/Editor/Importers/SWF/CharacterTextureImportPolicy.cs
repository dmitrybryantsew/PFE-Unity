#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// The character sampler policy — how a frozen vector part is sampled once it has become a bitmap.
    ///
    /// <para><b>Why this exists.</b> A body part is not pixel art. It is a vector shape that JPEXS
    /// rasterised at the artist's drawing scale, so its anti-aliased edge is a property of the
    /// rasteriser, not of the design. Drawing such a bitmap at ≈1:1 with <see cref="FilterMode.Point"/>
    /// re-quantises every one of those edges onto the screen grid, which is exactly the stair-stepping
    /// the A/B window shows on the legs; and drawing the 4.5×-oversampled body parts with Point
    /// <em>down</em>samples them with no filter at all, which aliases. Point is wrong at both ends of
    /// this project's own density distribution.</para>
    ///
    /// <para>The reference port (Remains-Unity) reaches the same conclusion and enforces it with a
    /// postprocessor rather than inside each importer, so a re-import cannot silently undo it — see
    /// <c>docs/CharacterImportPipeline/02_what_remains_did_differently.md</c> §2 and §4.</para>
    ///
    /// <para><b>Scope.</b> Sampler settings only. <c>textureType</c>, <c>spritePixelsPerUnit</c> and
    /// <c>spritePivot</c> are deliberately NOT set here: they depend on the symbol's SWF bounds and on
    /// the export zoom the art happens to be at, which a path-only postprocessor cannot know.
    /// <see cref="CharacterSpriteImporter"/> owns those.</para>
    /// </summary>
    public sealed class CharacterTextureImportPolicy : AssetPostprocessor
    {
        public const string Root = "Assets/_PFE/Art/Character/";

        /// <summary>Any PNG under the character art root, at any depth (parts live in <c>Legs/Front/fleg1/</c>).</summary>
        public static bool Applies(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string path = assetPath.Replace('\\', '/');
            return path.StartsWith(Root, StringComparison.Ordinal) &&
                   path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        }

        // ── The policy, in one place ──────────────────────────────────────────
        // Changing a value here changes every character texture on the next re-import; nothing else
        // in the project may set these four on a character texture.

        public const FilterMode Filter = FilterMode.Bilinear;
        public const bool Mipmaps = true;
        public const TextureImporterMipFilter MipFilter = TextureImporterMipFilter.KaiserFilter;
        public const bool AlphaDilation = true;

        /// <summary>
        /// <b>Deliberate divergence from the reference port.</b> Remains uses <c>CompressedHQ</c> (BC7)
        /// because its pony art is exported at a 2–4× reserve, so it has texels to spare. Ours is at a
        /// 1× reserve — the smallest front-leg segment is 5×7 px with 5×7 of ink — and at ≈0.86 texels
        /// per screen pixel it is <em>magnified</em>. Block-compressing a 5×7 bitmap that is then
        /// magnified spends quality the art does not have. Revisit only together with a reserve
        /// re-export, and never in the same change as a sampler switch: one variable at a time is what
        /// makes the A/B readable.
        /// </summary>
        public const TextureImporterCompression Compression = TextureImporterCompression.Uncompressed;

        void OnPreprocessTexture()
        {
            if (!Applies(assetPath)) return;
            ApplySampler((TextureImporter)assetImporter);
        }

        /// <summary>Apply every sampler setting from this file's constants. Idempotent.</summary>
        public static void ApplySampler(TextureImporter importer)
        {
            if (importer == null) return;
            importer.textureType = TextureImporterType.Sprite;   // the shipped path renders SpriteRenderers
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.alphaIsTransparency = AlphaDilation;
            importer.mipmapEnabled = Mipmaps;
            importer.mipmapFilter = MipFilter;
            importer.filterMode = Filter;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = 2048;
            importer.textureCompression = Compression;
        }

        /// <summary>
        /// True when the importer already matches the policy. Read from the importer, so callers that
        /// need "did this reach disk?" must check the .meta instead — the importer is updated in memory
        /// even when the write failed (<see cref="CharacterSpriteImporter"/> pass 2 documents this).
        /// </summary>
        public static bool Complies(TextureImporter importer)
        {
            return importer != null &&
                   importer.textureType == TextureImporterType.Sprite &&
                   importer.npotScale == TextureImporterNPOTScale.None &&
                   importer.alphaIsTransparency == AlphaDilation &&
                   importer.mipmapEnabled == Mipmaps &&
                   importer.mipmapFilter == MipFilter &&
                   importer.filterMode == Filter &&
                   importer.wrapMode == TextureWrapMode.Clamp &&
                   importer.maxTextureSize >= 2048 &&
                   importer.textureCompression == Compression;
        }

        static string[] CharacterTexturePaths()
        {
            var paths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Root.TrimEnd('/') }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Applies(path)) paths.Add(path);
            }
            paths.Sort(StringComparer.Ordinal);
            return paths.ToArray();
        }

        /// <summary>
        /// Report-only. Names every character texture that does not match the policy and changes
        /// nothing — run this before <see cref="ReimportAll"/> so the scope of the change is known.
        /// </summary>
        [MenuItem("PFE/Art/Character Sampler/Audit (report only)", priority = 300)]
        public static void Audit()
        {
            string[] paths = CharacterTexturePaths();
            var offenders = new List<string>();
            foreach (string path in paths)
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (!Complies(importer)) offenders.Add(path);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[CharacterSampler] {paths.Length} character textures, " +
                          $"{offenders.Count} do not match the policy " +
                          $"(filter={Filter}, mipmaps={Mipmaps}/{MipFilter}, dilation={AlphaDilation}, " +
                          $"compression={Compression}).");
            const int Show = 20;
            for (int i = 0; i < offenders.Count && i < Show; i++)
            {
                var importer = AssetImporter.GetAtPath(offenders[i]) as TextureImporter;
                sb.AppendLine($"  {offenders[i]}  filter={importer?.filterMode} " +
                              $"mips={importer?.mipmapEnabled} mipFilter={importer?.mipmapFilter} " +
                              $"dilation={importer?.alphaIsTransparency} compression={importer?.textureCompression}");
            }
            if (offenders.Count > Show) sb.AppendLine($"  … and {offenders.Count - Show} more.");

            if (offenders.Count == 0) Debug.Log(sb.ToString());
            else Debug.LogWarning(sb.ToString());
        }

        /// <summary>
        /// Explicit authoring step: force a re-import of every character texture so the policy reaches
        /// files whose pixels did not change. Unity only re-imports a texture when its source or its
        /// importer settings change, so a policy edit alone leaves untouched assets at their old
        /// sampler. Owner-run: this rewrites hundreds of .meta files.
        /// </summary>
        [MenuItem("PFE/Art/Character Sampler/Reimport All", priority = 301)]
        public static void ReimportAll()
        {
            string[] paths = CharacterTexturePaths();
            if (paths.Length == 0)
            {
                Debug.LogWarning($"[CharacterSampler] No character textures under {Root}.");
                return;
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in paths)
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            int compliant = 0;
            foreach (string path in paths)
                if (Complies(AssetImporter.GetAtPath(path) as TextureImporter)) compliant++;

            string summary = $"[CharacterSampler] Re-imported {paths.Length} character textures; " +
                             $"{compliant}/{paths.Length} now match the policy.";
            if (compliant == paths.Length) Debug.Log(summary);
            else Debug.LogWarning(summary + " Re-run the audit to see which refused to change.");
        }
    }
}
#endif
