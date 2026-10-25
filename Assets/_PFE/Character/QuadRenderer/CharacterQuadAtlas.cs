using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace PFE.Character
{
    /// <summary>One placed rectangle inside an atlas, in pixels, Y up from the bottom-left.</summary>
    public readonly struct CharacterAtlasRect
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Width;
        public readonly int Height;

        public CharacterAtlasRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public bool IsValid => Width > 0 && Height > 0;

        /// <summary>The UV window this rect occupies in an atlas of <paramref name="atlasWidth"/> ×
        /// <paramref name="atlasHeight"/>. <c>(uMin, vMin, uMax, vMax)</c>, bottom-up V — the same
        /// convention <see cref="CharacterQuadGeometry.Build"/> writes, so a quad can take these
        /// verbatim.</summary>
        public Vector4 ToUv(int atlasWidth, int atlasHeight)
        {
            float w = Mathf.Max(1, atlasWidth);
            float h = Mathf.Max(1, atlasHeight);
            return new Vector4(X / w, Y / h, (X + Width) / w, (Y + Height) / h);
        }
    }

    /// <summary>
    /// One mip level of one region's copy: the window to read in the source and where to write it in
    /// the atlas, both in <i>that level's</i> pixels. Produced by
    /// <see cref="CharacterQuadAtlas.ComputeMipCopy"/>, which is the pure arithmetic behind
    /// <c>Graphics.CopyTexture</c>.
    /// </summary>
    public readonly struct CharacterAtlasMipCopy
    {
        public readonly int Mip;
        public readonly int SourceX;
        public readonly int SourceY;
        public readonly int DestinationX;
        public readonly int DestinationY;
        public readonly int Width;
        public readonly int Height;

        public CharacterAtlasMipCopy(
            int mip, int sourceX, int sourceY, int destinationX, int destinationY, int width, int height)
        {
            Mip = mip;
            SourceX = sourceX;
            SourceY = sourceY;
            DestinationX = destinationX;
            DestinationY = destinationY;
            Width = width;
            Height = height;
        }

        /// <summary><c>false</c> when the level has collapsed to nothing and must not be copied.</summary>
        public bool IsValid => Width > 0 && Height > 0;

        public override string ToString()
        {
            return $"mip {Mip}: src ({SourceX},{SourceY}) {Width}x{Height} -> dst " +
                   $"({DestinationX},{DestinationY})";
        }
    }

    /// <summary>
    /// Shelf packer for the character's part textures. Pure arithmetic over sizes — no
    /// <see cref="Texture2D"/>, no <see cref="Sprite"/> — so the placement rules can be pinned by an
    /// offline fixture. <see cref="CharacterQuadAtlas"/> is the only thing that touches the GPU.
    ///
    /// <para><b>Why shelf and not MaxRects.</b> The input is 21–40 rectangles whose heights fall into a
    /// handful of bands (a head is ~173 px, a leg ~10 px), and the atlas is rebuilt once per character
    /// setup. Shelf packing is exact enough to fit them in 1024² with room to spare, and it is short
    /// enough to read. A better packer would buy a smaller atlas, not a visible improvement.</para>
    /// </summary>
    public sealed class CharacterAtlasPacker
    {
        readonly List<Vector2Int> _sizes = new();
        readonly List<CharacterAtlasRect> _rects = new();

        public int Count => _sizes.Count;

        public void Clear()
        {
            _sizes.Clear();
            _rects.Clear();
        }

        /// <summary>Records a rectangle to place. Returns its index, which <see cref="GetRect"/> uses.</summary>
        public int Add(int width, int height)
        {
            _sizes.Add(new Vector2Int(Mathf.Max(1, width), Mathf.Max(1, height)));
            _rects.Add(default);
            return _sizes.Count - 1;
        }

        public CharacterAtlasRect GetRect(int index)
        {
            return index >= 0 && index < _rects.Count ? _rects[index] : default;
        }

        /// <summary>
        /// Places every recorded rectangle inside <paramref name="maxWidth"/> ×
        /// <paramref name="maxHeight"/> with <paramref name="padding"/> pixels of gap between neighbours
        /// and around the border.
        ///
        /// <para><b>All or nothing.</b> A partial pack would leave some sprites pointing at an empty
        /// region of the atlas, which draws as transparent rather than as an error — the part would
        /// simply vanish. So a failed attempt writes nothing and the caller retries with a larger
        /// atlas.</para>
        /// </summary>
        public bool TryPack(int maxWidth, int maxHeight, int padding, out int usedWidth, out int usedHeight)
        {
            usedWidth = 0;
            usedHeight = 0;

            int count = _sizes.Count;
            if (count == 0)
            {
                return true;
            }

            if (maxWidth <= 0 || maxHeight <= 0)
            {
                return false;
            }

            int gap = Mathf.Max(0, padding);

            // Tallest first: a shelf is as tall as its tallest member, so putting the tall rects in
            // first stops a 173 px head from being pushed onto a shelf sized for 10 px legs.
            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) =>
            {
                int byHeight = _sizes[b].y.CompareTo(_sizes[a].y);
                return byHeight != 0 ? byHeight : _sizes[b].x.CompareTo(_sizes[a].x);
            });

            var placed = new CharacterAtlasRect[count];

            int cursorX = gap;
            int cursorY = gap;
            int shelfHeight = 0;

            for (int o = 0; o < count; o++)
            {
                int index = order[o];
                int width = _sizes[index].x;
                int height = _sizes[index].y;

                // A rect wider or taller than the whole atlas can never fit, on any shelf.
                if (width + gap * 2 > maxWidth || height + gap * 2 > maxHeight)
                {
                    return false;
                }

                if (cursorX + width + gap > maxWidth)
                {
                    cursorX = gap;
                    cursorY += shelfHeight + gap;
                    shelfHeight = 0;
                }

                if (cursorY + height + gap > maxHeight)
                {
                    return false;
                }

                placed[index] = new CharacterAtlasRect(cursorX, cursorY, width, height);
                cursorX += width + gap;
                shelfHeight = Mathf.Max(shelfHeight, height);
            }

            int right = 0;
            int top = 0;
            for (int i = 0; i < count; i++)
            {
                right = Mathf.Max(right, placed[i].X + placed[i].Width + gap);
                top = Mathf.Max(top, placed[i].Y + placed[i].Height + gap);
            }

            for (int i = 0; i < count; i++)
            {
                _rects[i] = placed[i];
            }

            usedWidth = right;
            usedHeight = top;
            return true;
        }
    }

    /// <summary>
    /// Where one packed region came from: the texture it was copied out of and the window inside it,
    /// both in level-0 pixels.
    ///
    /// <para><b>Why this is exposed.</b> A dump of the atlas that records only the destination rect
    /// cannot be checked: every failure mode of this class — a rect copied from the wrong source
    /// window, a region whose extent disagrees with its sprite, a clamped rect that silently lost a
    /// column — produces a destination rect that is perfectly well-formed. Pairing it with the source
    /// lets the dump be compared against the PNGs on disk, which is the only thing that can say the
    /// copy put the right pixels in the right place.</para>
    /// </summary>
    public readonly struct CharacterAtlasRegionSource
    {
        /// <summary>The source texture's asset name, i.e. the PNG the part was imported from.</summary>
        public readonly string TextureName;

        /// <summary>The window inside the source texture, level 0.</summary>
        public readonly CharacterAtlasRect Rect;

        public readonly int TextureWidth;
        public readonly int TextureHeight;

        public CharacterAtlasRegionSource(
            string textureName, CharacterAtlasRect rect, int textureWidth, int textureHeight)
        {
            TextureName = textureName;
            Rect = rect;
            TextureWidth = textureWidth;
            TextureHeight = textureHeight;
        }

        public bool IsValid => Rect.IsValid;
    }

    /// <summary>
    /// One texture and one material for every sprite a character can draw.
    ///
    /// <para><b>The problem this solves.</b> Each part is its own PNG, so each part is its own texture,
    /// so each part is its own draw call: 21–26 per character, and the shipped sprite path pays the
    /// same. With ten units on screen that is a couple of hundred draw calls to draw ten animals.
    /// <c>docs/RenderingPipeline_Research/08</c> §7 calls the atlas "the only thing that fixes the
    /// batch count"; this is it, built at runtime so it needs no owner-only <c>SpriteAtlas</c> asset.</para>
    ///
    /// <para><b>Why runtime and not a <c>SpriteAtlas</c>.</b> A <c>SpriteAtlas</c> asset repacks at
    /// import time and rewrites the sprite UVs project-wide, which would change the shipped sprite path
    /// as well as this one. Building the atlas in memory leaves both paths' assets untouched and lets
    /// the A/B window compare them on the same build. The cost is that the atlas is rebuilt per
    /// character; for 21 textures that is a handful of <c>Graphics.CopyTexture</c> calls.</para>
    ///
    /// <para><b>Why <c>CopyTexture</c> and not <c>GetPixels32</c>.</b> The imported PNGs are
    /// <c>isReadable: 0</c>, so their pixels cannot be read on the CPU at all. <c>Graphics.CopyTexture</c>
    /// stays on the GPU and needs no readable source — the same reason
    /// <see cref="CharacterQuadMaterialCache"/> uses it for the bilinear copies.</para>
    ///
    /// <para><b>The atlas carries the mip chain, not just mip 0.</b> Once the character art is imported
    /// with mipmaps (<c>CharacterTextureImportPolicy</c>), an atlas built from mip 0 alone is a
    /// <i>downgrade</i>: the body parts are drawn at ~4.8× minification, so sampling a mip-less atlas
    /// with a bilinear filter aliases badly — visibly worse than the per-texture path it replaces, and
    /// the reason the quad character regressed when the sampler policy landed. So the atlas is
    /// allocated with <see cref="TextureCreationFlags.MipChain"/> and every level is copied.</para>
    ///
    /// <para><b>The chain is as deep as the shallowest source.</b> A region cannot be copied to a level
    /// its own texture does not have, so the atlas depth is <c>min</c> over the packed sources — 5
    /// levels for this character, set by the 9×18 legs. That caps the deep end for the 160×173 head,
    /// which is harmless in practice: 4.8× minification wants level 2.3. Taking the <c>max</c> instead
    /// would leave the shallower regions' deep levels uninitialised, i.e. whatever the driver handed
    /// back — the kind of bug that looks like random noise in one part only.</para>
    ///
    /// <para><b>Point keeps the old texture exactly.</b> Mips are only built when the atlas is
    /// bilinear; a Point atlas is allocated with <see cref="TextureCreationFlags.None"/> and copies mip
    /// 0 only, which is byte-for-byte what this class did before the chain existed. That is what makes
    /// "the Point atlas is unchanged" a testable claim (<see cref="MipCount"/> == 1) rather than a
    /// hope.</para>
    /// </summary>
    public sealed class CharacterQuadAtlas : IDisposable
    {
        /// <summary>Square sizes tried in order. 1024² holds this character's 21 parts comfortably.</summary>
        static readonly int[] CandidateSizes = { 256, 512, 1024, 2048, 4096 };

        /// <summary>
        /// Gap between rects, in pixels. Two, not one: bilinear sampling reads half a texel either side
        /// of the UV, so a 1 px gap lets a neighbour's colour bleed into the outermost row of a part.
        /// At 2 px the bleed lands in the gap.
        /// </summary>
        public const int DefaultPadding = 2;

        readonly Dictionary<SpriteKey, int> _indexBySprite = new();
        readonly List<CharacterAtlasRect> _rects = new();
        readonly List<CharacterAtlasRegionSource> _sources = new();

        Texture2D _texture;
        Material _material;

        public Texture2D Texture => _texture;
        public Material Material => _material;
        public bool IsReady => _texture != null && _material != null;
        public int Width => _texture != null ? _texture.width : 0;
        public int Height => _texture != null ? _texture.height : 0;

        /// <summary>Distinct texture regions in the atlas — i.e. the draw calls this replaces.</summary>
        public int RegionCount => _rects.Count;

        /// <summary>
        /// Mip levels in the atlas texture: 1 for a Point atlas, <c>min</c> over the packed sources
        /// for a bilinear one. Read by the A/B readout, because "the atlas has no mip chain" and "the
        /// atlas has one" are indistinguishable on screen until something is minified.
        /// </summary>
        public int MipCount => _texture != null ? Mathf.Max(1, _texture.mipmapCount) : 0;

        /// <summary>Sprites the atlas had to leave out (unreadable format, or no room). They keep
        /// using the per-texture material, so a skipped part still draws.</summary>
        public int SkippedCount { get; private set; }

        public string Error { get; private set; }

        /// <summary>
        /// The worn armour set this atlas was built for, or empty. Recorded here rather than left to the
        /// caller so a readout can say which costume's sprites are actually in the texture — otherwise
        /// "the costume is missing from the atlas" and "the costume never reached the assembler" look
        /// identical from outside.
        /// </summary>
        public string ArmorId { get; private set; }

        /// <summary>
        /// The UV window for <paramref name="sprite"/>, or <c>false</c> when this atlas does not carry
        /// it. The caller then falls back to the per-texture material rather than drawing a wrong
        /// region — a missing part is a visible bug, a silently wrong UV is a mystery.
        /// </summary>
        public bool TryGetUvRect(Sprite sprite, out Vector4 uv)
        {
            uv = default;
            if (!IsReady || sprite == null)
            {
                return false;
            }

            if (!_indexBySprite.TryGetValue(SpriteKey.From(sprite), out int index))
            {
                return false;
            }

            CharacterAtlasRect rect = _rects[index];
            if (!rect.IsValid)
            {
                return false;
            }

            uv = rect.ToUv(_texture.width, _texture.height);
            return true;
        }

        /// <summary>
        /// Builds the atlas. Returns null when nothing could be packed — the caller then keeps the
        /// per-texture path, so this is a degradation and not a failure.
        /// </summary>
        /// <param name="sprites">Every sprite the character may draw, in any order, duplicates allowed.</param>
        /// <param name="bilinear">
        /// Filter for the atlas texture. The atlas is a texture this code owns, so unlike the imported
        /// PNGs its filter is free to be whatever the renderer wants — which is why the atlas also
        /// removes the need for the per-texture bilinear copies when it is active.
        /// </param>
        /// <param name="armorId">
        /// The worn set <paramref name="sprites"/> was collected for, recorded for diagnostics. It does
        /// not change what is packed: the caller decides that, and a mismatch between this and the
        /// caller's own context would be a bug in the caller, not something to paper over here.
        /// </param>
        public static CharacterQuadAtlas Build(
            IEnumerable<Sprite> sprites, bool bilinear, string armorId = null)
        {
            if (sprites == null)
            {
                return null;
            }

            // ── 1. Collect the distinct regions, in a deterministic order ──
            var regions = new List<Region>();
            var seen = new HashSet<SpriteKey>();
            int skipped = 0;

            foreach (Sprite sprite in sprites)
            {
                if (sprite == null || sprite.texture == null)
                {
                    continue;
                }

                Rect textureRect = sprite.textureRect;
                if (textureRect.width < 1f || textureRect.height < 1f)
                {
                    skipped++;
                    continue;
                }

                var key = SpriteKey.From(sprite);
                if (!seen.Add(key))
                {
                    continue;
                }

                // The source window must lie inside the texture it is read from. RoundRect rounds the
                // origin and the extent *independently*, so float noise in textureRect can push
                // x + width one pixel past the edge; Graphics.CopyTexture throws on that, the throw is
                // caught by this method, and the whole atlas is abandoned — the character silently
                // reverts to one material per part. Clamping is the cheap answer, and an empty
                // intersection is a genuine skip rather than a copy of nothing.
                CharacterAtlasRect source = ClampToTexture(
                    RoundRect(textureRect), sprite.texture.width, sprite.texture.height);

                if (!source.IsValid)
                {
                    skipped++;
                    continue;
                }

                regions.Add(new Region(key, sprite.texture, source));
            }

            if (regions.Count == 0)
            {
                return null;
            }

            // ── 2. Every source must share one format; CopyTexture cannot convert ──
            //
            // The importer writes uncompressed RGBA32 (textureCompression: 0), so in practice this
            // drops nothing. It is here because the alternative failure — a copy that silently does
            // nothing on a platform that compressed the art — looks like an empty atlas.
            GraphicsFormat format = regions[0].Texture.graphicsFormat;
            for (int i = regions.Count - 1; i >= 0; i--)
            {
                if (regions[i].Texture.graphicsFormat != format)
                {
                    skipped++;
                    regions.RemoveAt(i);
                }
            }

            if (regions.Count == 0)
            {
                return null;
            }

            // ── 3. Find the smallest square that fits ──
            var packer = new CharacterAtlasPacker();
            foreach (Region region in regions)
            {
                packer.Add(region.Rect.Width, region.Rect.Height);
            }

            int size = 0;
            int usedWidth = 0;
            int usedHeight = 0;
            foreach (int candidate in CandidateSizes)
            {
                if (packer.TryPack(candidate, candidate, DefaultPadding, out usedWidth, out usedHeight))
                {
                    size = candidate;
                    break;
                }
            }

            if (size == 0)
            {
                return null;
            }

            // ── 4. How deep a chain the atlas can honestly carry ──
            //
            // A region cannot be copied into a mip level its own texture does not have, so the depth
            // is the minimum over the packed sources. A Point atlas gets no chain at all, which keeps
            // the pre-mip behaviour byte-identical.
            int atlasMips = 1;
            if (bilinear)
            {
                atlasMips = int.MaxValue;
                foreach (Region region in regions)
                {
                    atlasMips = Mathf.Min(atlasMips, Mathf.Max(1, region.Texture.mipmapCount));
                }

                if (atlasMips == int.MaxValue)
                {
                    atlasMips = 1;
                }
            }

            // ── 5. Allocate, copy, and keep the lookup ──
            var atlas = new CharacterQuadAtlas
            {
                SkippedCount = skipped,
                ArmorId = armorId ?? string.Empty
            };

            try
            {
                TextureCreationFlags flags = atlasMips > 1
                    ? TextureCreationFlags.MipChain
                    : TextureCreationFlags.None;

                atlas._texture = new Texture2D(size, size, format, flags)
                {
                    name = atlasMips > 1
                        ? $"CharacterQuadAtlas_{size}_m{atlasMips}"
                        : $"CharacterQuadAtlas_{size}",
                    filterMode = bilinear ? FilterMode.Bilinear : FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    anisoLevel = 0,
                    hideFlags = HideFlags.HideAndDontSave
                };

                // ── Clear before copying, and this is not cosmetic ──
                //
                // A texture created with `new Texture2D` has *undefined* contents, and the packer
                // deliberately leaves 2 px between regions. Bilinear sampling reads half a texel past
                // every region edge, so those gaps are sampled by every part; the padding is therefore
                // a visible colour, not empty space. Undefined turned out to be **white** on this
                // driver, and a quad that ended up reading only the padding — a degenerate UV window,
                // or a stale slot — drew as a flat opaque rectangle tinted by the part colour.
                //
                // Measured: the atlas's own average with white gaps is (206,207,206), and multiplying
                // it by the observed part tints reproduces both block colours in the A/B screenshot
                // exactly. Clearing to transparent makes the gaps *provably* empty, so that failure
                // mode becomes invisible instead of loud-but-wrong.
                ClearToTransparent(atlas._texture, size, atlasMips > 1);

                for (int i = 0; i < regions.Count; i++)
                {
                    CharacterAtlasRect rect = packer.GetRect(i);
                    Region region = regions[i];

                    // Level 0 is the rect the packer placed; deeper levels are that rect halved,
                    // clamped to each texture's own level. Levels the region cannot fill are skipped
                    // rather than copied from a clamped neighbour, which would smear one part's edge
                    // across the gap at high mips.
                    for (int mip = 0; mip < atlasMips; mip++)
                    {
                        CharacterAtlasMipCopy copy = ComputeMipCopy(
                            region.Texture.width, region.Texture.height, size,
                            region.Rect, rect, mip);

                        if (!copy.IsValid)
                        {
                            break;
                        }

                        Graphics.CopyTexture(
                            region.Texture, 0, copy.Mip,
                            copy.SourceX, copy.SourceY, copy.Width, copy.Height,
                            atlas._texture, 0, copy.Mip, copy.DestinationX, copy.DestinationY);
                    }

                    atlas._rects.Add(rect);
                    atlas._sources.Add(new CharacterAtlasRegionSource(
                        region.Texture.name, region.Rect,
                        region.Texture.width, region.Texture.height));
                    atlas._indexBySprite[region.Key] = atlas._rects.Count - 1;
                }
            }
            catch (Exception exception)
            {
                // A format the platform cannot copy, a texture destroyed mid-build, or an atlas the
                // driver refuses to allocate. Degrade to the per-texture path and say so once.
                atlas.Error = exception.Message;
                Debug.LogWarning(
                    $"[CharacterQuad] Atlas build failed ({size}², {atlasMips} mip level(s), " +
                    $"{regions.Count} regions), keeping one material per texture: {exception.Message}");

                atlas.Dispose();
                return null;
            }

            // One material for every slot. Vertex colour still carries the tint, so the material needs
            // no per-part state and the renderers can batch.
            Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                         ?? Shader.Find("Sprites/Default");

            if (shader == null)
            {
                atlas.Error = "no sprite shader";
                atlas.Dispose();
                return null;
            }

            atlas._material = new Material(shader)
            {
                name = $"CharacterQuadAtlas_{size}",
                hideFlags = HideFlags.HideAndDontSave
            };
            atlas._material.mainTexture = atlas._texture;
            atlas._material.SetColor("_Color", Color.white);

            return atlas;
        }

        static CharacterAtlasRect RoundRect(Rect rect)
        {
            int x = Mathf.RoundToInt(rect.x);
            int y = Mathf.RoundToInt(rect.y);
            int width = Mathf.Max(1, Mathf.RoundToInt(rect.width));
            int height = Mathf.Max(1, Mathf.RoundToInt(rect.height));
            return new CharacterAtlasRect(x, y, width, height);
        }

        /// <summary>
        /// Intersects a region's rect with the texture it is read from. Returns an invalid rect when
        /// the intersection is empty, which the caller treats as a skip.
        ///
        /// <para><b>Why this is needed at all.</b> <see cref="RoundRect"/> rounds the origin and the
        /// extent independently, so a <c>textureRect</c> whose float edges sit a hair outside the
        /// texture produces <c>x + width = textureWidth + 1</c>. <c>Graphics.CopyTexture</c> throws on
        /// that, the throw is caught by <see cref="Build"/>, and the whole atlas is abandoned — so the
        /// symptom of one bad pixel is "the quad character went back to 92 materials", which reads as a
        /// performance change rather than a failure.</para>
        /// </summary>
        public static CharacterAtlasRect ClampToTexture(
            CharacterAtlasRect rect, int textureWidth, int textureHeight)
        {
            if (!rect.IsValid || textureWidth < 1 || textureHeight < 1)
            {
                return default;
            }

            int x0 = Mathf.Max(0, rect.X);
            int y0 = Mathf.Max(0, rect.Y);
            int x1 = Mathf.Min(textureWidth, rect.X + rect.Width);
            int y1 = Mathf.Min(textureHeight, rect.Y + rect.Height);

            if (x1 <= x0 || y1 <= y0)
            {
                return default;
            }

            return new CharacterAtlasRect(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// Fills the texture with transparent black so the packer's padding is defined.
        ///
        /// <para><b>Mips are regenerated, not copied.</b> <c>Apply(true)</c> rebuilds the chain from the
        /// cleared level 0, so every level starts transparent and each region's copy lands on top of
        /// empty rather than on top of whatever the driver handed back. The regions are copied
        /// afterwards, so this never overwrites art — it only fills what nothing writes.</para>
        /// </summary>
        static void ClearToTransparent(Texture2D texture, int size, bool withMips)
        {
            var clear = new Color32[size * size];
            texture.SetPixels32(clear);
            texture.Apply(withMips, false);
        }

        /// <summary>
        /// The rect a packed region occupies, by index, for diagnostics. Pairs with
        /// <see cref="GetRegionUv"/>; the index space is the packer's, so a dump can name the region
        /// that a wrong window came from.
        /// </summary>
        public CharacterAtlasRect GetRegionRect(int index)
        {
            return index >= 0 && index < _rects.Count ? _rects[index] : default;
        }

        /// <summary>The UV window of a packed region, by index.</summary>
        public Vector4 GetRegionUv(int index)
        {
            CharacterAtlasRect rect = GetRegionRect(index);
            return rect.IsValid && _texture != null ? rect.ToUv(Width, Height) : default;
        }

        /// <summary>
        /// The texture and window a packed region was copied from, by index. Pairs with
        /// <see cref="GetRegionRect"/> so a dump can be checked against the art instead of only
        /// against itself; see <see cref="CharacterAtlasRegionSource"/>.
        /// </summary>
        public CharacterAtlasRegionSource GetRegionSource(int index)
        {
            return index >= 0 && index < _sources.Count ? _sources[index] : default;
        }

        /// <summary>
        /// Where one mip level of a region is read from and written to.
        ///
        /// <para><b>Why this is pure and public.</b> <c>Graphics.CopyTexture</c> throws on a rectangle
        /// that leaves either texture, and the failure mode is not a warning but a dead character — the
        /// whole atlas build lands in the catch and the renderer falls back to per-texture materials.
        /// The halving-and-clamping that keeps it inside the bounds is arithmetic, so it is testable
        /// without a GPU; only the copy itself needs looking at.</para>
        ///
        /// <para><b>Two rects, because the packer moves regions.</b> <paramref name="sourceRect"/> is
        /// where the art lives in its own PNG; <paramref name="destinationRect"/> is where the packer put
        /// it in the atlas. They are the same size and unrelated origins, so taking the window from the
        /// destination rect would copy a neighbour's pixels — silently, since the copy would still be
        /// in bounds.</para>
        ///
        /// <para><b>Halved, then clamped, then shrunk to fit.</b> A rect at level <c>L</c> is
        /// <c>rect >> L</c> in both textures, but a region near the right edge of a small source can
        /// round past that source's own level extent, and a region near the edge of a 256² atlas runs
        /// out of room at level 7. So each origin is clamped into its own level, and the extent is the
        /// smallest of what the rect wants and what each side has left. Returns
        /// <see cref="CharacterAtlasMipCopy.IsValid"/> <c>false</c> when nothing is left, which is the
        /// signal to stop descending rather than copy a degenerate 0-wide strip.</para>
        /// </summary>
        /// <param name="sourceWidth">The source texture's level-0 width.</param>
        /// <param name="sourceHeight">The source texture's level-0 height.</param>
        /// <param name="atlasSize">The atlas's level-0 edge length (the atlas is square).</param>
        /// <param name="sourceRect">The region's level-0 rect inside its own texture.</param>
        /// <param name="destinationRect">The region's level-0 rect inside the atlas.</param>
        /// <param name="mip">The level to compute, zero-based.</param>
        public static CharacterAtlasMipCopy ComputeMipCopy(
            int sourceWidth, int sourceHeight, int atlasSize,
            CharacterAtlasRect sourceRect, CharacterAtlasRect destinationRect, int mip)
        {
            if (!sourceRect.IsValid || !destinationRect.IsValid || mip < 0
                || sourceWidth < 1 || sourceHeight < 1 || atlasSize < 1)
            {
                return default;
            }

            int sourceLevelWidth = Mathf.Max(1, sourceWidth >> mip);
            int sourceLevelHeight = Mathf.Max(1, sourceHeight >> mip);
            int atlasLevelSize = Mathf.Max(1, atlasSize >> mip);

            int sourceX = Mathf.Clamp(sourceRect.X >> mip, 0, sourceLevelWidth - 1);
            int sourceY = Mathf.Clamp(sourceRect.Y >> mip, 0, sourceLevelHeight - 1);
            int destinationX = Mathf.Clamp(destinationRect.X >> mip, 0, atlasLevelSize - 1);
            int destinationY = Mathf.Clamp(destinationRect.Y >> mip, 0, atlasLevelSize - 1);

            int width = Mathf.Min(
                Mathf.Max(1, sourceRect.Width >> mip),
                Mathf.Min(sourceLevelWidth - sourceX, atlasLevelSize - destinationX));

            int height = Mathf.Min(
                Mathf.Max(1, sourceRect.Height >> mip),
                Mathf.Min(sourceLevelHeight - sourceY, atlasLevelSize - destinationY));

            if (width < 1 || height < 1)
            {
                return default;
            }

            return new CharacterAtlasMipCopy(
                mip, sourceX, sourceY, destinationX, destinationY, width, height);
        }

        public void Dispose()
        {
            if (_material != null)
            {
                CharacterQuadMaterialCache.DestroyObject(_material);
                _material = null;
            }

            if (_texture != null)
            {
                CharacterQuadMaterialCache.DestroyObject(_texture);
                _texture = null;
            }

            _indexBySprite.Clear();
            _rects.Clear();
            _sources.Clear();
        }

        readonly struct Region
        {
            public readonly SpriteKey Key;
            public readonly Texture2D Texture;
            public readonly CharacterAtlasRect Rect;

            public Region(SpriteKey key, Texture2D texture, CharacterAtlasRect rect)
            {
                Key = key;
                Texture = texture;
                Rect = rect;
            }
        }

        /// <summary>
        /// Identity of a drawable region: which texture, and which rectangle inside it. Two sprites cut
        /// from one PNG (a Multiple-mode sheet) share a texture but not a rect, and both must land in
        /// the atlas separately.
        /// </summary>
        readonly struct SpriteKey : IEquatable<SpriteKey>
        {
            readonly int _textureId;
            readonly int _x;
            readonly int _y;
            readonly int _width;
            readonly int _height;

            SpriteKey(int textureId, int x, int y, int width, int height)
            {
                _textureId = textureId;
                _x = x;
                _y = y;
                _width = width;
                _height = height;
            }

            public static SpriteKey From(Sprite sprite)
            {
                Rect rect = sprite.textureRect;
                return new SpriteKey(
                    sprite.texture != null ? sprite.texture.GetInstanceID() : 0,
                    Mathf.RoundToInt(rect.x),
                    Mathf.RoundToInt(rect.y),
                    Mathf.RoundToInt(rect.width),
                    Mathf.RoundToInt(rect.height));
            }

            public bool Equals(SpriteKey other)
            {
                return _textureId == other._textureId
                    && _x == other._x
                    && _y == other._y
                    && _width == other._width
                    && _height == other._height;
            }

            public override bool Equals(object obj) => obj is SpriteKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _textureId;
                    hash = hash * 397 ^ _x;
                    hash = hash * 397 ^ _y;
                    hash = hash * 397 ^ _width;
                    hash = hash * 397 ^ _height;
                    return hash;
                }
            }
        }
    }
}
