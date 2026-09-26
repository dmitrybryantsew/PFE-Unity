using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.DataMigration;

namespace PFE.Editor.Importers
{
    public static class SurfRoomImporter
    {
        private const string RoomsAsPath = "E:/Games/UnityGames/pfeToUnity/pfe/scripts/fe/rooms/Rooms.as";
        private const string GameDataAsPath = "E:/Games/UnityGames/pfeToUnity/pfe/scripts/fe/GameData.as";
        private const string SurfOutputDir = "Assets/_PFE/Data/Resources/Rooms/Surf";
        private const string BaseDir = "Assets/_PFE/Data/Resources/Rooms/Base";

        [InitializeOnLoadMethod]
        private static void AutoImportIfMissing()
        {
            if (!Directory.Exists(SurfOutputDir) || Directory.GetFiles(SurfOutputDir, "*.asset").Length < 20)
            {
                EditorApplication.delayCall += () =>
                {
                    ImportSurfRooms();
                };
            }
        }

        [MenuItem("PFE/Map/Import Surf (Wasteland) Rooms", false, 25)]
        public static void ImportSurfRooms()
        {
            if (!File.Exists(RoomsAsPath))
            {
                Debug.LogError($"[SurfRoomImporter] Could not find Rooms.as at {RoomsAsPath}");
                return;
            }

            Debug.Log("[SurfRoomImporter] Starting extraction and import of Surf (Wasteland) rooms...");

            if (!Directory.Exists(SurfOutputDir))
            {
                Directory.CreateDirectory(SurfOutputDir);
            }

            string content = File.ReadAllText(RoomsAsPath);
            Match match = Regex.Match(
                content,
                @"(?:internal|public)\s+var\s+rooms_surf\s*:\s*XML\s*=\s*(?<xml><all>[\s\S]*?<\/all>)\s*;",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                Debug.LogError("[SurfRoomImporter] Could not find 'rooms_surf' XML block in Rooms.as");
                return;
            }

            string xml = match.Groups["xml"].Value;
            var parser = new AS3RoomParser();
            var collection = parser.ParseXmlString(xml);

            if (collection == null || collection.rooms.Count == 0)
            {
                Debug.LogError("[SurfRoomImporter] Failed to parse any rooms from rooms_surf XML");
                return;
            }

            // Load mapping, catalog, and land defaults
            AS3ObjectMapping mapping = null;
            string[] mappingGuids = AssetDatabase.FindAssets($"t:{nameof(AS3ObjectMapping)}");
            if (mappingGuids.Length > 0)
            {
                string mPath = AssetDatabase.GUIDToAssetPath(mappingGuids[0]);
                mapping = AssetDatabase.LoadAssetAtPath<AS3ObjectMapping>(mPath);
            }

            MapObjectCatalog catalog = null;
            string[] catGuids = AssetDatabase.FindAssets($"t:{nameof(MapObjectCatalog)}");
            if (catGuids.Length > 0)
            {
                string cPath = AssetDatabase.GUIDToAssetPath(catGuids[0]);
                catalog = AssetDatabase.LoadAssetAtPath<MapObjectCatalog>(cPath);
            }

            AS3LandDefaultsDatabase landDefaults = null;
            if (File.Exists(GameDataAsPath))
            {
                landDefaults = AS3LandDefaultsDatabase.ParseFromFile(GameDataAsPath);
            }

            var converter = new AS3ToUnityConverter(mapping, landDefaults, catalog);
            int importedCount = 0;

            foreach (var as3Room in collection.rooms)
            {
                as3Room.sourceCollectionId = "Surf";
                RoomTemplate template = converter.ConvertRoom(as3Room);
                template.sourceCollectionId = "Surf";
                template.name = as3Room.name;

                string assetPath = $"{SurfOutputDir}/{as3Room.name}.asset";

                if (File.Exists(assetPath))
                {
                    RoomTemplate existing = AssetDatabase.LoadAssetAtPath<RoomTemplate>(assetPath);
                    if (existing != null)
                    {
                        EditorUtility.CopySerialized(template, existing);
                        EditorUtility.SetDirty(existing);
                    }
                    else
                    {
                        AssetDatabase.CreateAsset(template, assetPath);
                    }
                }
                else
                {
                    AssetDatabase.CreateAsset(template, assetPath);
                }

                importedCount++;
            }

            // Clean up the 13 misallocated rooms (x >= 5) from Base/
            string[] misplacedRooms = new string[]
            {
                "room_5_1", "room_6_1", "room_6_2", "room_7_1", "room_8_1",
                "room_9_1", "room_9_2", "room_9_3", "room_10_1", "room_10_2",
                "room_10_3", "room_11_1", "room_12_1"
            };

            int cleanedCount = 0;
            foreach (var rName in misplacedRooms)
            {
                string basePath = $"{BaseDir}/{rName}.asset";
                if (File.Exists(basePath))
                {
                    AssetDatabase.DeleteAsset(basePath);
                    cleanedCount++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[SurfRoomImporter] Successfully imported {importedCount} rooms into '{SurfOutputDir}' and cleaned {cleanedCount} misplaced rooms from '{BaseDir}'.");
        }
    }
}
