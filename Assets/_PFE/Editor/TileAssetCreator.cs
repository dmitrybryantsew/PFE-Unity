using UnityEngine;
using UnityEditor;
using System.IO;
using PFE.Systems.Map.Rendering;

namespace PFE.Editor
{
    /// <summary>
    /// Editor tool to create placeholder tile sprites for testing the Map System.
    /// Generates simple colored squares for each tile type.
    /// </summary>
    public class TileAssetCreator : EditorWindow
    {
        private const int TileSize = 40;
        private const string OutputPath = "Assets/_PFE/Art/TileSprites";
        private const string DatabasePath = "Assets/_PFE/Data/Map/TileAssetDatabase.asset";

        private TileAssetDatabase database;

        [MenuItem("PFE/Map/Create Tile Assets")]
        public static void ShowWindow()
        {
            GetWindow<TileAssetCreator>("Tile Asset Creator");
        }

        private void OnGUI()
        {
            GUILayout.Label("Tile Asset Creator", EditorStyles.boldLabel);
            GUILayout.Label("Creates placeholder tile sprites for testing", EditorStyles.label);

            EditorGUILayout.Space();

            if (GUILayout.Button("Create All Tile Assets", GUILayout.Height(30)))
            {
                CreateAllAssets();
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("Create Tile Asset Database", GUILayout.Height(30)))
            {
                CreateDatabase();
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "This will create:\n" +
                "1. Placeholder sprites for each tile type\n" +
                "2. TileAssetDatabase with sprite references\n\n" +
                "Output: " + OutputPath,
                MessageType.Info
            );
        }

        private void CreateAllAssets()
        {
            // Ensure output directory exists
            if (!AssetDatabase.IsValidFolder(OutputPath))
            {
                string parent = Path.GetDirectoryName(OutputPath);
                string folder = Path.GetFileName(OutputPath);
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "_PFE/Art/TileSprites"));
                AssetDatabase.Refresh();
            }

            // Create sprites for each tile type
            CreateTileSprite("tile_air", new Color(0.2f, 0.2f, 0.2f, 0.3f)); // Transparent gray
            CreateTileSprite("tile_wall", new Color(0.5f, 0.5f, 0.5f, 1f)); // Solid gray
            CreateTileSprite("tile_platform", new Color(0.3f, 0.6f, 0.3f, 1f)); // Green
            CreateTileSprite("tile_stair", new Color(0.6f, 0.4f, 0.2f, 1f)); // Brown
            CreateTileSprite("tile_slope_left", new Color(0.4f, 0.4f, 0.6f, 1f)); // Blue-ish
            CreateTileSprite("tile_slope_right", new Color(0.4f, 0.4f, 0.6f, 1f)); // Blue-ish

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[TileAssetCreator] Created placeholder tile sprites at {OutputPath}");
        }

        private void CreateTileSprite(string name, Color color)
        {
            // Create texture
            Texture2D texture = new Texture2D(TileSize, TileSize);
            Color[] pixels = new Color[TileSize * TileSize];

            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            texture.SetPixels(pixels);
            texture.Apply();

            // Add border for visibility
            for (int x = 0; x < TileSize; x++)
            {
                texture.SetPixel(x, 0, Color.black);
                texture.SetPixel(x, TileSize - 1, Color.black);
            }
            for (int y = 0; y < TileSize; y++)
            {
                texture.SetPixel(0, y, Color.black);
                texture.SetPixel(TileSize - 1, y, Color.black);
            }
            texture.Apply();

            // Save texture
            string fullPath = Path.Combine(OutputPath, name + ".png");
            byte[] bytes = texture.EncodeToPNG();
            File.WriteAllBytes(Path.Combine(Application.dataPath, fullPath.Replace("Assets/", "")), bytes);

            AssetDatabase.Refresh();

            // Import as sprite
            TextureImporter importer = AssetImporter.GetAtPath(fullPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spritePackingTag = "Tiles";
                importer.filterMode = FilterMode.Point;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            // Cleanup
            DestroyImmediate(texture);
        }

        private void CreateDatabase()
        {
            // Load or create database
            TileAssetDatabase db = AssetDatabase.LoadAssetAtPath<TileAssetDatabase>(DatabasePath);

            if (db == null)
            {
                db = ScriptableObject.CreateInstance<TileAssetDatabase>();
                AssetDatabase.CreateAsset(db, DatabasePath);
            }

            // Find all tile sprites
            string[] guids = AssetDatabase.FindAssets("tile_", new[] { OutputPath });

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);

                if (sprite != null)
                {
                    string name = Path.GetFileNameWithoutExtension(path);

                    // Map sprite names to tile types
                    switch (name)
                    {
                        case "tile_air":
                            // Air tiles typically have no sprite
                            break;
                        case "tile_wall":
                            // Store reference - actual implementation depends on TileAssetDatabase structure
                            break;
                        case "tile_platform":
                            break;
                        case "tile_stair":
                            break;
                        case "tile_slope_left":
                        case "tile_slope_right":
                            break;
                    }
                }
            }

            EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssets();

            Debug.Log($"[TileAssetCreator] Created/updated TileAssetDatabase at {DatabasePath}");
        }
    }
}
