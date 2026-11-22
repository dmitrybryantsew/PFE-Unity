using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace PFE.Character
{
    /// <summary>
    /// What a packed region's pixels actually turned out to be. See
    /// <see cref="CharacterQuadAtlas.MeasureRegionFill"/> for why <see cref="Empty"/> is not a
    /// failure and <see cref="Partial"/> is.
    /// </summary>
    public enum CharacterAtlasRegionFill
    {
        /// <summary>No ink anywhere in the rect. A blank frame and a lost copy are
        /// indistinguishable from inside the atlas, so this is reported and never warned about.</summary>
        Empty = 0,

        /// <summary>Ink reaches all four edges, within the allowed band. What a correct copy
        /// produces, because the rect is a frame's alpha-trim box.</summary>
        Filled = 1,

        /// <summary>Ink exists but stops short of an edge: the copy landed smaller than the rect.
        /// This is the corruption signature the mip chain produced.</summary>
        Partial = 2,
    }

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
        /// Mip levels the atlas actually <b>filled</b>: 1 for a Point atlas, otherwise the depth the
        /// build copied. Read by the A/B readout, because "the atlas has no mip chain" and "the atlas
        /// has one" are indistinguishable on screen until something is minified.
        ///
        /// <para><b>This used to read <c>_texture.mipmapCount</c>, and that was a number that could
        /// not fail.</b> A texture allocated with <see cref="TextureCreationFlags.MipChain"/> always
        /// reports the full chain for its size — 11 for a 1024² atlas — whatever the build managed to
        /// copy into it. So the readout printed 11 while the loop was stopping at 3 or 4, and the
        /// <c>CopyTexture</c> error in the log was the only evidence to the contrary. Reporting the
        /// copied depth is what makes "the chain is as deep as the shallowest source" checkable from
        /// the window.</para>
        /// </summary>
        public int MipCount => _texture != null ? Mathf.Max(1, _mipDepth) : 0;

        int _mipDepth = 1;

        /// <summary>
        /// Sprites the atlas had to leave out (unreadable format, or no room). <b>A skipped part
        /// does not draw correctly:</b> the consolidated mesh binds the one atlas material to every
        /// quad, so a skipped sprite's quad samples the atlas at the UV fractions of its own source
        /// texture — which is the flat-rectangle bug, not a graceful degradation. That was not
        /// always true: it held while each slot owned a renderer and a material, and the doc
        /// comment outlived that design. Kept counted (and named by the caller's own checks)
        /// because a nonzero count with a ready atlas is a bug to report.
        /// </summary>
        public int SkippedCount { get; private set; }

        /// <summary>
        /// How many packed regions came out only <b>partly</b> filled — read back off the finished
        /// atlas, not asserted. This is the count of regions whose copy landed smaller than the rect
        /// it was given, which draws a part too small inside a correctly sized quad.
        ///
        /// <para><b>Why the four edges are the test.</b> Every packed rect is a sprite's
        /// <c>textureRect</c>, which is the frame's <b>alpha-trim box</b>: by construction the ink
        /// touches all four of its edges. So if a region's top or right edge comes back empty while
        /// the region is not empty as a whole, the copy landed smaller than the rect. That is exactly
        /// the state the mip chain produced on this driver (regions holding half-size fragments
        /// anchored in a corner, one region holding 7 px), and nothing in the class noticed, because
        /// every number it reported was about intent rather than about the pixels.</para>
        ///
        /// <para><b>Why an empty region is not counted here.</b> The sources are
        /// <c>isReadable: 0</c>, so a blank region cannot be told apart from a blank frame —
        /// <c>lwing/f001</c> really is 86×101 of transparent and <c>helm/base</c> really is a 1×1
        /// placeholder. Those are counted by <see cref="RegionsEmpty"/> instead, which nothing warns
        /// about. See <see cref="CountRegionsMissingAnEdge"/> for the full reasoning and for the
        /// measurement that fixes the tolerance.</para>
        ///
        /// <para><b>Cost.</b> One <c>GetPixels32</c> per build, which is a one-off per character
        /// setup, not per frame.</para>
        /// </summary>
        public int RegionsMissingAnEdge { get; private set; }

        /// <summary>
        /// Regions holding no ink at all. Reported so the number is visible, never warned about: a
        /// legitimate blank frame and an empty region left by a failed copy are indistinguishable
        /// from inside the atlas. In the current art this is 3 of 330 — two 1×1 transparent
        /// placeholders and one fully transparent wing frame.
        /// </summary>
        public int RegionsEmpty { get; private set; }

        /// <summary>
        /// How the pixels got into the texture: <c>"direct"</c> when they were copied straight out of
        /// the imported part textures, <c>"staging"</c> when the direct route failed its own read-back
        /// and the build went through a render texture instead. Recorded rather than inferred, so the
        /// readout says which route produced the atlas it is describing.
        /// </summary>
        public string CopyRoute { get; private set; } = "direct";

        /// <summary>
        /// True when the read-back found <b>no ink anywhere</b> — every region blank at once. That is not
        /// a property a real atlas has (the quad side would draw nothing), so it means the read-back did
        /// not see the writes, and the region fill for this build is simply unverified.
        ///
        /// <para><b>Why this can happen.</b> On the staging route the pixels come from a
        /// <c>Graphics.Blit</c> into a render texture and a copy out of it; the copy may not have
        /// retired by the time <c>GetPixels32</c> reads back. It is reported instead of guessed at,
        /// because the alternative is a window line claiming 83 regions are blank frames while the
        /// character is visibly drawn from that atlas.</para>
        /// </summary>
        public bool ReadbackInconclusive { get; private set; }

        /// <summary>Names of the first few regions <see cref="RegionsMissingAnEdge"/> counted, so a
        /// warning can say which part rather than how many.</summary>
        public string RegionMismatchSample { get; private set; }

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
            IEnumerable<Sprite> sprites, bool bilinear, string armorId = null, bool mips = false)
        {
            if (sprites == null)
            {
                return null;
            }

            // ── 0. Build, then believe the pixels, not the request ──
            //
            // `Graphics.CopyTexture` with a region is asked for the full rect (verified: for the
            // dump's region 14 that is `(0,0,102,99) -> (2,318)`, and `ComputeMipCopy` returns
            // exactly that). What lands on this driver is the source's **mip-1 extent** — 51x49 —
            // placed at the **level-0** origin: half size, anchored bottom-left, for all 83 regions,
            // with the chain off and with it on. `docs/RenderingPipelineImprovement/03` §8.5 has the
            // measurements. A request that is right and a result that is wrong is not something to
            // argue with in a comment, so the build now checks and, if the copy lied, rebuilds
            // through a route that cannot be confused by the source's mip chain.
            var materialised = new List<Sprite>(sprites);
            CharacterQuadAtlas atlas = BuildCore(materialised, bilinear, armorId, mips, false);

            if (atlas != null && (atlas.RegionsMissingAnEdge > 0 || atlas.ReadbackInconclusive))
            {
                atlas.Dispose();
                atlas = BuildCore(materialised, bilinear, armorId, mips, true);

                if (atlas != null && (atlas.RegionsMissingAnEdge > 0 || atlas.ReadbackInconclusive))
                {
                    // Both routes produced a wrong or unverifiable atlas. Say so and hand back
                    // nothing. The caller's fallback is NOT a correct render: the consolidated
                    // single mesh binds one material for every part, so a multi-texture character
                    // (every imported character is one) draws wrong art for every part that does
                    // not live on the first visible part's texture — flat rectangles, not a slow
                    // character. That claim used to be true, when each slot had its own renderer
                    // and material; the one-mesh consolidation retired it. An abandoned atlas is
                    // therefore a rendering bug, said out loud.
                    string reason = atlas.ReadbackInconclusive
                        ? "read-back was inconclusive (GPU writes not visible to CPU)"
                        : $"{atlas.RegionsMissingAnEdge} of {atlas.RegionCount} regions are only partly filled";
                    Debug.LogError(
                        $"[CharacterQuad] Atlas abandoned: {reason} even after a staging copy " +
                        $"through CPU memory ({atlas.RegionMismatchSample}). The character will " +
                        "render with one shared material, which is WRONG ART for any multi-texture " +
                        "character — report this, do not ship it.");
                    atlas.Dispose();
                    return null;
                }
            }

            return atlas;
        }

        /// <summary>
        /// Process-wide reuse of built atlases, so a character spawn or armour swap with a
        /// <i>seen</i> costume pays a dictionary lookup instead of a region pack, a page of
        /// <c>CopyTexture</c> calls and the pixel read-back that verifies them.
        ///
        /// <para><b>Cache policy, stated explicitly.</b></para>
        /// <list type="number">
        /// <item><description><b>Key.</b> <c>(armorId, bilinear, mips, spriteSetHash)</c>. The hash is
        /// FNV-1a over every collected sprite's identity — texture instance id, texture name and
        /// rounded texture rect, in collection order — so two builds with the same key pack the same
        /// pixels by construction. <c>armorId</c> is part of the key even though the worn set's
        /// sprites already move the hash, because a diagnostic that can say which costume an atlas
        /// carries is worth the extra field.</description></item>
        /// <item><description><b>Bounded, LRU.</b> At most <see cref="MaxEntries"/> atlases, kept as a
        /// use-clock LRU. An evicted atlas is disposed on the spot — safe only because the cache owns
        /// play-mode atlases outright (see below). Not an unbounded dictionary: each entry is a
        /// 1024² texture plus a mip chain, and an unbounded cache of those is a leak with a hit
        /// rate.</description></item>
        /// <item><description><b>Play mode only.</b> In the editor (the A/B preview windows) this
        /// returns <c>null</c> and the caller builds a fresh, caller-owned atlas exactly as before —
        /// the preview's rebuild-on-demand semantics (toggling Bilinear, AtlasEnabled or the armour
        /// set re-runs the pack) stay honest, and the editor session does not accumulate megabytes of
        /// <c>HideAndDontSave</c> textures.</description></item>
        /// <item><description><b>Ownership.</b> An atlas this cache hands out in play mode belongs to
        /// the cache, never to the assembler: dropping a costume must not tear a texture another
        /// character is still drawing, so the assembler's teardown paths deliberately do
        /// <i>not</i> dispose a cached atlas (see <c>CharacterQuadAssembler.ReleaseAtlas</c>).</description></item>
        /// <item><description><b>Single-threaded.</b> Main thread only, like every Unity API this
        /// class already calls. No locking.</description></item>
        /// <item><description><b>Invalidation.</b> Play mode performs no content reloads, so no
        /// reload hook is needed; the entries that matter — the atlas texture and material — are
        /// destroyed by play-mode teardown, and the dead references are pruned on the next access
        /// (<c>IsReady</c> is false for a destroyed texture). Failed builds return <c>null</c> and
        /// are never cached, so a transient GPU failure is retried, not fossilised. Content edited
        /// between play sessions changes the imported sprites (and, after a domain reload, their
        /// instance ids), so the next session's keys cannot collide with a stale entry even in
        /// principle.</description></item>
        /// </list>
        /// </summary>
        public static class Cache
        {
            /// <summary>How many atlases stay resident at once. Eight costumes is far beyond what a
            /// session visits; the bound exists so the cache is never the leak the per-assembler
            /// material cache warned about.</summary>
            public const int MaxEntries = 8;

            sealed class Entry
            {
                public CharacterQuadAtlas Atlas;
                public uint LastUse;
            }

            /// <summary>
            /// The key: the build flags, the armour id, and the sprite-set hash. The hash is split
            /// into two lanes because FNV-1a at 64 bits over a few hundred ints is cheap, but a
            /// single 64-bit lane still leaves "same art, same order" indistinguishable from a
            /// collision for the lifetime of the process — two lanes of 64 bits make the accidental
            /// collision a non-subject.
            /// </summary>
            readonly struct Key : IEquatable<Key>
            {
                readonly string _armorId;
                readonly bool _bilinear;
                readonly bool _mips;
                readonly ulong _hashA;
                readonly ulong _hashB;

                Key(string armorId, bool bilinear, bool mips, ulong hashA, ulong hashB)
                {
                    _armorId = armorId ?? string.Empty;
                    _bilinear = bilinear;
                    _mips = mips;
                    _hashA = hashA;
                    _hashB = hashB;
                }

                /// <summary>
                /// Builds the key for one collected sprite set. Order-sensitive on purpose:
                /// <c>CollectSprites</c> iterates fixed asset arrays, so one costume always collects
                /// in the same order, and a different order would pack different regions.
                /// </summary>
                public static Key Compute(
                    IReadOnlyList<Sprite> sprites, bool bilinear, string armorId, bool mips)
                {
                    // FNV-1a 64-bit, two interleaved lanes: lane A takes texture identity, lane B
                    // the rectangle. Mixing both into one lane would also work; two lanes keep a
                    // swapped-width/height pair of rects from cancelling out.
                    const ulong offset = 14695981039346656037UL;
                    const ulong prime = 1099511628211UL;

                    ulong Mix(ulong hash, ulong value) => (hash ^ value) * prime;

                    ulong a = offset;
                    ulong b = offset;

                    int count = sprites?.Count ?? 0;
                    for (int i = 0; i < count; i++)
                    {
                        Sprite sprite = sprites[i];
                        if (sprite == null)
                        {
                            a = Mix(a, 0xFFFFFFFFUL);
                            b = Mix(b, 0xFFFFFFFFUL);
                            continue;
                        }

                        Texture texture = sprite.texture;
                        int textureId = texture != null ? texture.GetInstanceID() : 0;
                        string textureName = texture != null ? texture.name : string.Empty;
                        int nameHash = textureName != null ? textureName.GetHashCode() : 0;

                        Rect rect = sprite.textureRect;
                        a = Mix(a, (ulong)(uint)textureId);
                        a = Mix(a, (ulong)(uint)nameHash);
                        b = Mix(b, (ulong)(uint)Mathf.RoundToInt(rect.x));
                        b = Mix(b, (ulong)(uint)Mathf.RoundToInt(rect.y));
                        b = Mix(b, (ulong)(uint)Mathf.RoundToInt(rect.width));
                        b = Mix(b, (ulong)(uint)Mathf.RoundToInt(rect.height));
                    }

                    return new Key(armorId, bilinear, mips, a, b);
                }

                public bool Equals(Key other)
                {
                    return _hashA == other._hashA
                        && _hashB == other._hashB
                        && _bilinear == other._bilinear
                        && _mips == other._mips
                        && string.Equals(_armorId, other._armorId, StringComparison.Ordinal);
                }

                public override bool Equals(object obj) => obj is Key other && Equals(other);

                public override int GetHashCode()
                {
                    unchecked
                    {
                        int hash = _armorId != null ? _armorId.GetHashCode() : 0;
                        hash = hash * 397 ^ _bilinear.GetHashCode();
                        hash = hash * 397 ^ _mips.GetHashCode();
                        hash = hash * 397 ^ _hashA.GetHashCode();
                        hash = hash * 397 ^ _hashB.GetHashCode();
                        return hash;
                    }
                }
            }

            static readonly Dictionary<Key, Entry> _entries = new();

            static uint _clock;

            /// <summary>
            /// The atlas for this exact build, or <c>null</c> when this host does not cache (the
            /// editor) or the build failed. Never disposes a previously handed-out atlas.
            /// </summary>
            public static CharacterQuadAtlas Get(
                IReadOnlyList<Sprite> sprites, bool bilinear, string armorId, bool mips)
            {
                if (!Application.isPlaying)
                {
                    return null;
                }

                PruneDead();

                var key = Key.Compute(sprites, bilinear, armorId, mips);
                if (_entries.TryGetValue(key, out Entry existing))
                {
                    if (existing.Atlas != null && existing.Atlas.IsReady)
                    {
                        existing.LastUse = ++_clock;
                        return existing.Atlas;
                    }

                    _entries.Remove(key);
                }

                CharacterQuadAtlas built = Build(sprites, bilinear, armorId, mips);
                if (built == null)
                {
                    // A failed build is not cached: the failure may be transient, and a fossilised
                    // null would turn "the atlas missed once" into "this costume never atlases".
                    return null;
                }

                _entries[key] = new Entry { Atlas = built, LastUse = ++_clock };
                EvictOverflow();
                return built;
            }

            /// <summary>How many atlases are currently resident. Diagnostics and fixtures.</summary>
            public static int Count => _entries.Count;

            /// <summary>Drops every entry and disposes its atlas. Play-mode teardown only — tests and
            /// future session boundaries; the dead-entry prune on access is the normal path.</summary>
            public static void Clear()
            {
                foreach (Entry entry in _entries.Values)
                {
                    entry.Atlas?.Dispose();
                }
                _entries.Clear();
            }

            static void PruneDead()
            {
                // Play-mode exit destroys every runtime-created texture and material, leaving the
                // dictionary pointing at dead objects. Removing them here keeps the cache honest
                // across sessions without any editor-only lifecycle hook (which a runtime assembly
                // cannot use anyway).
                if (_entries.Count == 0) return;

                var dead = new List<Key>();
                foreach (KeyValuePair<Key, Entry> pair in _entries)
                {
                    if (pair.Value.Atlas == null || !pair.Value.Atlas.IsReady)
                    {
                        dead.Add(pair.Key);
                    }
                }

                for (int i = 0; i < dead.Count; i++)
                {
                    _entries.Remove(dead[i]);
                }
            }

            static void EvictOverflow()
            {
                while (_entries.Count > MaxEntries)
                {
                    Key oldest = default;
                    uint oldestUse = uint.MaxValue;
                    foreach (KeyValuePair<Key, Entry> pair in _entries)
                    {
                        if (pair.Value.LastUse < oldestUse)
                        {
                            oldestUse = pair.Value.LastUse;
                            oldest = pair.Key;
                        }
                    }

                    _entries[oldest].Atlas.Dispose();
                    _entries.Remove(oldest);
                }
            }
        }

        /// <summary>
        /// One attempt at the atlas. <paramref name="viaRenderTexture"/> routes every region through a
        /// reusable <see cref="RenderTexture"/> instead of copying straight out of the imported
        /// texture — see <see cref="Build"/> for why that is a separate attempt rather than the only
        /// path.
        /// </summary>
        static CharacterQuadAtlas BuildCore(            List<Sprite> sprites, bool bilinear, string armorId, bool mips, bool viaRenderTexture)
        {
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

            // ── 4. How deep a chain the atlas carries ──
            //
            // **Level 0 only unless the caller asks for a chain, and the default is off.**
            //
            // The chain is now produced by Unity, from the finished level 0, in one `Apply(true)` —
            // not by copying each region into each level. That per-region copy is what produced every
            // `Graphics.CopyTexture called with invalid source mip level` line in the log: it needed
            // the source texture's level count, and that count is not obtainable here.
            // `Texture.mipmapCount` over-reports (the log says `got 4, have 4 mips`, so level 4 does
            // not exist on a texture that reports 4), and the dimensions over-report further still
            // (`ComputeMipDepth(86,101)` is 7 for a texture the importer gave 4 levels). With no
            // trustworthy depth, the loop asked for levels that were not there, `CopyTexture` logged
            // and returned, and the regions that needed those levels stayed half-written.
            //
            // Generating the chain from level 0 needs none of that: the atlas's own depth is exactly
            // `ComputeMipDepth(size, size)`, which this code owns. The cost is that the filter sees
            // across region boundaries at high levels — the packer's 2 px transparent gaps are what
            // keep that to a soft edge rather than a neighbour's colour.
            bool wantChain = bilinear && mips;
            int atlasMips = wantChain ? ComputeMipDepth(size, size) : 1;

            // ── 5. Allocate, copy, and keep the lookup ──
            var atlas = new CharacterQuadAtlas
            {
                SkippedCount = skipped,
                ArmorId = armorId ?? string.Empty,
                _mipDepth = atlasMips,
                CopyRoute = viaRenderTexture ? "staging" : "direct"
            };

            // Only the render-texture route needs these; the direct route leaves them empty and the
            // finally below is a no-op. The staging cache holds one single-level render texture per
            // distinct source texture; the scratch cache holds one CPU-readable texture per distinct
            // region size, the landing pad for ReadPixels.
            var stagingCache = new Dictionary<long, RenderTexture>();
            var readbackScratch = new Dictionary<long, Texture2D>();

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

                    // Level 0 only. The chain, if one was asked for, is generated from the finished
                    // level 0 after this loop — see step 4 for why copying per level could not work.
                    CharacterAtlasMipCopy copy = ComputeMipCopy(
                        region.Texture.width, region.Texture.height, size,
                        region.Rect, rect, 0);

                    if (!copy.IsValid)
                    {
                        atlas._rects.Add(rect);
                        atlas._sources.Add(new CharacterAtlasRegionSource(
                            region.Texture.name, region.Rect,
                            region.Texture.width, region.Texture.height));
                        atlas._indexBySprite[region.Key] = atlas._rects.Count - 1;
                        continue;
                    }

                    if (viaRenderTexture)
                    {
                        // The staging route copies through CPU memory (blit-made render texture ->
                        // ReadPixels -> SetPixels32) instead of CopyTexture straight out of the
                        // staging texture. The reason is the lesson of the armoured-character
                        // report: on this driver the CopyTexture(staging -> atlas) writes were
                        // invisible to the GetPixels32 verification that runs after this loop —
                        // the build read back zero ink in every region, declared itself
                        // inconclusive, and was abandoned, and the character fell into the broken
                        // one-material fallback. ReadPixels performs a real GPU->CPU sync, so the
                        // pixels verification reads are the pixels this method wrote, by
                        // construction. The route exists only for builds whose direct copy failed
                        // verification, so the extra copies cost nothing in the common case.
                        RenderTexture staging = GetStagingCopy(region.Texture, stagingCache);
                        CopyRegionThroughCpu(staging, copy, atlas._texture, readbackScratch);
                    }
                    else
                    {
                        Graphics.CopyTexture(
                            region.Texture, 0, 0,
                            copy.SourceX, copy.SourceY, copy.Width, copy.Height,
                            atlas._texture, 0, 0, copy.DestinationX, copy.DestinationY);
                    }

                    atlas._rects.Add(rect);
                    atlas._sources.Add(new CharacterAtlasRegionSource(
                        region.Texture.name, region.Rect,
                        region.Texture.width, region.Texture.height));
                    atlas._indexBySprite[region.Key] = atlas._rects.Count - 1;
                }

                // The staging route wrote level 0 through SetPixels32; those uploads batch into one
                // Apply here. (The direct route needs none — CopyTexture lands on the GPU directly.)
                if (viaRenderTexture)
                {
                    atlas._texture.Apply(false, false);
                }

                // The chain, from the finished level 0. `Apply(true)` is the only mip generation this
                // build needs, and it cannot ask for a level that does not exist.
                if (wantChain)
                {
                    atlas._texture.Apply(true, false);
                }

                // Read the result back. Everything above this line is arithmetic on rectangles, and
                // arithmetic that is right can still be copied wrong — which is what the copy path did.
                var badNames = new List<string>();
                atlas.RegionsMissingAnEdge = CountRegionsMissingAnEdge(
                    atlas._texture, atlas._rects, badNames, out int emptyRegions);
                atlas.RegionsEmpty = emptyRegions;

                // An atlas in which *nothing* is inked is not a verdict about the copies: the quad side
                // would be drawing an empty texture and the character would be invisible. It means the
                // read-back did not see the writes — which happens on the staging route, where the
                // pixels are produced by a blit and the copy out of the render texture may not have
                // retired by the time `GetPixels32` runs. Say that, rather than reporting 83 blank
                // frames, because "83 regions hold no ink" is a claim the picture itself contradicts.
                if (atlas._rects.Count > 0 && atlas.RegionsEmpty == atlas._rects.Count)
                {
                    atlas.RegionsEmpty = 0;
                    atlas.ReadbackInconclusive = true;
                    Debug.LogWarning(
                        $"[CharacterQuad] Atlas {size}² read back with no ink in any of " +
                        $"{atlas._rects.Count} regions ({atlas.CopyRoute} copy). The atlas itself is " +
                        "drawing, so this is the read-back not seeing the writes, not an empty " +
                        "atlas. Region fill is unverified for this build.");
                }

                if (atlas.RegionsMissingAnEdge > 0)
                {
                    atlas.RegionMismatchSample = string.Join(" | ", badNames);
                    Debug.LogWarning(
                        $"[CharacterQuad] Atlas {size}² ({atlasMips} mip level(s), " +
                        $"{(viaRenderTexture ? "staging" : "direct")} copy): " +
                        $"{atlas.RegionsMissingAnEdge} of {atlas._rects.Count} regions are only " +
                        $"partly filled — the art landed smaller than the rect the copy was given, " +
                        $"so those parts draw too small. {atlas.RegionMismatchSample}. " +
                        "Compare Temp/quad_atlas_dump/quad_atlas_*.png against the .tsv.");
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
            finally
            {
                // Staging copies are scratch: the atlas holds its own pixels once the copy has run.
                foreach (RenderTexture staging in stagingCache.Values)
                {
                    if (staging == null)
                    {
                        continue;
                    }

                    staging.Release();
                    CharacterQuadMaterialCache.DestroyObject(staging);
                }

                stagingCache.Clear();

                // Scratch textures are scratch too: the atlas holds the pixels once SetPixels32 has
                // run, so the landing pads follow the staging textures into the bin.
                foreach (Texture2D scratch in readbackScratch.Values)
                {
                    if (scratch == null)
                    {
                        continue;
                    }

                    CharacterQuadMaterialCache.DestroyObject(scratch);
                }

                readbackScratch.Clear();
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
        /// A plain, single-level copy of one imported part texture, made once per distinct source
        /// <b>texture</b> and reused for the (usually few) regions cut from it.
        ///
        /// <para><b>Why this exists.</b> Copying a region straight out of an imported texture on this
        /// driver lands the source's <b>mip-1 extent at the level-0 origin</b> — half size, anchored
        /// bottom-left, for every region — even though the request is the full rect. The imported part
        /// PNGs are all <c>enableMipMap: 1</c>, so the source always has a chain; a render texture
        /// created here has exactly one level, so there is no chain to be confused by.</para>
        ///
        /// <para><b>Why a blit and not <c>CopyTexture</c>.</b> A blit is a raster operation: it samples
        /// level 0 by definition, so it cannot pick a level. That is the property being bought here,
        /// and it is worth one full-screen copy per distinct source texture (at most one per packed
        /// region) on a build that already does dozens of copies.</para>
        ///
        /// <para><b>Format.</b> The staging texture takes the source's own <c>graphicsFormat</c>, so no
        /// conversion happens and the blit is a straight copy in the same colour space. If the platform
        /// cannot allocate that format the build throws and <see cref="Build"/> falls back to one
        /// material per part texture, which is the correct outcome for a texture this code cannot
        /// copy.</para>
        /// </summary>
        static RenderTexture GetStagingCopy(
            Texture2D source, Dictionary<long, RenderTexture> cache)
        {
            // Keyed by the source TEXTURE, not its size — the armored-character report caught this
            // caching by (width, height): the part PNGs of one armour frame sequence all share one
            // canvas, so same-size collisions are the norm, and a cache hit returned the previous
            // texture's blit for every region after the first of that size. The content then came
            // from the wrong texture, which the edge test reads as partly-filled regions. One entry
            // per distinct source texture, of which there are at most one per packed region.
            long key = source.GetInstanceID();
            if (cache.TryGetValue(key, out RenderTexture cached) && cached != null)
            {
                return cached;
            }

            var descriptor = new RenderTextureDescriptor(
                source.width, source.height, source.graphicsFormat, 0)
            {
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };

            var staging = new RenderTexture(descriptor)
            {
                name = $"CharacterQuadAtlasStage_{source.width}x{source.height}",
                hideFlags = HideFlags.HideAndDontSave,
            };
            staging.Create();

            // `Blit` sets the destination as the active render target and leaves it there. Restoring
            // it matters here because this texture is read back region-by-region while it is the
            // ACTIVE render texture (`ReadPixels` requires that), and an active RT that is still
            // bound when the next blit or copy comes along produces silently empty reads — the
            // failure the restore existed to prevent.
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(source, staging);
            RenderTexture.active = previous;

            cache[key] = staging;
            return staging;
        }

        /// <summary>
        /// Copies one region from a staging render texture into the atlas through CPU memory:
        /// activate the staging texture, <c>ReadPixels</c> the region into a scratch texture,
        /// <c>SetPixels32</c> it into the atlas at the destination rect.
        ///
        /// <para><b>Why through the CPU at all.</b> The previous staging route ended in a
        /// <c>CopyTexture</c> from the render texture into the atlas, and on this driver the
        /// result could not be verified: the <c>GetPixels32</c> read-back after the loop saw no
        /// ink in any region, the build declared itself inconclusive, and the whole atlas was
        /// abandoned — which is how an equipped armour set rendered as flat rectangles while the
        /// same costume in a preview rendered correctly. <c>ReadPixels</c> performs an actual
        /// GPU-to-CPU synchronisation, so once this method has run, the atlas's CPU-visible pixels
        /// are exactly the pixels that were asked for, and the verification pass reads the truth.</para>
        ///
        /// <para><b>Scratch cache.</b> One readable texture per distinct region size, reused across
        /// regions and builds for the life of one <see cref="BuildCore"/> call — the staging route
        /// only runs for builds whose direct copy already failed, so this allocation is paid on a
        /// path that is already rare, and never per frame.</para>
        /// </summary>
        static void CopyRegionThroughCpu(
            RenderTexture staging,
            in CharacterAtlasMipCopy copy,
            Texture2D destination,
            Dictionary<long, Texture2D> scratchCache)
        {
            long key = ((long)copy.Width << 32) ^ (uint)copy.Height;
            if (!scratchCache.TryGetValue(key, out Texture2D scratch) || scratch == null)
            {
                // Same format as the destination so neither ReadPixels nor SetPixels32 performs a
                // colour-space or layout conversion.
                scratch = new Texture2D(copy.Width, copy.Height, destination.graphicsFormat,
                    TextureCreationFlags.None)
                {
                    name = $"CharacterQuadAtlasScratch_{copy.Width}x{copy.Height}",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                scratchCache[key] = scratch;
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = staging;
            try
            {
                // ReadPixels reads the active render texture in its own pixels, origin bottom-left —
                // the same space ComputeMipCopy works in at level 0, so the window is verbatim.
                scratch.ReadPixels(
                    new Rect(copy.SourceX, copy.SourceY, copy.Width, copy.Height), 0, 0, false);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            destination.SetPixels32(
                copy.DestinationX, copy.DestinationY, copy.Width, copy.Height, scratch.GetPixels32());
        }

        /// <summary>
        /// Counts regions whose pixels did not land where the rect says they should, and fills
        /// <paramref name="badNames"/> with the first few so a warning can name a part.
        ///
        /// <para><b>What it can and cannot tell apart, and why the line is drawn there.</b> The
        /// imported PNGs are <c>isReadable: 0</c> (see the note on <c>Graphics.CopyTexture</c> above),
        /// so this can only look at the finished atlas — never at the source. That makes one case
        /// genuinely ambiguous: a region with <b>no ink at all</b> is either a blank frame
        /// (<c>lwing/f001</c> is 86×101 of pure transparent; <c>helm/base</c> is a 1×1 transparent
        /// placeholder) or an empty region left by a bad copy, and nothing inside the atlas separates
        /// them. A <b>partial</b> fill is not ambiguous: ink exists but does not reach an edge, which
        /// no correct copy produces. So partial fills are the warning and empty regions are only
        /// counted.</para>
        ///
        /// <para><b>Measured, not assumed.</b> Across all 330 part sprites in
        /// <c>Assets/_PFE/Art/Character</c>: 327 fill their rect, 3 have no ink, <b>0 are partial</b>.
        /// An exact-edge test would flag 276 of 330 — every rect is a pixel or two wider than its
        /// alpha-trim ink — so <see cref="EdgeBandPixels"/> exists, and its value is the smallest that
        /// makes honest art quiet rather than slack chosen by feel.</para>
        /// </summary>
        /// <param name="texture">The finished atlas. Must be CPU-readable, which a <c>new Texture2D</c> is.</param>
        /// <param name="rects">Every packed rect, in the atlas's own bottom-up pixel space.</param>
        /// <param name="badNames">Receives up to <see cref="MismatchNameLimit"/> names.</param>
        /// <param name="emptyCount">Regions with no ink at all. Reported, never warned about.</param>
        static int CountRegionsMissingAnEdge(
            Texture2D texture, List<CharacterAtlasRect> rects, List<string> badNames, out int emptyCount)
        {
            emptyCount = 0;
            if (texture == null || rects == null || !texture.isReadable)
            {
                return 0;
            }

            int width = texture.width;
            int height = texture.height;
            Color32[] pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length < width * height)
            {
                return 0;
            }

            int partial = 0;
            for (int i = 0; i < rects.Count; i++)
            {
                CharacterAtlasRect rect = rects[i];
                if (!rect.IsValid)
                {
                    continue;
                }

                int x0 = Mathf.Max(0, rect.X);
                int y0 = Mathf.Max(0, rect.Y);
                int x1 = Mathf.Min(width, rect.X + rect.Width);
                int y1 = Mathf.Min(height, rect.Y + rect.Height);
                if (x1 <= x0 || y1 <= y0)
                {
                    continue;
                }

                CharacterAtlasRegionFill fill =
                    MeasureRegionFill(pixels, width, height, rect, out string missingEdges);

                if (fill == CharacterAtlasRegionFill.Empty)
                {
                    emptyCount++;
                    continue;
                }

                if (fill == CharacterAtlasRegionFill.Filled)
                {
                    continue;
                }

                partial++;
                if (badNames != null && badNames.Count < MismatchNameLimit)
                {
                    badNames.Add($"#{i} {rect.Width}x{rect.Height} at ({rect.X},{rect.Y}) " +
                                 $"missing {missingEdges}");
                }
            }

            return partial;
        }

        /// <summary>
        /// Classifies one packed region by the pixels actually in it. Pure, and public, for the same
        /// reason <see cref="ComputeMipCopy"/> is: the failure it detects is a GPU copy, which no
        /// offline fixture can perform, so the part that can be wrong in a way a test can catch is
        /// kept out of the part that needs a device.
        ///
        /// <para><b>Why this is not "is the region blank".</b> Blank art exists and is legitimate —
        /// <c>lwing/f001</c> is 86×101 of pure transparent, <c>helm/base</c> is a 1×1 placeholder — and
        /// the sources are <c>isReadable: 0</c>, so the atlas cannot tell those from a region a bad
        /// copy left empty. Ink that exists but stops short of an edge cannot be either: no correct
        /// copy produces it, because every packed rect is a frame's alpha-trim box. So
        /// <see cref="CharacterAtlasRegionFill.Partial"/> is the only verdict worth warning about.</para>
        ///
        /// <para><b>The band.</b> A rect is routinely a pixel or two wider than its alpha-trim ink, so
        /// an exact-edge test flags 276 of the project's 330 parts. With
        /// <see cref="EdgeBandPixels"/> lines of slack, and with ink defined as any alpha above zero,
        /// every one of those 330 comes back
        /// <see cref="CharacterAtlasRegionFill.Filled"/> or <see cref="CharacterAtlasRegionFill.Empty"/>
        /// — none partial. That is the measurement the constant below rests on.</para>
        /// </summary>
        /// <param name="pixels">The atlas's pixels, bottom-up: index <c>y * atlasWidth + x</c>, the
        /// same space the rect lives in.</param>
        /// <param name="missingEdges">Which edges the ink failed to reach, e.g. <c>"top+right"</c>, or
        /// empty when the region is filled or has no ink at all.</param>
        public static CharacterAtlasRegionFill MeasureRegionFill(
            Color32[] pixels, int atlasWidth, int atlasHeight,
            CharacterAtlasRect rect, out string missingEdges)
        {
            missingEdges = string.Empty;
            if (pixels == null || atlasWidth < 1 || atlasHeight < 1 || !rect.IsValid)
            {
                return CharacterAtlasRegionFill.Empty;
            }

            int x0 = Mathf.Max(0, rect.X);
            int y0 = Mathf.Max(0, rect.Y);
            int x1 = Mathf.Min(atlasWidth, rect.X + rect.Width);
            int y1 = Mathf.Min(atlasHeight, rect.Y + rect.Height);
            if (x1 <= x0 || y1 <= y0 || pixels.Length < atlasWidth * atlasHeight)
            {
                return CharacterAtlasRegionFill.Empty;
            }

            bool anyInk = false;
            for (int y = y0; y < y1 && !anyInk; y++)
            {
                int row = y * atlasWidth;
                for (int x = x0; x < x1; x++)
                {
                    if (pixels[row + x].a > InkAlphaFloor)
                    {
                        anyInk = true;
                        break;
                    }
                }
            }

            if (!anyInk)
            {
                return CharacterAtlasRegionFill.Empty;
            }

            bool top = false, bottom = false, left = false, right = false;
            int bandLimit = Mathf.Min(EdgeBandPixels, Mathf.Min(x1 - x0, y1 - y0));
            for (int band = 0; band < bandLimit; band++)
            {
                int topRow = y1 - 1 - band;
                int bottomRow = y0 + band;
                int leftColumn = x0 + band;
                int rightColumn = x1 - 1 - band;

                if (!top)
                {
                    for (int x = x0; x < x1 && !top; x++)
                    {
                        top = pixels[topRow * atlasWidth + x].a > InkAlphaFloor;
                    }
                }

                if (!bottom)
                {
                    for (int x = x0; x < x1 && !bottom; x++)
                    {
                        bottom = pixels[bottomRow * atlasWidth + x].a > InkAlphaFloor;
                    }
                }

                if (!left)
                {
                    for (int y = y0; y < y1 && !left; y++)
                    {
                        left = pixels[y * atlasWidth + leftColumn].a > InkAlphaFloor;
                    }
                }

                if (!right)
                {
                    for (int y = y0; y < y1 && !right; y++)
                    {
                        right = pixels[y * atlasWidth + rightColumn].a > InkAlphaFloor;
                    }
                }

                if (top && bottom && left && right)
                {
                    return CharacterAtlasRegionFill.Filled;
                }
            }

            var missing = new List<string>(4);
            if (!top) missing.Add("top");
            if (!bottom) missing.Add("bottom");
            if (!left) missing.Add("left");
            if (!right) missing.Add("right");
            missingEdges = string.Join("+", missing);
            return CharacterAtlasRegionFill.Partial;
        }

        /// <summary>Ink means any alpha at all — the same definition the alpha-trim box uses, so the
        /// measurement and the invariant are about the same pixels. Measured: raising this to 8
        /// (a third of the range) starts flagging honest art, because plenty of parts carry a soft
        /// fringe of alpha 1–8 exactly where the trim box stops.</summary>
        const byte InkAlphaFloor = 0;

        /// <summary>
        /// How many pixel <i>lines</i> are examined at each edge — so a gap of
        /// <c>EdgeBandPixels - 1</c> px is tolerated. Three lines means a rect may be up to 2 px wider
        /// than its ink and still count as filled.
        ///
        /// <para>Measured over the project's 330 part sprites with <see cref="InkAlphaFloor"/> at 0:
        /// two lines flags 3, three lines flags 3, four lines flags 3 — and those three are the blank
        /// frames, which are classified <see cref="CharacterAtlasRegionFill.Empty"/> and never reach
        /// this test. One line flags nothing extra but leaves no room for a rect that is 2 px wider
        /// than its art, which the art routinely is. Three is the smallest value with slack, and the
        /// corruption signature it has to catch is tens of pixels wide, not one.</para>
        /// </summary>
        const int EdgeBandPixels = 3;

        /// <summary>How many failing regions are named in the warning before it says "and more".</summary>
        const int MismatchNameLimit = 4;

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
        /// How many mip levels a texture of this size actually holds, level 0 included: 1×1 → 1,
        /// 5×7 → 3, 8×8 → 4, 1024² → 11.
        ///
        /// <para><b>Why this and not <see cref="Texture.mipmapCount"/>.</b> That property described a
        /// deeper chain than the texture could fill on a live build — the editor log carried
        /// <c>Graphics.CopyTexture called with invalid source mip level: got 4, have 4 mips</c> from
        /// the atlas loop. The level's extent is a function of the dimensions, so deriving the depth
        /// from the dimensions is the only answer the copy can be held to. Public so a fixture can pin
        /// the edges rather than re-deriving them.</para>
        /// </summary>
        public static int ComputeMipDepth(int width, int height)
        {
            int longest = Mathf.Max(width, height);
            if (longest < 1)
            {
                return 1;
            }

            int depth = 1;
            while (longest > 1)
            {
                longest >>= 1;
                depth++;
            }

            return depth;
        }

        /// <summary>
        /// The depth this region's texture really has — and the reason the per-level copy loop is gone.
        ///
        /// <para>Kept because the number is still worth knowing, and because deleting it would erase
        /// the evidence. It takes the smaller of what the dimensions allow and what
        /// <see cref="Texture.mipmapCount"/> reports, and the log proved <b>both over-report</b>:
        /// <c>CopyTexture</c> answered <c>got 4, have 4 mips</c> for a texture this function scored 4,
        /// i.e. the level at index 4 does not exist. There is no third source to ask, so no loop can be
        /// held to this number — hence <see cref="BuildCore"/> copies level 0 only and lets
        /// <c>Apply(true)</c> generate the chain from the atlas's own, known depth.</para>
        /// </summary>
        static int RegionMipDepth(in Region region)
        {
            int bySize = ComputeMipDepth(region.Texture.width, region.Texture.height);
            int reported = region.Texture.mipmapCount;
            return reported > 0 ? Mathf.Min(bySize, reported) : bySize;
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
