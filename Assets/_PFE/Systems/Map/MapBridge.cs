using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VContainer;
// Required, not decorative: `ISubscriber<T>.Subscribe(Action<T>)` is an extension method declared in
// this namespace, so without the using the only visible overload is the one taking an
// IMessageHandler<T> and the handler method group will not convert.
using MessagePipe;
using PFE.Core;
using PFE.Sim.Campaign;
using PFE.Systems.Campaign;
using PFE.Systems.Map;
using PFE.Systems.Map.Generation;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Streaming;

/// <summary>
/// Land-transition choreography — the scene half of a land change. Owns the coroutine that disables
/// the player, clears the room, drives the build, re-activates the streaming manager and re-enables
/// the player; owns the <c>LandBuildRequestMessage</c> subscription; owns player spawn/bootstrap.
///
/// <para><b>What no longer lives here (Wave 2, thread G).</b> The bridge used to be three jobs in
/// one component: this choreography, the authoritative entry decisions (which collection, which
/// entry cell, what run difficulty, which checkpoint resumes, the <c>visited</c>/<c>firstroom</c>
/// campaign writes), and the presentation handovers (render assets, damage authority, particles,
/// the land <c>fon</c> plane). The decisions moved to <see cref="LandSessionService"/> — plain C#,
/// constructible in a fixture, no scene knowledge — and the handovers to
/// <see cref="RoomPresentationCoordinator"/> with the render lookups grouped in
/// <see cref="RoomRenderAssetBundle"/>. What remains here is exactly the part that needs frames:
/// yields, scene-object creation and enable/disable ordering.</para>
/// </summary>
public class MapBridge : MonoBehaviour
{
    [SerializeField] private RoomVisualController _visualController;

    /// <summary>
    /// The particle adapter this bridge was constructed with, kept only to hand to
    /// <c>RoomTransitionManager</c> — the prob registry needs it for <c>inter.shine()</c> on the doors
    /// <c>doorsOnOff</c> hides. Not serialized: it is a DI singleton, not scene data.
    /// </summary>
    private PFE.Systems.Particles.Adapters.RoomParticleEmitter _particleEmitter;

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
    private PFE.Core.PfeDebugSettings _debugSettings;

    /// <summary>
    /// The authoritative half of a land transition — entry plan, checkpoint activation, the
    /// campaign writes that end an entry. Injected; the manual-caller path
    /// (<c>MapRendererBootstrapper</c>) may pass null, in which case a bare instance is used —
    /// the service is stateless, so the default-constructed copy is functionally identical.
    /// </summary>
    private LandSessionService _session;

    /// <summary>
    /// The container's campaign singleton (thread E) — entry plans, debug-room override reports and
    /// land builds all read it. Null when the bridge was built without a container (the manual
    /// bootstrapper path); every site falls back to <c>CampaignManager.Current</c>, which tracks the
    /// same instance for exactly those containerless paths.
    /// </summary>
    private PFE.Systems.Campaign.CampaignManager _campaign;

