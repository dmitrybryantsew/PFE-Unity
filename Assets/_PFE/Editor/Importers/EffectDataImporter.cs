using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PFE.Data.Definitions;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports EffectDefinition assets from AllData.as ActionScript file.
    /// Parses <eff> tags and creates ScriptableObject instances.
    /// </summary>
    public class EffectDataImporter
    {
        private static readonly string AllDataPath = SourceImportPaths.AllDataAsPath;
        private static readonly string OutputPath = "Assets/_PFE/Data/Resources/Effects";

        [MenuItem("PFE/Data/Import Effects from AllData.as")]
        public static void ImportEffects()
        {
            int imported = 0;
            int skipped = 0;

            if (!File.Exists(AllDataPath))
            {
                Debug.LogError($"AllData.as not found at: {AllDataPath}");
                return;
            }

            // Ensure output directory exists
            if (!Directory.Exists(OutputPath))
            {
                Directory.CreateDirectory(OutputPath);
            }

            string content = File.ReadAllText(AllDataPath);

            // Find all effect definitions using regex
            // Pattern: <eff id='...' [attributes]>
            var pattern = @"<eff\s+id='([^']+)'([^>]*)>";
            var matches = Regex.Matches(content, pattern);

            Debug.Log($"Found {matches.Count} effect definitions in AllData.as");

            foreach (Match match in matches)
            {
                string id = match.Groups[1].Value;
                string attributesStr = match.Groups[2].Value;

                // Skip if already exists
                string assetPath = $"{OutputPath}/{id}.asset";
                if (File.Exists(assetPath))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    // Create new effect definition
                    EffectDefinition effect = ScriptableObject.CreateInstance<EffectDefinition>();

                    // Parse attributes
                    var effectType = EffectType.Neutral;
                    int duration = 0;
                    bool isChem = false;
                    bool isBadComedown = false;
                    int value = 0;

                    // Parse tip attribute (effect type)
                    var tipMatch = Regex.Match(attributesStr, @"tip='(\d)'");
                    if (tipMatch.Success)
                    {
                        int tip = int.Parse(tipMatch.Groups[1].Value);
                        effectType = (EffectType)tip;
                    }

                    // Parse t attribute (duration)
                    var tMatch = Regex.Match(attributesStr, @"t='(\d+)'");
                    if (tMatch.Success)
                    {
                        duration = int.Parse(tMatch.Groups[1].Value);
                    }

                    // Parse isChem flag
                    if (Regex.IsMatch(attributesStr, @"chem='1'"))
                    {
                        isChem = true;
                    }

                    // Parse val attribute
                    var valMatch = Regex.Match(attributesStr, @"val='([^']*)'");
                    if (valMatch.Success)
                    {
                        string valStr = valMatch.Groups[1].Value;
                        if (float.TryParse(valStr, out float valFloat))
                        {
                            value = (int)valFloat;
                        }
                    }

                    // Set basic fields using reflection
                    SetPrivateField(effect, "effectId", id);
                    SetPrivateField(effect, "type", effectType);
                    SetPrivateField(effect, "durationTicks", duration);
                    SetPrivateField(effect, "isChem", isChem);
                    SetPrivateField(effect, "isBadComedown", isBadComedown);
                    SetPrivateField(effect, "value", value);
                    SetPrivateField(effect, "displayName", GenerateDisplayName(id));
                    SetPrivateField(effect, "displayValue", GenerateDisplayValue(id, effectType));

                    // Parse nested skill modifiers
                    // Look for <sk id='...' v0='...' v1='...' .../> within effect definition
                    // We need to find the content between this <eff> and the closing </eff> or next <eff>
                    int effectStart = match.Index;
                    int effectEnd = content.IndexOf("</eff>", effectStart);
                    if (effectEnd == -1)
                    {
                        // No closing tag, find next <eff> or end of file
                        int nextEff = content.IndexOf("<eff", effectStart + 1);
                        effectEnd = nextEff != -1 ? nextEff : content.Length;
                    }

                    string effectContent = content.Substring(effectStart, effectEnd - effectStart);

                    // Parse skill modifiers
                    var skillModifiers = new List<SkillModifier>();
                    var skillPattern = @"<sk\s+id='([^']+)'([^>]*)/>";
                    var skillMatches = Regex.Matches(effectContent, skillPattern);

                    foreach (Match skillMatch in skillMatches)
                    {
                        string skillId = skillMatch.Groups[1].Value;
                        string skillAttrs = skillMatch.Groups[2].Value;

                        var modifier = new SkillModifier(skillId, 0f);

                        // Parse values
                        ParseSkillValue(skillAttrs, "v0", ref modifier);
                        ParseSkillValue(skillAttrs, "v1", ref modifier);
                        ParseSkillValue(skillAttrs, "v2", ref modifier);
                        ParseSkillValue(skillAttrs, "v3", ref modifier);
                        ParseSkillValue(skillAttrs, "v4", ref modifier);
                        ParseSkillValue(skillAttrs, "v5", ref modifier);

                        // Parse vd (delta)
                        var vdMatch = Regex.Match(skillAttrs, @"vd='(-?\d+\.?\d*)'");
                        if (vdMatch.Success)
                        {
                            // Store in value field for now
                        }

                        skillModifiers.Add(modifier);
                    }

                    SetPrivateField(effect, "effects", skillModifiers.ToArray());

                    // Create asset
                    AssetDatabase.CreateAsset(effect, assetPath);
                    imported++;

                    Debug.Log($"Imported effect: {id}");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Failed to import effect {id}: {e.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Effect import complete. Imported: {imported}, Skipped: {skipped}");
        }

        private static void SetPrivateField(object obj, string fieldName, object value)
        {
            var field = obj.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public);

            if (field != null)
            {
                field.SetValue(obj, value);
            }
        }

        private static void ParseSkillValue(string attrs, string attrName, ref SkillModifier modifier)
        {
            var match = Regex.Match(attrs, $@"{attrName}='(-?\d+\.?\d*)'");
            if (match.Success)
            {
                float value = float.Parse(match.Groups[1].Value);
                modifier = new SkillModifier(modifier.skillId, value);
            }
        }

        private static string GenerateDisplayName(string id)
        {
            // Convert ID to display name
            // e.g., "burning" -> "Burning", "post_mint" -> "Post Mint"
            return id.Replace("_", " ").ToUpper();
        }

        private static string GenerateDisplayValue(string id, EffectType type)
        {
            // Generate display value based on effect type
            switch (type)
            {
                case EffectType.Bad:
                    return "Harmful";
                case EffectType.Good:
                    return "Beneficial";
                case EffectType.Special:
                    return "Special";
                default:
                    return "";
            }
        }
    }
}
