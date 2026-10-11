using System;
using System.Collections.Generic;
using UnityEngine;
using VContainer;
// Required, not decorative: `ISubscriber<T>.Subscribe(Action<T>)` is an extension method declared in
// this namespace, so without the using the only visible overload is the one taking an
// IMessageHandler<T> and the handler method group will not convert.
using MessagePipe;
using PFE.Core;
using PFE.Data.Definitions.Campaign;
using PFE.Sim.Campaign;
using PFE.Systems.Campaign;
using PFE.Systems.Map;
using PFE.Systems.Map.Generation;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Streaming;

public class MapBridge : MonoBehaviour
{
    [SerializeField] private RoomVisualController _visualController;
    [SerializeField] private TileAssetDatabase _tileDatabase;

    public RoomVisualController VisualController => _visualController;

    /// <summary>
    /// The live <see cref="LandMap"/> — every room in the land the player is in right now.
    ///
    /// <para><b>Why this is a property on the bridge rather than a lookup at the caller.</b> The map is
    /// owned by <c>GameManager</c> and handed to the bridge by injection; the debug overlay is
    /// bootstrapped at runtime and has no resolver, so without this it can only reach the single
    /// <c>RoomStreamingManager.CurrentRoom</c> and has no way to ask "how many rooms does this land have,
    /// and is every one of them reachable". That question is the whole point of the Land State tab.</para>
    ///
    /// <para>Null before the first land transition — the overlay must treat null as "no land built yet"
    /// rather than as an error.</para>
    /// </summary>
    public LandMap CurrentLandMap => _gameManager != null ? _gameManager.GetLandMap() : null;

    [Header("Player Spawning")]
    [Tooltip("Tag of the player GameObject to find and spawn")]
    [SerializeField] private string _playerTag = "Player";

    [Tooltip("Offset from bottom-left of walkable tile when spawning player")]
    [SerializeField] private Vector3 _spawnOffset = new Vector3(0.5f, 0.5f, 0);

    [Header("Debug Room Override")]
    [SerializeField] private bool _useRoomOverride = false;
    [SerializeField] private RoomTemplate _overrideRoomTemplate;
    [SerializeField] private string _overrideTemplateId = "";
    [SerializeField] private string _overrideRoomType = "";

    private GameManager _gameManager;
    private RoomGenerator _roomGenerator;
    private Transform _playerTransform;
    private TileTextureLookup _tileTextureLookup;
    private MaterialRenderDatabase _materialRenderDatabase;
    private TileMaskLookup _tileMaskLookup;
    private RoomBackgroundLookup _roomBackgroundLookup;
    private PFE.Core.PfeDebugSettings _debugSettings;
    private PFE.Core.SimClock _simClock;
    private PFE.Core.SimLoop _simLoop;
    private PFE.Systems.Physics.IPhysicsWorldService _physicsWorldService;

    /// <summary>
    /// The injected <see cref="PFE.Core.Messages.LandBuildRequestMessage"/> source. Stored here and
    /// subscribed in <see cref="Start"/>, not in <see cref="Construct"/> — see
    /// <see cref="_landBuildSubscription"/> for why the timing matters.
    /// </summary>
    private MessagePipe.ISubscriber<PFE.Core.Messages.LandBuildRequestMessage> _landBuildSubscriber;

    /// <summary>
    /// The subscription to <see cref="PFE.Core.Messages.LandBuildRequestMessage"/> — the one seam that
    /// actually rebuilds the world.
    ///
    /// <para>Before this, the only thing that rebuilt was <c>RoomVisualController.OnGotoLand</c>, raised
    /// by a map object's <c>gotoland</c> script action. <c>CampaignManager</c> logged a transition and
    /// moved two reactive properties nobody read (<c>docs/LandGameplayLoop/03_GAP_LEDGER.md</c> §1, D1),
    /// so the camp's wall map could not reach the builder at all. Now the campaign publishes a request
    /// and this component — the one that owns <c>WorldBuilder</c> and the room visuals — consumes it.</para>
    ///
    /// <para><b>Why it is created in <c>Start</c> and not in <c>Construct</c>.</b> VContainer runs
    /// <c>CampaignManager.Initialize</c> during the lifetime scope's <c>Awake</c>, and that call ends in
    /// <c>TransitionToLand(startingLand)</c> — i.e. it publishes a build request for the land the game
    /// boots into. But that land is already built by <c>GameManager.BuildWorldAsync</c>
    /// (<c>GameManager.cs:93/176</c>), and <c>WaitForInitialization</c> only *renders* the result. A
    /// subscription live during <c>Awake</c> would therefore consume that boot request and rebuild the
    /// starting land a second time, in the same frame, right after the first build. Subscribing in
    /// <c>Start</c> — the same place <c>OnGotoLand</c> is wired — skips exactly that one request and
    /// takes every later one. Collapsing the boot build into this seam is the real fix and is a separate,
    /// owner-only change (it moves who builds the first room).</para>
    ///
    /// <para>Held as <see cref="IDisposable"/> rather than a raw handler reference because MessagePipe's
    /// <c>Subscribe</c> returns the unsubscribe token; disposing it in <see cref="OnDestroy"/> is what
    /// stops a destroyed bridge being called by a broker that outlives the scene.</para>
    /// </summary>
    private IDisposable _landBuildSubscription;

    /// <summary>
    /// The LowLevelPhysics2D world service, for the debug overlay.
    ///
    /// <para><b>Why this is exposed at all.</b> The chain mirror is Box2D geometry inside a
    /// <c>PhysicsWorld</c> — there is no GameObject and no <c>Collider2D</c> to find by type, so
    /// <c>ColliderDebugOverlay</c>'s usual <c>FindObjectsByType</c> route cannot reach it. This
    /// property is the seam that lets <c>col on physics</c> draw the geometry a projectile actually
    /// sweeps against. Null until <see cref="Construct"/> has run.</para>
    /// </summary>
    public PFE.Systems.Physics.IPhysicsWorldService PhysicsWorldService => _physicsWorldService;

