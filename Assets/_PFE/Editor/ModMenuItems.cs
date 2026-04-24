#if UNITY_EDITOR
using System.IO;
using UnityEngine;
using UnityEditor;
using PFE.ModAPI;

namespace PFE.Editor
{
    /// <summary>
    /// Quick-access menu items for mod development workflow.
    /// </summary>
    public static class ModMenuItems
    {
        [MenuItem("PFE/Modding/Create New Mod Folder...", priority = 10)]
        public static void CreateNewModFolder()
        {
            string modsRoot = Path.Combine(Application.persistentDataPath, "Mods");
            string folder = EditorUtility.SaveFolderPanel("Create Mod Folder", modsRoot, "author.newmod");
            if (string.IsNullOrEmpty(folder)) return;

            string modId = Path.GetFileName(folder);

            // Create structure
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, "bundles"));
            Directory.CreateDirectory(Path.Combine(folder, "data"));

            // Write starter manifest
            var manifest = new ModManifest
            {
                modId = modId,
                displayName = modId,
                version = "1.0.0",
                author = "Author",
                description = "",
                targetGameVersion = Application.version,
                contentSchemaVersion = 1,
                loadOrder = 100,
            };

            string json = JsonUtility.ToJson(manifest, true);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), json);

            Debug.Log($"[ModMenuItems] Created mod folder: {folder}");
            EditorUtility.RevealInFinder(folder);
        }

        [MenuItem("PFE/Modding/Open Mods Folder", priority = 30)]
        public static void OpenModsFolder()
        {
            string modsRoot = Path.Combine(Application.persistentDataPath, "Mods");
            if (!Directory.Exists(modsRoot))
                Directory.CreateDirectory(modsRoot);
            EditorUtility.RevealInFinder(modsRoot);
        }

        [MenuItem("PFE/Modding/Log Content Registry Summary", priority = 50)]
        public static void LogRegistrySummary()
        {
            var loader = new Data.ModLoader();
            var registry = new Data.ContentRegistry { LogRegistrations = false, LogConflicts = false };
            var sources = loader.BuildSourceList();
            registry.Initialize(sources);

            Debug.Log(registry.GetSummary());

            foreach (ContentType ct in System.Enum.GetValues(typeof(ContentType)))
            {
                int count = registry.GetCount(ct);
                if (count == 0) continue;
                Debug.Log($"  {ct}: {string.Join(", ", registry.GetAllIds(ct))}");
            }
        }

        [MenuItem("PFE/Modding/Validate Selected Asset as IGameContent", priority = 51)]
        public static void ValidateSelectedAsset()
        {
            var obj = Selection.activeObject;
            if (obj == null)
            {
                Debug.LogWarning("[ModMenuItems] No asset selected");
                return;
            }

            if (obj is not ScriptableObject so)
            {
                Debug.LogWarning($"[ModMenuItems] {obj.name} is not a ScriptableObject");
                return;
            }

            if (so is IGameContent gc)
            {
                Debug.Log($"[ModMenuItems] {obj.name}: ContentType={gc.ContentType}, ContentId={gc.ContentId}");
            }
            else
            {
                Debug.LogWarning($"[ModMenuItems] {obj.name} ({so.GetType().Name}) does not implement IGameContent");
            }
        }
    }
}
#endif
