#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using PFE.Systems.RPG.Data;

namespace PFE.Systems.RPG.Editor
{
    /// <summary>
    /// Editor tool to import skill and perk data from AllData.as XML to ScriptableObjects.
    /// Source: pfe/scripts/fe/AllData.as lines 5209+
    /// </summary>
    public class RPGDataImporter : EditorWindow
    {
        private string xmlFilePath = "";
        private string outputFolder = "Assets/_PFE/Systems/RPG/Data";
        private Vector2 scrollPosition;
        private string importLog = "";

        [MenuItem("Tools/RPG/Data Importer")]
        public static void ShowWindow()
        {
            GetWindow<RPGDataImporter>("RPG Data Importer");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("RPG Data Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Imports skill and perk definitions from AllData.as XML to ScriptableObject assets.",
                MessageType.Info
            );

            EditorGUILayout.Space();

            // XML File Path
            EditorGUILayout.LabelField("AllData.as XML File:", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            xmlFilePath = EditorGUILayout.TextField(xmlFilePath);
            if (GUILayout.Button("Browse", GUILayout.Width(80)))
            {
                xmlFilePath = EditorUtility.OpenFilePanel(
                    "Select AllData.as",
                    PFE.Editor.Importers.SourceImportPaths.FeScriptsRoot,
                    "as"
                );
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // Output Folder
            EditorGUILayout.LabelField("Output Folder:", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.TextField(outputFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(80)))
            {
                outputFolder = EditorUtility.OpenFolderPanel("Select Output Folder", "Assets/_PFE/Systems/RPG/Data", "");
                if (outputFolder.StartsWith(Application.dataPath))
                {
                    outputFolder = "Assets" + outputFolder.Substring(Application.dataPath.Length);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // Import Button
            GUI.enabled = !string.IsNullOrEmpty(xmlFilePath) && File.Exists(xmlFilePath);
            if (GUILayout.Button("Import Data", GUILayout.Height(30)))
            {
                ImportAllData();
            }
            GUI.enabled = true;

            EditorGUILayout.Space();

            // Log Display
            if (!string.IsNullOrEmpty(importLog))
            {
                EditorGUILayout.LabelField("Import Log:", EditorStyles.boldLabel);
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(300));
                EditorGUILayout.TextArea(importLog, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        private void ImportAllData()
        {
            importLog = "";
            int successCount = 0;
            int errorCount = 0;

            try
            {
                LogMessage($"Reading XML from: {xmlFilePath}");
                string xmlContent = ExtractXMLFromAS(xmlFilePath);

                if (string.IsNullOrEmpty(xmlContent))
                {
                    LogMessage("ERROR: Could not extract XML from AllData.as", true);
                    return;
                }

                XDocument doc = XDocument.Parse(xmlContent);
                XElement root = doc.Root;

                // Ensure output folder exists
                if (!AssetDatabase.IsValidFolder(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder.Substring("Assets/".Length));
                    AssetDatabase.Refresh();
                }

                // Import Skills
                var skillElements = root.Descendants("skill").ToList();
                LogMessage($"Found {skillElements.Count} skills to import...");
                successCount += ImportSkills(skillElements);

                // Import Perks
                var perkElements = root.Descendants("perk").Where(p => p.Attribute("id")?.Value != "dead").ToList();
                LogMessage($"Found {perkElements.Count} perks to import...");
                int perkSuccess = ImportPerks(perkElements);
                successCount += perkSuccess;

                AssetDatabase.Refresh();
                LogMessage($"\n=== Import Complete ===");
                LogMessage($"Total: {successCount} assets created");

                // Generate SkillDefinitionDatabase
                GenerateDatabase();
            }
            catch (System.Exception ex)
            {
                LogMessage($"ERROR: {ex.Message}", true);
                LogMessage(ex.StackTrace, true);
                errorCount++;
            }
        }

        private string ExtractXMLFromAS(string asFilePath)
        {
            try
            {
                string[] lines = File.ReadAllLines(asFilePath);
                bool inXmlSection = false;
                System.Text.StringBuilder xmlBuilder = new System.Text.StringBuilder();

                foreach (string line in lines)
                {
                    // Find XML section start (look for <skill id='tele')
                    if (line.Contains("<skill id='") || line.Contains("<perk id='"))
                    {
                        inXmlSection = true;
                    }

                    if (inXmlSection)
                    {
                        xmlBuilder.AppendLine(line.TrimStart('\t'));
                    }

                    // End of perks section
                    if (inXmlSection && line.Contains("</textvar>") && !line.Contains("<"))
                    {
                        // Check if this is really the end
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("</textvar>") && !trimmed.Contains("<textvar"))
                        {
                            // Keep going, there might be more
                        }
                    }

                    // Stop at end of perks
                    if (inXmlSection && line.Trim() == "</textvar>" && !xmlBuilder.ToString().Contains("<perk"))
                    {
                        // Still processing
                    }
                }

                // Wrap in root element
                string xmlContent = xmlBuilder.ToString();

                // Extract from first skill to end of perks
                int skillStart = xmlContent.IndexOf("<skill id='");
                if (skillStart < 0) return null;

                // Find the end (last closing tag we care about)
                int perkEnd = xmlContent.LastIndexOf("</textvar>");
                if (perkEnd > 0)
                {
                    xmlContent = xmlContent.Substring(skillStart, perkEnd - skillStart + 10);
                }

                // Wrap in root
                return $"<rpgdata>{xmlContent}</rpgdata>";
            }
            catch (System.Exception ex)
            {
                LogMessage($"Error reading file: {ex.Message}", true);
                return null;
            }
        }

        private int ImportSkills(List<XElement> skillElements)
        {
            int count = 0;
            foreach (var skillEl in skillElements)
            {
                try
                {
                    string skillId = skillEl.Attribute("id")?.Value;
                    if (string.IsNullOrEmpty(skillId)) continue;

                    SkillDefinition skill = ScriptableObject.CreateInstance<SkillDefinition>();
                    skill.name = $"Skill_{skillId}";
                    skill.skillId = skillId;
                    skill.displayName = ObjectNames.NicifyVariableName(skillId);
                    skill.description = $"Generated from XML for {skillId}";

                    // Parse sort order
                    skill.sortOrder = int.Parse(skillEl.Attribute("sort")?.Value ?? "99");

                    // Check if post-game skill
                    skill.isPostGame = skillEl.Attribute("post")?.Value == "1";
                    skill.maxLevel = skill.isPostGame ? 100 : 20;

                    // Parse skill effects
                    List<StatModifier> modifiers = new List<StatModifier>();
                    List<TextVariable> textVars = new List<TextVariable>();

                    foreach (var skEl in skillEl.Descendants("sk"))
                    {
                        var mod = ParseStatModifier(skEl);
                        if (mod != null)
                            modifiers.Add(mod);
                    }

                    foreach (var tvEl in skillEl.Descendants("textvar"))
                    {
                        string s1 = tvEl.Attribute("s1")?.Value;
                        if (!string.IsNullOrEmpty(s1))
                        {
                            textVars.Add(new TextVariable
                            {
                                key = "s1",
                                value = s1
                            });
                        }
                    }

                    skill.levelEffects = new SkillLevelEffect[]
                    {
                        new SkillLevelEffect
                        {
                            levelThreshold = 0,
                            modifiers = modifiers.ToArray(),
                            textVariables = textVars.ToArray()
                        }
                    };

                    // Save asset
                    string assetPath = $"{outputFolder}/{skill.name}.asset";
                    AssetDatabase.CreateAsset(skill, assetPath);
                    LogMessage($"✓ Created skill: {skillId}");
                    count++;
                }
                catch (System.Exception ex)
                {
                    LogMessage($"ERROR importing skill: {ex.Message}", true);
                }
            }
            return count;
        }

        private int ImportPerks(List<XElement> perkElements)
        {
            int count = 0;
            foreach (var perkEl in perkElements)
            {
                try
                {
                    string perkId = perkEl.Attribute("id")?.Value;
                    if (string.IsNullOrEmpty(perkId)) continue;

                    PerkDefinition perk = ScriptableObject.CreateInstance<PerkDefinition>();
                    perk.name = $"Perk_{perkId}";
                    perk.perkId = perkId;
                    perk.displayName = ObjectNames.NicifyVariableName(perkId);
                    perk.description = $"Generated from XML for {perkId}";

                    // Parse tip (player selectable)
                    perk.isPlayerSelectable = perkEl.Attribute("tip")?.Value == "1";

                    // Parse max level
                    perk.maxRank = int.Parse(perkEl.Attribute("lvl")?.Value ?? "1");

                    // Parse requirements
                    List<PerkRequirement> requirements = new List<PerkRequirement>();
                    foreach (var reqEl in perkEl.Descendants("req"))
                    {
                        string reqId = reqEl.Attribute("id")?.Value;
                        int lvl = int.Parse(reqEl.Attribute("lvl")?.Value ?? "0");
                        int dlvl = int.Parse(reqEl.Attribute("dlvl")?.Value ?? "0");

                        RequirementType type = RequirementType.Level;
                        switch (reqId)
                        {
                            case "level":
                                type = RequirementType.Level;
                                break;
                            case "guns":
                                type = RequirementType.Guns;
                                break;
                            default:
                                type = RequirementType.Skill;
                                break;
                        }

                        requirements.Add(new PerkRequirement
                        {
                            type = type,
                            skillId = reqId,
                            level = lvl,
                            levelDelta = dlvl
                        });
                    }
                    perk.requirements = requirements.ToArray();

                    // Parse effects for each rank
                    List<PerkRankEffect> rankEffects = new List<PerkRankEffect>();

                    for (int rank = 1; rank <= perk.maxRank; rank++)
                    {
                        List<StatModifier> modifiers = new List<StatModifier>();
                        List<TextVariable> textVars = new List<TextVariable>();

                        foreach (var skEl in perkEl.Descendants("sk"))
                        {
                            var mod = ParsePerkStatModifier(skEl, rank);
                            if (mod != null)
                                modifiers.Add(mod);
                        }

                        foreach (var tvEl in perkEl.Descendants("textvar"))
                        {
                            // Get attribute for this rank (s1, s2, etc.)
                            string attrName = $"s{rank}";
                            string val = tvEl.Attribute(attrName)?.Value;
                            if (!string.IsNullOrEmpty(val))
                            {
                                textVars.Add(new TextVariable
                                {
                                    key = attrName,
                                    value = val
                                });
                            }
                        }

                        rankEffects.Add(new PerkRankEffect
                        {
                            rank = rank,
                            modifiers = modifiers.ToArray(),
                            textVariables = textVars.ToArray()
                        });
                    }

                    perk.rankEffects = rankEffects.ToArray();

                    // Save asset
                    string assetPath = $"{outputFolder}/{perk.name}.asset";
                    AssetDatabase.CreateAsset(perk, assetPath);
                    LogMessage($"✓ Created perk: {perkId} (max rank: {perk.maxRank})");
                    count++;
                }
                catch (System.Exception ex)
                {
                    LogMessage($"ERROR importing perk: {ex.Message}", true);
                }
            }
            return count;
        }

        private StatModifier ParseStatModifier(XElement skEl)
        {
            string id = skEl.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(id)) return null;

            var mod = new StatModifier
            {
                statId = id
            };

            // Parse type from tip attribute
            string tip = skEl.Attribute("tip")?.Value;
            if (tip == "weap")
                mod.type = ModifierType.WeaponSkill;
            else if (tip == "res")
                mod.type = ModifierType.Add; // Resistance adds to resistance value
            else if (skEl.Attribute("ref")?.Value == "add")
                mod.type = ModifierType.Add;
            else if (skEl.Attribute("ref")?.Value == "mult")
                mod.type = ModifierType.Multiply;
            else
                mod.type = ModifierType.Set;

            // Parse target
            string targetTip = skEl.Attribute("tip")?.Value;
            if (targetTip == "unit")
                mod.target = ModifierTarget.Unit;
            else
                mod.target = ModifierTarget.Player;

            // Parse values
            mod.values = new float[6];
            for (int i = 0; i < 6; i++)
            {
                string attrName = $"v{i}";
                mod.values[i] = float.Parse(skEl.Attribute(attrName)?.Value ?? "0");
            }

            // Parse value delta
            mod.valueDelta = float.Parse(skEl.Attribute("vd")?.Value ?? "0f");

            // Parse dop (is multiplier)
            mod.isMultiplier = skEl.Attribute("dop")?.Value == "1";

            return mod;
        }

        private StatModifier ParsePerkStatModifier(XElement skEl, int rank)
        {
            string id = skEl.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(id)) return null;

            var mod = new StatModifier
            {
                statId = id
            };

            // Parse type
            string tip = skEl.Attribute("tip")?.Value;
            if (tip == "res")
                mod.type = ModifierType.Add; // Resistance adds to resistance value
            else if (tip == "m")
                return null; // Skip max mana modifiers for now
            else if (skEl.Attribute("ref")?.Value == "add")
                mod.type = ModifierType.Add;
            else if (skEl.Attribute("ref")?.Value == "mult")
                mod.type = ModifierType.Multiply;
            else
                mod.type = ModifierType.Set;

            // Parse target
            string targetTip = skEl.Attribute("tip")?.Value;
            if (targetTip == "unit")
                mod.target = ModifierTarget.Unit;
            else
                mod.target = ModifierTarget.Player;

            // Get value for this rank
            string attrName = $"v{rank}";
            string valStr = skEl.Attribute(attrName)?.Value;

            if (!string.IsNullOrEmpty(valStr))
            {
                mod.values = new float[] { float.Parse(valStr) };
            }
            else if (skEl.Attribute("vd") != null)
            {
                // Has delta, calculate value
                float vd = float.Parse(skEl.Attribute("vd")?.Value ?? "0f");
                mod.values = new float[] { vd };
            }
            else
            {
                mod.values = new float[] { 0 };
            }

            return mod;
        }

        private void GenerateDatabase()
        {
            LogMessage("\nGenerating SkillDefinitionDatabase...");

            // Find or create database
            string dbPath = $"{outputFolder}/SkillDefinitionDatabase.asset";
            SkillDefinitionDatabase db = AssetDatabase.LoadAssetAtPath<SkillDefinitionDatabase>(dbPath);

            if (db == null)
            {
                db = ScriptableObject.CreateInstance<SkillDefinitionDatabase>();
                AssetDatabase.CreateAsset(db, dbPath);
            }

            // Find all skills
            List<SkillDefinition> skills = new List<SkillDefinition>();
            string[] guids = AssetDatabase.FindAssets("t:SkillDefinition", new[] { outputFolder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                skills.Add(AssetDatabase.LoadAssetAtPath<SkillDefinition>(path));
            }

            // We need to use reflection or add a public method to set skills
            // For now, let's use the public property if available
            var dbType = typeof(SkillDefinitionDatabase);
            var skillsField = dbType.GetField("skillDefinitions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (skillsField != null)
            {
                skillsField.SetValue(db, skills.ToArray());
            }
            EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssets();

            LogMessage($"✓ Database created with {skills.Count} skills");
        }

        private void LogMessage(string message, bool isError = false)
        {
            if (isError)
                importLog += $"<color=red>{message}</color>\n";
            else
                importLog += message + "\n";

            Debug.Log($"[RPGDataImporter] {message}");
            Repaint();
        }
    }
}
#endif
