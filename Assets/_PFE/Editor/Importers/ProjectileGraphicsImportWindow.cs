#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;
using PFE.Systems.Combat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports projectile sprites from the original Flash export, creates
    /// ProjectileVisualDefinition assets, wires weapon assets, and produces
    /// a baseline ProjectilePrefabRegistry asset for the current Unity runtime.
    /// </summary>
    public class ProjectileGraphicsImportWindow : EditorWindow
    {
        static readonly string DefaultProjectRoot = SourceImportPaths.PfeRoot;

        string _projectRoot = DefaultProjectRoot;
        bool _importSprites = true;
        bool _createVisualAssets = true;
        bool _wireWeapons = true;
        bool _createRegistry = true;
        bool _assignRegistryToOpenScene = true;
        bool _isRunning;
        Vector2 _scrollPos;
        readonly List<string> _logMessages = new();

        [MenuItem("PFE/Art/Import Projectile Graphics")]
        public static void ShowWindow()
        {
            var window = GetWindow<ProjectileGraphicsImportWindow>("Projectile Import");
            window.minSize = new Vector2(620f, 560f);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Projectile Graphics Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Imports original PFE projectile sprite folders, creates ProjectileVisualDefinition assets, " +
                "wires matching WeaponDefinition assets by vbul/default ballistic rules, and builds a " +
                "ProjectilePrefabRegistry asset for the current projectile runtime path.",
                MessageType.Info);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            _projectRoot = EditorGUILayout.TextField("Original PFE Root", _projectRoot);
            if (GUILayout.Button("...", GUILayout.Width(30f)))
            {
                string folder = EditorUtility.OpenFolderPanel("Select original PFE project root", _projectRoot, "");
                if (!string.IsNullOrEmpty(folder))
                    _projectRoot = folder;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Expected scripts folder", Path.Combine(_projectRoot, "scripts"), EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Expected sprites folder", Path.Combine(_projectRoot, "sprites"), EditorStyles.miniLabel);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Pipeline", EditorStyles.boldLabel);
            _importSprites = EditorGUILayout.Toggle("1. Import Sprites", _importSprites);
            _createVisualAssets = EditorGUILayout.Toggle("2. Create Visual SOs", _createVisualAssets);
            _wireWeapons = EditorGUILayout.Toggle("3. Wire Weapon Assets", _wireWeapons);
            _createRegistry = EditorGUILayout.Toggle("4. Create Registry Asset", _createRegistry);
            _assignRegistryToOpenScene = EditorGUILayout.Toggle("5. Assign Open Scene Scope", _assignRegistryToOpenScene);

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                "After import, you can fine-tune scale/offset/rotation per projectile in the generated " +
                "ProjectileVisualDefinition assets under Assets/_PFE/Data/Resources/ProjectileVisuals.",
                MessageType.None);

            GUI.enabled = !_isRunning;
            if (GUILayout.Button("Run Import", GUILayout.Height(34f)))
                RunImport();
            GUI.enabled = true;

            if (_logMessages.Count <= 0)
                return;

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            if (GUILayout.Button("Copy Log", GUILayout.Width(80f)))
                GUIUtility.systemCopyBuffer = string.Join("\n", _logMessages);
            EditorGUILayout.EndHorizontal();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.MaxHeight(320f));
            foreach (string message in _logMessages)
            {
                var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                if (message.StartsWith("[WARN]"))
                    style.normal.textColor = new Color(1f, 0.72f, 0.2f);
                else if (message.StartsWith("[ERR]"))
                    style.normal.textColor = new Color(1f, 0.35f, 0.35f);
                else if (message.StartsWith("[OK]"))
                    style.normal.textColor = new Color(0.35f, 0.9f, 0.4f);

                EditorGUILayout.LabelField(message, style);
            }
            EditorGUILayout.EndScrollView();
        }

        void RunImport()
        {
            _isRunning = true;
            _logMessages.Clear();

            try
            {
                EditorUtility.DisplayProgressBar("Projectile Import", "Importing projectile graphics...", 0.2f);

                var result = ProjectileGraphicsImporter.Run(
                    _projectRoot,
                    _importSprites,
                    _createVisualAssets,
                    _wireWeapons,
                    _createRegistry,
                    _assignRegistryToOpenScene,
                    Log);

                Log($"[OK] Imported sprite folders: {result.ImportedSpriteFolders}");
                Log($"[OK] Created or updated visual assets: {result.CreatedOrUpdatedVisualAssets}");
                Log($"[OK] Wired weapon assets: {result.WiredWeapons}");
                Log($"[OK] Registry entries written: {result.RegistryEntries}");

                if (result.AssignedOpenSceneScopes > 0)
                {
                    Log($"[OK] Assigned ProjectilePrefabRegistry to {result.AssignedOpenSceneScopes} open GameLifetimeScope object(s).");
                }
                else if (_assignRegistryToOpenScene && _createRegistry)
                {
                    Log("[WARN] No open GameLifetimeScope objects were auto-assigned. " +
                        "If needed, drag the generated ProjectilePrefabRegistry asset into the scene's GameLifetimeScope manually.");
                }

                foreach (string warning in result.Warnings)
                    Log($"[WARN] {warning}");

                Log("[OK] Projectile graphics import complete.");
            }
            catch (Exception ex)
            {
                Log($"[ERR] Import failed: {ex.Message}");
                Debug.LogException(ex);
            }
            finally
            {
                _isRunning = false;
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        void Log(string message)
        {
            _logMessages.Add(message);

            if (message.StartsWith("[ERR]"))
                Debug.LogError($"[ProjectileImport] {message}");
            else if (message.StartsWith("[WARN]"))
                Debug.LogWarning($"[ProjectileImport] {message}");
            else
                Debug.Log($"[ProjectileImport] {message}");
        }
    }

    static class ProjectileGraphicsImporter
    {
        const string ProjectileArtRoot = "Assets/_PFE/Art/Projectiles";
        const string ProjectileVisualRoot = "Assets/_PFE/Data/Resources/ProjectileVisuals";
        const string ProjectileRegistryAssetPath = "Assets/_PFE/Data/Resources/ProjectilePrefabRegistry.asset";
        const string WeaponSearchRoot = "Assets/_PFE/Data/Resources/Weapons";
        const string GenericProjectilePrefabPath = "Assets/_PFE/Prefabs/projectile.prefab";
        const string DefaultBallisticId = "default_ballistic";
        const float DefaultFrameRate = 30f;
        const int PixelsPerUnit = 100;

        static readonly Regex ClassNameRegex = new(
            @"public\s+dynamic\s+class\s+(\w+)\s+extends\s+MovieClip",
            RegexOptions.Compiled);
        static readonly Regex SymbolRegex = new(
            @"symbol=""symbol(\d+)""",
            RegexOptions.Compiled);

        internal sealed class ImportResult
        {
            public readonly List<string> Warnings = new();
            public int ImportedSpriteFolders;
            public int CreatedOrUpdatedVisualAssets;
            public int WiredWeapons;
            public int RegistryEntries;
            public int AssignedOpenSceneScopes;
        }

        sealed class SourceVisualInfo
        {
            public string VisualId;
            public string ClassName;
            public int SymbolId;
            public string SourceFolder;
            public int FrameCount;
            public bool Loop;
            public ProjectileArchetype RecommendedArchetype;
        }

        public static ImportResult Run(
            string projectRoot,
            bool importSprites,
            bool createVisualAssets,
            bool wireWeapons,
            bool createRegistry,
            bool assignRegistryToOpenScene,
            Action<string> log)
        {
            var result = new ImportResult();

            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
                throw new DirectoryNotFoundException($"Original PFE root not found: {projectRoot}");

            string scriptsRoot = Path.Combine(projectRoot, "scripts");
            if (!Directory.Exists(scriptsRoot))
                throw new DirectoryNotFoundException($"Scripts folder not found: {scriptsRoot}");

            var sourceVisuals = DiscoverSourceVisuals(projectRoot, scriptsRoot, result);
            log($"[OK] Discovered {sourceVisuals.Count} projectile visual source folders.");

            if (importSprites)
            {
                ImportSourceSprites(sourceVisuals, result);
                AssetDatabase.Refresh();
                ConfigureImportedSprites(sourceVisuals);
                log($"[OK] Imported projectile sprites into {ProjectileArtRoot}.");
            }

            Dictionary<string, ProjectileVisualDefinition> visuals = LoadExistingVisualDefinitions();
            if (createVisualAssets)
            {
                visuals = CreateOrUpdateVisualAssets(sourceVisuals, result);
                log($"[OK] Created or updated {result.CreatedOrUpdatedVisualAssets} ProjectileVisualDefinition assets.");
            }

            if (wireWeapons)
            {
                result.WiredWeapons = WireWeapons(visuals);
                log($"[OK] Updated {result.WiredWeapons} weapon assets with projectile visual references.");
            }

            ProjectilePrefabRegistry registry = null;
            if (createRegistry)
            {
                registry = CreateOrUpdateRegistry(result);
                if (registry != null)
                    log($"[OK] Registry asset ready at {ProjectileRegistryAssetPath}.");
            }

            if (assignRegistryToOpenScene && registry != null)
                result.AssignedOpenSceneScopes = AssignRegistryToOpenSceneScopes(registry);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return result;
        }

        static List<SourceVisualInfo> DiscoverSourceVisuals(string projectRoot, string scriptsRoot, ImportResult result)
        {
            var weaponArchetypes = BuildWeaponArchetypeLookup();
            var discovered = new Dictionary<string, SourceVisualInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (string scriptPath in Directory.GetFiles(scriptsRoot, "*.as", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(scriptPath);
                if (!fileName.Equals("visualBullet.as", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.StartsWith("visbul", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string scriptText = File.ReadAllText(scriptPath);
                var classMatch = ClassNameRegex.Match(scriptText);
                var symbolMatch = SymbolRegex.Match(scriptText);
                if (!classMatch.Success || !symbolMatch.Success)
                {
                    result.Warnings.Add($"Could not parse projectile metadata from {fileName}.");
                    continue;
                }

                string className = classMatch.Groups[1].Value;
                string visualId = ToVisualId(className);
                if (string.IsNullOrWhiteSpace(visualId))
                    continue;

                int symbolId = int.Parse(symbolMatch.Groups[1].Value);
                string sourceFolder = FindSourceFolder(projectRoot, symbolId, className);
                if (string.IsNullOrEmpty(sourceFolder))
                {
                    result.Warnings.Add($"{className} (symbol {symbolId}) has no exported sprite folder.");
                    continue;
                }

                int frameCount = Directory.GetFiles(sourceFolder, "*.png").Length;
                if (frameCount <= 0)
                {
                    result.Warnings.Add($"{className} (symbol {symbolId}) has no PNG frames in {sourceFolder}.");
                    continue;
                }

                discovered[visualId] = new SourceVisualInfo
                {
                    VisualId = visualId,
                    ClassName = className,
                    SymbolId = symbolId,
                    SourceFolder = sourceFolder,
                    FrameCount = frameCount,
                    Loop = DetermineLoop(scriptText, frameCount),
                    RecommendedArchetype = ResolveArchetype(visualId, weaponArchetypes)
                };
            }

            return discovered.Values.OrderBy(v => v.VisualId).ToList();
        }

        static void ImportSourceSprites(IEnumerable<SourceVisualInfo> sourceVisuals, ImportResult result)
        {
            EnsureAssetDirectory(ProjectileArtRoot);

            foreach (SourceVisualInfo visual in sourceVisuals)
            {
                string targetFolder = GetArtFolderPath(visual.VisualId);
                EnsureAssetDirectory(targetFolder);

                var sourcePngs = Directory.GetFiles(visual.SourceFolder, "*.png")
                    .OrderBy(GetFrameNumber)
                    .ToArray();

                var expectedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string sourcePng in sourcePngs)
                {
                    int frameNumber = GetFrameNumber(sourcePng);
                    string fileName = $"f{frameNumber:D3}.png";
                    expectedFileNames.Add(fileName);

                    string destFullPath = Path.Combine(Path.GetFullPath(targetFolder), fileName);
                    if (!File.Exists(destFullPath) ||
                        File.GetLastWriteTimeUtc(sourcePng) > File.GetLastWriteTimeUtc(destFullPath))
                    {
                        File.Copy(sourcePng, destFullPath, true);
                    }
                }

                foreach (string existing in Directory.GetFiles(Path.GetFullPath(targetFolder), "*.png"))
                {
                    if (!expectedFileNames.Contains(Path.GetFileName(existing)))
                        File.Delete(existing);
                }

                result.ImportedSpriteFolders++;
            }
        }

        static void ConfigureImportedSprites(IEnumerable<SourceVisualInfo> sourceVisuals)
        {
            foreach (SourceVisualInfo visual in sourceVisuals)
            {
                string folder = GetArtFolderPath(visual.VisualId);
                string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (importer == null)
                        continue;

                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.spritePixelsPerUnit = PixelsPerUnit;
                    importer.filterMode = FilterMode.Bilinear;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;

                    var settings = new TextureImporterSettings();
                    importer.ReadTextureSettings(settings);
                    settings.spriteMode = (int)SpriteImportMode.Single;
                    settings.spriteAlignment = (int)SpriteAlignment.Center;
                    settings.spritePivot = new Vector2(0.5f, 0.5f);
                    importer.SetTextureSettings(settings);
                    importer.SaveAndReimport();
                }
            }
        }

        static Dictionary<string, ProjectileVisualDefinition> CreateOrUpdateVisualAssets(
            IEnumerable<SourceVisualInfo> sourceVisuals,
            ImportResult result)
        {
            EnsureAssetDirectory(ProjectileVisualRoot);
            var visuals = new Dictionary<string, ProjectileVisualDefinition>(StringComparer.OrdinalIgnoreCase);

            foreach (SourceVisualInfo source in sourceVisuals)
            {
                string assetPath = $"{ProjectileVisualRoot}/{SanitizeName(source.VisualId)}.asset";
                var visual = AssetDatabase.LoadAssetAtPath<ProjectileVisualDefinition>(assetPath);
                bool created = visual == null;
                if (created)
                    visual = ScriptableObject.CreateInstance<ProjectileVisualDefinition>();

                visual.visualId = source.VisualId;
                visual.sourceClassName = source.ClassName;
                visual.sourceSymbolId = source.SymbolId;
                visual.recommendedArchetype = source.RecommendedArchetype;
                visual.frameRate = DefaultFrameRate;
                visual.loop = source.Loop;
                visual.frames = LoadImportedFrames(source.VisualId);

                if (created)
                    AssetDatabase.CreateAsset(visual, assetPath);
                else
                    EditorUtility.SetDirty(visual);

                visuals[source.VisualId] = visual;
                result.CreatedOrUpdatedVisualAssets++;
            }

            return visuals;
        }

        static Sprite[] LoadImportedFrames(string visualId)
        {
            string folder = GetArtFolderPath(visualId);
            string[] guids = AssetDatabase.FindAssets("t:Sprite", new[] { folder });
            return guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(GetFrameNumber)
                .Select(AssetDatabase.LoadAssetAtPath<Sprite>)
                .Where(sprite => sprite != null)
                .ToArray();
        }

        static Dictionary<string, ProjectileVisualDefinition> LoadExistingVisualDefinitions()
        {
            var lookup = new Dictionary<string, ProjectileVisualDefinition>(StringComparer.OrdinalIgnoreCase);
            string[] guids = AssetDatabase.FindAssets("t:ProjectileVisualDefinition", new[] { ProjectileVisualRoot });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var visual = AssetDatabase.LoadAssetAtPath<ProjectileVisualDefinition>(path);
                if (visual == null)
                    continue;

                string key = !string.IsNullOrWhiteSpace(visual.visualId)
                    ? visual.visualId
                    : Path.GetFileNameWithoutExtension(path);

                lookup[key] = visual;
            }

            return lookup;
        }

        static int WireWeapons(Dictionary<string, ProjectileVisualDefinition> visuals)
        {
            int updated = 0;
            visuals.TryGetValue(DefaultBallisticId, out var defaultBallistic);

            string[] guids = AssetDatabase.FindAssets("t:WeaponDefinition", new[] { WeaponSearchRoot });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var weapon = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
                if (weapon == null)
                    continue;

                ProjectileVisualDefinition target = null;
                if (!string.IsNullOrWhiteSpace(weapon.vbul))
                {
                    visuals.TryGetValue(weapon.vbul, out target);
                }
                else if (ShouldUseDefaultBallistic(weapon))
                {
                    target = defaultBallistic;
                }

                if (target == null || weapon.projectileVisual == target)
                    continue;

                weapon.projectileVisual = target;
                EditorUtility.SetDirty(weapon);
                updated++;
            }

            return updated;
        }

        static ProjectilePrefabRegistry CreateOrUpdateRegistry(ImportResult result)
        {
            EnsureAssetDirectory(Path.GetDirectoryName(ProjectileRegistryAssetPath)?.Replace('\\', '/'));

            var registry = AssetDatabase.LoadAssetAtPath<ProjectilePrefabRegistry>(ProjectileRegistryAssetPath);
            bool created = registry == null;
            if (created)
                registry = ScriptableObject.CreateInstance<ProjectilePrefabRegistry>();

            Projectile prefab = AssetDatabase.LoadAssetAtPath<Projectile>(GenericProjectilePrefabPath);
            if (prefab == null)
            {
                result.Warnings.Add($"Generic projectile prefab not found at {GenericProjectilePrefabPath}.");
                return null;
            }

            var entries = Enum.GetValues(typeof(ProjectileArchetype))
                .Cast<ProjectileArchetype>()
                .Select(archetype => new ProjectilePrefabRegistry.Entry
                {
                    archetype = archetype,
                    prefab = prefab
                })
                .ToList();

            registry.SetEntries(entries);
            if (created)
                AssetDatabase.CreateAsset(registry, ProjectileRegistryAssetPath);
            else
                EditorUtility.SetDirty(registry);

            result.RegistryEntries = entries.Count;
            return registry;
        }

        static int AssignRegistryToOpenSceneScopes(ProjectilePrefabRegistry registry)
        {
            int assigned = 0;

            foreach (MonoBehaviour behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (behaviour == null)
                    continue;

                Type behaviourType = behaviour.GetType();
                if (!string.Equals(behaviourType.Name, "GameLifetimeScope", StringComparison.Ordinal))
                    continue;

                if (EditorUtility.IsPersistent(behaviour) || !behaviour.gameObject.scene.IsValid())
                    continue;

                var serializedObject = new SerializedObject(behaviour);
                var property = serializedObject.FindProperty("_projectilePrefabRegistry");
                if (property == null || property.objectReferenceValue == registry)
                    continue;

                property.objectReferenceValue = registry;
                serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(behaviour);
                EditorSceneManager.MarkSceneDirty(behaviour.gameObject.scene);
                assigned++;
            }

            return assigned;
        }

        static Dictionary<string, ProjectileArchetype> BuildWeaponArchetypeLookup()
        {
            var counts = new Dictionary<string, Dictionary<ProjectileArchetype, int>>(StringComparer.OrdinalIgnoreCase);
            string[] guids = AssetDatabase.FindAssets("t:WeaponDefinition", new[] { WeaponSearchRoot });

            foreach (string guid in guids)
            {
                var weapon = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (weapon == null || string.IsNullOrWhiteSpace(weapon.vbul))
                    continue;

                if (!counts.TryGetValue(weapon.vbul, out var perArchetype))
                {
                    perArchetype = new Dictionary<ProjectileArchetype, int>();
                    counts[weapon.vbul] = perArchetype;
                }

                if (!perArchetype.ContainsKey(weapon.projectileArchetype))
                    perArchetype[weapon.projectileArchetype] = 0;

                perArchetype[weapon.projectileArchetype]++;
            }

            return counts.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.OrderByDescending(pair => pair.Value).First().Key,
                StringComparer.OrdinalIgnoreCase);
        }

        static ProjectileArchetype ResolveArchetype(
            string visualId,
            Dictionary<string, ProjectileArchetype> weaponLookup)
        {
            if (visualId.Equals(DefaultBallisticId, StringComparison.OrdinalIgnoreCase))
                return ProjectileArchetype.Ballistic;

            if (weaponLookup.TryGetValue(visualId, out var fromWeapons))
                return fromWeapons;

            string id = visualId.ToLowerInvariant();
            if (id.Contains("rocket") || id.Contains("gren") || id.Contains("saw"))
                return ProjectileArchetype.Explosive;
            if (id.Contains("flame") || id.Contains("fireball") || id.Contains("arson"))
                return ProjectileArchetype.Flame;
            if (id.Contains("laser") || id == "dray" || id == "termo")
                return ProjectileArchetype.Laser;
            if (id.Contains("plasma") || id == "pulse" || id == "blump" || id == "eclipse" || id == "cryo")
                return ProjectileArchetype.Plasma;
            if (id.Contains("spark") || id.Contains("lightning") || id.Contains("moln") || id.Contains("bloodlight"))
                return ProjectileArchetype.Spark;
            if (id.Contains("navod"))
                return ProjectileArchetype.Homing;
            if (id.Contains("telebullet") || id.Contains("skybolt"))
                return ProjectileArchetype.Magic;
            if (id.Contains("venom") || id.Contains("kapl") || id.Contains("plevok") ||
                id == "blood" || id == "necro" || id == "psy" ||
                id == "pink" || id == "necrbullet")
            {
                return ProjectileArchetype.Spit;
            }

            return ProjectileArchetype.Ballistic;
        }

        static string FindSourceFolder(string projectRoot, int symbolId, string className)
        {
            string[] candidateRoots =
            {
                Path.Combine(projectRoot, "sprites"),
                Path.Combine(projectRoot, "scripts", "_assets", "sprites"),
                Path.Combine(projectRoot, "_assets", "sprites")
            };

            string preferredPattern = $"DefineSprite_{symbolId}_{className}";
            string fallbackPattern = $"DefineSprite_{symbolId}_*";

            foreach (string root in candidateRoots)
            {
                if (!Directory.Exists(root))
                    continue;

                string preferred = Path.Combine(root, preferredPattern);
                if (Directory.Exists(preferred))
                    return preferred;

                string[] fallback = Directory.GetDirectories(root, fallbackPattern);
                if (fallback.Length > 0)
                    return fallback[0];
            }

            return null;
        }

        static bool DetermineLoop(string scriptText, int frameCount)
        {
            if (frameCount <= 1)
                return false;

            return scriptText.IndexOf("stop();", StringComparison.OrdinalIgnoreCase) < 0;
        }

        static string ToVisualId(string className)
        {
            if (className.Equals("visualBullet", StringComparison.OrdinalIgnoreCase))
                return DefaultBallisticId;

            if (className.StartsWith("visbul", StringComparison.OrdinalIgnoreCase))
                return className.Substring("visbul".Length).ToLowerInvariant();

            return null;
        }

        static bool ShouldUseDefaultBallistic(WeaponDefinition weapon)
        {
            return string.IsNullOrWhiteSpace(weapon.vbul) &&
                   weapon.projectileArchetype == ProjectileArchetype.Ballistic &&
                   (weapon.weaponType == WeaponType.Guns || weapon.weaponType == WeaponType.BigGun);
        }

        static string GetArtFolderPath(string visualId)
        {
            return $"{ProjectileArtRoot}/{SanitizeName(visualId)}";
        }

        static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unnamed_projectile";

            char[] invalid = Path.GetInvalidFileNameChars();
            return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
        }

        static int GetFrameNumber(string path)
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            if (fileName.StartsWith("f", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(fileName.Substring(1), out int importedFrame))
            {
                return importedFrame;
            }

            return int.TryParse(fileName, out int sourceFrame) ? sourceFrame : 0;
        }

        static void EnsureAssetDirectory(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            string fullPath = Path.GetFullPath(assetPath);
            if (!Directory.Exists(fullPath))
                Directory.CreateDirectory(fullPath);
        }
    }
}
#endif
