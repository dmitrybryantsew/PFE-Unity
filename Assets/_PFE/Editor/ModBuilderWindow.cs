#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using PFE.ModAPI;

namespace PFE.Editor
{
    /// <summary>
    /// Editor window for building mod content packs.
    /// Lets you configure a manifest, select ScriptableObjects to include,
    /// and build an AssetBundle + manifest.json ready for distribution.
    ///
    /// Menu: PFE > Modding > Mod Builder
    /// </summary>
    public class ModBuilderWindow : EditorWindow
    {
        // Manifest fields
        string _modId = "author.mymod";
        string _displayName = "My Mod";
        string _version = "1.0.0";
        string _author = "Author";
        string _description = "";
        string _targetGameVersion = "0.1.0";
        int _contentSchemaVersion = 1;
        int _loadOrder = 100;
        bool _isCosmeticOnly;
        int _multiplayerPolicy; // 0=RequireMatch, 1=HostOnly, 2=ClientLocal

        // Content selection
        List<ScriptableObject> _selectedAssets = new();
        List<string> _overrides = new();
        string _newOverride = "";

        // Build settings
        string _outputFolder = "";
        BuildTarget _buildTarget = BuildTarget.StandaloneWindows64;

        // UI state
        Vector2 _scrollPos;
        Vector2 _assetScrollPos;
        bool _showManifest = true;
        bool _showContent = true;
        bool _showOverrides;
        bool _showBuild = true;
        string _lastBuildResult = "";

        [MenuItem("PFE/Modding/Mod Builder")]
        public static void ShowWindow()
        {
            var window = GetWindow<ModBuilderWindow>("Mod Builder");
            window.minSize = new Vector2(500, 600);
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(_outputFolder))
                _outputFolder = Path.Combine(Application.persistentDataPath, "Mods");
        }

        void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawManifestSection();
            EditorGUILayout.Space(10);
            DrawContentSection();
            EditorGUILayout.Space(10);
            DrawOverridesSection();
            EditorGUILayout.Space(10);
            DrawBuildSection();

