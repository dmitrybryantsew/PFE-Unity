#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using PFE.Data.Definitions;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Fixes data import issues by re-setting IDs based on asset filenames.
    /// Run this after importing data to ensure IDs are correctly set.
    /// </summary>
    public static class FixDataImport
    {
        [MenuItem("PFE/Data/Fix Imported Data IDs")]
        public static void FixAllIds()
        {
            FixAmmoIds();
            FixItemIds();
            FixPerkIds();
            AssetDatabase.SaveAssets();
            Debug.Log("Fixed all imported data IDs");
        }

        private static void FixAmmoIds()
        {
            string path = "Assets/_PFE/Data/Resources/Ammo";
            string[] guids = AssetDatabase.FindAssets("t:AmmoDefinition", new[] { path });

            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                AmmoDefinition ammo = AssetDatabase.LoadAssetAtPath<AmmoDefinition>(assetPath);

                if (ammo != null && string.IsNullOrEmpty(ammo.ammoId))
                {
                    ammo.ammoId = Path.GetFileNameWithoutExtension(assetPath);
                    if (string.IsNullOrEmpty(ammo.displayName))
                        ammo.displayName = ammo.ammoId;
                    EditorUtility.SetDirty(ammo);
                }
            }

            Debug.Log($"Fixed {guids.Length} ammo items");
        }

        private static void FixItemIds()
        {
            string path = "Assets/_PFE/Data/Resources/Items";
            string[] guids = AssetDatabase.FindAssets("t:ItemDefinition", new[] { path });
            string sourceFilePath = SourceImportPaths.AllDataAsPath;
            string sourceContent = File.ReadAllText(sourceFilePath);

            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                ItemDefinition item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(assetPath);

                if (item != null)
                {
                    bool needsSave = false;

                    // Set ID if empty
                    if (string.IsNullOrEmpty(item.itemId))
                    {
                        item.itemId = Path.GetFileNameWithoutExtension(assetPath);
                        needsSave = true;
                    }

                    // Set display name if empty
                    if (string.IsNullOrEmpty(item.displayName))
                    {
                        item.displayName = item.itemId;
                        needsSave = true;
                    }

                    // Set description if empty
                    if (string.IsNullOrEmpty(item.description))
                    {
                        item.description = $"{item.itemId} description";
                        needsSave = true;
                    }

                    // Set type based on source data tip code (always fix, not just for Ammo)
                    PFE.Data.Definitions.ItemType? itemTypeFromSource = GetItemTypeFromSource(item.itemId, sourceContent);
                    if (itemTypeFromSource.HasValue && item.type != itemTypeFromSource.Value)
                    {
                        item.type = itemTypeFromSource.Value;
                        needsSave = true;
                    }

                    if (needsSave)
                    {
                        EditorUtility.SetDirty(item);
                    }
                }
            }

            Debug.Log($"Fixed {guids.Length} items");
        }

        private static ItemType? GetItemTypeFromSource(string itemId, string sourceContent)
        {
            // Find the item in source data
            var match = System.Text.RegularExpressions.Regex.Match(sourceContent,
                $"<item\\s+[^>]*id\\s*=\\s*['\"]{System.Text.RegularExpressions.Regex.Escape(itemId)}['\"][^>]*>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            if (!match.Success) return null;

            string itemTag = match.Value;
            var tipMatch = System.Text.RegularExpressions.Regex.Match(itemTag, "tip\\s*=\\s*['\"]([^'\"]*)['\"]");

            if (!tipMatch.Success) return null;

            string tip = tipMatch.Groups[1].Value;

            // Map tip codes to ItemType enum values
            switch (tip)
            {
                case "a": return PFE.Data.Definitions.ItemType.Ammo;
                case "med": return PFE.Data.Definitions.ItemType.Medical;
                case "him": return PFE.Data.Definitions.ItemType.Chems;
                case "pot": return PFE.Data.Definitions.ItemType.Chems;
                case "book": return PFE.Data.Definitions.ItemType.Book;
                case "e":
                case "equip": return PFE.Data.Definitions.ItemType.Equipment;
                case "impl": return PFE.Data.Definitions.ItemType.Implant;
                case "key": return PFE.Data.Definitions.ItemType.Key;
                case "val":
                case "valuables": return PFE.Data.Definitions.ItemType.Valuable;
                case "art":
                case "sphera": return PFE.Data.Definitions.ItemType.Sphera;
                case "m":
                case "compa":
                case "compm":
                case "compe":
                case "compp":
                case "compw": return PFE.Data.Definitions.ItemType.Component;
                case "food":
                case "paint":
                case "spell":
                case "note":
                case "weap":
                case "stuff":
                case "instr":
                case "scheme":
                case "res":
                case "spec":
                case "up":
                case "trap":
                case "door":
                case "box":
                case "unit":
                case "0":
                case "1":
                case "2":
                case "3":
                case "4":
                case "5":
                case "6":
                case "area":
                case "bonus":
                case "checkpoint":
                case "enspawn":
                case "money":
                case "spawnpoint":
                default: return PFE.Data.Definitions.ItemType.Misc;
            }
        }

        private static void FixPerkIds()
        {
            string path = "Assets/_PFE/Data/Resources/Perks";
            string[] guids = AssetDatabase.FindAssets("t:PerkDefinition", new[] { path });

            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                PerkDefinition perk = AssetDatabase.LoadAssetAtPath<PerkDefinition>(assetPath);

                if (perk != null && string.IsNullOrEmpty(perk.perkId))
                {
                    perk.perkId = Path.GetFileNameWithoutExtension(assetPath);
                    if (string.IsNullOrEmpty(perk.displayName))
                        perk.displayName = GenerateDisplayName(perk.perkId);
                    EditorUtility.SetDirty(perk);
                }
            }

            Debug.Log($"Fixed {guids.Length} perks");
        }

        private static string GenerateDisplayName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "Unknown";

            string name = id;
            name = System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
            name = System.Threading.Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase(name);
            return name;
        }
    }
}
#endif
