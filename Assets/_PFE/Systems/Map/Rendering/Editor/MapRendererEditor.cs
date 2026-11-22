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
            mapRendererObj.AddComponent<RoomVisualController>();

            // Add MapBridge
            mapRendererObj.AddComponent<MapBridge>();

            // Register undo
            Undo.RegisterCreatedObjectUndo(mapRendererObj, "Create Map Renderer");

            // Select the new object
            Selection.activeGameObject = mapRendererObj;

            EditorUtility.DisplayDialog("MapRenderer Created",
                "MapRenderer has been created!\n\n" +
                "The GameLifetimeScope will automatically inject the GameManager.",
                "OK");

            Debug.Log("[MapRendererEditor] Created MapRenderer GameObject with MapBridge and RoomVisualController");
        }
    }
}
#endif // UNITY_EDITOR