            if (!string.IsNullOrEmpty(_lastBuildResult))
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.HelpBox(_lastBuildResult, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        #region Manifest Section

        void DrawManifestSection()
        {
            _showManifest = EditorGUILayout.BeginFoldoutHeaderGroup(_showManifest, "Manifest");
            if (!_showManifest) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            _modId = EditorGUILayout.TextField("Mod ID", _modId);
            _displayName = EditorGUILayout.TextField("Display Name", _displayName);
            _version = EditorGUILayout.TextField("Version", _version);
            _author = EditorGUILayout.TextField("Author", _author);
            _description = EditorGUILayout.TextField("Description", _description);

            EditorGUILayout.Space(5);
            _targetGameVersion = EditorGUILayout.TextField("Target Game Version", _targetGameVersion);
            _contentSchemaVersion = EditorGUILayout.IntField("Schema Version", _contentSchemaVersion);
            _loadOrder = EditorGUILayout.IntField("Load Order", _loadOrder);
            _isCosmeticOnly = EditorGUILayout.Toggle("Cosmetic Only", _isCosmeticOnly);
            _multiplayerPolicy = EditorGUILayout.Popup("Multiplayer Policy",
                _multiplayerPolicy, new[] { "Require Match", "Host Only", "Client Local" });

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        #endregion

        #region Content Section

        void DrawContentSection()
        {
            _showContent = EditorGUILayout.BeginFoldoutHeaderGroup(_showContent,
                $"Content ({_selectedAssets.Count} assets)");
            if (!_showContent) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Drop area
            var dropRect = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "Drag & Drop ScriptableObjects here", EditorStyles.helpBox);
            HandleDragAndDrop(dropRect);

            // Add from selection button
            if (GUILayout.Button("Add Selected Assets"))
            {
                AddSelectedAssets();
            }

            // List selected assets
            if (_selectedAssets.Count > 0)
            {
                EditorGUILayout.Space(5);
                _assetScrollPos = EditorGUILayout.BeginScrollView(_assetScrollPos,
                    GUILayout.MaxHeight(200));

                for (int i = _selectedAssets.Count - 1; i >= 0; i--)
                {
                    EditorGUILayout.BeginHorizontal();

                    var asset = _selectedAssets[i];
                    if (asset == null)
                    {
                        _selectedAssets.RemoveAt(i);
                        EditorGUILayout.EndHorizontal();
                        continue;
                    }

                    // Show type icon and info
                    string typeName = asset.GetType().Name;
                    string contentInfo = "";
                    if (asset is IGameContent gc)
                        contentInfo = $" [{gc.ContentType}/{gc.ContentId}]";

                    EditorGUILayout.ObjectField(asset, asset.GetType(), false);
                    EditorGUILayout.LabelField($"{typeName}{contentInfo}", GUILayout.Width(200));

                    if (GUILayout.Button("X", GUILayout.Width(20)))
                    {
                        _selectedAssets.RemoveAt(i);
                    }

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndScrollView();

                EditorGUILayout.Space(5);
                if (GUILayout.Button("Clear All"))
                    _selectedAssets.Clear();
            }

            // Validation
            int nonContentCount = _selectedAssets.Count(a => a is not IGameContent);
            if (nonContentCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{nonContentCount} asset(s) don't implement IGameContent and will be skipped.",
                    MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        void HandleDragAndDrop(Rect dropRect)
        {
            var evt = Event.current;
            if (!dropRect.Contains(evt.mousePosition)) return;

            if (evt.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                evt.Use();
            }
            else if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is ScriptableObject so && !_selectedAssets.Contains(so))
                        _selectedAssets.Add(so);
                }
                evt.Use();
            }
        }

        void AddSelectedAssets()
        {
            foreach (var obj in Selection.objects)
            {
                if (obj is ScriptableObject so && !_selectedAssets.Contains(so))
                    _selectedAssets.Add(so);
            }
        }

        #endregion

        #region Overrides Section

        void DrawOverridesSection()
        {
            _showOverrides = EditorGUILayout.BeginFoldoutHeaderGroup(_showOverrides,
                $"Overrides ({_overrides.Count})");
            if (!_showOverrides) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.HelpBox(
                "Declare content IDs this mod overrides from the base game.\nFormat: ContentType/ContentId (e.g. Weapon/rifle)",
                MessageType.Info);

            // Add override
            EditorGUILayout.BeginHorizontal();
            _newOverride = EditorGUILayout.TextField(_newOverride);
            if (GUILayout.Button("Add", GUILayout.Width(50)) && !string.IsNullOrEmpty(_newOverride))
            {
                if (!_overrides.Contains(_newOverride))
                    _overrides.Add(_newOverride);
                _newOverride = "";
            }
            EditorGUILayout.EndHorizontal();

            // Auto-detect from selected assets
            if (GUILayout.Button("Auto-detect from content"))
            {
                AutoDetectOverrides();
            }

            // List
            for (int i = _overrides.Count - 1; i >= 0; i--)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(_overrides[i]);
                if (GUILayout.Button("X", GUILayout.Width(20)))
                    _overrides.RemoveAt(i);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        void AutoDetectOverrides()
        {
            // Check which content IDs in this mod match base game Resources
            foreach (var asset in _selectedAssets)
            {
                if (asset is not IGameContent gc) continue;
                string key = $"{gc.ContentType}/{gc.ContentId}";

                // Check if base game has this ID by looking in Resources
                bool existsInBase = CheckBaseGameHasContent(gc);
                if (existsInBase && !_overrides.Contains(key))
                {
                    _overrides.Add(key);
                    Debug.Log($"[ModBuilder] Auto-detected override: {key}");
                }
            }
        }

        bool CheckBaseGameHasContent(IGameContent content)
        {
            // Quick check: try to find a matching asset in Resources
            string resourcePath = content.ContentType switch
            {
                ContentType.Unit => "Units",
                ContentType.Weapon => "Weapons",
                ContentType.RoomTemplate => "Rooms",
                ContentType.CharacterAnimation => "Characters",
                ContentType.Item => "Items",
                ContentType.Ammo => "Ammo",
                ContentType.Perk => "Perks",
                ContentType.Effect => "Effects",
                ContentType.Skill => "Skills",
                _ => ""
            };

            if (string.IsNullOrEmpty(resourcePath)) return false;

            var all = Resources.LoadAll<ScriptableObject>(resourcePath);
            return all.Any(a => a is IGameContent gc && gc.ContentId == content.ContentId);
        }

        #endregion

        #region Build Section

        void DrawBuildSection()
        {
            _showBuild = EditorGUILayout.BeginFoldoutHeaderGroup(_showBuild, "Build");
            if (!_showBuild) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Output folder
            EditorGUILayout.BeginHorizontal();
            _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string selected = EditorUtility.OpenFolderPanel("Select Mods Output Folder", _outputFolder, "");
                if (!string.IsNullOrEmpty(selected))
                    _outputFolder = selected;
            }
            EditorGUILayout.EndHorizontal();

            _buildTarget = (BuildTarget)EditorGUILayout.EnumPopup("Build Target", _buildTarget);

            EditorGUILayout.Space(5);

            // Validation
            var errors = ValidateBuild();
            if (errors.Count > 0)
            {
                foreach (var err in errors)
                    EditorGUILayout.HelpBox(err, MessageType.Error);

                GUI.enabled = false;
            }

            if (GUILayout.Button("Build Mod", GUILayout.Height(30)))
            {
                BuildMod();
            }

            GUI.enabled = true;

            EditorGUILayout.Space(5);
            if (GUILayout.Button("Open Output Folder"))
            {
                string modFolder = Path.Combine(_outputFolder, _modId);
                if (Directory.Exists(modFolder))
                    EditorUtility.RevealInFinder(modFolder);
                else if (Directory.Exists(_outputFolder))
                    EditorUtility.RevealInFinder(_outputFolder);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        List<string> ValidateBuild()
        {
            var errors = new List<string>();

            if (string.IsNullOrEmpty(_modId) || !_modId.Contains('.'))
                errors.Add("Mod ID must be in 'author.modname' format (contain a dot)");
            if (_modId == "pfe.base")
                errors.Add("Cannot use reserved mod ID 'pfe.base'");
            if (string.IsNullOrEmpty(_displayName))
                errors.Add("Display Name is required");
            if (string.IsNullOrEmpty(_version))
                errors.Add("Version is required");
            if (_selectedAssets.Count == 0)
                errors.Add("No content assets selected");
            if (string.IsNullOrEmpty(_outputFolder))
                errors.Add("Output folder is required");

            int validContent = _selectedAssets.Count(a => a is IGameContent);
            if (validContent == 0 && _selectedAssets.Count > 0)
                errors.Add("None of the selected assets implement IGameContent");

            return errors;
        }

        void BuildMod()
        {
            try
            {
                _lastBuildResult = "";

                // Create mod folder
                string modFolder = Path.Combine(_outputFolder, _modId);
                string bundlesFolder = Path.Combine(modFolder, "bundles");
                Directory.CreateDirectory(bundlesFolder);

                // 1. Write manifest.json
                var manifest = new ModManifest
                {
                    modId = _modId,
                    displayName = _displayName,
                    version = _version,
                    author = _author,
                    description = _description,
                    targetGameVersion = _targetGameVersion,
                    contentSchemaVersion = _contentSchemaVersion,
                    loadOrder = _loadOrder,
                    isCosmeticOnly = _isCosmeticOnly,
                    overrides = _overrides.ToArray(),
                    multiplayerPolicy = (MultiplayerPolicy)_multiplayerPolicy,
                };

                string manifestJson = JsonUtility.ToJson(manifest, true);
                File.WriteAllText(Path.Combine(modFolder, "manifest.json"), manifestJson);

                // 2. Build AssetBundle from selected content
                var contentAssets = _selectedAssets
                    .Where(a => a != null && a is IGameContent)
                    .ToList();

                if (contentAssets.Count == 0)
                {
                    _lastBuildResult = "No valid IGameContent assets to bundle.";
                    return;
                }

                // Create AssetBundle build
                var bundleName = $"{_modId}_content";
                var builds = new AssetBundleBuild[1];
                builds[0].assetBundleName = $"{bundleName}.bundle";
                builds[0].assetNames = contentAssets
                    .Select(a => AssetDatabase.GetAssetPath(a))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToArray();

                if (builds[0].assetNames.Length == 0)
                {
                    _lastBuildResult = "Could not resolve asset paths. Are all assets saved to disk?";
                    return;
                }

                var builtManifest = BuildPipeline.BuildAssetBundles(
                    bundlesFolder,
                    builds,
                    BuildAssetBundleOptions.None,
                    _buildTarget);

                if (builtManifest == null)
                {
                    _lastBuildResult = "AssetBundle build failed! Check console for errors.";
                    return;
                }

                // 3. Clean up extra files BuildPipeline creates
                // It creates a folder-name bundle and .manifest files we don't need
                string extraBundle = Path.Combine(bundlesFolder, "bundles");
                if (File.Exists(extraBundle)) File.Delete(extraBundle);
                if (File.Exists(extraBundle + ".manifest")) File.Delete(extraBundle + ".manifest");
                string contentManifestFile = Path.Combine(bundlesFolder, $"{bundleName}.bundle.manifest");
                if (File.Exists(contentManifestFile)) File.Delete(contentManifestFile);

                // 4. Summary
                long bundleSize = 0;
                string bundlePath = Path.Combine(bundlesFolder, $"{bundleName}.bundle");
                if (File.Exists(bundlePath))
                    bundleSize = new FileInfo(bundlePath).Length;

                _lastBuildResult = $"Mod built successfully!\n" +
                    $"  Location: {modFolder}\n" +
                    $"  Assets: {contentAssets.Count}\n" +
                    $"  Bundle size: {bundleSize / 1024f:F1} KB\n" +
                    $"  Overrides: {_overrides.Count}";

                Debug.Log($"[ModBuilder] Built mod '{_modId}' to {modFolder} " +
                    $"({contentAssets.Count} assets, {bundleSize / 1024f:F1} KB)");

                EditorUtility.RevealInFinder(modFolder);
            }
            catch (Exception e)
            {
                _lastBuildResult = $"Build failed: {e.Message}";
                Debug.LogError($"[ModBuilder] Build failed: {e}");
            }
        }

        #endregion
    }
}
#endif
