using UnityEngine;
using UnityEditor;
using System.Xml;
using System.IO;
using System.Collections.Generic;
using PFE.Data.Definitions;

namespace PFE.Editor.Tools
{
    /// <summary>
    /// XML Converter - Data Librarian Tool
    /// Parses AllData.as XML and generates Unity ScriptableObject assets.
    ///
    /// This tool converts the legacy ActionScript 3 XML data format into Unity's
    /// ScriptableObject system, enabling inspector-based editing and type safety.
    ///
    /// Usage:
    /// 1. Place AllData.as in project root (or update sourcePath below)
    /// 2. Assets > PFE Tools > Convert AllData.xml
    /// 3. Assets will be created at Assets/_PFE/Data/Generated/
    ///
    /// Reference: docs/task3_data_architecture/01_xml_schemas.md
    /// Source: pfe/scripts/fe/AllData.as
    /// </summary>
    public class XMLConverter : EditorWindow
    {
        // Path configuration
        private const string SourcePath = "AllData.as";
        private const string OutputPath = "Assets/_PFE/Data/Generated";

        // Conversion statistics
        private int unitsProcessed = 0;
        private int weaponsProcessed = 0;
        private int itemsProcessed = 0;
        private int perksProcessed = 0;
        private int errorsCount = 0;

        private Vector2 scrollPosition;
        private string statusMessage = "Ready to convert";

        [MenuItem("PFE Tools/XML Converter")]
        public static void ShowWindow()
        {
            var window = GetWindow<XMLConverter>("XML Converter");
            window.minSize = new Vector2(400, 300);
        }

        /// <summary>
        /// Static entry point for batch mode execution.
        /// Usage: Unity.exe -executeMethod PFE.Editor.Tools.XMLConverter.RunConversion
        /// </summary>
        public static void RunConversion()
        {
            var converter = CreateInstance<XMLConverter>();
            converter.ConvertAllData();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("PFE XML Data Converter", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Converts AllData.as XML to Unity ScriptableObjects.\n" +
                "Source: " + SourcePath + "\n" +
                "Output: " + OutputPath,
                MessageType.Info
            );

            EditorGUILayout.Space();

            // Statistics display
            EditorGUILayout.LabelField("Conversion Statistics", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Units Processed: {unitsProcessed}");
            EditorGUILayout.LabelField($"Weapons Processed: {weaponsProcessed}");
            EditorGUILayout.LabelField($"Items Processed: {itemsProcessed}");
            EditorGUILayout.LabelField($"Perks Processed: {perksProcessed}");
            EditorGUILayout.LabelField($"Errors: {errorsCount}");

            EditorGUILayout.Space();

            // Status message
            if (!string.IsNullOrEmpty(statusMessage))
            {
                MessageType messageType = errorsCount > 0 ? MessageType.Warning : MessageType.Info;
                EditorGUILayout.HelpBox(statusMessage, messageType);
            }

            EditorGUILayout.Space();

            // Buttons
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Convert AllData.xml", GUILayout.Height(30)))
                {
                    ConvertAllData();
                }

                if (GUILayout.Button("Clear Output", GUILayout.Height(30)))
                {
                    ClearGeneratedData();
                }
            }

            // Log scroll view
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Conversion Log", EditorStyles.boldLabel);
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(150));
            EditorGUILayout.LabelField("Ready to start conversion...");
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Main conversion entry point - parses AllData.as and creates assets.
        /// </summary>
        private void ConvertAllData()
        {
            Debug.Log("[XMLConverter] Starting conversion...");

            // Reset counters
            unitsProcessed = 0;
            weaponsProcessed = 0;
            itemsProcessed = 0;
            perksProcessed = 0;
            errorsCount = 0;

            // Ensure output directory exists
            if (!AssetDatabase.IsValidFolder(OutputPath))
            {
                string parent = Path.GetDirectoryName(OutputPath);
                string folder = Path.GetFileName(OutputPath);
                Directory.CreateDirectory(OutputPath);
                AssetDatabase.Refresh();
            }

            // Parse XML file
            string sourceFile = Path.Combine(Application.dataPath, "..", SourcePath);

            if (!File.Exists(sourceFile))
            {
                statusMessage = $"ERROR: Source file not found: {sourceFile}";
                Debug.LogError($"[XMLConverter] {statusMessage}");
                errorsCount++;
                return;
            }

            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.Load(sourceFile);

                // Extract XML from AllData.d (embedded XML literal)
                XmlNode allDataNode = xmlDoc.SelectSingleNode("//all");

                if (allDataNode == null)
                {
                    // Try reading as raw XML (AllData.as contains inline XML)
                    string content = File.ReadAllText(sourceFile);
                    int xmlStart = content.IndexOf("<all>");
                    int xmlEnd = content.LastIndexOf("</all>") + 6;

                    if (xmlStart >= 0 && xmlEnd > xmlStart)
                    {
                        string xmlContent = content.Substring(xmlStart, xmlEnd - xmlStart);
                        xmlDoc.LoadXml(xmlContent);
                        allDataNode = xmlDoc.DocumentElement;
                    }
                }

                if (allDataNode == null)
                {
                    statusMessage = "ERROR: Could not find <all> root element in XML";
                    Debug.LogError("[XMLConverter] " + statusMessage);
                    errorsCount++;
                    return;
                }

                // Process each type of data
                ProcessUnitDefinitions(allDataNode);
                ProcessWeaponDefinitions(allDataNode);
                ProcessItemDefinitions(allDataNode);
                ProcessPerkDefinitions(allDataNode);

                // Save and refresh
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                statusMessage = $"Conversion complete!\n" +
                               $"Units: {unitsProcessed}, Weapons: {weaponsProcessed}, Items: {itemsProcessed}, Perks: {perksProcessed}\n" +
                               $"Errors: {errorsCount}";

                Debug.Log($"[XMLConverter] {statusMessage}");
            }
            catch (System.Exception ex)
            {
                statusMessage = $"ERROR: {ex.Message}";
                Debug.LogError($"[XMLConverter] Exception: {ex}");
                errorsCount++;
            }
        }

