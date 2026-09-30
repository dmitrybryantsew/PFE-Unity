using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PFE.Data.Definitions;
namespace PFE.Systems.Map.DataMigration.Editor
{
    /// <summary>
    /// Editor window for importing AS3 XML room data into Unity RoomTemplate assets.
    /// Provides batch import, preview, and configuration options.
    /// </summary>
    public class RoomTemplateImporterWindow : EditorWindow
    {
        // UI State
        private Vector2 scrollPosition;
        private string sourcePath = "";
        private string outputPath = "Assets/_PFE/Data/Resources/Rooms";
        private string gameDataPath = "";
        private AS3ObjectMapping objectMapping;
        private MapObjectCatalog objectCatalog;
        private AS3LandDefaultsDatabase landDefaults;
        private bool showPreview = true;
        private bool importInProgress = false;
        private float importProgress = 0f;
        private string importStatus = "";

        // Parsed data
        private List<AS3RoomCollection> parsedCollections = new List<AS3RoomCollection>();
        private List<AS3RoomData> allRooms = new List<AS3RoomData>();

        // Configuration
        private bool createFolders = true;
        private bool overwriteExisting = false;
        private bool removeStaleCollections = true;

        [MenuItem("PFE/Map/Room Template Importer", false, 10)]
        public static void ShowWindow()
        {
            RoomTemplateImporterWindow window = GetWindow<RoomTemplateImporterWindow>("Room Importer");
            window.minSize = new Vector2(600, 400);
            window.RefreshObjectMapping();
            window.RefreshObjectCatalog();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("AS3 Room Template Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Import AS3 XML room files into Unity RoomTemplate ScriptableObjects.\n" +
                "Supports batch import of multiple XML files.",
                MessageType.Info
            );

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            // Configuration Section
            DrawConfigurationSection();

            // Object Mapping Section
            DrawObjectMappingSection();

            // File Selection Section
            DrawFileSelectionSection();

            // Preview Section
            if (showPreview && allRooms.Count > 0)
            {
                DrawPreviewSection();
            }

            // Import Section
            DrawImportSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawConfigurationSection()
        {
            EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            outputPath = EditorGUILayout.TextField("Output Path", outputPath);
            EditorGUILayout.BeginHorizontal();
            gameDataPath = EditorGUILayout.TextField("GameData Path", gameDataPath);
            if (GUILayout.Button("Browse", GUILayout.Width(80)))
            {
                string selectedPath = EditorUtility.OpenFilePanel("Select GameData.as", gameDataPath, "as");
                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    gameDataPath = selectedPath;
                    RefreshLandDefaults();
                }
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Land Defaults Loaded", landDefaults != null && !landDefaults.IsEmpty);
            }

            createFolders = EditorGUILayout.Toggle("Create Type Folders", createFolders);
            overwriteExisting = EditorGUILayout.Toggle("Overwrite Existing", overwriteExisting);
            removeStaleCollections = EditorGUILayout.Toggle("Remove Stale Collections", removeStaleCollections);
            showPreview = EditorGUILayout.Toggle("Show Preview", showPreview);

            if (removeStaleCollections && createFolders)
            {
                EditorGUILayout.HelpBox(
                    "After importing, deletes any sub-folder of the output path that this import did not " +
                    "produce. Collections are keyed by land (rooms_rbl, rooms_surf, ...), so folders named " +
                    "after a source file (Camp, Base, Surf, ...) are from the older scheme; leaving them " +
                    "would keep both id schemes alive. Only directories are touched — loose assets such as " +
                    "DefaultAS3ObjectMapping.asset are left alone.",
                    MessageType.Info);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(10);
        }

        private void DrawObjectMappingSection()
        {
            EditorGUILayout.LabelField("Object Mapping", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (objectMapping != null)
            {
                EditorGUILayout.LabelField($"Current: {objectMapping.name}");
                EditorGUILayout.LabelField($"Mappings: {objectMapping.mappings.Count}");

                if (GUILayout.Button("Select Mapping Asset"))
                {
                    Selection.activeObject = objectMapping;
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "No AS3ObjectMapping assigned.\n" +
                    "Create or select one to properly map AS3 objects to Unity prefabs.",
                    MessageType.Warning
                );

                if (GUILayout.Button("Create Default Mapping"))
                {
                    CreateDefaultObjectMapping();
                }

                if (GUILayout.Button("Find Existing Mapping"))
                {
                    FindObjectMappingAsset();
                }
            }

            EditorGUILayout.Space(6);

            if (objectCatalog != null)
            {
                EditorGUILayout.LabelField($"Definition Catalog: {objectCatalog.name}");
                EditorGUILayout.LabelField($"Definitions: {objectCatalog.Count}");

                if (GUILayout.Button("Select Definition Catalog"))
                {
                    Selection.activeObject = objectCatalog;
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "No MapObjectCatalog found. Room import will fall back to legacy heuristics until definitions are imported.",
                    MessageType.Info
                );

                if (GUILayout.Button("Find Definition Catalog"))
                {
                    FindObjectCatalogAsset();
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(10);
        }

        private void DrawFileSelectionSection()
        {
            EditorGUILayout.LabelField("Source Files", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            sourcePath = EditorGUILayout.TextField("XML Folder Path", sourcePath);
            if (GUILayout.Button("Browse", GUILayout.Width(80)))
            {
                sourcePath = EditorUtility.OpenFolderPanel("Select XML Folder", sourcePath, "");
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Load XML Files"))
            {
                AutoDetectGameDataPath();
                LoadXmlFiles();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(10);
        }

        private void DrawPreviewSection()
        {
            EditorGUILayout.LabelField($"Preview ({allRooms.Count} rooms)", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            foreach (AS3RoomData room in allRooms)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(room.name, EditorStyles.boldLabel);
                // z is printed only when it is set. The z=0 rooms are the overwhelming majority and a
                // permanent third number would be noise, while a z=1 room's layer is the entire point of
                // its coordinate — printing "Position: (0, 0)" for room_0_0 and room_0_0_1 alike is how a
                // z=1 room went through an import unnoticed.
                EditorGUILayout.LabelField(room.z != 0
                    ? $"Position: ({room.x}, {room.y}, z={room.z})"
                    : $"Position: ({room.x}, {room.y})");
                // The land, not the file. One file declares up to eight of them, and the same room id
                // recurs across lands — without this the preview cannot show that room_0_0 appears three
                // times in RoomsCamp.as, which is what made the collision invisible.
                EditorGUILayout.LabelField($"Collection: {room.sourceCollectionId}");
                EditorGUILayout.LabelField($"Tiles: {room.tileLayers.Count} rows");
                EditorGUILayout.LabelField($"Objects: {room.objects.Count}");
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(10);
        }

        private void DrawImportSection()
        {
            EditorGUILayout.LabelField("Import", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (importInProgress)
            {
                EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 20), importProgress, importStatus);
                Repaint();
            }
            else
            {
                bool canImport = allRooms.Count > 0 && objectMapping != null;

                using (new EditorGUI.DisabledScope(!canImport))
                {
                    if (GUILayout.Button("Import All Rooms", GUILayout.Height(30)))
                    {
                        StartImport();
                    }
                }

                if (!canImport)
                {
                    EditorGUILayout.HelpBox(
                        allRooms.Count == 0
                            ? "Load XML files first."
                            : "Select object mapping first.",
                        MessageType.Warning
                    );
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void LoadXmlFiles()
        {
            if (string.IsNullOrEmpty(sourcePath) || !Directory.Exists(sourcePath))
            {
                EditorUtility.DisplayDialog("Error", "Invalid folder path.", "OK");
                return;
            }

            parsedCollections.Clear();
            allRooms.Clear();
            RefreshLandDefaults();

            AS3RoomParser parser = new AS3RoomParser();
            string[] xmlFiles = Directory.GetFiles(sourcePath, "*.*", SearchOption.TopDirectoryOnly)
    .Where(f => f.EndsWith(".xml") || f.EndsWith(".as"))
    .ToArray();

            foreach (string file in xmlFiles)
            {
                string lowerName = Path.GetFileName(file).ToLowerInvariant();
                if (!lowerName.Contains("room") && !lowerName.Contains("Room") && !lowerName.Contains("layout"))
                {
                    continue;
                }

                AS3RoomCollection collection = parser.ParseFile(file);
                if (collection.rooms.Count > 0)
                {
                    parsedCollections.Add(collection);
                    allRooms.AddRange(collection.rooms);
                }
            }

            Debug.Log($"[RoomImporter] Loaded {allRooms.Count} rooms from {parsedCollections.Count} files");
            Repaint();
        }

        private void StartImport()
        {
            if (objectMapping == null)
            {
                EditorUtility.DisplayDialog("Error", "No object mapping assigned.", "OK");
                return;
            }

            if (parsedCollections.Count == 0)
            {
                EditorUtility.DisplayDialog("Error", "No rooms loaded.", "OK");
                return;
            }

            importInProgress = true;
            importProgress = 0f;

            AS3ToUnityConverter converter = new AS3ToUnityConverter(objectMapping, landDefaults, objectCatalog);

            if (!Directory.Exists(outputPath))
                Directory.CreateDirectory(outputPath);

            int imported = 0;
            int failed = 0;

            int totalRooms = parsedCollections.Sum(c => c.rooms.Count);
            int processed = 0;

            // A source file holds one XML field per *land*, and room ids repeat across lands: RoomsCamp.as
            // declares rooms_rbl, rooms_covert and rooms_src, and all three define room_0_0. Foldering by
            // source file therefore dropped all three into Camp/ and let the last land overwrite the others
            // — that silently cost 76 of 639 rooms (Base 3, Camp 4, Serial 26, Serial2 43). Folder by
            // collection id (the land) instead, and keep the file name only as a fallback for a block the
            // parser could not name.
            HashSet<string> createdFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> writtenAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> importedCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int duplicatePaths = 0;

            foreach (var collection in parsedCollections)
            {
                string fileFolder = Path.GetFileNameWithoutExtension(collection.sourceFile).Replace("Rooms", "");
                if (string.IsNullOrEmpty(fileFolder))
                    fileFolder = "Base";

                foreach (var roomData in collection.rooms)
                {
                    string subfolder = string.IsNullOrWhiteSpace(roomData.sourceCollectionId)
                        ? fileFolder
                        : roomData.sourceCollectionId;

                    importedCollections.Add(subfolder);

                    string folderPath = createFolders
                        ? Path.Combine(outputPath, subfolder)
                        : outputPath;

                    if (createFolders && createdFolders.Add(folderPath) && !Directory.Exists(folderPath))
                        Directory.CreateDirectory(folderPath);

                    importProgress = (float)processed / totalRooms;
                    importStatus = $"Importing {roomData.name}...";
                    processed++;

                    try
                    {
                        RoomTemplate template = converter.ConvertRoom(roomData);

                        string assetName = string.IsNullOrEmpty(template.id)
                            ? roomData.name
                            : template.id;

                        template.name = assetName;

                        string assetPath = Path.Combine(folderPath, $"{assetName}.asset")
                            .Replace('\\', '/');

                        if (!writtenAssetPaths.Add(assetPath))
                        {
                            // Two rooms in one collection share an id, so the second clobbers the first.
                            // A silent last-wins is exactly how the camp's main room was lost; name it.
                            Debug.LogWarning(
                                $"[RoomImporter] Duplicate asset path '{assetPath}': room '{roomData.name}' " +
                                $"(collection '{roomData.sourceCollectionId}') overwrites an earlier room.");
                            duplicatePaths++;
                        }

                        if (File.Exists(assetPath) && !overwriteExisting)
                        {
                            Debug.LogWarning($"[RoomImporter] Skipped {assetPath} (already exists)");
                            failed++;
                            continue;
                        }

                        if (File.Exists(assetPath))
                        {
                            RoomTemplate existing = AssetDatabase.LoadAssetAtPath<RoomTemplate>(assetPath);
                            EditorUtility.CopySerialized(template, existing);
                        }
                        else
                        {
                            AssetDatabase.CreateAsset(template, assetPath);
                        }

                        imported++;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"[RoomImporter] Failed to import {roomData.name}: {ex.Message}");
                        failed++;
                    }

                    if (processed % 5 == 0)
                        Repaint();
                }
            }

            // Collections are keyed by land, so the folders from the older file-named scheme (Camp, Base,
            // Serial, Surf, ...) are not produced by this import and would keep both id schemes alive —
            // Camp/room_0_0 (id "Camp") sitting next to rooms_rbl/room_0_0 (id "rooms_rbl"). Remove the
            // stale ones. Directories only: loose assets under the output path are none of our business.
            int removedCollections = 0;
            if (removeStaleCollections && createFolders && Directory.Exists(outputPath))
            {
                foreach (string directory in Directory.GetDirectories(outputPath))
                {
                    string collectionName = Path.GetFileName(directory);
                    if (string.IsNullOrEmpty(collectionName) || importedCollections.Contains(collectionName))
                        continue;

                    string assetPath = directory.Replace('\\', '/');
                    if (AssetDatabase.DeleteAsset(assetPath))
                    {
                        Debug.LogWarning(
                            $"[RoomImporter] Removed stale collection folder '{assetPath}' — no land in this " +
                            "import produces it.");
                        removedCollections++;
                    }
                    else
                    {
                        Debug.LogWarning($"[RoomImporter] Could not remove stale collection folder '{assetPath}'.");
                    }
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            importProgress = 1f;
            importStatus = duplicatePaths == 0
                ? $"Imported {imported} rooms, {failed} failed."
                : $"Imported {imported} rooms, {failed} failed, {duplicatePaths} duplicate asset paths.";

            EditorUtility.DisplayDialog(
                "Import Complete",
                duplicatePaths == 0
                    ? $"Successfully imported {imported} rooms.\n{failed} failed." +
                      (removedCollections > 0 ? $"\nRemoved {removedCollections} stale collection folder(s)." : "")
                    : $"Successfully imported {imported} rooms.\n{failed} failed.\n\n" +
                      $"WARNING: {duplicatePaths} room(s) shared an asset path and overwrote an earlier " +
                      "room. See the Console — the import is lossy.",
                "OK"
            );

            Debug.Log($"[RoomImporter] Import complete: {imported} imported, {failed} failed");

            importInProgress = false;
            Repaint();
        }
        
        private void RefreshObjectMapping()
        {
            FindObjectMappingAsset();
        }

        private void RefreshObjectCatalog()
        {
            FindObjectCatalogAsset();
        }

        private void AutoDetectGameDataPath()
        {
            if (!string.IsNullOrWhiteSpace(gameDataPath) && File.Exists(gameDataPath))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
            {
                return;
            }

            DirectoryInfo current = new DirectoryInfo(sourcePath);
            while (current != null)
            {
                string candidate = Path.Combine(current.FullName, "GameData.as");
                if (File.Exists(candidate))
                {
                    gameDataPath = candidate;
                    return;
                }

                current = current.Parent;
            }
        }

        private void RefreshLandDefaults()
        {
            landDefaults = AS3LandDefaultsDatabase.ParseFromFile(gameDataPath);
        }

        private void FindObjectMappingAsset()
        {
            string[] guids = AssetDatabase.FindAssets($"t:{nameof(AS3ObjectMapping)}");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                objectMapping = AssetDatabase.LoadAssetAtPath<AS3ObjectMapping>(path);
                Repaint();
            }
        }

        private void FindObjectCatalogAsset()
        {
            string[] guids = AssetDatabase.FindAssets($"t:{nameof(MapObjectCatalog)}");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                objectCatalog = AssetDatabase.LoadAssetAtPath<MapObjectCatalog>(path);
                Repaint();
            }
        }

        private void CreateDefaultObjectMapping()
        {
            AS3ObjectMapping mapping = ScriptableObject.CreateInstance<AS3ObjectMapping>();
            mapping.name = "DefaultAS3ObjectMapping";

            // Add common PFE object mappings
            mapping.mappings = AS3DefaultObjectMappings.GetDefaultMappings();

            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            string assetPath = Path.Combine(outputPath, "DefaultAS3ObjectMapping.asset").Replace('\\', '/');
            AssetDatabase.CreateAsset(mapping, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            objectMapping = mapping;
            Debug.Log($"[RoomImporter] Created default object mapping at {assetPath}");
            Repaint();
        }
    }
}
