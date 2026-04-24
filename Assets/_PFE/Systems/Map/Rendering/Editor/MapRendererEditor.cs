#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using PFE.Systems.Map.Rendering;
using PFE.Data;

namespace PFE.Systems.Map.Editor
{
    /// <summary>
    /// Editor tools for setting up the MapRenderer in the scene.
    /// </summary>
    public static class MapRendererEditor
    {
        [MenuItem("PFE/Map/Create MapRenderer", false, 10)]
        public static void CreateMapRenderer()
        {
            // Check if already exists
            MapBridge existingBridge = Object.FindFirstObjectByType<MapBridge>();
            if (existingBridge != null)
            {
                EditorUtility.DisplayDialog("MapRenderer Exists", 
                    "MapRenderer already exists in the scene!", "OK");
                Selection.activeGameObject = existingBridge.gameObject;
                return;
            }

            // Create MapRenderer GameObject
            GameObject mapRendererObj = new GameObject("MapRenderer");
            mapRendererObj.transform.position = Vector3.zero;

            // Add RoomVisualController
            RoomVisualController visualController = mapRendererObj.AddComponent<RoomVisualController>();

            // Add MapBridge
            MapBridge mapBridge = mapRendererObj.AddComponent<MapBridge>();

            // Try to find and assign TileAssetDatabase
            TileAssetDatabase tileDb = FindTileAssetDatabase();
            if (tileDb != null)
            {
                // Use reflection to set private field
                var field = typeof(MapBridge).GetField("_tileDatabase", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(mapBridge, tileDb);
                }
            }

            // Register undo
            Undo.RegisterCreatedObjectUndo(mapRendererObj, "Create MapRenderer");

            // Select the new object
            Selection.activeGameObject = mapRendererObj;

            EditorUtility.DisplayDialog("MapRenderer Created", 
                "MapRenderer has been created!\n\n" +
                "The GameLifetimeScope will automatically inject the GameManager.\n" +
                (tileDb == null ? "\nWARNING: No TileAssetDatabase found. Please create one in Assets/_PFE/Data/Map/" : ""),
                "OK");

            Debug.Log("[MapRendererEditor] Created MapRenderer GameObject with MapBridge and RoomVisualController");
        }

        [MenuItem("PFE/Map/Find TileAssetDatabase", false, 11)]
        public static void FindAndSelectTileDatabase()
        {
            TileAssetDatabase tileDb = FindTileAssetDatabase();
            if (tileDb != null)
            {
                Selection.activeObject = tileDb;
                EditorUtility.DisplayDialog("TileAssetDatabase Found", 
                    $"Found: {tileDb.name}\nPath: {AssetDatabase.GetAssetPath(tileDb)}", "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("TileAssetDatabase Not Found", 
                    "No TileAssetDatabase found in project.\n\n" +
                    "Create one at: Assets/_PFE/Data/Map/TileAssetDatabase.asset", "OK");
            }
        }

        private static TileAssetDatabase FindTileAssetDatabase()
        {
            // Look for existing database
            string[] guids = AssetDatabase.FindAssets("t:TileAssetDatabase");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<TileAssetDatabase>(path);
            }

            // Check specific path
            TileAssetDatabase tileDb = AssetDatabase.LoadAssetAtPath<TileAssetDatabase>(
                "Assets/_PFE/Data/Map/TileAssetDatabase.asset");
            
            return tileDb;
        }
    }
}
#endif // UNITY_EDITOR
