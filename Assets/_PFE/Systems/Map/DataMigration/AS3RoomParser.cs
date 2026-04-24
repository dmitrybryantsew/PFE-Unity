using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEngine;

namespace PFE.Systems.Map.DataMigration
{
    /// <summary>
    /// Parses AS3 room data into AS3RoomData structures.
    /// 
    /// Handles TWO input formats:
    ///   1. Pure XML files (rooms_begin.xml) — parsed directly
    ///   2. AS3 class files (Rooms.as, RoomsPlant.as) — XML extracted from embedded literals
    /// 
    /// The AS3 files contain XML embedded in ActionScript like:
    ///   internal var rooms_begin:XML = <all>
    ///     <room name="room_3_0" x="3" y="0">
    ///       <a>C.C.C._E._E.C</a>
    ///     </room>
    ///   </all>;
    /// 
    /// Tile rows (<a> tags) contain dot-separated multi-character codes.
    /// These are preserved verbatim in AS3RoomData.tileLayers.
    /// </summary>
    public class AS3RoomParser
    {
        private sealed class ExtractedXmlBlock
        {
            public string CollectionId;
            public string Xml;
        }

        /// <summary>
        /// Parse a single file (XML or AS3).
        /// </summary>
        public AS3RoomCollection ParseFile(string filePath)
        {
            AS3RoomCollection collection = new AS3RoomCollection
            {
                sourceFile = filePath
            };

            if (!File.Exists(filePath))
            {
                Debug.LogError($"[AS3RoomParser] File not found: {filePath}");
                return collection;
            }

            try
            {
                string fileContent = File.ReadAllText(filePath);

                // Extract XML from the file content (handles both pure XML and AS3 files)
                List<ExtractedXmlBlock> xmlBlocks = ExtractXmlBlocks(fileContent, filePath);

                if (xmlBlocks.Count == 0)
                {
                    Debug.LogWarning($"[AS3RoomParser] No XML blocks found in {filePath}");
                    return collection;
                }

                foreach (ExtractedXmlBlock xmlBlock in xmlBlocks)
                {
                    if (!string.IsNullOrWhiteSpace(xmlBlock.CollectionId) &&
                        !collection.collectionIds.Contains(xmlBlock.CollectionId))
                    {
                        collection.collectionIds.Add(xmlBlock.CollectionId);
                    }

                    ParseXmlBlock(xmlBlock.Xml, collection, xmlBlock.CollectionId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AS3RoomParser] Error parsing {filePath}: {ex.Message}\n{ex.StackTrace}");
            }

            Debug.Log($"[AS3RoomParser] Parsed {collection.rooms.Count} rooms from {Path.GetFileName(filePath)}");
            return collection;
        }

        /// <summary>
        /// Parse multiple files.
        /// </summary>
        public List<AS3RoomCollection> ParseMultipleFiles(string[] filePaths)
        {
            List<AS3RoomCollection> collections = new List<AS3RoomCollection>();
            foreach (string path in filePaths)
            {
                AS3RoomCollection c = ParseFile(path);
                if (c.rooms.Count > 0)
                    collections.Add(c);
            }
            return collections;
        }

        /// <summary>
        /// Parse an XML string directly (for testing).
        /// </summary>
        public AS3RoomCollection ParseXmlString(string xmlContent)
        {
            AS3RoomCollection collection = new AS3RoomCollection { sourceFile = "String" };
            ParseXmlBlock(xmlContent, collection, string.Empty);
            return collection;
        }

        // ==========================================================
        //  XML EXTRACTION — handles both pure XML and AS3 files
        // ==========================================================

        /// <summary>
        /// Extract XML blocks from file content.
        /// 
        /// Strategy:
        /// 1. Try to find <all>...</all> blocks (AS3 embedded XML)
        /// 2. If found, extract each one
        /// 3. If not found, try the whole content as XML
        /// 4. If that fails, fall back to regex room extraction
        /// </summary>
        private List<ExtractedXmlBlock> ExtractXmlBlocks(string content, string filePath)
        {
            List<ExtractedXmlBlock> blocks = new List<ExtractedXmlBlock>();

            MatchCollection namedBlocks = Regex.Matches(
                content,
                @"(?:internal|public)\s+var\s+(?<name>[A-Za-z0-9_]+)\s*:\s*XML\s*=\s*(?<xml><all>[\s\S]*?<\/all>)\s*;",
                RegexOptions.IgnoreCase);

            foreach (Match match in namedBlocks)
            {
                string xml = match.Groups["xml"].Value;
                if (string.IsNullOrWhiteSpace(xml))
                {
                    continue;
                }

                blocks.Add(new ExtractedXmlBlock
                {
                    CollectionId = match.Groups["name"].Value.Trim(),
                    Xml = xml
                });
            }

            if (blocks.Count > 0)
            {
                Debug.Log($"[AS3RoomParser] Found {blocks.Count} named <all> XML block(s)");
                return blocks;
            }

            // Strategy 1: Find all <all>...</all> blocks
            // (AS3 files can have multiple: rooms_begin, rooms_plant, etc.)
            int searchStart = 0;
            while (searchStart < content.Length)
            {
                int allStart = content.IndexOf("<all>", searchStart, StringComparison.Ordinal);
                if (allStart < 0) break;

                int allEnd = content.IndexOf("</all>", allStart, StringComparison.Ordinal);
                if (allEnd < 0) break;

                allEnd += "</all>".Length;
                string block = content.Substring(allStart, allEnd - allStart);
                blocks.Add(new ExtractedXmlBlock
                {
                    CollectionId = InferCollectionIdFromFilePath(filePath),
                    Xml = block
                });

                searchStart = allEnd;
            }

            if (blocks.Count > 0)
            {
                Debug.Log($"[AS3RoomParser] Found {blocks.Count} <all> XML block(s)");
                return blocks;
            }

            // Strategy 2: No <all> tags — maybe it's a pure XML file with <room> tags
            // Try wrapping in a root element
            if (content.Contains("<room"))
            {
                blocks.Add(new ExtractedXmlBlock
                {
                    CollectionId = InferCollectionIdFromFilePath(filePath),
                    Xml = $"<all>{content}</all>"
                });
                return blocks;
            }

            // Strategy 3: Try as-is (might be well-formed XML)
            blocks.Add(new ExtractedXmlBlock
            {
                CollectionId = InferCollectionIdFromFilePath(filePath),
                Xml = content
            });
            return blocks;
        }

        /// <summary>
        /// Parse a single XML block containing <room> elements.
        /// </summary>
        private void ParseXmlBlock(string xmlContent, AS3RoomCollection collection, string collectionId)
        {
            // Normalize: remove XML declaration if present
            string normalized = Regex.Replace(xmlContent.Trim(),
                @"^\s*<\?xml[^>]*\?>", string.Empty, RegexOptions.IgnoreCase);

            // Wrap in root if not already wrapped
            if (!normalized.StartsWith("<all>", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("<root>", StringComparison.OrdinalIgnoreCase))
            {
                normalized = $"<root>{normalized}</root>";
            }

            // Try DOM parse first
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(normalized);

                XmlNodeList roomNodes = doc.SelectNodes("//room");
                foreach (XmlNode roomNode in roomNodes)
                {
                    AS3RoomData room = ParseRoom(roomNode, collectionId);
                    if (room != null)
                        collection.rooms.Add(room);
                }
                return; // Success
            }
            catch (XmlException ex)
            {
                Debug.LogWarning($"[AS3RoomParser] DOM parse failed: {ex.Message}. Using regex fallback.");
            }

            // Fallback: extract <room>...</room> blocks via regex
            ParseRoomsFromBlocks(normalized, collection, collectionId);
        }

        // ==========================================================
        //  ROOM PARSING
        // ==========================================================

        private AS3RoomData ParseRoom(XmlNode roomNode, string collectionId)
        {
            AS3RoomData room = new AS3RoomData
            {
                sourceCollectionId = collectionId ?? string.Empty
            };

            room.name = GetAttr(roomNode, "name", "");
            room.x = GetIntAttr(roomNode, "x", 0);
            room.y = GetIntAttr(roomNode, "y", 0);

            // Parse tile rows — store raw dot-separated strings
            XmlNodeList aNodes = roomNode.SelectNodes("./a");
            foreach (XmlNode aNode in aNodes)
            {
                string row = aNode.InnerText?.Trim() ?? "";
                room.tileLayers.Add(row);
            }

            // Parse options
            XmlNode optionsNode = roomNode.SelectSingleNode("./options");
            if (optionsNode?.Attributes != null)
            {
                foreach (XmlAttribute attr in optionsNode.Attributes)
                    room.options[attr.Name] = attr.Value;
            }

            // Parse doors (from <doors> tag if present)
            XmlNode doorsNode = roomNode.SelectSingleNode("./doors");
            if (doorsNode != null)
            {
                room.options["_doors_raw"] = doorsNode.InnerText?.Trim() ?? "";
            }

            // Parse objects
            XmlNodeList objNodes = roomNode.SelectNodes("./obj");
            foreach (XmlNode objNode in objNodes)
            {
                AS3Object obj = ParseObject(objNode);
                if (obj != null)
                    room.objects.Add(obj);
            }

            // Parse backgrounds
            XmlNodeList backNodes = roomNode.SelectNodes("./back");
            foreach (XmlNode backNode in backNodes)
            {
                room.backgrounds.Add(new AS3Background
                {
                    id = GetAttr(backNode, "id", ""),
                    x = GetIntAttr(backNode, "x", 0),
                    y = GetIntAttr(backNode, "y", 0)
                });
            }

            return room;
        }

        private AS3Object ParseObject(XmlNode objNode)
        {
            AS3Object obj = new AS3Object
            {
                id = GetAttr(objNode, "id", ""),
                code = GetAttr(objNode, "code", ""),
                x = GetIntAttr(objNode, "x", 0),
                y = GetIntAttr(objNode, "y", 0)
            };

            // Store all attributes
            foreach (XmlAttribute attr in objNode.Attributes)
            {
                if (!obj.attributes.ContainsKey(attr.Name))
                    obj.attributes[attr.Name] = attr.Value;
            }

            // Parse items
            XmlNodeList itemNodes = objNode.SelectNodes("./item");
            foreach (XmlNode itemNode in itemNodes)
            {
                AS3Item item = new AS3Item { id = GetAttr(itemNode, "id", "") };
                foreach (XmlAttribute attr in itemNode.Attributes)
                    item.attributes[attr.Name] = attr.Value;
                obj.items.Add(item);
            }

            // Parse scripts
            XmlNodeList scrNodes = objNode.SelectNodes("./scr");
            foreach (XmlNode scrNode in scrNodes)
            {
                AS3Script script = new AS3Script
                {
                    eventName = GetAttr(scrNode, "eve", "")
                };

                // Nested <s> elements or direct attributes
                XmlNodeList sNodes = scrNode.SelectNodes("./s");
                if (sNodes.Count > 0)
                {
                    foreach (XmlNode sNode in sNodes)
                    {
                        script.actions.Add(new AS3ScriptAction
                        {
                            act = GetAttr(sNode, "act", ""),
                            targ = GetAttr(sNode, "targ", ""),
                            val = GetAttr(sNode, "val", "")
                        });
                    }
                }
                else
                {
                    string act = GetAttr(scrNode, "act", "");
                    if (!string.IsNullOrEmpty(act))
                    {
                        script.actions.Add(new AS3ScriptAction
                        {
                            act = act,
                            targ = GetAttr(scrNode, "targ", ""),
                            val = GetAttr(scrNode, "val", "")
                        });
                    }
                }

                obj.scripts.Add(script);
            }

            return obj;
        }

        // ==========================================================
        //  REGEX FALLBACK
        // ==========================================================

        private void ParseRoomsFromBlocks(string content, AS3RoomCollection collection, string collectionId)
        {
            MatchCollection roomBlocks = Regex.Matches(content,
                @"<room\b[\s\S]*?</room>", RegexOptions.IgnoreCase);

            foreach (Match match in roomBlocks)
            {
                try
                {
                    XmlDocument roomDoc = new XmlDocument();
                    roomDoc.LoadXml($"<root>{match.Value}</root>");
                    XmlNode roomNode = roomDoc.SelectSingleNode("//room");
                    if (roomNode != null)
                    {
                        AS3RoomData room = ParseRoom(roomNode, collectionId);
                        if (room != null)
                            collection.rooms.Add(room);
                    }
                }
                catch
                {
                    // Skip malformed room block
                }
            }
        }

        // ==========================================================
        //  HELPERS
        // ==========================================================

        private string GetAttr(XmlNode node, string name, string def)
        {
            return node.Attributes?[name]?.Value ?? def;
        }

        private int GetIntAttr(XmlNode node, string name, int def)
        {
            string val = GetAttr(node, name, "");
            return int.TryParse(val, out int result) ? result : def;
        }

        private string InferCollectionIdFromFilePath(string filePath)
        {
            string baseName = Path.GetFileNameWithoutExtension(filePath)?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(baseName))
            {
                return string.Empty;
            }

            if (baseName.Equals("Rooms", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (baseName.StartsWith("Rooms", StringComparison.OrdinalIgnoreCase) && baseName.Length > "Rooms".Length)
            {
                string suffix = baseName.Substring("Rooms".Length).Trim();
                if (!string.IsNullOrEmpty(suffix))
                {
                    return $"rooms_{suffix.ToLowerInvariant()}";
                }
            }

            return baseName.ToLowerInvariant();
        }
    }
}