        /// <summary>
        /// Process unit (<unit>) elements from XML.
        /// </summary>
        private void ProcessUnitDefinitions(XmlNode root)
        {
            XmlNodeList unitNodes = root.SelectNodes(".//unit");

            foreach (XmlNode unitNode in unitNodes)
            {
                try
                {
                    string unitId = GetAttribute(unitNode, "id");

                    if (string.IsNullOrEmpty(unitId) || unitId == "training")
                    {
                        continue; // Skip template/empty units
                    }

                    // Create ScriptableObject asset
                    UnitDefinition unitDef = ScriptableObject.CreateInstance<UnitDefinition>();
                    PopulateUnitData(unitDef, unitNode);

                    // Save asset
                    string assetPath = $"{OutputPath}/Units/{unitId}.asset";
                    EnsureDirectoryExists(assetPath);
                    AssetDatabase.CreateAsset(unitDef, assetPath);

                    unitsProcessed++;
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[XMLConverter] Error processing unit: {ex.Message}");
                    errorsCount++;
                }
            }
        }

        /// <summary>
        /// Process weapon (<weapon>) elements from XML.
        /// </summary>
        private void ProcessWeaponDefinitions(XmlNode root)
        {
            XmlNodeList weaponNodes = root.SelectNodes(".//weapon");

            foreach (XmlNode weaponNode in weaponNodes)
            {
                try
                {
                    string weaponId = GetAttribute(weaponNode, "id");

                    if (string.IsNullOrEmpty(weaponId))
                    {
                        continue;
                    }

                    // Create ScriptableObject asset
                    WeaponDefinition weaponDef = ScriptableObject.CreateInstance<WeaponDefinition>();
                    PopulateWeaponData(weaponDef, weaponNode);

                    // Save asset
                    string assetPath = $"{OutputPath}/Weapons/{weaponId}.asset";
                    EnsureDirectoryExists(assetPath);
                    AssetDatabase.CreateAsset(weaponDef, assetPath);

                    weaponsProcessed++;
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[XMLConverter] Error processing weapon: {ex.Message}");
                    errorsCount++;
                }
            }
        }

        /// <summary>
        /// Process item (<item>) elements from XML.
        /// </summary>
        private void ProcessItemDefinitions(XmlNode root)
        {
            XmlNodeList itemNodes = root.SelectNodes(".//item");

            foreach (XmlNode itemNode in itemNodes)
            {
                try
                {
                    string itemId = GetAttribute(itemNode, "id");

                    if (string.IsNullOrEmpty(itemId))
                    {
                        continue;
                    }

                    // Create ScriptableObject asset
                    ItemDefinition itemDef = ScriptableObject.CreateInstance<ItemDefinition>();
                    PopulateItemData(itemDef, itemNode);

                    // Save asset
                    string assetPath = $"{OutputPath}/Items/{itemId}.asset";
                    EnsureDirectoryExists(assetPath);
                    AssetDatabase.CreateAsset(itemDef, assetPath);

                    itemsProcessed++;
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[XMLConverter] Error processing item: {ex.Message}");
                    errorsCount++;
                }
            }
        }

