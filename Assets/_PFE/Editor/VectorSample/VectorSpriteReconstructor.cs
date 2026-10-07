#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using PFE.Editor.Art.OnDemand;
using PFE.Editor.Importers;
using Unity.VectorGraphics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace PFE.Editor.VectorSample
{
    /// <summary>
    /// How a shape SVG's coordinates are mapped back into the SWF shape's own coordinate space
    /// before its placement matrix is applied.
    ///
    /// <para><b>Why this is a switch and not a constant.</b> FFDec writes each shape SVG with its
    /// content re-origined into a padded box — the root <c>&lt;g&gt;</c> carries a
    /// <c>translate(-boundsMin/20 + margin/2)</c>. Whether Unity's <c>TessellateScene</c> bakes that
    /// <c>&lt;g&gt;</c> into the tessellated vertices determines whether we must undo it. Reading the
    /// API surface does not settle it (the module ships no documentation for
    /// <c>TessellateScene</c>), and the answer is visible the moment a two-shape sprite is drawn — so
    /// it is a switch, defaulted to the reading that is exact by construction, with the comparison
    /// panel as the arbiter.</para>
    /// </summary>
    public enum ShapeOriginMode
    {
        /// <summary>
        /// Undo FFDec's re-origin using the shape's SWF bounds from the capture:
        /// <c>v += (Xmin, Ymin) / 20</c>. Exact when the SVG is unpadded (2357 of 2563 shapes),
        /// and off by at most <c>margin/2</c> otherwise.
        /// </summary>
        UndoFfdecOrigin = 0,

        /// <summary>
        /// Use the tessellated geometry as-is. This is correct if <c>TessellateScene</c> does
        /// <b>not</b> bake the root <c>&lt;g&gt;</c>, i.e. if the vertices already come back in the
        /// shape's own space.
        /// </summary>
        RawSvgSpace = 1,

        /// <summary>
        /// Translate each shape so its own geometry bounding box lands on its SWF bounds box.
        /// Equals <see cref="UndoFfdecOrigin"/> when the SVG is unpadded; differs only in that it
        /// measures the geometry instead of trusting the capture's bounds.
        /// </summary>
        AlignGeometryBounds = 2,
    }

    /// <summary>A shape that was deliberately not tessellated, and why.</summary>
    public sealed class SkippedShape
    {
        public int Id;
        public long Bytes;
        public string Reason;
    }

    /// <summary>What one reconstruction produced, including everything needed to judge it.</summary>
    public sealed class SpriteBuildResult
    {
        public Mesh Mesh;

        /// <summary>Note the qualification: <c>TextureAtlas</c> is nested in <c>VectorUtils</c>, not a
        /// top-level type in <c>Unity.VectorGraphics</c> — as is <c>Geometry</c>.</summary>
        public VectorUtils.TextureAtlas Atlas;

        /// <summary>Set when an atlas was requested but skipped because every fill is a solid colour.</summary>
        public bool AtlasSkippedAsUnnecessary;

        public int SpriteId;
        public int FrameIndex;
        public int ResolvedShapeCount;

        /// <summary>
        /// Placed ids that produced no geometry. Two sources: a <see cref="PlacementKind.Other"/> leaf
        /// (text/button/morph — no SVG exists in the capture, so it can never be drawn), and a shape
        /// that tessellated to nothing without a recorded error. Populated during the display-list
        /// walk, so a frame that is *partly* text reports it instead of looking merely wrong.
        /// </summary>
        public readonly List<int> SkippedIds = new List<int>();

        /// <summary>Shapes whose SVG file was missing or unreadable.</summary>
        public readonly List<int> MissingSvgIds = new List<int>();

        /// <summary>
        /// Shapes withheld by the size guard. Kept separate from <see cref="MissingSvgIds"/>: these are
        /// present and valid, just far too expensive to tessellate at preview settings, and the remedy
        /// is a toggle rather than a missing file.
        /// </summary>
        public readonly List<SkippedShape> SkippedHeavy = new List<SkippedShape>();

        /// <summary>
        /// Shapes whose geometry came from the raster fast-path rather than tessellation — i.e. the
        /// drawn result is an <b>approximation</b> (one clamped quad) and not the tessellated vector.
        /// Kept so the window can say which ids those were instead of presenting them as faithful.
        /// </summary>
        public readonly List<int> RasterApproximatedIds = new List<int>();

        public int VertexCount;
        public int TriangleCount;
        public double TessellateMs;
        public double BuildMs;
        public string Error;

        /// <summary>Bounds of the composed mesh, in SVG px (before pixels-per-unit).</summary>
        public Bounds MeshBounds;

        /// <summary>Raw SWF bounds of the whole sprite frame, unioned from the placed shapes, in px.</summary>
        public bool HasSwfBounds;
        public Vector2 SwfMin, SwfMax;
    }

    /// <summary>
    /// Rebuilds a sprite from the one-shot capture plus the FFDec shape SVGs.
    ///
    /// <para><b>The pipeline.</b> For a sprite and a frame: resolve the display list to a flat list of
    /// (shape, composed SWF matrix) — that is <see cref="VectorCaptureData.ResolveFrame"/>. Tessellate
    /// each distinct shape SVG once and cache it. Then, per placed shape, map its vertices from SVG
    /// space into the shape's SWF-local space, apply the composed matrix (translating in twips), and
    /// hand the whole set to <c>VectorUtils.FillMesh</c> as one mesh.</para>
    ///
    /// <para><b>Units.</b> SWF stores translation in twips (20 per pixel). The capture stores the raw
    /// twip values; this class divides by 20 to get px. Everything downstream is in SVG px, and
    /// <c>FillMesh</c> applies the pixels-per-unit scale — the same 100 px/unit
    /// <c>CharacterSpriteImporter</c> uses, so a preview here is at the same scale as the imported
    /// PNGs.</para>
    /// </summary>
    public sealed class VectorSpriteReconstructor
    {
        /// <summary>Twips per pixel in SWF. Also carried in the capture manifest.</summary>
        public const float TwipsPerPixel = 20f;

        /// <summary>Matches <c>CharacterSpriteImporter.PixelsPerUnit</c>.</summary>
        public const float SvgPixelsPerUnit = 100f;

        readonly VectorCaptureData _data;
        readonly string _shapesRoot;

        /// <summary>
        /// Tessellated geometry per shape id, in the SVG's own space (untransformed). The cache is the
        /// whole reason a multi-shape sprite is affordable: a body is 26 parts, and re-tessellating
        /// them per frame would dominate.
        /// </summary>
        readonly Dictionary<int, List<VectorUtils.Geometry>> _geomCache =
            new Dictionary<int, List<VectorUtils.Geometry>>();

        /// <summary>
        /// Geometry bounding-box minimum per cached shape, in SVG user space (i.e. with the shape's
        /// own <c>WorldTransform</c> applied). Computed once, with the cache entry.
        /// </summary>
        readonly Dictionary<int, Vector2> _geomMin = new Dictionary<int, Vector2>();

        readonly Dictionary<int, string> _shapeError = new Dictionary<int, string>();

        /// <summary>Shape ids withheld by the size guard, with their byte counts.</summary>
        readonly Dictionary<int, long> _heavySkipped = new Dictionary<int, long>();

        /// <summary>Shape ids drawn from a raster fast-path quad instead of a tessellation.</summary>
        readonly HashSet<int> _rasterShapes = new HashSet<int>();

        RasterFastPathProvider _raster;

        // ── Raster fast-path ─────────────────────────────────────────────────
        //
        // A bitmap-pattern shape is a <path> filled with a repeating tile, and tessellating that at
        // preview tolerance is what produced the historical freezes. Measured against the real export
        // (2026-10-07): ALL TWELVE shapes over the 128 KB guard are bitmap-pattern, and so are all
        // eight of the named freeze offenders. Two of those (4652, 495, 1131) are only ~1.5 KB, so the
        // BYTE guard cannot see them at all — they sail past it and freeze the editor.
        //
        // The raster provider answers "is this shape a bitmap-pattern fill, and where is the bitmap?"
        // and hands back a 4-vertex textured quad. It is OFF by default and must stay off by default:
        // it draws ONE clamped quad, which is NOT the same picture as a repeated tile, so it belongs
        // behind an explicit toggle in a window whose entire purpose is judging fidelity.

        /// <summary>
        /// Draw bitmap-pattern shapes from their source bitmap as a single quad instead of tessellating
        /// the repeating pattern. Fast (sub-millisecond) but an <b>approximation</b>: see the note above.
        /// </summary>
        public bool UseRasterFastPath;

        /// <summary>
        /// Where the raw bitmaps live (<c>_assets/images/</c>). Null falls back to
        /// <see cref="SourceImportPaths.RawImagesRoot"/> when the fast-path is first used.
        /// </summary>
        public string ImagesRoot;

        /// <summary>How many distinct shapes have been drawn via the raster fast-path so far.</summary>
        public int RasterShapeCount => _rasterShapes.Count;

        /// <summary>True when this shape's cached geometry is a raster quad, not a tessellation.</summary>
        public bool IsRasterApproximated(int shapeId) => _rasterShapes.Contains(shapeId);

        /// <summary>
        /// Cheap probe — a stat plus a cached SVG sniff, no tessellation — for the window to warn
        /// *before* a build that a shape will be slow and that the byte guard cannot save it.
        /// </summary>
        public bool IsBitmapPatternShape(int shapeId)
            => Raster != null && Raster.TryGetRasterMeta(shapeId, out var meta) && meta.IsRaster;

        RasterFastPathProvider Raster
            => _raster ?? (_raster = new RasterFastPathProvider(_shapesRoot, ImagesRoot));

        // ── Cost controls ────────────────────────────────────────────────────
        //
        // These exist because of a real freeze, not for tidiness. The SVG export is not uniformly
        // cheap: 12 of the 2563 shapes are 128 KB - 1.9 MB and hold a SINGLE path with ~150 000-180 000
        // commands on a 1920x1080 canvas (visMainMenu, visualWait and friends). Tessellating one of
        // those at the 0.5 px chord tolerance that suits a 135 px limb is pathological, and because
        // BuildFrame tessellates every shape in the display list, selecting such a sprite froze the
        // editor with no progress bar and no way out.

        /// <summary>
        /// Skip any shape whose SVG exceeds this. 128 KB keeps the whole 2563-shape set except 12
        /// shapes (0.5 %), which are exactly the full-screen single-path monsters. Set
        /// <see cref="AllowHeavyShapes"/> to include them anyway.
        /// </summary>
        public long MaxShapeSvgBytes = 128 * 1024;

        /// <summary>Opt back in to the expensive shapes. Expect a multi-second, possibly minute-long hitch.</summary>
        public bool AllowHeavyShapes;

        /// <summary>
        /// Scale the tessellation tolerance with the shape's size. A fixed 0.5 px chord deviation means
        /// a 1920 px shape is flattened ~14x finer than a 135 px one, for no visible gain at preview
        /// scale. Scaling keeps the vertex count roughly constant instead of quadratic in size.
        /// </summary>
        public bool AdaptiveTolerance = true;

        /// <summary>The size at which the sliders are taken literally; larger shapes scale the tolerance.</summary>
        public float ToleranceReferenceSizePx = 256f;

        /// <summary>
        /// Only build an atlas when some fill actually needs one. The atlas rasterises every fill into a
        /// texture, which is the single most expensive step after tessellation — and it is pure waste
        /// when every fill is a solid colour, which is the common case here (the shapes are white
        /// silhouettes tinted by vertex colour or by the atlas, and solid fills need neither).
        /// </summary>
        public bool AtlasOnlyWhenNeeded = true;

        public VectorUtils.TessellationOptions Tessellation = new VectorUtils.TessellationOptions
        {
            MaxCordDeviation = 0.5f,
            MaxTanAngleDeviation = 0.1f,
            StepDistance = 0.5f,
            SamplingStepSize = 0.2f,
        };

        public bool BuildAtlas = true;
        public uint AtlasRasterSize = 1024;
        public bool FlipY = true;
        public bool PreserveViewport = true;
        public ShapeOriginMode OriginMode = ShapeOriginMode.UndoFfdecOrigin;

        public VectorSpriteReconstructor(VectorCaptureData data, string shapesRoot)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _shapesRoot = shapesRoot;
        }

        public int CachedShapeCount => _geomCache.Count;
        public int CacheErrorCount => _shapeError.Count;

        /// <summary>How many shapes the size guard has withheld so far.</summary>
        public int HeavySkippedCount => _heavySkipped.Count;

        public void ClearCache()
        {
            _geomCache.Clear();
            _geomMin.Clear();
            _shapeError.Clear();
            _heavySkipped.Clear();
            _rasterShapes.Clear();
        }

        public string ShapeError(int shapeId)
            => _shapeError.TryGetValue(shapeId, out var e) ? e : null;

        /// <summary>
        /// Size of a shape's SVG on disk, or 0 when it cannot be read. Cheap — a stat, not a parse —
        /// so the window can total the cost of a build before starting it.
        /// </summary>
        public long GetShapeSvgBytes(int shapeId)
        {
            if (string.IsNullOrEmpty(_shapesRoot)) return 0;
            if (!_data.Shapes.TryGetValue(shapeId, out var shape)) return 0;
            try
            {
                var fi = new FileInfo(Path.Combine(_shapesRoot, shape.File));
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// The distinct shapes one frame draws, in display order. Cheap: it walks the display list but
        /// tessellates nothing, so the window can use it to size and step a build.
        /// </summary>
        public List<int> DistinctShapeIds(int spriteId, int frameIndex)
        {
            var ids = new List<int>();
            if (!_data.Sprites.TryGetValue(spriteId, out var sprite)) return ids;
            if (sprite.Frames == null || sprite.Frames.Length == 0) return ids;

            int index = Mathf.Clamp(frameIndex, 0, sprite.Frames.Length - 1);
            var seen = new HashSet<int>();
            foreach (var rs in _data.ResolveFrame(sprite.Frames[index], index))
                if (seen.Add(rs.Shape.Id)) ids.Add(rs.Shape.Id);
            return ids;
        }

        /// <summary>
        /// Tessellate one shape into the cache, if it is not already there. Exposed so a caller can do
        /// the expensive part across several editor frames instead of in one blocking call.
        /// </summary>
        public void PreloadShape(int shapeId) => GetShapeGeometry(shapeId);

        // ── Shape tessellation ───────────────────────────────────────────────

        /// <summary>
        /// The tessellation options actually used for a shape of this size. With
        /// <see cref="AdaptiveTolerance"/> the absolute tolerances scale with the shape, so a 1920 px
        /// background is not flattened to the same 0.5 px chord deviation as a 135 px limb.
        /// </summary>
        VectorUtils.TessellationOptions EffectiveTessellation(float maxDimensionPx)
        {
            if (!AdaptiveTolerance) return Tessellation;

            float reference = Mathf.Max(1f, ToleranceReferenceSizePx);
            float scale = Mathf.Max(1f, maxDimensionPx / reference);

            return new VectorUtils.TessellationOptions
            {
                MaxCordDeviation = Tessellation.MaxCordDeviation * scale,
                // An angle is already scale-free; leave it.
                MaxTanAngleDeviation = Tessellation.MaxTanAngleDeviation,
                StepDistance = Tessellation.StepDistance * scale,
                SamplingStepSize = Tessellation.SamplingStepSize * scale,
            };
        }

        /// <summary>
        /// Tessellates one shape SVG and caches the result. Returns an empty list (never null) when the
        /// shape cannot be built; the reason lands in <see cref="ShapeError"/> or
        /// <see cref="SkippedShape"/>.
        /// </summary>
        List<VectorUtils.Geometry> GetShapeGeometry(int shapeId)
        {
            if (_geomCache.TryGetValue(shapeId, out var cached)) return cached;

            var result = new List<VectorUtils.Geometry>();
            _geomCache[shapeId] = result;
            _geomMin[shapeId] = Vector2.zero;

            if (!_data.Shapes.TryGetValue(shapeId, out var shape))
            {
                _shapeError[shapeId] = "shape " + shapeId + " is not in the capture";
                return result;
            }

            if (string.IsNullOrEmpty(_shapesRoot))
            {
                _shapeError[shapeId] = "no shapes root resolved";
                return result;
            }

            string path = Path.Combine(_shapesRoot, shape.File);
            if (!File.Exists(path))
            {
                _shapeError[shapeId] = "SVG not on disk: " + shape.File;
                return result;
            }

            // Raster fast-path, BEFORE the size guard. Checked first because the guard cannot see the
            // worst offenders: 4652/495/1131 are ~1.5 KB SVGs that tessellate for 12-22 minutes. If the
            // bitmap cannot be resolved we fall through to the faithful path rather than return an
            // empty result — a fast path that silently draws nothing would be worse than a slow one.
            if (UseRasterFastPath && Raster != null
                && Raster.TryGetRasterMeta(shapeId, out var rasterMeta) && rasterMeta.IsRaster)
            {
                var quad = Raster.CreateQuadGeometry(shapeId);
                if (quad != null && quad.Count > 0)
                {
                    result.AddRange(quad);
                    _geomMin[shapeId] = ComputeMin(quad);
                    _rasterShapes.Add(shapeId);
                    return result;
                }
            }

            // Size guard. Checked BEFORE reading the file, because the cost here is dominated by path
            // complexity and a 1.9 MB SVG is ~180 000 path commands.
            long bytes = 0;
            try { bytes = new FileInfo(path).Length; } catch { /* fall through to the parse attempt */ }

            if (!AllowHeavyShapes && bytes > MaxShapeSvgBytes)
            {
                _heavySkipped[shapeId] = bytes;
                _shapeError[shapeId] = string.Format(CultureInfo.InvariantCulture,
                    "skipped: {0:0.#} KB SVG is over the {1:0.#} KB guard",
                    bytes / 1024.0, MaxShapeSvgBytes / 1024.0);
                return result;
            }

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                _shapeError[shapeId] = "read failed: " + ex.GetType().Name + ": " + ex.Message;
                return result;
            }

            try
            {
                float w, h;
                if (!VectorShapeCatalog.TryReadRootSize(text, out w, out h)) { w = 256f; h = 256f; }

                SVGParser.SceneInfo info;
                using (var reader = new StringReader(text))
                {
                    info = SVGParser.ImportSVG(
                        reader,
                        PreserveViewport ? ViewportOptions.PreserveViewport : ViewportOptions.DontPreserve,
                        1f,
                        SvgPixelsPerUnit,
                        Mathf.Max(1, Mathf.CeilToInt(w)),
                        Mathf.Max(1, Mathf.CeilToInt(h)));
                }

                if (info.Scene == null || info.Scene.Root == null)
                {
                    _shapeError[shapeId] = "SVGParser produced an empty scene";
                    return result;
                }

                var options = EffectiveTessellation(Mathf.Max(w, h));
                var geoms = VectorUtils.TessellateScene(info.Scene, options, info.NodeOpacity);
                if (geoms == null || geoms.Count == 0)
                {
                    _shapeError[shapeId] = "tessellation produced no geometry";
                    return result;
                }

                result.AddRange(geoms);
                _geomMin[shapeId] = ComputeMin(geoms);
            }
            catch (Exception ex)
            {
                _shapeError[shapeId] = ex.GetType().Name + ": " + ex.Message;
                result.Clear();
            }

            return result;
        }

        /// <summary>
        /// True when any geometry's fill needs a texture — a gradient, a bitmap or a pattern. A solid
        /// fill does not: its colour is already carried in the vertex channel.
        /// </summary>
        static bool AnyNonSolidFill(List<VectorUtils.Geometry> geoms)
        {
            foreach (var g in geoms)
                if (g.Fill != null && !(g.Fill is SolidFill)) return true;
            return false;
        }

        /// <summary>
        /// Minimum corner of the shape's geometry in <b>SVG user space</b> — i.e. after
        /// <c>WorldTransform</c>, which is where <see cref="ShapeOriginMode.AlignGeometryBounds"/>
        /// needs it, since that mode compares against the capture's SWF bounds.
        /// </summary>
        static Vector2 ComputeMin(List<VectorUtils.Geometry> geoms)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            foreach (var g in geoms)
            {
                if (g.Vertices == null) continue;
                var wt = g.WorldTransform;
                foreach (var v in g.Vertices)
                {
                    var p = wt * v;   // local -> SVG user space; vertices are NOT pre-transformed
                    if (p.x < min.x) min.x = p.x;
                    if (p.y < min.y) min.y = p.y;
                }
            }
            return min.x == float.MaxValue ? Vector2.zero : min;
        }

        // ── Frame build ──────────────────────────────────────────────────────

        /// <summary>
        /// Builds a mesh for one sprite frame. The returned <see cref="SpriteBuildResult.Mesh"/> is
        /// owned by the caller and must be destroyed; it is null when nothing could be built.
        /// </summary>
        public SpriteBuildResult BuildFrame(int spriteId, int frameIndex)
        {
            var result = new SpriteBuildResult { SpriteId = spriteId, FrameIndex = frameIndex };
            var total = Stopwatch.StartNew();

            if (!_data.Sprites.TryGetValue(spriteId, out var sprite))
            {
                result.Error = "sprite " + spriteId + " is not in the capture";
                return result;
            }

            if (sprite.Frames == null || sprite.Frames.Length == 0)
            {
                result.Error = "sprite " + spriteId + " has no frames";
                return result;
            }

            int index = Mathf.Clamp(frameIndex, 0, sprite.Frames.Length - 1);
            result.FrameIndex = index;
            var frame = sprite.Frames[index];

            var composed = new List<VectorUtils.Geometry>();
            var swfMin = new Vector2(float.MaxValue, float.MaxValue);
            var swfMax = new Vector2(float.MinValue, float.MinValue);
            bool anySwfBounds = false;

            var tessWatch = Stopwatch.StartNew();

            // result.SkippedIds doubles as the collector for geometry-less placements, so a frame that
            // is partly text/button/morph reports them here instead of silently drawing a subset.
            foreach (var rs in _data.ResolveFrame(frame, index, skippedNonShape: result.SkippedIds))
            {
                int shapeId = rs.Shape.Id;
                var geoms = GetShapeGeometry(shapeId);
                if (geoms.Count == 0)
                {
                    // Three distinct reasons, kept distinct: a heavy shape is present and valid (the
                    // remedy is a toggle), a missing SVG is not (the remedy is a re-export), and a
                    // non-shape placement is neither.
                    if (_heavySkipped.TryGetValue(shapeId, out long heavyBytes))
                        result.SkippedHeavy.Add(new SkippedShape
                        {
                            Id = shapeId,
                            Bytes = heavyBytes,
                            Reason = "over the " + (MaxShapeSvgBytes / 1024) + " KB size guard",
                        });
                    else if (_shapeError.ContainsKey(shapeId))
                        result.MissingSvgIds.Add(shapeId);
                    else
                        result.SkippedIds.Add(shapeId);
                    continue;
                }

                result.ResolvedShapeCount++;

                // Report approximation, do not hide it: the window's whole job is judging fidelity.
                if (_rasterShapes.Contains(shapeId) && !result.RasterApproximatedIds.Contains(shapeId))
                    result.RasterApproximatedIds.Add(shapeId);

                // Where this shape's SVG sits relative to its SWF-local origin.
                Vector2 svgToSwf = ComputeSvgToSwf(rs, shapeId, geoms);
                var m = rs.Matrix;

                // Accumulate the frame's SWF bounds so the window can say how big the sprite should be.
                if (rs.Shape.HasBounds)
                {
                    var a = SwfPoint(m, new Vector2(rs.Shape.X0 / TwipsPerPixel, rs.Shape.Y0 / TwipsPerPixel));
                    var b = SwfPoint(m, new Vector2(rs.Shape.X1 / TwipsPerPixel, rs.Shape.Y1 / TwipsPerPixel));
                    var lo = Vector2.Min(a, b);
                    var hi = Vector2.Max(a, b);
                    swfMin = Vector2.Min(swfMin, lo);
                    swfMax = Vector2.Max(swfMax, hi);
                    anySwfBounds = true;
                }

                foreach (var src in geoms)
                    composed.Add(TransformGeometry(src, svgToSwf, m));
            }

            tessWatch.Stop();
            result.TessellateMs = tessWatch.Elapsed.TotalMilliseconds;

            if (anySwfBounds)
            {
                result.HasSwfBounds = true;
                result.SwfMin = swfMin;
                result.SwfMax = swfMax;
            }

            if (composed.Count == 0)
            {
                result.Error = "nothing drawable in this frame ("
                             + result.SkippedIds.Count + " skipped, "
                             + result.MissingSvgIds.Count + " missing SVG, "
                             + result.SkippedHeavy.Count + " over the size guard)";
                return result;
            }

            try
            {
                // Atlas BEFORE the mesh: GenerateAtlasAndFillUVs writes UVs into the geometry and
                // FillMesh copies them across. Reversed, the mesh gets dead UVs. (Same ordering
                // constraint the single-shape sample window documents.)
                //
                // But only when a fill actually needs a texture. The atlas rasterises every fill, which
                // is the most expensive step after tessellation, and a scene of solid-colour fills
                // gains nothing from it — the colour is already in the vertex channel.
                bool needsAtlas = !AtlasOnlyWhenNeeded || AnyNonSolidFill(composed);

                if (BuildAtlas && needsAtlas)
                {
                    result.Atlas = VectorUtils.GenerateAtlasAndFillUVs(composed, AtlasRasterSize);
                }
                else if (BuildAtlas && !needsAtlas)
                {
                    result.AtlasSkippedAsUnnecessary = true;
                }

                var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                // A 26-part body at 1:1 can exceed the 65535 UInt16 index limit; FillMesh writes
                // UInt16 indices, so ask for the 32-bit path up front rather than lose triangles.
                mesh.indexFormat = IndexFormat.UInt32;
                VectorUtils.FillMesh(mesh, composed, SvgPixelsPerUnit, FlipY);
                mesh.RecalculateBounds();

                if (mesh.vertexCount == 0)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                    result.Error = "FillMesh produced an empty mesh";
                    return result;
                }

                result.Mesh = mesh;
                result.VertexCount = mesh.vertexCount;
                result.TriangleCount = mesh.triangles.Length / 3;
                result.MeshBounds = mesh.bounds;
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
            }

            total.Stop();
            result.BuildMs = total.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// Maps a vertex from the shape SVG's coordinate space into the shape's SWF-local space. See
        /// <see cref="ShapeOriginMode"/> for why this is a choice.
        /// </summary>
        Vector2 ComputeSvgToSwf(ResolvedShape rs, int shapeId, List<VectorUtils.Geometry> geoms)
        {
            var boundsMinPx = rs.Shape.HasBounds
                ? new Vector2(rs.Shape.X0 / TwipsPerPixel, rs.Shape.Y0 / TwipsPerPixel)
                : Vector2.zero;

            switch (OriginMode)
            {
                case ShapeOriginMode.RawSvgSpace:
                    return Vector2.zero;
                case ShapeOriginMode.AlignGeometryBounds:
                    return boundsMinPx - _geomMin[shapeId];
                default:
                    return boundsMinPx;
            }
        }

        static Vector2 SwfPoint(SwfMatrix m, Vector2 v)
            => new Vector2(m.A * v.x + m.B * v.y + m.Tx / TwipsPerPixel,
                           m.C * v.x + m.D * v.y + m.Ty / TwipsPerPixel);

        /// <summary>
        /// Produces a copy of one cached geometry placed into sprite-local space.
        ///
        /// <para><b>Why the transform goes into <c>WorldTransform</c> and not into <c>Vertices</c>.</b>
        /// Read off the shipped IL (2026-10-03): <c>FillVertexChannels</c> computes
        /// <c>WorldTransform * Vertices[i] / pixelsPerUnit</c> when it writes the mesh — so the
        /// vertices that <c>TessellateScene</c> returns are <b>local</b> to the shape's node, and the
        /// accumulated node transform (including FFDec's root <c>&lt;g&gt;</c>) lives in
        /// <c>WorldTransform</c> and is applied later. Moving vertices here <i>and</i> keeping
        /// <c>WorldTransform</c> would apply the SVG transform twice. So the placement is folded into
        /// <c>WorldTransform</c>, and vertices, colours, indices and UVs are shared with the cache.</para>
        /// </summary>
        static VectorUtils.Geometry TransformGeometry(VectorUtils.Geometry src, Vector2 svgToSwf, SwfMatrix m)
        {
            // Our matrix: apply svgToSwf first, then the placement. m's translation is in twips.
            var mine = new Matrix2D
            {
                m00 = m.A,
                m01 = m.B,
                m02 = m.A * svgToSwf.x + m.B * svgToSwf.y + m.Tx / TwipsPerPixel,
                m10 = m.C,
                m11 = m.D,
                m12 = m.C * svgToSwf.x + m.D * svgToSwf.y + m.Ty / TwipsPerPixel,
            };

            return new VectorUtils.Geometry
            {
                Fill = src.Fill,
                FillTransform = src.FillTransform,
                SettingIndex = src.SettingIndex,
                UnclippedBounds = src.UnclippedBounds,
                // Shared with the cache: none of these are position-dependent, and FillVertexChannels
                // reads them unchanged.
                Vertices = src.Vertices,
                Indices = src.Indices,
                Color = src.Color,
                UVs = src.UVs,
                // Source first (local -> SVG user space), then ours (SVG user space -> sprite-local).
                WorldTransform = mine * src.WorldTransform,
            };
        }
    }
}
#endif
