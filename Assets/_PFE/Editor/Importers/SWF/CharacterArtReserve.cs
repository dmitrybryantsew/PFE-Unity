#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// The parts that are shipped at a texel reserve rather than at zoom 1, and the one call that
    /// produces them — so "import the character" means one button and not "remember to run ffdec
    /// first".
    ///
    /// <para><b>Why a reserve at all.</b> A body part is vector art that JPEXS rasterises at export
    /// time. At zoom 1 the raster is the vector's own pixel size, which is fine for a part drawn at
    /// ≈1:1 on screen and wrong for a part that is tiny: <c>pip</c>(513) is 7.05 × 7.15 units and came
    /// out 7 × 7 px with every texel opaque, so the device's silhouette and its screen were both gone.
    /// The importer measures each symbol's zoom from the PNG (PPU = 100 × zoom, world size unchanged),
    /// so density is a free parameter — it only has to be *produced*. See
    /// <c>docs/CharacterImportPipeline/12</c> §2b and <c>05</c> §4.</para>
    ///
    /// <para><b>"Free" has three numbers in it, not two.</b> A part's on-screen size is
    /// <c>rect / ppu</c>, not <c>png / ppu</c>. Density is therefore only free while the sprite rect is
    /// re-measured with the art: on 2026-10-27 this class exported <c>pip</c> to 56 × 57, the importer
    /// set PPU 800, and the rect stayed <b>7 × 7</b> — an eighth of the right size, in every renderer,
    /// with nothing in the log to say so. The importer now reads every rect back after it finishes
    /// (<c>CharacterSpriteImporter.VerifySpriteRects</c>), which is what makes the premise true rather
    /// than merely stated. <c>docs/CharacterImportPipeline/07</c>, follow-up.</para>
    ///
    /// <para><b>Why the export lives in code and not in a README.</b> It did live in a README, and the
    /// first re-import after it was written ran without it: the importer faithfully re-imported the
    /// 7 × 7 file and the only symptom was the same block as before. A step that has to be remembered
    /// before a button is a step that gets skipped, so it is now part of the button. The export is
    /// also <i>idempotent</i>: a symbol whose art already measures its target zoom is skipped, so
    /// pressing Import repeatedly costs nothing and a stale folder repairs itself.</para>
    ///
    /// <para><b>Only the parts that need it.</b> The six leg parts are also below their ideal density
    /// (<c>05</c> §4 recommends 4×) but are left at 1× on purpose: they are 9–21 units and read at
    /// ≈0.9 texels per screen pixel, which the sampler policy handles; changing them is a visual
    /// change nobody has A/B'd yet. Adding one is a single line in <see cref="Reserved"/>.</para>
    /// </summary>
    public static class CharacterArtReserve
    {
        /// <summary>
        /// symbolId → the export zoom its art must be at, and the part name for the log. Keep this
        /// list short and justified: every entry is a folder that a re-export has to keep in step.
        /// </summary>
        public static readonly (int symbolId, string part, int zoom)[] Reserved =
        {
            (513, "pip", 8),   // 7 units; the only part with interior detail (a screen with two lines)
        };

        /// <summary>The zoom <paramref name="symbolId"/> must be shipped at — 1 when it is not reserved.</summary>
        public static int TargetZoom(int symbolId)
        {
            foreach (var (id, _, zoom) in Reserved)
                if (id == symbolId) return zoom;
            return 1;
        }

        /// <summary>
        /// Export every reserved symbol whose art on disk is below its target zoom.
        ///
        /// <para>Returns the log lines for the import window. Never throws and never blocks the import:
        /// a missing ffdec, a missing SWF or a non-zero exit is reported as an <c>[ERR]</c> line and the
        /// caller carries on with whatever art is on disk, which is the same behaviour as before this
        /// existed. Symbols already at (or above) their target are skipped with a line saying so, which
        /// is what makes the call cheap to repeat.</para>
        /// </summary>
        /// <param name="ffdecCli">Path to <c>ffdec-cli.exe</c>. Empty means "not configured".</param>
        /// <param name="swfPath">The SWF the art comes from — the same one the import parses.</param>
        /// <param name="exportRoot">The JPEXS export folder (<c>_assets</c>), i.e. the parent of <c>sprites/</c>.</param>
        /// <param name="swf">Parsed SWF, for the bounds the measurement needs.</param>
        public static List<string> ExportBelowTarget(string ffdecCli, string swfPath, string exportRoot,
                                                     SWFFile swf)
        {
            var lines = new List<string>();

            if (Reserved.Length == 0) return lines;

            if (string.IsNullOrEmpty(ffdecCli) || !File.Exists(ffdecCli))
            {
                lines.Add($"[WARN] ffdec CLI not found ({(string.IsNullOrEmpty(ffdecCli) ? "unset" : ffdecCli)}); " +
                          "reserved parts keep whatever zoom is already on disk. Set it in the import window.");
                return lines;
            }
            if (string.IsNullOrEmpty(swfPath) || !File.Exists(swfPath))
            {
                lines.Add($"[WARN] SWF not found at '{swfPath}'; cannot re-export reserved parts.");
                return lines;
            }

            // Group by zoom so a run is one ffdec invocation per distinct zoom, not per part.
            foreach (var group in Reserved.GroupBy(r => r.zoom).OrderBy(g => g.Key))
            {
                var pending = new List<(int symbolId, string part, int measured)>();
                foreach (var (symbolId, part, zoom) in group)
                {
                    int measured = CharacterSpriteImporter.MeasureShippedZoom(exportRoot, swf, symbolId);
                    if (measured >= zoom)
                    {
                        lines.Add($"  {part}({symbolId}): already {measured}x on disk, target {zoom}x — skipped");
                        continue;
                    }
                    pending.Add((symbolId, part, measured));
                }

                if (pending.Count == 0) continue;

                string ids = string.Join(",", pending.Select(p => p.symbolId));
                lines.Add($"  Exporting {string.Join(", ", pending.Select(p => $"{p.part}({p.symbolId})"))} " +
                          $"at zoom {group.Key} (ffdec -selectid {ids})");

                // Export into a staging folder, then copy in. Not because ffdec refuses to overwrite
                // (it does overwrite — verified by resetting a file to 7x7 and re-running), but
                // because the CLI and the GUI disagree about the layout: `-export sprite <dir>` writes
                // <dir>/DefineSprite_513_symbol513/, while the tree the importer reads is
                // <dir>/sprites/DefineSprite_513_symbol513/ — the GUI's "export all sprites". Pointing
                // ffdec straight at _assets therefore creates a sibling folder, prints OK, and changes
                // nothing that gets imported. Staging + an explicit copy is what bridges the two.
                string staging = Path.Combine(Path.GetTempPath(), "pfe_reserve_export",
                                              Guid.NewGuid().ToString("N"));
                var sw = Stopwatch.StartNew();
                try
                {
                    Directory.CreateDirectory(staging);
                    bool ok = RunFfdec(ffdecCli, $"-zoom {group.Key} -selectid {ids} -export sprite " +
                                                $"\"{staging}\" \"{swfPath}\"", out string output);
                    if (!ok)
                    {
                        lines.Add($"[ERR] ffdec failed for {ids} (see the console for its output); " +
                                  "the import continues with the art already on disk");
                        Debug.LogWarning($"[CharacterArtReserve] ffdec output:\n{output}");
                        continue;
                    }

                    foreach (var (symbolId, part, _) in pending)
                    {
                        int copied = CopySymbolFrames(staging, exportRoot, symbolId);
                        if (copied == 0)
                            lines.Add($"[WARN] {part}({symbolId}): ffdec produced no PNG for this symbol " +
                                      "in the staging folder — nothing was replaced");
                    }
                }
                finally
                {
                    sw.Stop();
                    try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                    catch (Exception e) { Debug.LogWarning($"[CharacterArtReserve] staging cleanup: {e.Message}"); }
                }

                // Report what actually landed, not what was asked for: the number below is the one
                // the importer will measure a moment later.
                foreach (var (symbolId, part, before) in pending)
                {
                    int after = CharacterSpriteImporter.MeasureShippedZoom(exportRoot, swf, symbolId);
                    if (after >= group.Key)
                        lines.Add($"  {part}({symbolId}): {before}x → {after}x in {sw.ElapsedMilliseconds}ms");
                    else
                        lines.Add($"[WARN] {part}({symbolId}) still measures {after}x after the export " +
                                  $"(wanted {group.Key}x) — the export folder may not be the one being imported");
                }
            }

            return lines;
        }

        /// <summary>
        /// Copy one symbol's PNGs out of the staging export over the shipped ones. Returns how many
        /// files were written; 0 means ffdec did not produce the symbol at all.
        /// </summary>
        static int CopySymbolFrames(string staging, string exportRoot, int symbolId)
        {
            string source = FindSymbolFolder(staging, symbolId);
            if (source == null) return 0;

            string target = Path.Combine(exportRoot, "sprites", Path.GetFileName(source));
            Directory.CreateDirectory(target);

            int copied = 0;
            foreach (string png in Directory.GetFiles(source, "*.png"))
            {
                File.Copy(png, Path.Combine(target, Path.GetFileName(png)), overwrite: true);
                copied++;
            }
            return copied;
        }

        /// <summary>
        /// Locate one symbol's export folder under <paramref name="root"/>, in either layout: the
        /// CLI's flat <c>{root}/DefineSprite_{id}_symbol{id}</c> or the GUI's
        /// <c>{root}/sprites/…</c>. A sprite and a bare shape differ only in the prefix.
        /// </summary>
        static string FindSymbolFolder(string root, int symbolId)
        {
            foreach (string prefix in new[] { "DefineSprite", "DefineShape" })
            {
                string name = $"{prefix}_{symbolId}_symbol{symbolId}";
                string flat = Path.Combine(root, name);
                if (Directory.Exists(flat)) return flat;
                string nested = Path.Combine(root, "sprites", name);
                if (Directory.Exists(nested)) return nested;
            }
            return null;
        }

        /// <summary>
        /// Run ffdec and collect its output. Both streams are read asynchronously: ffdec writes
        /// progress to stdout and warnings to stderr, and reading one to completion before the other
        /// deadlocks on a full pipe once the export is big enough.
        /// </summary>
        static bool RunFfdec(string exe, string arguments, out string output)
        {
            var sb = new StringBuilder();
            try
            {
                var psi = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
                };

                using Process process = Process.Start(psi);
                if (process == null)
                {
                    output = "Process.Start returned null";
                    return false;
                }

                process.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(180_000))
                {
                    try { process.Kill(); } catch { /* already gone */ }
                    output = sb + "\n[timed out after 180 s]";
                    return false;
                }

                // WaitForExit(int) does not wait for the async readers to drain.
                process.WaitForExit();
                output = sb.ToString().Trim();
                return process.ExitCode == 0;
            }
            catch (Exception e)
            {
                output = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }
    }
}
#endif