    /// <summary>
    /// The presentation half of this bridge — visual-service handovers, room show/clear, the land
    /// <c>fon</c> plane. Built in <see cref="Construct"/> from the injected render bundle; never null.
    /// </summary>
    private RoomPresentationCoordinator _presentation;

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
    /// and this component — the one that owns the build choreography — consumes it.</para>
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
    public void Construct(GameManager gameManager, RoomGenerator roomGenerator, RoomRenderAssetBundle renderAssets, PFE.Core.PfeDebugSettings debugSettings, PFE.Core.SimClock simClock, PFE.Core.SimLoop simLoop, PFE.Systems.Physics.IPhysicsWorldService physicsWorldService, PFE.Systems.Combat.DamageSystem damageSystem, LandSessionService landSession = null, PFE.Data.ContentRegistry registry = null, PFE.Systems.Particles.ParticleWorld particleWorld = null, PFE.Systems.Particles.Rendering.ParticleSpriteCatalog particleCatalog = null, PFE.Systems.Particles.Adapters.TileQueryParticleWater particleTileWater = null, PFE.Systems.Particles.Adapters.RoomParticleEmitter particleEmitter = null, VContainer.IObjectResolver objectResolver = null, MessagePipe.ISubscriber<PFE.Core.Messages.LandBuildRequestMessage> landBuildSubscriber = null, PFE.Systems.Campaign.CampaignManager campaign = null)
    {
        _gameManager = gameManager;
        _roomGenerator = roomGenerator;
        _debugSettings = debugSettings;
        _simClock = simClock;
        _simLoop = simLoop;
        _physicsWorldService = physicsWorldService;
        _session = landSession ?? new LandSessionService();

        // Thread E: the container's campaign singleton. Every read site falls back to
        // CampaignManager.Current so a fixture-built bridge (no container) keeps working — the static
        // is set for exactly the containerless paths this file's other lookups document.
        _campaign = campaign;

        // Kept, not subscribed, until Start — see the field remark. A null here is legal and is what a
        // test rig or a scene without the broker registration gets: the bridge then rebuilds only on
        // OnGotoLand, which is the pre-L0 behaviour, rather than throwing at injection time.
        _landBuildSubscriber = landBuildSubscriber;

        // The presentation half: render lookups, visual-service handovers, room show/clear, fon plane.
        // The handover chain (damage authority, effect resolver, particles, combat services, sim
        // attach) lives on the coordinator — see its docs for why each layer exists.
        _presentation = new RoomPresentationCoordinator(_visualController, renderAssets, debugSettings);
        _particleEmitter = _presentation.ConfigureVisualServices(
            damageSystem, registry, particleWorld, particleCatalog,
            particleTileWater, particleEmitter, objectResolver, simClock, simLoop);

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

        // L0's consumer half: CampaignManager owns campaign *state* and publishes a build request; this
        // component performs the build. Wired here rather than in Construct so the boot-time request is
        // not consumed twice — see _landBuildSubscription.
        if (_landBuildSubscriber != null)
        {
            _landBuildSubscription = _landBuildSubscriber.Subscribe(HandleLandBuildRequest);

            // ...and `OnGotoLand` is deliberately NOT wired when that seam exists.
            //
            // `gotoland` reaches the builder through BOTH wires, and the two start two independent
            // `PerformLandTransition` coroutines for one trigger. `AreaTriggerSystem.GotoLand` raises
            // `OnGotoLand` first (`AreaTriggerSystem.cs:860`) and only then calls the campaign host
            // (`:861`), which publishes the build request — so both fire, in the same frame, for the
            // same land.
            //
            // That is not harmless duplication, and it is why `nio` looked empty when entered through
            // the plant exit corridor but fine from the F2 Lands tab (which only publishes a request).
            // Measured in Editor.log for one corridor entry: two `Starting land transition to 'nio'`,
            // then the first coroutine's `RoomVisualController.Initialize` is followed by the second's,
            // which calls `ClearVisuals()` and destroys the objects and units the first had just built.
            // The first then activates the room and the second's `RoomStreamingManager.ActivateRoom`
            // **early-returns** — "Don't reactivate the same room" (`RoomStreamingManager.cs:94`) — so
            // the room is left activated by one coroutine and populated by the other. Only one
            // `Activating` line appears in the log for two transitions; that missing line is the bug.
            //
            // The oracle has one path: `Script.as:443-456` calls `Game.gotoLand` once. Keeping
            // `OnGotoLand` as a *fallback only* — for a scene or test rig with no campaign broker —
            // preserves the pre-L0 behaviour where it is still the only route, and removes the
            // double-build where it is not.
        }
        else
        {
            _visualController.OnGotoLand += HandleGotoLand;
        }

        // Wait for Game Manager to finish generating the world
        StartCoroutine(WaitForInitialization());
    }

    private IEnumerator WaitForInitialization()
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

                // Setup RoomTransitionManager BEFORE the visuals initialize — the order is load-bearing.
                //
                // RoomObjectVisualManager composes the `allact` dispatcher while it presents the room's
                // objects: its admission test asks the dispatcher whether a script has a handler, and
                // ApplyPresenterVisual then hands that same dispatcher to every DoorPropPresenter. But
                // DoorPropPresenter.CreateSceneDispatcher registers `comein` — the Z door's action — off
                // RoomTransitionManager.Instance, so if the manager does not exist yet the dispatcher is
                // built WITHOUT `comein` and cached for the room's whole lifetime. Every Z door then
                // dispatches `comein`, gets Unhandled, and falls back to ToggleOpen() — which a Z door
                // cannot do (IsDoorBox is false), so the door does nothing at all.
                //
                // Creating this block AFTER the room Initialize is exactly that bug; the boot
                // log read "[ObjectAction] No IRoomLayerTransition is available, so `comein` is not
                // registered". PerformLandTransition already had the manager alive before its own
                // Initialize, which is why only the boot land lost its Z doors.
                var transitionManager = RoomTransitionManager.Instance;
                if (transitionManager == null)
                {
                    var rtmGo = new GameObject("RoomTransitionManager");
                    transitionManager = rtmGo.AddComponent<RoomTransitionManager>();
                }
                transitionManager.SetLandMap(landMap);
                transitionManager.SetVisualController(_visualController);

