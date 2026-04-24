#if UNITY_EDITOR
using System;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using System.IO;
using PFE.Data.Definitions;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports ammunition data from AllData.as XML format.
    /// Uses regex-based extraction to handle embedded ActionScript XML.
    /// </summary>
    public static class AmmoDefinitionImporter
    {
        private static readonly string SourceFilePath = SourceImportPaths.AllDataAsPath;
        private static readonly string OutputPath = "Assets/_PFE/Data/Resources/Ammo";

        /// <summary>
        /// Parse and import all ammunition items from AllData.as
        /// </summary>
        [MenuItem("PFE/Data/Import Ammunition from AllData.as")]
        public static void ImportAmmunition()
        {
            Debug.Log($"Starting ammunition import from {SourceFilePath}");

            if (!File.Exists(SourceFilePath))
            {
                Debug.LogError($"Source file not found: {SourceFilePath}");
                return;
            }

            // Ensure output directory exists
            Directory.CreateDirectory(OutputPath);

            // Read the file with UTF-8 encoding
            string content;
            using (StreamReader reader = new StreamReader(SourceFilePath, System.Text.Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }

            // Use regex to find all item elements with tip='a' (ammunition)
            // This approach avoids XML parsing issues with embedded ActionScript
            string pattern = "<item\\s+([^>]*?)tip\\s*=\\s*['\"]a['\"]([^>]*?)>";
            MatchCollection matches = Regex.Matches(content, pattern, RegexOptions.Singleline);

            Debug.Log($"Found {matches.Count} ammunition items using regex");

            int imported = 0;
            int skipped = 0;

            foreach (Match match in matches)
            {
                try
                {
                    // Combine both parts of the match
                    string fullItemTag = $"<item {match.Groups[1].Value}{match.Groups[2].Value}>";

                    // Parse attributes from the item tag
                    string id = ExtractAttribute(fullItemTag, "id");

                    if (string.IsNullOrEmpty(id))
                    {
                        Debug.LogWarning($"Skipping item with no ID: {fullItemTag}");
                        skipped++;
                        continue;
                    }

                    // Skip special infinite ammo items
                    if (id == "recharg" || id == "not")
                    {
                        Debug.Log($"Skipping special ammo: {id}");
                        skipped++;
                        continue;
                    }

                    // Check if already exists
                    string assetPath = $"{OutputPath}/{id}.asset";
                    if (AssetDatabase.LoadAssetAtPath<AmmoDefinition>(assetPath) != null)
                    {
                        Debug.Log($"Skipping existing: {id}");
                        skipped++;
                        continue;
                    }

                    // Create the ammo asset
                    AmmoDefinition ammo = ScriptableObject.CreateInstance<AmmoDefinition>();
                    // Direct field assignment (fields are public)
                    ammo.ammoId = id;
                    ammo.displayName = id;

                    // Parse base attribute
                    string baseId = ExtractAttribute(fullItemTag, "base");
                    if (!string.IsNullOrEmpty(baseId))
                        ammo.baseId = baseId;

                    // Parse numeric attributes
                    ParseAndSetInt(fullItemTag, "kol", value => ammo.stackSize = value);
                    ParseAndSetFloat(fullItemTag, "chance", value => ammo.dropChance = value);
                    ParseAndSetInt(fullItemTag, "stage", value => ammo.requiredStoryStage = value);
                    ParseAndSetInt(fullItemTag, "lvl", value => ammo.requiredLevel = value);
                    ParseAndSetInt(fullItemTag, "price", value => ammo.basePrice = value);
                    ParseAndSetInt(fullItemTag, "sell", value => ammo.sellPrice = value);
                    ParseAndSetFloat(fullItemTag, "m", value => ammo.weight = value);

                    // Variant modifiers
                    string modValue = ExtractAttribute(fullItemTag, "mod");
                    if (!string.IsNullOrEmpty(modValue) && int.TryParse(modValue, out int mod))
                        ammo.modifier = (AmmoModifier)mod;

                    ParseAndSetInt(fullItemTag, "pier", value => ammo.armorPiercingBonus = value);
                    ParseAndSetFloat(fullItemTag, "damage", value => ammo.damageMultiplier = value);
                    ParseAndSetFloat(fullItemTag, "armor", value => ammo.armorMultiplier = value);
                    ParseAndSetFloat(fullItemTag, "knock", value => ammo.knockbackMultiplier = value);
                    ParseAndSetInt(fullItemTag, "fire", value => ammo.fireDamage = value);

                    // Save the asset
                    AssetDatabase.CreateAsset(ammo, assetPath);
                    imported++;

                    Debug.Log($"Imported: {id}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error importing item: {e.Message}\n{e.StackTrace}");
                    skipped++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Ammunition import complete. Imported: {imported}, Skipped: {skipped}");
        }

        /// <summary>
        /// Extract an attribute value from an XML tag string
        /// </summary>
        private static string ExtractAttribute(string tag, string attributeName)
        {
            // Match attribute='value' or attribute="value"
            var match = Regex.Match(tag, $"{attributeName}\\s*=\\s*['\"]([^'\"]*)['\"]");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Parse an integer attribute and invoke setter if found
        /// </summary>
        private static void ParseAndSetInt(string tag, string attributeName, System.Action<int> setter)
        {
            string value = ExtractAttribute(tag, attributeName);
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int intValue))
            {
                setter(intValue);
            }
        }

        /// <summary>
        /// Parse a float attribute and invoke setter if found
        /// </summary>
        private static void ParseAndSetFloat(string tag, string attributeName, System.Action<float> setter)
        {
            string value = ExtractAttribute(tag, attributeName);
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, out float floatValue))
            {
                setter(floatValue);
            }
        }
    }
}
#endif
