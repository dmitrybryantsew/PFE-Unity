using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PFE.Systems.Map
{
    [Serializable]
    public class MapObjectAttributeData
    {
        public string key;
        public string value;
    }

    [Serializable]
    public class MapObjectItemData
    {
        public string id;
        public List<MapObjectAttributeData> attributes = new List<MapObjectAttributeData>();

        public string GetAttribute(string key, string defaultValue = "")
        {
            return MapObjectDataUtility.GetAttribute(attributes, key, defaultValue);
        }
    }

    [Serializable]
    public class MapObjectScriptActionData
    {
        public string act;
        public string targ;
        public string val;
    }

    [Serializable]
    public class MapObjectScriptData
    {
        public string eventName;
        public List<MapObjectScriptActionData> actions = new List<MapObjectScriptActionData>();
    }

    [Serializable]
    public class MapObjectDynamicStateData
    {
        public bool isDynamic;
        public bool isGrounded;
        public bool isHeldByTelekinesis;
        public bool isThrown;
        public bool hasTelekineticTarget;
        public Vector2 velocity;
        public Vector2 telekineticTarget;
        public float throwGraceTime;
        public float lastImpactSpeed;
    }

    [Serializable]
    public class MapObjectRuntimeStateData
    {
        public bool isDestroyed;
        public bool isOpen;
        public bool isExploded;
        public int lootState;
        public bool hasLockValue;
        public float lockValue;
        public bool hasLockLevel;
        public int lockLevel;
        public bool hasMineValue;
        public int mineValue;
        public bool hasDifficultyState;
        public int difficultyState;
        public bool hasSignState;
        public int signState;
        public MapObjectDynamicStateData dynamicState = new MapObjectDynamicStateData();

        public void InitializeFromAttributes(List<MapObjectAttributeData> attributes)
        {
            if (MapObjectDataUtility.TryGetFloatAttribute(attributes, "lock", out float parsedLock))
            {
                hasLockValue = true;
                lockValue = parsedLock;
            }

            if (MapObjectDataUtility.TryGetIntAttribute(attributes, "locklevel", out int parsedLockLevel))
            {
                hasLockLevel = true;
                lockLevel = parsedLockLevel;
            }

            if (MapObjectDataUtility.TryGetIntAttribute(attributes, "mine", out int parsedMine))
            {
                hasMineValue = true;
                mineValue = parsedMine;
            }

            dynamicState ??= new MapObjectDynamicStateData();
        }
    }

    public static class MapObjectDataUtility
    {
        private static readonly Regex LegacyParameterRegex =
            new Regex("(\\w+)\\s*=\\s*(['\"])(.*?)\\2", RegexOptions.Compiled);

        public static List<MapObjectAttributeData> CloneAttributes(List<MapObjectAttributeData> source)
        {
            List<MapObjectAttributeData> result = new List<MapObjectAttributeData>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Count; i++)
            {
                MapObjectAttributeData attribute = source[i];
                if (attribute == null)
                {
                    continue;
                }

                result.Add(new MapObjectAttributeData
                {
                    key = attribute.key,
                    value = attribute.value
                });
            }

            return result;
        }

        public static List<MapObjectItemData> CloneItems(List<MapObjectItemData> source)
        {
            List<MapObjectItemData> result = new List<MapObjectItemData>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Count; i++)
            {
                MapObjectItemData item = source[i];
                if (item == null)
                {
                    continue;
                }

                result.Add(new MapObjectItemData
                {
                    id = item.id,
                    attributes = CloneAttributes(item.attributes)
                });
            }

            return result;
        }

        public static List<MapObjectScriptData> CloneScripts(List<MapObjectScriptData> source)
        {
            List<MapObjectScriptData> result = new List<MapObjectScriptData>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Count; i++)
            {
                MapObjectScriptData script = source[i];
                if (script == null)
                {
                    continue;
                }

                MapObjectScriptData clonedScript = new MapObjectScriptData
                {
                    eventName = script.eventName
                };

                if (script.actions != null)
                {
                    for (int actionIndex = 0; actionIndex < script.actions.Count; actionIndex++)
                    {
                        MapObjectScriptActionData action = script.actions[actionIndex];
                        if (action == null)
                        {
                            continue;
                        }

                        clonedScript.actions.Add(new MapObjectScriptActionData
                        {
                            act = action.act,
                            targ = action.targ,
                            val = action.val
                        });
                    }
                }

                result.Add(clonedScript);
            }

            return result;
        }

        public static List<MapObjectAttributeData> BuildAttributes(IReadOnlyDictionary<string, string> source, params string[] excludedKeys)
        {
            List<MapObjectAttributeData> result = new List<MapObjectAttributeData>();
            if (source == null)
            {
                return result;
            }

            HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excludedKeys != null)
            {
                for (int i = 0; i < excludedKeys.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(excludedKeys[i]))
                    {
                        excluded.Add(excludedKeys[i]);
                    }
                }
            }

            foreach (KeyValuePair<string, string> kvp in source)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || excluded.Contains(kvp.Key))
                {
                    continue;
                }

                result.Add(new MapObjectAttributeData
                {
                    key = kvp.Key,
                    value = kvp.Value ?? string.Empty
                });
            }

            return result;
        }

        public static string BuildLegacyParameters(string code, string uid, List<MapObjectAttributeData> attributes)
        {
            StringBuilder builder = new StringBuilder();
            AppendLegacyParameter(builder, "code", code);
            AppendLegacyParameter(builder, "uid", uid);

            if (attributes != null)
            {
                for (int i = 0; i < attributes.Count; i++)
                {
                    MapObjectAttributeData attribute = attributes[i];
                    if (attribute == null || string.IsNullOrWhiteSpace(attribute.key))
                    {
                        continue;
                    }

                    AppendLegacyParameter(builder, attribute.key, attribute.value);
                }
            }

            return builder.ToString().TrimEnd();
        }

        public static List<MapObjectAttributeData> ParseLegacyParameters(string parameters, out string code, out string uid)
        {
            List<MapObjectAttributeData> result = new List<MapObjectAttributeData>();
            code = string.Empty;
            uid = string.Empty;

            if (string.IsNullOrWhiteSpace(parameters))
            {
                return result;
            }

            MatchCollection matches = LegacyParameterRegex.Matches(parameters);
            for (int i = 0; i < matches.Count; i++)
            {
                string key = matches[i].Groups[1].Value;
                string value = matches[i].Groups[3].Value;

                if (key.Equals("code", StringComparison.OrdinalIgnoreCase))
                {
                    code = value;
                    continue;
                }

                if (key.Equals("uid", StringComparison.OrdinalIgnoreCase))
                {
                    uid = value;
                    continue;
                }

                result.Add(new MapObjectAttributeData
                {
                    key = key,
                    value = value
                });
            }

            return result;
        }

        public static string GetAttribute(List<MapObjectAttributeData> attributes, string key, string defaultValue = "")
        {
            if (attributes == null || string.IsNullOrWhiteSpace(key))
            {
                return defaultValue;
            }

            for (int i = 0; i < attributes.Count; i++)
            {
                MapObjectAttributeData attribute = attributes[i];
                if (attribute == null || string.IsNullOrWhiteSpace(attribute.key))
                {
                    continue;
                }

                if (string.Equals(attribute.key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return attribute.value ?? defaultValue;
                }
            }

            return defaultValue;
        }

        public static bool TryGetIntAttribute(List<MapObjectAttributeData> attributes, string key, out int value)
        {
            string rawValue = GetAttribute(attributes, key, string.Empty);
            return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryGetFloatAttribute(List<MapObjectAttributeData> attributes, string key, out float value)
        {
            string rawValue = GetAttribute(attributes, key, string.Empty);
            return float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static void AppendLegacyParameter(StringBuilder builder, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(value))
            {
                return;
            }

            builder.Append(key);
            builder.Append("=\"");
            builder.Append(value.Replace("\"", "\\\""));
            builder.Append("\" ");
        }
    }
}