                // Initialize the visual controller with the logical room data
                using (PFE.Core.Profiling.PfeProfiler.Region("room.init.visuals", "boot: MEASURED 4007ms (2026-09-25) — still the dominant phase: tiles.createAll 2518 + backdrop.createVisuals 1453"))
                {
                    _presentation.InitializeRoom(currentRoom);
                }

                // Spawn player at valid position
                using (PFE.Core.Profiling.PfeProfiler.Region("map.spawnPlayer", "boot: believed trivial"))
                {
                    SpawnPlayer(currentRoom);
                }

                // The streaming manager is only read when a transition actually runs, so it can be built
                // after the visuals and then handed to the transition manager.
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
                    _session.ReportOverrideLand(_campaign ?? CampaignManager.Current, template.sourceCollectionId);
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

        // The cell's z = 1 interior. Routed through the builder so this path cannot drift from the three
        // build paths again: it used `AddSpecialRoom("background", …, roomPosition)` here too, which
        // registered the room at the SAME cell in a different dictionary, leaving the Z door with nothing
        // to toggle to. See WorldBuilder.BuildBackgroundLayer.
        WorldBuilder overrideWorldBuilder = _gameManager?.GetWorldBuilder();
        if (overrideWorldBuilder != null)
        {
            overrideWorldBuilder.BuildBackgroundLayer(room, template, roomPosition, _debugSettings);
        }
        else if (!string.IsNullOrEmpty(template.backgroundRoomId))
        {
            Debug.LogWarning(
                $"[MapBridge] No WorldBuilder is available, so the background layer of override room " +
                $"'{template.id}' was not registered — its Z door will have nowhere to go.");
        }

        landMap.AddRoom(room, roomPosition);
        landMap.SwitchRoom(roomPosition);
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log($"[MapBridge] Debug room override loaded template '{template.id}' ({template.type}).");
        _session.ReportOverrideLand(_campaign ?? CampaignManager.Current, template.sourceCollectionId);
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

        // The player's own per-frame blocks — the swim impulse, mana, radiation (the geiger), spells,
        // auto-pickup — belong on the sim clock, and deliberately NOT behind SimTickMotor: that flag
        // chooses which clock drives the *motor*, while these are AS3 frame counts and need the sim
        // tick to mean anything (the same argument the action interactor below makes).
        //
        // Nothing else calls this for the player. The player is a prefab instance, so
        // PlayerRigBuilder's attach — the only other caller — never runs for it, and every one of
        // those blocks silently fell back to Unity's FixedUpdate: 50 Hz against the oracle's 30, with
        // catch-up bursts of up to 16 steps in one frame when a frame ran long. That is what made the
        // geiger click in clumps (up to 25/s instead of 15) and bill radiation 1.67x too fast.
        // Gated on the sim actually dispatching as well as on the clock existing: with
        // SimTickEnabled off, SimLoop accumulates nothing, so a block registered here would simply
        // never run — and a silent never-run reads exactly like "radiation does nothing".
        bool simTicks = _debugSettings == null || _debugSettings.SimTickEnabled;
        var player = playerObj.GetComponent<PFE.Entities.Player.PlayerController>();
        if (player != null && simTicks && _simClock != null && _simLoop != null)
        {
            player.AttachSimulation(_simClock, _simLoop);
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
    /// Set up the camera driver for the player.
    /// </summary>
    private void SetupCameraFollow()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogWarning("[MapBridge] No main camera found!");
            return;
        }

        // Typed, not reflected. The old lookup was
        //   System.Type.GetType("PFE.Core.CameraFollow, Assembly-CSharp")
        // but CameraFollow.cs compiles into PFE.View (PFE.View.asmdef covers Core/), so that lookup
        // ALWAYS returned null and this method silently took the "position the camera once" fallback.
        // The follow camera worked only because SampleScene.unity happens to serialize a CameraFollow
        // with an Inspector-wired target — exactly the scene-only dependency a runtime toggle cannot
        // rely on. MapBridge and CameraFollow are in the same assembly, so a direct reference is
        // legal, checked by the compiler, and cannot fail silently at runtime.
        var follow = mainCamera.GetComponent<CameraFollow>();
        if (follow == null)
        {
            follow = mainCamera.gameObject.AddComponent<CameraFollow>();
        }