    // Inject GameManager via VContainer
    //
    // Note: C# default values do NOT make a parameter optional here. VContainer's
    // ResolveOrParameter never consults ParameterInfo.HasDefaultValue — it checks only explicitly
    // supplied inject parameters, then calls Resolve(type) and throws if that fails. So every
    // parameter below must be registered, and the `= null` on the last few is DECORATIVE, not an
    // escape hatch: the container still throws when a type is missing. They are safe only because
    // GameLifetimeScope registers each of them unconditionally. This comment previously claimed the
    // defaults were "deliberately omitted" while the line beneath it carried three — the code is the
    // truth, and it is why a scene cannot opt out of a registration by relying on the default.
    [Inject]
    public void Construct(GameManager gameManager, RoomGenerator roomGenerator, TileTextureLookup tileTextureLookup, MaterialRenderDatabase materialRenderDatabase, TileMaskLookup tileMaskLookup, RoomBackgroundLookup roomBackgroundLookup, PFE.Core.PfeDebugSettings debugSettings, PFE.Core.SimClock simClock, PFE.Core.SimLoop simLoop, PFE.Systems.Physics.IPhysicsWorldService physicsWorldService, PFE.Systems.Combat.DamageSystem damageSystem, PFE.Data.ContentRegistry registry = null, PFE.Systems.Particles.ParticleWorld particleWorld = null, PFE.Systems.Particles.Rendering.ParticleSpriteCatalog particleCatalog = null, PFE.Systems.Particles.Adapters.TileQueryParticleWater particleTileWater = null, PFE.Systems.Particles.Adapters.RoomParticleEmitter particleEmitter = null, VContainer.IObjectResolver objectResolver = null, MessagePipe.ISubscriber<PFE.Core.Messages.LandBuildRequestMessage> landBuildSubscriber = null)
    {
        _gameManager = gameManager;
        _roomGenerator = roomGenerator;
        _tileTextureLookup = tileTextureLookup;
        _materialRenderDatabase = materialRenderDatabase;
        _tileMaskLookup = tileMaskLookup;
        _roomBackgroundLookup = roomBackgroundLookup;
        _debugSettings = debugSettings;
        _simClock = simClock;
        _simLoop = simLoop;
        _physicsWorldService = physicsWorldService;

        // Kept, not subscribed, until Start — see the field remark. A null here is legal and is what a
        // test rig or a scene without the broker registration gets: the bridge then rebuilds only on
        // OnGotoLand, which is the pre-L0 behaviour, rather than throwing at injection time.
        _landBuildSubscriber = landBuildSubscriber;

        // MapBridge is the injected one; RoomVisualController is a scene component with no [Inject] of
        // its own, and RoomUnitSpawner is a plain class built with `new`. So the damage authority has
        // to be carried down that chain by hand, or every spawned enemy resolves prop impacts as raw
        // HP with no armour. Handed over here rather than in Start() because injection runs before
        // Start, and the controller's setter is safe before a room exists.
        if (_visualController != null)
        {
            _visualController.SetDamageSystem(damageSystem);
        }

        // ...and hand the SAME chain the effect-definition resolver, so a spawned NPC's `effects` array
        // can resolve an id into a real effect rather than refusing every one. Built from the registry
        // the container already owns (rather than a second registration of the resolver itself), which
        // keeps one source of truth — the same shape PlayerWeaponLoadout uses for its ammo resolver. A
        // null registry (an older scene, or a test rig) leaves the resolver null and every unit's
        // effect set stays resolver-less, which refuses ids rather than materialising phantom effects.
        if (_visualController != null && registry != null)
        {
            _visualController.SetEffectResolver(
                new PFE.Systems.Effects.ContentRegistryEffectDefinitionResolver(registry));
        }

        // ...and the particle population, down the SAME chain, because AS3 scopes particles to the room
        // (`Emitter.as:362` adds each Part to the Location). This is what gives the room its own particle
        // view: parented to the room root, so a part is drawn in room-local pixels and is occluded by the
        // room's own overlays. Handing it to a unit instead would make muzzle smoke follow the player.
        //
        // Both are optional so a scene without the particle registrations still boots: the controller
        // simply never creates the view, and the world (if present) still ticks its budget and
        // unknown-id report. Silence here is not a silent failure — ParticleWorld.UnknownIds and
        // ParticleSpriteCatalog.MissingIds are the readbacks.
        if (_visualController != null && particleWorld != null && particleCatalog != null)
        {
            _visualController.SetParticleWorld(particleWorld, particleCatalog);
        }

        // ...and the two room-scoped adapters, so the controller can point them at whichever room is
        // current. They are handed over as a pair because they share one dependency — the room's tile
        // query — and are pushed it from one place, which is what keeps the water answer and the
        // particle coordinate conversion from disagreeing about which room they are in.
        //
        // Optional in the same way the world is: a scene without the registrations boots, the emitters
        // refuse (RoomParticleEmitter.RefusedWithoutRoom counts it) and the water answer stays dry,
        // rather than anything converting against a room that does not exist.
        if (_visualController != null && particleTileWater != null && particleEmitter != null)
        {
            _visualController.SetParticleAdapters(particleTileWater, particleEmitter);
        }

        // ...and the container itself, down the SAME chain, so a spawned enemy can resolve the weapon
        // and audio services that [Inject] cannot reach it: a unit is built with AddComponent, which
        // VContainer never observes, so the spawner is the only handover point. One field on the
        // spawner rather than four constructor parameters keeps this seam from growing a tail of
        // individually-forgettable services; see RoomUnitSpawner's field remarks.
        //
        // IObjectResolver is registered by the container itself, so this parameter is never the
        // decorative-null case the header comment above warns about. Null is still legal and is what a
        // test rig gets: the spawner then builds units with no weapon and no voice, which is the
        // pre-change behaviour and is reported per unit rather than silently.
        if (_visualController != null && objectResolver != null)
        {
            _visualController.SetCombatServices(objectResolver);
        }

        // P1: hand the fixed-step simulation down the SAME chain, so a motor-less NPC steps on
        // SimLoop's capped clock instead of Unity's uncapped FixedUpdate.
        //
        // Why this is not merely a rate fix: Unity's FixedUpdate catches up, so a frame that costs
        // more than one fixed step runs the step again inside that same frame. Measured in the camp,
        // 24-26 unit steps landed inside one frame against 5 in a healthy one, which is how the
        // units' own cost became the thing making the frame long — the 2 FPS report. SimLoop clamps
        // its accumulator to SimDt * MaxCatchupTicks and drops the rest, so the same load cannot
        // spiral. Gated on the flag here, read once at wiring time, exactly as SimTickMotor is read
        // once below for the player's motor.
        // `UnitMotor` needs the clock too, and that is not obvious from either flag's name: turning
        // the motor on without the handover would give a spawned unit a motor running on Unity's
        // uncapped FixedUpdate at the legacy 2x scaling — a re-home that silently changes the units'
        // speed and can still spiral. So the handover is gated on EITHER flag.
        bool simDriveUnits = _debugSettings != null
            && (_debugSettings.SimTickUnits || _debugSettings.UnitMotor);

        if (simDriveUnits && _visualController != null)
        {
            _visualController.AttachSimulation(_simClock, _simLoop);

            if (_simClock != null)
            {
                // Deliberately not gated on LogMapBridgeLifecycle: this confirms an opt-in behaviour
                // change, and silence would be ambiguous with "the flag did nothing".
                Debug.Log(
                    "[MapBridge] Units are sim-driven at " + _simClock.TicksPerSecond +
                    " Hz (unit tick fix ON; units step on SimLoop, capped at " +
                    PFE.Core.SimClock.MaxCatchupTicks + " ticks/frame). Driven by: " +
                    (_debugSettings.SimTickUnits ? "SimTickUnits " : "") +
                    (_debugSettings.UnitMotor ? "UnitMotor" : ""));
            }
            else
            {
                // The flag is on and there is nothing to attach it to. Say so rather than leaving the
                // units silently on the clock the fix exists to get them off — the spawner's own
                // handover returns early on a null clock and would report nothing.
                Debug.LogWarning(
                    "[MapBridge] SimTickUnits/UnitMotor is ON but no SimClock was injected, so units " +
                    "stay on Unity's FixedUpdate. The unit tick fix is NOT active.");
            }
        }

        if (_useRoomOverride)
            gameManager.SetSkipWorldBuild(true);
    }

