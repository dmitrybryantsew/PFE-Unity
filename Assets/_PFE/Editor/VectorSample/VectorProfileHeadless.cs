#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PFE.Editor.Importers;
using Unity.VectorGraphics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PFE.Editor.VectorSample
{
    /// <summary>
    /// Headless per-phase profiler for the vector import.
    ///
    /// <para><b>Why this exists.</b> Selecting a sprite in <c>PFE/Art/Vector Sprite Preview</c> froze the
    /// editor for 3 min 24 s, inside <c>VectorSpritePreviewWindow.MouseUp</c>. The cause has never been
    /// measured. An earlier note blamed tessellation of "~180 000-command paths" — that was a file size
    /// misattributed to a path; the real maximum in the corpus is 1380 commands and 64.5 % of the export
    /// bytes are embedded raster. So the phases are timed here instead of reasoned about.</para>
    ///
    /// <para><b>Run it with</b> (no editor GUI, no player build):</para>
    /// <code>
    /// Unity.exe -batchmode -quit -nographics -projectPath &lt;project&gt; -logFile &lt;log&gt; \
    ///           -executeMethod PFE.Editor.VectorSample.VectorProfileHeadless.Run
    /// </code>
    ///
    /// <para><b>Phases.</b> A: per shape, read / ImportSVG / TessellateScene (as shipped, and adaptive).
    /// A2: the atlas for the shapes that need one, heaviest first. B: <c>BuildFrame</c> end-to-end for a
    /// sample of sprites, cache cleared before each so nothing is masked by caching.</para>
    ///
    /// <para><b>These constants and calls are copied from <see cref="VectorSpriteReconstructor"/> on
    /// purpose.</b> If they drift, the numbers stop describing the shipped pipeline.</para>
    /// </summary>
    public static class VectorProfileHeadless
    {
        static string CapturePath => Path.Combine(Application.dataPath, "_PFE/Art/Vector/VectorCapture.json");
        static string OutCsv => Path.Combine(Application.dataPath, "../Logs/vector_profile.csv");
        static string OutTxt => Path.Combine(Application.dataPath, "../Logs/vector_profile_summary.txt");

        // ── must match VectorSpriteReconstructor ─────────────────────────────
        const float SvgPixelsPerUnit = 100f;
        const uint AtlasRasterSize = 1024;
        const float ToleranceReferenceSizePx = 256f;
        const float MaxCordDeviation = 0.5f;
        const float MaxTanAngleDeviation = 0.1f;
        const float StepDistance = 0.5f;
        const float SamplingStepSize = 0.2f;

        /// <summary>How many shapes get an atlas timing. The atlas is per frame in the real pipeline,
        /// so per-shape atlas numbers are an upper-bound probe, not the shipped call.</summary>
        const int AtlasSampleCount = 60;

        /// <summary>End-to-end sprite builds are capped by both count and wall time, because the
        /// freeze being investigated is minutes long.</summary>
        const int SpriteSampleCount = 25;
        const double SpriteBudgetMs = 120000;

        static void OutputLine(string line, StringBuilder summary)
        {
            summary.AppendLine(line);
            Debug.Log("[VECTORPROFILE] " + line);
        }

        sealed class ShapeRow
        {
            public int Id;
            public string File;
            public long Bytes;
            public string Kind;
            public float W, H;
            public double ReadMs, ParseMs, TessMs, TessAdaptiveMs, AtlasMs;
            public int Geoms, AtlasW, AtlasH;
            public long Verts, Tris;
            public bool NeedsAtlas;
            public string Error;
        }

        sealed class SpriteRow
        {
            public int Id, Frame, ShapeCount;
            public long SvgBytes;
            public double BuildMs, TessMs;
            public int MeshVerts, MeshTris;
            public bool AtlasSkipped;
            public bool NoAtlas;
            public int AtlasW, AtlasH;
            public string Error;
        }

        public static void Run()
        {
            var summary = new StringBuilder();
            var shapeRows = new List<ShapeRow>();
            var spriteRows = new List<SpriteRow>();
            string fatal = null;

            try
            {
                OutputLine("=== vector profile (headless) ===", summary);
                OutputLine("unity      : " + Application.unityVersion, summary);
                OutputLine("time       : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), summary);
                OutputLine("batchmode  : " + Application.isBatchMode, summary);
                OutputLine("", summary);

                string capPath = CapturePath;
                if (!File.Exists(capPath))
                {
                    fatal = "capture not found: " + capPath;
                    OutputLine("FATAL: " + fatal, summary);
                }
                else
                {
                    var sw = Stopwatch.StartNew();
                    VectorCaptureData data = VectorCaptureReader.Parse(File.ReadAllText(capPath));
                    OutputLine($"capture parsed in {sw.Elapsed.TotalMilliseconds:0.0} ms  " +
                               $"({data.Sprites.Count} sprites, {data.Shapes.Count} shapes)", summary);

                    string shapesRoot = ResolveShapesRoot(data);
                    OutputLine("shapes root: " + (string.IsNullOrEmpty(shapesRoot) ? "(unresolved)" : shapesRoot), summary);
                    OutputLine("", summary);

                    if (string.IsNullOrEmpty(shapesRoot) || !Directory.Exists(shapesRoot))
                    {
                        fatal = "shapes root unresolved or missing: " + shapesRoot;
                        OutputLine("FATAL: " + fatal, summary);
                    }
                    else
                    {
                        try
                        {
                            ProfileShapes(shapesRoot, shapeRows, summary);
                        }
                        catch (Exception ex)
                        {
                            OutputLine("ERROR in ProfileShapes: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace, summary);
                        }
                        TryWrite(OutCsv, BuildShapeCsv(shapeRows, spriteRows));
                        TryWrite(OutTxt, summary.ToString());

                        try
                        {
                            ProfileAtlas(shapesRoot, shapeRows, summary);
                        }
                        catch (Exception ex)
                        {
                            OutputLine("ERROR in ProfileAtlas: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace, summary);
                        }
                        TryWrite(OutCsv, BuildShapeCsv(shapeRows, spriteRows));
                        TryWrite(OutTxt, summary.ToString());

                        try
                        {
                            ProfileSprites(data, shapesRoot, spriteRows, summary);
                        }
                        catch (Exception ex)
                        {
                            OutputLine("ERROR in ProfileSprites: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace, summary);
                        }
                        TryWrite(OutCsv, BuildShapeCsv(shapeRows, spriteRows));
                        TryWrite(OutTxt, summary.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                fatal = ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace;
                OutputLine("FATAL: " + fatal, summary);
            }

            TryWrite(OutCsv, BuildShapeCsv(shapeRows, spriteRows));
            TryWrite(OutTxt, summary.ToString());
            OutputLine("=== vector profile complete ===", summary);
        }

        // ── Phase A ─────────────────────────────────────────────────────────

        static void ProfileShapes(string shapesRoot, List<ShapeRow> rows, StringBuilder summary)
        {
            List<VectorShapeEntry> catalog = VectorShapeCatalog.Scan(shapesRoot, out string scanError);
            if (!string.IsNullOrEmpty(scanError))
                OutputLine("catalog scan warning: " + scanError, summary);

            OutputLine("--- phase A: per shape (" + catalog.Count + ") ---", summary);

            var sw = Stopwatch.StartNew();
            int doneCount = 0;
            foreach (VectorShapeEntry entry in catalog)
            {
                doneCount++;
                var row = new ShapeRow
                {
                    Id = entry.ShapeId,
                    File = entry.FileName,
                    Bytes = entry.Bytes,
                    Kind = entry.KindLabel,
                    W = entry.RootWidth,
                    H = entry.RootHeight,
                };

                try
                {
                    string text = File.ReadAllText(entry.FullPath);
                    row.ReadMs = 0; // folded into the parse timing below; read alone is not the question

                    float w = entry.RootWidth > 0f ? entry.RootWidth : 256f;
                    float h = entry.RootHeight > 0f ? entry.RootHeight : 256f;

                    SVGParser.SceneInfo info;
                    var t = Stopwatch.StartNew();
                    using (var reader = new StringReader(text))
                    {
                        info = SVGParser.ImportSVG(
                            reader,
                            ViewportOptions.PreserveViewport,
                            1f,
                            SvgPixelsPerUnit,
                            Mathf.Max(1, Mathf.CeilToInt(w)),
                            Mathf.Max(1, Mathf.CeilToInt(h)));
                    }
                    row.ParseMs = t.Elapsed.TotalMilliseconds;

                    if (info.Scene == null || info.Scene.Root == null)
                    {
                        row.Error = "empty scene";
                        rows.Add(row);
                        continue;
                    }

                    t.Restart();
                    List<VectorUtils.Geometry> geoms =
                        VectorUtils.TessellateScene(info.Scene, Options(false, Mathf.Max(w, h)), info.NodeOpacity);
                    row.TessMs = t.Elapsed.TotalMilliseconds;

                    row.Geoms = geoms?.Count ?? 0;
                    row.Verts = CountVerts(geoms);
                    row.Tris = CountTris(geoms);
                    row.NeedsAtlas = AnyNonSolidFill(geoms);

                    t.Restart();
                    VectorUtils.TessellateScene(info.Scene, Options(true, Mathf.Max(w, h)), info.NodeOpacity);
                    row.TessAdaptiveMs = t.Elapsed.TotalMilliseconds;
                }
                catch (Exception ex)
                {
                    row.Error = ex.GetType().Name + ": " + FirstLine(ex.Message);
                }

                rows.Add(row);

                if (doneCount % 250 == 0 || doneCount == catalog.Count || row.ParseMs > 200 || row.TessMs > 200)
                {
                    OutputLine($"  [{doneCount}/{catalog.Count}] shape {row.Id}: parse {row.ParseMs:0.0}ms, tess {row.TessMs:0.0}ms (adaptive {row.TessAdaptiveMs:0.0}ms), verts {row.Verts}, needsAtlas={row.NeedsAtlas}", summary);
                }
            }

            var ok = rows.Where(r => r.Error == null).ToList();
            OutputLine($"  total wall       : {sw.Elapsed.TotalSeconds:0.0} s", summary);
            OutputLine($"  shapes ok        : {ok.Count} / {rows.Count}", summary);
            OutputLine($"  ImportSVG  total : {ok.Sum(r => r.ParseMs):0} ms   (worst {Max(ok, r => r.ParseMs):0} ms)", summary);
            OutputLine($"  Tessellate total : {ok.Sum(r => r.TessMs):0} ms   (worst {Max(ok, r => r.TessMs):0} ms)   [as shipped]", summary);
            OutputLine($"  Tessellate total : {ok.Sum(r => r.TessAdaptiveMs):0} ms   (worst {Max(ok, r => r.TessAdaptiveMs):0} ms)   [adaptive]", summary);
            OutputLine($"  vertices total   : {ok.Sum(r => r.Verts):N0}", summary);
            OutputLine($"  triangles total  : {ok.Sum(r => r.Tris):N0}", summary);
            OutputLine($"  shapes needing an atlas: {ok.Count(r => r.NeedsAtlas)}", summary);

            OutputLine("", summary);
            OutputLine("  top 10 by TessellateScene:", summary);
            foreach (var r in ok.OrderByDescending(r => r.TessMs).Take(10))
                OutputLine($"    {r.TessMs,9:0.##} ms  {r.Verts,9:N0} verts  {r.Bytes,8} B  {r.Kind,-14} {r.File}", summary);

            OutputLine("", summary);
            OutputLine("  top 10 by ImportSVG:", summary);
            foreach (var r in ok.OrderByDescending(r => r.ParseMs).Take(10))
                OutputLine($"    {r.ParseMs,9:0.##} ms  {r.Bytes,8} B  {r.Kind,-14} {r.File}", summary);

            var failed = rows.Where(r => r.Error != null).ToList();
            if (failed.Count > 0)
            {
                OutputLine("", summary);
                OutputLine("  failures:", summary);
                foreach (var g in failed.GroupBy(r => r.Error).OrderByDescending(g => g.Count()).Take(6))
                    OutputLine($"    {g.Count(),5} x {g.Key}", summary);
            }
        }

        // ── Phase A2 ────────────────────────────────────────────────────────

        static void ProfileAtlas(string shapesRoot, List<ShapeRow> rows, StringBuilder summary)
        {
            OutputLine("", summary);
            OutputLine("--- phase A2: atlas for the heaviest shapes that need one ---", summary);

            List<ShapeRow> sample = rows
                .Where(r => r.Error == null && r.NeedsAtlas)
                .OrderByDescending(r => r.Bytes)
                .Take(AtlasSampleCount)
                .ToList();

            if (sample.Count == 0)
            {
                OutputLine("  (none needed an atlas)", summary);
                return;
            }

            double total = 0;
            int done = 0;
            foreach (ShapeRow row in sample)
            {
                try
                {
                    string path = Path.Combine(shapesRoot, row.File);
                    string text = File.ReadAllText(path);

                    SVGParser.SceneInfo info;
                    using (var reader = new StringReader(text))
                    {
                        info = SVGParser.ImportSVG(reader, ViewportOptions.PreserveViewport, 1f,
                            SvgPixelsPerUnit,
                            Mathf.Max(1, Mathf.CeilToInt(row.W > 0 ? row.W : 256f)),
                            Mathf.Max(1, Mathf.CeilToInt(row.H > 0 ? row.H : 256f)));
                    }
                    if (info.Scene == null || info.Scene.Root == null) continue;

                    List<VectorUtils.Geometry> geoms = VectorUtils.TessellateScene(
                        info.Scene, Options(false, Mathf.Max(row.W, row.H)), info.NodeOpacity);
                    if (geoms == null || geoms.Count == 0) continue;

                    var t = Stopwatch.StartNew();
                    VectorUtils.TextureAtlas atlas = VectorUtils.GenerateAtlasAndFillUVs(geoms, AtlasRasterSize);
                    row.AtlasMs = t.Elapsed.TotalMilliseconds;

                    if (atlas != null && atlas.Texture != null)
                    {
                        row.AtlasW = atlas.Texture.width;
                        row.AtlasH = atlas.Texture.height;
                    }
                    total += row.AtlasMs;
                    done++;
                    OutputLine($"  atlas [{done}/{sample.Count}] shape {row.Id}: {row.AtlasMs:0.0} ms ({row.AtlasW}x{row.AtlasH}), {row.Bytes} B, {row.Kind}", summary);
                }
                catch (Exception ex)
                {
                    row.AtlasMs = -1;
                    row.Error = "atlas: " + ex.GetType().Name + ": " + FirstLine(ex.Message);
                    OutputLine($"  atlas [{done + 1}/{sample.Count}] shape {row.Id} FAILED: {row.Error}", summary);
                }
            }

            OutputLine($"  sampled          : {done} of {sample.Count}", summary);
            OutputLine($"  atlas total      : {total:0} ms   (worst {Max(sample, r => r.AtlasMs):0} ms)", summary);
            OutputLine($"  mean per shape   : {(done > 0 ? total / done : 0):0} ms", summary);
            OutputLine("", summary);
            OutputLine("  top 10 by atlas time:", summary);
            foreach (var r in sample.OrderByDescending(r => r.AtlasMs).Take(10))
                OutputLine($"    {r.AtlasMs,9:0.##} ms  atlas {r.AtlasW}x{r.AtlasH}  {r.Bytes,8} B  {r.Kind,-14} {r.File}", summary);
        }

        // ── Phase B ─────────────────────────────────────────────────────────

        static void ProfileSprites(VectorCaptureData data, string shapesRoot,
                                   List<SpriteRow> rows, StringBuilder summary)
        {
            OutputLine("", summary);
            OutputLine("--- phase B: BuildFrame end-to-end ---", summary);

            var recon = new VectorSpriteReconstructor(data, shapesRoot);
            var ids = data.Sprites.Keys.OrderBy(i => i).ToList();

            // Sample evenly across the id range so a single dense cluster cannot dominate.
            var sample = new List<int>();
            if (ids.Count <= SpriteSampleCount) sample.AddRange(ids);
            else
            {
                double step = ids.Count / (double)SpriteSampleCount;
                for (int i = 0; i < SpriteSampleCount; i++) sample.Add(ids[(int)(i * step)]);
            }

            // A local function so the shipped pass and the no-atlas fallback measure identically.
            void Measure(int id, bool noAtlas)
            {
                var row = new SpriteRow { Id = id, NoAtlas = noAtlas };
                try
                {
                    VectorSpriteInfo info = data.Sprites[id];

                    // First drawable frame, matching the window's behaviour.
                    int frame = 0;
                    for (int f = 0; f < info.Frames.Length; f++)
                    {
                        if (recon.DistinctShapeIds(id, f).Count > 0) { frame = f; break; }
                    }
                    row.Frame = frame;

                    List<int> shapeIds = recon.DistinctShapeIds(id, frame);
                    row.ShapeCount = shapeIds.Count;
                    foreach (int sid in shapeIds) row.SvgBytes += recon.GetShapeSvgBytes(sid);

                    // Cache cleared so nothing is masked -- this is a cold build, which is what froze.
                    recon.ClearCache();

                    var t = Stopwatch.StartNew();
                    SpriteBuildResult result = recon.BuildFrame(id, frame);
                    row.BuildMs = t.Elapsed.TotalMilliseconds;

                    // BuildFrame reports failure by SETTING Error, not by throwing. A build that
                    // silently produced nothing must not be recorded as a fast success.
                    if (result == null)
                    {
                        row.Error = "BuildFrame returned null";
                    }
                    else
                    {
                        row.TessMs = result.TessellateMs;
                        row.AtlasSkipped = result.AtlasSkippedAsUnnecessary;
                        row.MeshVerts = result.VertexCount;
                        row.MeshTris = result.TriangleCount;
                        if (result.Mesh != null)
                        {
                            row.MeshVerts = result.Mesh.vertexCount;
                            row.MeshTris = result.Mesh.triangles.Length / 3;
                        }
                        if (result.Atlas != null && result.Atlas.Texture != null)
                        {
                            row.AtlasW = result.Atlas.Texture.width;
                            row.AtlasH = result.Atlas.Texture.height;
                        }
                        if (!string.IsNullOrEmpty(result.Error))
                            row.Error = "build: " + FirstLine(result.Error);
                    }
                }
                catch (Exception ex)
                {
                    row.Error = ex.GetType().Name + ": " + FirstLine(ex.Message);
                }
                rows.Add(row);

                OutputLine($"  sprite [{rows.Count}/{sample.Count}] sym{row.Id} f{row.Frame}: build {row.BuildMs:0.0} ms (tess {row.TessMs:0.0} ms, {row.ShapeCount} shapes, {row.MeshVerts} verts, atlas: {(row.AtlasW > 0 ? $"{row.AtlasW}x{row.AtlasH}" : row.AtlasSkipped ? "skipped" : "none")}{(string.IsNullOrEmpty(row.Error) ? "" : " ERR: " + row.Error)})", summary);
            }

            double cumulative = 0;
            foreach (int id in sample)
            {
                int before = rows.Count;
                Measure(id, false);
                cumulative += rows[before].BuildMs;

                if (cumulative > SpriteBudgetMs)
                {
                    OutputLine($"  stopped early: cumulative {cumulative:0} ms over the {SpriteBudgetMs:0} ms budget", summary);
                    break;
                }
            }

            var ok = rows.Where(r => r.Error == null).ToList();
            OutputLine($"  sprites measured : {rows.Count} of {ids.Count}   (failures {rows.Count - ok.Count})", summary);
            if (ok.Count > 0)
            {
                OutputLine($"  total            : {ok.Sum(r => r.BuildMs):0} ms", summary);
                OutputLine($"  worst single     : {ok.Max(r => r.BuildMs):0} ms   (sprite {ok.OrderByDescending(r => r.BuildMs).First().Id})", summary);
                OutputLine($"  mean             : {ok.Average(r => r.BuildMs):0} ms", summary);
                OutputLine($"  of which tessell.: {ok.Sum(r => r.TessMs):0} ms   " +
                           $"({(ok.Sum(r => r.BuildMs) > 0 ? 100.0 * ok.Sum(r => r.TessMs) / ok.Sum(r => r.BuildMs) : 0):0.0} % of the total)", summary);
                OutputLine($"  atlas skipped    : {ok.Count(r => r.AtlasSkipped)} of {ok.Count} sprites " +
                           $"(AtlasOnlyWhenNeeded: solid-only frames build no texture at all)", summary);
                OutputLine($"  atlas built      : {ok.Count(r => r.AtlasW > 0)} sprites", summary);
            }

            OutputLine("", summary);
            OutputLine("  top 10 slowest sprites:", summary);
            foreach (var r in ok.OrderByDescending(r => r.BuildMs).Take(10))
                OutputLine($"    {r.BuildMs,10:0.##} ms  sprite {r.Id,6} f{r.Frame,-3} " +
                           $"{r.ShapeCount,4} shapes  {r.SvgBytes / 1024.0,9:0.0} KB SVG  " +
                           $"{r.MeshVerts,8:N0} verts  " +
                           (r.AtlasW > 0 ? $"atlas {r.AtlasW}x{r.AtlasH}" : "no atlas"), summary);

            if (rows.Count > ok.Count)
            {
                OutputLine("", summary);
                OutputLine("  failures:", summary);
                foreach (var g in rows.Where(r => r.Error != null)
                                      .GroupBy(r => r.Error).OrderByDescending(g => g.Count()).Take(5))
                    OutputLine($"    {g.Count(),5} x {g.Key}", summary);
            }

            // If no graphics device is available (batchmode -nographics), GenerateAtlasAndFillUVs
            // can fail for every sprite. Retry the same sample with the atlas off: the
            // tessellate+mesh cost is then still measured, and the atlas cost falls out by
            // difference instead of being lost.
            if (ok.Count == 0 && rows.Count > 0)
            {
                OutputLine("", summary);
                OutputLine("  every shipped build failed -> retrying the sample with BuildAtlas = false", summary);
                recon.BuildAtlas = false;

                var fallback = new List<SpriteRow>();
                double fb = 0;
                foreach (int id in sample)
                {
                    int before = rows.Count;
                    Measure(id, true);
                    var fr = rows[before];
                    fallback.Add(fr);
                    fb += fr.BuildMs;
                    if (fb > SpriteBudgetMs) { OutputLine($"  fallback stopped early at {fb:0} ms", summary); break; }
                }

                var fok = fallback.Where(r => r.Error == null).ToList();
                OutputLine($"  fallback ok      : {fok.Count} of {fallback.Count}", summary);
                if (fok.Count > 0)
                {
                    OutputLine($"  fallback total   : {fok.Sum(r => r.BuildMs):0} ms   " +
                               $"(tessellate {fok.Sum(r => r.TessMs):0} ms, " +
                               $"mesh+rest {fok.Sum(r => r.BuildMs) - fok.Sum(r => r.TessMs):0} ms)", summary);
                    OutputLine($"  fallback worst   : {fok.Max(r => r.BuildMs):0} ms   " +
                               $"(sprite {fok.OrderByDescending(r => r.BuildMs).First().Id})", summary);
                    OutputLine("", summary);
                    OutputLine("  top 10 slowest sprites (no atlas):", summary);
                    foreach (var r in fok.OrderByDescending(r => r.BuildMs).Take(10))
                        OutputLine($"    {r.BuildMs,10:0.##} ms  sprite {r.Id,6} f{r.Frame,-3} " +
                                   $"{r.ShapeCount,4} shapes  {r.SvgBytes / 1024.0,9:0.0} KB SVG  " +
                                   $"{r.MeshVerts,8:N0} verts", summary);
                }
                else
                {
                    OutputLine("  fallback also failed:", summary);
                    foreach (var g in fallback.Where(r => r.Error != null)
                                              .GroupBy(r => r.Error).OrderByDescending(g => g.Count()).Take(3))
                        OutputLine($"    {g.Count(),5} x {g.Key}", summary);
                }
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────

        static string ResolveShapesRoot(VectorCaptureData data)
        {
            string project = SourceImportPaths.ShapesRoot;
            if (!string.IsNullOrEmpty(project) && Directory.Exists(project)) return project;
            string recorded = data?.Manifest?.ShapesDir;
            if (!string.IsNullOrEmpty(recorded) && Directory.Exists(recorded)) return recorded;
            return project ?? recorded ?? string.Empty;
        }

        static VectorUtils.TessellationOptions Options(bool adaptive, float maxDim)
        {
            float scale = 1f;
            if (adaptive)
            {
                float reference = Mathf.Max(1f, ToleranceReferenceSizePx);
                scale = Mathf.Max(1f, maxDim / reference);
            }
            return new VectorUtils.TessellationOptions
            {
                MaxCordDeviation = MaxCordDeviation * scale,
                MaxTanAngleDeviation = MaxTanAngleDeviation,
                StepDistance = StepDistance * scale,
                SamplingStepSize = SamplingStepSize * scale,
            };
        }

        static bool AnyNonSolidFill(List<VectorUtils.Geometry> geoms)
        {
            if (geoms == null) return false;
            foreach (var g in geoms)
                if (g != null && g.Fill != null && !(g.Fill is SolidFill)) return true;
            return false;
        }

        static long CountVerts(List<VectorUtils.Geometry> geoms)
        {
            if (geoms == null) return 0;
            long n = 0;
            foreach (var g in geoms) if (g?.Vertices != null) n += g.Vertices.Length;
            return n;
        }

        static long CountTris(List<VectorUtils.Geometry> geoms)
        {
            if (geoms == null) return 0;
            long n = 0;
            foreach (var g in geoms) if (g?.Indices != null) n += g.Indices.Length / 3;
            return n;
        }

        static double Max(List<ShapeRow> rows, Func<ShapeRow, double> f)
            => rows.Count == 0 ? 0 : rows.Max(f);

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            int nl = s.IndexOf('\n');
            return (nl < 0 ? s : s.Substring(0, nl)).Replace(',', ';');
        }

        static string BuildShapeCsv(List<ShapeRow> shapes, List<SpriteRow> sprites)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# shapes");
            sb.AppendLine("id,file,bytes,kind,w,h,parseMs,tessMs,tessAdaptiveMs,atlasMs,atlasW,atlasH,geoms,verts,tris,needsAtlas,error");
            foreach (var r in shapes)
                sb.Append(r.Id).Append(',').Append(Csv(r.File)).Append(',').Append(r.Bytes).Append(',')
                  .Append(Csv(r.Kind)).Append(',')
                  .Append(F(r.W)).Append(',').Append(F(r.H)).Append(',')
                  .Append(F(r.ParseMs)).Append(',').Append(F(r.TessMs)).Append(',')
                  .Append(F(r.TessAdaptiveMs)).Append(',').Append(F(r.AtlasMs)).Append(',')
                  .Append(r.AtlasW).Append(',').Append(r.AtlasH).Append(',')
                  .Append(r.Geoms).Append(',').Append(r.Verts).Append(',').Append(r.Tris).Append(',')
                  .Append(r.NeedsAtlas ? "1" : "0").Append(',').Append(Csv(r.Error)).AppendLine();

            sb.AppendLine("# sprites");
            sb.AppendLine("id,frame,shapeCount,svgBytes,noAtlas,buildMs,tessMs,atlasSkipped,atlasW,atlasH,meshVerts,meshTris,error");
            foreach (var r in sprites)
                sb.Append(r.Id).Append(',').Append(r.Frame).Append(',').Append(r.ShapeCount).Append(',')
                  .Append(r.SvgBytes).Append(',').Append(r.NoAtlas ? "1" : "0").Append(',')
                  .Append(F(r.BuildMs)).Append(',').Append(F(r.TessMs)).Append(',')
                  .Append(r.AtlasSkipped ? "1" : "0").Append(',')
                  .Append(r.AtlasW).Append(',').Append(r.AtlasH).Append(',')
                  .Append(r.MeshVerts).Append(',').Append(r.MeshTris).Append(',')
                  .Append(Csv(r.Error)).AppendLine();
            return sb.ToString();
        }

        static string Csv(string s) => s == null ? string.Empty : s.Replace(',', ';').Replace('\n', ' ');
        static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        static void TryWrite(string path, string content)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, content);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VECTORPROFILE] could not write " + path + ": " + ex.Message);
            }
        }
    }
}
#endif