        follow.SetTarget(_playerTransform);
        follow.smoothSpeed = 5f;
        follow.offset = new Vector3(0, 0, -10);

        // CameraFollow.StaticRoomCamera is process-wide, so a driver added here joins whatever mode
        // the F2 tab selected without anyone having to push the flag into this component.
        if (_debugSettings?.LogMapBridgeLifecycle == true)
            Debug.Log("[MapBridge] Camera follow setup complete" +
                      (CameraFollow.StaticRoomCamera ? " (room-static mode)" : string.Empty));
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

        // The fon plane is parented to the camera, not to anything this bridge owns — see
        // RoomPresentationCoordinator.Dispose for why it is destroyed by hand.
        _presentation?.Dispose();
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

    /// <summary>
    /// Whether a land transition coroutine is currently running. See
    /// <see cref="PerformLandTransition"/> for why one transition at a time is a correctness
    /// requirement and not just tidiness.
    /// </summary>
    private bool _transitionInFlight;

    /// <summary>
    /// Entry point for every land change. Refuses to start a second transition while one is running.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a guard rather than trusting the wiring.</b> Two concurrent transitions do not
    /// interleave safely: each calls <c>landMap.Clear()</c> and then builds, so the later one wipes the
    /// map the earlier one just populated, and each re-initializes the room visuals — whose first act on
    /// an initialized controller is <c>ClearVisuals()</c>. The survivor's
    /// <c>RoomStreamingManager.ActivateRoom</c> then early-returns because the room is already the
    /// active one, so the room ends up activated by one coroutine and drawn by the other.</para>
    ///
    /// <para><b>Observed symptom.</b> Entering <c>nio</c> through the plant exit corridor produced two
    /// transitions and a room with no props, no enemies and no boss; the same land entered from the F2
    /// Lands tab produced one transition and a fully populated room. The wiring that caused the pair is
    /// fixed at its source in <see cref="Start"/>; this guard is the second line of defence, because a
    /// script can also fire two <c>gotoland</c> actions in consecutive ticks.</para>
    ///
    /// <para><b>Ignore, not queue.</b> A queued transition would still build the world twice. The
    /// ignored request names the land the player is already being taken to; the honest response is to
    /// refuse it and say so.</para>
    /// </remarks>
    public IEnumerator PerformLandTransition(string targetLand, Vector3Int? targetPosition = null)
    {
        if (_transitionInFlight)
        {
            Debug.LogWarning(
                $"[MapBridge] Ignoring a second land transition to '{targetLand}': one is already in " +
                "flight. Two overlapping transitions each clear the land map and re-initialise the room " +
                "visuals, which leaves the room activated but unpopulated.");
            yield break;
        }

        _transitionInFlight = true;

        // A nested coroutine that ends with `yield break` simply completes, so the flag is cleared on
        // every exit path without wrapping the body in try/finally (which cannot span a `yield` in an
        // iterator with a `catch`, and would need the whole body re-indented here).
        yield return RunLandTransition(targetLand, targetPosition);

        _transitionInFlight = false;
    }

    private IEnumerator RunLandTransition(string targetLand, Vector3Int? targetPosition)
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
        _presentation.ClearRoom();

        // Get LandMap and clear
        var landMap = _gameManager.GetLandMap();
        if (landMap != null)
        {
            landMap.Clear();
        }

        // Resolve collection name
        string collectionName = _session.ResolveCollectionName(targetLand);
        var templates = _gameManager.GetTemplatesForCollection(collectionName);
        if (templates == null || templates.Count == 0)
        {
            Debug.LogError($"[MapBridge] No templates found for collection '{collectionName}' (targetLand: '{targetLand}')!");
            if (playerController != null) playerController.enabled = true;
            yield break;
        }

        // The whole entry decision — AS3's `rnd`/`visited` reads, the run difficulty, the checkpoint
        // resume, the entry cell — in the oracle's order, with no scene knowledge. See
        // LandSessionService.BuildEntryPlan for the rules and their AS3 citations.
        CampaignManager campaign = _campaign ?? CampaignManager.Current;
        LandEntryPlan plan = _session.BuildEntryPlan(
            targetLand,
            templates,
            campaign,
            playerLevel: playerController != null && playerController.CharacterStats != null
                ? playerController.CharacterStats.Level
                : 1,
            targetPosition: targetPosition);

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

