using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Physics;
using PFE.Systems.Map.TileQuery;

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

        /// <summary>
        /// The physics layer <b>if one already exists</b>, without creating one. Null for a room that
        /// has never had an object added nor been rebuilt.
        /// </summary>
        /// <remarks>
        /// <para><b>For read-only consumers only.</b> <see cref="ObjectPhysicsLayer"/> is lazy and
        /// allocates on first touch, so a diagnostic that read it would manufacture the very object it
        /// was trying to report on — and an empty layer is indistinguishable from a room with no props,
        /// which is the worst possible answer for a tool whose whole job is to report that count.</para>
        ///
        /// <para>Every room that has gone through <see cref="AddObject"/> or
        /// <see cref="RebuildRuntimeLayers"/> already has one, so null here means "this room genuinely
        /// has no physics layer", which is a fact worth reporting rather than papering over.</para>
        /// </remarks>
        public RoomObjectPhysicsLayer ExistingObjectPhysicsLayer => _objectPhysicsLayer;

        // ── Tile mutation notification ──────────────────────────────────────────────────────

        /// <summary>
        /// Raised when tiles in this room change by a path outside the motor's own move
        /// resolution — tile destruction today, scripted edits and streaming later.
        ///
        /// <para>The region is in ROOM-LOCAL tile coordinates and <b>includes a one-tile border
        /// around the change</b>. That is not padding: derived geometry is built from runs of
        /// adjacent solid tiles, so removing one tile changes the surfaces of its neighbours too
        /// (a run splits in two, a vertical face appears). A listener that rebuilt only the exact
        /// tile would leave stale geometry one tile out.</para>
        ///
        /// <para>Listeners holding derived geometry — today the LowLevelPhysics2D chain mirror —
        /// rebuild here. This is a host-authoritative mutation notification, not Sim state;
        /// replication of the change itself is P4's concern.</para>
        /// </summary>
        public event Action<RoomInstance, RectInt> TilesMutated;

        /// <summary>
        /// Announces that tiles changed. Called by tile destruction and forwarded by
        /// <see cref="ITileQueryService.NotifyTilesMutated"/>. Safe to call with no listeners.
        /// </summary>
        public void NotifyTilesMutated(RectInt tileRegion)
        {
            TilesMutated?.Invoke(this, tileRegion);
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
            // The `tiles == null` test is load-bearing, not defensive padding. `width`/`height` have
            // non-zero defaults (WorldConstants.ROOM_WIDTH/HEIGHT) while `tiles` has no initialiser,
            // so a RoomInstance that exists but has not been filled in yet passes the bounds check
            // below and dereferences a null array. That is reachable in practice: Unity cannot
            // serialise a multidimensional array, so `tiles` is also lost when a script recompile
            // triggers a domain reload while the game is running. Returning null — "no tile here" —
            // is exactly what the bounds check already means, so this keeps one contract.
            if (tiles == null ||
                coord.x < 0 || coord.x >= width ||
                coord.y < 0 || coord.y >= height)
            {
                return null;
            }
            return tiles[coord.x, coord.y];
        }

        /// <summary>
        /// Check collision with tiles at position (room-local coordinates).
        /// Converted to world-pixel bounds and evaluated against unified tile collision logic.
        /// </summary>
        public bool CheckCollision(Vector2 pos, Vector2 size)
        {
            float roomWorldPixelX = landPosition.x * WorldConstants.ROOM_WIDTH * WorldConstants.TILE_SIZE
                                  - borderOffset * WorldConstants.TILE_SIZE;
            float roomWorldPixelY = landPosition.y * WorldConstants.ROOM_HEIGHT * WorldConstants.TILE_SIZE
                                  - borderOffset * WorldConstants.TILE_SIZE;

            Rect worldBounds = new Rect(pos.x + roomWorldPixelX, pos.y + roomWorldPixelY, size.x, size.y);

            return TileCollisionMath.CheckCollision(
                this,
                worldBounds,
                roomWorldPixelX,
                roomWorldPixelY,
                platformThreshold: TileQueryConstants.PorogGrounded,
                isTransparent: false,
                canFallThroughPlatforms: false,
                velocityY: 0f);
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
        /// <remarks>
        /// Uses <see cref="RoomObjectPhysicsLayer.LegacyPerFrameDeltaTime"/>, the historical hardcoded
        /// 1/60 step, so the per-frame driver's behaviour is unchanged by the sim-tick work. The
        /// sim-driven heartbeat calls <see cref="Update(float)"/> with <c>SimClock.SimDt</c> instead.
        /// </remarks>
        public void Update()
        {
            Update(RoomObjectPhysicsLayer.LegacyPerFrameDeltaTime);
        }

        /// <summary>
        /// Update room with an explicit simulation step. One call advances the room by
        /// <paramref name="deltaTime"/> seconds; at <c>SimClock.SimDt</c> that is exactly one AS3
        /// frame at 30 fps.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!isActive) return;

            // `room.tick` is the per-frame room step (called from the sim tick when the sim drives,
            // and from the legacy Update otherwise). `room.tick.units` / `room.tick.objects` are the
            // two loops that scale with how many of each the room holds — which is the whole
            // difference between the camp and a corridor.
            using (PFE.Core.Profiling.PfeProfiler.Region("room.tick",
                "physics: one room step. Children are the prop layer and the unit/object loops; self is the rest."))
            {
            using (PFE.Core.Profiling.PfeProfiler.Region("phys.objects.tick",
                "physics: RoomObjectPhysicsLayer.Update — the prop physics tick (sync + every dynamic prop's step)."))
            {
                ObjectPhysicsLayer.Update(this, deltaTime);
            }

            // Update units
            using (PFE.Core.Profiling.PfeProfiler.Region("room.tick.units",
                "physics: per-frame unit loop. ObjectInstance.Update/UnitInstance.Update are empty, so this should be ~0."))
            {
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
            }

            // Update objects
            using (PFE.Core.Profiling.PfeProfiler.Region("room.tick.objects",
                "physics: per-frame prop loop. ObjectInstance.Update is empty, so this should be ~0."))
            {
                foreach (var obj in objects)
                {
                    if (obj != null)
                    {
                        obj.Update();
                    }
                }
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
        public string entityId = "";
        public string unitType = "";
        public Vector2 position;
        public bool isDead = false;
        public bool IsDead => isDead;
        public float currentHealth = 100f;
        public float maxHealth = 100f;

        /// <summary>
        /// The AS3 controller class name that <c>Unit.as:708</c> switches on to pick a controller class
        /// — e.g. <c>"UnitTrain"</c> for the training dummy.
        ///
        /// <para><b>Where it actually comes from.</b> <c>cl</c> lives on the <c>&lt;obj&gt;</c>
        /// <i>definition</i> row — <c>AllData.as:5048</c>
        /// <c>&lt;obj ed='13' ico='pon' tip='unit' id='training' cl='UnitTrain' …/&gt;</c>. <c>Unit.as:702</c>
        /// rebinds its local <c>node</c> with <c>node = AllData.d.obj.(@id == id)[0]</c> and <c>:708</c>
        /// reads <c>node.@cl</c> from that; <c>param3</c>, the <i>placed</i> node, is a different variable
        /// and never carries <c>cl</c> (0 of the 564 room assets in <c>Resources/Rooms</c> do). A comment
        /// here used to claim the opposite.</para>
        ///
        /// <para>Because the lookup is keyed on the unit id alone, one id always resolves to one
        /// controller (67 <c>cl=</c> rows; <c>npc</c> and <c>vendor</c> share <c>UnitNPC</c>, no id has
        /// two). This field is therefore a <b>cache of a definition-level value</b>, carried on the
        /// placement so <c>RoomUnitSpawner</c> can build the right controller without re-resolving the
        /// definition — not an independent per-placement property.</para>
        ///
        /// <para><b>Careful with the lookalike.</b> <c>UnitDefinition.controllerId</c> is populated by
        /// <c>UnitDataImporter</c> from the <c>cont=</c> attribute on <c>&lt;unit&gt;</c> rows (40 of
        /// them), while the controller selector is <c>cl=</c> on <c>&lt;obj&gt;</c> rows. They are
        /// different attributes on different elements and they are not interchangeable.</para>
        /// </summary>
        public string controllerId = "";

        /// <summary>
        /// The placement attributes from the source <c>&lt;obj&gt;</c> row (<c>turn</c>, <c>fix</c>,
        /// <c>tr</c>, <c>light</c>, …).
        ///
        /// <para>These used to be dropped at population: <c>RoomPopulator.CreateUnit</c> took only a
        /// unit id. But a controller reads them in its constructor — <c>UnitTrain.as:16-38</c> takes
        /// <c>turn</c> for facing, <c>tr</c> for the armoured variant and <c>fix</c> for immobility, and
        /// <c>tr == 1</c> is what sets <c>skin = 20</c> (<c>:41-49</c>). Without them the armoured dummy
        /// is indistinguishable from the plain one.</para>
        /// </summary>
        public List<MapObjectAttributeData> attributes = new List<MapObjectAttributeData>();

        /// <summary>
        /// The facing this placement resolved to: 1 = right, -1 = left.
        ///
        /// <para>Resolved once by <c>RoomPopulator</c> from the <c>turn</c> attribute
        /// (<c>Unit.as:596-611</c>), because the "absent <c>turn</c>" case is a coin flip on the spawn
        /// RNG and that stream belongs to the generation layer. Storing the answer keeps the presenter
        /// pure and the value reproducible.</para>
        /// </summary>
        public int facingDirection = 1;

        public string GetAttribute(string key, string defaultValue = "")
        {
            return MapObjectDataUtility.GetAttribute(attributes, key, defaultValue);
        }

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
        public string entityId = "";
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

        /// <summary>
        /// The <see cref="parameters"/> value the one-time legacy migration has already run on, or null
        /// if it has not run. Not serialized — it is a per-run cache, so the room assets are unchanged.
        ///
        /// <para><b>Why the existing guard is not enough.</b> The migration is gated on "we have no
        /// attributes yet" (<c>attributes.Count == 0</c>), but an authored prop ships
        /// <c>attributes: []</c> together with <c>parameters: code="..."</c> — and
        /// <see cref="MapObjectDataUtility.ParseLegacyParameters"/> routes <c>code</c>/<c>uid</c> to its
        /// out-params and adds <b>nothing</b> to the returned list, so the list comes back empty,
        /// "no attributes yet" stays true, and the regex re-runs on <i>every</i> call, forever. This is
        /// not a rare shape: it is every box in the workshop rooms.</para>
        ///
        /// <para><b>Why that costs a frame.</b> <see cref="EnsureStructuredData"/> is called from every
        /// predicate on the physics hot path (<see cref="IsDestroyed"/>, <see cref="IsShelf"/>,
        /// <see cref="ShouldSimulateDynamicPhysics"/>, <see cref="ShouldTrackInPhysicsLayer"/>, and via
        /// <see cref="GetApproximatePixelSize"/>), several times per prop per tick. Measured in the camp:
        /// <c>phys.objects.step</c> self <b>814.2 ms over 191 ticks and 38 tracked props = 112 µs per prop
        /// per tick</b>, for a loop whose own arithmetic is a handful of float ops.</para>
        ///
        /// <para>Keyed on the <i>value</i> rather than a plain "attempted" flag, so a caller that assigns
        /// <see cref="parameters"/> after construction still gets its migration; an unchanged string
        /// skips only a parse whose result we already hold.</para>
        /// </summary>
        private string _migratedFromParameters;

        public void EnsureStructuredData()
        {
            if (string.IsNullOrWhiteSpace(definitionId) && !string.IsNullOrWhiteSpace(objectId))
            {
                definitionId = objectId;
            }

            // The third clause is load-bearing: a `parameters` string holding only `code="..."` parses to
            // ZERO attributes, which leaves `attributes` empty — the very condition that lets this branch
            // run. Without the memo the regex re-runs on every call, several times per prop per tick.
            // See _migratedFromParameters.
            if ((attributes == null || attributes.Count == 0) &&
                !string.IsNullOrWhiteSpace(parameters) &&
                !string.Equals(parameters, _migratedFromParameters, StringComparison.Ordinal))
            {
                _migratedFromParameters = parameters;
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

        /// <summary>
        /// How long the player must hold the action key on this object before it acts, in frames —
        /// AS3 <c>Interact.t_action</c>.
        ///
        /// <para><b>Where the number comes from.</b> The authored <c>time</c> attribute. AS3 reads it
        /// from the object <i>definition</i> row first (<c>Interact.as:308-311</c>,
        /// <c>param2.@time</c>) and then lets the <i>placed</i> node override it (<c>:399-402</c>,
        /// <c>this.xml.@time</c>), each guarded by <c>.length()</c> — so the placed value wins only
        /// when it is actually present. Same shape as <c>cl</c>, and the same reason: a per-placement
        /// override of a definition-level value.</para>
        ///
        /// <para><b>Absent means instant, and that is the overwhelmingly common case.</b> Only 22 of
        /// the 19,461 <c>&lt;obj&gt;</c> definition rows in AllData author a <c>time</c> — the seven Z
        /// doors (<c>indoor1..4</c>, <c>instdoor</c>, <c>inbasedoor</c>, <c>inencldoor</c>, all
        /// <c>time='10'</c>), the metal doors and the terminals. Every plain <c>door1</c>,
        /// <c>door2</c>, <c>hatch</c> and <c>instr</c> row omits it, so returning 0 here is what keeps
        /// those acting on the press exactly as they do today. AS3's <c>t_action</c> also defaults to
        /// 0 (<c>Interact.as:102</c>), and its zero branch fires <c>is_act</c> directly without ever
        /// arming a timer (<c>UnitPlayer.as:1985-1988</c>).</para>
        ///
        /// <para><b>Note the two <c>comein</c> doors that are instant:</b> <c>door_st1</c> and
        /// <c>door_st2</c> carry <c>allact='comein'</c> but no <c>time</c>. They are not an oversight
        /// to correct — AS3 gives them no hold, and neither does this.</para>
        /// </summary>
        public int GetHoldFrames()
        {
            EnsureStructuredData();

            // The placed node wins when it carries a usable number, and an explicit 0 counts as usable:
            // AS3's guard is `.length()`, so a present `time='0'` assigns t_action = 0 and the action
            // fires at once rather than inheriting the definition's hold. Treating 0 as "absent" here
            // would hand a deliberately instant placement the definition's duration.
            if (TryParseHoldFrames(MapObjectDataUtility.GetAttribute(attributes, "time", string.Empty), out int placedFrames))
            {
                return placedFrames;
            }

            if (definition != null &&
                TryParseHoldFrames(definition.GetAttribute("time", string.Empty), out int definitionFrames))
            {
                return definitionFrames;
            }

            return 0;
        }

        /// <summary>
        /// The object's <c>allact</c> script id, or empty when it has none.
        ///
        /// <para><b>Placement first, then definition</b> — the same order as <see cref="GetHoldFrames"/>
        /// and the same order AS3 uses: <c>Interact.as:287-289</c> copies <c>param2.@allact</c> (the
        /// definition row) and <c>:383-385</c> then lets the placed node override it with
        /// <c>this.xml.@allact</c>.</para>
        ///
        /// <para><b>Why the definition half is load-bearing.</b> The seven Z doors author
        /// <c>allact='comein'</c> on the <i>definition</i> (<c>AllData.as:4916-4922</c>) and say nothing
        /// about it on the placement — <c>RoomsCamp.as:115</c> is
        /// <c>&lt;obj id="indoor2" code="BJ1whp5k1LO1jk6w" x="16" y="15" locktip="0" lock="1"
        /// uid="doorRBL1"/&gt;</c>. Reading only the placement therefore reported "no script" for every Z
        /// door in the camp, so <c>ObjectActionDispatcher</c> returned <c>NotApplicable</c> and
        /// <c>DoorPropPresenter.Interact</c> fell through to opening the door instead of moving the
        /// player to the other layer.</para>
        ///
        /// <para>Only 4 of the 639 imported room assets carry an <c>allact</c> on a placement (the
        /// <c>doorboss</c> nodes); the rest — the Z doors among them — rely on this fallback.</para>
        /// </summary>
        public string GetAllAct()
        {
            EnsureStructuredData();

            string placed = MapObjectDataUtility.GetAttribute(attributes, "allact", string.Empty);
            if (!string.IsNullOrWhiteSpace(placed))
            {
                return placed;
            }

            return definition != null ? definition.GetAttribute("allact", string.Empty) : string.Empty;
        }

        /// <summary>
        /// Whether this object is a <b>door box</b> in AS3's sense — i.e. its <i>definition</i> row
        /// authors <c>door=</c>.
        ///
        /// <para><b>The oracle is the attribute, not the id and not the family.</b>
        /// <c>Box.as:290-297</c> reads <c>node.@door</c> — and <c>node</c> is the definition row,
        /// <c>AllData.d.obj.(@id == id)[0]</c> (<c>:155</c>) — and only then calls <c>initDoor()</c>,
        /// which is what stamps the box's tiles solid (<c>:653-673</c>) and what <c>setDoor()</c>
        /// toggles (<c>:679</c>). Exactly 21 <c>&lt;obj&gt;</c> rows in AllData declare it:
        /// <c>door1..4</c>, <c>hatch1/2</c>, <c>stdoor</c>, <c>basedoor</c>, <c>encldoor</c>,
        /// <c>enclpole</c>, <c>alib1/2</c>, <c>septum</c>, <c>grate</c>, <c>hgrate</c>,
        /// <c>platform1</c>, <c>window1/2</c> — and the same 21 definition assets carry
        /// <c>legacyAttributes: door=…</c>.</para>
        ///
        /// <para><b>The Z doors are not door boxes.</b> <c>indoor1..4</c>, <c>instdoor</c>,
        /// <c>inbasedoor</c>, <c>inencldoor</c> declare <c>allact='comein'</c>, <c>inter='8'</c> and
        /// <c>open='…'</c> but <b>no</b> <c>door=</c> — so AS3 never runs <c>initDoor</c> on them, never
        /// stamps their tiles, and never gives them an open/close state. They are interactive
        /// <c>Box</c> props whose interaction happens to be a layer toggle. Their tiles are open
        /// ground (<c>RoomsCamp.as</c> places <c>indoor2</c> at 16,15 and 16,7; both are <c>_</c> in the
        /// imported tile map), so stamping them solid would wall off the doorway the player has to walk
        /// into.</para>
        ///
        /// <para><b>A missing definition answers <c>true</c>, on purpose.</b> With no definition row
        /// there is no <c>door=</c> to read, so the question is unanswerable; this returns the
        /// <i>historical</i> answer rather than the strict one, because the only caller is a presenter
        /// that routing sent here precisely because the object looked like a door. Answering
        /// <c>false</c> would let a broken definition reference silently un-stamp a real door — the
        /// same "keep the incumbent so the outcome is deterministic" choice <c>LandMap.AddRoom</c>
        /// makes for a coordinate clash. A real imported object always has a definition, so this branch
        /// is reachable only from a hand-built fixture or a missing GUID.</para>
        /// </summary>
        public bool IsDoorBox()
        {
            EnsureStructuredData();

            if (definition == null)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(definition.GetAttribute("door", string.Empty));
        }

        /// <summary>
        /// Parses a <c>time</c> attribute. False means "no usable number here, try the next source" —
        /// i.e. the attribute is absent, blank, or not a number.
        ///
        /// <para>A present but non-positive number is a <b>usable</b> answer, not a miss: it parses to 0,
        /// which is the authored way of saying "no hold". A negative is clamped to 0 rather than
        /// rejected, because it can only ever be a typo for 0 and 0 is the safe reading.</para>
        /// </summary>
        private static bool TryParseHoldFrames(string raw, out int frames)
        {
            frames = 0;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return false;
            }

            frames = parsed > 0 ? parsed : 0;
            return true;
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
            if (GetResolvedPhysicalCapability() == MapObjectPhysicalCapability.DynamicTelekinetic)
            {
                return true;
            }

            // The debug "grab anything" switch widens this to EVERY dynamic prop, and it is widened here,
            // at the one predicate, deliberately. Four gates read this method — the candidate filter
            // (RoomObjectPhysicsLayer:254), TrySetTelekineticHold (:326), StepDynamicObject (:452) and
            // IsLiftable below — and widening only some of them produces the worst failure mode this
            // project has: a grab that is accepted and then never stepped, i.e. a prop that is held and
            // motionless with nothing in any log. One predicate, one answer.
            //
            // Only a DYNAMIC prop is eligible. A StaticWall carries no dynamic state, so admitting one
            // would hand StepHeldObject an object with nothing to integrate.
            return IsDynamicPhysicalProp() &&
                   PFE.Core.DebugOverlays.Settings?.TelekinesisGrabAnything == true;
        }

        public bool CanBeThrown()
        {
            MapObjectPhysicalCapability capability = GetResolvedPhysicalCapability();
            return capability == MapObjectPhysicalCapability.DynamicThrowable ||
                   capability == MapObjectPhysicalCapability.DynamicTelekinetic;
        }

        /// <summary>
        /// AS3 <c>Obj.shelf</c> (<c>Box.as:22</c>) — can a unit stand on this prop, and can another prop
        /// stack on it. The rule lives in <see cref="MapObjectShelfRule"/>; this is the instance-side
        /// entry point, which reads the definition when there is one and the instance's own attributes
        /// when there is not — the same two-step shape as
        /// <see cref="GetApproximatePixelSize"/> and <see cref="GetAs3Massa"/>.
        ///
        /// <para><b>Note the word collision with tiles.</b> <c>TileData.shelf</c> is the one-way catwalk
        /// tile and has nothing to do with this. A crate is not a tile, so no tile query can answer this
        /// question — which is exactly why a unit standing on a crate needed a second, prop-aware ground
        /// test rather than a wider tile query.</para>
        /// </summary>
        public bool IsShelf()
        {
            EnsureStructuredData();

            if (definition != null)
            {
                return definition.IsShelf();
            }

            int wall = TryParseIntAttribute("wall", TryParseIntAttribute("stena", 0));

            return MapObjectShelfRule.IsShelf(
                MapObjectDataUtility.HasAttribute(attributes, "shelf"),
                wall > 0,
                MapObjectShelfRule.ResolveWidthPixels(
                    GetAttribute("scx", string.Empty),
                    TryParseIntAttribute("size", 1)));
        }

        /// <summary>
        /// AS3 <c>Obj.levitPoss</c> — whether this <i>instance</i> is liftable right now. This is the
        /// term the grab gate reads (<c>UnitPlayer.as:1790</c>), and it is <b>not</b>
        /// <see cref="SupportsTelekinesis"/>: that answers a per-definition question from the authored
        /// <c>wall</c>/<c>tip</c>/<c>massa</c> data, while this is a per-instance flag AS3 mutates at
        /// runtime. Both have to hold — a prop can be the right kind and still be unliftable because
        /// something cleared the flag this frame.
        /// </summary>
        public bool IsLiftable()
        {
            EnsureStructuredData();
            return SupportsTelekinesis() && runtimeState.dynamicState.levitPoss;
        }

        /// <summary>
        /// AS3 <c>Obj.stay</c> — at rest. Maintained by <c>RoomObjectPhysicsLayer</c> from contact and
        /// speed (<c>Box.as</c> sets it on landing, clears it while moving; <c>UnitPlayer.as:1831</c>
        /// clears it on grab).
        ///
        /// <para><b>Nothing in the grab path reads this, deliberately.</b> AS3's <c>actTele</c>
        /// (<c>UnitPlayer.as:1769-1790</c>) does not test <c>stay</c>; only the HUD hint at
        /// <c>GUI.as:1326</c> does. The port used to gate grabs on it, which refused props caught
        /// mid-flight — a grab AS3 allows. See the field's own remarks for the full citation.</para>
        ///
        /// <para>Kept as the accessor for real oracle state so the HUD hint can read it; if you are
        /// about to use it as a precondition, read the field's remarks first.</para>
        /// </summary>
        public bool IsAtRest()
        {
            EnsureStructuredData();
            return runtimeState.dynamicState.stay;
        }

        /// <summary>
        /// Is this prop already held by telekinesis right now.
        ///
        /// <para><b>Why this is a separate question from <see cref="IsAtRest"/>.</b> The grab
        /// candidate search used to exclude held props by testing <c>stay</c>, because
        /// <c>RoomObjectPhysicsLayer.StepHeldObject</c> clears <c>stay</c> while an object is held.
        /// That conflated two different things, and <c>stay</c> is the wrong one to gate on: AS3's
        /// <c>actTele</c> never tests it (see <c>MapObjectDynamicStateData.stay</c>). Now that the
        /// candidate search matches the oracle and ignores <c>stay</c>, "already held" needs to be
        /// asked directly — otherwise a held prop becomes a candidate again and can be re-grabbed
        /// mid-flight.</para>
        /// </summary>
        public bool IsHeldByTelekinesis()
        {
            EnsureStructuredData();
            return runtimeState.dynamicState.isHeldByTelekinesis;
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

        /// <summary>
        /// AS3's <c>Obj.massa</c>, in AS3's own scale — 50x smaller than
        /// <see cref="GetResolvedMass"/>. See <see cref="MapObjectDefinition.GetAs3Massa"/> for why the
        /// two are separate. The telekinesis gate reads this one.
        /// </summary>
        public float GetAs3Massa()
        {
            EnsureStructuredData();

            if (definition != null)
            {
                return definition.GetAs3Massa();
            }

            float explicitMass = TryParseFloatAttribute("massa", 0f);
            if (explicitMass > 0f)
            {
                // Box.as:283 — @massa / 50, and massaMult is overwritten rather than applied.
                return explicitMass / 50f;
            }

            Vector2 sizePixels = GetApproximatePixelSize();
            float derivedMass = Mathf.Max(1f, (sizePixels.x / WorldConstants.TILE_SIZE) * (sizePixels.y / WorldConstants.TILE_SIZE) * 50f);
            return derivedMass * Mathf.Max(0.01f, TryParseFloatAttribute("massaMult", 1f)) / 50f;
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
                targetPosition.y,
                sizePixels.x,
                sizePixels.y);
        }

        /// <summary>
        /// Bounds built from a size the caller has already resolved, at this object's <b>live</b>
        /// position.
        ///
        /// <para><b>Why this overload exists.</b> <see cref="GetApproximatePixelSize"/> re-parses the
        /// <c>scx</c>/<c>scy</c> attributes on every call — <c>GetAttribute</c> plus a culture-aware
        /// <c>float.TryParse</c> — and the prop queries ask for bounds once per <i>candidate</i>. A
        /// caller walking a candidate list can therefore resolve the size once per prop and build the
        /// rect per candidate from here. The position is still read fresh, so the only thing reused is
        /// the part that cannot move: a prop's size does not change as it falls.</para>
        ///
        /// <para>Kept separate from <see cref="GetApproximateBounds(Vector2)"/> rather than replacing it
        /// with an optional parameter, so that every existing call site keeps resolving its own size and
        /// no caller can silently pass a stale one.</para>
        /// </summary>
        public Rect GetApproximateBoundsWithSize(Vector2 sizePixels)
        {
            return new Rect(
                position.x - sizePixels.x * 0.5f,
                position.y,
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
