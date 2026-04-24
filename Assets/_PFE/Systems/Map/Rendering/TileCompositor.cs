using System.Collections.Generic;
using UnityEngine;

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
            // Position affects the sampled texture phase, so it must be part of the cache key.
            string key = $"f_{materialId}_{tileX}_{tileY}_{kont1}{kont2}{kont3}{kont4}";

            if (_spriteCache.TryGetValue(key, out Sprite cached))
                return cached;

            // Look up material render data
            var matRender = _materialDb?.GetFrontMaterial(materialId);
            Texture2D tileTex = GenerateTile(matRender, FallbackWall, tileX, tileY,
                kont1, kont2, kont3, kont4);

            Sprite sprite = Sprite.Create(tileTex,
                new Rect(0, 0, TILE_PX, TILE_PX),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect); // Avoid tight-mesh cracks between adjacent tiles

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
            string key = $"b_{materialId}_{tileX}_{tileY}_{pont1}{pont2}{pont3}{pont4}";

            if (_spriteCache.TryGetValue(key, out Sprite cached))
                return cached;

            var matRender = _materialDb?.GetBackMaterial(materialId);
            Texture2D tileTex = GenerateTile(matRender, FallbackBack, tileX, tileY,
                pont1, pont2, pont3, pont4);

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
            Texture2D mainTexture = ResolveTexture(material?.mainTexture);
            Texture2D floorTexture = ResolveTexture(material?.floorTexture);
            Texture2D borderTexture = ResolveTexture(material?.borderTexture);

            Texture2D result = new Texture2D(TILE_PX, TILE_PX, TextureFormat.RGBA32, false);
            result.filterMode = FilterMode.Point;
            result.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[TILE_PX * TILE_PX];

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

            ApplyMaterialFilter(pixels, TILE_PX, TILE_PX, material?.filterType);
            result.SetPixels(pixels);
            result.Apply();
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

            Vector2Int sampleOffset = GetManualTextureSampleOffset(tilingTexture);
            int texX = PositiveModulo(tileX * TILE_PX + x + sampleOffset.x, readableTexture.width);
            int texY = PositiveModulo(tileY * TILE_PX + y + sampleOffset.y, readableTexture.height);
            return readableTexture.GetPixel(texX, texY);
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

            IReadOnlyList<Sprite> frames = _maskLookup.GetFrames(maskName);
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

            IReadOnlyList<Sprite> frames = _maskLookup.GetFrames(maskName);
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

            frames = _maskLookup.GetFrames(atlasName);
            return frames != null && frames.Count >= 9;
        }

        private float SampleFloorMaskAlpha(string maskName, int px, int py, int leftKontur, int rightKontur)
        {
            if (_maskLookup == null || string.IsNullOrWhiteSpace(maskName))
            {
                return 0f;
            }

            IReadOnlyList<Sprite> frames = _maskLookup.GetFrames(maskName);
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

            int localY = py - bandTop;
            int sampleY = Mathf.Clamp(Mathf.FloorToInt((localY + 0.5f) / FLOOR_BAND_PX * sprite.rect.height), 0, Mathf.Max(0, Mathf.FloorToInt(sprite.rect.height) - 1));
            return SampleSpriteAlpha(sprite, px, sampleY, sprite.rect.width, sprite.rect.height);
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
            return SampleSpriteAlpha(sprite, px, py, sprite != null ? sprite.rect : new Rect(0f, 0f, sampleWidth, sampleHeight), sampleWidth, sampleHeight);
        }

        private float SampleSpriteAlpha(Sprite sprite, int px, int py, Rect sampleRect, float sampleWidth, float sampleHeight)
        {
            if (sprite == null)
            {
                return 1f;
            }

            Texture2D readableTexture = GetReadableTexture(sprite.texture);
            if (readableTexture == null)
            {
                return 1f;
            }

            float width = Mathf.Max(1f, sampleWidth);
            float height = Mathf.Max(1f, sampleHeight);
            int sampleX = Mathf.Clamp(Mathf.FloorToInt(sampleRect.x + ((px + 0.5f) / width) * sampleRect.width), Mathf.FloorToInt(sampleRect.x), Mathf.FloorToInt(sampleRect.xMax) - 1);
            float flippedPy = (height - 1f) - py;
            int sampleY = Mathf.Clamp(Mathf.FloorToInt(sampleRect.y + ((flippedPy + 0.5f) / height) * sampleRect.height), Mathf.FloorToInt(sampleRect.y), Mathf.FloorToInt(sampleRect.yMax) - 1);
            return readableTexture.GetPixel(sampleX, sampleY).a;
        }

        private bool ShouldCropSingleFrameMask(Sprite sprite)
        {
            if (sprite == null)
            {
                return false;
            }

            Rect rect = sprite.rect;
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

            for (int y = Mathf.FloorToInt(spriteRect.y); y < Mathf.FloorToInt(spriteRect.yMax); y++)
            {
                for (int x = Mathf.FloorToInt(spriteRect.x); x < Mathf.FloorToInt(spriteRect.xMax); x++)
                {
                    if (readableTexture.GetPixel(x, y).a <= AlphaEpsilon)
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