        if (plan.Land != null && plan.Land.isProcedural)
        {
            LandRuntimeState state = campaign.LandStates.Get(targetLand);
            bool mbaseVisited = campaign.GetTrigger("mbase_visited").IsGreaterThanZero();

            Debug.Log($"[MapBridge] Procedural land '{targetLand}' (conf {plan.Land.configId}, " +
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
                _gameManager.GetTemplatesForCollection(_session.ResolveCollectionName("prob")),
                plan.Land.probRooms,
                campaign);

            // The builder READS `ProbContext` *while it builds*: PlaceCellObjects → BuildAndRegisterProbRoom
            // builds and registers each exit / trial room through it. This assignment used to live after the
            // branch, so the builder always saw null, every prob room (the exit room included) was placed
            // but never built, and the exit box refused with "prob 'exit_plant' has no built room". It must
            // be set here, before BuildLand.
            worldBuilder.ProbContext = probContext;

            // A regenerated procedural land's objects are FRESH, and its checkpoint record must not
            // outlive the layout that named it:
            //   · AS3 never persists a random land's objects — `Land.saveObjs` returns immediately
            //     (`Land.as:1193-1196`), so no `<obj code>` for a checkpoint in a procedural land is ever
            //     written back, and `CheckPoint`'s constructor has nothing to restore `act` from
            //     (`:105-124`).
            //   · The port's `CurrentCheckpoint` is campaign state and survives a rebuild. Every conf 0
            //     land enters at (0,0) at every stage, and a procedural checkpoint's `code` is empty — so
            //     the identity falls back to the room cell (`CampaignManager.MatchesCheckpoint`) and the
            //     NEW entry checkpoint reads as "already active". `ActivateCheckpoint` then refuses, the
            //     return is never armed, and no save is written: the entry checkpoint would be a prop you
            //     cannot press, which is exactly the state this change exists to fix.
            campaign?.ClearCheckpointForRebuild();

            built = worldBuilder.BuildLand(plan.Land, templates, state.landStage, state.visited,
                forceRegenerate: false, mbaseVisited: mbaseVisited, runDifficulty: plan.RunDifficulty);
        }
        else
        {
            if (plan.Land == null)
            {
                // Name the condition that failed. The old message fired identically for a null campaign, a
                // null catalogue and a missing definition, so a lookup that had gone stale (see
                // CampaignCatalog.Initialize) was indistinguishable from a land that was never authored —
                // and the two need different fixes. AllLands is the raw serialized list; GetLand is the
                // index over it, so printing the list size says which of the two is empty.
                Debug.LogWarning(
                    $"[MapBridge] No LandDefinition for '{targetLand}'; building it as an authored land. " +
                    $"[campaign={(campaign != null ? "ok" : "NULL")}, " +
                    $"catalog={(plan.Catalog != null ? "ok" : "NULL")}, " +
                    $"catalog.AllLands={(plan.Catalog != null ? plan.Catalog.AllLands.Count : -1)}]");

                // Refuse to build a land that is guaranteed to be a trap. An authored build places one room
                // per distinct fixedPosition, and an AS3 procedural room carries no x/y/z, so the importer
                // gives every procedural template fixedPosition (0,0,0) — the bounds then collapse to a
                // single cell and the player spawns with no neighbour to walk to and no way out (observed:
                // entering `random_plant` through this branch dropped the player into
                // `rooms_plant/арсенал_0_0_0`).
                //
                // Only the unresolved case is refused. A land that *did* resolve and is simply not
                // procedural is authored by definition, and its build is not ours to second-guess.
                if (_session.TemplatesCollapseToASingleCell(templates))
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

            // An authored land is still a LAND ENTRY, so it owes the oracle's `Land` constructor fields —
            // the difficulty, and the two land-scoped loot fields. It cannot go through `BuildLand`,
            // because that method chooses its own entry cell from the land's authored `locx/locy`, while
            // this branch must keep `plan.StartPos` (the checkpoint / named-room / entrance resolution in
            // LandSessionService.BuildEntryPlan). So the context is built here and handed in as the third
            // argument.
            //
            // Without this the authored path passed `LocationBuildContext.None`, so EVERY authored land's
            // rooms were generated at `landDifLevel 0` / biome -1 — the port's room difficulty was the
            // sentinel rather than the land's, which is the largest single divergence on this axis
            // (authored lands are the bulk of the campaign).
            built = worldBuilder.BuildSpecificWorld(templates, plan.StartPos,
                worldBuilder.EnterLand(plan.Land, plan.RunDifficulty));
        }

        // The transition manager reads the context only when a door is used, so it is handed the same
        // object after the build — the builder registered the prob rooms into it during the build, so the
        // door and the room it opens into can never disagree. (The builder's own copy was set before
        // BuildLand, not here.)
        RoomTransitionManager.Instance?.SetParticleEmitter(_particleEmitter);
        RoomTransitionManager.Instance?.SetProbContext(probContext);

        if (!built)
        {
            Debug.LogError($"[MapBridge] Failed to build world for collection '{collectionName}'!");
            if (playerController != null) playerController.enabled = true;
            yield break;
        }

        // The land's `fon` backdrop — AS3 `World.ativateLand` (`World.as:1058-1070`), which is the
        // only caller of `Grafon.drawFon` and the exact counterpart of this point in the transition.
        //
        // Land-scoped, not room-scoped, so it is refreshed here rather than by the room presentation:
        // a door inside the same land goes through RoomTransitionManager and never comes back here,
        // and every room in a land shares one backdrop. `plan.Land` is null only for a land that did
        // not resolve from the catalogue, and that takes AS3's own fallback (`fonDefault`) rather than
        // skipping the layer — the authored-fallback build below is still a land.
        _presentation.RefreshLandFon(plan.Land);

        yield return null;

        var currentRoom = landMap.currentRoom;
        if (currentRoom != null)
        {
            _presentation.InitializeRoom(currentRoom);
            SpawnPlayer(currentRoom);

            // AS3 `Location.createCheck(true)` ends with `this.cp.activate(true)`
            // (Location.as:2105-2108) — the entry checkpoint is activated by the BUILD, not by the
            // player. That is what makes the "safe prop" at the start of a level the land's current
            // checkpoint, and — when the placement carries `teleOn` — what arms its return-to-camp
            // button (`CheckPoint.as:234-240`).
            //
            // Procedural only: `createCheck` is called from `buildRandomLand` alone, and an authored
            // room's checkpoints come from its own `<obj>` children, which the oracle never activates.
            //
            // AFTER SpawnPlayer, which is the port's ordering rather than the oracle's. AS3 activates
            // inside `new Land(...)`, when the player has no position yet; here the save that ends the
            // activation (`CheckPoint.as:247`) is a real file write, so it must record the arrival
            // rather than the room the player just left.
            if (plan.LandIsRandom)
            {
                _session.ActivateEntryCheckpoint(campaign, targetLand, currentRoom);
            }

            if (rtm != null)
            {
                rtm.SetLandMap(landMap);
                rtm.SetVisualController(_visualController);
            }

            if (rsm != null)
            {
                // Drop the streaming manager's record of whatever was active BEFORE activating the new
                // room. `ActivateRoom` early-returns when the room it is handed is already the active
                // one ("Don't reactivate the same room", `RoomStreamingManager.cs:94`), and that early
                // return skips `ActivateRoomInternal` — the only writer of `room.isActive` and the only
                // raiser of `OnRoomActivated`.
                //
                // That matters here because `InitializeRoom` above may have destroyed and rebuilt the room's
                // visuals (its first act on an already-initialized controller is `ClearVisuals()`).
                // Without this reset, a build that re-used a room instance the manager still considered
                // active would finish with fresh visuals and no activation pass — a room that draws its
                // tiles but none of its props or enemies. Resetting first makes activation unconditional
                // and idempotent, which is the property the transition actually needs.
                rsm.ResetStreaming();
                rsm.ActivateRoom(currentRoom);
            }
        }

        if (playerController != null)
        {
            playerController.enabled = true;
        }

        // The two campaign writes that end every land entry — `visited` and the `firstroom` trigger —
        // deliberately AFTER the build: both were READ to compute this entry's plan, and writing them
        // first would corrupt the next entry. See LandSessionService.CompleteLandEntry for the AS3
        // citations and the recorded re-visit divergence.
        _session.CompleteLandEntry(campaign, targetLand, plan.FirstVisit);

        Debug.Log($"[MapBridge] Successfully transitioned to land '{targetLand}' ({collectionName}), current room: {currentRoom?.id}");
    }
}
