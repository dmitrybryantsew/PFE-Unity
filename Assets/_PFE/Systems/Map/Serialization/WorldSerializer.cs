using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace PFE.Systems.Map.Serialization
{
    /// <summary>
    /// Serialize world state to file.
    /// Handles JSON serialization and compression.
    /// </summary>
    public static class WorldSerializer
    {
        private static readonly string SaveDirectory = Path.Combine(Application.persistentDataPath, "Saves");

        /// <summary>
        /// Serialize world to JSON and save to file.
        /// </summary>
        public static bool SerializeWorld(WorldSaveData saveData, string saveId = null)
        {
            if (saveData == null)
            {
                Debug.LogError("Cannot serialize null save data");
                return false;
            }

            try
            {
                // Ensure save directory exists
                Directory.CreateDirectory(SaveDirectory);

                // Generate save ID if not provided
                if (string.IsNullOrEmpty(saveId))
                {
                    saveId = saveData.saveId;
                }

                string filePath = GetSaveFilePath(saveId);

                // Serialize to JSON
                string json = JsonUtility.ToJson(saveData, prettyPrint: true);

                // Compress and write
                WriteCompressedJson(filePath, json);

                Debug.Log($"World saved successfully to: {filePath}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to serialize world: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Serialize world to JSON string (without saving to file).
        /// </summary>
        public static string SerializeToJson(WorldSaveData saveData)
        {
            if (saveData == null)
            {
                Debug.LogError("Cannot serialize null save data");
                return null;
            }

            try
            {
                return JsonUtility.ToJson(saveData, prettyPrint: true);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to serialize world to JSON: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get save file path for a save ID.
        /// </summary>
        public static string GetSaveFilePath(string saveId)
        {
            return Path.Combine(SaveDirectory, $"save_{saveId}.json.gz");
        }

        /// <summary>
        /// Check if save file exists.
        /// </summary>
        public static bool SaveExists(string saveId)
        {
            string filePath = GetSaveFilePath(saveId);
            return File.Exists(filePath);
        }

        /// <summary>
        /// Delete save file.
        /// </summary>
        public static bool DeleteSave(string saveId)
        {
            try
            {
                string filePath = GetSaveFilePath(saveId);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Debug.Log($"Deleted save: {saveId}");
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to delete save {saveId}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Write compressed JSON to file.
        /// </summary>
        private static void WriteCompressedJson(string filePath, string json)
        {
            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Compress))
            using (var writer = new StreamWriter(gzipStream))
            {
                writer.Write(json);
            }
        }

        /// <summary>
        /// Get all save files.
        /// </summary>
        public static string[] GetAllSaveFiles()
        {
            try
            {
                if (!Directory.Exists(SaveDirectory))
                {
                    return Array.Empty<string>();
                }

                string[] files = Directory.GetFiles(SaveDirectory, "save_*.json.gz");
                return files;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to get save files: {e.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Get save IDs from file names.
        /// </summary>
        public static string[] GetAllSaveIds()
        {
            string[] files = GetAllSaveFiles();
            string[] ids = new string[files.Length];

            for (int i = 0; i < files.Length; i++)
            {
                // Extract ID from "save_{id}.json.gz"
                string fileName = Path.GetFileNameWithoutExtension(files[i]);  // "save_{id}.json"
                fileName = Path.GetFileNameWithoutExtension(fileName);  // "save_{id}"
                ids[i] = fileName.Substring(5);  // "{id}"
            }

            return ids;
        }

        /// <summary>
        /// Get save file size in bytes.
        /// </summary>
        public static long GetSaveFileSize(string saveId)
        {
            try
            {
                string filePath = GetSaveFilePath(saveId);
                if (File.Exists(filePath))
                {
                    return new FileInfo(filePath).Length;
                }
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to get save file size for {saveId}: {e.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Export save to uncompressed JSON (for debugging/modding).
        /// </summary>
        public static bool ExportToJson(WorldSaveData saveData, string exportPath)
        {
            if (saveData == null)
            {
                Debug.LogError("Cannot export null save data");
                return false;
            }

            try
            {
                string json = JsonUtility.ToJson(saveData, prettyPrint: true);
                File.WriteAllText(exportPath, json);
                Debug.Log($"Exported save to: {exportPath}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to export save: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Create a backup of a save file.
        /// </summary>
        public static bool BackupSave(string saveId, string backupSuffix = ".bak")
        {
            try
            {
                string sourcePath = GetSaveFilePath(saveId);
                if (!File.Exists(sourcePath))
                {
                    return false;
                }

                string backupPath = sourcePath + backupSuffix;
                File.Copy(sourcePath, backupPath, overwrite: true);
                Debug.Log($"Created backup: {backupPath}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to backup save {saveId}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get save directory path.
        /// </summary>
        public static string GetSaveDirectory()
        {
            return SaveDirectory;
        }
    }
}