        /// <summary>
        /// Process perk (<perk>) elements from XML.
        /// </summary>
        private void ProcessPerkDefinitions(XmlNode root)
        {
            XmlNodeList perkNodes = root.SelectNodes(".//perk");

            foreach (XmlNode perkNode in perkNodes)
            {
                try
                {
                    string perkId = GetAttribute(perkNode, "id");

                    if (string.IsNullOrEmpty(perkId))
                    {
                        continue;
                    }

                    // Create ScriptableObject asset
                    PerkDefinition perkDef = ScriptableObject.CreateInstance<PerkDefinition>();
                    PopulatePerkData(perkDef, perkNode);

                    // Save asset
                    string assetPath = $"{OutputPath}/Perks/{perkId}.asset";
                    EnsureDirectoryExists(assetPath);
                    AssetDatabase.CreateAsset(perkDef, assetPath);

                    perksProcessed++;
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[XMLConverter] Error processing perk: {ex.Message}");
                    errorsCount++;
                }
            }
        }

        /// <summary>
        /// Populate UnitDefinition from XML <unit> node.
        /// Reference: docs/task3_data_architecture/01_xml_schemas.md section 1
        /// </summary>
        private void PopulateUnitData(UnitDefinition def, XmlNode node)
        {
            // Core identity
            def.id = GetAttribute(node, "id");

            // Physics data (convert pixels to units: 55px = 0.55 units)
            XmlNode phisNode = node.SelectSingleNode("phis");
            if (phisNode != null)
            {
                float sX = ParseFloat(GetAttribute(phisNode, "sX"));
                float sY = ParseFloat(GetAttribute(phisNode, "sY"));
                def.width = sX > 0 ? sX / 100f : 0.55f; // Convert px to units
                def.height = sY > 0 ? sY / 100f : 0.70f;
                def.mass = ParseFloat(GetAttribute(phisNode, "massa"));
            }

            // Movement data
            XmlNode moveNode = node.SelectSingleNode("move");
            if (moveNode != null)
            {
                def.moveSpeed = ParseFloat(GetAttribute(moveNode, "speed"));
                def.runMultiplier = ParseFloat(GetAttribute(moveNode, "run"));
                def.acceleration = ParseFloat(GetAttribute(moveNode, "accel"));
                def.jumpForce = ParseFloat(GetAttribute(moveNode, "jump"));
            }

            // Combat data
            XmlNode combNode = node.SelectSingleNode("comb");
            if (combNode != null)
            {
                def.health = (int)ParseFloat(GetAttribute(combNode, "hp"));
                def.damage = (int)ParseFloat(GetAttribute(combNode, "damage"));
            }

            // Display name
            XmlNode nameNode = node.SelectSingleNode("n");
            if (nameNode != null)
            {
                def.displayName = nameNode.InnerText;
            }
        }

        /// <summary>
        /// Populate WeaponDefinition from XML <weapon> node.
        /// </summary>
        private void PopulateWeaponData(WeaponDefinition def, XmlNode node)
        {
            // Core identity
            def.weaponId = GetAttribute(node, "id");

            // Weapon type
            string tipStr = GetAttribute(node, "tip");
            if (!string.IsNullOrEmpty(tipStr))
            {
                def.weaponType = (WeaponType)int.Parse(tipStr);
            }

            // Characteristic data
            XmlNode charNode = node.SelectSingleNode("char");
            if (charNode != null)
            {
                def.baseDamage = ParseFloat(GetAttribute(charNode, "damage"));
                def.rapid = ParseFloat(GetAttribute(charNode, "rapid"));
            }

            // Physics
            XmlNode phisNode = node.SelectSingleNode("phis");
            if (phisNode != null)
            {
                // Note: WeaponDefinition doesn't have projectileSpeed or accuracy fields yet
                // These would need to be added to WeaponDefinition.cs
            }
        }

