using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.DataMigration
{
    /// <summary>
    /// Maps AS3 object IDs to Unity prefab paths and handles object type conversion.
    /// This provides the bridge between AS3 XML object definitions and Unity prefabs.
    /// </summary>
    [CreateAssetMenu(fileName = "AS3ObjectMapping", menuName = "PFE/Map/AS3 Object Mapping")]
    public class AS3ObjectMapping : ScriptableObject
    {
        /// <summary>
        /// Mapping configuration for individual object types
        /// </summary>
        [Serializable]
        public class ObjectMapping
        {
            /// <summary>
            /// AS3 object ID (e.g., "chest", "door1", "player")
            /// </summary>
            public string as3ObjectId;

            /// <summary>
            /// Unity prefab path (relative to Resources folder or Addressables)
            /// </summary>
            public string unityPrefabPath;

            /// <summary>
            /// Object category for categorization
            /// </summary>
            public ObjectCategory category;

            /// <summary>
            /// Should this object be instantiated during room load?
            /// </summary>
            public bool instantiateOnLoad = true;

            /// <summary>
            /// Default scale for this object type
            /// </summary>
            public Vector3 defaultScale = Vector3.one;
        }

        /// <summary>
        /// All object mappings
        /// </summary>
        public List<ObjectMapping> mappings = new List<ObjectMapping>();

        /// <summary>
        /// Fallback prefab for unknown objects
        /// </summary>
        public string unknownObjectFallback = "Prefabs/Objects/UnknownObject";

        /// <summary>
        /// Should warnings be logged for unknown objects?
        /// </summary>
        public bool logUnknownObjects = true;

        private Dictionary<string, ObjectMapping> mappingCache;

        /// <summary>
        /// Initialize the mapping cache
        /// </summary>
        public void InitializeCache()
        {
            mappingCache = new Dictionary<string, ObjectMapping>();
            foreach (var mapping in mappings)
            {
                if (!string.IsNullOrEmpty(mapping.as3ObjectId))
                {
                    mappingCache[mapping.as3ObjectId] = mapping;
                }
            }
        }

        /// <summary>
        /// Get mapping for AS3 object ID
        /// </summary>
        public ObjectMapping GetMapping(string as3ObjectId)
        {
            if (mappingCache == null)
            {
                InitializeCache();
            }

            if (mappingCache.TryGetValue(as3ObjectId, out ObjectMapping mapping))
            {
                return mapping;
            }

            // Log warning for unknown objects
            if (logUnknownObjects)
            {
                Debug.LogWarning($"[AS3ObjectMapping] Unknown object ID: {as3ObjectId}. Using fallback.");
            }

            return CreateFallbackMapping(as3ObjectId);
        }

        /// <summary>
        /// Check if object ID is known
        /// </summary>
        public bool IsKnown(string as3ObjectId)
        {
            if (mappingCache == null)
            {
                InitializeCache();
            }

            return mappingCache.ContainsKey(as3ObjectId);
        }

        /// <summary>
        /// Get all object IDs of a specific category
        /// </summary>
        public List<string> GetObjectsByCategory(ObjectCategory category)
        {
            List<string> result = new List<string>();
            foreach (var mapping in mappings)
            {
                if (mapping.category == category)
                {
                    result.Add(mapping.as3ObjectId);
                }
            }
            return result;
        }

        private ObjectMapping CreateFallbackMapping(string as3ObjectId)
        {
            return new ObjectMapping
            {
                as3ObjectId = as3ObjectId,
                unityPrefabPath = unknownObjectFallback,
                category = ObjectCategory.Unknown,
                instantiateOnLoad = false,
                defaultScale = Vector3.one
            };
        }
    }

    /// <summary>
    /// Object categories for classification
    /// </summary>
    public enum ObjectCategory
    {
        Unknown,
        Player,
        Enemy,
        Container,
        Door,
        Item,
        Decoration,
        Trigger,
        Weapon,
        NPC,
        Furniture
    }

    /// <summary>
    /// Default object mapping data for common PFE objects.
    /// This can be used to populate the initial ScriptableObject.
    /// </summary>
    public static class AS3DefaultObjectMappings
    {
        /// <summary>
        /// Get common object mappings for Phoenix Force
        /// </summary>
        public static List<AS3ObjectMapping.ObjectMapping> GetDefaultMappings()
        {
            return new List<AS3ObjectMapping.ObjectMapping>
            {
                // Player
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "player",
                    unityPrefabPath = "Prefabs/Player/Player",
                    category = ObjectCategory.Player,
                    instantiateOnLoad = true
                },

                // Containers
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "chest",
                    unityPrefabPath = "Prefabs/Objects/Containers/Chest",
                    category = ObjectCategory.Container,
                    instantiateOnLoad = true
                },
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "bigbox",
                    unityPrefabPath = "Prefabs/Objects/Containers/BigBox",
                    category = ObjectCategory.Container,
                    instantiateOnLoad = true
                },
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "box",
                    unityPrefabPath = "Prefabs/Objects/Containers/Box",
                    category = ObjectCategory.Container,
                    instantiateOnLoad = true
                },
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "woodbox",
                    unityPrefabPath = "Prefabs/Objects/Containers/WoodBox",
                    category = ObjectCategory.Container,
                    instantiateOnLoad = true
                },
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "medbox",
                    unityPrefabPath = "Prefabs/Objects/Containers/MedBox",
                    category = ObjectCategory.Container,
                    instantiateOnLoad = true
                },
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "case",
                    unityPrefabPath = "Prefabs/Objects/Containers/Case",
                    category = ObjectCategory.Container,
                    instantiateOnLoad = true
                },
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "wallcab",
                    unityPrefabPath = "Prefabs/Objects/Containers/WallCabinet",
                    category = ObjectCategory.Container,
                    instantiateOnLoad = true
                },

                // Doors
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "door1",
                    unityPrefabPath = "Prefabs/Objects/Doors/Door1",
                    category = ObjectCategory.Door,
                    instantiateOnLoad = true
                },

                // Enemies
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "tarakan",
                    unityPrefabPath = "Prefabs/Enemies/Tarakan",
                    category = ObjectCategory.Enemy,
                    instantiateOnLoad = true
                },

                // Interactive objects
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "instr1",
                    unityPrefabPath = "Prefabs/Objects/Interactive/Instrument1",
                    category = ObjectCategory.Item,
                    instantiateOnLoad = true
                },
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "instr2",
                    unityPrefabPath = "Prefabs/Objects/Interactive/Instrument2",
                    category = ObjectCategory.Item,
                    instantiateOnLoad = true
                },

                // Triggers
                new AS3ObjectMapping.ObjectMapping
                {
                    as3ObjectId = "area",
                    unityPrefabPath = "Prefabs/Objects/Triggers/AreaTrigger",
                    category = ObjectCategory.Trigger,
                    instantiateOnLoad = true
                }
            };
        }
    }
}
