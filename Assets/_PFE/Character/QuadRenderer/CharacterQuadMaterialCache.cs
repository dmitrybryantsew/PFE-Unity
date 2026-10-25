using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace PFE.Character
{
    /// <summary>
    /// Per-assembler cache of the one material each part texture needs, and of the bilinear copy of
    /// that texture when the import settings say Point.
    ///
    /// <para><b>Why a copy instead of a filter setting.</b> <see cref="FilterMode"/> is a property of
    /// the <i>texture asset</i>, not of a material, so a renderer cannot ask for bilinear sampling
    /// without either re-importing the PNG (owner-only asset work, and it would change the shipped
    /// path too) or sampling a different texture. The character art is vector rasterised and is drawn
    /// at 0.72×–1.08× on a 1080p screen, where Point drops every seventh row; Bilinear is the cheap
    /// approximation to the vector original. So each part texture gets a GPU-side copy with
    /// <c>filterMode = Bilinear</c>, made once, owned by this cache.</para>
    ///
    /// <para><b>GPU-side, not <c>GetPixels</c>.</b> <c>Graphics.CopyTexture</c> never leaves the GPU
    /// and does not require the source to be readable, which matters because the imported PNGs are
    /// not. The copy keeps the source's <c>graphicsFormat</c> so no conversion happens either.</para>
    ///
    /// <para><b>Owned, not static.</b> A static cache survives play-mode exit and leaks its textures
    /// and materials; the previous shader singleton in this project has the same shape and only
    /// survives because it is one material. One cache per assembler, disposed in
    /// <c>OnDestroy</c>.</para>
    /// </summary>
    public sealed class CharacterQuadMaterialCache : IDisposable
    {
        readonly Dictionary<Texture, Material> _materials = new();
        readonly Dictionary<Texture2D, Texture2D> _bilinearCopies = new();

        Shader _shader;
        bool _shaderResolved;

        /// <summary>
        /// The material that draws <paramref name="texture"/>, creating it on first use.
        /// </summary>
        /// <param name="bilinear">
        /// When true, samples a bilinear copy of the texture; when false, the imported texture as-is
        /// (Point, i.e. exactly what the shipped sprite path samples).
        /// </param>
        public Material GetMaterial(Texture2D texture, bool bilinear)
        {
            if (texture == null)
            {
                return null;
            }

            Texture sampled = bilinear ? GetBilinearCopy(texture) : texture;
            if (sampled == null)
            {
                sampled = texture;
            }

            if (_materials.TryGetValue(sampled, out Material cached) && cached != null)
            {
                return cached;
            }

            Shader shader = ResolveShader();
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader)
            {
                name = $"CharacterQuad_{texture.name}",
                hideFlags = HideFlags.HideAndDontSave
            };
            material.mainTexture = sampled;
            material.SetColor("_Color", Color.white);

            _materials[sampled] = material;
            return material;
        }

        /// <summary>
        /// The texture a renderer should sample for this sprite. Exposed so the preview window can
        /// report which filter is actually in force rather than which one was requested.
        /// </summary>
        public Texture GetSampledTexture(Texture2D texture, bool bilinear)
        {
            if (texture == null)
            {
                return null;
            }

            if (!bilinear)
            {
                return texture;
            }

            return GetBilinearCopy(texture) ?? texture;
        }

        Texture2D GetBilinearCopy(Texture2D source)
        {
            if (source == null)
            {
                return null;
            }

            // The character import policy (CharacterTextureImportPolicy) now imports every part as
            // bilinear with a mip chain, so this copy has nothing left to impose — and it would do
            // damage if it ran: the copy is created with TextureCreationFlags.None and only mip 0 is
            // copied, so the quad side would sample a texture that has *lost* the mip chain the
            // imported one has. At the body parts' 4.8x minification that is visible aliasing the
            // sprite side does not have, which makes the A/B compare two different filters.
            // Sampling the imported texture directly is cheaper and correct; the copy stays for
            // textures that are still Point.
            if (source.filterMode == FilterMode.Bilinear)
            {
                return source;
            }

            if (_bilinearCopies.TryGetValue(source, out Texture2D cached))
            {
                // A destroyed copy (domain reload, asset unload) is stored as null and re-made below.
                if (cached != null)
                {
                    return cached;
                }
                _bilinearCopies.Remove(source);
            }

            Texture2D copy = null;
            try
            {
                copy = new Texture2D(source.width, source.height, source.graphicsFormat, TextureCreationFlags.None)
                {
                    name = $"{source.name} (bilinear)",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = source.wrapMode,
                    anisoLevel = source.anisoLevel,
                    hideFlags = HideFlags.HideAndDontSave
                };

                // Mip 0 only: the copy is created without a mip chain, and the simple overload copies
                // every mip the source has.
                Graphics.CopyTexture(source, 0, 0, copy, 0, 0);
            }
            catch (Exception exception)
            {
                // Compressed formats, platform quirks and destroyed sources all land here. Falling back
                // to the imported texture keeps the character drawable (Point, as shipped) instead of
                // magenta — a wrong filter is a much smaller failure than an invisible character.
                Debug.LogWarning(
                    $"[CharacterQuad] Could not build a bilinear copy of '{source.name}', falling back to " +
                    $"the imported texture: {exception.Message}");

                if (copy != null)
                {
                    DestroyObject(copy);
                    copy = null;
                }
            }

            _bilinearCopies[source] = copy;
            return copy;
        }

        Shader ResolveShader()
        {
            if (_shaderResolved)
            {
                return _shader;
            }

            _shaderResolved = true;

            // Same lookup order as SpritePresenterMaterial: under URP the built-in Sprites/Default is
            // the wrong shader, so the URP sprite shader must be tried first.
            _shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                   ?? Shader.Find("Sprites/Default");

            if (_shader == null)
            {
                Debug.LogError("[CharacterQuad] No sprite shader found; the quad renderer cannot draw.");
            }

            return _shader;
        }

        /// <summary>Drops every cached material and copy. Safe to call more than once.</summary>
        public void Dispose()
        {
            foreach (Material material in _materials.Values)
            {
                DestroyObject(material);
            }
            _materials.Clear();

            foreach (Texture2D copy in _bilinearCopies.Values)
            {
                DestroyObject(copy);
            }
            _bilinearCopies.Clear();
        }

        /// <summary>
        /// <c>Destroy</c> is deferred and never runs in edit mode, where the preview window lives;
        /// <c>DestroyImmediate</c> is illegal while playing. This picks the right one for both.
        /// </summary>
        internal static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
