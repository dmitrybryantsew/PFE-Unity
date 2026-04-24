using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using PFE.Systems.Audio;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Editor tool: PFE / Import / Import Sounds
    ///
    /// What it does:
    ///   1. Copies wav/mp3 files from the three extracted SWF sound folders into Unity,
    ///      stripping the leading numeric prefix (e.g. "15_rifle_s.wav" → "rifle_s.wav").
    ///   2. Calls AssetDatabase.ImportAsset on each file.
    ///   3. Parses Snd.as to extract group declarations.
    ///   4. Builds (or updates) the SoundCatalog asset at Assets/_PFE/Data/SoundCatalog.asset.
    ///   5. Writes a report to Assets/_PFE/Data/SoundImportReport.txt.
    ///
    /// Local source paths come from the PFE_IMPORT_ROOT environment variable when present.
    /// </summary>
    public static class SoundImporter
    {
        // --- Configuration -----------------------------------------------------------

        private static string SourceRoot => SourceImportPaths.SourceProjectRoot;
        private static string SndAsPath => SourceImportPaths.SoundDefinitionPath;

        private const string DEST_ROOT =
            "Assets/_PFE/Data/Resources/Sounds";

        private const string CATALOG_PATH =
            "Assets/_PFE/Data/SoundCatalog.asset";

        private static readonly (string srcFolder, string destSubfolder, string libraryId)[] Libraries =
        {
            (@"sound.swf\sounds",        "sound",        "sound"),
            (@"sound_weapon.swf\sounds", "sound_weapon", "sound_weapon"),
            (@"sound_unit.swf\sounds",   "sound_unit",   "sound_unit"),
        };

        // Numeric prefix pattern: one or more digits followed by underscore at start of name
        private static readonly Regex NumericPrefix = new Regex(@"^\d+_", RegexOptions.Compiled);

        // ---------------------------------------------------------------------------------

        [MenuItem("PFE/Import/Import Sounds")]
        public static void ImportSounds()
        {
            var report = new StringBuilder();
            report.AppendLine($"Sound Import Report — {DateTime.Now:yyyy-MM-dd HH:mm}");
            report.AppendLine(new string('=', 60));

            // Step 1: Copy audio files into Unity project
            var importedClipPaths = new Dictionary<string, string>(); // logicalId → assetPath
            int copied = 0, skipped = 0, errors = 0;

            foreach (var (srcFolder, destSubfolder, libraryId) in Libraries)
            {
                string srcPath = string.IsNullOrWhiteSpace(SourceRoot)
                    ? string.Empty
                    : Path.Combine(SourceRoot, srcFolder);
                string destPath = Path.Combine(Application.dataPath, "_PFE/Data/Resources/Sounds", destSubfolder);

                if (!Directory.Exists(srcPath))
                {
                    report.AppendLine($"[MISSING] Source folder not found: {srcPath}");
                    errors++;
                    continue;
                }

                Directory.CreateDirectory(destPath);

                report.AppendLine($"\n[{libraryId}] {srcPath}");

                foreach (string srcFile in Directory.GetFiles(srcPath))
                {
                    string ext = Path.GetExtension(srcFile).ToLowerInvariant();
                    if (ext != ".wav" && ext != ".mp3") continue;

                    string rawName = Path.GetFileName(srcFile);
                    string cleanName = NumericPrefix.Replace(rawName, "");          // strip prefix
                    string logicalId = Path.GetFileNameWithoutExtension(cleanName); // strip extension
                    string destFile = Path.Combine(destPath, cleanName);
                    string assetPath = $"{DEST_ROOT}/{destSubfolder}/{cleanName}";

                    if (File.Exists(destFile))
                    {
                        report.AppendLine($"  SKIP  {cleanName}");
                        skipped++;
                    }
                    else
                    {
                        try
                        {
                            File.Copy(srcFile, destFile);
                            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                            report.AppendLine($"  COPY  {cleanName}");
                            copied++;
                        }
                        catch (Exception ex)
                        {
                            report.AppendLine($"  ERR   {cleanName} — {ex.Message}");
                            errors++;
                        }
                    }

                    // Track with asset path for catalog building
                    if (!importedClipPaths.ContainsKey(logicalId))
                        importedClipPaths[logicalId] = assetPath;
                }
            }

            report.AppendLine($"\nCopied: {copied}  Skipped: {skipped}  Errors: {errors}");
            AssetDatabase.Refresh();

            // Step 2: Parse Snd.as for group declarations
            var groups = ParseSndAsGroups(SndAsPath, report);

            // Step 3: Build SoundCatalog
            BuildCatalog(importedClipPaths, groups, report);

            // Step 4: Ensure ImpactSoundTable exists with AS3-parity defaults
            EnsureImpactSoundTable(report);

            // Step 5: Write report
            string reportPath = Path.Combine(Application.dataPath, "_PFE/Data/SoundImportReport.txt");
            File.WriteAllText(reportPath, report.ToString());
            AssetDatabase.ImportAsset("Assets/_PFE/Data/SoundImportReport.txt");

            Debug.Log($"[SoundImporter] Done. Copied {copied} files. See SoundImportReport.txt for details.");
            EditorUtility.DisplayDialog("Sound Import Complete",
                $"Copied: {copied}\nSkipped (already present): {skipped}\nErrors: {errors}\n\nSee Assets/_PFE/Data/SoundImportReport.txt",
                "OK");
        }

        // ---------------------------------------------------------------------------------
        // Parse Snd.as
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Extract group declarations from the embedded XML in Snd.as.
        /// Groups look like: &lt;s id='rm'&gt;&lt;s id='rm1'/&gt;...&lt;/s&gt;
        /// Returns: groupId → list of child IDs.
        /// Also returns non-grouped leaf IDs as single-element lists.
        /// </summary>
        private static Dictionary<string, List<string>> ParseSndAsGroups(string sndAsPath,
            StringBuilder report)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(sndAsPath))
            {
                report.AppendLine($"\n[WARN] Snd.as not found at {sndAsPath} — catalog built without group info");
                return groups;
            }

            string content = File.ReadAllText(sndAsPath);

            // Extract the XML block between <all> and </all>
            int xmlStart = content.IndexOf("<all>", StringComparison.Ordinal);
            int xmlEnd   = content.LastIndexOf("</all>", StringComparison.Ordinal);
            if (xmlStart < 0 || xmlEnd < 0)
            {
                report.AppendLine("\n[WARN] Could not find <all>…</all> block in Snd.as");
                return groups;
            }

            string xml = content.Substring(xmlStart, xmlEnd - xmlStart + "</all>".Length);

            // Use a simple regex-based parser (not XmlDocument to avoid Unity quirks with
            // ActionScript embedded XML).
            //
            // Pattern: <s id='GROUP'><s id='CHILD'/> ... </s>
            // We look for <s id='X'> (opening, not self-closing) then collect <s id='Y'/>
            // children before the matching </s>.

            var groupPattern = new Regex(
                @"<s\s+id='(?<gid>[^']+)'\s*>(?<inner>.*?)</s>",
                RegexOptions.Singleline | RegexOptions.Compiled);

            var childPattern = new Regex(
                @"<s\s+id='(?<cid>[^']+)'\s*/>",
                RegexOptions.Compiled);

            foreach (Match gm in groupPattern.Matches(xml))
            {
                string groupId = gm.Groups["gid"].Value;
                string inner   = gm.Groups["inner"].Value;
                var children = new List<string>();

                foreach (Match cm in childPattern.Matches(inner))
                    children.Add(cm.Groups["cid"].Value);

                if (children.Count > 0)
                    groups[groupId] = children;
            }

            report.AppendLine($"\n[Snd.as] Parsed {groups.Count} groups");
            return groups;
        }

        // ---------------------------------------------------------------------------------
        // Build catalog
        // ---------------------------------------------------------------------------------

        private static void BuildCatalog(
            Dictionary<string, string> clipPaths,
            Dictionary<string, List<string>> groups,
            StringBuilder report)
        {
            // Load or create the catalog asset
            var catalog = AssetDatabase.LoadAssetAtPath<SoundCatalog>(CATALOG_PATH);
            bool isNew = catalog == null;
            if (isNew)
            {
                catalog = ScriptableObject.CreateInstance<SoundCatalog>();
                // Ensure directory exists
                string dir = Path.GetDirectoryName(CATALOG_PATH);
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "../", dir));
            }

            // Build a lookup of existing entries so we don't duplicate
            var existing = new Dictionary<string, SoundCatalog.Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in catalog.entries)
                if (!string.IsNullOrEmpty(e.id)) existing[e.id] = e;

            int added = 0, updated = 0;

            // --- Add/update group entries ---
            foreach (var kv in groups)
            {
                string groupId = kv.Key;
                var childIds   = kv.Value;
                var clips      = new List<AudioClip>();

                foreach (string cid in childIds)
                {
                    if (!clipPaths.TryGetValue(cid, out string ap)) continue;
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(ap);
                    if (clip != null) clips.Add(clip);
                }

                if (clips.Count == 0) continue;

                string library = GuessLibrary(groupId, childIds, clipPaths);

                if (existing.TryGetValue(groupId, out var entry))
                {
                    entry.variants = clips.ToArray();
                    entry.library  = library;
                    updated++;
                }
                else
                {
                    var newEntry = new SoundCatalog.Entry
                    {
                        id       = groupId,
                        variants = clips.ToArray(),
                        library  = library,
                    };
                    catalog.entries.Add(newEntry);
                    existing[groupId] = newEntry;
                    added++;
                }

                // Also register individual variant entries (fang_hit1, rm1, etc.)
                foreach (string cid in childIds)
                {
                    if (!clipPaths.TryGetValue(cid, out string ap)) continue;
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(ap);
                    if (clip == null) continue;

                    if (!existing.ContainsKey(cid))
                    {
                        var ve = new SoundCatalog.Entry
                        {
                            id       = cid,
                            variants = new[] { clip },
                            library  = library,
                        };
                        catalog.entries.Add(ve);
                        existing[cid] = ve;
                        added++;
                    }
                }
            }

            // --- Add remaining ungrouped clips ---
            // Collect all IDs that are children of a group (so we don't make duplicate top-level entries)
            var childSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in groups)
                foreach (string c in kv.Value) childSet.Add(c);

            foreach (var kv in clipPaths)
            {
                string logicalId = kv.Key;
                string assetPath = kv.Value;

                if (existing.ContainsKey(logicalId)) continue; // already in catalog

                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                if (clip == null) continue;

                string library = DeduceLibrary(assetPath);

                var entry = new SoundCatalog.Entry
                {
                    id       = logicalId,
                    variants = new[] { clip },
                    library  = library,
                };
                catalog.entries.Add(entry);
                existing[logicalId] = entry;
                added++;
            }

            // Sort entries by id for readability in the inspector
            catalog.entries.Sort((a, b) => string.Compare(a.id, b.id, StringComparison.OrdinalIgnoreCase));
            catalog.InvalidateCache();

            // Save
            if (isNew)
                AssetDatabase.CreateAsset(catalog, CATALOG_PATH);
            else
                EditorUtility.SetDirty(catalog);

            AssetDatabase.SaveAssets();

            report.AppendLine($"\n[Catalog] Added: {added}  Updated: {updated}  Total entries: {catalog.entries.Count}");
            report.AppendLine($"  Saved to: {CATALOG_PATH}");
        }

        // ---------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------

        private static string DeduceLibrary(string assetPath)
        {
            if (assetPath.Contains("sound_weapon")) return "sound_weapon";
            if (assetPath.Contains("sound_unit"))   return "sound_unit";
            return "sound";
        }

        // ── ImpactSoundTable ──────────────────────────────────────────────────

        private const string IMPACT_TABLE_PATH =
            "Assets/_PFE/Data/Resources/ImpactSoundTable.asset";

        /// <summary>
        /// Creates the ImpactSoundTable asset if absent, or adds any missing entries
        /// if it already exists. Never overwrites manually-customised values.
        ///
        /// Default mapping matches AS3 Bullet.sound() from Bullet.as.
        /// </summary>
        private static void EnsureImpactSoundTable(StringBuilder report)
        {
            report.AppendLine("\n[ImpactSoundTable]");

            var table = AssetDatabase.LoadAssetAtPath<PFE.Systems.Audio.ImpactSoundTable>(IMPACT_TABLE_PATH);
            bool isNew = table == null;
            if (isNew)
            {
                table = ScriptableObject.CreateInstance<PFE.Systems.Audio.ImpactSoundTable>();
                report.AppendLine("  Created new ImpactSoundTable asset.");
            }

            // ── Default surface entries (AS3 parity) ─────────────────────────
            var defaultSurfaces = new[]
            {
                new PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry
                    { material = PFE.Systems.Audio.SurfaceMaterial.Metal,    soundId = "hit_metal",    volume = 0.4f },
                new PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry
                    { material = PFE.Systems.Audio.SurfaceMaterial.Concrete, soundId = "hit_concrete", volume = 0.5f },
                new PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry
                    { material = PFE.Systems.Audio.SurfaceMaterial.Wood,     soundId = "hit_wood",     volume = 0.5f },
                new PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry
                    { material = PFE.Systems.Audio.SurfaceMaterial.Glass,    soundId = "hit_glass",    volume = 0.5f },
                new PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry
                    { material = PFE.Systems.Audio.SurfaceMaterial.Pole,     soundId = "hit_pole",     volume = 0.5f },
                new PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry
                    { material = PFE.Systems.Audio.SurfaceMaterial.Flesh,    soundId = "hit_flesh",    volume = 0.5f },
                new PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry
                    { material = PFE.Systems.Audio.SurfaceMaterial.Water,    soundId = "hit_water",    volume = 0.5f },
                new PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry
                    { material = PFE.Systems.Audio.SurfaceMaterial.Slime,    soundId = "hit_slime",    volume = 0.5f },
                new PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry
                    { material = PFE.Systems.Audio.SurfaceMaterial.Necrotic, soundId = "hit_necr",     volume = 0.5f },
            };

            // ── Default flesh overrides (AS3 parity: bullet/blade differentiation) ──
            var defaultFlesh = new[]
            {
                new PFE.Systems.Audio.ImpactSoundTable.FleshOverride
                    { damageType = PFE.Data.Definitions.DamageType.PhysicalBullet, soundId = "hit_bullet", volume = 0.8f },
                new PFE.Systems.Audio.ImpactSoundTable.FleshOverride
                    { damageType = PFE.Data.Definitions.DamageType.Blade,          soundId = "hit_blade",  volume = 0.8f },
                new PFE.Systems.Audio.ImpactSoundTable.FleshOverride
                    { damageType = PFE.Data.Definitions.DamageType.PhysicalMelee,  soundId = "hit_blade",  volume = 0.8f },
                new PFE.Systems.Audio.ImpactSoundTable.FleshOverride
                    { damageType = PFE.Data.Definitions.DamageType.Fang,           soundId = "hit_blade",  volume = 0.8f },
            };

            // Merge: only add entries that don't already exist (preserves manual edits).
            var existingSurfaces = new HashSet<PFE.Systems.Audio.SurfaceMaterial>();
            if (table.surfaceSounds != null)
                foreach (var e in table.surfaceSounds)
                    existingSurfaces.Add(e.material);

            var existingFlesh = new HashSet<PFE.Data.Definitions.DamageType>();
            if (table.fleshOverrides != null)
                foreach (var o in table.fleshOverrides)
                    existingFlesh.Add(o.damageType);

            var mergedSurfaces = new List<PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry>(
                table.surfaceSounds ?? Array.Empty<PFE.Systems.Audio.ImpactSoundTable.SurfaceEntry>());
            int addedSurfaces = 0;
            foreach (var d in defaultSurfaces)
            {
                if (!existingSurfaces.Contains(d.material))
                {
                    mergedSurfaces.Add(d);
                    addedSurfaces++;
                }
            }

            var mergedFlesh = new List<PFE.Systems.Audio.ImpactSoundTable.FleshOverride>(
                table.fleshOverrides ?? Array.Empty<PFE.Systems.Audio.ImpactSoundTable.FleshOverride>());
            int addedFlesh = 0;
            foreach (var d in defaultFlesh)
            {
                if (!existingFlesh.Contains(d.damageType))
                {
                    mergedFlesh.Add(d);
                    addedFlesh++;
                }
            }

            table.surfaceSounds  = mergedSurfaces.ToArray();
            table.fleshOverrides = mergedFlesh.ToArray();
            table.InvalidateCache();

            if (isNew)
                AssetDatabase.CreateAsset(table, IMPACT_TABLE_PATH);
            else
                EditorUtility.SetDirty(table);

            AssetDatabase.SaveAssets();

            report.AppendLine($"  Surface entries added: {addedSurfaces}  Flesh overrides added: {addedFlesh}");
            report.AppendLine($"  Saved to: {IMPACT_TABLE_PATH}");
            report.AppendLine("  Assign 'ImpactSoundTable' asset to GameLifetimeScope._impactSoundTable in the scene.");
        }

        private static string GuessLibrary(string groupId, List<string> childIds,
            Dictionary<string, string> clipPaths)
        {
            // Check any child to determine library
            foreach (string c in childIds)
            {
                if (clipPaths.TryGetValue(c, out string ap))
                    return DeduceLibrary(ap);
            }
            return "sound";
        }
    }
}
