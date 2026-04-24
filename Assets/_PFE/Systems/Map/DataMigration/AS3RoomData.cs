using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.DataMigration
{
    /// <summary>
    /// Represents a room parsed from AS3 XML format.
    /// Matches the structure from Phoenix Force Engine room data.
    /// </summary>
    [Serializable]
    public class AS3RoomData
    {
        /// <summary>
        /// Room name/ID (e.g., "room_3_0")
        /// </summary>
        public string name;

        /// <summary>
        /// Grid X position in land
        /// </summary>
        public int x;

        /// <summary>
        /// Grid Y position in land
        /// </summary>
        public int y;

        /// <summary>
        /// Tile layers - each string is a row of 48 characters
        /// 27 rows total (ROOM_HEIGHT)
        /// Each character represents one tile
        /// </summary>
        public List<string> tileLayers = new List<string>();

        /// <summary>
        /// Objects placed in this room
        /// </summary>
        public List<AS3Object> objects = new List<AS3Object>();

        /// <summary>
        /// Background decorations
        /// </summary>
        public List<AS3Background> backgrounds = new List<AS3Background>();

        /// <summary>
        /// Room-level options parsed from the &lt;options&gt; tag.
        /// Example keys: tip, level, back, wrad, wtip, color, music.
        /// </summary>
        public Dictionary<string, string> options = new Dictionary<string, string>();

        /// <summary>
        /// Source AS3 room collection key (for example "rooms_rbl" or "rooms_stable").
        /// Used to resolve inherited land defaults from GameData.as.
        /// </summary>
        public string sourceCollectionId = "";

        /// <summary>
        /// Get the tile character at the specified grid position
        /// </summary>
        public char GetTile(int x, int y)
        {
            if (y < 0 || y >= tileLayers.Count)
                return '_'; // Default to empty/air

            string row = tileLayers[y];
            if (x < 0 || x >= row.Length)
                return '_';

            return row[x];
        }

        /// <summary>
        /// Get the full tile code string at the specified grid position.
        /// Handles dot-separated multi-character tile format (e.g., "CА", "_Б-", "_E").
        /// </summary>
        public string GetTileCode(int x, int y)
        {
            if (y < 0 || y >= tileLayers.Count)
                return "_";

            string row = tileLayers[y];
            if (string.IsNullOrEmpty(row))
                return "_";

            string[] tokens = row.Split('.');
            if (x < 0 || x >= tokens.Length)
                return "_";

            return string.IsNullOrEmpty(tokens[x]) ? "_" : tokens[x];
        }

        /// <summary>
        /// Validate room data integrity
        /// </summary>
        public bool IsValid()
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (tileLayers.Count != WorldConstants.ROOM_HEIGHT)
            {
                Debug.LogWarning($"Room {name} has {tileLayers.Count} tile rows, expected {WorldConstants.ROOM_HEIGHT}");
                return false;
            }

            // Check each row has correct width
            for (int i = 0; i < tileLayers.Count; i++)
            {
                if (tileLayers[i].Length != WorldConstants.ROOM_WIDTH)
                {
                    Debug.LogWarning($"Room {name} row {i} has {tileLayers[i].Length} tiles, expected {WorldConstants.ROOM_WIDTH}");
                    return false;
                }
            }

            return true;
        }

        public override string ToString()
        {
            int width = tileLayers.Count > 0 ? tileLayers[0].Length : 0;
            return $"AS3Room({name}, pos=({x},{y}), collection={sourceCollectionId}, tiles={tileLayers.Count}x{width}, objects={objects.Count})";
        }

        /// <summary>
        /// Get room option value or default.
        /// </summary>
        public string GetOption(string key, string defaultValue = "")
        {
            return options.TryGetValue(key, out string value) ? value : defaultValue;
        }
    }

    /// <summary>
    /// Represents an object placed in a room (from AS3 XML)
    /// </summary>
    [Serializable]
    public class AS3Object
    {
        /// <summary>
        /// Object type ID (e.g., "chest", "door1", "player", "tarakan")
        /// </summary>
        public string id;

        /// <summary>
        /// Unique instance code
        /// </summary>
        public string code;

        /// <summary>
        /// Grid X position
        /// </summary>
        public int x;

        /// <summary>
        /// Grid Y position
        /// </summary>
        public int y;

        /// <summary>
        /// Additional attributes (lock, cont, uid, etc.)
        /// </summary>
        public Dictionary<string, string> attributes = new Dictionary<string, string>();

        /// <summary>
        /// Items contained in this object (for containers)
        /// </summary>
        public List<AS3Item> items = new List<AS3Item>();

        /// <summary>
        /// Scripts attached to this object
        /// </summary>
        public List<AS3Script> scripts = new List<AS3Script>();

        /// <summary>
        /// Get attribute value or default
        /// </summary>
        public string GetAttribute(string key, string defaultValue = "")
        {
            return attributes.TryGetValue(key, out string value) ? value : defaultValue;
        }

        /// <summary>
        /// Get attribute as integer
        /// </summary>
        public int GetIntAttribute(string key, int defaultValue = 0)
        {
            if (attributes.TryGetValue(key, out string value))
            {
                if (int.TryParse(value, out int result))
                    return result;
            }
            return defaultValue;
        }

        public override string ToString()
        {
            return $"AS3Object({id} at ({x},{y}), code={code})";
        }
    }

    /// <summary>
    /// Represents an item inside an object/container
    /// </summary>
    [Serializable]
    public class AS3Item
    {
        /// <summary>
        /// Item type ID
        /// </summary>
        public string id;

        /// <summary>
        /// Item attributes (imp, kol, etc.)
        /// </summary>
        public Dictionary<string, string> attributes = new Dictionary<string, string>();

        public override string ToString()
        {
            return $"AS3Item({id})";
        }
    }

    /// <summary>
    /// Represents a script/trigger attached to an object
    /// </summary>
    [Serializable]
    public class AS3Script
    {
        /// <summary>
        /// Script actions
        /// </summary>
        public List<AS3ScriptAction> actions = new List<AS3ScriptAction>();

        /// <summary>
        /// Event trigger (eve attribute)
        /// </summary>
        public string eventName;

        public override string ToString()
        {
            return $"AS3Script(eve={eventName}, actions={actions.Count})";
        }
    }

    /// <summary>
    /// Single script action
    /// </summary>
    [Serializable]
    public class AS3ScriptAction
    {
        /// <summary>
        /// Action type (act attribute: "off", "sign", "mess", etc.)
        /// </summary>
        public string act;

        /// <summary>
        /// Target reference (targ attribute)
        /// </summary>
        public string targ;

        /// <summary>
        /// Value (val attribute)
        /// </summary>
        public string val;

        public override string ToString()
        {
            return $"Action({act} -> {targ} = {val})";
        }
    }

    /// <summary>
    /// Background decoration element
    /// </summary>
    [Serializable]
    public class AS3Background
    {
        /// <summary>
        /// Background graphic ID
        /// </summary>
        public string id;

        /// <summary>
        /// Grid X position
        /// </summary>
        public int x;

        /// <summary>
        /// Grid Y position
        /// </summary>
        public int y;

        public override string ToString()
        {
            return $"AS3Background({id} at ({x},{y}))";
        }
    }

    /// <summary>
    /// Collection of AS3 room data for batch processing
    /// </summary>
    [Serializable]
    public class AS3RoomCollection
    {
        /// <summary>
        /// All parsed rooms
        /// </summary>
        public List<AS3RoomData> rooms = new List<AS3RoomData>();

        /// <summary>
        /// Source file path (for logging)
        /// </summary>
        public string sourceFile;

        /// <summary>
        /// Parsed collection keys found in the source file.
        /// </summary>
        public List<string> collectionIds = new List<string>();

        /// <summary>
        /// Total rooms count
        /// </summary>
        public int Count => rooms.Count;

        /// <summary>
        /// Find room by name
        /// </summary>
        public AS3RoomData FindRoom(string name)
        {
            return rooms.Find(r => r.name == name);
        }

        /// <summary>
        /// Get rooms at grid position
        /// </summary>
        public List<AS3RoomData> GetRoomsAt(int x, int y)
        {
            return rooms.FindAll(r => r.x == x && r.y == y);
        }

        /// <summary>
        /// Validate all rooms
        /// </summary>
        public bool ValidateAll()
        {
            bool allValid = true;
            foreach (var room in rooms)
            {
                if (!room.IsValid())
                {
                    Debug.LogError($"Invalid room data: {room}");
                    allValid = false;
                }
            }
            return allValid;
        }

        public override string ToString()
        {
            return $"AS3RoomCollection({Count} rooms from {sourceFile})";
        }
    }
}
