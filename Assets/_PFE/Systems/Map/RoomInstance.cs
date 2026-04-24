using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Room instance data.
    /// From AS3: Location class - individual room with tile grid and entities.
    /// </summary>
    [Serializable]
    public class RoomInstance
    {
        // Identification
        public string id;
        public string templateId;
        public Vector3Int landPosition;

        // Tile grid
        public TileData[,] tiles;

        // Dimensions
        public int width = WorldConstants.ROOM_WIDTH;
        public int height = WorldConstants.ROOM_HEIGHT;
        
        // Border offset (tiles added on each side when border is applied)
        // Used to convert between room-local and world pixel coordinates
        public int borderOffset = 0;

        // Room state
        public bool isActive = false;
        public bool isVisited = false;

        // Room properties
        public RoomDifficulty difficulty = new RoomDifficulty();
        public RoomEnvironment environment = new RoomEnvironment();

        // Lists of entities
        [NonSerialized]
        public List<UnitInstance> units = new List<UnitInstance>();

        [NonSerialized]
        public List<ObjectInstance> objects = new List<ObjectInstance>();

        public List<DoorInstance> doors = new List<DoorInstance>();
        public List<SpawnPoint> spawnPoints = new List<SpawnPoint>();
        public List<BackgroundDecorationInstance> backgroundDecorations = new List<BackgroundDecorationInstance>();

        // Special features
        public bool hasBackgroundLayer = false;
        [NonSerialized]
        public RoomInstance backgroundRoom;

        // Metadata
        public string roomType = "";  // beg0, pass, roof, etc.

        [NonSerialized]
        private RoomObjectPhysicsLayer _objectPhysicsLayer;

        public RoomObjectPhysicsLayer ObjectPhysicsLayer
        {
            get
            {
                if (_objectPhysicsLayer == null)
                {
                    _objectPhysicsLayer = new RoomObjectPhysicsLayer();
                }

                return _objectPhysicsLayer;
            }
        }

        public void RebuildRuntimeLayers()
        {
            ObjectPhysicsLayer.Rebuild(objects);
        }

        public void AddObject(ObjectInstance obj)
        {
            if (obj == null)
            {
                return;
            }

            obj.EnsureStructuredData();
            objects.Add(obj);
            ObjectPhysicsLayer.Register(obj);
        }

        public bool RemoveObject(ObjectInstance obj)
        {
            if (obj == null)
            {
                return false;
            }

            bool removed = objects.Remove(obj);
            if (removed)
            {
                ObjectPhysicsLayer.Unregister(obj);
            }

            return removed;
        }

        public void ClearObjects()
        {
            objects.Clear();
            RebuildRuntimeLayers();
        }

        public bool TryFindNearestTelekineticObject(Vector2 origin, float maxDistancePixels, out ObjectInstance obj)
        {
            ObjectPhysicsLayer.EnsureSynchronized(objects);
            return ObjectPhysicsLayer.TryFindNearestTelekineticObject(origin, maxDistancePixels, out obj);
        }

        public bool TryHoldObject(ObjectInstance obj, Vector2 targetPosition)
        {
            ObjectPhysicsLayer.EnsureSynchronized(objects);
            return ObjectPhysicsLayer.TrySetTelekineticHold(obj, targetPosition);
        }

        public bool TryReleaseHeldObject(ObjectInstance obj, Vector2 releaseVelocity, bool treatAsThrow = true)
        {
            ObjectPhysicsLayer.EnsureSynchronized(objects);
            return ObjectPhysicsLayer.TryReleaseTelekineticHold(obj, releaseVelocity, treatAsThrow);
        }

        public bool TryApplyObjectImpulse(ObjectInstance obj, Vector2 deltaVelocity, bool treatAsThrow = false)
        {
            ObjectPhysicsLayer.EnsureSynchronized(objects);
            return ObjectPhysicsLayer.TryApplyImpulse(obj, deltaVelocity, treatAsThrow);
        }

        /// <summary>
        /// Initialize tile grid.
        /// </summary>
        public void InitializeTiles()
        {
            tiles = new TileData[width, height];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    tiles[x, y] = new TileData
                    {
                        gridPosition = new Vector2Int(x, y)
                    };
                }
            }
        }

        /// <summary>
        /// Get tile at pixel position.
        /// </summary>
        public TileData GetTileAt(Vector2 pixelPos)
        {
            Vector2Int tileCoord = WorldCoordinates.PixelToTile(pixelPos);
            return GetTileAtCoord(tileCoord);
        }

        /// <summary>
        /// Get tile at tile coordinates.
        /// </summary>
        public TileData GetTileAtCoord(Vector2Int coord)
        {
            if (coord.x < 0 || coord.x >= width || coord.y < 0 || coord.y >= height)
            {
                return null;
            }
            return tiles[coord.x, coord.y];
        }

        /// <summary>
        /// Check collision with tiles at position.
        /// </summary>
        public bool CheckCollision(Vector2 pos, Vector2 size)
        {
            Vector2Int topLeft = WorldCoordinates.PixelToTile(pos);
            Vector2Int bottomRight = WorldCoordinates.PixelToTile(pos + size);

            for (int x = topLeft.x; x <= bottomRight.x; x++)
            {
                for (int y = topLeft.y; y <= bottomRight.y; y++)
                {
                    TileData tile = GetTileAtCoord(new Vector2Int(x, y));
                    if (tile != null && tile.IsSolid())
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Get ground height at position (handles slopes).
        /// </summary>
        public float GetGroundHeight(Vector2 pos)
        {
            TileData tile = GetTileAt(pos);
            if (tile != null)
            {
                return tile.GetGroundHeight(pos.x);
            }
            return pos.y;
        }

        /// <summary>
        /// Activate this room (called when player enters).
        /// From AS3: Location.reactivate()
        /// </summary>
        public void Activate()
        {
            isActive = true;
            isVisited = true;
            RebuildRuntimeLayers();

            // Activate units
            foreach (var unit in units)
            {
                if (unit != null && !unit.IsDead)
                {
                    unit.Activate();
                }
            }

            // Activate objects
            foreach (var obj in objects)
            {
                if (obj != null)
                {
                    obj.Activate();
                }
            }
        }

        /// <summary>
        /// Deactivate this room (called when player leaves).
        /// </summary>
        public void Deactivate()
        {
            isActive = false;

            // Deactivate units
            foreach (var unit in units)
            {
                if (unit != null)
                {
                    unit.Deactivate();
                }
            }

            // Deactivate objects
            foreach (var obj in objects)
            {
                if (obj != null)
                {
                    obj.Deactivate();
                }
            }
        }

        /// <summary>
        /// Update room (called every frame if active).
        /// From AS3: Location.step()
        /// </summary>
        public void Update()
        {
            if (!isActive) return;

            ObjectPhysicsLayer.Update(this);

            // Update units
            for (int i = units.Count - 1; i >= 0; i--)
            {
                if (units[i] != null && !units[i].IsDead)
                {
                    units[i].Update();
                }
                else if (units[i] != null && units[i].IsDead)
                {
                    units.RemoveAt(i);
                }
            }

            // Update objects
            foreach (var obj in objects)
            {
                if (obj != null)
                {
                    obj.Update();
                }
            }
        }

        /// <summary>
        /// Limited update (called for previous room).
        /// From AS3: Location.stepInvis()
        /// </summary>
        public void UpdateLimited()
        {
            // Only update active objects, no AI
            foreach (var obj in objects)
            {
                if (obj != null && obj.isActive)
                {
                    obj.UpdateLimited();
                }
            }
        }

        /// <summary>
        /// Get a random spawn point for the player.
        /// </summary>
        public Vector2 GetPlayerSpawnPoint()
        {
            if (spawnPoints.Count > 0)
            {
                // Find first player spawn
                foreach (var spawn in spawnPoints)
                {
                    if (spawn.type == SpawnType.Player)
                    {
                        return spawn.GetWorldPosition();
                    }
                }
                // Fallback to any spawn
                return spawnPoints[0].GetWorldPosition();
            }

            // Default: center of room
            return new Vector2(
                width * WorldConstants.TILE_SIZE / 2,
                height * WorldConstants.TILE_SIZE / 2
            );
        }

        /// <summary>
        /// Save room state.
        /// </summary>
        public void SaveState()
        {
            // Save tile modifications
            foreach (var tile in tiles)
            {
                if (tile != null)
                {
                    // Tile state is serialized automatically
                }
            }

            // Save entity states
            foreach (var unit in units)
            {
                unit.SaveState();
            }

            foreach (var obj in objects)
            {
                obj.SaveState();
            }
        }

        /// <summary>
        /// Load room state.
        /// </summary>
        public void LoadState()
        {
            // Load entity states
            foreach (var unit in units)
            {
                unit.LoadState();
            }

            foreach (var obj in objects)
            {
                obj.LoadState();
            }

            RebuildRuntimeLayers();
        }
    }

    /// <summary>
    /// Placeholder for Unit instance (will be implemented separately).
    /// </summary>
    [Serializable]
    public class UnitInstance
    {
        public string unitId;
        public string unitType = "";
        public Vector2 position;
        public bool isDead = false;
        public bool IsDead => isDead;
        public float currentHealth = 100f;
        public float maxHealth = 100f;

        public void Activate() { }
        public void Deactivate() { }
        public void Update() { }
        public void SaveState() { }
        public void LoadState() { }
    }

    /// <summary>
    /// Placeholder for Object instance (will be implemented separately).
    /// </summary>
    [Serializable]
    public class ObjectInstance
    {
        public string objectId;
        public string objectType = "";
        public string definitionId = "";
        public MapObjectDefinition definition;
        public string code = "";
        public string uid = "";
        public List<MapObjectAttributeData> attributes = new List<MapObjectAttributeData>();
        public List<MapObjectItemData> items = new List<MapObjectItemData>();
        public List<MapObjectScriptData> scripts = new List<MapObjectScriptData>();
        public string parameters = "";
        public Vector2 position;
        public bool isActive = true;
        public MapObjectRuntimeStateData runtimeState = new MapObjectRuntimeStateData();

        public void EnsureStructuredData()
        {
            if (string.IsNullOrWhiteSpace(definitionId) && !string.IsNullOrWhiteSpace(objectId))
            {
                definitionId = objectId;
            }

            if ((attributes == null || attributes.Count == 0) && !string.IsNullOrWhiteSpace(parameters))
            {
                attributes = MapObjectDataUtility.ParseLegacyParameters(parameters, out string parsedCode, out string parsedUid);
                if (string.IsNullOrEmpty(code))
                {
                    code = parsedCode;
                }

                if (string.IsNullOrEmpty(uid))
                {
                    uid = parsedUid;
                }
            }

            attributes ??= new List<MapObjectAttributeData>();
            items ??= new List<MapObjectItemData>();
            scripts ??= new List<MapObjectScriptData>();
            runtimeState ??= new MapObjectRuntimeStateData();
            runtimeState.dynamicState ??= new MapObjectDynamicStateData();
        }

        public string GetAttribute(string key, string defaultValue = "")
        {
            EnsureStructuredData();

            if (string.Equals(key, "code", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrEmpty(code) ? defaultValue : code;
            }

            if (string.Equals(key, "uid", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrEmpty(uid) ? defaultValue : uid;
            }

            return MapObjectDataUtility.GetAttribute(attributes, key, defaultValue);
        }

        public string GetResolvedDefinitionId()
        {
            if (definition != null && !string.IsNullOrWhiteSpace(definition.objectId))
            {
                return definition.objectId;
            }

            if (!string.IsNullOrWhiteSpace(definitionId))
            {
                return definitionId;
            }

            return objectId ?? string.Empty;
        }

        public MapObjectPhysicalCapability GetResolvedPhysicalCapability()
        {
            EnsureStructuredData();

            if (definition != null)
            {
                return definition.GetResolvedPhysicalCapability();
            }

            return MapObjectDefinitionClassifier.ResolvePhysicalCapability(
                objectId,
                ResolveFallbackFamily(),
                GetAttribute("tip", objectType),
                key => GetAttribute(key, string.Empty));
        }

        public bool IsDynamicPhysicalProp()
        {
            MapObjectPhysicalCapability capability = GetResolvedPhysicalCapability();
            return capability == MapObjectPhysicalCapability.DynamicPassive ||
                   capability == MapObjectPhysicalCapability.DynamicThrowable ||
                   capability == MapObjectPhysicalCapability.DynamicTelekinetic;
        }

        public bool SupportsTelekinesis()
        {
            return GetResolvedPhysicalCapability() == MapObjectPhysicalCapability.DynamicTelekinetic;
        }

        public bool CanBeThrown()
        {
            MapObjectPhysicalCapability capability = GetResolvedPhysicalCapability();
            return capability == MapObjectPhysicalCapability.DynamicThrowable ||
                   capability == MapObjectPhysicalCapability.DynamicTelekinetic;
        }

        public float GetResolvedMass()
        {
            EnsureStructuredData();

            if (definition != null)
            {
                return definition.GetResolvedMass();
            }

            float massMultiplier = TryParseFloatAttribute("massaMult", 1f);
            float explicitMass = TryParseFloatAttribute("massa", 0f);
            if (explicitMass > 0f)
            {
                return explicitMass * Mathf.Max(0.01f, massMultiplier);
            }

            Vector2 sizePixels = GetApproximatePixelSize();
            float derivedMass = Mathf.Max(1f, (sizePixels.x / WorldConstants.TILE_SIZE) * (sizePixels.y / WorldConstants.TILE_SIZE) * 50f);
            return derivedMass * Mathf.Max(0.01f, massMultiplier);
        }

        public float GetResolvedBuoyancyFactor()
        {
            EnsureStructuredData();

            if (definition != null)
            {
                return definition.GetResolvedBuoyancyFactor();
            }

            return TryParseFloatAttribute("plav", 0f);
        }

        public Vector2 GetApproximatePixelSize()
        {
            EnsureStructuredData();

            float widthPixels = TryParseFloatAttribute("scx", 0f);
            float heightPixels = TryParseFloatAttribute("scy", 0f);

            if (widthPixels <= 0f)
            {
                int widthTiles = definition != null ? Mathf.Max(1, definition.size) : TryParseIntAttribute("size", 1);
                widthPixels = widthTiles * WorldConstants.TILE_SIZE;
            }

            if (heightPixels <= 0f)
            {
                int heightTiles = definition != null ? Mathf.Max(1, definition.width) : TryParseIntAttribute("wid", 1);
                heightPixels = heightTiles * WorldConstants.TILE_SIZE;
            }

            return new Vector2(
                Mathf.Max(8f, widthPixels),
                Mathf.Max(8f, heightPixels));
        }

        public Rect GetApproximateBounds()
        {
            return GetApproximateBounds(position);
        }

        public Rect GetApproximateBounds(Vector2 targetPosition)
        {
            Vector2 sizePixels = GetApproximatePixelSize();
            return new Rect(
                targetPosition.x - sizePixels.x * 0.5f,
                targetPosition.y - sizePixels.y,
                sizePixels.x,
                sizePixels.y);
        }

        public void InitializeDynamicRuntimeState()
        {
            EnsureStructuredData();
            runtimeState.dynamicState.isDynamic = ShouldTrackInPhysicsLayer();
            if (!runtimeState.dynamicState.isDynamic)
            {
                runtimeState.dynamicState.isGrounded = false;
                runtimeState.dynamicState.isHeldByTelekinesis = false;
                runtimeState.dynamicState.isThrown = false;
                runtimeState.dynamicState.hasTelekineticTarget = false;
                runtimeState.dynamicState.velocity = Vector2.zero;
                runtimeState.dynamicState.telekineticTarget = Vector2.zero;
                runtimeState.dynamicState.throwGraceTime = 0f;
                runtimeState.dynamicState.lastImpactSpeed = 0f;
            }
        }

        public bool ShouldTrackInPhysicsLayer()
        {
            EnsureStructuredData();
            return IsDynamicPhysicalProp() && !runtimeState.isDestroyed;
        }

        public bool ShouldSimulateDynamicPhysics()
        {
            EnsureStructuredData();
            return ShouldTrackInPhysicsLayer() && isActive && runtimeState.dynamicState.isDynamic;
        }

        public bool IsDestroyed()
        {
            EnsureStructuredData();
            return runtimeState.isDestroyed;
        }

        public float GetEstimatedImpactDamage(float impactSpeed)
        {
            if (impactSpeed <= 0f || !IsDynamicPhysicalProp())
            {
                return 0f;
            }

            float normalizedMass = Mathf.Max(1f, GetResolvedMass()) / 100f;
            float normalizedSpeed = impactSpeed / 220f;
            float capabilityMultiplier = GetResolvedPhysicalCapability() switch
            {
                MapObjectPhysicalCapability.DynamicPassive => 0.45f,
                MapObjectPhysicalCapability.DynamicThrowable => 0.8f,
                MapObjectPhysicalCapability.DynamicTelekinetic => 0.65f,
                _ => 0f
            };

            return Mathf.Max(0f, normalizedMass * normalizedSpeed * capabilityMultiplier);
        }

        public bool HasEnabledLightFlag()
        {
            string rawValue = GetAttribute("light", string.Empty);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            return rawValue == "1" ||
                rawValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                rawValue.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                rawValue.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        public void RefreshLegacyParameters()
        {
            EnsureStructuredData();
            parameters = MapObjectDataUtility.BuildLegacyParameters(code, uid, attributes);
        }

        MapObjectFamily ResolveFallbackFamily()
        {
            if (string.Equals(objectType, "door", StringComparison.OrdinalIgnoreCase))
            {
                return MapObjectFamily.Door;
            }

            if (string.Equals(objectType, "trap", StringComparison.OrdinalIgnoreCase))
            {
                return MapObjectFamily.Trap;
            }

            if (string.Equals(objectType, "checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                return MapObjectFamily.Checkpoint;
            }

            if (string.Equals(objectType, "area", StringComparison.OrdinalIgnoreCase))
            {
                return MapObjectFamily.AreaTrigger;
            }

            if (string.Equals(objectType, "bonus", StringComparison.OrdinalIgnoreCase))
            {
                return MapObjectFamily.Bonus;
            }

            return MapObjectFamily.GenericObject;
        }

        float TryParseFloatAttribute(string key, float defaultValue)
        {
            string rawValue = GetAttribute(key, string.Empty);
            return float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue)
                ? parsedValue
                : defaultValue;
        }

        int TryParseIntAttribute(string key, int defaultValue)
        {
            string rawValue = GetAttribute(key, string.Empty);
            return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue)
                ? parsedValue
                : defaultValue;
        }

        public void Activate() { }
        public void Deactivate() { }
        public void Update() { }
        public void UpdateLimited() { }
        public void SaveState() { }
        public void LoadState() { }
    }

    /// <summary>
    /// Runtime room background decoration placement.
    /// </summary>
    [Serializable]
    public class BackgroundDecorationInstance
    {
        public string decorationId;
        public Vector2Int tileCoord;
    }
}