    private void Start()
    {
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log("[MapBridge] Start() called - waiting for world generation...");

        if (_visualController == null)
        {
            Debug.LogError("[MapBridge] RoomVisualController is not assigned! Assign it in the inspector.");
            return;
        }

        if (_tileDatabase == null)
        {
            Debug.LogError("[MapBridge] TileAssetDatabase is not assigned! Assign it in the inspector.");
            return;
        }

        _visualController.OnGotoLand += HandleGotoLand;

        // L0's consumer half: CampaignManager owns campaign *state* and publishes a build request; this
        // component owns WorldBuilder and performs the build. Wired here rather than in Construct so the
        // boot-time request is not consumed twice — see _landBuildSubscription.
        if (_landBuildSubscriber != null)
        {
            _landBuildSubscription = _landBuildSubscriber.Subscribe(HandleLandBuildRequest);
        }

        // Wait for Game Manager to finish generating the world
        StartCoroutine(WaitForInitialization());
    }

    private System.Collections.IEnumerator WaitForInitialization()
    {
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log("[MapBridge] Coroutine started - checking GameManager...");

        // Wait for GameManager to exist and be injected
        int waitFrames = 0;
        while (_gameManager == null && waitFrames < 300)
        {
            waitFrames++;
            yield return null;
        }

        if (_gameManager == null)
        {
            Debug.LogError("[MapBridge] GameManager was never injected! Check VContainer setup.");
            yield break;
        }

        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log($"[MapBridge] GameManager found after {waitFrames} frames, waiting for initialization...");

        // Wait until GameManager says it's ready (poll every frame)
        float timeout = 30f;
        float timer = 0f;
        int checkCount = 0;

        while (timer < timeout)
        {
            try
            {
                bool initialized = _gameManager.IsInitialized();
                checkCount++;

                if (initialized)
                {
                    if (_debugSettings?.LogMapBridgeLifecycle == true)
                        Debug.Log($"[MapBridge] GameManager is initialized! (checked {checkCount} times)");
                    break;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MapBridge] Exception calling IsInitialized(): {ex.Message}");
                yield break;
            }

            timer += Time.deltaTime;
            yield return null;
        }

        if (timer >= timeout)
        {
            bool finalCheck = _gameManager.IsInitialized();
            Debug.LogError($"[MapBridge] TIMEOUT after {checkCount} checks! GameManager.IsInitialized()={finalCheck}, Instance={_gameManager.GetHashCode()}");
            yield break;
        }

        // Continue with initialization
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log("[MapBridge] Proceeding with room rendering...");

        // Get the current room from logic
        var landMap = _gameManager.GetLandMap();

        if (landMap != null)
        {
            if (_debugSettings?.LogMapBridgeLifecycle == true)
                Debug.Log($"[MapBridge] LandMap has {landMap.GetRoomCount()} rooms");
            if (_useRoomOverride)
            {
                TryApplyRoomOverride(landMap);
                PFE.Core.Profiling.PfeProfiler.Mark("map.roomOverride.applied");
            }

            var currentRoom = landMap.currentRoom;

            if (currentRoom != null)
            {
                if (_debugSettings?.LogMapBridgeLifecycle == true)
                {
                    Debug.Log($"[MapBridge] Current room: {currentRoom.id} at {currentRoom.landPosition}, type: {currentRoom.roomType}");
                    Debug.Log($"[MapBridge] Room dimensions: {currentRoom.width}x{currentRoom.height}, tiles array: {(currentRoom.tiles == null ? "NULL" : "initialized")}");
                }

                if (currentRoom.tiles != null)
                {
                    int nonAirTiles = 0;
                    for (int x = 0; x < currentRoom.width; x++)
                    {
                        for (int y = 0; y < currentRoom.height; y++)
                        {
                            if (currentRoom.tiles[x, y] != null && currentRoom.tiles[x, y].physicsType != TilePhysicsType.Air)
                                nonAirTiles++;
                        }
                    }
                    if (_debugSettings?.LogMapBridgeLifecycle == true)
                        Debug.Log($"[MapBridge] Room has {nonAirTiles} non-air tiles to render");
                }

                _visualController.ConfigureCompositorAssets(_tileTextureLookup, _materialRenderDatabase, _roomBackgroundLookup, _tileMaskLookup);
                if (_debugSettings?.LogMapBridgeLifecycle == true)
                    Debug.Log($"[MapBridge] Using TileAssetDatabase '{_tileDatabase.name}' with {_tileDatabase.GetCount()} overlay sprites.");

                // Initialize the visual controller with the logical room data
                using (PFE.Core.Profiling.PfeProfiler.Region("room.init.visuals", "boot: MEASURED 4007ms (2026-09-25) — still the dominant phase: tiles.createAll 2518 + backdrop.createVisuals 1453"))
                {
                    _visualController.Initialize(currentRoom, _tileDatabase);
                }

                // Spawn player at valid position
                using (PFE.Core.Profiling.PfeProfiler.Region("map.spawnPlayer", "boot: believed trivial"))
                {
                    SpawnPlayer(currentRoom);
                }

                // Setup RoomTransitionManager & RoomStreamingManager
                var transitionManager = RoomTransitionManager.Instance;
                if (transitionManager == null)
                {
                    var rtmGo = new GameObject("RoomTransitionManager");
                    transitionManager = rtmGo.AddComponent<RoomTransitionManager>();
                }
                transitionManager.SetLandMap(landMap);
                transitionManager.SetVisualController(_visualController, _tileDatabase);

                var streamingManager = FindFirstObjectByType<RoomStreamingManager>();
                if (streamingManager == null)
                {
                    var rsmGo = new GameObject("RoomStreamingManager");
                    streamingManager = rsmGo.AddComponent<RoomStreamingManager>();
                }
                transitionManager.SetStreamingManager(streamingManager);

                // Wire LowLevelPhysics2D room geometry lifecycle (Stage B).
                if (_physicsWorldService != null)
                {
                    _physicsWorldService.SubscribeToRoomEvents(streamingManager);

                    // A PhysicsWorld only advances when Simulate() is called, and this service is
                    // the only thing that calls it. Without this registration the world would be
                    // built and never stepped — a failure with no visible symptom, since Stage B
                    // has no consumers yet. SimLoop.Register de-duplicates, so re-running this
                    // path cannot double-register.
                    _simLoop?.Register(_physicsWorldService);
                }
            }
            else
            {
                Debug.LogError("[MapBridge] Logic generated no current room! Check WorldBuilder.SwitchRoom()");
            }
        }
        else
        {
            Debug.LogError("[MapBridge] LandMap is null!");
        }
    }

