using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using UnityEditor;
using UnityEngine;
using PFE.Systems.Audio;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Editor tool: PFE / Import / Import Music
    ///
    /// Copies .mp3 music files from the extracted game source into Unity,
    /// then builds (or updates) MusicCatalog.asset.
    ///
    /// Source: pfeToUnity music folder (mp3 files named by track ID).
    /// Dest:   Assets/_PFE/Data/Resources/Music/
    /// Catalog: Assets/_PFE/Data/MusicCatalog.asset
    ///
    /// Re-running is safe — existing files and entries are not duplicated.
    /// Clips should be set to Load Type = Streaming after import (do this once manually
    /// or via an AudioImporterOverride preset).
    /// </summary>
    public static class MusicImporter
    {
        private static string SourceRoot => SourceImportPaths.SourceProjectRoot;

        private const string DEST_ROOT =
            "Assets/_PFE/Data/Resources/Music";

        private const string CATALOG_PATH =
            "Assets/_PFE/Data/MusicCatalog.asset";

        // All known music track IDs from Snd.as — used to name entries in the catalog.
        // Files are expected as "<id>.mp3" in the source folder.
        private static readonly string[] KnownTrackIds =
        {
            // Location themes
            "music_begin", "music_strange", "music_base", "music_surf", "music_enc",
            "music_raiders", "music_plant_1", "music_plant_2", "music_sewer_1",
            "music_stable_1", "music_stable_2", "music_cat_1", "music_mane_1", "music_mane_2",
            "music_mbase", "music_covert", "music_minst", "music_encl_1", "music_encl_2",
            "music_pi", "music_workshop", "music_hql", "music_red",
            "music_fall_1", "music_fall_2", "music_end",
            // Combat
            "pre_1", "combat_1",
            "boss_1", "boss_2", "boss_3", "boss_4", "boss_5", "boss_6",
            // Special
            "harddie",
            // Main menu
            "mainmenu",
        };

        // Remote URL base (original game CDN — still live)
        private const string REMOTE_MUSIC_BASE = "https://foe.ucoz.org/Sound/music/";

        // Possible source locations for the music files (local extraction fallback)
        private static readonly string[] SourceSearchFolders =
        {
            @"music",
            @"pfe\music",
            @"pfe\as3_map_extraction\music",
            @"Music",
        };

        [MenuItem("PFE/Import/Import Music")]
        public static void ImportMusic()
        {
            var report = new StringBuilder();
            report.AppendLine($"Music Import Report — {DateTime.Now:yyyy-MM-dd HH:mm}");
            report.AppendLine(new string('=', 60));

            // Ensure destination folder exists
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "../", DEST_ROOT));

            // Find where source music files live
            string sourceFolder = FindSourceFolder();
            bool useRemote = sourceFolder == null;

            if (useRemote)
            {
                report.AppendLine($"No local source found — downloading from {REMOTE_MUSIC_BASE}");
                Debug.Log($"[MusicImporter] No local music folder found. Downloading from {REMOTE_MUSIC_BASE}");
            }
            else
            {
                report.AppendLine($"Source folder: {sourceFolder}");
            }

            // Copy/download files into Unity
            var importedEntries = new List<(string id, string assetPath)>();
            int copied = 0, skipped = 0, missing = 0;

            using var client = new WebClient();

            foreach (string trackId in KnownTrackIds)
            {
                string destRelative = $"{DEST_ROOT}/{trackId}.mp3";
                string destFull = Path.Combine(Application.dataPath, "../", destRelative);

                if (File.Exists(destFull))
                {
                    report.AppendLine($"  EXISTS   {trackId}.mp3");
                    importedEntries.Add((trackId, destRelative));
                    skipped++;
                    continue;
                }

                if (useRemote)
                {
                    try
                    {
                        string url = REMOTE_MUSIC_BASE + trackId + ".mp3";
                        EditorUtility.DisplayProgressBar("Downloading Music", $"Fetching {trackId}.mp3…", (float)copied / KnownTrackIds.Length);
                        client.DownloadFile(url, destFull);
                        AssetDatabase.ImportAsset(destRelative);
                        SetStreamingImportSettings(destRelative);
                        report.AppendLine($"  DOWNLOADED {trackId}.mp3");
                        importedEntries.Add((trackId, destRelative));
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"  FAILED   {trackId}.mp3 — {ex.Message}");
                        missing++;
                    }
                }
                else
                {
                    string srcFile = Path.Combine(sourceFolder, trackId + ".mp3");
                    if (!File.Exists(srcFile))
                        srcFile = Path.Combine(sourceFolder, trackId + ".wav");

                    if (!File.Exists(srcFile))
                    {
                        report.AppendLine($"  MISSING  {trackId}");
                        missing++;
                        continue;
                    }

                    string ext = Path.GetExtension(srcFile);
                    string destWithExt = $"{DEST_ROOT}/{trackId}{ext}";
                    string destWithExtFull = Path.Combine(Application.dataPath, "../", destWithExt);
                    File.Copy(srcFile, destWithExtFull);
                    AssetDatabase.ImportAsset(destWithExt);
                    SetStreamingImportSettings(destWithExt);
                    report.AppendLine($"  COPIED   {trackId}{ext}");
                    importedEntries.Add((trackId, destWithExt));
                    copied++;
                }
            }

            EditorUtility.ClearProgressBar();

            report.AppendLine();
            report.AppendLine($"Copied: {copied}  Already exists: {skipped}  Missing from source: {missing}");

            AssetDatabase.Refresh();

            // Build or update catalog
            BuildOrUpdateCatalog(importedEntries, report);
            WriteFinalReport(report);

            string verb = useRemote ? "Downloaded" : "Copied";
            EditorUtility.DisplayDialog(
                "Music Import Complete",
                $"{verb}: {copied}\nAlready existed: {skipped}\nFailed/missing: {missing}\n\nCatalog updated: {CATALOG_PATH}",
                "OK");
        }

        private static string FindSourceFolder()
        {
            foreach (var rel in SourceSearchFolders)
            {
                if (string.IsNullOrWhiteSpace(SourceRoot))
                    continue;

                string path = Path.Combine(SourceRoot, rel);
                if (Directory.Exists(path))
                    return path;
            }
            return null;
        }

        private static void SetStreamingImportSettings(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            if (importer == null) return;

            var settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.Streaming;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            settings.quality = 0.7f;
            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
        }

        private static void BuildOrUpdateCatalog(
            List<(string id, string assetPath)> entries,
            StringBuilder report)
        {
            // Load or create catalog asset
            var catalog = AssetDatabase.LoadAssetAtPath<MusicCatalog>(CATALOG_PATH);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<MusicCatalog>();
                var dir = Path.GetDirectoryName(Path.Combine(Application.dataPath, "../", CATALOG_PATH));
                Directory.CreateDirectory(dir);
                AssetDatabase.CreateAsset(catalog, CATALOG_PATH);
                report.AppendLine($"\nCreated new catalog: {CATALOG_PATH}");
            }
            else
            {
                report.AppendLine($"\nUpdating existing catalog: {CATALOG_PATH}");
            }

            // Build lookup of existing catalog entries
            var existing = new Dictionary<string, int>(catalog.tracks.Count, StringComparer.Ordinal);
            for (int i = 0; i < catalog.tracks.Count; i++)
                if (!string.IsNullOrEmpty(catalog.tracks[i].id))
                    existing[catalog.tracks[i].id] = i;

            int added = 0, updated = 0;

            foreach (var (id, assetPath) in entries)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                if (clip == null) continue;

                if (existing.TryGetValue(id, out int idx))
                {
                    catalog.tracks[idx].clip = clip;
                    updated++;
                }
                else
                {
                    catalog.tracks.Add(new MusicCatalog.Entry { id = id, clip = clip });
                    added++;
                }
            }

            catalog.InvalidateCache();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            report.AppendLine($"Catalog entries — added: {added}, updated: {updated}, total: {catalog.tracks.Count}");
        }

        private static void WriteFinalReport(StringBuilder report)
        {
            string reportPath = "Assets/_PFE/Data/MusicImportReport.txt";
            File.WriteAllText(
                Path.Combine(Application.dataPath, "../", reportPath),
                report.ToString(),
                Encoding.UTF8);
            AssetDatabase.ImportAsset(reportPath);
            Debug.Log($"[MusicImporter] Done. Report: {reportPath}");
        }
    }
}
