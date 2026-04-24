using UnityEngine;
using UnityEditor;
using System.Xml;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;

namespace PFE.Editor.DataConverter
{
    /// <summary>
    /// Converts ActionScript XML data from AllData.as to Unity ScriptableObjects.
    /// Phase 1: Preview and Analysis tool
    /// Phase 2: Full conversion (with proper field access)
    /// </summary>
    public class DataConverterWindow : EditorWindow
    {
        private string sourceFilePath = Importers.SourceImportPaths.AllDataAsPath;
        private Vector2 scrollPosition;
        private string analysisReport = "";
        private Dictionary<string, int> dataCounts = new Dictionary<string, int>();

        [MenuItem("Tools/PFE Data/Data Converter")]
        public static void ShowWindow()
        {
            GetWindow<DataConverterWindow>("PFE Data Converter");
        }

        void OnGUI()
        {
            GUILayout.Label("PFE ActionScript Data Converter", EditorStyles.boldLabel);

            EditorGUILayout.Space();

            // Source file
            GUILayout.Label("Source File (AllData.as):", EditorStyles.label);
            EditorGUILayout.BeginHorizontal();
            sourceFilePath = EditorGUILayout.TextField(sourceFilePath);
            if (GUILayout.Button("Browse", GUILayout.Width(80)))
            {
                sourceFilePath = EditorUtility.OpenFilePanel("Select AllData.as", "", "as");
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // Analyze button
            EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath));
            if (GUILayout.Button("Analyze Source Data", GUILayout.Height(25)))
            {
                AnalyzeSourceFile();
            }
            EditorGUI.EndDisabledGroup();

            // Report
            if (!string.IsNullOrEmpty(analysisReport))
            {
                EditorGUILayout.Space();
                GUILayout.Label("Analysis Report:", EditorStyles.boldLabel);

                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(300));
                EditorGUILayout.HelpBox(analysisReport, MessageType.None);
                EditorGUILayout.EndScrollView();
            }

            // Data counts
            if (dataCounts.Count > 0)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Data Entry Counts:", EditorStyles.boldLabel);

                foreach (var kvp in dataCounts)
                {
                    EditorGUILayout.LabelField($"{kvp.Key}:", kvp.Value.ToString());
                }

