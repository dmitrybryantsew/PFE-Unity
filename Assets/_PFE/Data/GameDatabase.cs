using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.ModAPI;
using PFE.Systems.Map;

namespace PFE.Data
{
    /// <summary>
    /// Game database registry — the public API for looking up game content.
    /// Replaces AllData.d from ActionScript.
    ///
    /// Architecture: "base game as a mod"
    /// - All content (base game + mods) flows through ContentRegistry via IContentSource
    /// - The base game is "pfe.base", loaded first via BuiltInContentSource
    /// - Mods are discovered by ModLoader, loaded in priority order after base
    /// - GameDatabase is the public facade; ContentRegistry is the merge engine
    /// </summary>
    public class GameDatabase
    {
        readonly PFE.Core.PfeDebugSettings _debugSettings;

        /// <summary>The content registry backing this database. All content lives here.</summary>
        public ContentRegistry Registry { get; private set; }

        /// <summary>The mod loader used to discover and order content sources.</summary>
        public ModLoader Loader { get; private set; }

        // Legacy dictionaries for types not yet on IGameContent (commented-out types).
        // Also used by tests that call Register directly.
        readonly Dictionary<string, UnitDefinition> _units = new();
        readonly Dictionary<string, WeaponDefinition> _weapons = new();
        readonly Dictionary<string, RoomTemplate> _roomTemplates = new();
        readonly Dictionary<string, MapObjectDefinition> _mapObjectDefinitions = new();

        public GameDatabase() : this(ScriptableObject.CreateInstance<PFE.Core.PfeDebugSettings>())
        {
        }

        public GameDatabase(PFE.Core.PfeDebugSettings debugSettings)
        {
            _debugSettings = debugSettings != null
                ? debugSettings
                : ScriptableObject.CreateInstance<PFE.Core.PfeDebugSettings>();
            Registry = new ContentRegistry();
            Loader = new ModLoader();
        }

        #region Registration (legacy + IGameContent bridge)

        /// <summary>
        /// Register a unit definition in the database.
        /// Called during initialization or when loading mods.
        /// </summary>
        public void RegisterUnit(UnitDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.ID))
            {
                Debug.LogWarning("[GameDatabase] Attempted to register invalid unit definition");
                return;
            }

            if (_units.ContainsKey(def.ID))
            {
                if (_debugSettings.LogGameDatabaseDuplicateRegistrationWarnings)
                {
                    Debug.LogWarning($"[GameDatabase] Unit ID '{def.ID}' already registered, skipping");
                }
                return;
            }