    private bool TryApplyRoomOverride(LandMap landMap)
    {
        if (landMap == null || _roomGenerator == null)
        {
            Debug.LogWarning("[MapBridge] Cannot apply room override: LandMap or RoomGenerator is missing.");
            return false;
        }

        RoomTemplate template = ResolveOverrideTemplate();
        if (template == null)
        {
            Debug.LogWarning("[MapBridge] Room override enabled, but no matching template was found.");
            return false;
        }

        // If the override template belongs to a multi-room collection (e.g. "rooms_rbl"), build the full
        // collection so adjacent rooms (e.g. room_2_0) and door connections exist, starting the player in
        // the override room.
        var allLoaded = _gameManager?.GetLoadedRoomTemplates();
        var worldBuilder = _gameManager?.GetWorldBuilder();
        if (worldBuilder != null && allLoaded != null && !string.IsNullOrWhiteSpace(template.sourceCollectionId))
        {
            var collectionTemplates = new List<RoomTemplate>();
            for (int i = 0; i < allLoaded.Count; i++)
            {
                var t = allLoaded[i];
                if (t != null && string.Equals(t.sourceCollectionId, template.sourceCollectionId, System.StringComparison.OrdinalIgnoreCase))
                {
                    collectionTemplates.Add(t);
                }
            }

            if (collectionTemplates.Count > 1)
            {
                bool built = worldBuilder.BuildSpecificWorld(collectionTemplates, template.fixedPosition);
                if (built && landMap.currentRoom != null)
                {
                    if (_debugSettings?.LogMapBridgeLifecycle == true)
                    {
                        Debug.Log($"[MapBridge] Debug room override loaded full collection '{template.sourceCollectionId}' ({collectionTemplates.Count} rooms) starting at {landMap.currentRoom.id}.");
                    }
                    return true;
                }
            }
        }

        Vector3Int roomPosition = template.fixedPosition;
        landMap.Initialize(roomPosition, roomPosition + Vector3Int.one);

        RoomInstance room = _roomGenerator.GenerateRoom(template, roomPosition);
        if (room == null)
        {
            Debug.LogError($"[MapBridge] Failed to generate override room from template '{template.id}'.");
            return false;
        }

        FinalizeOverrideRoom(room, template);

        if (!string.IsNullOrEmpty(template.backgroundRoomId))
        {
            RoomTemplate backgroundTemplate = FindTemplateById(template.backgroundRoomId, template);
            if (backgroundTemplate != null)
            {
                RoomInstance backgroundRoom = _roomGenerator.GenerateRoom(backgroundTemplate, roomPosition);
                FinalizeOverrideRoom(backgroundRoom, backgroundTemplate);
                backgroundRoom.roomType = "back";
                landMap.AddSpecialRoom("background", backgroundRoom, roomPosition);
                room.backgroundRoom = backgroundRoom;
                room.hasBackgroundLayer = true;
            }
            else
            {
                Debug.LogWarning($"[MapBridge] Background room template '{template.backgroundRoomId}' was not found for override room '{template.id}'.");
            }
        }

        landMap.AddRoom(room, roomPosition);
        landMap.SwitchRoom(roomPosition);
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log($"[MapBridge] Debug room override loaded template '{template.id}' ({template.type}).");
        return true;
    }

    private void FinalizeOverrideRoom(RoomInstance room, RoomTemplate template)
    {
        if (room == null || template == null)
        {
            return;
        }

        if (template.specificMapOnly)
        {
            if (_debugSettings?.LogMapIntegrityDiagnostics == true)
            {
                Debug.Log(
                    $"[MapBridge] Finalizing override room '{template.GetContentId()}' as specific/authored. " +
                    "Skipping random border and door carving.");
            }

            RoomSetup.FinalizeSpecificRoom(room, template, _debugSettings);
            return;
        }

        if (_debugSettings?.LogMapIntegrityDiagnostics == true)
        {
            Debug.Log($"[MapBridge] Finalizing override room '{template.GetContentId()}' as random/procedural.");
        }

        // Debug override inspects a single room, so the neighbour pass never runs and there is no drawn
        // door. Open the whole candidate mask so the tool still shows the room's doorways.
        RoomSetup.ActivateAllCandidateDoors(room);
        RoomSetup.FinalizeRoom(room, template, debugSettings: _debugSettings);
    }

    private RoomTemplate ResolveOverrideTemplate()
    {
        if (_overrideRoomTemplate != null)
        {
            return _overrideRoomTemplate;
        }

        if (!string.IsNullOrWhiteSpace(_overrideTemplateId))
        {
            RoomTemplate template = FindTemplateById(_overrideTemplateId);
            if (template != null)
            {
                return template;
            }
        }

        if (!string.IsNullOrWhiteSpace(_overrideRoomType))
        {
            var templates = _gameManager.GetLoadedRoomTemplates();
            for (int i = 0; i < templates.Count; i++)
            {
                RoomTemplate template = templates[i];
                if (template != null && string.Equals(template.type, _overrideRoomType, System.StringComparison.Ordinal))
                {
                    return template;
                }
            }
        }

        return null;
    }