        /// <summary>
        /// Populate ItemDefinition from XML <item> node.
        /// </summary>
        private void PopulateItemData(ItemDefinition def, XmlNode node)
        {
            // Core identity
            def.itemId = GetAttribute(node, "id");
            def.baseItemId = GetAttribute(node, "base");

            // Item type (tip='a' = ammo, 'm' = medical, 'b' = book, etc.)
            string tipStr = GetAttribute(node, "tip");
            if (!string.IsNullOrEmpty(tipStr))
            {
                def.type = ParseItemType(tipStr);
            }

            // Acquisition data
            def.dropChance = ParseFloat(GetAttribute(node, "chance"));
            def.requiredStoryStage = ParseInt(GetAttribute(node, "stage"));
            def.requiredLevel = ParseInt(GetAttribute(node, "lvl"));
            def.basePrice = (int)ParseFloat(GetAttribute(node, "price"));
            def.sellPrice = (int)ParseFloat(GetAttribute(node, "sell"));
            def.weight = ParseFloat(GetAttribute(node, "m"));
            def.stackSize = ParseInt(GetAttribute(node, "kol"));

            // Modifier type
            def.modifierType = ParseInt(GetAttribute(node, "mod"));

            // Ammo variant data
            if (def.type == ItemType.Ammo)
            {
                def.ammoVariant = new AmmoVariantData
                {
                    armorPiercingBonus = ParseInt(GetAttribute(node, "pier")),
                    damageMultiplier = ParseFloat(GetAttribute(node, "damage")),
                    armorMultiplier = ParseFloat(GetAttribute(node, "armor")),
                    knockbackMultiplier = ParseFloat(GetAttribute(node, "knock")),
                    precisionMultiplier = ParseFloat(GetAttribute(node, "probiv"))  // Note: probiv is probability, not precision
                };
            }

            // Display name (if present as inner text or child node)
            // Items in AllData.as typically don't have display names - they're generated from ID
            def.displayName = GenerateItemDisplayName(def.itemId);
            def.description = $"Item: {def.itemId}";
        }

        /// <summary>
        /// Populate PerkDefinition from XML <perk> node.
        /// </summary>
        private void PopulatePerkData(PerkDefinition def, XmlNode node)
        {
            // Core identity
            def.perkId = GetAttribute(node, "id");

            // Perk type
            string tipStr = GetAttribute(node, "tip");
            if (!string.IsNullOrEmpty(tipStr))
            {
                def.type = (PerkType)int.Parse(tipStr);
            }

            // Max ranks
            def.maxRanks = ParseInt(GetAttribute(node, "lvl"));
            if (def.maxRanks == 0) def.maxRanks = 1;

            // Requirements
            XmlNodeList reqNodes = node.SelectNodes(".//req");
            if (reqNodes.Count > 0)
            {
                def.requirements = new PerkRequirement[reqNodes.Count];
                for (int i = 0; i < reqNodes.Count; i++)
                {
                    def.requirements[i] = ParsePerkRequirement(reqNodes[i]);
                }
            }

            // Effects (skill modifiers)
            XmlNodeList skNodes = node.SelectNodes(".//sk");
            if (skNodes.Count > 0)
            {
                def.effects = new SkillModifier[skNodes.Count];
                for (int i = 0; i < skNodes.Count; i++)
                {
                    def.effects[i] = ParseSkillModifier(skNodes[i]);
                }
            }

            // Display values (textvar elements)
            XmlNodeList textVarNodes = node.SelectNodes(".//textvar");
            if (textVarNodes.Count > 0)
            {
                def.displayValues = new string[textVarNodes.Count];
                for (int i = 0; i < textVarNodes.Count; i++)
                {
                    def.displayValues[i] = textVarNodes[i].InnerText;
                }
            }

            // Display name
            def.displayName = GeneratePerkDisplayName(def.perkId);
            def.description = $"Perk: {def.perkId}";
        }

        /// <summary>
        /// Parse item type from tip attribute.
        /// tip='a' = Ammo, 'm' = Medical, 'b' = Book, 'c' = Component, 'e' = Equipment
        /// </summary>
        private ItemType ParseItemType(string tip)
        {
            switch (tip.ToLower())
            {
                case "a": return ItemType.Ammo;
                case "m": return ItemType.Medical;
                case "b": return ItemType.Book;
                case "c": return ItemType.Component;
                case "e": return ItemType.Equipment;
                case "s": return ItemType.Chems;
                case "i": return ItemType.Implant;
                case "k": return ItemType.Key;
                case "q": return ItemType.Quest;
                case "v": return ItemType.Valuable;
                default: return ItemType.Misc;
            }
        }

