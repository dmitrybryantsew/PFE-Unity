using System;
using System.Text;
using UnityEngine;
using PFE.Systems.Inventory;
using PFE.Systems.Map;
using PFE.Systems.Map.Streaming;
using PFE.Systems.Map.Rendering.RoomTerrain;
using PFE.Entities.Player;
using PFE.Core;
using Profiler = PFE.Core.Profiling.PfeProfiler;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// MonoBehaviour controller for visual representation of a room.
    /// Manages tile rendering and GameObject lifecycle for a single room.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomVisualController : MonoBehaviour
    {
        private const int BackgroundTileSortingOffset = -3000;
        private static readonly Vector3 BackgroundTileLayerLocalPosition = new Vector3(0f, 0f, -1f);

        [Header("Room Data")]
        [SerializeField] private RoomInstance roomInstance = null;

        [Header("Rendering")]
        [SerializeField] private TileAssetDatabase tileAssetDatabase = null;
        [SerializeField] private TileFormDatabase tileFormDatabase = null;
        [SerializeField] private TileTextureLookup tileTextureLookup = null;
        [SerializeField] private MaterialRenderDatabase materialRenderDb = null;
        [SerializeField] private TileMaskLookup tileMaskLookup = null;

        /// <summary>
        /// The art the quarter terrain renderer needs, when <see cref="PfeDebugSettings.QuadRoomTerrain"/>
        /// is on.
        ///
        /// <para><b>Optional, and that is deliberate.</b> It is resolved through
        /// <see cref="RoomTerrainWiring.ResolveArt"/>, which falls back to the editor's asset database at
        /// the importer's own output path — so the flag can be flipped from the console and tried without
        /// a scene edit first. A Player build has no asset database, so a build that wants the quarter
        /// renderer must have this assigned; the log line names that case rather than drawing an empty
        /// room.</para>
        /// </summary>
        [SerializeField] private RoomTerrainArt roomTerrainArt = null;

        /// <summary>
        /// Baked tile sprites, shared across every room this controller renders.
        ///
        /// This used to be a local in <see cref="Initialize"/>, so each room transition threw away
        /// ~1100 baked 40x40 textures and rebuilt them from scratch — and, because
        /// <c>TileCompositor.ClearCache</c> had no caller anywhere, never released the old ones.
        /// Materials and textures do not vary per room, so the cache stays valid across a transition
        /// and is dropped only when the dependency assets change
        /// (<see cref="ConfigureCompositorAssets"/>) or the controller dies.
        /// </summary>
        private TileCompositor tileCompositor = null;
        [SerializeField] private RoomBackgroundLookup roomBackgroundLookup = null;
        [SerializeField] private RoomBackdropSettingsLookup roomBackdropSettingsLookup = null;
        [SerializeField] private PfeDebugSettings debugSettings = null;
        [Tooltip("Scales room backwall texture sampling. Values above 1 make the texture appear larger; values below 1 make it tile more densely.")]
        [SerializeField] private Vector2 backdropTextureScale = Vector2.one;
        [Tooltip("Offsets room backwall texture sampling in texture-repeat units. 1 moves by one full texture repeat.")]
        [SerializeField] private Vector2 backdropTextureOffset = Vector2.zero;
        [SerializeField] private bool flipBackdropTextureX = false;
        [SerializeField] private bool flipBackdropTextureY = true;
        [SerializeField] private Color backdropTint = Color.white;
        [SerializeField] private float backdropBrightness = 1f;
        [Tooltip("Optional sharpening applied to the generated backwall texture. Useful for matching the crisper Flash presentation.")]
        [Range(0f, 2f)]
        [SerializeField] private float backdropSharpenStrength = 0f;
        [SerializeField] private Color backgroundAssetTint = Color.white;
        [SerializeField] private float backgroundAssetBrightness = 1f;
        [Tooltip("Debug toggle to isolate whether backdrop shadow baking is causing the room to look too dark or blurry.")]
        [SerializeField] private bool disableBackdropShadowBakeForDebug = false;
        [SerializeField] private Transform tileParent = null;
        [SerializeField] private Transform backgroundParent = null;
        [SerializeField] private Transform backgroundTileParent = null;
        [SerializeField] private Transform backgroundObjectParent = null;
        [SerializeField] private Transform backgroundPhysicalObjectParent = null;
        [SerializeField] private Transform lightingParent = null;
        [SerializeField] private Transform fogOfWarParent = null;

        [Header("Editor Preview")]
        [SerializeField] private RoomTemplate previewTemplate = null;
        [SerializeField] private Vector3Int previewLandPosition = Vector3Int.zero;

        [Header("Debug")]
        [SerializeField] private bool showContourDebugOverlay = false;
        [SerializeField] private bool useLongDebugExport = false;

        [Header("State")]
        [SerializeField] private bool isInitialized = false;
        [SerializeField] private bool isVisible = true;
        [SerializeField] private bool fogOfWarDisabled = false;

        private TileVisualManager tileVisualManager;
        private TileVisualManager backgroundTileVisualManager;

        /// <summary>
        /// The quarter terrain renderer, when the debug flag put it in place of the shipped tile sprites.
        /// Null on the shipped path, which is the escape hatch (<c>terrain quad off</c>).
        /// </summary>
        /// <remarks>
        /// Held so <see cref="ClearVisuals"/> can destroy it with the room and so a caller can read the
        /// build's own numbers (<c>ShapesPlaced</c>, <c>LayerCount</c>, <c>LastBuildMilliseconds</c>)
        /// without hunting for the component. Nothing in the game reads it for drawing — the tile
        /// objects are already collision-only by the time this is set, so there is no picture to suppress
        /// and no state to keep in sync.
        /// </remarks>
        private RoomTerrainRenderer roomTerrainRenderer;

        /// <summary>
        /// The collision-only tile objects, when the quarter renderer draws this room. Null on the
        /// shipped path, where <see cref="TileVisualManager"/> owns the tiles (and their colliders).
        ///
        /// <para><b>Exactly one of the two is non-null</b> after a successful <see cref="Initialize"/>.
        /// That invariant is the whole point of the change: the room's collision no longer rides on the
        /// renderer's GameObjects, so "which renderer" and "which objects hold the colliders" are one
        /// decision instead of two that can disagree.</para>
        /// </summary>
        private TileCollisionBuilder tileCollisionBuilder;

        /// <summary>
        /// Where this room's tiles stamp the decals a hit leaves — AS3's <c>Grafon.frontBmp</c>, minus
        /// the bitmap.
        ///
        /// <para><b>Created for both terrain paths.</b> The decal is the one piece of the picture that
        /// neither renderer owns: the quarter renderer draws the terrain as a mesh and the shipped path
        /// composites one sprite per cell, and neither can accept a stamp at an arbitrary pixel. A layer
        /// of pooled sprites above <see cref="MapSortingLayers.MainTiles"/> is the answer for both, so
        /// this is built once in <c>Initialize</c> regardless of which path runs.</para>
        /// </summary>
        private TileDecalLayer tileDecalLayer;

        private RoomBackdropRenderer roomBackdropRenderer;
        private RoomObjectVisualManager roomObjectVisualManager;
        private RoomUnitSpawner roomUnitSpawner;
        private PFE.Systems.Map.Scripting.AreaTriggerSystem areaTriggerSystem;

        /// <summary>
        /// The player whose light drives the fog — <b>the player, not its transform</b>, because the
        /// light is not at the transform's origin. See
        /// <see cref="FogOcclusionMath.ResolvePlayerLightPixelOffset"/>: AS3 puts the player's light
        /// 12 px in front of the unit and 75.3 % of the way up its sprite
        /// (<c>Location.lighting():3137-3140</c>), and both terms need the unit's own state — its
        /// facing and its sprite height. Cached so the per-frame path does not search for it.
        /// </summary>
        private PlayerController visibilityRevealTargetPlayer;

        /// <summary>
        /// The port's damage authority, forwarded to every <see cref="RoomUnitSpawner"/> this
        /// controller builds.
        /// </summary>
        /// <remarks>
        /// <para>Held here rather than read from a container because this controller is a scene object
        /// with no <c>[Inject]</c> of its own — <c>MapBridge</c> is the injected one, and it owns this
        /// component as a serialized field. <c>MapBridge</c> hands it over; see
        /// <see cref="SetDamageSystem"/>.</para>
        ///
        /// <para>Stored before the first room is built in the normal order (MapBridge's
        /// <c>Construct</c> runs, then <c>Start</c> kicks off world generation), but a later handover
        /// still reaches a spawner that already exists, so the two are not order-dependent.</para>
        /// </remarks>
        private PFE.Systems.Combat.DamageSystem damageSystem;

        /// <summary>
        /// The fixed-step simulation, forwarded to every <see cref="RoomUnitSpawner"/> this controller
        /// builds so a motor-less unit steps on <c>SimLoop</c> rather than Unity's <c>FixedUpdate</c>.
        /// </summary>
        /// <remarks>
        /// Held here for the same reason <see cref="damageSystem"/> is: this controller is a scene
        /// object with no <c>[Inject]</c> of its own, and <c>MapBridge</c> — the injected one — owns it
        /// as a serialized field, so the handover has to be walked down by hand. Stored before the
        /// first room is built in the normal order, but <see cref="AttachSimulation"/> also reaches a
        /// spawner that already exists, so the two are not order-dependent.
        /// </remarks>
        private PFE.Core.SimClock simClock;
        private PFE.Core.SimLoop simLoop;

        /// <summary>
        /// The effect-definition resolver, forwarded to every <see cref="RoomUnitSpawner"/> this
        /// controller builds so a spawned unit's status effects can resolve their ids.
        /// </summary>
        /// <remarks>
        /// Held here for the same reason <see cref="damageSystem"/> is: this controller is a scene
        /// object with no <c>[Inject]</c> of its own, and <c>MapBridge</c> — the injected one — owns it
        /// as a serialized field, so the handover is walked down by hand. Stored before the first room
        /// is built in the normal order, but <see cref="SetEffectResolver"/> also reaches a spawner that
        /// already exists, so the two are not order-dependent.
        /// </remarks>
        private PFE.Systems.Effects.IEffectDefinitionResolver effectResolver;

        /// <summary>
        /// The container, forwarded to every <see cref="RoomUnitSpawner"/> this controller builds so a
        /// spawned enemy can reach the weapon and audio services <c>[Inject]</c> cannot deliver to it.
        /// </summary>
        /// <remarks>
        /// Held here for the same reason <see cref="damageSystem"/> is: this controller is a scene
        /// object with no <c>[Inject]</c> of its own, and <c>MapBridge</c> — the injected one — owns it
        /// as a serialized field, so the handover has to be walked down by hand. Stored before the
        /// first room is built in the normal order, but <see cref="SetCombatServices"/> also reaches a
        /// spawner that already exists, so the two are not order-dependent.
        /// </remarks>
        private VContainer.IObjectResolver resolver;

        /// <summary>
        /// The live particle population and its art catalogue, handed down the same chain as
        /// <see cref="effectResolver"/> (this controller is a scene object with no <c>[Inject]</c> of its
        /// own, so <c>MapBridge</c> walks them down by hand).
        /// </summary>
        private PFE.Systems.Particles.ParticleWorld particleWorld;
        private PFE.Systems.Particles.Rendering.ParticleSpriteCatalog particleCatalog;

        /// <summary>
        /// The two room-scoped particle adapters. Both are handed the current room's tile query in
        /// <see cref="Initialize"/> and cleared in <see cref="ClearVisuals"/>.
        ///
        /// <para><b>Why this controller owns the push.</b> Both adapters need the room's
        /// <c>ITileQueryService</c> — the water adapter for the <c>water=</c> lifetime rule, the emitter
        /// for the world→AS3 coordinate conversion — and this is the room-scoped object that holds
        /// <c>roomInstance</c> and already owns the particle view. Nothing else in the chain knows which
        /// room is current, and a stale query would convert every particle against the previous room's
        /// origin: correct in the room everyone tests in, wrong one land step away.</para>
        /// </summary>
        private PFE.Systems.Particles.Adapters.TileQueryParticleWater particleTileWater;
        private PFE.Systems.Particles.Adapters.RoomParticleEmitter particleEmitter;

        /// <summary>
        /// The room's particle view. Created lazily by <see cref="EnsureParticleRenderer"/>, because this
        /// controller's <c>transform</c> is the room root and it exists from scene load — the renderer can
        /// be attached long before any room is built.
        /// </summary>
        private PFE.Systems.Particles.Rendering.ParticleRenderer particleRenderer;

        /// <summary>
        /// Whether fog of war / darkness overlay is disabled (revealed).
        /// </summary>
        public bool FogOfWarDisabled
        {
            get => fogOfWarDisabled;
            set => SetFogOfWarDisabled(value);
        }

        /// <summary>
        /// The quarter terrain renderer for the current room, or null when the room is drawn the shipped
        /// way.
        ///
        /// <para>Read-only and read by nothing in the game: it exists so a debug tool can answer "did the
        /// flag actually take" without searching the hierarchy, and so the console and the hierarchy
        /// agree about which renderer is live. Exposing it cannot make the two paths depend on each
        /// other, because the shipped path never calls it.</para>
        ///
        /// <para><b>Deliberately not named <c>RoomTerrainRenderer</c>.</b> A member with a type's name
        /// hides that type inside its own class, and this file uses
        /// <c>AddComponent&lt;RoomTerrainRenderer&gt;()</c> a few lines below — the generic argument would
        /// then resolve against the property and fail with a message that names the property, not the
        /// shadowing.</para>
        /// </summary>
        public RoomTerrainRenderer QuadTerrainRenderer => roomTerrainRenderer;

        /// <summary>
        /// Enable or disable fog of war in this room.
        /// </summary>
        public void SetFogOfWarDisabled(bool disabled)
        {
            fogOfWarDisabled = disabled;
            if (roomBackdropRenderer != null)
            {
                roomBackdropRenderer.SetFogOfWarDisabled(disabled);
            }
            if (fogOfWarParent != null)
            {
                fogOfWarParent.gameObject.SetActive(!disabled);
            }
        }

        /// <summary>
        /// Permanently reveal all tiles in the current room's visibility mask.
        /// </summary>
        public void RevealFogOfWar()
        {
            if (roomBackdropRenderer != null)
            {
                roomBackdropRenderer.RevealAll();
            }
        }

        /// <summary>
        /// Get the room instance this controller renders.
        /// </summary>
        public RoomInstance RoomInstance => roomInstance;

        /// <summary>
        /// Get the area trigger system for this room.
        /// </summary>
        public PFE.Systems.Map.Scripting.AreaTriggerSystem AreaTriggerSystem => areaTriggerSystem;

        /// <summary>
        /// Give this controller the port's damage authority, which it forwards to the room's unit
        /// spawner so a prop impact on an enemy resolves through armour and vulnerabilities instead of
        /// as raw HP. See <c>RoomUnitSpawner</c>'s field remarks for why the spawner cannot get it from
        /// VContainer.
        /// </summary>
        /// <remarks>
        /// <b>Safe in either order, and it never rebuilds the spawner.</b> A spawner is stateful — it
        /// owns one GameObject per unit record — so replacing it would orphan every existing unit's
        /// GameObject and spawn a second copy of it. The handover is therefore a setter on the spawner
        /// that reaches the units already built as well as the ones still to come.
        /// </remarks>
        public void SetDamageSystem(PFE.Systems.Combat.DamageSystem system)
        {
            damageSystem = system;
            roomUnitSpawner?.SetDamageSystem(system);
        }

        /// <summary>
        /// Hand this controller the fixed-step simulation, so the units it spawns step on the sim clock
        /// instead of Unity's <c>FixedUpdate</c>.
        /// </summary>
        /// <remarks>
        /// <b>Safe in either order, and it never rebuilds the spawner.</b> Same shape as
        /// <see cref="SetDamageSystem"/> and for the same reason: the spawner is stateful — it owns one
        /// GameObject per unit record — so replacing it would orphan every existing unit and spawn a
        /// duplicate. The stored clock is used by the next <c>Initialize</c>; the setter on the spawner
        /// covers a room that was already built.
        /// </remarks>
        public void AttachSimulation(PFE.Core.SimClock clock, PFE.Core.SimLoop loop)
        {
            simClock = clock;
            simLoop = loop;
            roomUnitSpawner?.AttachSimulation(clock, loop);
        }

        /// <summary>
        /// Give this controller the resolver that turns an effect id into its template, which it
        /// forwards to the room's unit spawner so a spawned NPC can carry status effects.
        /// </summary>
        /// <remarks>
        /// <b>Safe in either order, and it never rebuilds the spawner.</b> Same shape as
        /// <see cref="SetDamageSystem"/> and for the same reason: the spawner is stateful — it owns one
        /// GameObject per unit record — so replacing it would orphan every existing unit and spawn a
        /// duplicate. The stored resolver is used by the next <c>Initialize</c>; the setter on the
        /// spawner covers a room that was already built.
        /// </remarks>
        public void SetEffectResolver(PFE.Systems.Effects.IEffectDefinitionResolver resolver)
        {
            effectResolver = resolver;
            roomUnitSpawner?.SetEffectResolver(resolver);
        }

        /// <summary>
        /// Give this controller the container, which it forwards to the room's unit spawner so a
        /// spawned enemy can resolve the weapon and audio services it cannot get from <c>[Inject]</c>.
        /// </summary>
        /// <remarks>
        /// <b>Safe in either order, and it never rebuilds the spawner.</b> Same shape as
        /// <see cref="SetDamageSystem"/> and for the same reason: the spawner is stateful — it owns one
        /// GameObject per unit record — so replacing it would orphan every existing unit and spawn a
        /// duplicate. The stored container is used by the next <c>Initialize</c>; the setter on the
        /// spawner covers a room that was already built.
        /// </remarks>
        public void SetCombatServices(VContainer.IObjectResolver objectResolver)
        {
            resolver = objectResolver;
            roomUnitSpawner?.SetCombatServices(objectResolver);
        }

        /// <summary>
        /// Hands the room the particle population and its art catalogue, and creates the view.
        ///
        /// <para><b>Why the room owns the particles.</b> AS3 adds each <c>Part</c> to the
        /// <b><c>Location</c></b> (<c>Emitter.as:362</c>), so particles are room-scoped: they are drawn
        /// above the room's foreground tiles and below its water and lighting overlays, and they die with
        /// the room. Parenting them to a unit instead gives the player muzzle smoke that follows them
        /// around the map.</para>
        ///
        /// <para><b>Both arguments are stored, not just used.</b> <see cref="EnsureParticleRenderer"/>
        /// re-creates the view if a visual clear removed it, so it needs the collaborators to survive
        /// here rather than being captured once.</para>
        /// </summary>
        public void SetParticleWorld(
            PFE.Systems.Particles.ParticleWorld world,
            PFE.Systems.Particles.Rendering.ParticleSpriteCatalog catalog)
        {
            particleWorld = world;
            particleCatalog = catalog;
            EnsureParticleRenderer();
        }

        /// <summary>
        /// Hands down the two room-scoped particle adapters and pushes the current room's tile query
        /// into them — see <see cref="particleTileWater"/> for why this controller owns that push.
        /// </summary>
        public void SetParticleAdapters(
            PFE.Systems.Particles.Adapters.TileQueryParticleWater tileWater,
            PFE.Systems.Particles.Adapters.RoomParticleEmitter emitter)
        {
            particleTileWater = tileWater;
            particleEmitter = emitter;

            // Pushed here as well as in Initialize: MapBridge hands the adapters down after the first
            // room may already have been built, and an adapter that missed its only push would answer
            // dry and refuse to emit for the whole session.
            PushParticleTileQuery();

            // Same reason, one layer down: the first room's units were built before this arrived, so
            // the spawner has to reach back and hand them the emitter. Without this the opening room's
            // effects would never draw while every later room's would — a difference that looks like a
            // room-specific bug rather than a missed handover.
            roomUnitSpawner?.SetParticleEmitter(emitter);

            // And the same handover for the scripted unlock's `shine()` (Interact.as:1678-1686): the
            // trigger layer is built before this adapter exists, so without this push the begDoor
            // cutscene's unlock in the opening room would flash nothing while later rooms worked.
            areaTriggerSystem?.SetParticleEmitter(emitter);
        }

        /// <summary>
        /// Builds the current room's tile query and pushes it into both particle adapters, or clears
        /// them when there is no room. Null is the honest answer for a teardown: the adapters then
        /// answer dry and refuse to emit rather than converting against a room that is gone.
        /// </summary>
        private void PushParticleTileQuery()
        {
            PFE.Systems.Map.TileQuery.ITileQueryService query = roomInstance != null
                ? new PFE.Systems.Map.TileQuery.UnifiedTileQueryService(roomInstance)
                : null;

            particleTileWater?.SetTileQuery(query);
            particleEmitter?.SetTileQuery(query);
        }

        /// <summary>
        /// Creates the particle view under the room root if it is missing.
        ///
        /// <para>Called from <see cref="SetParticleWorld"/> and again every frame from
        /// <see cref="UpdateVisuals"/>. The per-frame call is deliberate: <see cref="ClearVisuals"/> may
        /// tear the child down on a room rebuild, and a renderer that is never re-created presents as
        /// "particles worked, then stopped after the first room transition" — the kind of failure that
        /// costs an afternoon. The guard is a single null test.</para>
        /// </summary>
        private void EnsureParticleRenderer()
        {
            if (particleWorld == null || particleCatalog == null) return;
            if (particleRenderer != null) return;

            var rendererObject = new GameObject("__ParticleRenderer");
            rendererObject.transform.SetParent(transform, false);

            particleRenderer = rendererObject.AddComponent<PFE.Systems.Particles.Rendering.ParticleRenderer>();
            particleRenderer.Initialize(particleWorld, particleCatalog);

            // The room root IS this controller's transform, and it is the space `ParticleState.X/Y`
            // (room-local pixels) are expressed in. Attaching anywhere else displaces every particle by a
            // room width in any room that is not at the origin.
            //
            // The height goes with it because `ParticleState.Y` is AS3's downward axis and the port's is
            // upward: the renderer mirrors about the room height, so it must be the ROOM's height and not
            // the 25-tile default. `roomInstance` is the same object the tile grid and doors are built
            // from, so all three agree.
            int roomHeightTiles = roomInstance != null ? roomInstance.height : WorldConstants.ROOM_HEIGHT;
            particleRenderer.AttachTo(transform, roomHeightTiles);
        }

        /// <summary>
        /// Fired when an area trigger executes a gotoland command to transition to another land.
        /// </summary>
        public event Action<string> OnGotoLand;

        /// <summary>
        /// Get the tile asset database used for rendering this room.
        /// </summary>
        public TileAssetDatabase TileAssetDatabase => tileAssetDatabase;

        /// <summary>
        /// Is this room currently visible?
        /// </summary>
        public bool IsVisible => isVisible;
        public bool ShowContourDebugOverlay
        {
            get => showContourDebugOverlay;
            set => showContourDebugOverlay = value;
        }

        public bool UseLongDebugExport
        {
            get => useLongDebugExport;
            set => useLongDebugExport = value;
        }

        public void ConfigureCompositorAssets(TileTextureLookup textureLookup, MaterialRenderDatabase materialDatabase, RoomBackgroundLookup backgroundLookup = null, TileMaskLookup maskLookup = null)
        {
            // The shared compositor caches sprites baked from the current assets, so swapping the
            // assets under it makes every cached sprite stale. Detect that before assigning.
            bool compositorAssetsChanged = tileTextureLookup != textureLookup
                || materialRenderDb != materialDatabase;

            tileTextureLookup = textureLookup;
            materialRenderDb = materialDatabase;
            if (backgroundLookup != null)
            {
                roomBackgroundLookup = backgroundLookup;
            }
            if (maskLookup != null)
            {
                tileMaskLookup = maskLookup;
            }

            if (compositorAssetsChanged)
            {
                ReleaseCompositor();
            }
        }

        /// <summary>
        /// Get the shared tile compositor, creating it on first use.
        ///
        /// Deliberately kept alive across <see cref="Initialize"/> calls: a room transition then
        /// reuses the sprites baked for any material it shares with the previous room instead of
        /// regenerating ~1100 textures. Released by <see cref="ReleaseCompositor"/>.
        /// </summary>
        private TileCompositor ResolveCompositor()
        {
            if (tileCompositor != null)
            {
                return tileCompositor;
            }

            if (tileTextureLookup == null || materialRenderDb == null)
            {
                Debug.LogWarning("[RoomVisualController] TileCompositor disabled: TileTextureLookup or MaterialRenderDatabase is missing.");
                return null;
            }

            tileCompositor = new TileCompositor(tileTextureLookup, materialRenderDb, tileMaskLookup);
            return tileCompositor;
        }

        /// <summary>
        /// Release the baked tile sprites but keep the compositor itself.
        ///
        /// The sprite cache is shared across rooms — that is what makes a room transition cheap — but
        /// it is keyed by material + texture phase, so it keeps growing as more materials are seen.
        /// A land change is the natural bound, because the whole tileset differs afterwards.
        ///
        /// Safe to call only when no live tile renderer still references these sprites. MapBridge
        /// calls it after <see cref="ClearVisuals"/> on the land-switch path, which is two frames
        /// before the next <see cref="Initialize"/>, so the old tiles are already destroyed.
        /// </summary>
        public void ReleaseBakedTileSprites()
        {
            tileCompositor?.ClearCache();
        }

        /// <summary>
        /// Destroy the shared compositor's baked textures and drop it, so the next
        /// <see cref="ResolveCompositor"/> rebuilds against the current assets.
        /// </summary>
        private void ReleaseCompositor()
        {
            if (tileCompositor == null)
            {
                return;
            }

            tileCompositor.ClearCache();
            tileCompositor = null;
        }

        /// <summary>
        /// Initialize the room visual controller.
        /// </summary>
        public void Initialize(RoomInstance room, TileAssetDatabase assetDatabase)
        {
            if (isInitialized)
            {
                ClearVisuals();
            }

            debugSettings = ResolveDebugSettings();
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Initializing room {room?.id}...");
            }
            
            roomInstance = room;
            tileAssetDatabase = ResolveTileAssetDatabase(assetDatabase);
            Profiler.Mark("room.resolveTileDb");
            visibilityRevealTargetPlayer = null;

            // Point the particle adapters at THIS room. Done here rather than at the particle-view
            // creation below because the adapters are pushed, not injected, and this is the first
            // moment the room is known. Null room pushes null, which is the honest teardown answer.
            PushParticleTileQuery();

            // The scripting/trigger layer is not a rendering concern, so create and wire it BEFORE
            // the render-asset guards below. Otherwise a missing tile database leaves
            // AreaTriggerSystem null, which silently kills the OnGotoLand forwarding chain that
            // MapBridge subscribes to.
            areaTriggerSystem = new PFE.Systems.Map.Scripting.AreaTriggerSystem();
            areaTriggerSystem.OnGotoLand += land => OnGotoLand?.Invoke(land);

            // Publish this room's `@targ` host so a script that is NOT owned by a room can still
            // resolve a target against one. The caller that needs it is the land-level
            // `<scr eve='take' item='…'>` path: it starts from an inventory event on the player, which
            // knows the room it is standing in but has no way to reach the object host that serves it
            // (`RoomInstance` is plain map data with no back-reference to its renderer, deliberately).
            // This controller is the only thing that constructs an AreaTriggerSystem, so it is the only
            // place that can publish the pair.
            PFE.Systems.Map.Scripting.ScriptSystem.Current?.RegisterObjectHost(roomInstance, areaTriggerSystem);

            if (room == null)
            {
                Debug.LogError("[RoomVisualController] Cannot initialize with null room!");
                return;
            }
            
            if (tileAssetDatabase == null)
            {
                Debug.LogError("[RoomVisualController] Cannot initialize with null asset database!");
                return;
            }

            // Create parent transforms
            if (tileParent == null)
            {
                GameObject tileParentObj = new GameObject("Tiles");
                tileParentObj.transform.SetParent(transform);
                tileParent = tileParentObj.transform;
            }

            if (backgroundParent == null)
            {
                GameObject bgParentObj = new GameObject("Background");
                bgParentObj.transform.SetParent(transform);
                backgroundParent = bgParentObj.transform;
            }

            EnsureBackgroundTileParent();
            EnsureOverlayParents();
            EnsureObjectParents();
            CleanupLegacyOverlayChildren();
            EnsureDecalLayer();

            // Create tile visual managers
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Creating TileVisualManager for {room.width}x{room.height} room");
            }
            // Shared across rooms — see the tileCompositor field. Deliberately NOT cleared by
            // ClearVisuals, so a transition reuses the sprites already baked for this controller.
            TileCompositor compositor = ResolveCompositor();

            tileVisualManager = new TileVisualManager(
                room,
                tileAssetDatabase,
                tileParent,
                compositor,
                debugSettings,
                0,
                Color.white,
                ResolveBackgroundTileTint(room),
                MapSortingLayers.MainTiles,
                MapSortingLayers.Foreground);
                Profiler.Mark("room.tileVisualManager.ctor");
            tileVisualManager.Decals = tileDecalLayer;

            // Create background layer if exists
            if (room.hasBackgroundLayer && room.backgroundRoom != null)
            {
                if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
                {
                    Debug.Log("[RoomVisualController] Creating background layer...");
                }
                backgroundTileVisualManager = new TileVisualManager(
                    room.backgroundRoom,
                    tileAssetDatabase,
                    backgroundTileParent,
                    compositor,
                    debugSettings,
                    BackgroundTileSortingOffset,
                    ResolveBackgroundTileTint(room),
                    ResolveBackgroundTileTint(room),
                    MapSortingLayers.BackgroundTiles,
                    MapSortingLayers.BackgroundTiles);
                    Profiler.Mark("room.bgTileVisualManager.ctor");
            }
            else if (room.hasBackgroundLayer)
            {
                Debug.LogWarning($"[RoomVisualController] Room '{room.id}' expects a background layer, but no background room instance is attached.");
            }

            ApplyStoredRoomTintSettingsIfAvailable(room);
            ApplyStoredBackdropSettingsIfAvailable(room);
            Profiler.Mark("room.storedSettings.applied");
            roomBackdropRenderer = new RoomBackdropRenderer(
                room,
                tileTextureLookup,
                roomBackgroundLookup,
                roomBackdropSettingsLookup,
                GetCurrentRoomStorageKey(),
                backgroundParent,
                fogOfWarParent,
                backdropTextureScale,
                backdropTextureOffset,
                flipBackdropTextureX,
                flipBackdropTextureY,
                GetCurrentBackdropTintSettings(),
                backdropSharpenStrength,
                GetCurrentBackgroundAssetTintSettings(),
                disableBackdropShadowBakeForDebug);
                Profiler.Mark("room.backdropRenderer.ctor");

            using (Profiler.Region("room.kontur.calculateAll", "boot: expected trivial"))
            {
                KonturCalculator.CalculateAll(room);
            }

            // Which renderer draws this room is decided BEFORE anything is built, because the decision
            // is what decides whether a tile object needs a sprite at all. Building the tiles first and
            // then hiding them (which is what this used to do) pays for every composited sprite the
            // quarter renderer immediately covers up — 1 136 objects and ~570 ms of pixel work per room
            // for a picture nobody sees. See TileCollisionBuilder for the collision-only half.
            bool quarterTerrain;
            using (Profiler.Region("room.terrain.quad", "S3: quarter renderer — measured against tiles.createAll"))
            {
                quarterTerrain = TryBuildRoomTerrain(room);
            }

            int mainTiles;
            if (quarterTerrain)
            {
                // Collision without a picture: same objects, same transforms, same TileCollider, no
                // TileRenderer and no compositor. The quarter renderer already drew the room above.
                using (Profiler.Region("room.tiles.collisionOnly", "replaces tiles.createAll on the default path — no compositing at all"))
                {
                    tileCollisionBuilder = new TileCollisionBuilder(room, tileParent, debugSettings);
                    tileCollisionBuilder.Decals = tileDecalLayer;
                    mainTiles = tileCollisionBuilder.BuildAll();
                }

                if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
                {
                    Debug.Log($"[RoomVisualController] Built {mainTiles} collision-only tile object(s); " +
                              "the quarter renderer draws this room's terrain.");
                }
            }
            else
            {
                // Create all tiles
                if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
                {
                    Debug.Log("[RoomVisualController] Creating main layer tiles...");
                }
                using (Profiler.Region("tiles.createAll", "boot: MEASURED 2518ms (2026-09-25) — was ~31s before the TileCompositor fixes"))
                {
                    tileVisualManager.CreateAllTiles();
                }
                mainTiles = tileVisualManager.GetTileCount();
                if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
                {
                    Debug.Log($"[RoomVisualController] Created {mainTiles} main layer tiles");
                }
            }

            if (backgroundTileVisualManager != null)
            {
                using (Profiler.Region("tiles.createAll.bg", "boot: background layer — never fires for the base room"))
                {
                    backgroundTileVisualManager.CreateAllTiles();
                }
                if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
                {
                    Debug.Log($"[RoomVisualController] Created {backgroundTileVisualManager.GetTileCount()} background tiles");
                }
            }

            using (Profiler.Region("room.backdrop.createVisuals", "boot: MEASURED 1453ms (2026-09-25) — 2nd largest item. Now split into backdrop.* sub-regions"))
            {
                roomBackdropRenderer.CreateVisuals();
                roomBackdropRenderer.SetFogOfWarDisabled(fogOfWarDisabled);
            }
            roomObjectVisualManager = new RoomObjectVisualManager(room, backgroundObjectParent, backgroundPhysicalObjectParent, areaTriggerSystem);
            roomObjectVisualManager.RefreshAll();
            Profiler.Mark("room.objects.refreshAll");

            // Units share the physical-object parent: they are bodies in the world, not backdrop.
            // This is the producer the unit layer was missing — until now room.units had readers
            // (RoomStateSnapshot, this controller's own bounds checks) and no instantiation site, so
            // every authored enemy existed as data and was never drawn.
            roomUnitSpawner = new RoomUnitSpawner(
                room, backgroundPhysicalObjectParent, damageSystem: damageSystem,
                simClock: simClock, simLoop: simLoop, effectResolver: effectResolver,
                particleEmitter: particleEmitter,
                // Stage C path 6: read once at build time, like every other flag on this seam.
                useTileMotor: debugSettings != null && debugSettings.UnitMotor,
                // The container the spawner reaches the weapon/audio services through. Without it a
                // spawned enemy has no weapon stack and no voice — see RoomUnitSpawner's field remarks.
                resolver: resolver);
            roomUnitSpawner.RefreshAll();
            Profiler.Mark("room.units.refreshAll");

            // ...and the floor loot, which is the one thing in this room that is NOT rebuilt from the
            // template: props and units come back from `room.objects` / `room.units`, which survive, and
            // loot comes back from `room.droppedItems`, which is the only place a dropped stack exists at
            // all. See WorldItemPickup.RestoreForRoom for why an already-live view is skipped (a save
            // applied to the room the player is standing in) and why an unresolvable id is retried
            // rather than dropped.
            using (Profiler.Region("room.loot.restore", "boot: floor loot rebuilt from the room's records"))
            {
                WorldItemPickup.RestoreForRoom(room);
            }
            Profiler.Mark("room.loot.restore");

            // ...and this is the line that makes a script's `@targ` able to name a UNIT.
            //
            // AS3 keeps ONE lookup table per land — `Script.as:218-236` resolves every `@targ` through
            // `this.land.uidObjs[param1.targ]`, and units register into it at spawn alongside objects.
            // The port split that table by ownership (objects on AreaTriggerSystem, live units on this
            // spawner), so the runner has to be able to reach both halves. Without this call the unit
            // half exists but is unreachable, and `surfStory`'s five `@targ="surfCalam"` actions are
            // dropped with "no object in the room matches that uid or code" — the exact symptom that
            // started the Calamity work.
            //
            // AFTER RefreshAll, because the host answers against the spawned GameObjects: a handover
            // before the units exist would resolve `surfCalam` to a record with no controller behind it.
            areaTriggerSystem.SetUnitHost(roomUnitSpawner);

            // ...and the particle adapter, for the `shine()` that ends a scripted unlock
            // (Interact.as:1678-1686). Pushed here as well as in SetParticleAdapters because the two
            // orderings are both real: the adapter may arrive first (this path) or the room may be
            // built first (that one).
            areaTriggerSystem.SetParticleEmitter(particleEmitter);

            // ...and the same table, handed the other way, so a DIALOGUE script can resolve `@targ`.
            //
            // AS3 builds a dialogue's script with the land as its context (`NPC.as:322`,
            // `new Script(node, World.w.land, owner, true)`), and `Script.com` resolves every `@targ`
            // through `land.uidObjs` — objects and units in one table. The port splits that table by
            // ownership, so an NPC needs BOTH halves; `RoomUnitSpawner.UnitCommand` alone would let a
            // dialogue name a unit but not an object, and the shipped data does both
            // (`unlock targ="doorRBL1"`, `sign targ="worldMap"`, `check targ="rblCalam"`).
            //
            // `AreaTriggerSystem` implements all three host interfaces, so the same instance serves the
            // room's own runner and every NPC's dialogue runner — one lookup, two callers, which is what
            // the oracle has.
            roomUnitSpawner.SetScriptHosts(areaTriggerSystem, areaTriggerSystem);

            EnsureBoundaryDoorTriggers(room);

            // Set world position
            Vector3 worldPos = GetRoomWorldPosition();
            transform.position = worldPos;
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Room positioned at world: {worldPos}");
            }

            isInitialized = true;
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Room {room.id} initialized successfully with {mainTiles} tiles!");
            }
        }

        /// <summary>
        /// Builds the quarter terrain renderer for this room, and reports whether it will draw it.
        ///
        /// <para><b>The answer decides what the tiles are.</b> <see langword="true"/> means the caller
        /// builds collision-only tiles (<see cref="TileCollisionBuilder"/>) and no sprites at all;
        /// <see langword="false"/> means the shipped path builds both, exactly as it always did. This
        /// method does not touch the tile objects — it runs before them, which is why there is no
        /// "suppress the other renderer" state left to keep honest.</para>
        ///
        /// <para><b>Failure is a fallback, not an empty room.</b> Every way this can fail — flag off,
        /// art asset absent, no material database, the shader missing — returns
        /// <see langword="false"/>, and the shipped renderer then draws the room from nothing. That is
        /// structural rather than defensive: the replacement is built <i>before</i> anything is skipped,
        /// so "sprites skipped, replacement never built" cannot be reached at all. It used to be a
        /// condition on <see cref="RoomTerrainRenderer.IsBuilt"/> instead, because the sprites were built
        /// first and then hidden.</para>
        ///
        /// <para><b>Read once, here.</b> The flag is sampled at build time and never again, so a room
        /// cannot be half-quarter and half-shipped; the console verb therefore takes effect on the next
        /// room. That is a deliberate limitation, not an oversight: rebuilding terrain mid-room would have
        /// to rebuild the tile colliders too, and that is a different, riskier change.</para>
        /// </summary>
        /// <returns>True when the quarter renderer was built and owns this room's terrain.</returns>
        private bool TryBuildRoomTerrain(RoomInstance room)
        {
            PfeDebugSettings settings = ResolveDebugSettings();
            bool enabled = settings != null && settings.QuadRoomTerrain;

            if (!enabled)
            {
                // The escape hatch, and it must cost nothing: no log, no allocation, no asset lookup.
                return false;
            }

            RoomTerrainArt art = RoomTerrainWiring.ResolveArt(roomTerrainArt);

            if (!RoomTerrainWiring.TryResolve(enabled, art, materialRenderDb, out string reason))
            {
                Debug.LogWarning(
                    $"[RoomTerrain] Quarter renderer NOT built for room '{room.id}': {reason}. " +
                    "The shipped per-tile renderer draws this room — this is a fallback, not a failure.");
                return false;
            }

            var go = new GameObject("RoomTerrain");
            // worldPositionStays: false — the renderer's coordinates are the room's own local grid
            // (`x * cell`, `y * cell` with the top of the room at `height * cell`), so a parent offset
            // would move every shape and put the bitmap fill's origin somewhere else.
            go.transform.SetParent(transform, false);

            var renderer = go.AddComponent<RoomTerrainRenderer>();
            renderer.Build(room, art, materialRenderDb, RoomTerrainWiring.ResolveOptions(RoomTerrainWiring.ShippedCellSize));

            if (!renderer.IsBuilt)
            {
                // Build() already logged why (no shader). Nothing has been skipped yet, so the shipped
                // renderer can simply be the one that runs.
                Debug.LogWarning(
                    $"[RoomTerrain] Quarter renderer failed to build for room '{room.id}'; " +
                    "the shipped per-tile renderer draws this room.");
                DestroyObject(go);
                return false;
            }

            roomTerrainRenderer = renderer;

            Debug.Log(
                $"[RoomTerrain] Room '{room.id}' drawn by the quarter renderer: " +
                $"{renderer.ShapesPlaced} shape(s) in {renderer.LayerCount} layer(s) " +
                $"× {renderer.MeshesPerLayer} mesh(es) (chunk {renderer.ChunkSize}), " +
                $"{renderer.OverlaysPlaced} overlay(s), {renderer.LastBuildMilliseconds:0.0} ms. " +
                "Tile objects are built for collision only — no composited sprites. " +
                "Layers is the terrain's own floor on the HUD's Batches count.");

            if (renderer.OverwrittenShapes != 0)
            {
                // Four quarters of a cell belong on four different half-cell slots, so a correct build
                // replaces nothing. Non-zero means the mask grid and the Tilemap grid disagree about the
                // cell size, and the picture is missing three quarters out of four while ShapesPlaced
                // reports all of them.
                Debug.LogError(
                    $"[RoomTerrain] {renderer.OverwrittenShapes} shape(s) replaced a shape already in that cell — " +
                    "the half-cell grid is wrong and the room is missing quarters.");
            }

            return true;
        }

        private TileAssetDatabase ResolveTileAssetDatabase(TileAssetDatabase requestedDatabase)
        {
            if (HasOverlaySprites(requestedDatabase))
            {
                return requestedDatabase;
            }

            if (HasOverlaySprites(tileAssetDatabase))
            {
                if (requestedDatabase != null && requestedDatabase != tileAssetDatabase)
                {
                    Debug.LogWarning(
                        $"[RoomVisualController] Requested TileAssetDatabase '{requestedDatabase.name}' has no tileFront sprites. " +
                        $"Falling back to serialized database '{tileAssetDatabase.name}'.");
                }

                return tileAssetDatabase;
            }

            return requestedDatabase ?? tileAssetDatabase;
        }

        private static bool HasOverlaySprites(TileAssetDatabase database)
        {
            return database != null && database.GetCount() > 0;
        }

        /// <summary>
        /// Get world position for this room.
        /// Accounts for the room's pixel origin in the land grid.
        /// </summary>
        private Vector3 GetRoomWorldPosition()
        {
            if (roomInstance == null)
                return Vector3.zero;

            int borderOffset = Mathf.Max(0, roomInstance.borderOffset);
            
            Vector2 roomPixelPos = new Vector2(
                WorldCoordinates.RoomOriginPixelX(roomInstance.landPosition.x, borderOffset),
                WorldCoordinates.RoomOriginPixelY(roomInstance.landPosition.y, borderOffset)
            );

            return WorldCoordinates.PixelToUnity(roomPixelPos);
        }

        /// <summary>
        /// Instantiate boundary door triggers for all active DoorInstances in the room.
        /// </summary>
        public void EnsureBoundaryDoorTriggers(RoomInstance room)
        {
            if (room?.doors == null || room.doors.Count == 0) return;

            Transform doorParent = transform.Find("DoorTriggers");
            if (doorParent == null)
            {
                var doorParentGo = new GameObject("DoorTriggers");
                doorParentGo.transform.SetParent(transform, false);
                doorParent = doorParentGo.transform;
            }

            for (int i = 0; i < room.doors.Count; i++)
            {
                DoorInstance door = room.doors[i];
                if (door == null || !door.isActive) continue;

                // Validate that the door position is NOT inside a solid wall
                TileData tile = room.GetTileAtCoord(door.tilePosition);
                if (tile != null && tile.physicsType == TilePhysicsType.Wall)
                {
                    continue;
                }

                string doorName = $"DoorTrigger_{door.doorIndex}_{door.side}";
                Transform existing = doorParent.Find(doorName);
                GameObject doorGo = existing != null ? existing.gameObject : new GameObject(doorName);
                doorGo.transform.SetParent(doorParent, false);

                // Convert tile position to room local Unity space
                Vector2 tilePixel = WorldCoordinates.TileToPixel(door.tilePosition);
                Vector3 localUnityPos = WorldCoordinates.PixelToUnity(tilePixel + new Vector2(WorldConstants.TILE_SIZE * 0.5f, WorldConstants.TILE_SIZE * 0.5f));
                doorGo.transform.localPosition = localUnityPos;

                BoxCollider2D collider = doorGo.GetComponent<BoxCollider2D>();
                if (collider == null)
                {
                    collider = doorGo.AddComponent<BoxCollider2D>();
                }
                collider.isTrigger = true;

                // Size collider depending on horizontal or vertical door
                bool isVertical = door.side == DoorSide.Left || door.side == DoorSide.Right;
                float w = isVertical ? 0.20f : 0.40f;
                float h = isVertical ? 0.40f : 0.20f;
                collider.size = new Vector2(w, h);
                collider.offset = Vector2.zero;

                DoorTrigger trigger = doorGo.GetComponent<DoorTrigger>();
                if (trigger == null)
                {
                    trigger = doorGo.AddComponent<DoorTrigger>();
                }
                trigger.SetDoor(door, room);
            }
        }

        /// <summary>
        /// Update visual state.
        /// </summary>
        public void UpdateVisuals()
        {
            if (!isInitialized)
                return;

            // Self-healing: ClearVisuals may have taken the particle view down on a room rebuild.
            EnsureParticleRenderer();

            tileVisualManager?.UpdateAllTiles();
            backgroundTileVisualManager?.UpdateAllTiles();
            roomObjectVisualManager?.RefreshAll();
            roomUnitSpawner?.RefreshAll();

            // AS3 `if(loc.prob) loc.prob.check()` from inside a unit's `die()` (Unit.as:4395-4402). The
            // scan is the port's inverse of that: the spawned controllers hold the live aliveness and
            // nothing gives a unit a reference back to its room, so the room asks them.
            //
            // Here rather than in a tick of its own because this method is already the per-frame room
            // refresh — adding a second driver for the one call would be a second schedule to drift.
            ReportProbRoomDeaths();
        }

        /// <summary>
        /// Hands the prob-room death scan to this room's spawner and runs it once.
        ///
        /// <para><b>The provider is resolved per call by the spawner.</b> A land build replaces
        /// <c>RoomTransitionManager.ProbSessions</c>, so the reference handed over here must be a
        /// getter rather than the object — see <see cref="RoomUnitSpawner.CheckDeaths"/>.</para>
        /// </summary>
        private void ReportProbRoomDeaths()
        {
            if (roomUnitSpawner == null) return;

            if (roomUnitSpawner.ProbSessionsProvider == null)
            {
                roomUnitSpawner.ProbSessionsProvider = () =>
                    PFE.Systems.Map.Streaming.RoomTransitionManager.Instance?.ProbSessions;
            }

            roomUnitSpawner.CheckDeaths();
        }

        /// <summary>
        /// Append a caller-authored <see cref="UnitInstance"/> to this room and build it now.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists rather than a spawn inside the debug overlay.</b> A live unit is not
        /// just a GameObject: <see cref="RoomUnitSpawner"/> is the one place that hands a spawned unit its
        /// damage system, sim clock, tile query, object-physics layer, effect resolver and particle
        /// emitter. A debug tab that built its own GameObject would get a unit that is unarmoured, falls
        /// through the floor and cannot be set on fire — and every one of those failures is silent. So the
        /// tab authors a <i>record</i> and this method hands it to the same producer an authored enemy goes
        /// through; <c>RefreshAll</c> is what actually builds it.</para>
        ///
        /// <para><b>The position is converted here, not by the caller.</b> A <see cref="UnitInstance"/>
        /// carries <b>room-local pixels</b> — the spawner does
        /// <c>localPosition = WorldCoordinates.PixelToUnity(unit.position)</c> under the
        /// physical-object parent — while a debug caller naturally holds a Unity world position. The
        /// conversion goes through that same parent's <c>InverseTransformPoint</c>, so the two cannot
        /// disagree about where the room's origin is. Re-deriving the origin from the land grid instead is
        /// the coordinate-convention mistake this project has made repeatedly.</para>
        ///
        /// <para><b>Returns false rather than guessing.</b> A room that has not been initialised has no
        /// spawner and no physical-object parent; falling back to <c>transform</c> would silently place
        /// the unit at a different point than the one asked for, which is worse than a visible
        /// refusal.</para>
        ///
        /// <para><b>Not on the room's spawn RNG.</b> This deliberately draws nothing from
        /// <c>IRngService</c>: a debug spawn must not shift the seeded stream that generated the room, or
        /// "the room is reproducible from its seed" would stop being true the moment a tester clicked a
        /// button. The caller therefore supplies the facing.</para>
        /// </remarks>
        /// <param name="unit">The record to append. Its <c>position</c> is overwritten.</param>
        /// <param name="worldPosition">Where the unit's feet should land, in Unity world space.</param>
        /// <returns>True when the record was appended and the spawner rebuilt.</returns>
        /// <summary>
        /// Room-local <b>pixel</b> coordinates → Unity world position. The exact inverse of the
        /// conversion inside <see cref="SpawnUnitNow"/>.
        ///
        /// <para><b>Needed by anything that has to place a world-space effect at an object the map
        /// layer only knows in pixels.</b> A room is not at the world origin and its physical-object
        /// parent carries the pixel scale, so adding the room origin by hand gets the position right in
        /// exactly the room the author tested in. Returns the controller's own position when the parent
        /// has not been built yet, so a caller that fires before initialisation lands somewhere visible
        /// rather than at (0,0,0).</para>
        /// </summary>
        public Vector3 RoomPixelToWorld(Vector2 roomPixel)
        {
            if (backgroundPhysicalObjectParent == null)
            {
                return transform.position;
            }

            return backgroundPhysicalObjectParent.TransformPoint(
                WorldCoordinates.PixelToUnity(roomPixel));
        }

        /// <summary>
        /// How much of the fog this room has revealed at a world point, in <c>[0, 1]</c>; <c>false</c>
        /// when nothing is drawn over that point (no fog, no mask, outside the room) — in which case the
        /// caller must treat it as fully visible.
        ///
        /// <para><b>For HUD elements anchored to a world position</b> — the starred-enemy marker is the
        /// first caller. The oracle composites those under the fog mask; this port draws them on the
        /// canvas, so they have to ask. See <see cref="RoomBackdropRenderer.TryGetFogVisibility"/>.</para>
        /// </summary>
        public bool TryGetFogVisibility(Vector3 worldPosition, out float visibility)
        {
            visibility = 1f;
            return roomBackdropRenderer != null &&
                roomBackdropRenderer.TryGetFogVisibility(worldPosition, out visibility);
        }

        /// <summary>
        /// The GameObject a runtime-added <see cref="UnitInstance"/> was built into, or false when the
        /// room has no spawner or the record is not live. See
        /// <see cref="RoomUnitSpawner.TryGetSpawnedObject"/> for why the caller cannot find it itself.
        /// </summary>
        public bool TryGetSpawnedUnit(UnitInstance unit, out GameObject spawned)
        {
            if (roomUnitSpawner != null)
            {
                return roomUnitSpawner.TryGetSpawnedObject(unit, out spawned);
            }

            spawned = null;
            return false;
        }

        /// <summary>
        /// The live controller that owns <paramref name="room"/>, or <c>null</c>.
        ///
        /// <para><b>Needed by anything that spawns a unit from inside the simulation.</b> A brain holds a
        /// <see cref="RoomInstance"/>, not a GameObject — <c>UnitController.Room</c> comes from the tile
        /// query — so it has no way to reach <see cref="SpawnUnitNow"/>, which is where a runtime unit is
        /// actually built. The map bridge's own controller is tried first because that is the room the
        /// player is in; the scene scan is the same fallback <c>SecurityRuntime</c> uses for alarm
        /// reinforcements.</para>
        ///
        /// <para><b>A scene scan per call is acceptable here and would not be per tick.</b> The callers
        /// are a nest's 100-tick emit and a bloat's death split, not a movement path.</para>
        /// </summary>
        public static RoomVisualController FindForRoom(RoomInstance room)
        {
            if (room == null)
            {
                return null;
            }

            var bridge = FindFirstObjectByType<MapBridge>();
            if (bridge != null && bridge.VisualController != null &&
                bridge.VisualController.RoomInstance == room)
            {
                return bridge.VisualController;
            }

            RoomVisualController[] controllers =
                FindObjectsByType<RoomVisualController>(FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++)
            {
                if (controllers[i] != null && controllers[i].RoomInstance == room)
                {
                    return controllers[i];
                }
            }

            return null;
        }

        public bool SpawnUnitNow(UnitInstance unit, Vector3 worldPosition)
        {
            if (unit == null || roomInstance == null || roomUnitSpawner == null ||
                backgroundPhysicalObjectParent == null)
            {
                return false;
            }

            unit.position = WorldCoordinates.UnityToPixel(
                backgroundPhysicalObjectParent.InverseTransformPoint(worldPosition));

            roomInstance.units.Add(unit);
            roomUnitSpawner.RefreshAll();
            return true;
        }

        /// <summary>
        /// Drop a unit record from this room's list and destroy the GameObject built for it.
        /// </summary>
        /// <remarks>
        /// <para>This is the inverse of <see cref="SpawnUnitNow"/> and it goes through the same
        /// <c>RefreshAll</c> on purpose: a record removed from <c>room.units</c> becomes <i>stale</i> to
        /// the spawner, which destroys its GameObject. Destroying the GameObject directly would leave the
        /// record behind, and the next <c>RefreshAll</c> — which every room update calls — would rebuild
        /// the unit, so the button would look like it did nothing.</para>
        /// </remarks>
        /// <returns>True when the record was present and removed.</returns>
        public bool DespawnUnitNow(UnitInstance unit)
        {
            if (unit == null || roomInstance == null || roomUnitSpawner == null)
            {
                return false;
            }

            if (!roomInstance.units.Remove(unit))
            {
                return false;
            }

            roomUnitSpawner.RefreshAll();
            return true;
        }

        /// <summary>
        /// How many unit GameObjects this room's spawner currently has live.
        /// </summary>
        /// <remarks>
        /// Read from the presenter that builds them rather than from <c>room.units</c> — the two numbers
        /// disagree exactly when a spawn has failed, and that disagreement is the finding, so a debug
        /// counter must not collapse them into one.
        /// </remarks>
        public int LiveUnitCount => roomUnitSpawner != null ? roomUnitSpawner.SpawnedCount : 0;

        /// <summary>The number of unit <i>records</i> the room holds, spawned or not.</summary>
        public int UnitRecordCount => roomInstance != null && roomInstance.units != null
            ? roomInstance.units.Count
            : 0;

        private void LateUpdate()
        {
            if (!isInitialized)
            {
                return;
            }

            // Moving platforms step BEFORE the presenter sync below, so the frame that advances them is
            // also the frame that draws them at their new position.
            //
            // AS3 reaches this from `Location.step`, which walks the room's object list once per frame and
            // calls each `Interact.step` — whose `if(this.isMove) this.move()` is the only thing that
            // writes a platform's X/Y (Interact.as:620-624). So the oracle's cadence is one step per
            // rendered frame, which is what LateUpdate is; a fixed-rate tick would have been the other
            // defensible choice and is what `SimLoop` is for, but it would have needed a new
            // SimTickOrder slot and a container registration for a system whose whole lifetime is one
            // room's.
            //
            // `Count > 0` rather than an unconditional call: `MovingPlatforms` is lazy, and a room with no
            // `<move>` node — 542 of the 639 imported rooms — should pay a null check, not a list walk.
            PFE.Systems.Map.MovingPlatformSystem platforms = roomInstance?.MovingPlatforms;
            if (platforms != null && platforms.Count > 0)
            {
                platforms.Step();
            }

            roomObjectVisualManager?.UpdateVisuals(Time.deltaTime);

            if (roomBackdropRenderer == null)
            {
                return;
            }

            if (visibilityRevealTargetPlayer == null)
            {
                visibilityRevealTargetPlayer = FindFirstObjectByType<PlayerController>();
            }

            // AS3's light is NOT at the unit's origin: `Location.lighting():3137-3140` defaults to
            // `(gg.X + gg.storona*12, gg.Y1 + gg.stayY*0.247)`, i.e. 12 px in front of the unit and,
            // standing, 75.3 % of the way up its sprite. See FogOcclusionMath.ResolvePlayerLightPixelOffset
            // for why this is load-bearing rather than cosmetic — with the light on the feet the AS3
            // lattice ray sits exactly on a tile boundary, and the pixel↔unit round trip tips it into
            // the tile below. The two-height form matters while crawling: AS3's `scY` halves with the
            // crouch (`Unit.sit():2520`), and feeding the standing height instead parks the light
            // inside the solid tile above a one-tile passage and reveals the tiles overhead.
            Vector3? playerLightWorldPosition = null;
            if (visibilityRevealTargetPlayer != null)
            {
                Vector2 lightOffsetPixels = FogOcclusionMath.ResolvePlayerLightPixelOffset(
                    visibilityRevealTargetPlayer.FacingDirection,
                    visibilityRevealTargetPlayer.FogLightCurrentHeightPixels,
                    visibilityRevealTargetPlayer.SpriteSizePixels.y);
                playerLightWorldPosition = visibilityRevealTargetPlayer.transform.position
                    + WorldCoordinates.PixelToUnity(lightOffsetPixels);
            }

            roomBackdropRenderer.UpdateVisibilityMask(playerLightWorldPosition);
        }

        /// <summary>
        /// Set visibility of this room.
        /// </summary>
        public void SetVisible(bool visible)
        {
            isVisible = visible;
            gameObject.SetActive(visible);
        }

        /// <summary>
        /// Update a specific tile.
        ///
        /// <para><b>Two different things, depending on who draws the room.</b> The shipped path redraws
        /// one <see cref="TileRenderer"/>. A collision-only room has no renderer to redraw — its picture
        /// is a function of the tile grid — so the equivalent action is to refresh the cell's collider and
        /// say the cell changed. Saying nothing would leave the quarter renderer drawing the old shape
        /// after an edit, which is the picture-lying failure the <c>TilesMutated</c> subscription exists
        /// to prevent.</para>
        /// </summary>
        public void UpdateTile(Vector2Int coord)
        {
            if (!isInitialized)
                return;

            if (tileCollisionBuilder != null)
            {
                tileCollisionBuilder.RebuildCell(coord);
                roomInstance?.NotifyTilesMutated(new RectInt(coord.x - 1, coord.y - 1, 3, 3));
                return;
            }

            if (tileVisualManager == null)
                return;

            tileVisualManager.UpdateTile(coord);
        }

        public void ApplyEditedPreviewTile(TileData editedTile)
        {
            if (editedTile == null || roomInstance == null)
            {
                return;
            }

            TileData targetTile = roomInstance.GetTileAtCoord(editedTile.gridPosition);
            if (targetTile == null)
            {
                return;
            }

            CopyEditedTileFields(editedTile, targetTile);
            UpdateTile(editedTile.gridPosition);
        }

        /// <summary>
        /// Destroy a specific tile.
        ///
        /// <para>A collision-only tile is destroyed through its builder, which also tells the room the
        /// cell changed. Going through <see cref="TileCollider"/> instead is what the runtime path does
        /// (the projectile hits the collider), so this is the same two steps in the same order.</para>
        /// </summary>
        public void DestroyTile(Vector2Int coord)
        {
            if (!isInitialized)
                return;

            if (tileCollisionBuilder != null)
            {
                tileCollisionBuilder.DestroyTile(coord);
                roomInstance?.NotifyTilesMutated(new RectInt(coord.x - 1, coord.y - 1, 3, 3));
                return;
            }

            if (tileVisualManager == null)
                return;

            tileVisualManager.DestroyTile(coord);
        }

        /// <summary>
        /// Flash a tile (for damage).
        ///
        /// <para><b>The quarter renderer has its own answer, and it is not the same mechanism.</b> A
        /// flashed tile in the shipped path is one <see cref="SpriteRenderer"/> whose colour is replaced
        /// for a moment. The quarter renderer's terrain is a mesh tinted per cell by the <i>material</i>,
        /// so there is no per-cell colour to replace — it draws a transient quad over the cell instead
        /// (<see cref="RoomTerrainRenderer.FlashCell"/>). Routing this to the visual manager in a
        /// collision-only room would have been a silent no-op: there is no renderer to flash.</para>
        /// </summary>
        public void FlashTile(Vector2Int coord, Color color, float duration = 0.1f)
        {
            if (roomTerrainRenderer != null && roomTerrainRenderer.IsBuilt)
            {
                roomTerrainRenderer.FlashCell(coord, color, duration);
                return;
            }

            if (!isInitialized || tileVisualManager == null)
                return;

            tileVisualManager.FlashTile(coord, color, duration);
        }

        /// <summary>
        /// Activate this room (show and enable).
        /// </summary>
        public void Activate()
        {
            SetVisible(true);
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Activated room at {roomInstance?.landPosition}");
            }
        }

        /// <summary>
        /// Deactivate this room (hide but don't destroy).
        /// </summary>
        public void Deactivate()
        {
            SetVisible(false);
            if (debugSettings != null && debugSettings.LogRoomRenderingLifecycle)
            {
                Debug.Log($"[RoomVisualController] Deactivated room at {roomInstance?.landPosition}");
            }
        }

        /// <summary>
        /// Destroy this room's visuals.
        /// </summary>
        public void DestroyRoom()
        {
            tileVisualManager?.DestroyAllTiles();
            tileCollisionBuilder?.DestroyAll();
            tileCollisionBuilder = null;
            backgroundTileVisualManager?.DestroyAllTiles();
            roomBackdropRenderer?.DestroyVisuals();
            roomObjectVisualManager?.DestroyAll();
            roomObjectVisualManager = null;
            roomUnitSpawner?.DestroyAll();
            roomUnitSpawner = null;
            DestroyDroppedItemViews();
            visibilityRevealTargetPlayer = null;
            ReleaseCompositor();

            if (Application.isPlaying)
            {
                Destroy(gameObject);
            }
            else
            {
                DestroyImmediate(gameObject);
            }
        }

        /// <summary>
        /// Refresh sprites (useful after changing asset database).
        /// </summary>
        public void RefreshSprites()
        {
            tileVisualManager?.RefreshSprites();
            backgroundTileVisualManager?.RefreshSprites();
            roomObjectVisualManager?.RefreshAll();
            roomUnitSpawner?.RefreshAll();
            RefreshBackdropVisuals();
        }

        /// <summary>
        /// Get tile renderer for a specific coordinate.
        ///
        /// <para>Null in a collision-only room, by construction: there is no per-tile renderer to return.
        /// Kept because the editor preview and a few diagnostics still ask, and a null is the honest
        /// answer rather than a stale object.</para>
        /// </summary>
        public TileRenderer GetTileRenderer(Vector2Int coord)
        {
            if (!isInitialized || tileVisualManager == null)
                return null;

            return tileVisualManager.GetTileRenderer(coord);
        }

        /// <summary>
        /// Get total number of tile objects this room built — collider-only or drawn.
        ///
        /// <para><b>Counts objects, not sprites.</b> The number is used in the build log and in the debug
        /// overlay as "how many tiles does this room have", and on the default path those tiles have no
        /// sprites at all. Reporting the visual manager's count there would report zero for a room that
        /// built 1 136 colliders.</para>
        /// </summary>
        public int GetTileCount()
        {
            if (!isInitialized)
                return 0;

            if (tileCollisionBuilder != null)
                return tileCollisionBuilder.Count;

            if (tileVisualManager == null)
                return 0;

            return tileVisualManager.GetTileCount();
        }

        public string BuildRoomDebugString(bool longFormat)
        {
            if (roomInstance == null || roomInstance.tiles == null)
            {
                return string.Empty;
            }

            return longFormat
                ? BuildLongDebugString(roomInstance)
                : BuildShortDebugString(roomInstance);
        }

        private void OnDestroy()
        {
            // Detach before the room hierarchy goes, so the pooled sprites are handed back rather than
            // destroyed with it — the pool is owned by the renderer, and a detached renderer can be
            // re-attached to the next room instead of rebuilding its pool from scratch.
            particleRenderer?.Detach();
            if (particleRenderer != null) Destroy(particleRenderer.gameObject);
            particleRenderer = null;

            tileVisualManager?.DestroyAllTiles();
            tileCollisionBuilder?.DestroyAll();
            tileCollisionBuilder = null;
            backgroundTileVisualManager?.DestroyAllTiles();
            roomBackdropRenderer?.DestroyVisuals();
            roomObjectVisualManager?.DestroyAll();
            roomObjectVisualManager = null;
            roomUnitSpawner?.DestroyAll();
            roomUnitSpawner = null;
            DestroyDroppedItemViews();
            visibilityRevealTargetPlayer = null;

            // The shared compositor outlives every room, so this is the one place its ~1100 baked
            // textures are guaranteed to be released. Ordered after the tile GameObjects are gone so
            // nothing is left holding a sprite whose texture has just been destroyed.
            ReleaseCompositor();
        }

        private void OnValidate()
        {
            ClampBackdropSettings();

            #if UNITY_EDITOR
            // Do NOT schedule the editor preview rebuild while playing.
            //
            // OnValidate fires during play-mode boot (Unity calls it when the component is loaded),
            // and the delayed call below rebuilds the entire backdrop. Measured 2026-09-25: that
            // second CreateVisuals cost ~1418 ms — a full rebuild on top of the one Initialize had
            // already done a few seconds earlier, i.e. ~20% of the 6.9 s boot, entirely wasted.
            // The editor preview is not visible in play mode, and nothing reads a preview there.
            //
            // Verified that OnValidate -> delayCall is the ONLY live path into that rebuild:
            // RoomVisualController.RefreshSprites() (the other caller of RefreshBackdropVisuals)
            // has no call sites anywhere in the project.
            if (Application.isPlaying)
            {
                return;
            }

            UnityEditor.EditorApplication.delayCall -= RefreshBackdropPreviewDelayed;
            UnityEditor.EditorApplication.delayCall += RefreshBackdropPreviewDelayed;
            #endif
        }

        private void RefreshBackdropVisuals()
        {
            if (roomInstance == null || backgroundParent == null)
            {
                return;
            }

            

            EnsureBackgroundTileParent();
            EnsureOverlayParents();
            EnsureObjectParents();
            CleanupLegacyOverlayChildren();
            ApplyBackgroundLayerRendering(roomInstance);
            roomBackdropRenderer?.DestroyVisuals();
            visibilityRevealTargetPlayer = null;
            roomBackdropRenderer = new RoomBackdropRenderer(
                roomInstance,
                tileTextureLookup,
                roomBackgroundLookup,
                roomBackdropSettingsLookup,
                GetCurrentRoomStorageKey(),
                backgroundParent,
                fogOfWarParent,
                backdropTextureScale,
                backdropTextureOffset,
                flipBackdropTextureX,
                flipBackdropTextureY,
                GetCurrentBackdropTintSettings(),
                backdropSharpenStrength,
                GetCurrentBackgroundAssetTintSettings(),
                disableBackdropShadowBakeForDebug);

            // Separate region from room.backdrop.createVisuals on purpose: this path is reached
            // from the editor-preview refresh, NOT from Initialize. When both shared one id the
            // report showed a child (2 calls, 2394 ms) totalling more than its parent (1 call,
            // 1418 ms), which is how the redundant rebuild was found. Keep them distinguishable.
            using (Profiler.Region("backdrop.createVisuals.refresh", "boot: REBUILD path (editor preview / RefreshSprites), not the Initialize path. Should not fire during play"))
            {
                roomBackdropRenderer.CreateVisuals();
                roomBackdropRenderer.SetFogOfWarDisabled(fogOfWarDisabled);
            }
        }

        private void ApplyBackgroundLayerRendering(RoomInstance room)
        {
            if (backgroundTileVisualManager == null)
            {
                return;
            }

            EnsureBackgroundTileParent();
            backgroundTileVisualManager.SetSortingOrderOffset(BackgroundTileSortingOffset);
            backgroundTileVisualManager.SetSortingLayers(MapSortingLayers.BackgroundTiles, MapSortingLayers.BackgroundTiles);
            backgroundTileVisualManager.SetTintColor(ResolveBackgroundTileTint(room));
            backgroundTileVisualManager.SetSecondaryTintColor(ResolveBackgroundTileTint(room));
        }

        private void EnsureBackgroundTileParent()
        {
            if (backgroundParent == null)
            {
                return;
            }

            if (backgroundTileParent == null)
            {
                Transform existing = backgroundParent.Find("BackgroundTiles");
                if (existing != null)
                {
                    backgroundTileParent = existing;
                }
                else
                {
                    GameObject tileLayerObject = new GameObject("BackgroundTiles");
                    tileLayerObject.transform.SetParent(backgroundParent, false);
                    backgroundTileParent = tileLayerObject.transform;
                }
            }

            backgroundTileParent.localPosition = BackgroundTileLayerLocalPosition;
            backgroundTileParent.localRotation = Quaternion.identity;
            backgroundTileParent.localScale = Vector3.one;
        }

        private void EnsureOverlayParents()
        {
            lightingParent = EnsureNamedChild(transform, lightingParent, "Lighting");
            fogOfWarParent = EnsureNamedChild(transform, fogOfWarParent, "FogOfWar");
        }

        private void EnsureObjectParents()
        {
            backgroundObjectParent = EnsureNamedChild(transform, backgroundObjectParent, "BackgroundObjects");
            backgroundPhysicalObjectParent = EnsureNamedChild(transform, backgroundPhysicalObjectParent, "BackgroundPhysicalObjects");
        }

        /// <summary>
        /// Creates the room's decal layer if it does not exist yet.
        ///
        /// <para><b>Idempotent, because <c>Initialize</c> is not called once.</b> A land transition
        /// re-enters it on the same controller, and <see cref="ClearVisuals"/> is allowed to have taken
        /// the layer down in between — so the field is the source of truth and the child lookup is only
        /// the fallback, exactly like <see cref="EnsureNamedChild"/>.</para>
        /// </summary>
        private void EnsureDecalLayer()
        {
            if (tileDecalLayer != null)
            {
                tileDecalLayer.transform.localPosition = Vector3.zero;
                return;
            }

            Transform existing = transform.Find(DecalLayerName);
            if (existing != null)
            {
                tileDecalLayer = existing.GetComponent<TileDecalLayer>();
            }

            if (tileDecalLayer == null)
            {
                var layerObject = new GameObject(DecalLayerName);
                layerObject.transform.SetParent(transform, false);
                tileDecalLayer = layerObject.AddComponent<TileDecalLayer>();
            }

            tileDecalLayer.transform.localPosition = Vector3.zero;
            tileDecalLayer.transform.localRotation = Quaternion.identity;
            tileDecalLayer.transform.localScale = Vector3.one;
        }

        private const string DecalLayerName = "TileDecals";

        private static Transform EnsureNamedChild(Transform parent, Transform current, string childName)
        {
            if (parent == null)
            {
                return null;
            }

            if (current == null)
            {
                Transform existing = parent.Find(childName);
                if (existing != null)
                {
                    current = existing;
                }
                else
                {
                    GameObject childObject = new GameObject(childName);
                    childObject.transform.SetParent(parent, false);
                    current = childObject.transform;
                }
            }

            current.localPosition = Vector3.zero;
            current.localRotation = Quaternion.identity;
            current.localScale = Vector3.one;
            return current;
        }

        private void CleanupLegacyOverlayChildren()
        {
            CleanupNamedChildren(backgroundParent, "LightingOverlay", "VisibilityMask");
            CleanupNamedChildren(lightingParent, "LightingOverlay", "VisibilityMask");
            CleanupNamedChildren(fogOfWarParent, "LightingOverlay", "VisibilityMask");
        }

        private static void CleanupNamedChildren(Transform parent, params string[] childNames)
        {
            if (parent == null || childNames == null || childNames.Length == 0)
            {
                return;
            }

            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Transform child = parent.GetChild(i);
                for (int j = 0; j < childNames.Length; j++)
                {
                    if (!string.Equals(child.name, childNames[j], System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (Application.isPlaying)
                    {
                        Destroy(child.gameObject);
                    }
                    else
                    {
                        DestroyImmediate(child.gameObject);
                    }

                    break;
                }
            }
        }

        public string GetCurrentRoomStorageKey()
        {
            if (!string.IsNullOrWhiteSpace(roomInstance?.templateId))
            {
                return roomInstance.templateId;
            }

            return roomInstance?.id ?? string.Empty;
        }

        public RoomBackdropSettingsLookup.BackdropSettings GetCurrentBackdropSettings()
        {
            return new RoomBackdropSettingsLookup.BackdropSettings
            {
                textureScale = backdropTextureScale,
                textureOffset = backdropTextureOffset,
                flipX = flipBackdropTextureX,
                flipY = flipBackdropTextureY,
                overrideGlobalTint = true,
                tint = GetCurrentBackdropTintSettings()
            };
        }

        public RoomBackdropSettingsLookup.TintSettings GetCurrentBackdropTintSettings()
        {
            return new RoomBackdropSettingsLookup.TintSettings
            {
                tint = backdropTint,
                brightness = backdropBrightness
            };
        }

        public RoomBackdropSettingsLookup.TintSettings GetCurrentBackgroundAssetTintSettings()
        {
            return new RoomBackdropSettingsLookup.TintSettings
            {
                tint = backgroundAssetTint,
                brightness = backgroundAssetBrightness
            };
        }

        public void SetBackdropSettingsLookup(RoomBackdropSettingsLookup lookup)
        {
            roomBackdropSettingsLookup = lookup;
        }

        private void ApplyStoredBackdropSettingsIfAvailable(RoomInstance room)
        {
            if (room?.environment == null || string.IsNullOrWhiteSpace(room.environment.backgroundWall))
            {
                return;
            }

            string roomKey = ResolveRoomStorageKey(room);
            if (string.IsNullOrWhiteSpace(roomKey))
            {
                return;
            }

            RoomBackdropSettingsLookup lookup = ResolveBackdropSettingsLookup();
            if (lookup == null || !lookup.TryGetBackdrop(roomKey, out RoomBackdropSettingsLookup.BackdropSettings settings))
            {
                return;
            }

            backdropTextureScale = settings.textureScale;
            backdropTextureOffset = settings.textureOffset;
            flipBackdropTextureX = settings.flipX;
            flipBackdropTextureY = settings.flipY;
            if (settings.overrideGlobalTint)
            {
                ApplyTintSettings(settings.tint, isBackdrop: true);
            }
            ClampBackdropSettings();
        }

        private void ApplyStoredRoomTintSettingsIfAvailable(RoomInstance room)
        {
            string roomKey = ResolveRoomStorageKey(room);
            if (string.IsNullOrWhiteSpace(roomKey))
            {
                return;
            }

            RoomBackdropSettingsLookup lookup = ResolveBackdropSettingsLookup();
            if (lookup == null)
            {
                return;
            }

            if (lookup.TryGetRoomBackdropTint(roomKey, out RoomBackdropSettingsLookup.TintSettings backdropTintSettings))
            {
                ApplyTintSettings(backdropTintSettings, isBackdrop: true);
            }

            if (lookup.TryGetRoomDecorationTint(roomKey, out RoomBackdropSettingsLookup.TintSettings decorationTintSettings))
            {
                ApplyTintSettings(decorationTintSettings, isBackdrop: false);
            }

            ClampBackdropSettings();
        }

        private RoomBackdropSettingsLookup ResolveBackdropSettingsLookup()
        {
            if (roomBackdropSettingsLookup != null)
            {
                return roomBackdropSettingsLookup;
            }

            roomBackdropSettingsLookup = Resources.Load<RoomBackdropSettingsLookup>(RoomBackdropSettingsLookup.ResourcesPath);
            return roomBackdropSettingsLookup;
        }

        private PfeDebugSettings ResolveDebugSettings()
        {
            if (debugSettings != null)
            {
                return debugSettings;
            }

            debugSettings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
            return debugSettings;
        }

        private void ClampBackdropSettings()
        {
            backdropTextureScale.x = Mathf.Clamp(backdropTextureScale.x, 0.1f, 4f);
            backdropTextureScale.y = Mathf.Clamp(backdropTextureScale.y, 0.1f, 4f);
            backdropTextureOffset.x = Mathf.Clamp(backdropTextureOffset.x, -2f, 2f);
            backdropTextureOffset.y = Mathf.Clamp(backdropTextureOffset.y, -2f, 2f);
            backdropBrightness = Mathf.Clamp(backdropBrightness, 0f, 2.5f);
            backdropSharpenStrength = Mathf.Clamp(backdropSharpenStrength, 0f, 2f);
            backgroundAssetBrightness = Mathf.Clamp(backgroundAssetBrightness, 0f, 2.5f);
        }

        private void ApplyTintSettings(RoomBackdropSettingsLookup.TintSettings settings, bool isBackdrop)
        {
            if (isBackdrop)
            {
                backdropTint = settings.tint;
                backdropBrightness = settings.brightness;
            }
            else
            {
                backgroundAssetTint = settings.tint;
                backgroundAssetBrightness = settings.brightness;
            }
        }

        private static string ResolveRoomStorageKey(RoomInstance room)
        {
            if (!string.IsNullOrWhiteSpace(room?.templateId))
            {
                return room.templateId;
            }

            return room?.id ?? string.Empty;
        }

        private static Color ResolveBackgroundTileTint(RoomInstance room)
        {
            return RoomBackdropRenderer.ResolveBackgroundTileTint(room?.environment);
        }

        #if UNITY_EDITOR
        private void RefreshBackdropPreviewDelayed()
        {
            if (this == null)
            {
                return;
            }

            RefreshBackdropVisuals();
        }
        #endif

        private static void CopyEditedTileFields(TileData source, TileData destination)
        {
            if (source == null || destination == null)
            {
                return;
            }

            destination.physicsType = source.physicsType;
            destination.indestructible = source.indestructible;
            destination.hitPoints = source.hitPoints;
            destination.damageThreshold = source.damageThreshold;
            destination.SetFrontGraphic(source.GetFrontGraphic());
            destination.SetBackGraphic(source.GetBackGraphic());
            destination.SetZadGraphic(source.GetZadGraphic());
            destination.visualId = source.visualId;
            destination.visualId2 = source.visualId2;
            destination.frontRear = source.frontRear;
            destination.vidRear = source.vidRear;
            destination.vid2Rear = source.vid2Rear;
            destination.opacity = source.opacity;
            destination.doorOcclusion = source.doorOcclusion;
            destination.levelTwoSolid = source.levelTwoSolid;
            destination.heightLevel = source.heightLevel;
            destination.slopeType = source.slopeType;
            destination.stairType = source.stairType;
            destination.isLedge = source.isLedge;
            destination.hasWater = source.hasWater;
            destination.lurk = source.lurk;
            destination.kontur1 = source.kontur1;
            destination.kontur2 = source.kontur2;
            destination.kontur3 = source.kontur3;
            destination.kontur4 = source.kontur4;
            destination.pontur1 = source.pontur1;
            destination.pontur2 = source.pontur2;
            destination.pontur3 = source.pontur3;
            destination.pontur4 = source.pontur4;
            destination.material = source.material;
            destination.canPlaceObjects = source.canPlaceObjects;
            destination.doorId = source.doorId;
            destination.trapId = source.trapId;
        }

        /// <summary>
        /// Clear all visual elements for this room, including tiles, backdrops, objects, triggers, and lights.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT inside <c>#if UNITY_EDITOR</c>. <see cref="Initialize"/> calls this at runtime
        /// when re-initializing an already-initialized room, so it must exist in Player builds too. It used
        /// to live inside the editor-only preview block below, which made every Player build fail with
        /// CS0103 "The name 'ClearVisuals' does not exist in the current context" — invisible in the Editor
        /// because <c>UNITY_EDITOR</c> is defined there.
        /// </remarks>
        public void ClearVisuals()
        {
            // The quarter renderer first: it owns Tilemaps, materials and tiles created for this room,
            // and RoomTerrainRenderer.Clear (run from OnDestroy) destroys exactly those. Left behind, it
            // would keep drawing the previous room's terrain through the next room's walls.
            if (roomTerrainRenderer != null)
            {
                // Cleared *before* the GameObject is destroyed, and that ordering is load-bearing:
                // `DestroyObject` is `Destroy` in play mode, which is deferred to the end of the frame,
                // so `OnDestroy` — and therefore the `TilesMutated` unsubscribe inside `Clear` — would
                // not run until after the next room had been built. Clearing here unsubscribes and sets
                // `IsBuilt` false synchronously, so the old renderer cannot service a pending region
                // into a room that is being torn down. `Clear` is idempotent, so the later OnDestroy
                // call is a no-op.
                roomTerrainRenderer.Clear();
                DestroyObject(roomTerrainRenderer.gameObject);
                roomTerrainRenderer = null;
            }

            tileVisualManager?.DestroyAllTiles();

            // The other half of "exactly one of the two owns the tiles": a room drawn by the quarter
            // renderer has no TileRenderers to destroy, and its collider-only objects are the only thing
            // standing between the next room and this one's walls.
            tileCollisionBuilder?.DestroyAll();
            tileCollisionBuilder = null;

            backgroundTileVisualManager?.DestroyAllTiles();
            roomBackdropRenderer?.DestroyVisuals();
            roomObjectVisualManager?.DestroyAll();

            // AS3 clears every decal at the top of a room redraw — `frontBmp.fillRect(this.allRect, 0)`
            // at `Grafon.as:418` — so a decal's lifetime is the room view's, not the tile's. The layer
            // object survives (EnsureDecalLayer re-finds it); only its stamped marks go.
            tileDecalLayer?.Clear();

            tileVisualManager = null;
            backgroundTileVisualManager = null;
            roomBackdropRenderer = null;
            roomObjectVisualManager = null;
            roomUnitSpawner?.DestroyAll();
            roomUnitSpawner = null;

            // Floor loot is part of the room view, so it goes with it — and unlike a prop or a unit it
            // is NOT parented to anything under this transform, so destroying the parents below does not
            // reach it. Until this call existed a drop outlived the room that owned it: it stayed as a
            // stray GameObject at the old room's coordinates and `WorldItemPickup.Live` grew for the
            // whole session. The records stay on the room (DestroyView, not Despawn), which is what makes
            // Initialize able to put them back.
            //
            // `roomInstance` is still the OLD room here — Initialize reassigns it after this call — so
            // this is exactly the room whose views must go.
            DestroyDroppedItemViews();

            visibilityRevealTargetPlayer = null;
            isInitialized = false;

            // Drop this room's published `@targ` host. Done here rather than in the area trigger system
            // itself because that object has no idea which room it was built for; this controller is
            // what made the pairing, so it is what unpairs it. `roomInstance` is still the OLD room at
            // this point (Initialize reassigns it after calling ClearVisuals), which is exactly the one
            // whose entry must go.
            PFE.Systems.Map.Scripting.ScriptSystem.Current?.UnregisterObjectHost(roomInstance);

            // The room's tile query goes with it. An adapter still holding the old room would convert
            // every later particle against a stale origin — and a stale origin is ZERO in the first
            // room, so the mistake is invisible in exactly the room most likely to be tested.
            particleTileWater?.SetTileQuery(null);
            particleEmitter?.SetTileQuery(null);
            // Nothing is drawn any more, so the room must stop claiming to be visible. Without this
            // IsVisible stayed true after a clear even though every visual had been destroyed.
            isVisible = false;

            DestroyChildren(tileParent);
            DestroyChildren(backgroundParent);
            DestroyChildren(backgroundTileParent);
            DestroyChildren(backgroundObjectParent);
            DestroyChildren(backgroundPhysicalObjectParent);
            DestroyChildren(lightingParent);
            DestroyChildren(fogOfWarParent);

            Transform doorParent = transform.Find("DoorTriggers");
            DestroyChildren(doorParent);
        }

        /// <summary>
        /// Destroy every <see cref="WorldItemPickup"/> view whose record belongs to <see cref="roomInstance"/>,
        /// keeping the records on the room.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is a named step and not part of <see cref="DestroyChildren"/>.</b> Every other
        /// visual this controller owns is a child of one of its parent transforms, so destroying the
        /// parents sweeps it up. A pickup is a <i>root</i> GameObject — it is built by
        /// <c>new GameObject</c> in <c>WorldItemPickup.SpawnCore</c> and parented to nothing, because it
        /// moves in world space and is found by a cursor probe rather than by hierarchy. The consequence
        /// is that the sweep does not reach it, and the only thing that ever removed one was the F2
        /// overlay's <c>DespawnAll</c>.</para>
        ///
        /// <para><b>Keeping the record is the whole point.</b> <see cref="WorldItemPickup.DestroyView"/>
        /// is the "the view is going, the fact stays" half; <see cref="WorldItemPickup.Despawn"/> is the
        /// other one and is what taking an item calls. Swapping them here would either delete a room's
        /// loot every time the player walked out or duplicate it on re-entry.</para>
        /// </remarks>
        private void DestroyDroppedItemViews()
        {
            WorldItemPickup.DestroyViewsForRoom(roomInstance);
        }

        /// <summary>
        /// <c>Destroy</c> throws in edit mode, and the editor preview path builds rooms outside play
        /// mode. Same seam, and same reason, as <c>TileRenderer.DestroyTile</c> and
        /// <c>RoomTerrainRenderer.DestroyObject</c>.
        /// <para><c>new</c> is required: <c>UnityEngine.Object</c> has a deprecated
        /// <c>DestroyObject</c> of its own, and without it every call here would be a CS0108. The
        /// two sibling helpers above omit it and carry that warning; this one does not have to.</para>
        /// </summary>
        private static new void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }

        private static void DestroyChildren(Transform parent)
        {
            if (parent == null)
            {
                return;
            }

            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.GetChild(i).gameObject;
                if (Application.isPlaying)
                {
                    Destroy(child);
                }
                else
                {
                    DestroyImmediate(child);
                }
            }
        }

        #if UNITY_EDITOR
        public RoomTemplate PreviewTemplate
        {
            get => previewTemplate;
            set => previewTemplate = value;
        }

        public void LoadPreviewRoom()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[RoomVisualController] Preview loading is editor-only. Stop play mode to use it.");
                return;
            }

            if (previewTemplate == null)
            {
                Debug.LogWarning("[RoomVisualController] No preview template selected.");
                return;
            }

            if (tileAssetDatabase == null)
            {
                Debug.LogError("[RoomVisualController] TileAssetDatabase is required for preview loading.");
                return;
            }

            if (tileFormDatabase == null)
            {
                Debug.LogError("[RoomVisualController] TileFormDatabase is required for preview loading.");
                return;
            }

            RoomInstance previewRoom = BuildPreviewRoom(previewTemplate, previewLandPosition);
            if (previewRoom == null)
            {
                Debug.LogError($"[RoomVisualController] Failed to build preview room for template '{previewTemplate.name}'.");
                return;
            }

            ClearPreviewVisuals();
            Initialize(previewRoom, tileAssetDatabase);
            UnityEditor.EditorUtility.SetDirty(this);
        }

        public void ClearPreviewRoom()
        {
            if (Application.isPlaying)
            {
                return;
            }

            ClearPreviewVisuals();
            roomInstance = null;
            isInitialized = false;
            UnityEditor.EditorUtility.SetDirty(this);
        }

        private RoomInstance BuildPreviewRoom(RoomTemplate template, Vector3Int landPosition)
        {
            RoomGenerator generator = new RoomGenerator(tileFormDatabase);
            RoomInstance room = generator.GenerateRoom(template, landPosition);
            if (room == null)
            {
                return null;
            }

            // Editor preview builds one room with no neighbours, so no door is ever drawn. Open the whole
            // candidate mask so the preview still shows the room's doorways.
            RoomSetup.ActivateAllCandidateDoors(room);
            RoomSetup.FinalizeRoom(room, template);

            if (!string.IsNullOrEmpty(template.backgroundRoomId))
            {
                RoomTemplate backgroundTemplate = LoadRoomTemplateById(template.backgroundRoomId, template);
                if (backgroundTemplate != null)
                {
                    RoomInstance backgroundRoom = generator.GenerateRoom(backgroundTemplate, landPosition);
                    RoomSetup.ActivateAllCandidateDoors(backgroundRoom);
                    RoomSetup.FinalizeRoom(backgroundRoom, backgroundTemplate);
                    backgroundRoom.roomType = "back";
                    room.backgroundRoom = backgroundRoom;
                    room.hasBackgroundLayer = true;
                }
                else
                {
                    Debug.LogWarning($"[RoomVisualController] Preview background room template '{template.backgroundRoomId}' was not found for '{template.id}'.");
                }
            }

            return room;
        }

        private void ClearPreviewVisuals()
        {
            ClearVisuals();
        }

        private static RoomTemplate LoadRoomTemplateById(string templateId, RoomTemplate context = null)
        {
            if (string.IsNullOrWhiteSpace(templateId))
            {
                return null;
            }

            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:RoomTemplate", new[] { "Assets/_PFE/Data/Resources/Rooms" });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
                RoomTemplate template = UnityEditor.AssetDatabase.LoadAssetAtPath<RoomTemplate>(path);
                if (template != null && template.GetContentId() == templateId)
                {
                    return template;
                }
            }

            if (context != null && !string.IsNullOrWhiteSpace(context.sourceCollectionId))
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
                    RoomTemplate template = UnityEditor.AssetDatabase.LoadAssetAtPath<RoomTemplate>(path);
                    if (template != null &&
                        template.id == templateId &&
                        template.sourceCollectionId == context.sourceCollectionId)
                    {
                        return template;
                    }
                }
            }

            for (int i = 0; i < guids.Length; i++)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
                RoomTemplate template = UnityEditor.AssetDatabase.LoadAssetAtPath<RoomTemplate>(path);
                if (template != null && template.id == templateId)
                {
                    return template;
                }
            }

            return null;
        }
        #endif

        #if UNITY_EDITOR
        private static GUIStyle _debugLabelStyle;

        /// <summary>
        /// Draw room bounds in editor.
        /// </summary>
        private void OnDrawGizmos()
        {
            if (roomInstance == null)
                return;

            // Use transform.position (which is already set to room world position)
            // rather than recalculating, to avoid mismatches
            Vector3 center = transform.position;
            // Use actual room dimensions (may be expanded due to border)
            Vector3 size = WorldCoordinates.PixelToUnity(new Vector2(
                roomInstance.width * WorldConstants.TILE_SIZE,
                roomInstance.height * WorldConstants.TILE_SIZE
            ));

            Gizmos.color = new Color(0, 1, 1, 0.3f);
            Gizmos.DrawWireCube(center, size);

            // Draw room label
            UnityEditor.Handles.Label(center, $"Room {roomInstance.landPosition}\n{roomInstance.roomType}");

            if (!showContourDebugOverlay || roomInstance.tiles == null)
            {
                return;
            }

            DrawContourDebugOverlay();
        }

        private void DrawContourDebugOverlay()
        {
            for (int x = 0; x < roomInstance.width; x++)
            {
                for (int y = 0; y < roomInstance.height; y++)
                {
                    TileData tile = roomInstance.tiles[x, y];
                    if (tile == null)
                    {
                        continue;
                    }

                    string label = BuildDebugLabel(tile);
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        continue;
                    }

                    Vector3 world = transform.position + WorldCoordinates.PixelToUnity(tile.GetBounds().center);
                    UnityEditor.Handles.Label(world, label, GetDebugLabelStyle());
                }
            }
        }

        private string BuildDebugLabel(TileData tile)
        {
            if (tile == null)
            {
                return string.Empty;
            }

            if (tile.physicsType == TilePhysicsType.Wall)
            {
                return $"{SanitizeToken(tile.GetFrontGraphic())}\n{tile.kontur1}{tile.kontur2}\n{tile.kontur3}{tile.kontur4}";
            }

            if (!string.IsNullOrWhiteSpace(tile.GetBackGraphic()))
            {
                return $"{SanitizeToken(tile.GetBackGraphic())}\n{tile.pontur1}{tile.pontur2}\n{tile.pontur3}{tile.pontur4}";
            }

            return string.Empty;
        }

        private static GUIStyle GetDebugLabelStyle()
        {
            if (_debugLabelStyle == null)
            {
                _debugLabelStyle = CreateDebugLabelStyle();
            }

            return _debugLabelStyle;
        }

        private static GUIStyle CreateDebugLabelStyle()
        {
            GUIStyle style = new GUIStyle(UnityEditor.EditorStyles.miniLabel);
            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = 10;
            style.richText = false;
            style.normal.textColor = new Color(1f, 0.95f, 0.4f, 0.95f);
            return style;
        }
        #endif

        private string BuildShortDebugString(RoomInstance room)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"room={room.id} template={room.templateId} size={room.width}x{room.height} type={room.roomType}");

            for (int y = 0; y < room.height; y++)
            {
                sb.Append("row ");
                sb.Append(y.ToString("D2"));
                sb.Append(": ");

                for (int x = 0; x < room.width; x++)
                {
                    if (x > 0)
                    {
                        sb.Append(' ');
                    }

                    sb.Append(BuildShortTileToken(room.tiles[x, y]));
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string BuildLongDebugString(RoomInstance room)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"room={room.id} template={room.templateId} size={room.width}x{room.height} type={room.roomType}");

            for (int y = 0; y < room.height; y++)
            {
                for (int x = 0; x < room.width; x++)
                {
                    TileData tile = room.tiles[x, y];
                    if (tile == null)
                    {
                        sb.Append('[').Append(x).Append(',').Append(y).AppendLine("] null");
                        continue;
                    }

                    MaterialRenderEntry frontMaterial = materialRenderDb != null ? materialRenderDb.GetFrontMaterial(tile.GetFrontGraphic()) : null;
                    MaterialRenderEntry backMaterial = materialRenderDb != null ? materialRenderDb.GetBackMaterial(tile.GetBackGraphic()) : null;

                    sb.Append('[').Append(x).Append(',').Append(y).Append("] ");
                    sb.Append("phys=").Append(tile.physicsType);
                    sb.Append(" front=").Append(SanitizeToken(tile.GetFrontGraphic()));
                    sb.Append(" back=").Append(SanitizeToken(tile.GetBackGraphic()));
                    sb.Append(" k=").Append(tile.kontur1).Append(tile.kontur2).Append(tile.kontur3).Append(tile.kontur4);
                    sb.Append(" p=").Append(tile.pontur1).Append(tile.pontur2).Append(tile.pontur3).Append(tile.pontur4);
                    sb.Append(" h=").Append(tile.heightLevel);
                    sb.Append(" v1=").Append(tile.visualId);
                    sb.Append(" v2=").Append(tile.visualId2);
                    sb.Append(" frontMask=").Append(SanitizeToken(frontMaterial?.mainMask));
                    sb.Append(" borderMask=").Append(SanitizeToken(frontMaterial?.borderMask));
                    sb.Append(" floorMask=").Append(SanitizeToken(frontMaterial?.floorMask));
                    sb.Append(" backMask=").Append(SanitizeToken(backMaterial?.mainMask));
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        private string BuildShortTileToken(TileData tile)
        {
            if (tile == null)
            {
                return "null";
            }

            if (tile.physicsType == TilePhysicsType.Wall)
            {
                return $"{SanitizeToken(tile.GetFrontGraphic())}[{tile.kontur1}{tile.kontur2}{tile.kontur3}{tile.kontur4}]";
            }

            if (!string.IsNullOrWhiteSpace(tile.GetBackGraphic()))
            {
                return $"{SanitizeToken(tile.GetBackGraphic())}<{tile.pontur1}{tile.pontur2}{tile.pontur3}{tile.pontur4}>";
            }

            return "_";
        }

        private static string SanitizeToken(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "_" : value.Replace(' ', '_');
        }
    }
}
