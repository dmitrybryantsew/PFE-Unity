using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PFE.Systems.Map.DataMigration
{
    /// <summary>
    /// Lightweight parsed entry from AllData.as.
    /// Represents any tag: mat, unit, weapon, item, eff, perk, obj, etc.
    /// </summary>
    [Serializable]
    public class ParsedEntry
    {
        public string tagName;
        public string id;
        public Dictionary<string, string> attributes = new Dictionary<string, string>();
        public List<ParsedEntry> children = new List<ParsedEntry>();
        public string innerText = "";

        public string GetAttr(string name, string def = "")
        {
            return attributes.TryGetValue(name, out string val) ? val : def;
        }

        public int GetIntAttr(string name, int def = 0)
        {
            string val = GetAttr(name);
            return int.TryParse(val, out int result) ? result : def;
        }

        public float GetFloatAttr(string name, float def = 0f)
        {
            string val = GetAttr(name);
            return float.TryParse(val, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result) ? result : def;
        }

        public bool HasAttr(string name)
        {
            return attributes.ContainsKey(name);
        }

        /// <summary>Get first child with matching tag name, or null.</summary>
        public ParsedEntry GetChild(string childTagName)
        {
            foreach (var child in children)
            {
                if (child.tagName == childTagName)
                    return child;
            }
            return null;
        }

        /// <summary>Get all children with matching tag name.</summary>
        public List<ParsedEntry> GetChildren(string childTagName)
        {
            var result = new List<ParsedEntry>();
            foreach (var child in children)
            {
                if (child.tagName == childTagName)
                    result.Add(child);
            }
            return result;
        }

        public override string ToString()
        {
            return $"<{tagName} id='{id}' attrs={attributes.Count} children={children.Count}>";
        }
    }

    /// <summary>
    /// Parses AllData.as once and provides queryable access to all tag types.
    /// 
    /// Handles the AS3-embedded XML format:
    /// - Extracts XML from between <all> and </all> tags
    /// - Single-quoted attributes: id='value'
    /// - Self-closing tags: <main tex='foo'/>
    /// - Container tags: <mat ...> children </mat>
    /// - Cyrillic characters in values
    /// - No strict root element
    /// 
    /// Usage:
    ///   var parser = new AllDataParser();
    ///   parser.LoadFile("path/to/AllData.as");
    ///   var matC = parser.GetEntry("mat", "C");
    ///   string tex = matC.GetChild("main")?.GetAttr("tex");
    /// </summary>
    public class AllDataParser
    {
        // All entries indexed by (tagName, id)
        private Dictionary<string, Dictionary<string, ParsedEntry>> _entries
            = new Dictionary<string, Dictionary<string, ParsedEntry>>();

        // All entries in parse order, by tag name
        private Dictionary<string, List<ParsedEntry>> _entriesByTag
            = new Dictionary<string, List<ParsedEntry>>();

        private bool _loaded = false;
        private string _sourcePath = "";
        private int _totalEntries = 0;

        public bool IsLoaded => _loaded;
        public string SourcePath => _sourcePath;
        public int TotalEntries => _totalEntries;

        /// <summary>
        /// Load and parse AllData.as from file.
        /// </summary>
        public void LoadFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Debug.LogError($"[AllDataParser] File not found: {filePath}");
                return;
            }

            _sourcePath = filePath;
            string content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            Parse(content);
        }

        /// <summary>
        /// Parse from string content (for testing).
        /// </summary>
        public void LoadFromString(string content)
        {
            _sourcePath = "(string)";
            Parse(content);
        }

        /// <summary>
        /// Get a specific entry by tag name and id.
        /// Returns null if not found.
        /// </summary>
        public ParsedEntry GetEntry(string tagName, string id)
        {
            if (_entries.TryGetValue(tagName, out var byId))
            {
                if (byId.TryGetValue(id, out var entry))
                    return entry;
            }
            return null;
        }

        /// <summary>
        /// Get all entries of a specific tag type.
        /// </summary>
        public List<ParsedEntry> GetAllEntries(string tagName)
        {
            if (_entriesByTag.TryGetValue(tagName, out var list))
                return list;
            return new List<ParsedEntry>();
        }

        /// <summary>
        /// Get all known tag types.
        /// </summary>
        public List<string> GetTagTypes()
        {
            return new List<string>(_entriesByTag.Keys);
        }

        /// <summary>
        /// Get count of entries for a tag type.
        /// </summary>
        public int GetCount(string tagName)
        {
            if (_entriesByTag.TryGetValue(tagName, out var list))
                return list.Count;
            return 0;
        }

        /// <summary>
        /// Get summary string for logging.
        /// </summary>
        public string GetSummary()
        {
            var parts = new List<string>();
            foreach (var kvp in _entriesByTag)
            {
                parts.Add($"{kvp.Value.Count} {kvp.Key}");
            }
            return $"AllDataParser: {_totalEntries} total entries ({string.Join(", ", parts)})";
        }

        // ============================================================
        //  PARSING
        // ============================================================

        private void Parse(string content)
        {
            _entries.Clear();
            _entriesByTag.Clear();
            _totalEntries = 0;

            // Extract XML from AS3 file — find <all>...</all> block
            string xml = ExtractXml(content);
            if (string.IsNullOrEmpty(xml))
            {
                Debug.LogError("[AllDataParser] Could not find <all>...</all> block in file");
                return;
            }

            // Parse all top-level tags with their children
            ParseTopLevelTags(xml);

            _loaded = true;
            Debug.Log($"[AllDataParser] {GetSummary()}");
        }

        /// <summary>
        /// Extract the XML content from between <all> and </all> tags.
        /// Handles AS3 code surrounding the XML.
        /// </summary>
        private string ExtractXml(string content)
        {
            int start = content.IndexOf("<all>", StringComparison.Ordinal);
            if (start < 0)
            {
                // Maybe it's already pure XML — try finding top-level tags directly
                if (content.Contains("<mat") || content.Contains("<unit") || content.Contains("<weapon"))
                    return content;
                return null;
            }

            int end = content.IndexOf("</all>", start, StringComparison.Ordinal);
            if (end < 0)
            {
                // No closing tag — take everything after <all>
                return content.Substring(start + 5);
            }

            return content.Substring(start + 5, end - start - 5);
        }

        /// <summary>
        /// Parse all top-level container and self-closing tags.
        /// Uses regex to find tag boundaries, then parses attributes and children.
        /// </summary>
        private void ParseTopLevelTags(string xml)
        {
            // Match opening tags: <tagName attributes...> or <tagName attributes.../>
            // This regex captures: tagName, attributes, and whether it's self-closing
            var tagPattern = new Regex(
                @"<(\w+)\s+([^>]*?)(/?)>",
                RegexOptions.Singleline);

            var matches = tagPattern.Matches(xml);

            for (int i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                string tagName = match.Groups[1].Value;
                string attrString = match.Groups[2].Value;
                bool selfClosing = match.Groups[3].Value == "/";

                // Skip closing tags, child tags (we'll parse those within parents),
                // and known non-entry tags
                if (tagName == "all" || tagName == "root" || tagName == "land" || tagName == "serial")
                    continue;

                // Parse attributes
                var attrs = ParseAttributes(attrString);

                // Need an id to index
                string id = attrs.ContainsKey("id") ? attrs["id"] : null;

                // For entries without id, generate one
                if (id == null)
                {
                    // Some tags don't have id (like <land>), skip them
                    continue;
                }

                var entry = new ParsedEntry
                {
                    tagName = tagName,
                    id = id,
                    attributes = attrs
                };

                if (!selfClosing)
                {
                    // Find the closing tag and parse children
                    string closingTag = $"</{tagName}>";
                    int contentStart = match.Index + match.Length;
                    int contentEnd = xml.IndexOf(closingTag, contentStart, StringComparison.Ordinal);

                    if (contentEnd > contentStart)
                    {
                        string innerContent = xml.Substring(contentStart, contentEnd - contentStart);
                        ParseChildren(entry, innerContent);
                    }
                }

                // Register entry
                RegisterEntry(entry);
            }
        }

        /// <summary>
        /// Parse child tags within a container tag's content.
        /// Handles both self-closing (<main tex='foo'/>) and content tags (<n>text</n>).
        /// </summary>
        private void ParseChildren(ParsedEntry parent, string content)
        {
            // Match self-closing children: <tagName attributes/>
            var selfClosingPattern = new Regex(
                @"<(\w+)\s+([^>]*?)\s*/>",
                RegexOptions.Singleline);

            foreach (Match match in selfClosingPattern.Matches(content))
            {
                string childTag = match.Groups[1].Value;
                string childAttrs = match.Groups[2].Value;

                parent.children.Add(new ParsedEntry
                {
                    tagName = childTag,
                    id = "",
                    attributes = ParseAttributes(childAttrs)
                });
            }

            // Match content children: <tagName>text</tagName> (like <n>Display Name</n>)
            var contentPattern = new Regex(
                @"<(\w+)>([^<]*)</\1>",
                RegexOptions.Singleline);

            foreach (Match match in contentPattern.Matches(content))
            {
                string childTag = match.Groups[1].Value;
                string childText = match.Groups[2].Value.Trim();

                parent.children.Add(new ParsedEntry
                {
                    tagName = childTag,
                    id = "",
                    innerText = childText
                });
            }

            // Match content children with attributes: <tagName attrs>text</tagName>
            var contentWithAttrsPattern = new Regex(
                @"<(\w+)\s+([^>]*)>([^<]*)</\1>",
                RegexOptions.Singleline);

            foreach (Match match in contentWithAttrsPattern.Matches(content))
            {
                string childTag = match.Groups[1].Value;
                string childAttrs = match.Groups[2].Value;
                string childText = match.Groups[3].Value.Trim();

                // Avoid duplicates with the content-only pattern above
                bool alreadyAdded = false;
                foreach (var existing in parent.children)
                {
                    if (existing.tagName == childTag && existing.innerText == childText)
                    {
                        // Merge attributes into existing
                        var newAttrs = ParseAttributes(childAttrs);
                        foreach (var kvp in newAttrs)
                            existing.attributes[kvp.Key] = kvp.Value;
                        alreadyAdded = true;
                        break;
                    }
                }

                if (!alreadyAdded)
                {
                    parent.children.Add(new ParsedEntry
                    {
                        tagName = childTag,
                        id = "",
                        attributes = ParseAttributes(childAttrs),
                        innerText = childText
                    });
                }
            }
        }

        /// <summary>
        /// Parse attributes from a tag string.
        /// Handles both single and double quoted values: id='value' or id="value"
        /// </summary>
        private Dictionary<string, string> ParseAttributes(string attrString)
        {
            var attrs = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(attrString))
                return attrs;

            // Match key='value' or key="value"
            var attrPattern = new Regex(@"(\w+)\s*=\s*['""]([^'""]*)['""]");
            foreach (Match match in attrPattern.Matches(attrString))
            {
                string key = match.Groups[1].Value;
                string value = match.Groups[2].Value;
                attrs[key] = value;
            }

            return attrs;
        }

        /// <summary>
        /// Register a parsed entry in the lookup dictionaries.
        /// </summary>
        private void RegisterEntry(ParsedEntry entry)
        {
            // By tag + id
            if (!_entries.ContainsKey(entry.tagName))
                _entries[entry.tagName] = new Dictionary<string, ParsedEntry>();

            // Handle duplicate IDs (some tags share ids across ed types)
            string key = entry.id;
            if (_entries[entry.tagName].ContainsKey(key))
            {
                // For mat entries, ed=1 and ed=2 can share the same letter id
                // Disambiguate with ed value
                string ed = entry.GetAttr("ed", "");
                if (!string.IsNullOrEmpty(ed))
                    key = $"{entry.id}_ed{ed}";
                else
                    key = $"{entry.id}_{_totalEntries}";
            }
            _entries[entry.tagName][key] = entry;

            // By tag (ordered list)
            if (!_entriesByTag.ContainsKey(entry.tagName))
                _entriesByTag[entry.tagName] = new List<ParsedEntry>();
            _entriesByTag[entry.tagName].Add(entry);

            _totalEntries++;
        }
    }
}