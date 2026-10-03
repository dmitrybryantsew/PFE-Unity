using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using PFE.Systems.RPG.Data;
using PerkDefinition = PFE.Systems.RPG.Data.PerkDefinition;
using SkillDefinition = PFE.Systems.RPG.Data.SkillDefinition;
using StatModifier = PFE.Systems.RPG.Data.StatModifier;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports SkillDefinition and PerkDefinition assets from AllData.as.
    /// Creates ScriptableObjects for all 16 skills and 84 authored perks.
    /// Also builds and links the central SkillDefinitionDatabase asset.
    /// </summary>
    public static class SkillAndPerkDataImporter
    {
        private static string AllDataPath => SourceImportPaths.AllDataAsPath;

        public const string SkillsDirectory = "Assets/_PFE/Data/Resources/Skills";
        public const string PerksDirectory = "Assets/_PFE/Data/Resources/RPG/Perks";
        public const string DatabasePath = "Assets/_PFE/Data/Resources/SkillDefinitionDatabase.asset";
        public const string LevelCurvePath = "Assets/_PFE/Data/Resources/LevelCurve.asset";

        [MenuItem("PFE/Data/Import Skills and Perks from AllData.as")]
        public static void ImportAllMenu()
        {
            ImportAll(interactive: true);
        }

        public static void ImportAll(bool interactive = false)
        {
            string content = LoadAllDataContent();
            if (string.IsNullOrEmpty(content))
            {
                Debug.LogError($"[SkillAndPerkDataImporter] Could not find AllData.as at {AllDataPath}");
                return;
            }

            EnsureDirectories();

            var skills = ImportSkills(content);
            var perks = ImportPerks(content);
            var levelCurve = EnsureLevelCurve();
            var db = CreateOrUpdateDatabase(skills, perks, levelCurve);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[SkillAndPerkDataImporter] Successfully imported {skills.Count} skills and {perks.Count} perks into {DatabasePath}");
            if (interactive)
            {
                EditorUtility.DisplayDialog(
                    "Skill & Perk Import Complete",
                    $"Successfully imported:\n- {skills.Count} Skills\n- {perks.Count} Perks\nLinked in {DatabasePath}",
                    "OK"
                );
            }
        }

        public static string LoadAllDataContent()
        {
            if (File.Exists(AllDataPath))
            {
                return File.ReadAllText(AllDataPath);
            }
            if (File.Exists("AllData.as"))
            {
                return File.ReadAllText("AllData.as");
            }
            if (File.Exists("E:\\Games\\UnityGames\\pfeToUnity\\pfe\\scripts\\fe\\AllData.as"))
            {
                return File.ReadAllText("E:\\Games\\UnityGames\\pfeToUnity\\pfe\\scripts\\fe\\AllData.as");
            }
            return null;
        }

        public static void EnsureDirectories()
        {
            if (!Directory.Exists(SkillsDirectory))
                Directory.CreateDirectory(SkillsDirectory);
            if (!Directory.Exists(PerksDirectory))
                Directory.CreateDirectory(PerksDirectory);
            if (!Directory.Exists("Assets/_PFE/Data/Resources"))
                Directory.CreateDirectory("Assets/_PFE/Data/Resources");
        }

        public static List<SkillDefinition> ImportSkills(string content)
        {
            var skills = new List<SkillDefinition>();
            var pattern = @"<skill\s+([^>]+)>(.*?)</skill>";
            var matches = Regex.Matches(content, pattern, RegexOptions.Singleline);

            foreach (Match m in matches)
            {
                string attrStr = m.Groups[1].Value;
                string body = m.Groups[2].Value;

                string id = GetAttribute(attrStr, "id");
                if (string.IsNullOrEmpty(id)) continue;

                string sortStr = GetAttribute(attrStr, "sort");
                int.TryParse(sortStr, out int sort);

                string postStr = GetAttribute(attrStr, "post");
                bool isPost = postStr == "1";

                string assetPath = $"{SkillsDirectory}/{id}.asset";
                var skillDef = AssetDatabase.LoadAssetAtPath<SkillDefinition>(assetPath);
                if (skillDef == null)
                {
                    skillDef = ScriptableObject.CreateInstance<SkillDefinition>();
                    AssetDatabase.CreateAsset(skillDef, assetPath);
                }

                skillDef.skillId = id;
                skillDef.displayName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(id);
                skillDef.sortOrder = sort;
                skillDef.isPostGame = isPost;
                skillDef.maxLevel = isPost ? 100 : 20;

                skillDef.modifiers = ParseModifiers(body);

                EditorUtility.SetDirty(skillDef);
                skills.Add(skillDef);
            }

            return skills;
        }

        public static List<PerkDefinition> ImportPerks(string content)
        {
            var perks = new List<PerkDefinition>();
            var pattern = @"<perk\s+([^>]+)>(.*?)</perk>";
            var matches = Regex.Matches(content, pattern, RegexOptions.Singleline);

            foreach (Match m in matches)
            {
                string attrStr = m.Groups[1].Value;
                string body = m.Groups[2].Value;

                string id = GetAttribute(attrStr, "id");
                if (string.IsNullOrEmpty(id)) continue;

                string tipStr = GetAttribute(attrStr, "tip");
                bool isSelectable = tipStr == "1";

                string lvlStr = GetAttribute(attrStr, "lvl");
                int maxRanks = 1;
                if (!string.IsNullOrEmpty(lvlStr) && int.TryParse(lvlStr, out int parsedLvl) && parsedLvl > 0)
                {
                    maxRanks = parsedLvl;
                }

                string assetPath = $"{PerksDirectory}/{id}.asset";
                var perkDef = AssetDatabase.LoadAssetAtPath<PerkDefinition>(assetPath);
                if (perkDef == null)
                {
                    perkDef = ScriptableObject.CreateInstance<PerkDefinition>();
                    AssetDatabase.CreateAsset(perkDef, assetPath);
                }

                perkDef.perkId = id;
                perkDef.displayName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(id.Replace('_', ' '));
                perkDef.isPlayerSelectable = isSelectable;
                perkDef.maxRank = maxRanks;

                perkDef.requirements = ParseRequirements(body);
                perkDef.modifiers = ParseModifiers(body);

                EditorUtility.SetDirty(perkDef);
                perks.Add(perkDef);
            }

            return perks;
        }

        public static LevelCurve EnsureLevelCurve()
        {
            var curve = AssetDatabase.LoadAssetAtPath<LevelCurve>(LevelCurvePath);
            if (curve == null)
            {
                curve = ScriptableObject.CreateInstance<LevelCurve>();
                curve.xpDelta = 5000;
                curve.skillPointsPerLevel = 5;
                curve.baseHp = 100;
                curve.hpPerLevel = 15;
                curve.organHpPerLevel = 40;
                curve.baseOrganHp = 200;
                curve.postSkillThresholds = new int[] { 5, 11, 18, 26, 35, 45, 56, 68, 82, 100 };
                AssetDatabase.CreateAsset(curve, LevelCurvePath);
                EditorUtility.SetDirty(curve);
            }
            return curve;
        }

        public static SkillDefinitionDatabase CreateOrUpdateDatabase(List<SkillDefinition> skills, List<PerkDefinition> perks, LevelCurve curve)
        {
            var db = AssetDatabase.LoadAssetAtPath<SkillDefinitionDatabase>(DatabasePath);
            if (db == null)
            {
                db = ScriptableObject.CreateInstance<SkillDefinitionDatabase>();
                AssetDatabase.CreateAsset(db, DatabasePath);
            }

            var so = new SerializedObject(db);
            so.Update();

            var skillsProp = so.FindProperty("skillDefinitions");
            skillsProp.ClearArray();
            for (int i = 0; i < skills.Count; i++)
            {
                skillsProp.InsertArrayElementAtIndex(i);
                skillsProp.GetArrayElementAtIndex(i).objectReferenceValue = skills[i];
            }

            var perksProp = so.FindProperty("perkDefinitions");
            perksProp.ClearArray();
            for (int i = 0; i < perks.Count; i++)
            {
                perksProp.InsertArrayElementAtIndex(i);
                perksProp.GetArrayElementAtIndex(i).objectReferenceValue = perks[i];
            }

            var curveProp = so.FindProperty("levelCurve");
            if (curveProp != null)
            {
                curveProp.objectReferenceValue = curve;
            }

            so.ApplyModifiedProperties();
            db.Initialize();
            EditorUtility.SetDirty(db);

            return db;
        }

        private static PerkRequirement[] ParseRequirements(string body)
        {
            var reqList = new List<PerkRequirement>();
            var pattern = @"<req\s+([^>]+)(?:/>|>(.*?)</req>)";
            var matches = Regex.Matches(body, pattern, RegexOptions.Singleline);

            foreach (Match m in matches)
            {
                string attrStr = m.Groups[1].Value;
                string id = GetAttribute(attrStr, "id");
                if (string.IsNullOrEmpty(id)) continue;

                string lvlStr = GetAttribute(attrStr, "lvl");
                int.TryParse(lvlStr, out int reqLvl);
                if (reqLvl <= 0) reqLvl = 1;

                string dlvlStr = GetAttribute(attrStr, "dlvl");
                int.TryParse(dlvlStr, out int dlvl);

                var req = new PerkRequirement();
                req.level = reqLvl;
                req.levelDelta = dlvl;

                if (id == "level")
                {
                    req.type = RequirementType.Level;
                }
                else if (id == "guns")
                {
                    req.type = RequirementType.Guns;
                }
                else
                {
                    req.type = RequirementType.Skill;
                    req.skillId = id;
                }

                reqList.Add(req);
            }

            return reqList.ToArray();
        }

        private static StatModifier[] ParseModifiers(string body)
        {
            var modList = new List<StatModifier>();
            var pattern = @"<sk\s+([^>]+)(?:/>|>(.*?)</sk>)";
            var matches = Regex.Matches(body, pattern, RegexOptions.Singleline);

            foreach (Match m in matches)
            {
                string attrStr = m.Groups[1].Value;
                string id = GetAttribute(attrStr, "id");
                if (string.IsNullOrEmpty(id)) continue;

                var mod = new StatModifier();
                mod.statId = id;
                mod.tip = GetAttribute(attrStr, "tip");
                mod.refType = GetAttribute(attrStr, "ref");
                mod.dop = GetAttribute(attrStr, "dop") == "1";

                string v0Str = GetAttribute(attrStr, "v0");
                if (!string.IsNullOrEmpty(v0Str) && float.TryParse(v0Str, NumberStyles.Float, CultureInfo.InvariantCulture, out float v0))
                {
                    mod.v0 = v0;
                    mod.hasV0 = true;
                }

                string vdStr = GetAttribute(attrStr, "vd");
                if (!string.IsNullOrEmpty(vdStr) && float.TryParse(vdStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float vd))
                {
                    mod.vd = vd;
                    mod.hasVd = true;
                    mod.valueDelta = vd;
                }

                var vList = new List<float> { mod.hasV0 ? mod.v0 : 0f };
                for (int i = 1; i <= 10; i++)
                {
                    string vNStr = GetAttribute(attrStr, $"v{i}");
                    if (!string.IsNullOrEmpty(vNStr) && float.TryParse(vNStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float vN))
                    {
                        while (vList.Count <= i) vList.Add(float.NaN);
                        vList[i] = vN;
                    }
                }

                if (vList.Count > 1)
                {
                    mod.v = vList.ToArray();
                    mod.values = mod.v;
                }

                if (mod.refType == "add")
                    mod.type = ModifierType.Add;
                else if (mod.refType == "mult")
                    mod.type = ModifierType.Multiply;
                else if (mod.tip == "weap")
                    mod.type = ModifierType.WeaponSkill;
                else
                    mod.type = ModifierType.Set;

                modList.Add(mod);
            }

            return modList.ToArray();
        }

        private static string GetAttribute(string attributeString, string name)
        {
            var match = Regex.Match(attributeString, $"{name}='([^']*)'");
            if (match.Success) return match.Groups[1].Value;

            match = Regex.Match(attributeString, $"{name}=\"([^\"]*)\"");
            if (match.Success) return match.Groups[1].Value;

            return null;
        }
    }

    [InitializeOnLoad]
    public static class SkillAndPerkDataImporterAutoRunner
    {
        static SkillAndPerkDataImporterAutoRunner()
        {
            EditorApplication.delayCall += CheckAndAutoImport;
        }

        private static void CheckAndAutoImport()
        {
            if (!Directory.Exists(SkillAndPerkDataImporter.SkillsDirectory) || 
                Directory.GetFiles(SkillAndPerkDataImporter.SkillsDirectory, "*.asset").Length < 16)
            {
                SkillAndPerkDataImporter.ImportAll(interactive: false);
            }
        }
    }
}