        /// <summary>
        /// Parse perk requirement from XML <req> node.
        /// </summary>
        private PerkRequirement ParsePerkRequirement(XmlNode reqNode)
        {
            PerkRequirement req = new PerkRequirement();

            string id = GetAttribute(reqNode, "id");
            req.skillId = id;
            req.level = ParseInt(GetAttribute(reqNode, "lvl"));
            req.levelDelta = ParseInt(GetAttribute(reqNode, "dlvl"));

            // Determine requirement type from id
            if (id == "level")
            {
                req.type = RequirementType.Level;
            }
            else if (id == "guns" || id == "energy" || id == "melee" || id == "explosives")
            {
                req.type = RequirementType.Guns;
            }
            else
            {
                req.type = RequirementType.Skill;
            }

            return req;
        }

        /// <summary>
        /// Parse skill modifier from XML <sk> node.
        /// </summary>
        private SkillModifier ParseSkillModifier(XmlNode skNode)
        {
            SkillModifier mod = new SkillModifier();

            mod.skillId = GetAttribute(skNode, "id");

            // Parse value fields (v0 through v5)
            mod.value = ParseFloat(GetAttribute(skNode, "v1"));

            // Check if multiplier (tip='m')
            string tip = GetAttribute(skNode, "tip");
            mod.isMultiplier = (tip == "m");

            // Check if weapon skill
            mod.isWeaponSkill = (tip == "w");

            return mod;
        }

        /// <summary>
        /// Generate display name for item from ID.
        /// </summary>
        private string GenerateItemDisplayName(string itemId)
        {
            // Capitalize first letter and replace underscores
            if (string.IsNullOrEmpty(itemId)) return "Unknown Item";

            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(itemId.Replace("_", " ").ToLower());
        }

        /// <summary>
        /// Generate display name for perk from ID.
        /// </summary>
        private string GeneratePerkDisplayName(string perkId)
        {
            // Capitalize first letter and replace underscores
            if (string.IsNullOrEmpty(perkId)) return "Unknown Perk";

            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(perkId.Replace("_", " ").ToLower());
        }

        /// <summary>
        /// Helper: Get XML attribute value, return empty string if not found.
        /// </summary>
        private string GetAttribute(XmlNode node, string attributeName)
        {
            if (node == null || node.Attributes[attributeName] == null)
            {
                return string.Empty;
            }
            return node.Attributes[attributeName].Value;
        }

        /// <summary>
        /// Helper: Parse float with default fallback.
        /// </summary>
        private float ParseFloat(string value, float defaultValue = 0f)
        {
            if (string.IsNullOrEmpty(value))
            {
                return defaultValue;
            }
            return float.TryParse(value, out float result) ? result : defaultValue;
        }

        /// <summary>
        /// Helper: Parse int with default fallback.
        /// </summary>
        private int ParseInt(string value, int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(value))
            {
                return defaultValue;
            }
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        /// <summary>
        /// Helper: Ensure directory exists for asset path.
        /// </summary>
        private void EnsureDirectoryExists(string assetPath)
        {
            string directory = Path.GetDirectoryName(assetPath);

            if (!string.IsNullOrEmpty(directory) && !AssetDatabase.IsValidFolder(directory))
            {
                string parent = Path.GetDirectoryName(directory);
                string folderName = Path.GetFileName(directory);

                // Recursively create parent directories
                if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                {
                    EnsureDirectoryExists(parent);
                }

                // Create this directory
                if (!AssetDatabase.IsValidFolder(directory))
                {
                    AssetDatabase.CreateFolder(parent, folderName);
                }
            }
        }

        /// <summary>
        /// Clear all generated data assets.
        /// </summary>
        private void ClearGeneratedData()
        {
            if (!AssetDatabase.IsValidFolder(OutputPath))
            {
                statusMessage = "No generated data to clear.";
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "Clear Generated Data",
                $"Delete all assets in {OutputPath}?\nThis cannot be undone.",
                "Delete", "Cancel"
            );

            if (confirmed)
            {
                string fullPath = Path.Combine(Directory.GetCurrentDirectory(), OutputPath);
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: true);
                    File.Delete(Path.Combine(Directory.GetCurrentDirectory(), OutputPath + ".meta"));
                    AssetDatabase.Refresh();
                    statusMessage = "Generated data cleared.";
                    Debug.Log("[XMLConverter] Generated data cleared.");
                }
            }
        }
    }
}
