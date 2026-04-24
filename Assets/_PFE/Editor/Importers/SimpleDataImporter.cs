#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using PFE.Data.Definitions;
using PFE.Editor.Importers;

namespace PFE.Editor
{
    /// <summary>
    /// Simple data importer that creates assets with minimal data.
    /// IDs and other fields will be populated by FixDataImport.cs based on filenames.
    /// </summary>
    public static class SimpleDataImporter
    {
        private static readonly string SourceFilePath = SourceImportPaths.AllDataAsPath;

        [MenuItem("PFE/Data/Simple Import All Data")]
        public static void ImportAll()
        {
            ImportAmmo();
            ImportItems();
            ImportPerks();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Simple import complete. Run 'PFE > Data > Fix Imported Data IDs' to set IDs.");
        }

        private static void ImportAmmo()
        {
            string outputPath = "Assets/_PFE/Data/Resources/Ammo";
            Directory.CreateDirectory(outputPath);

            string content = File.ReadAllText(SourceFilePath);
            var matches = System.Text.RegularExpressions.Regex.Matches(content,
                "<item\\s+([^>]*?)tip\\s*=\\s*['\"]a['\"]([^>]*?)>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            int imported = 0;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string tagContent = match.Groups[1].Value + match.Groups[2].Value;
                string id = ExtractAttribute(tagContent, "id");

                if (!string.IsNullOrEmpty(id) && id != "not" && id != "recharg")
                {
                    string assetPath = $"{outputPath}/{id}.asset";
                    if (AssetDatabase.LoadAssetAtPath<AmmoDefinition>(assetPath) == null)
                    {
                        var ammo = ScriptableObject.CreateInstance<AmmoDefinition>();
                        AssetDatabase.CreateAsset(ammo, assetPath);
                        imported++;
                    }
                }
            }
            Debug.Log($"Ammo: {imported} created");
        }

        private static void ImportItems()
        {
            string outputPath = "Assets/_PFE/Data/Resources/Items";
            Directory.CreateDirectory(outputPath);

            string content = File.ReadAllText(SourceFilePath);
            var matches = System.Text.RegularExpressions.Regex.Matches(content,
                "<item\\s+([^>]*?)tip\\s*=\\s*['\"]([^'\"]*)['\"]([^>]*?)>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            int imported = 0;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string tipCode = match.Groups[2].Value;
                if (tipCode == "a") continue; // Skip ammo

                string tagContent = match.Groups[1].Value + match.Groups[3].Value;
                string id = ExtractAttribute(tagContent, "id");

                if (!string.IsNullOrEmpty(id))
                {
                    string assetPath = $"{outputPath}/{id}.asset";
                    if (AssetDatabase.LoadAssetAtPath<ItemDefinition>(assetPath) == null)
                    {
                        var item = ScriptableObject.CreateInstance<ItemDefinition>();
                        AssetDatabase.CreateAsset(item, assetPath);
                        imported++;
                    }
                }
            }
            Debug.Log($"Items: {imported} created");
        }

        private static void ImportPerks()
        {
            string outputPath = "Assets/_PFE/Data/Resources/Perks";
            Directory.CreateDirectory(outputPath);

            string content = File.ReadAllText(SourceFilePath);
            var matches = System.Text.RegularExpressions.Regex.Matches(content,
                "<perk\\s+([^>]*?)>(.*?)</perk>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            int imported = 0;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string tagContent = match.Groups[1].Value;
                string id = ExtractAttribute(tagContent, "id");

                if (!string.IsNullOrEmpty(id))
                {
                    string assetPath = $"{outputPath}/{id}.asset";
                    if (AssetDatabase.LoadAssetAtPath<PerkDefinition>(assetPath) == null)
                    {
                        var perk = ScriptableObject.CreateInstance<PerkDefinition>();
                        AssetDatabase.CreateAsset(perk, assetPath);
                        imported++;
                    }
                }
            }
            Debug.Log($"Perks: {imported} created");
        }

        private static string ExtractAttribute(string tag, string attributeName)
        {
            var match = System.Text.RegularExpressions.Regex.Match(tag,
                $"{attributeName}\\s*=\\s*['\"]([^'\"]*)['\"]");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
#endif