    private RoomTemplate FindTemplateById(string templateId, RoomTemplate context = null)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return null;
        }

        var templates = _gameManager.GetLoadedRoomTemplates();
        for (int i = 0; i < templates.Count; i++)
        {
            RoomTemplate template = templates[i];
            if (template != null && string.Equals(template.GetContentId(), templateId, System.StringComparison.Ordinal))
            {
                return template;
            }
        }

        if (context != null && !string.IsNullOrWhiteSpace(context.sourceCollectionId))
        {
            for (int i = 0; i < templates.Count; i++)
            {
                RoomTemplate template = templates[i];
                if (template != null &&
                    string.Equals(template.id, templateId, System.StringComparison.Ordinal) &&
                    string.Equals(template.sourceCollectionId, context.sourceCollectionId, System.StringComparison.Ordinal))
                {
                    return template;
                }
            }
        }

        for (int i = 0; i < templates.Count; i++)
        {
            RoomTemplate template = templates[i];
            if (template != null && string.Equals(template.id, templateId, System.StringComparison.Ordinal))
            {
                return template;
            }
        }

        return null;
    }

    /// <summary>
    /// Spawn player at a valid walkable position in the room.
    /// </summary>
    private void SpawnPlayer(RoomInstance room)
    {
        GameObject playerObj = GameObject.FindWithTag(_playerTag);
        if (playerObj == null)
        {
            var playerController = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
            if (playerController != null)
                playerObj = playerController.gameObject;
        }

        if (playerObj == null)
        {
            Debug.LogWarning("[MapBridge] No player found in scene!");
            return;
        }

        _playerTransform = playerObj.transform;

        // NEW: Use RoomSetup for spawn position (pixel-accurate, finds air-above-ground)
        Vector3 spawnPosition = RoomSetup.FindPlayerSpawnUnity(room);
        _playerTransform.position = spawnPosition;
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log($"[MapBridge] Player spawned at {spawnPosition}");

        // NEW: Connect TilePhysicsController to current room
        var tilePhysics = playerObj.GetComponent<PFE.Systems.Physics.TilePhysicsController>();
        if (tilePhysics != null)
        {
            tilePhysics.SetRoom(room);
            tilePhysics.SetPixelPosition(
                spawnPosition.x * 100f,
                spawnPosition.y * 100f);
            if (_debugSettings?.LogMapBridgeLifecycle == true)
                Debug.Log("[MapBridge] TilePhysicsController connected to room");

            // P1: hand the motor to the fixed-step simulation. Opt-in — with the flag off the motor
            // stays on the legacy FixedUpdate path, byte-identical to the pre-P1 behaviour.
            if (_debugSettings != null && _debugSettings.SimTickMotor)
            {
                // AttachSimulation owns the null guard (it warns and stays legacy), so the clock is
                // only dereferenced here once we know it arrived.
                tilePhysics.AttachSimulation(_simClock, _simLoop);
                if (_simClock != null)
                {
                    // Deliberately not gated on LogMapBridgeLifecycle: this confirms an opt-in
                    // behaviour change, and silence would be ambiguous with "the flag did nothing".
                    Debug.Log(
                        "[MapBridge] Motor is sim-driven at " + _simClock.TicksPerSecond +
                        " Hz (tick fix ON; step scale " + _simClock.StepScale.ToString("0.###") + ")");
                }
            }
        }

        // Hand the telekinesis controller the LandMap — not the room.
        //
        // The component is created by PlayerController.Awake via AddComponent, and VContainer's
        // ExistingComponentProvider injects only the component it was handed (PlayerController), so
        // PlayerTelekinesisController's own [Inject] Construct never runs. Without this call its
        // _landMap stays null, CurrentRoom resolves to null, and TryGrab returns false at its first
        // guard — the prop physics, the mass gate and the guards all work and none of them are ever
        // reached. Nothing logs, because a null room is a legal state for a fixture.
        //
        // The LandMap rather than the room, because this method runs once per land load: a door
        // inside the same land goes through RoomTransitionManager -> landMap.SetCurrentRoom and
        // never comes back here, so a room captured now would be the room the player left.
        var telekinesis = playerObj.GetComponent<PFE.Entities.Player.PlayerTelekinesisController>();
        if (telekinesis != null)
        {
            telekinesis.SetLandMap(_gameManager.GetLandMap());
            if (_debugSettings?.LogMapBridgeLifecycle == true)
                Debug.Log("[MapBridge] Telekinesis connected to LandMap");
        }
        else
        {
            // PlayerController.Awake always creates this component, and this coroutine runs after
            // Awake, so null here means the player object is not what we think it is. Warn rather
            // than skip: a silent skip is exactly how the missing wiring above stayed invisible.
            Debug.LogWarning(
                "[MapBridge] No PlayerTelekinesisController on the player — telekinesis will not " +
                "be able to resolve a room and nothing will be grabbable.");
        }

        // Hand the player's hold-to-act timers to the simulation as well — but deliberately NOT behind
        // SimTickMotor. That flag chooses which clock drives the *motor*; a hold duration is an AS3
        // frame count, so it needs the sim tick to mean anything at all. Left on the fallback driver it
        // would count at Unity's fixed rate (50 Hz) and every hold would finish 1.67x early, which for
        // the Z doors' time='10' is 0.2 s instead of 0.33 s. Attaching here is what makes the hold
        // independent of whether the motor has been switched over yet.
        var actionInteractor = playerObj.GetComponent<PFE.Entities.Player.PlayerActionInteractor>();
        if (actionInteractor != null)
        {
            actionInteractor.AttachSimulation(_simClock, _simLoop);
        }

        SetupCameraFollow();
    }
    /// <summary>
    /// Find a valid walkable tile to spawn the player.
    /// Prioritizes floor/platform tiles near the center of the room.
    /// </summary>
    private Vector3 FindValidSpawnPosition(RoomInstance room)
    {
        if (room?.tiles == null)
        {
            Debug.LogWarning("[MapBridge] Room tiles not available, spawning at origin");
            return Vector3.zero;
        }

        // First try: Find ground/platform tiles near center
        int centerX = room.width / 2;
        int centerY = room.height / 2;

        // Search outward from center for walkable tile
        for (int radius = 0; radius < Mathf.Max(room.width, room.height); radius++)
        {
            for (int x = Mathf.Max(0, centerX - radius); x <= Mathf.Min(room.width - 1, centerX + radius); x++)
            {
                for (int y = Mathf.Max(0, centerY - radius); y <= Mathf.Min(room.height - 1, centerY + radius); y++)
                {
                    var tile = room.tiles[x, y];
                    if (tile != null && IsWalkable(tile))
                    {
                        // Found walkable tile, spawn above it
                        Vector3 worldPos = new Vector3(
                            room.landPosition.x + x + _spawnOffset.x,
                            room.landPosition.y + y + _spawnOffset.y,
                            0
                        );
                        if (_debugSettings?.LogMapBridgeLifecycle == true)
                            Debug.Log($"[MapBridge] Found spawn position at tile ({x}, {y}), world pos: {worldPos}");
                        return worldPos;
                    }
                }
            }
        }

        // Fallback: Center of room
        Vector3 fallbackPos = new Vector3(
            room.landPosition.x + centerX,
            room.landPosition.y + centerY,
            0
        );
        Debug.LogWarning($"[MapBridge] No walkable tile found, spawning at room center: {fallbackPos}");
        return fallbackPos;
    }

    /// <summary>
    /// Check if a tile is walkable (floor, platform, or stair).
    /// </summary>
    private bool IsWalkable(TileData tile)
    {
        return tile.physicsType == TilePhysicsType.Wall ||
               tile.physicsType == TilePhysicsType.Platform ||
               tile.physicsType == TilePhysicsType.Stair;
    }

    /// <summary>
    /// Setup camera to follow the player.
    /// </summary>
    private void SetupCameraFollow()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogWarning("[MapBridge] No main camera found!");
            return;
        }

        // Add CameraFollow component via reflection to avoid compile issues
        var followType = System.Type.GetType("PFE.Core.CameraFollow, Assembly-CSharp");
        if (followType == null)
        {
            // Fallback: just position camera at player
            if (_debugSettings?.LogMapBridgeLifecycle == true)
                Debug.Log("[MapBridge] CameraFollow not yet compiled, positioning camera directly");
            mainCamera.transform.position = new Vector3(
                _playerTransform.position.x,
                _playerTransform.position.y,
                -10
            );
            return;
        }

        var follow = mainCamera.GetComponent(followType);
        if (follow == null)
        {
            follow = mainCamera.gameObject.AddComponent(followType);
        }

        // Set target via reflection
        var setTargetMethod = followType.GetMethod("SetTarget");
        setTargetMethod?.Invoke(follow, new object[] { _playerTransform });

        // Set properties
        followType.GetField("smoothSpeed")?.SetValue(follow, 5f);
        followType.GetField("offset")?.SetValue(follow, new Vector3(0, 0, -10));

        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log("[MapBridge] Camera follow setup complete");
    }

    private void OnDestroy()
    {
        // Before the visual controller is torn down, and unconditionally: the broker is a container-owned
        // singleton that can outlive this scene, so an undisposed subscription would call into a
        // destroyed MonoBehaviour on the next publish.
        _landBuildSubscription?.Dispose();
        _landBuildSubscription = null;

        if (_visualController != null)
        {
            _visualController.OnGotoLand -= HandleGotoLand;
        }
    }

    private void HandleGotoLand(string targetLand)
    {
        Debug.Log($"[MapBridge] HandleGotoLand triggered for: '{targetLand}'");
        StartCoroutine(PerformLandTransition(targetLand));
    }

    /// <summary>
    /// The <c>LandBuildRequestMessage</c> consumer — the campaign asking for a world.
    ///
    /// <para>Both entry points converge on <see cref="PerformLandTransition"/>: this one and the
    /// <c>gotoland</c> script action. That is deliberate, so a travel through the camp's wall map and a
    /// travel through a door script cannot drift apart.</para>
    ///
    /// <para><b>What <c>ForceRegenerate</c> does today: nothing.</b> <c>PerformLandTransition</c> rebuilds
    /// the layout on every call — the procedural branch re-plans from scratch and the authored branch
    /// re-reads the templates — so <c>crea = true</c> and <c>crea = false</c> already agree. The flag is
    /// carried and logged rather than silently dropped, so the day a layout cache is added it is already
    /// on the wire. <b>The message's flag is not threaded through yet:</b> both entry points pass
    /// <c>forceRegenerate: false</c> to <c>WorldBuilder.BuildLand</c>, which is honest while a rebuild is
    /// unconditional and would be a lie the moment one is not.</para>
    /// </summary>
    private void HandleLandBuildRequest(PFE.Core.Messages.LandBuildRequestMessage msg)
    {
        if (string.IsNullOrWhiteSpace(msg.LandId))
        {
            Debug.LogWarning("[MapBridge] Ignoring a land build request with an empty land id.");
            return;
        }

        Vector3Int? entry = EntryCoordinates.Parse(msg.EntryCoordinates);

        if (entry == null && !string.IsNullOrWhiteSpace(msg.EntryCoordinates))
        {
            // Reported, not swallowed: a coordinate the parser did not understand falls back to the
            // land's own entry cell, and silently landing somewhere other than the script asked for is
            // indistinguishable from the script having no coordinate at all.
            Debug.LogWarning(
                $"[MapBridge] Entry coordinates '{msg.EntryCoordinates}' are not a numeric \"x:y\"; " +
                "using the land's own entry cell instead.");
        }

        Debug.Log($"[MapBridge] Land build request: land='{msg.LandId}', " +
                  $"entry='{msg.EntryCoordinates ?? "-"}'{(entry.HasValue ? $" -> {entry.Value}" : string.Empty)}, " +
                  $"forceRegenerate={msg.ForceRegenerate}.");

        StartCoroutine(PerformLandTransition(msg.LandId, entry));
    }

    public System.Collections.IEnumerator PerformLandTransition(string targetLand, Vector3Int? targetPosition = null)
    {
        Debug.Log($"[MapBridge] Starting land transition to '{targetLand}'...");

        // Disable player controller during transition
        GameObject playerObj = GameObject.FindWithTag(_playerTag);
        if (playerObj == null)
        {
            var pc = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
            if (pc != null) playerObj = pc.gameObject;
        }

        var playerController = playerObj != null ? playerObj.GetComponent<PFE.Entities.Player.PlayerController>() : null;
        if (playerController != null)
        {
            playerController.enabled = false;
        }

        // Reset room streaming & ongoing room transitions
        var rtm = RoomTransitionManager.Instance;
        if (rtm != null)
        {
            rtm.StopAllCoroutines();
        }

        var rsm = FindFirstObjectByType<RoomStreamingManager>();
        if (rsm != null)
        {
            rsm.ResetStreaming();
        }

        // Yield a frame so physics callbacks finish cleanly
        yield return null;

        // Clear visuals
        _visualController.ClearVisuals();

        // The tileset is about to change, so the shared sprite cache is both stale and otherwise
        // unbounded across lands. Safe here: ClearVisuals already destroyed the old tiles (and a
        // yield passed before it), so no live renderer still references these sprites.
        _visualController.ReleaseBakedTileSprites();

        // Get LandMap and clear
        var landMap = _gameManager.GetLandMap();
        if (landMap != null)
        {
            landMap.Clear();
        }

        // Resolve collection name
        string collectionName = ResolveCollectionName(targetLand);
        var templates = _gameManager.GetTemplatesForCollection(collectionName);
        if (templates == null || templates.Count == 0)
        {
            Debug.LogError($"[MapBridge] No templates found for collection '{collectionName}' (targetLand: '{targetLand}')!");
            if (playerController != null) playerController.enabled = true;
            yield break;
        }

        // AS3 Land ctor (Land.as:118): `rnd` picks buildRandomLand, else buildSpecifLand. This is the route
        // that was missing — every land used to take the authored path, and a procedural land's templates
        // all sit at fixedPosition (0,0,0), so the grid collapsed to one cell and nothing could transition.
        //
        // Read BEFORE the starting room is chosen, because `rnd` and `visited` are exactly what decide
        // whether this entry resumes at the land's checkpoint (CheckpointRules.IsFirstVisit /
        // ResumeRoomOnEntry). These three lines used to sit *after* `startPos`, which is why the resume
        // branch could not be expressed at the point the oracle expresses it.
        CampaignManager campaign = CampaignManager.Current;
        CampaignCatalog catalog = campaign != null ? campaign.Catalog : null;
        LandDefinition land = catalog != null ? catalog.GetLand(targetLand) : null;

        // AS3 Game.as:347-350 computes `_loc1_` — the `param1` enterLand receives — from exactly these
        // two facts. An unresolved land (`land == null`) takes the authored reading, which is the same
        // conservative choice the authored fallback below already makes for it.
        bool landIsRandom = land != null && land.isProcedural;
        bool landVisited = campaign != null && campaign.LandStates.Get(targetLand).visited;
        bool firstVisit = CheckpointRules.IsFirstVisit(landIsRandom, landVisited);

        // Determine starting room position (in Surf, entry is room_0_1 at (0, 1, 0)).
        //
        // AS3 `Land.enterLand` (Land.as:1143-1186) is a three-way choice, and this is that order:
        //   1. param2 != null              — the caller named a room ("x:y")        -> use it
        //   2. land.currentCP && !param1   — a re-visit to a land with a checkpoint -> resume there
        //   3. otherwise                   — the land's begin cell                  -> ResolveEntrancePosition
        //
        // Order is load-bearing: a caller that DID name a room still gets that room on a re-visit, so only
        // a nameless entry falls through to the checkpoint. The precedence is asserted offline in
        // CheckpointRules.ResolveEntryRoomSource rather than left to the shape of this expression.
        Vector3Int? resumeRoom = ResolveCheckpointResumeRoom(campaign, targetLand, firstVisit);

        Vector3Int startPos;
        switch (CheckpointRules.ResolveEntryRoomSource(targetPosition.HasValue, resumeRoom.HasValue))
        {
            case CheckpointRules.EntryRoomSource.Named:
                startPos = targetPosition.Value;
                break;

            case CheckpointRules.EntryRoomSource.Checkpoint:
                startPos = resumeRoom.Value;
                break;

            default:
                startPos = ResolveEntrancePosition(templates, collectionName);
                break;
        }

        var worldBuilder = _gameManager.GetWorldBuilder();

        bool built;

        // One context per land, and it must be in the builder's hands BEFORE the build — see the
        // assignment inside the procedural branch. It is cleared here first so an authored build cannot
        // keep the previous land's rooms: the builder is a shared instance whose `ProbContext` outlives a
        // build (`_gameManager.GetWorldBuilder()`), and the room-transition manager is a scene singleton.
        // Leaving either holding the previous land's rooms would let an exit box from the land you just
        // left route into the land you just entered — the doors themselves are gone, but the registry
        // is not.
        ProbDoorContext probContext = null;
        worldBuilder.ProbContext = null;

        if (land != null && land.isProcedural)
        {
            LandRuntimeState state = CampaignManager.Current.LandStates.Get(targetLand);
            bool mbaseVisited = CampaignManager.Current.GetTrigger("mbase_visited") > 0;

            Debug.Log($"[MapBridge] Procedural land '{targetLand}' (conf {land.configId}, " +
                      $"stage {state.landStage}, visited {state.visited}).");

            // The prob / boss door subsystem needs a context before the land is built: the `prob`
            // land's room templates (content) and the run's `prob_<id>` completion record (run state).
            // AS3 reads both inside newRandomProb (Land.as:809-863), and the builder cannot reach
            // either on its own — BuildLand is handed one land's collection. Without a context the
            // builder places no prob doors and reports once, so this is what makes them appear at all.
            //
            // It also carries the exit rooms: AS3 reaches those through the same registry, because
            // Location.createObj pushes the exit box's `prob` into land.probIds (:2080-2082) and
            // Land.buildProbs builds every id in it (Land.as:752-768). See WorldBuilder.PlaceCellObjects.
            probContext = ProbDoorContext.ForLand(
                _gameManager.GetTemplatesForCollection(ResolveCollectionName("prob")),
                land.probRooms,
                CampaignManager.Current);

            // The builder READS `ProbContext` *while it builds*: PlaceCellObjects → BuildAndRegisterProbRoom
            // builds and registers each exit / trial room through it. This assignment used to live after the
            // branch, so the builder always saw null, every prob room (the exit room included) was placed
            // but never built, and the exit box refused with "prob 'exit_plant' has no built room". It must
            // be set here, before BuildLand.
            worldBuilder.ProbContext = probContext;

            built = worldBuilder.BuildLand(land, templates, state.landStage, state.visited,
                forceRegenerate: false, mbaseVisited: mbaseVisited);
        }
        else
        {
            if (land == null)
            {
                // Name the condition that failed. The old message fired identically for a null campaign, a
                // null catalogue and a missing definition, so a lookup that had gone stale (see
                // CampaignCatalog.Initialize) was indistinguishable from a land that was never authored —
                // and the two need different fixes. AllLands is the raw serialized list; GetLand is the
                // index over it, so printing the list size says which of the two is empty.
                Debug.LogWarning(
                    $"[MapBridge] No LandDefinition for '{targetLand}'; building it as an authored land. " +
                    $"[campaign={(campaign != null ? "ok" : "NULL")}, " +
                    $"catalog={(catalog != null ? "ok" : "NULL")}, " +
                    $"catalog.AllLands={(catalog != null ? catalog.AllLands.Count : -1)}]");

                // Refuse to build a land that is guaranteed to be a trap. An authored build places one room
                // per distinct fixedPosition, and an AS3 procedural room carries no x/y/z, so the importer
                // gives every procedural template fixedPosition (0,0,0) — the bounds then collapse to a
                // single cell and the player spawns with no neighbour to walk to and no way out (observed:
                // entering `random_plant` through this branch dropped the player into
                // `rooms_plant/арсенал_0_0_0`).
                //
                // Only the unresolved case is refused. A land that *did* resolve and is simply not
                // procedural is authored by definition, and its build is not ours to second-guess.
                if (TemplatesCollapseToASingleCell(templates))
                {
                    Debug.LogError(
                        $"[MapBridge] Refusing to build '{targetLand}' as an authored land: all " +
                        $"{templates.Count} of its templates sit on one land coordinate, which would " +
                        "produce a single room with no exit. The land is procedural but was not resolved " +
                        "from the catalogue (see the warning above). Staying in the current land.");
                    if (playerController != null) playerController.enabled = true;
                    yield break;
                }
            }

            built = worldBuilder.BuildSpecificWorld(templates, startPos);
        }

        // The transition manager reads the context only when a door is used, so it is handed the same
        // object after the build — the builder registered the prob rooms into it during the build, so the
        // door and the room it opens into can never disagree. (The builder's own copy was set before
        // BuildLand, not here.)
        RoomTransitionManager.Instance?.SetProbContext(probContext);

        if (!built)
        {
            Debug.LogError($"[MapBridge] Failed to build world for collection '{collectionName}'!");
            if (playerController != null) playerController.enabled = true;
            yield break;
        }

        yield return null;

        var currentRoom = landMap.currentRoom;
        if (currentRoom != null)
        {
            _visualController.Initialize(currentRoom, _tileDatabase);
            SpawnPlayer(currentRoom);

            if (rtm != null)
            {
                rtm.SetLandMap(landMap);
                rtm.SetVisualController(_visualController, _tileDatabase);
            }

            if (rsm != null)
            {
                rsm.ActivateRoom(currentRoom);
            }
        }

        if (playerController != null)
        {
            playerController.enabled = true;
        }

        // AS3 Game.as:386-389 — `if(!this.curLand.rnd) { this.curLand.visited = true; }`.
        //
        // Deliberately AFTER the entry and not before. `firstVisit` above was computed from the pre-entry
        // value, and marking the land visited on the way in would make every entry read as a re-visit —
        // so every authored land would resume at its checkpoint, including the first one.
        //
        // Gated on `!landIsRandom` for two reasons that agree: it is literally the oracle's condition, and
        // `visited` is consumed by LandLayoutPlanner (it gates the `beg*` entry room), which is reached only
        // from the procedural branch. Writing it for a procedural land would change that land's layout on
        // its second entry — a behaviour change nothing here asked for. (Land.as:1145 also writes
        // `act.visited` unconditionally at enterLand's first line; the port leaves that write out, which is
        // recorded in the open-work list rather than silently reproduced.)
        if (!landIsRandom && campaign != null)
        {
            campaign.LandStates.MarkVisited(targetLand);
        }

        Debug.Log($"[MapBridge] Successfully transitioned to land '{targetLand}' ({collectionName}), current room: {currentRoom?.id}");
    }

    /// <summary>
    /// Land id -> room collection, transcribed from the <c>&lt;land&gt;</c> table in <c>GameData.as</c>.
    ///
    /// A collection is keyed by a land's <c>file</c> attribute — <c>&lt;land id='rbl' file='rooms_rbl'&gt;</c>
    /// — because that is also the AS3 field name and the key <c>Rooms.as:2794-2822</c> registers the land
    /// under, and the key <c>AS3LandDefaultsDatabase</c> merges its inherited options by. The id alone
    /// cannot be turned into it: <c>random_mbase</c> is <c>rooms_mbase</c>, not <c>rooms_random_mbase</c>,
    /// and <c>stable_pi_surf</c> is <c>rooms_pis</c>. Note also that the camp is <c>rbl</c> — there is no
    /// land called "camp" in the oracle.
    /// </summary>
    private static readonly Dictionary<string, string> LandIdToCollection =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "test", "rooms" },
            { "test2", "rooms2" },
            { "begin", "rooms_begin" },
            { "surf", "rooms_surf" },
            { "rbl", "rooms_rbl" },
            { "covert", "rooms_covert" },
            { "src", "rooms_src" },
            { "nio", "rooms_nio" },
            { "raiders", "rooms_raiders" },
            { "core", "rooms_core" },
            { "mtn", "rooms_mtn" },
            { "minst", "rooms_minst" },
            { "garages", "rooms_garages" },
            { "way", "rooms_way" },
            { "workshop", "rooms_workshop" },
            { "hql", "rooms_hql" },
            { "post", "rooms_post" },
            { "comm", "rooms_comm" },
            { "art", "rooms_art" },
            { "thunder", "rooms_thunder" },
            { "grave", "rooms_grave" },
            { "random_plant", "rooms_plant" },
            { "random_sewer", "rooms_sewer" },
            { "random_stable", "rooms_stable" },
            { "random_mane", "rooms_mane" },
            { "random_canter", "rooms_canter" },
            { "random_mbase", "rooms_mbase" },
            { "random_encl", "rooms_encl" },
            { "bunker", "rooms_mbase" },
            { "stable_pi", "rooms_pi" },
            { "stable_pi_atk", "rooms_pi" },
            { "stable_pi_surf", "rooms_pis" },
            { "prob", "rooms_prob" }
        };

    private static string ResolveCollectionName(string targetLand)
    {
        if (string.IsNullOrWhiteSpace(targetLand))
        {
            return string.Empty;
        }

        string id = targetLand.Trim();
        if (LandIdToCollection.TryGetValue(id, out string collection))
        {
            return collection;
        }

        // A caller may already hold the collection id — it is what RoomTemplate.sourceCollectionId stores,
        // so a round-tripped value arrives in that form rather than as a land id.
        if (id.StartsWith("rooms", StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        // Do not guess. The previous fallback capitalised the id, which produced "Nio", "Covert", "Src" —
        // collections that do not exist — so a land transition failed with "no templates found" and no
        // hint that the name was invented.
        Debug.LogWarning(
            $"[MapBridge] Unknown land id '{targetLand}': no <land> entry in GameData.as declares it.");
        return string.Empty;
    }

    /// <summary>
    /// AS3 <c>Land.enterLand</c>'s middle branch (<c>Land.as:1171-1176</c>) — the room a re-visit resumes
    /// in. The rule is <see cref="CheckpointRules.ResumeRoomOnEntry"/>; this is the campaign-state read.
    ///
    /// <para><b>Authored lands only, and that is a limit of the port rather than of the rule.</b> The
    /// chosen <c>startPos</c> reaches the builder only through <c>WorldBuilder.BuildSpecificWorld</c>, and
    /// <c>MapBridge</c> takes that path for an authored land. A procedural land goes to
    /// <c>BuildProceduralLand(land, plan)</c>, which takes no start room at all — so for a procedural land
    /// this value is computed and then <b>unused</b>. AS3 has no such split: <c>ativateLoc()</c> runs
    /// against whatever land was just built, procedural or not. Recorded rather than silently returning
    /// null, because the difference is invisible from the outside — the player lands in the entry room
    /// either way, which is what a broken resume looks like too.</para>
    /// </summary>
    private static Vector3Int? ResolveCheckpointResumeRoom(
        CampaignManager campaign, string targetLand, bool firstVisit)
    {
        if (campaign == null) return null;

        CampaignManager.CheckpointRecord? checkpoint = campaign.CurrentCheckpoint;
        if (checkpoint == null) return null;

        CheckpointRules.CheckpointRoom? room = CheckpointRules.ResumeRoomOnEntry(
            hasCheckpoint: true,
            checkpointLandId: checkpoint.Value.landId,
            targetLand: targetLand,
            firstVisit: firstVisit,
            roomX: checkpoint.Value.roomX,
            roomY: checkpoint.Value.roomY,
            roomZ: checkpoint.Value.roomZ);

        if (room == null) return null;

        Debug.Log($"[MapBridge] Re-entering '{targetLand}' at its checkpoint " +
                  $"({room.Value.x},{room.Value.y},{room.Value.z}).");

        return new Vector3Int(room.Value.x, room.Value.y, room.Value.z);
    }

    private static Vector3Int ResolveEntrancePosition(List<RoomTemplate> templates, string collectionName)
    {
        if (string.Equals(collectionName, "rooms_surf", StringComparison.OrdinalIgnoreCase))
        {
            // The outside bunker exit room in rooms_surf is room_0_1 at (0, 1, 0)
            return new Vector3Int(0, 1, 0);
        }

        // Look for beg0 or first spawn point
        var entry = templates.Find(t => t.type == "beg0")
                 ?? templates.Find(t => t.spawnPoints != null && t.spawnPoints.Exists(sp => sp.type == 0))
                 ?? templates.Find(t => t.fixedPosition.x >= 0);

        return entry != null ? entry.fixedPosition : Vector3Int.zero;
    }

    /// <summary>
    /// Whether an authored build of this collection would place every room on one land coordinate.
    ///
    /// <para>This is the fingerprint of a procedural land's template collection: AS3 generates those rooms
    /// rather than placing them, so the importer gives every one of them <c>fixedPosition (0,0,0)</c>
    /// (<c>WorldBuilder.HasFixedPosition</c> accepts them all, because it only tests <c>z &gt;= 0</c>).
    /// Building such a collection authored yields a one-cell land with no neighbour to transition to —
    /// the player is sealed in.</para>
    ///
    /// <para>Requires at least two templates that claim a position, so a genuinely single-room authored
    /// land is not caught. <c>z</c> is the "has a position" axis, matching <c>WorldBuilder</c>.</para>
    /// </summary>
    private static bool TemplatesCollapseToASingleCell(List<RoomTemplate> templates)
    {
        if (templates == null || templates.Count < 2) return false;

        bool seen = false;
        Vector3Int first = default;

        for (int i = 0; i < templates.Count; i++)
        {
            RoomTemplate template = templates[i];
            if (template == null || template.fixedPosition.z < 0) continue;

            if (!seen)
            {
                seen = true;
                first = template.fixedPosition;
                continue;
            }

            if (template.fixedPosition != first) return false;
        }

        return seen;
    }
}
