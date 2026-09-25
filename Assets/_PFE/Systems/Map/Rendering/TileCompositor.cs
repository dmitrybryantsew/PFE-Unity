using System.Collections.Generic;
using UnityEngine;
using Profiler = PFE.Core.Profiling.PfeProfiler;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Generates tile sprites by compositing tiling textures with edge masks.
    /// Port of the AS3 Grafon.drawKusok() compositing concept.
    /// 
    /// In AS3, each material has a tiling texture masked by a per-tile shape.
    /// The mask has 4 corners, each selecting an edge variant based on neighbor analysis (kontur).
    /// 
    /// In Unity, we generate a Texture2D per unique tile appearance:
    ///   1. Sample the material's tiling texture at the tile position
    ///   2. Apply a procedural edge mask based on kontur values
    ///   3. Cache the result as a Sprite
    /// 
    /// Kontur values (from Location.tileKontur -> insKontur):
    ///   0 = fully surrounded (no edge)
    ///   1 = inner corner
    ///   2 = horizontal edge (neighbor above/below missing)
    ///   3 = vertical edge (neighbor left/right missing)
    ///   4 = outer corner (both neighbors missing)
    /// 
    /// Each tile has 4 corners: kont1(top-left), kont2(top-right), kont3(bottom-left), kont4(bottom-right)
    /// </summary>
    public class TileCompositor
    {
        

        private static readonly Dictionary<string, string> ManualBorderAtlasOverrides = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "maskStoneBorder", "MyStoneBorderMask" },
            { "MyStoneBorderMask", "MyStoneBorderMask" }
        };

        private static readonly HashSet<string> MirroredCornerMaskNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "maskBare",
            "TileMaskBare",
            "maskDamaged",
            "TileMaskDamaged",
            "BorderMask",
            "maskSimple",
            "maskStoneBorder",
            "maskMetalBorder",
            "maskDirtBorder",
            "maskBorderBare",
            "SkolMask",
            "maskSkol"
        };

        private static readonly HashSet<string> MirroredFloorMaskNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "FloorMask",
            "maskFloor"
        };

        private static readonly HashSet<string> ExplicitMainShapeMaskNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "maskBare",
            "TileMaskBare",
            "maskDamaged",
            "TileMaskDamaged"
        };

        // Some extracted tiling textures include a phase offset relative to the
        // original Flash fill origin. Applying that offset restores seamless room-wide repeats.
        private static readonly Dictionary<string, Vector2Int> ManualTextureSampleOffsets = new Dictionary<string, Vector2Int>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "tBlocks", new Vector2Int(12, 2) }
        };

        private const int TILE_PX = 40; // Tile size in pixels
        private const int EDGE_PX = 6;  // Edge softness in pixels
        private const int CORNER_PX = 8; // Corner radius in pixels
        private const int BORDER_WIDTH_PX = 5;
        private const int FLOOR_BAND_PX = 10;
        private const float AlphaEpsilon = 0.001f;

        // Sprite cache: key = "materialId_k1_k2_k3_k4" -> Sprite
        private Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        private readonly Dictionary<Texture2D, Texture2D> _readableTextureCache = new Dictionary<Texture2D, Texture2D>();

        // ── Boot-cost optimisation ────────────────────────────────────────────────────
        // ROLLBACK: set this to false to restore per-pixel Texture2D.GetPixel sampling.
        //
        // GetPixel is a bounds-checked virtual call. GenerateTile performs up to ~4800 of
        // them per tile (up to 3 textures x 1600 px, plus mask sampling), which dominated
        // boot. Caching GetPixels() once per texture and indexing the buffer is
        // mathematically identical — GetPixel(x,y) == pixels[y * width + x] — but far cheaper.
        private const bool CacheTexturePixels = true;

        private readonly Dictionary<Texture2D, Color[]> _pixelsCache = new Dictionary<Texture2D, Color[]>();

        /// <summary>Returns the cached pixel buffer for a texture, or null if unavailable.</summary>
        private Color[] GetCachedPixels(Texture2D texture)
        {
            if (!CacheTexturePixels || texture == null) return null;
            if (_pixelsCache.TryGetValue(texture, out Color[] cached)) return cached;

            Color[] pixels;
            try { pixels = texture.GetPixels(); }
            catch (System.Exception) { return null; }

            _pixelsCache[texture] = pixels;
            return pixels;
        }

        // ── Boot-cost optimisation ────────────────────────────────────────────────────
        // ROLLBACK: set this to false to read Sprite/Texture properties per pixel again.
        //
        // Sprite.rect, Sprite.texture, Texture2D.width/height and Texture2D.name are NOT
        // cached fields — every access is a managed->native interop call (~1 us in a
        // development build). GenerateTile runs 1600 pixels per generated tile and touched
        // ~9-11 of those properties per pixel => ~20M native calls per room, which measured
        // as ~19 s of boot (STATS: generate=18758ms for 1122 generations).
        // Resolve each object's native properties once and reuse them.
        private const bool CacheNativeProps = true;

        private struct SpriteSampleInfo
        {
            public Texture2D readable;
            public Rect rect;
            public int texWidth;
            public bool valid;
        }

        private struct TilingSampleInfo
        {
            public int width;
            public int height;
            public Vector2Int offset;
        }

        private readonly Dictionary<Sprite, SpriteSampleInfo> _spriteSampleCache = new Dictionary<Sprite, SpriteSampleInfo>();
        private readonly Dictionary<Texture2D, TilingSampleInfo> _tilingInfoCache = new Dictionary<Texture2D, TilingSampleInfo>();
        private readonly Dictionary<string, IReadOnlyList<Sprite>> _maskFramesCache = new Dictionary<string, IReadOnlyList<Sprite>>(System.StringComparer.Ordinal);

        /// <summary>Resolves a Sprite's native properties (rect, texture) once and caches them.</summary>
        private SpriteSampleInfo GetSpriteSampleInfo(Sprite sprite)
        {
            if (sprite == null) return default;

            if (CacheNativeProps && _spriteSampleCache.TryGetValue(sprite, out SpriteSampleInfo cached))
                return cached;

            var info = new SpriteSampleInfo
            {
                rect = sprite.rect,
                readable = GetReadableTexture(sprite.texture)
            };

            if (info.readable != null)
            {
                info.texWidth = info.readable.width;
                info.valid = true;
            }

            if (CacheNativeProps) _spriteSampleCache[sprite] = info;
            return info;
        }

        /// <summary>
        /// Cached width/height/sample-offset for a tiling texture.
        /// Keyed on the SOURCE texture, not the readable copy: CreateReadableCopy preserves
        /// width and height but not .name, and the manual offset table is keyed by name.
        /// </summary>
        private TilingSampleInfo GetTilingInfo(Texture2D texture)
        {
            if (texture == null) return default;

            if (CacheNativeProps && _tilingInfoCache.TryGetValue(texture, out TilingSampleInfo cached))
                return cached;

            string textureName = texture.name;
            var info = new TilingSampleInfo
            {
                width = texture.width,
                height = texture.height,
                offset = string.IsNullOrWhiteSpace(textureName)
                    ? Vector2Int.zero
                    : (ManualTextureSampleOffsets.TryGetValue(textureName, out Vector2Int found) ? found : Vector2Int.zero)
            };

            if (CacheNativeProps) _tilingInfoCache[texture] = info;
            return info;
        }

        /// <summary>
        /// Memoised mask frame lookup. Pure lookup memo — but note it becomes stale if the
        /// underlying TileMaskLookup is mutated (Clear/SetEntry) while this compositor lives.
        /// </summary>
        private IReadOnlyList<Sprite> GetMaskFrames(string maskName)
        {
            if (_maskLookup == null || string.IsNullOrWhiteSpace(maskName)) return null;
            if (_maskFramesCache.TryGetValue(maskName, out IReadOnlyList<Sprite> cached)) return cached;

            IReadOnlyList<Sprite> frames = _maskLookup.GetFrames(maskName);
            _maskFramesCache[maskName] = frames;
            return frames;
        }

        // ── Boot-cost optimisation ────────────────────────────────────────────────────
        // ROLLBACK: set this to false to restore the previous per-pixel lookup path.
        //
        // After native-prop caching (CacheNativeProps) the remaining per-pixel cost WAS the
        // dictionary lookups themselves: Dictionary<Sprite/T> and Dictionary<Texture2D/T>
        // resolve through UnityEngine.Object.Equals -> CompareBaseObjects ->
        // IsNativeObjectAlive, which is itself a native call. ~9-10 lookups per pixel x
        // 1.8M pixels = ~16M native calls (STATS: generate=9782ms for 1122 generations,
        // 5.45us/pixel, 4.44us/sample).
        //
        // GenerateTile already knows the material and k1..k4 before the pixel loop starts,
        // so every lookup that depends only on those is hoisted to once per generation.
        private const bool HoistSamplers = true;

        private struct TilingSampler
        {
            public Texture2D source;    // as resolved from the material; null => slot not composited
            public Texture2D readable;  // CPU-readable copy; null => contributes fallbackColor
            public Color[] pixels;      // cached buffer; null => GetPixel fallback
            public int width;
            public int height;
            public Vector2Int offset;
        }

        private struct MaskSampler
        {
            public bool valid;              // frames != null && frames.Count > 0
            public int frameCount;
            public Sprite[] sprites;        // per frame (null-checked by the floor path)
            public SpriteSampleInfo[] info; // per frame
            public bool cropSingleFrame;
            public Rect opaqueBounds;
            public bool mirroredCorner;
            public bool mirroredFloor;
        }

        private sealed class MaterialSamplers
        {
            public TilingSampler floorSampler;
            public bool hasFloorTexture;

            public TilingSampler mainSampler;   // sampled regardless of null-ness (falls back to fallbackColor)

            public TilingSampler borderSampler;
            public bool hasBorderTexture;

            public MaskSampler floorMask;
            public MaskSampler mainMask;
            public MaskSampler borderMask;
            public MaskSampler borderManualAtlas;
            public bool hasManualBorderAtlas;

            public bool usesExplicitMainShape;
        }

        private readonly Dictionary<string, MaskSampler> _maskSamplerCache =
            new Dictionary<string, MaskSampler>(System.StringComparer.Ordinal);
        private readonly Dictionary<MaterialRenderEntry, MaterialSamplers> _materialSamplerCache =
            new Dictionary<MaterialRenderEntry, MaterialSamplers>();
        private MaterialSamplers _nullMaterialSamplers;

        /// <summary>
        /// One dictionary lookup per generation instead of ~9-10 per pixel.
        /// Mask samplers do not depend on k1..k4 (only the trivial IsFullySurroundedTile test
        /// does, and that stays in the sampling call), so they are cacheable by name.
        /// </summary>
        private MaterialSamplers GetMaterialSamplers(MaterialRenderEntry material)
        {
            if (material == null)
            {
                return _nullMaterialSamplers ?? (_nullMaterialSamplers = BuildMaterialSamplers(null));
            }

            if (_materialSamplerCache.TryGetValue(material, out MaterialSamplers cached))
                return cached;

            MaterialSamplers built = BuildMaterialSamplers(material);
            _materialSamplerCache[material] = built;
            return built;
        }

        private MaterialSamplers BuildMaterialSamplers(MaterialRenderEntry material)
        {
            var samplers = new MaterialSamplers
            {
                floorSampler = BuildTilingSampler(ResolveTexture(material?.floorTexture)),
                mainSampler = BuildTilingSampler(ResolveTexture(material?.mainTexture)),
                borderSampler = BuildTilingSampler(ResolveTexture(material?.borderTexture)),
                floorMask = GetMaskSampler(material?.floorMask),
                mainMask = GetMaskSampler(material?.mainMask),
                borderMask = GetMaskSampler(material?.borderMask),
                usesExplicitMainShape = UsesExplicitMainShape(material?.mainMask)
            };

            samplers.hasFloorTexture = samplers.floorSampler.source != null;
            samplers.hasBorderTexture = samplers.borderSampler.source != null;

            string borderMaskName = material?.borderMask;
            if (!string.IsNullOrWhiteSpace(borderMaskName) &&
                ManualBorderAtlasOverrides.TryGetValue(borderMaskName, out string atlasName))
            {
                MaskSampler atlas = GetMaskSampler(atlasName);
                if (atlas.valid && atlas.frameCount >= 9)
                {
                    samplers.borderManualAtlas = atlas;
                    samplers.hasManualBorderAtlas = true;
                }
            }

            return samplers;
        }

        private TilingSampler BuildTilingSampler(Texture2D source)
        {
            var sampler = new TilingSampler { source = source };
            if (source == null)
            {
                return sampler;
            }

            TilingSampleInfo info = GetTilingInfo(source);
            sampler.width = info.width;
            sampler.height = info.height;
            sampler.offset = info.offset;
            sampler.readable = GetReadableTexture(source);
            sampler.pixels = GetCachedPixels(sampler.readable);
            return sampler;
        }

        private MaskSampler GetMaskSampler(string maskName)
        {
            if (_maskLookup == null || string.IsNullOrWhiteSpace(maskName))
            {
                return default;
            }

            if (_maskSamplerCache.TryGetValue(maskName, out MaskSampler cached))
                return cached;

            MaskSampler sampler = default;

            IReadOnlyList<Sprite> frames = GetMaskFrames(maskName);
            if (frames != null && frames.Count > 0)
            {
                sampler.valid = true;
                sampler.frameCount = frames.Count;
                sampler.mirroredCorner = IsMirroredCornerMask(maskName);
                sampler.mirroredFloor = IsMirroredFloorMask(maskName);
                sampler.sprites = new Sprite[frames.Count];
                sampler.info = new SpriteSampleInfo[frames.Count];

                for (int i = 0; i < frames.Count; i++)
                {
                    sampler.sprites[i] = frames[i];
                    sampler.info[i] = GetSpriteSampleInfo(frames[i]);
                }

                sampler.cropSingleFrame = frames.Count == 1 && ShouldCropSingleFrameMask(frames[0]);
                if (sampler.cropSingleFrame)
                {
                    sampler.opaqueBounds = GetOpaqueSpriteBounds(frames[0]);
                }
            }

            _maskSamplerCache[maskName] = sampler;
            return sampler;
        }

        private readonly Dictionary<Sprite, Rect> _opaqueSpriteBoundsCache = new Dictionary<Sprite, Rect>();

        private readonly TileTextureLookup _textureLookup;
        private readonly MaterialRenderDatabase _materialDb;
        private readonly TileMaskLookup _maskLookup;

        // Fallback color for missing textures
        private static readonly Color FallbackWall = new Color(0.45f, 0.45f, 0.45f, 1f);
        private static readonly Color FallbackBack = new Color(0.25f, 0.25f, 0.3f, 1f);

        public TileCompositor(TileTextureLookup textureLookup, MaterialRenderDatabase materialDb, TileMaskLookup maskLookup = null)
        {
            _textureLookup = textureLookup;
            _materialDb = materialDb;
            _maskLookup = maskLookup;
        }

        // ── Boot-cost optimisation ────────────────────────────────────────────────────
        // ROLLBACK: set this to false to restore the previous behaviour (cache key used the
        // raw tileX/tileY, so every tile position generated its own 40x40 texture).
        //
        // Sampling is PositiveModulo(tileX * TILE_PX + x, texture.width), so the sampled
        // phase repeats every (width / gcd(TILE_PX, width)) tiles. Keying the cache on a
        // coordinate quantised by that period is PIXEL-IDENTICAL, but collapses the ~1136
        // per-position generations down to one per distinct phase.
        private const bool QuantizeTilePhase = true;

        private static int Gcd(int a, int b)
        {
            while (b != 0) { int t = a % b; a = b; b = t; }
            return a < 0 ? -a : a;
        }

        private static int Lcm(int a, int b)
        {
            if (a <= 0 || b <= 0) return Mathf.Max(a, b);
            return a / Gcd(a, b) * b;
        }

        private static int PhasePeriod(int textureSize)
        {
            if (textureSize <= 0) return 1;
            int g = Gcd(TILE_PX, textureSize);
            return g <= 0 ? 1 : textureSize / g;
        }

        private readonly Dictionary<string, Vector2Int> _phasePeriodCache = new Dictionary<string, Vector2Int>();

        private static void AccumulatePeriod(Texture2D tex, ref int px, ref int py)
        {
            if (tex == null) return;
            px = Lcm(px, PhasePeriod(tex.width));
            py = Lcm(py, PhasePeriod(tex.height));
        }

        /// <summary>
        /// Period (in whole tiles) after which the sampled texture phase repeats for a material.
        /// Cached per material so texture lookups are not repeated for every tile.
        /// </summary>
        private Vector2Int GetPhasePeriod(string materialId, MaterialRenderEntry material)
        {
            string id = materialId ?? "";
            if (_phasePeriodCache.TryGetValue(id, out Vector2Int cached))
                return cached;

            int px = 1, py = 1;
            AccumulatePeriod(ResolveTexture(material?.mainTexture), ref px, ref py);
            AccumulatePeriod(ResolveTexture(material?.floorTexture), ref px, ref py);
            AccumulatePeriod(ResolveTexture(material?.borderTexture), ref px, ref py);

            var period = new Vector2Int(Mathf.Max(1, px), Mathf.Max(1, py));
            _phasePeriodCache[id] = period;
            return period;
        }

        /// <summary>
        /// Get or create a sprite for a front (wall) tile.
        /// </summary>
        /// <param name="materialId">Form front graphic ID (e.g., "C", "A")</param>
        /// <param name="tileX">Tile grid X (for texture tiling offset)</param>
        /// <param name="tileY">Tile grid Y (for texture tiling offset)</param>
        /// <param name="kont1">Top-left corner kontur value (0-4)</param>
        /// <param name="kont2">Top-right corner kontur value (0-4)</param>
        /// <param name="kont3">Bottom-left corner kontur value (0-4)</param>
        /// <param name="kont4">Bottom-right corner kontur value (0-4)</param>
        public Sprite GetFrontTileSprite(string materialId, int tileX, int tileY,
            int kont1, int kont2, int kont3, int kont4)
        {
            // Look up material render data (needed before keying when the phase is quantised)
            var matRender = _materialDb?.GetFrontMaterial(materialId);

            int keyX = tileX, keyY = tileY;
            if (QuantizeTilePhase)
            {
                Vector2Int period = GetPhasePeriod(materialId, matRender);
                keyX = PositiveModulo(tileX, period.x);
                keyY = PositiveModulo(tileY, period.y);
            }

            // Position affects the sampled texture phase, so it must be part of the cache key.
            string key = $"f_{materialId}_{keyX}_{keyY}_{kont1}{kont2}{kont3}{kont4}";

            if (_spriteCache.TryGetValue(key, out Sprite cached))
            {
                return cached;
            }

            Texture2D tileTex;
            using (Profiler.Region("tiles.generateFront", "boot: per-miss front tile pixel generation. Region call count == front cache misses"))
            {
                tileTex = GenerateTile(matRender, FallbackWall, keyX, keyY,
                    kont1, kont2, kont3, kont4);
            }

            Sprite sprite;
            using (Profiler.Region("tiles.spriteCreate", "boot: Sprite.Create per generated tile — measured 20-43ms total, trivial"))
            {
                sprite = Sprite.Create(tileTex,
                    new Rect(0, 0, TILE_PX, TILE_PX),
                    new Vector2(0.5f, 0.5f),
                    100f,
                    0,
                    SpriteMeshType.FullRect); // Avoid tight-mesh cracks between adjacent tiles
            }

            sprite.name = key;
            _spriteCache[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// Get or create a sprite for a back (background) tile.
        /// </summary>
        public Sprite GetBackTileSprite(string materialId, int tileX, int tileY,
            int pont1, int pont2, int pont3, int pont4)
        {
            var matRender = _materialDb?.GetBackMaterial(materialId);

            int keyX = tileX, keyY = tileY;
            if (QuantizeTilePhase)
            {
                Vector2Int period = GetPhasePeriod(materialId, matRender);
                keyX = PositiveModulo(tileX, period.x);
                keyY = PositiveModulo(tileY, period.y);
            }

            string key = $"b_{materialId}_{keyX}_{keyY}_{pont1}{pont2}{pont3}{pont4}";

            if (_spriteCache.TryGetValue(key, out Sprite cached))
            {
                return cached;
            }

            Texture2D tileTex;
            using (Profiler.Region("tiles.generateBack", "boot: per-miss back tile pixel generation. Region call count == back cache misses"))
            {
                tileTex = GenerateTile(matRender, FallbackBack, keyX, keyY,
                    pont1, pont2, pont3, pont4);
            }

            Sprite sprite;
            using (Profiler.Region("tiles.spriteCreate", "boot: Sprite.Create per generated tile — measured 20-43ms total, trivial"))
            {
                sprite = Sprite.Create(tileTex,
                    new Rect(0, 0, TILE_PX, TILE_PX),
                    new Vector2(0.5f, 0.5f),
                    100f,
                    0,
                    SpriteMeshType.FullRect);
            }

            sprite.name = key;
            _spriteCache[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// Get a simple colored sprite for a material (no edge masking).
        /// Used as ultra-fast fallback.
        /// </summary>
        public Sprite GetSimpleSprite(string materialId, bool isFront)
        {
            string key = $"s_{(isFront ? "f" : "b")}_{materialId}";
            if (_spriteCache.TryGetValue(key, out Sprite cached))
                return cached;

            var matRender = isFront
                ? _materialDb?.GetFrontMaterial(materialId)
                : _materialDb?.GetBackMaterial(materialId);

            Texture2D tileTex = GenerateTile(matRender,
                isFront ? FallbackWall : FallbackBack, 0, 0, 0, 0, 0, 0);

            Sprite sprite = Sprite.Create(tileTex,
                new Rect(0, 0, TILE_PX, TILE_PX),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);

            sprite.name = key;
            _spriteCache[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// Generate a tile texture by sampling a tiling texture and applying edge mask.
        /// </summary>
        private Texture2D GenerateTile(MaterialRenderEntry material, Color fallbackColor,
            int tileX, int tileY, int k1, int k2, int k3, int k4)
        {
            Texture2D result = new Texture2D(TILE_PX, TILE_PX, TextureFormat.RGBA32, false);
            result.filterMode = FilterMode.Point;
            result.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[TILE_PX * TILE_PX];

            // Run 17: tiles.generateFront 919.5 + tiles.generateBack 909.1 = 1828.6 ms, the largest
            // item in boot and the only big one with no sub-regions. These three split it into the
            // per-pixel composition, the post-pass filter, and the texture upload. Ids are shared by
            // the front and back paths on purpose — the parent region already tells us which one,
            // and sharing keeps the id set static instead of forwarding a label.
            using (Profiler.Region("tiles.compose", "boot: ComposeTilePixelHoisted per pixel — mask sampling + edge mask + alpha composite"))
            {
                if (HoistSamplers)
                {
                    // All per-pixel lookups resolved once, before the loop.
                    MaterialSamplers samplers = GetMaterialSamplers(material);

                    for (int y = 0; y < TILE_PX; y++)
                    {
                        for (int x = 0; x < TILE_PX; x++)
                        {
                            Color pixel = ComposeTilePixelHoisted(
                                samplers, fallbackColor, tileX, tileY, k1, k2, k3, k4, x, y);

                            // Unity textures are bottom-up, AS3 is top-down
                            pixels[(TILE_PX - 1 - y) * TILE_PX + x] = pixel;
                        }
                    }
                }
                else
                {
                    Texture2D mainTexture = ResolveTexture(material?.mainTexture);
                    Texture2D floorTexture = ResolveTexture(material?.floorTexture);
                    Texture2D borderTexture = ResolveTexture(material?.borderTexture);

                    for (int y = 0; y < TILE_PX; y++)
                    {
                        for (int x = 0; x < TILE_PX; x++)
                        {
                            Color pixel = ComposeTilePixel(
                                material,
                                fallbackColor,
                                mainTexture,
                                floorTexture,
                                borderTexture,
                                tileX,
                                tileY,
                                k1,
                                k2,
                                k3,
                                k4,
                                x,
                                y);

                            // Unity textures are bottom-up, AS3 is top-down
                            pixels[(TILE_PX - 1 - y) * TILE_PX + x] = pixel;
                        }
                    }
                }
            }

            using (Profiler.Region("tiles.filter", "boot: ApplyMaterialFilter post-pass over the whole tile"))
            {
                ApplyMaterialFilter(pixels, TILE_PX, TILE_PX, material?.filterType);
            }

            using (Profiler.Region("tiles.upload", "boot: SetPixels + Apply for one tile texture"))
            {
                result.SetPixels(pixels);
                result.Apply();
            }

            return result;
        }

        private Color ComposeTilePixel(
            MaterialRenderEntry material,
            Color fallbackColor,
            Texture2D mainTexture,
            Texture2D floorTexture,
            Texture2D borderTexture,
            int tileX,
            int tileY,
            int k1,
            int k2,
            int k3,
            int k4,
            int x,
            int y)
        {
            Color pixel = new Color(0f, 0f, 0f, 0f);

            if (floorTexture != null)
            {
                bool hasImportedFloorMask = HasMaskFrames(material?.floorMask);
                float floorAlpha = hasImportedFloorMask
                    ? SampleFloorMaskAlpha(material?.floorMask, x, y, k1, k2)
                    : ComputeProceduralFloorAlpha(x, y, k1, k2);
                pixel = AlphaComposite(pixel, SampleTilingTexture(floorTexture, fallbackColor, tileX, tileY, x, y), floorAlpha);
            }

            float shapeAlpha = ComputeEdgeMask(x, y, k1, k2, k3, k4);
            float mainMaskAlpha = SampleMaskAlpha(material?.mainMask, x, y, k1, k2, k3, k4, 1f);
            float mainAlpha = UsesExplicitMainShape(material?.mainMask)
                ? mainMaskAlpha
                : shapeAlpha * mainMaskAlpha;

            if (mainAlpha > AlphaEpsilon)
            {
                pixel = AlphaComposite(pixel, SampleTilingTexture(mainTexture, fallbackColor, tileX, tileY, x, y), mainAlpha);
            }

            if (borderTexture != null)
            {
                bool hasManualBorderMask = TryGetManualBorderAtlasFrames(material?.borderMask, out _);
                bool hasImportedBorderMask = hasManualBorderMask || HasMaskFrames(material?.borderMask);
                float borderAlpha = hasManualBorderMask
                    ? SampleManualBorderMaskAlpha(material?.borderMask, x, y, k1, k2, k3, k4)
                    : hasImportedBorderMask
                        ? SampleMaskAlpha(material?.borderMask, x, y, k1, k2, k3, k4, 0f)
                        : ComputeProceduralBorderAlpha(x, y, k1, k2, k3, k4);
                if (borderAlpha > AlphaEpsilon)
                {
                    pixel = AlphaComposite(pixel, SampleTilingTexture(borderTexture, fallbackColor, tileX, tileY, x, y), borderAlpha);
                }
            }

            return pixel;
        }

        // ── Hoisted (HoistSamplers == true) composition path ──────────────────────────
        // Same logic as ComposeTilePixel, but every texture/sprite lookup arrives pre-resolved
        // in the samplers, so the pixel loop performs no dictionary lookups at all.

        private Color ComposeTilePixelHoisted(
            MaterialSamplers samplers,
            Color fallbackColor,
            int tileX,
            int tileY,
            int k1,
            int k2,
            int k3,
            int k4,
            int x,
            int y)
        {
            Color pixel = new Color(0f, 0f, 0f, 0f);

            if (samplers.hasFloorTexture)
            {
                float floorAlpha = samplers.floorMask.valid
                    ? SampleFloorMaskAlpha(samplers.floorMask, x, y, k1, k2)
                    : ComputeProceduralFloorAlpha(x, y, k1, k2);
                pixel = AlphaComposite(pixel, SampleTilingTexture(samplers.floorSampler, fallbackColor, tileX, tileY, x, y), floorAlpha);
            }

            float shapeAlpha = ComputeEdgeMask(x, y, k1, k2, k3, k4);
            float mainMaskAlpha = SampleMaskAlpha(samplers.mainMask, x, y, k1, k2, k3, k4, 1f);
            float mainAlpha = samplers.usesExplicitMainShape
                ? mainMaskAlpha
                : shapeAlpha * mainMaskAlpha;

            if (mainAlpha > AlphaEpsilon)
            {
                pixel = AlphaComposite(pixel, SampleTilingTexture(samplers.mainSampler, fallbackColor, tileX, tileY, x, y), mainAlpha);
            }

            if (samplers.hasBorderTexture)
            {
                bool hasManualBorderMask = samplers.hasManualBorderAtlas;
                bool hasImportedBorderMask = hasManualBorderMask || samplers.borderMask.valid;
                float borderAlpha = hasManualBorderMask
                    ? SampleManualBorderMaskAlpha(samplers.borderManualAtlas, x, y, k1, k2, k3, k4)
                    : hasImportedBorderMask
                        ? SampleMaskAlpha(samplers.borderMask, x, y, k1, k2, k3, k4, 0f)
                        : ComputeProceduralBorderAlpha(x, y, k1, k2, k3, k4);
                if (borderAlpha > AlphaEpsilon)
                {
                    pixel = AlphaComposite(pixel, SampleTilingTexture(samplers.borderSampler, fallbackColor, tileX, tileY, x, y), borderAlpha);
                }
            }

            return pixel;
        }

        private Color SampleTilingTexture(in TilingSampler sampler, Color fallbackColor, int tileX, int tileY, int x, int y)
        {
            if (sampler.readable == null)
            {
                return fallbackColor;
            }

            int texX = PositiveModulo(tileX * TILE_PX + x + sampler.offset.x, sampler.width);
            int texY = PositiveModulo(tileY * TILE_PX + y + sampler.offset.y, sampler.height);

            return sampler.pixels != null
                ? sampler.pixels[texY * sampler.width + texX]
                : sampler.readable.GetPixel(texX, texY);
        }

        private float SampleMaskAlpha(in MaskSampler sampler, int px, int py, int k1, int k2, int k3, int k4, float defaultAlpha)
        {
            if (!sampler.valid)
            {
                return defaultAlpha;
            }

            int frameIndex = 0;
            if (sampler.frameCount > 1)
            {
                frameIndex = ResolveMaskFrameIndex(sampler.frameCount, px, py, k1, k2, k3, k4);
                if (frameIndex < 0)
                    return defaultAlpha;  // 1.0 for main (fully opaque), 0.0 for border (no border)
            }

            frameIndex = Mathf.Clamp(frameIndex, 0, sampler.frameCount - 1);
            SpriteSampleInfo info = sampler.info[frameIndex];

            if (sampler.cropSingleFrame && defaultAlpha > 0.5f && IsFullySurroundedTile(k1, k2, k3, k4))
            {
                return 1f;
            }

            if (sampler.mirroredCorner)
            {
                MirrorCornerSampleCoordinates(px, py, out int mirroredX, out int mirroredY);
                return sampler.cropSingleFrame
                    ? SampleSpriteAlphaCore(info, sampler.opaqueBounds, mirroredX, mirroredY, TILE_PX, TILE_PX)
                    : SampleSpriteAlphaCore(info, info.rect, mirroredX, mirroredY, TILE_PX, TILE_PX);
            }

            return sampler.cropSingleFrame
                ? SampleSpriteAlphaCore(info, sampler.opaqueBounds, px, py, TILE_PX, TILE_PX)
                : SampleSpriteAlphaCore(info, info.rect, px, py, TILE_PX, TILE_PX);
        }

        private float SampleManualBorderMaskAlpha(in MaskSampler sampler, int px, int py, int k1, int k2, int k3, int k4)
        {
            if (!sampler.valid)
            {
                return 0f;
            }

            if (!KonturBorderMaskAtlasMapper.TryMap(px, py, k1, k2, k3, k4, sampler.frameCount, out var sample))
            {
                return ComputeProceduralBorderAlpha(px, py, k1, k2, k3, k4);
            }

            int frameIndex = Mathf.Clamp(sample.FrameIndex, 0, sampler.frameCount - 1);
            SpriteSampleInfo info = sampler.info[frameIndex];
            return SampleSpriteAlphaCore(info, info.rect, sample.LocalX, sample.LocalY, 20f, 20f);
        }

        private float SampleFloorMaskAlpha(in MaskSampler sampler, int px, int py, int leftKontur, int rightKontur)
        {
            if (!sampler.valid)
            {
                return 0f;
            }

            int bandTop = (TILE_PX - FLOOR_BAND_PX) / 2;
            int bandBottom = bandTop + FLOOR_BAND_PX;
            if (py < bandTop || py >= bandBottom)
            {
                return 0f;
            }

            int contourValue = px < TILE_PX / 2 ? leftKontur : rightKontur;
            int frameIndex = Mathf.Clamp(contourValue, 0, sampler.frameCount - 1);
            if (sampler.sprites[frameIndex] == null)
            {
                return 0f;
            }

            if (sampler.mirroredFloor && px >= TILE_PX / 2)
            {
                px = TILE_PX - 1 - px;
            }

            SpriteSampleInfo info = sampler.info[frameIndex];
            Rect spriteRect = info.rect;
            int localY = py - bandTop;
            int sampleY = Mathf.Clamp(Mathf.FloorToInt((localY + 0.5f) / FLOOR_BAND_PX * spriteRect.height), 0, Mathf.Max(0, Mathf.FloorToInt(spriteRect.height) - 1));
            return SampleSpriteAlphaCore(info, spriteRect, px, sampleY, spriteRect.width, spriteRect.height);
        }

        private static void ApplyMaterialFilter(Color[] pixels, int width, int height, string filterType)
        {
            if (pixels == null || pixels.Length == 0 || string.IsNullOrWhiteSpace(filterType))
            {
                return;
            }

            switch (filterType.Trim().ToLowerInvariant())
            {
                case "plitka":
                    ApplyPlitkaFilter(pixels, width, height);
                    break;
            }
        }

        private static void ApplyPlitkaFilter(Color[] pixels, int width, int height)
        {
            Color[] source = new Color[pixels.Length];
            pixels.CopyTo(source, 0);

            float[] alpha = new float[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                alpha[i] = source[i].a;
            }

            float[] blurredAlpha = BlurAlpha(alpha, width, height, 2, 2);
            Vector2 lightDir = new Vector2(0.34f, -0.94f).normalized;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    Color color = source[index];

                    float left = alpha[indexForClamped(x - 1, y, width, height)];
                    float right = alpha[indexForClamped(x + 1, y, width, height)];
                    float up = alpha[indexForClamped(x, y - 1, width, height)];
                    float down = alpha[indexForClamped(x, y + 1, width, height)];

                    float gradX = left - right;
                    float gradY = up - down;
                    float bevel = Mathf.Clamp(gradX * lightDir.x + gradY * lightDir.y, -1f, 1f);

                    if (color.a > AlphaEpsilon)
                    {
                        float highlight = Mathf.Max(0f, bevel) * 0.18f;
                        float shadow = Mathf.Max(0f, -bevel) * 0.18f;
                        color.r = Mathf.Clamp01(color.r + highlight - shadow);
                        color.g = Mathf.Clamp01(color.g + highlight - shadow);
                        color.b = Mathf.Clamp01(color.b + highlight - shadow);
                    }
                    else
                    {
                        float glow = Mathf.Clamp01((blurredAlpha[index] - alpha[index]) * 0.5f);
                        if (glow > 0f)
                        {
                            color = new Color(0f, 0f, 0f, glow * 0.5f);
                        }
                    }

                    pixels[index] = color;
                }
            }
        }

        private static float[] BlurAlpha(float[] source, int width, int height, int radius, int iterations)
        {
            if (source == null || source.Length == 0 || radius <= 0 || iterations <= 0)
            {
                return source ?? System.Array.Empty<float>();
            }

            float[] current = source;
            for (int i = 0; i < iterations; i++)
            {
                float[] horizontal = new float[current.Length];
                float[] vertical = new float[current.Length];
                BlurHorizontal(current, horizontal, width, height, radius);
                BlurVertical(horizontal, vertical, width, height, radius);
                current = vertical;
            }

            return current;
        }

        private static void BlurHorizontal(float[] source, float[] target, int width, int height, int radius)
        {
            float[] prefix = new float[width + 1];
            for (int y = 0; y < height; y++)
            {
                prefix[0] = 0f;
                int rowStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    prefix[x + 1] = prefix[x] + source[rowStart + x];
                }

                for (int x = 0; x < width; x++)
                {
                    int minX = Mathf.Max(0, x - radius);
                    int maxX = Mathf.Min(width - 1, x + radius);
                    float sum = prefix[maxX + 1] - prefix[minX];
                    target[rowStart + x] = sum / (maxX - minX + 1);
                }
            }
        }

        private static void BlurVertical(float[] source, float[] target, int width, int height, int radius)
        {
            float[] prefix = new float[height + 1];
            for (int x = 0; x < width; x++)
            {
                prefix[0] = 0f;
                for (int y = 0; y < height; y++)
                {
                    prefix[y + 1] = prefix[y] + source[y * width + x];
                }

                for (int y = 0; y < height; y++)
                {
                    int minY = Mathf.Max(0, y - radius);
                    int maxY = Mathf.Min(height - 1, y + radius);
                    float sum = prefix[maxY + 1] - prefix[minY];
                    target[y * width + x] = sum / (maxY - minY + 1);
                }
            }
        }

        private static int indexForClamped(int x, int y, int width, int height)
        {
            int clampedX = Mathf.Clamp(x, 0, width - 1);
            int clampedY = Mathf.Clamp(y, 0, height - 1);
            return clampedY * width + clampedX;
        }

        private Texture2D ResolveTexture(string textureName)
        {
            if (string.IsNullOrEmpty(textureName))
            {
                return null;
            }

            return _textureLookup?.GetTexture(textureName);
        }

        private Color SampleTilingTexture(Texture2D tilingTexture, Color fallbackColor, int tileX, int tileY, int x, int y)
        {
            Texture2D readableTexture = GetReadableTexture(tilingTexture);
            if (readableTexture == null)
            {
                return fallbackColor;
            }

            TilingSampleInfo info = GetTilingInfo(tilingTexture);
            int w = info.width;
            int h = info.height;
            int texX = PositiveModulo(tileX * TILE_PX + x + info.offset.x, w);
            int texY = PositiveModulo(tileY * TILE_PX + y + info.offset.y, h);

            Color[] pixels = GetCachedPixels(readableTexture);
            return pixels != null ? pixels[texY * w + texX] : readableTexture.GetPixel(texX, texY);
        }

        private static Vector2Int GetManualTextureSampleOffset(Texture2D texture)
        {
            if (texture == null || string.IsNullOrWhiteSpace(texture.name))
            {
                return Vector2Int.zero;
            }

            return ManualTextureSampleOffsets.TryGetValue(texture.name, out Vector2Int offset)
                ? offset
                : Vector2Int.zero;
        }

        private float SampleMaskAlpha(string maskName, int px, int py, int k1, int k2, int k3, int k4, float defaultAlpha)
        {
            if (_maskLookup == null || string.IsNullOrWhiteSpace(maskName))
            {
                return defaultAlpha;
            }

            IReadOnlyList<Sprite> frames = GetMaskFrames(maskName);
            if (frames == null || frames.Count == 0)
            {
                return defaultAlpha;
            }

            int frameIndex = 0;
            if (frames.Count > 1)
            {
                frameIndex = ResolveMaskFrameIndex(frames.Count, px, py, k1, k2, k3, k4);
                if (frameIndex < 0)
                    return defaultAlpha;  // 1.0 for main (fully opaque), 0.0 for border (no border)
            }

            Sprite sprite = frames[Mathf.Clamp(frameIndex, 0, frames.Count - 1)];
            bool useOpaqueBounds = frames.Count == 1 && ShouldCropSingleFrameMask(sprite);
            if (useOpaqueBounds && defaultAlpha > 0.5f && IsFullySurroundedTile(k1, k2, k3, k4))
            {
                return 1f;
            }

            if (IsMirroredCornerMask(maskName))
            {
                MirrorCornerSampleCoordinates(px, py, out int mirroredX, out int mirroredY);
                return useOpaqueBounds
                    ? SampleSpriteAlpha(sprite, mirroredX, mirroredY, GetOpaqueSpriteBounds(sprite), TILE_PX, TILE_PX)
                    : SampleSpriteAlpha(sprite, mirroredX, mirroredY);
            }

            return useOpaqueBounds
                ? SampleSpriteAlpha(sprite, px, py, GetOpaqueSpriteBounds(sprite), TILE_PX, TILE_PX)
                : SampleSpriteAlpha(sprite, px, py);
        }

        private float SampleManualBorderMaskAlpha(string maskName, int px, int py, int k1, int k2, int k3, int k4)
        {
            if (!TryGetManualBorderAtlasFrames(maskName, out IReadOnlyList<Sprite> frames))
            {
                return 0f;
            }

            if (!KonturBorderMaskAtlasMapper.TryMap(px, py, k1, k2, k3, k4, frames.Count, out var sample))
            {
                return ComputeProceduralBorderAlpha(px, py, k1, k2, k3, k4);
            }

            Sprite sprite = frames[Mathf.Clamp(sample.FrameIndex, 0, frames.Count - 1)];
            return SampleSpriteAlpha(sprite, sample.LocalX, sample.LocalY, 20f, 20f);
        }

        private bool HasMaskFrames(string maskName)
        {
            if (_maskLookup == null || string.IsNullOrWhiteSpace(maskName))
            {
                return false;
            }

            IReadOnlyList<Sprite> frames = GetMaskFrames(maskName);
            return frames != null && frames.Count > 0;
        }

        private bool TryGetManualBorderAtlasFrames(string maskName, out IReadOnlyList<Sprite> frames)
        {
            frames = null;
            if (_maskLookup == null || string.IsNullOrWhiteSpace(maskName))
            {
                return false;
            }

            if (!ManualBorderAtlasOverrides.TryGetValue(maskName, out string atlasName))
            {
                return false;
            }

            frames = GetMaskFrames(atlasName);
            return frames != null && frames.Count >= 9;
        }

        private float SampleFloorMaskAlpha(string maskName, int px, int py, int leftKontur, int rightKontur)
        {
            if (_maskLookup == null || string.IsNullOrWhiteSpace(maskName))
            {
                return 0f;
            }

            IReadOnlyList<Sprite> frames = GetMaskFrames(maskName);
            if (frames == null || frames.Count == 0)
            {
                return 0f;
            }

            int bandTop = (TILE_PX - FLOOR_BAND_PX) / 2;
            int bandBottom = bandTop + FLOOR_BAND_PX;
            if (py < bandTop || py >= bandBottom)
            {
                return 0f;
            }

            int contourValue = px < TILE_PX / 2 ? leftKontur : rightKontur;
            int frameIndex = Mathf.Clamp(contourValue, 0, frames.Count - 1);
            Sprite sprite = frames[frameIndex];
            if (sprite == null)
            {
                return 0f;
            }

            if (IsMirroredFloorMask(maskName) && px >= TILE_PX / 2)
            {
                px = TILE_PX - 1 - px;
            }

            Rect spriteRect = GetSpriteSampleInfo(sprite).rect;
            int localY = py - bandTop;
            int sampleY = Mathf.Clamp(Mathf.FloorToInt((localY + 0.5f) / FLOOR_BAND_PX * spriteRect.height), 0, Mathf.Max(0, Mathf.FloorToInt(spriteRect.height) - 1));
            return SampleSpriteAlpha(sprite, px, sampleY, spriteRect.width, spriteRect.height);
        }

        private static bool IsMirroredCornerMask(string maskName)
        {
            return !string.IsNullOrWhiteSpace(maskName) && MirroredCornerMaskNames.Contains(maskName);
        }

        private static bool UsesExplicitMainShape(string maskName)
        {
            return !string.IsNullOrWhiteSpace(maskName) && ExplicitMainShapeMaskNames.Contains(maskName);
        }

        private static bool IsMirroredFloorMask(string maskName)
        {
            return !string.IsNullOrWhiteSpace(maskName) && MirroredFloorMaskNames.Contains(maskName);
        }

        private static bool IsFullySurroundedTile(int k1, int k2, int k3, int k4)
        {
            return k1 == 0 && k2 == 0 && k3 == 0 && k4 == 0;
        }

        private static void MirrorCornerSampleCoordinates(int px, int py, out int mirroredX, out int mirroredY)
        {
            mirroredX = px >= TILE_PX / 2 ? TILE_PX - 1 - px : px;
            mirroredY = py >= TILE_PX / 2 ? TILE_PX - 1 - py : py;
        }

        private static int ResolveMaskFrameIndex(int frameCount, int px, int py,
    int k1, int k2, int k3, int k4)
        {
            int halfX = TILE_PX / 2;
            int halfY = TILE_PX / 2;

            int contourValue;
            if (px < halfX && py < halfY)
                contourValue = k1;
            else if (px >= halfX && py < halfY)
                contourValue = k2;
            else if (px < halfX && py >= halfY)
                contourValue = k3;
            else
                contourValue = k4;

            if (contourValue <= 0)
                return -1;  // no edge, skip mask

            return Mathf.Clamp(contourValue, 0, frameCount - 1);
        }

        private float SampleSpriteAlpha(Sprite sprite, int px, int py)
        {
            return SampleSpriteAlpha(sprite, px, py, TILE_PX, TILE_PX);
        }

        private float SampleSpriteAlpha(Sprite sprite, int px, int py, float sampleWidth, float sampleHeight)
        {
            if (sprite == null)
            {
                return 1f;
            }

            SpriteSampleInfo info = GetSpriteSampleInfo(sprite);
            return SampleSpriteAlphaCore(info, info.rect, px, py, sampleWidth, sampleHeight);
        }

        private float SampleSpriteAlpha(Sprite sprite, int px, int py, Rect sampleRect, float sampleWidth, float sampleHeight)
        {
            if (sprite == null)
            {
                return 1f;
            }

            return SampleSpriteAlphaCore(GetSpriteSampleInfo(sprite), sampleRect, px, py, sampleWidth, sampleHeight);
        }

        private float SampleSpriteAlphaCore(SpriteSampleInfo info, Rect sampleRect, int px, int py, float sampleWidth, float sampleHeight)
        {
            if (!info.valid)
            {
                return 1f;
            }

            float width = Mathf.Max(1f, sampleWidth);
            float height = Mathf.Max(1f, sampleHeight);
            int sampleX = Mathf.Clamp(Mathf.FloorToInt(sampleRect.x + ((px + 0.5f) / width) * sampleRect.width), Mathf.FloorToInt(sampleRect.x), Mathf.FloorToInt(sampleRect.xMax) - 1);
            float flippedPy = (height - 1f) - py;
            int sampleY = Mathf.Clamp(Mathf.FloorToInt(sampleRect.y + ((flippedPy + 0.5f) / height) * sampleRect.height), Mathf.FloorToInt(sampleRect.y), Mathf.FloorToInt(sampleRect.yMax) - 1);
            Color[] alphaPixels = GetCachedPixels(info.readable);
            return alphaPixels != null
                ? alphaPixels[sampleY * info.texWidth + sampleX].a
                : info.readable.GetPixel(sampleX, sampleY).a;
        }

        private bool ShouldCropSingleFrameMask(Sprite sprite)
        {
            if (sprite == null)
            {
                return false;
            }

            Rect rect = GetSpriteSampleInfo(sprite).rect;
            return Mathf.RoundToInt(rect.width) != TILE_PX || Mathf.RoundToInt(rect.height) != TILE_PX;
        }

        private Rect GetOpaqueSpriteBounds(Sprite sprite)
        {
            if (sprite == null)
            {
                return new Rect(0f, 0f, TILE_PX, TILE_PX);
            }

            if (_opaqueSpriteBoundsCache.TryGetValue(sprite, out Rect cached))
            {
                return cached;
            }

            Rect spriteRect = sprite.rect;
            Texture2D readableTexture = GetReadableTexture(sprite.texture);
            if (readableTexture == null)
            {
                _opaqueSpriteBoundsCache[sprite] = spriteRect;
                return spriteRect;
            }

            int minX = Mathf.FloorToInt(spriteRect.xMax);
            int minY = Mathf.FloorToInt(spriteRect.yMax);
            int maxX = Mathf.FloorToInt(spriteRect.x) - 1;
            int maxY = Mathf.FloorToInt(spriteRect.y) - 1;

            Color[] boundsPixels = GetCachedPixels(readableTexture);
            int boundsWidth = readableTexture.width;

            for (int y = Mathf.FloorToInt(spriteRect.y); y < Mathf.FloorToInt(spriteRect.yMax); y++)
            {
                for (int x = Mathf.FloorToInt(spriteRect.x); x < Mathf.FloorToInt(spriteRect.xMax); x++)
                {
                    float alpha = boundsPixels != null
                        ? boundsPixels[y * boundsWidth + x].a
                        : readableTexture.GetPixel(x, y).a;

                    if (alpha <= AlphaEpsilon)
                    {
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            Rect opaqueBounds = maxX >= minX && maxY >= minY
                ? new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1)
                : spriteRect;

            _opaqueSpriteBoundsCache[sprite] = opaqueBounds;
            return opaqueBounds;
        }

        private Texture2D GetReadableTexture(Texture2D source)
        {
            if (source == null)
            {
                return null;
            }

            if (_readableTextureCache.TryGetValue(source, out Texture2D cached))
            {
                return cached;
            }

            Texture2D readable = CreateReadableCopy(source);
            _readableTextureCache[source] = readable;
            return readable;
        }

        private static Texture2D CreateReadableCopy(Texture2D source)
        {
            if (source == null)
            {
                return null;
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);

            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;

            Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.filterMode = source.filterMode;
            readable.wrapMode = source.wrapMode;
            readable.ReadPixels(new Rect(0, 0, temporary.width, temporary.height), 0, 0);
            readable.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
            return readable;
        }

        private static Color AlphaComposite(Color destination, Color source, float alphaMultiplier)
        {
            float srcAlpha = Mathf.Clamp01(source.a * alphaMultiplier);
            if (srcAlpha <= 0.001f)
            {
                return destination;
            }

            float outAlpha = srcAlpha + destination.a * (1f - srcAlpha);
            if (outAlpha <= 0.001f)
            {
                return Color.clear;
            }

            Color outColor = new Color(
                (source.r * srcAlpha + destination.r * destination.a * (1f - srcAlpha)) / outAlpha,
                (source.g * srcAlpha + destination.g * destination.a * (1f - srcAlpha)) / outAlpha,
                (source.b * srcAlpha + destination.b * destination.a * (1f - srcAlpha)) / outAlpha,
                outAlpha);

            return outColor;
        }

        private static int PositiveModulo(int value, int modulo)
        {
            if (modulo <= 0)
            {
                return 0;
            }

            int remainder = value % modulo;
            return remainder < 0 ? remainder + modulo : remainder;
        }

        /// <summary>
        /// Compute the edge mask alpha for a pixel position given 4 corner kontur values.
        /// 
        /// The tile is divided into 4 quadrants:
        ///   k1 (top-left)     k2 (top-right)
        ///   k3 (bottom-left)  k4 (bottom-right)
        /// 
        /// Each corner value determines the edge shape in that quadrant:
        ///   0 = full (no edge)
        ///   1 = inner corner (small notch)
        ///   2 = edge along the horizontal axis (top or bottom missing)
        ///   3 = edge along the vertical axis (left or right missing)
        ///   4 = outer corner (both edges meet)
        /// </summary>
        private float ComputeProceduralBorderAlpha(int px, int py, int k1, int k2, int k3, int k4)
        {
            float outer = ComputeEdgeMask(px, py, k1, k2, k3, k4, EDGE_PX, CORNER_PX);
            float inner = ComputeEdgeMask(px, py, k1, k2, k3, k4, EDGE_PX + BORDER_WIDTH_PX, CORNER_PX + BORDER_WIDTH_PX);
            return Mathf.Clamp01(outer - inner);
        }

        private float ComputeProceduralFloorAlpha(int px, int py, int leftKontur, int rightKontur)
        {
            int bandTop = (TILE_PX - FLOOR_BAND_PX) / 2;
            int bandBottom = bandTop + FLOOR_BAND_PX;
            if (py < bandTop || py >= bandBottom)
            {
                return 0f;
            }

            int contourValue = px < TILE_PX / 2 ? leftKontur : rightKontur;
            if (contourValue <= 0)
            {
                return 0f;
            }

            int distanceToBandEdge = Mathf.Min(py - bandTop, bandBottom - 1 - py);
            if (distanceToBandEdge <= 0)
            {
                return 0.5f;
            }

            return Mathf.Clamp01(distanceToBandEdge / 2f);
        }

        private float ComputeEdgeMask(int px, int py, int k1, int k2, int k3, int k4)
        {
            return ComputeEdgeMask(px, py, k1, k2, k3, k4, EDGE_PX, CORNER_PX);
        }

        private float ComputeEdgeMask(int px, int py, int k1, int k2, int k3, int k4, int edgePx, int cornerPx)
        {
            int halfX = TILE_PX / 2;
            int halfY = TILE_PX / 2;

            // Determine which quadrant this pixel is in and get its kontur value
            int k;
            int localX, localY;

            if (px < halfX && py < halfY)
            {
                // Top-left quadrant -> k1
                k = k1;
                localX = px;
                localY = py;
            }
            else if (px >= halfX && py < halfY)
            {
                // Top-right quadrant -> k2
                k = k2;
                localX = TILE_PX - 1 - px;
                localY = py;
            }
            else if (px < halfX && py >= halfY)
            {
                // Bottom-left quadrant -> k3
                k = k3;
                localX = px;
                localY = TILE_PX - 1 - py;
            }
            else
            {
                // Bottom-right quadrant -> k4
                k = k4;
                localX = TILE_PX - 1 - px;
                localY = TILE_PX - 1 - py;
            }

            // localX, localY are now in corner-local coords where (0,0) is the outer corner

            switch (k)
            {
                case 0:
                    // Fully surrounded — full opacity
                    return 1f;

                case 1:
                    // Inner corner — small diagonal notch at the outer corner
                    if (localX + localY < cornerPx)
                        return 0f;
                    if (localX + localY < cornerPx + edgePx)
                        return (float)(localX + localY - cornerPx) / edgePx;
                    return 1f;

                case 2:
                    // Horizontal edge — edge along Y axis (top/bottom missing)
                    if (localY < edgePx)
                        return (float)localY / edgePx;
                    return 1f;

                case 3:
                    // Vertical edge — edge along X axis (left/right missing)
                    if (localX < edgePx)
                        return (float)localX / edgePx;
                    return 1f;

                case 4:
                    // Outer corner — both edges meet
                    float edgeX = localX < edgePx ? (float)localX / edgePx : 1f;
                    float edgeY = localY < edgePx ? (float)localY / edgePx : 1f;
                    // Round the corner
                    if (localX < cornerPx && localY < cornerPx)
                    {
                        float dist = Mathf.Sqrt(localX * localX + localY * localY);
                        if (dist < cornerPx - edgePx)
                            return 0f;
                        if (dist < cornerPx)
                            return (dist - (cornerPx - edgePx)) / edgePx;
                    }
                    return Mathf.Min(edgeX, edgeY);

                default:
                    return 1f;
            }
        }

        /// <summary>
        /// Clear the sprite cache (call when changing rooms or materials).
        /// </summary>
        public void ClearCache()
        {
            // Destroy cached textures to free memory
            foreach (var kvp in _spriteCache)
            {
                if (kvp.Value != null && kvp.Value.texture != null)
                    Object.Destroy(kvp.Value.texture);
            }
            _spriteCache.Clear();

            foreach (var kvp in _readableTextureCache)
            {
                if (kvp.Value != null)
                {
                    Object.Destroy(kvp.Value);
                }
            }
            _readableTextureCache.Clear();
            _opaqueSpriteBoundsCache.Clear();
        }

        /// <summary>
        /// Get cache stats for debugging.
        /// </summary>
        public string GetCacheStats()
        {
            return $"TileCompositor cache: {_spriteCache.Count} sprites";
        }
    }
}
