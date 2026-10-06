#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PFE.Editor.Importers;
using PFE.Editor.VectorSample;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PFE.Editor.Art.OnDemand
{
    /// <summary>
    /// Headless batchmode test suite for OnDemandArtProvider.
    /// Runs via: -executeMethod PFE.Editor.Art.OnDemand.OnDemandArtHeadlessTests.RunHeadlessTests
    /// </summary>
    public static class OnDemandArtHeadlessTests
    {
        const string CapturePath = "Assets/_PFE/Art/Vector/VectorCapture.json";

        public static void RunHeadlessTests()
        {
            int exitCode = 0;
            var summary = new StringBuilder();
            var totalWatch = Stopwatch.StartNew();

            summary.AppendLine("================================================================================");
            summary.AppendLine("PFE ON-DEMAND ART PIPELINE HEADLESS VERIFICATION & BENCHMARK REPORT");
            summary.AppendLine("Unity Version : " + Application.unityVersion);
            summary.AppendLine("Timestamp     : " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
            summary.AppendLine("================================================================================");
            summary.AppendLine();

            Debug.Log("[ONDEMAND_TEST] Starting OnDemandArtProvider headless verification...");

            try
            {
                var provider = OnDemandArtProvider.Shared;
                int passed = 0;
                int failed = 0;

                // ────────────────────────────────────────────────────────────────
                // SECTION 1: Pathological Raster Shapes (The Baseline Freeze Test)
                // ────────────────────────────────────────────────────────────────
                summary.AppendLine("--- SECTION 1: PATHOLOGICAL RASTER SHAPES (PREVIOUS FREEZE OFFENDERS) ---");
                summary.AppendLine("ID   | SVG File               | Baseline Tess | OnDemand Time | Speedup      | Result");
                summary.AppendLine("-----+------------------------+---------------+---------------+--------------+-------");

                var rasterOffenders = new (int id, string baselineStr, double baselineMs)[]
                {
                    (4652, "1,333,694 ms (22.2m)", 1333694.0),
                    (495,  "  770,827 ms (12.8m)",  770827.0),
                    (1131, "  765,422 ms (12.7m)",  765422.0),
                    (1137, "  298,160 ms (5.0m)",   298160.0),
                    (425,  "  290,122 ms (4.8m)",   290122.0),
                    (411,  "  228,803 ms (3.8m)",   228803.0),
                    (385,  "  202,021 ms (3.4m)",   202021.0),
                    (791,  "  137,960 ms (2.3m)",   137960.0),
                };

                foreach (var item in rasterOffenders)
                {
                    var sw = Stopwatch.StartNew();
                    var sprite = provider.GetShapeSprite(item.id);
                    sw.Stop();

                    double elapsed = sw.Elapsed.TotalMilliseconds;
                    bool ok = sprite != null && sprite.texture != null && sprite.texture.width > 0;

                    if (ok) passed++; else failed++;

                    double speedup = elapsed > 0 ? (item.baselineMs / elapsed) : 0;
                    string line = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0,-4} | {1,-22} | {2,-13} | {3,9:0.00} ms | {4,10:0.0}x | {5}",
                        item.id,
                        $"sym{item.id}",
                        item.baselineStr,
                        elapsed,
                        speedup,
                        ok ? "PASS" : "FAIL");

                    summary.AppendLine(line);
                    Debug.Log($"[ONDEMAND_TEST] Shape {item.id}: {elapsed:0.00} ms (Speedup: {speedup:0.0}x) -> {(ok ? "PASS" : "FAIL")}");
                }
                summary.AppendLine();

                // ────────────────────────────────────────────────────────────────
                // SECTION 2: Pure Vector Shapes (Adaptive Tessellation)
                // ────────────────────────────────────────────────────────────────
                summary.AppendLine("--- SECTION 2: GENUINE VECTOR SHAPES (ADAPTIVE TESSELLATION) ---");
                summary.AppendLine("ID   | Description                         | Gen Time     | Vertices | Triangles | Result");
                summary.AppendLine("-----+-------------------------------------+--------------+----------+-----------+-------");

                var vectorShapes = new (int id, string desc)[]
                {
                    (6,    "UI Arrow Symbol"),
                    (1755, "Complex Vector Glyph"),
                    (3113, "Heaviest Vector (1,380 path commands)"),
                };

                foreach (var item in vectorShapes)
                {
                    var sw = Stopwatch.StartNew();
                    var mesh = provider.GetShapeMesh(item.id);
                    sw.Stop();

                    double elapsed = sw.Elapsed.TotalMilliseconds;
                    bool ok = mesh != null && mesh.vertexCount > 0;
                    if (ok) passed++; else failed++;

                    string line = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0,-4} | {1,-35} | {2,8:0.00} ms | {3,8} | {4,9} | {5}",
                        item.id,
                        item.desc,
                        elapsed,
                        mesh != null ? mesh.vertexCount : 0,
                        mesh != null ? mesh.triangles.Length / 3 : 0,
                        ok ? "PASS" : "FAIL");

                    summary.AppendLine(line);
                    Debug.Log($"[ONDEMAND_TEST] Vector Shape {item.id} ({item.desc}): {elapsed:0.00} ms, verts: {(mesh != null ? mesh.vertexCount : 0)} -> {(ok ? "PASS" : "FAIL")}");
                }
                summary.AppendLine();

                // ────────────────────────────────────────────────────────────────
                // SECTION 3: Two-Tier Cache (L1 Memory & L2 Disk)
                // ────────────────────────────────────────────────────────────────
                summary.AppendLine("--- SECTION 3: TWO-TIER CACHE VERIFICATION ---");
                {
                    var sw = Stopwatch.StartNew();
                    var cachedSprite = provider.GetShapeSprite(385);
                    sw.Stop();

                    double cacheHitTime = sw.Elapsed.TotalMilliseconds;
                    bool isHit = provider.LastSource == "Cache";
                    if (isHit) passed++; else failed++;

                    summary.AppendLine($"L1 Cache Hit Shape 385: {cacheHitTime:0.000} ms (Source: {provider.LastSource}) -> {(isHit ? "PASS" : "FAIL")}");
                    summary.AppendLine($"Total Hits: {provider.Cache.Hits}, Total Misses: {provider.Cache.Misses}, Cached Items: {provider.Cache.Count}");
                    Debug.Log($"[ONDEMAND_TEST] Cache Hit Test: {cacheHitTime:0.000} ms, source: {provider.LastSource} -> {(isHit ? "PASS" : "FAIL")}");
                }
                summary.AppendLine();

                // ────────────────────────────────────────────────────────────────
                // SECTION 4: Composite Multi-Shape Sprite Frames (Phase B)
                // ────────────────────────────────────────────────────────────────
                summary.AppendLine("--- SECTION 4: COMPOSITE SPRITE FRAMES (PHASE B OFFENDERS) ---");
                summary.AppendLine("Sprite ID | Frame | Baseline Build | OnDemand Build | Shapes | Vertices | Result");
                summary.AppendLine("----------+-------+----------------+----------------+--------+----------+-------");

                if (File.Exists(CapturePath))
                {
                    var captureData = VectorCaptureReader.Parse(File.ReadAllText(CapturePath));

                    var compositeTargets = new (int spriteId, int frame, string baselineStr)[]
                    {
                        (3084, 0, "6,549 ms"),
                        (786,  0, "5,887 ms"),
                        (2517, 0, "5,488 ms"),
                        (3621, 0, "5,469 ms"),
                        (1972, 0, "4,415 ms"),
                    };

                    foreach (var target in compositeTargets)
                    {
                        var result = provider.BuildCompositeSpriteFrame(captureData, target.spriteId, target.frame);
                        bool ok = result != null && string.IsNullOrEmpty(result.Error) && result.Mesh != null;
                        if (ok) passed++; else failed++;

                        string line = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0,-9} | {1,-5} | {2,-14} | {3,10:0.00} ms | {4,6} | {5,8} | {6}",
                            target.spriteId,
                            target.frame,
                            target.baselineStr,
                            result.BuildMs,
                            result.ResolvedShapeCount,
                            result.VertexCount,
                            ok ? "PASS" : "FAIL");

                        summary.AppendLine(line);
                        Debug.Log($"[ONDEMAND_TEST] Composite Sprite {target.spriteId} f{target.frame}: {result.BuildMs:0.00} ms (Shapes: {result.ResolvedShapeCount}, Verts: {result.VertexCount}) -> {(ok ? "PASS" : "FAIL")}");
                    }
                }
                else
                {
                    summary.AppendLine($"WARNING: Capture file not found at {CapturePath}");
                }

                totalWatch.Stop();
                summary.AppendLine();
                summary.AppendLine("================================================================================");
                summary.AppendLine($"TOTAL TESTS: {passed + failed} | PASSED: {passed} | FAILED: {failed}");
                summary.AppendLine($"TOTAL RUNTIME: {totalWatch.Elapsed.TotalSeconds:0.00} s");
                summary.AppendLine("================================================================================");

                if (failed > 0)
                {
                    exitCode = 1;
                    Debug.LogError($"[ONDEMAND_TEST] Verification finished with {failed} failures!");
                }
                else
                {
                    Debug.Log("[ONDEMAND_TEST] ALL TESTS PASSED SUCCESSFULLY!");
                }
            }
            catch (Exception ex)
            {
                exitCode = 2;
                summary.AppendLine("\nFATAL EXCEPTION: " + ex.ToString());
                Debug.LogError("[ONDEMAND_TEST] FATAL EXCEPTION: " + ex);
            }

            // Write report to Logs
            string logsDir = Path.Combine(Application.dataPath, "..", "Logs");
            try
            {
                if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
                string outPath = Path.Combine(logsDir, "ondemand_art_test_summary.txt");
                File.WriteAllText(outPath, summary.ToString());
                Debug.Log($"[ONDEMAND_TEST] Summary written to {outPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ONDEMAND_TEST] Failed writing summary file: {ex.Message}");
            }

            EditorApplication.Exit(exitCode);
        }
    }
}
#endif