            _units.Add(def.ID, def);
            if (_debugSettings.LogGameDatabaseAssetRegistration)
            {
                Debug.Log($"[GameDatabase] Registered unit: {def.ID} ({def.DisplayName})");
            }
        }

        /// <summary>
        /// Register a weapon definition in the database.
        /// </summary>
        public void RegisterWeapon(WeaponDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.ID))
            {
                Debug.LogWarning("[GameDatabase] Attempted to register invalid weapon definition");
                return;
            }

            if (_weapons.ContainsKey(def.ID))
            {
                if (_debugSettings.LogGameDatabaseDuplicateRegistrationWarnings)
                {
                    Debug.LogWarning($"[GameDatabase] Weapon ID '{def.ID}' already registered, skipping");
                }
                return;
            }

            _weapons.Add(def.ID, def);
            if (_debugSettings.LogGameDatabaseAssetRegistration)
            {
                Debug.Log($"[GameDatabase] Registered weapon: {def.ID}");
            }
        }

        /// <summary>
        /// Register a room template in the database.
        /// Called during initialization or when loading mods.
        /// </summary>
        public void RegisterRoomTemplate(RoomTemplate template)
        {
            if (template == null || string.IsNullOrEmpty(template.id))
            {
                Debug.LogWarning("[GameDatabase] Attempted to register invalid room template");
                return;
            }

            if (_roomTemplates.ContainsKey(template.id))
            {
                if (_debugSettings.LogGameDatabaseDuplicateRegistrationWarnings)
                {
                    Debug.LogWarning($"[GameDatabase] Room template ID '{template.id}' already registered, skipping");
                }
                return;
            }

            _roomTemplates.Add(template.id, template);
            if (_debugSettings.LogGameDatabaseAssetRegistration)
            {
                Debug.Log($"[GameDatabase] Registered room template: {template.id} ({template.type})");
            }
        }

        /// <summary>
        /// Register a shared map object definition.
        /// </summary>
        public void RegisterMapObjectDefinition(MapObjectDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.objectId))
            {
                Debug.LogWarning("[GameDatabase] Attempted to register invalid map object definition");
                return;
            }

            if (_mapObjectDefinitions.ContainsKey(definition.objectId))
            {
                if (_debugSettings.LogGameDatabaseDuplicateRegistrationWarnings)
                {
                    Debug.LogWarning($"[GameDatabase] Map object definition ID '{definition.objectId}' already registered, skipping");
                }
                return;
            }

            _mapObjectDefinitions.Add(definition.objectId, definition);
            if (_debugSettings.LogGameDatabaseAssetRegistration)
            {
                Debug.Log($"[GameDatabase] Registered map object definition: {definition.objectId}");
            }
        }

        #endregion

        #region Lookups — delegate to ContentRegistry, fall back to legacy dictionaries

        public UnitDefinition GetUnit(string id)
        {
            // Try ContentRegistry first (mod-aware path)
            var fromRegistry = Registry.Get<UnitDefinition>(ContentType.Unit, id);
            if (fromRegistry != null) return fromRegistry;

            // Fall back to legacy dictionary (for direct Register calls / tests)
            return _units.TryGetValue(id, out var def) ? def : null;
        }

        public WeaponDefinition GetWeapon(string id)
        {
            var fromRegistry = Registry.Get<WeaponDefinition>(ContentType.Weapon, id);
            if (fromRegistry != null) return fromRegistry;

            return _weapons.TryGetValue(id, out var def) ? def : null;
        }

        public RoomTemplate GetRoomTemplate(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            var fromRegistry = Registry.Get<RoomTemplate>(ContentType.RoomTemplate, id);
            if (fromRegistry != null) return fromRegistry;

            return _roomTemplates.TryGetValue(id, out var template) ? template : null;
        }

        public IEnumerable<string> GetAllUnitIDs()
        {
            var registryIds = Registry.GetAllBareIds(ContentType.Unit);
            return registryIds.Any() ? registryIds : _units.Keys;
        }

        public IEnumerable<string> GetAllWeaponIDs()
        {
            var registryIds = Registry.GetAllBareIds(ContentType.Weapon);
            return registryIds.Any() ? registryIds : _weapons.Keys;
        }

        public IEnumerable<string> GetAllRoomTemplateIDs()
        {
            var registryIds = Registry.GetAllBareIds(ContentType.RoomTemplate);
            return registryIds.Any() ? registryIds : _roomTemplates.Keys;
        }

        public MapObjectDefinition GetMapObjectDefinition(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            var fromRegistry = Registry.Get<MapObjectDefinition>(ContentType.MapObjectDefinition, id);
            if (fromRegistry != null) return fromRegistry;

            return _mapObjectDefinitions.TryGetValue(id, out var definition) ? definition : null;
        }

        public IEnumerable<string> GetAllMapObjectDefinitionIDs()
        {
            var registryIds = Registry.GetAllBareIds(ContentType.MapObjectDefinition);
            return registryIds.Any() ? registryIds : _mapObjectDefinitions.Keys;
        }

        /// <summary>
        /// Get all room templates of a specific type.
        /// </summary>
        public List<RoomTemplate> GetRoomTemplatesByType(string type)
        {
            // Try registry first
            var fromRegistry = Registry.GetAll<RoomTemplate>(ContentType.RoomTemplate)
                .Where(t => t.type == type)
                .ToList();
            if (fromRegistry.Count > 0) return fromRegistry;

            // Fall back to legacy
            var result = new List<RoomTemplate>();
            foreach (var kvp in _roomTemplates)
            {
                if (kvp.Value.type == type)
                    result.Add(kvp.Value);
            }
            return result;
        }

        public IEnumerable<RoomTemplate> GetAllRoomTemplates()
        {
            var fromRegistry = Registry.GetAll<RoomTemplate>(ContentType.RoomTemplate).ToList();
            return fromRegistry.Count > 0 ? fromRegistry : _roomTemplates.Values;
        }

        #endregion

        /// <summary>
        /// Initialize the database by loading all content through the mod pipeline.
        /// Base game content loads first ("pfe.base"), then any discovered mods.
        /// </summary>
        public void Initialize()
        {
            if (_debugSettings.LogGameDatabaseInitializationSummary)
            {
                Debug.Log("[GameDatabase] Starting initialization via content pipeline...");
            }

            // Configure registry logging from debug settings
            Registry.LogRegistrations = _debugSettings.LogGameDatabaseAssetRegistration;
            Registry.LogConflicts = _debugSettings.LogGameDatabaseDuplicateRegistrationWarnings;

            // Build ordered content sources: base game first, then mods
            var sources = Loader.BuildSourceList();

            // Feed all sources into the registry
            Registry.Initialize(sources);

            // Log validation issues from mod discovery
            foreach (var warning in Loader.ValidationLog)
            {
                Debug.LogWarning($"[GameDatabase] Mod validation: {warning}");
            }

            if (_debugSettings.LogGameDatabaseInitializationSummary)
            {
                Debug.Log(Registry.GetSummary());
                Debug.Log("[GameDatabase] Initialization complete");
            }
        }
    }
}