                int total = dataCounts.Values.Sum();
                EditorGUILayout.HelpBox($"Total Entries: {total}", MessageType.Info);
            }
        }

        private void AnalyzeSourceFile()
        {
            analysisReport = "";
            dataCounts.Clear();

            try
            {
                string fileContent = File.ReadAllText(sourceFilePath);

                // Extract XML content
                int xmlStart = fileContent.IndexOf("<all>");
                int xmlEnd = fileContent.IndexOf("</all>");

                if (xmlStart < 0 || xmlEnd < 0)
                {
                    analysisReport = "Error: Could not find <all> tags in file.";
                    EditorUtility.DisplayDialog("Error", "Could not find <all> tags in file", "OK");
                    return;
                }

                string xmlContent = fileContent.Substring(xmlStart, xmlEnd - xmlStart + 6);

                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(xmlContent);

                analysisReport = "XML loaded successfully!\n\n";

                // Count all data types
                CountDataTypes(xmlDoc);

                // Sample data
                analysisReport += SampleData(xmlDoc);

                analysisReport += $"\nFile size: {fileContent.Length:N0} characters";
                analysisReport += $"\nXML lines: {xmlContent.Split('\n').Length:N0}";
            }
            catch (System.Exception e)
            {
                analysisReport = $"Error: {e.Message}\n\n{e.StackTrace}";
                EditorUtility.DisplayDialog("Analysis Error", e.Message, "OK");
            }
        }

        private void CountDataTypes(XmlDocument xmlDoc)
        {
            // Units
            XmlNodeList units = xmlDoc.SelectNodes("//unit");
            dataCounts["Units"] = units.Count;

            // Weapons
            XmlNodeList weapons = xmlDoc.SelectNodes("//weapon");
            dataCounts["Weapons"] = weapons.Count;

            // Items
            XmlNodeList items = xmlDoc.SelectNodes("//item");
            dataCounts["Items"] = items.Count;

            // Ammo
            XmlNodeList ammo = xmlDoc.SelectNodes("//ammo");
            dataCounts["Ammo"] = ammo.Count;

            // Perks
            XmlNodeList perks = xmlDoc.SelectNodes("//perk");
            dataCounts["Perks"] = perks.Count;

            // Effects
            XmlNodeList effects = xmlDoc.SelectNodes("//effect");
            dataCounts["Effects"] = effects.Count;

            analysisReport += "Data Type Counts:\n";
            foreach (var kvp in dataCounts)
            {
                analysisReport += $"  {kvp.Key}: {kvp.Value}\n";
            }
            analysisReport += "\n";
        }

        private string SampleData(XmlDocument xmlDoc)
        {
            string sample = "Sample Data:\n\n";

            // Sample unit
            XmlNodeList units = xmlDoc.SelectNodes("//unit");
            if (units.Count > 0)
            {
                XmlNode unit = units[0];
                sample += "--- Unit Sample ---\n";
                sample += $"ID: {GetAttribute(unit, "id")}\n";
                sample += $"Category: {GetAttribute(unit, "cat")} (1=Template, 2=Faction, 3=Spawnable)\n";
                sample += $"Faction: {GetAttribute(unit, "fraction")} (0=Neutral, 1=Player, 2=Enemy)\n";
                sample += $"XP: {GetAttribute(unit, "xp")}\n";
                sample += $"Parent: {GetAttribute(unit, "parent")}\n";

                XmlNode phis = unit["phis"];
                if (phis != null)
                {
                    sample += $"Size: {GetAttribute(phis, "sX")}x{GetAttribute(phis, "sY")}, Mass: {GetAttribute(phis, "massa")}\n";
                }

                XmlNode move = unit["move"];
                if (move != null)
                {
                    sample += $"Speed: {GetAttribute(move, "speed")}, Jump: {GetAttribute(move, "jump")}\n";
                }

                XmlNode comb = unit["comb"];
                if (comb != null)
                {
                    sample += $"HP: {GetAttribute(comb, "hp")}, Damage: {GetAttribute(comb, "damage")}\n";
                }

                XmlNode name = unit["n"];
                if (name != null)
                {
                    sample += $"Name: {name.InnerText}\n";
                }

                sample += "\n";
            }

            // Sample weapon
            XmlNodeList weapons = xmlDoc.SelectNodes("//weapon");
            if (weapons.Count > 0)
            {
                XmlNode weapon = weapons[0];
                sample += "--- Weapon Sample ---\n";
                sample += $"ID: {GetAttribute(weapon, "id")}\n";
                sample += $"Type: {GetAttribute(weapon, "tip")} (0=Internal, 1=Melee, 2=Guns, 3=BigGun)\n";
                sample += $"Category: {GetAttribute(weapon, "cat")} (0=Unarmed, 1=Melee, 2=Pistol, etc.)\n";
                sample += $"Skill Required: {GetAttribute(weapon, "skill")}\n";

                XmlNode charNode = weapon["char"];
                if (charNode != null)
                {
                    sample += $"Damage: {GetAttribute(charNode, "damage")}\n";
                    sample += $"Fire Rate: {GetAttribute(charNode, "rapid")}\n";
                    sample += $"Crit Chance: {GetAttribute(charNode, "crit")}\n";
                }

                XmlNode phis = weapon["phis"];
                if (phis != null)
                {
                    sample += $"Speed: {GetAttribute(phis, "speed")}\n";
                    sample += $"Deviation: {GetAttribute(phis, "deviation")}\n";
                }

                XmlNode spec = weapon["spec"];
                if (spec != null)
                {
                    sample += $"Magazine: {GetAttribute(spec, "holder")}\n";
                    sample += $"Ammo Type: {GetAttribute(spec, "ammo")}\n";
                }

                sample += "\n";
            }

            // Sample item
            XmlNodeList items = xmlDoc.SelectNodes("//item");
            if (items.Count > 0)
            {
                XmlNode item = items[0];
                sample += "--- Item Sample ---\n";
                sample += $"ID: {GetAttribute(item, "id")}\n";
                sample += $"Type: {GetAttribute(item, "tip")} (a=Ammo, m=Medical, b=Book, c=Component, e=Equipment)\n";
                sample += $"Price: {GetAttribute(item, "price")}\n";
                sample += $"Weight: {GetAttribute(item, "massa")}\n";

                XmlNode name = item["n"];
                if (name != null)
                {
                    sample += $"Name: {name.InnerText}\n";
                }
            }

            return sample;
        }

        private string GetAttribute(XmlNode node, string name)
        {
            if (node == null || node.Attributes[name] == null) return "";
            return node.Attributes[name].Value;
        }

        private string GetAttribute(XmlNode node, string name, string defaultValue)
        {
            string value = GetAttribute(node, name);
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }
    }
}
