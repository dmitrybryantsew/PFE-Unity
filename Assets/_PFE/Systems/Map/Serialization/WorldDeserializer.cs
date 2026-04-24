using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace PFE.Systems.Map.Serialization
{
    /// <summary>
    /// Deserialize world state from file.
    /// Handles JSON deserialization and decompression.
    /// </summary>
    public static class WorldDeserializer
    {
        /// <summary>
        /// Deserialize world from file.
        /// </summary>
        public static WorldSaveData DeserializeWorld(string saveId)
        {
            if (string.IsNullOrEmpty(saveId))
            {
                Debug.LogError("Save ID cannot be null or empty");
                return null;
            }

            try
            {
                string filePath = WorldSerializer.GetSaveFilePath(saveId);

                if (!File.Exists(filePath))
                {
                    Debug.LogError($"Save file not found: {filePath}");
                    return null;
                }

                // Read and decompress
                string json = ReadCompressedJson(filePath);

                if (string.IsNullOrEmpty(json))
                {
                    Debug.LogError($"Failed to read save file: {filePath}");
                    return null;
                }

                // Deserialize from JSON
                WorldSaveData saveData = JsonUtility.FromJson<WorldSaveData>(json);

                if (saveData == null)
                {
                    Debug.LogError($"Failed to deserialize save data from: {filePath}");
                    return null;
                }

                // Validate save data
                if (!ValidateSaveData(saveData))
                {
                    Debug.LogError($"Invalid save data in: {filePath}");
                    return null;
                }

                Debug.Log($"World loaded successfully from: {filePath}");
                return saveData;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to deserialize world: {e.Message}\n{e.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Deserialize world from JSON string.
        /// </summary>
        public static WorldSaveData DeserializeFromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError("JSON cannot be null or empty");
                return null;
            }

            try
            {
                WorldSaveData saveData = JsonUtility.FromJson<WorldSaveData>(json);

                if (saveData == null)
                {
                    Debug.LogError("Failed to deserialize save data from JSON");
                    return null;
                }

                if (!ValidateSaveData(saveData))
                {
                    Debug.LogError("Invalid save data from JSON");
                    return null;
                }

                return saveData;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to deserialize from JSON: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Read and decompress JSON from file.
        /// </summary>
        private static string ReadCompressedJson(string filePath)
        {
            try
            {
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzipStream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to read compressed JSON: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Validate save data structure and version.
        /// </summary>
        private static bool ValidateSaveData(WorldSaveData saveData)
        {
            if (saveData == null)
            {
                return false;
            }

            // Check required fields
            if (string.IsNullOrEmpty(saveData.saveId))
            {
                Debug.LogError("Save data missing saveId");
                return false;
            }

            if (saveData.timestamp <= 0)
            {
                Debug.LogError($"Invalid timestamp: {saveData.timestamp}");
                return false;
            }

            // Check version compatibility
            if (!IsVersionCompatible(saveData.saveVersion))
            {
                Debug.LogWarning($"Save version {saveData.saveVersion} may not be compatible");
                // Don't fail, just warn - future migration logic would handle this
            }

            // Validate bounds
            if (saveData.minX > saveData.maxX ||
                saveData.minY > saveData.maxY ||
                saveData.minZ > saveData.maxZ)
            {
                Debug.LogError($"Invalid bounds: min({saveData.minX},{saveData.minY},{saveData.minZ}) max({saveData.maxX},{saveData.maxY},{saveData.maxZ})");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check if save version is compatible with current version.
        /// </summary>
        private static bool IsVersionCompatible(string saveVersion)
        {
            if (string.IsNullOrEmpty(saveVersion))
            {
                return false;
            }

            // For now, accept any version starting with "1."
            // Future versions would implement proper version comparison
            return saveVersion.StartsWith("1.");
        }

        /// <summary>
        /// Get save metadata without loading full data.
        /// </summary>
        public static SaveMetadata GetSaveMetadata(string saveId)
        {
            if (string.IsNullOrEmpty(saveId))
            {
                return null;
            }

            try
            {
                string filePath = WorldSerializer.GetSaveFilePath(saveId);
                if (!File.Exists(filePath))
                {
                    return null;
                }

                // Read just enough to get metadata
                string json = ReadCompressedJson(filePath);
                if (string.IsNullOrEmpty(json))
                {
                    return null;
                }

                // Parse metadata from JSON
                var saveData = JsonUtility.FromJson<WorldSaveData>(json);
                if (saveData == null)
                {
                    return null;
                }

                return new SaveMetadata
                {
                    saveId = saveData.saveId,
                    timestamp = saveData.timestamp,
                    saveVersion = saveData.saveVersion,
                    gameVersion = saveData.gameVersion,
                    roomCount = saveData.rooms != null ? saveData.rooms.Length : 0,
                    fileSize = new FileInfo(filePath).Length
                };
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to get save metadata for {saveId}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Import save from uncompressed JSON file.
        /// </summary>
        public static WorldSaveData ImportFromJson(string importPath)
        {
            if (!File.Exists(importPath))
            {
                Debug.LogError($"Import file not found: {importPath}");
                return null;
            }

            try
            {
                string json = File.ReadAllText(importPath);
                return DeserializeFromJson(json);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to import save from {importPath}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Try to repair corrupted save file.
        /// </summary>
        public static bool RepairSave(string saveId)
        {
            try
            {
                string filePath = WorldSerializer.GetSaveFilePath(saveId);
                if (!File.Exists(filePath))
                {
                    return false;
                }

                // Try to read and validate
                string json = ReadCompressedJson(filePath);
                if (string.IsNullOrEmpty(json))
                {
                    return false;
                }

                // Try to deserialize
                WorldSaveData saveData = JsonUtility.FromJson<WorldSaveData>(json);
                if (saveData == null)
                {
                    return false;
                }

                // If we got here, the file is valid
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Cannot repair save {saveId}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if save file is corrupted.
        /// </summary>
        public static bool IsSaveCorrupted(string saveId)
        {
            try
            {
                string filePath = WorldSerializer.GetSaveFilePath(saveId);
                if (!File.Exists(filePath))
                {
                    return true;  // Missing file is considered corrupted
                }

                // Try to read header only
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzipStream))
                {
                    string firstLine = reader.ReadLine();
                    return string.IsNullOrEmpty(firstLine) || !firstLine.TrimStart().StartsWith("{");
                }
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Metadata about a save file.
    /// </summary>
    [Serializable]
    public class SaveMetadata
    {
        public string saveId;
        public long timestamp;
        public string saveVersion;
        public int gameVersion;
        public int roomCount;
        public long fileSize;

        /// <summary>
        /// Get display name for this save.
        /// </summary>
        public string GetDisplayName()
        {
            DateTime saveTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;
            return $"Save {saveId.Substring(0, 8)} - {saveTime:yyyy-MM-dd HH:mm}";
        }

        /// <summary>
        /// Get timestamp as DateTime.
        /// </summary>
        public DateTime GetDateTime()
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;
        }

        /// <summary>
        /// Get file size as formatted string.
        /// </summary>
        public string GetFileSizeString()
        {
            if (fileSize < 1024)
            {
                return $"{fileSize} B";
            }
            else if (fileSize < 1024 * 1024)
            {
                return $"{fileSize / 1024.0:F1} KB";
            }
            else
            {
                return $"{fileSize / (1024.0 * 1024.0):F1} MB";
            }
        }
    }
}
