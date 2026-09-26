using UnityEngine;

namespace PFE.Core
{
    /// <summary>
    /// Centralized runtime logging toggles for PFE systems.
    /// Keep high-signal lifecycle logs enabled by default and make verbose diagnostics opt-in.
    /// </summary>
    [CreateAssetMenu(fileName = "PfeDebugSettings", menuName = "PFE/Debug Settings")]
    public sealed class PfeDebugSettings : ScriptableObject
    {
        [Header("Global")]
        [SerializeField]
        [Tooltip("Master toggle — disabling this silences all optional runtime debug logs at once.")]
        private bool runtimeLoggingEnabled = true;

        // ── Profiling ────────────────────────────────────────────────────────

        [Header("Profiling")]
        [SerializeField]
        [Tooltip("Enables PfeProfiler regions. Off = every Region()/Mark() call is a single branch and records nothing. Development builds only.")]
        private bool profilingEnabled = true;

        // ── Game Database ────────────────────────────────────────────────────

        [Header("Game Database")]
        [SerializeField]
        [Tooltip("Logs database initialization progress and loaded asset counts.")]
        private bool logGameDatabaseInitializationSummary = true;

        [SerializeField]
        [Tooltip("Logs every asset registered into the game database.")]
        private bool logGameDatabaseAssetRegistration = false;

        [SerializeField]
        [Tooltip("Logs duplicate database registration warnings when multiple assets share the same ID.")]
        private bool logGameDatabaseDuplicateRegistrationWarnings = false;

        // ── Room Template Loading ────────────────────────────────────────────

        [Header("Room Template Loading")]
        [SerializeField]
        [Tooltip("Logs room template load totals during startup.")]
        private bool logRoomTemplateLoadSummary = false;

        [SerializeField]
        [Tooltip("Logs path-by-path room template loading diagnostics.")]
        private bool logRoomTemplateLoadDiagnostics = false;

        [SerializeField]
        [Tooltip("Logs each loaded room template by name and ID.")]
        private bool logLoadedRoomTemplateList = false;

        // ── VContainer / Dependency Injection ────────────────────────────────

        [Header("VContainer / Dependency Injection")]
        [SerializeField]
        [Tooltip("Logs Construct() calls when VContainer injects MonoBehaviours. Disable once wiring is verified — these fire twice per component due to AutoInjectAll.")]
        private bool logDependencyInjectionConstruct = false;

        // ── Input ────────────────────────────────────────────────────────────

        [Header("Input Events")]
        [SerializeField]
        [Tooltip("Logs every attack/action input event from InputReader. Very noisy during gameplay.")]
        private bool logInputActionEvents = false;

        // ── Weapon / Combat ──────────────────────────────────────────────────

        [Header("Weapon / Combat")]
        [SerializeField]
        [Tooltip("Logs WeaponView initialization, BeginFiring/EndFiring, and AttackMessage delivery to PlayerController.")]
        private bool logWeaponLifecycle = false;

        [SerializeField]
        [Tooltip("Logs every Shoot() attempt with Fire result and remaining ammo. Very noisy during gameplay.")]
        private bool logWeaponFiring = false;

        [SerializeField]
        [Tooltip("Logs new weapon-controller flow: attack forwarding, FixedUpdate ticks, t_attack arming, and ShotPlan creation. Use when input arrives but shots never materialize.")]
        private bool logWeaponControllerDiagnostics = false;

        [SerializeField]
        [Tooltip("Logs projectile creation from ProjectileFactory (archetype, position, direction).")]
        private bool logProjectileSpawning = false;

        [SerializeField]
        [Tooltip("Logs pooled projectile lifecycle: Initialize, ApplyVisual, damage-context assignment, and return-to-pool. Use to confirm pooled bullets really activate.")]
        private bool logProjectileLifecycle = false;

        // ── Game Manager / Core ──────────────────────────────────────────────

        [Header("Game Manager / Core")]
        [SerializeField]
        [Tooltip("Logs the game init sequence: Initializing, Building world, World built, Game initialized. Disable once startup is stable.")]
        private bool logGameManagerLifecycle = true;

        [SerializeField]
        [Tooltip("Logs GameLoopManager pause and resume events.")]
        private bool logGameLoopEvents = false;

        // ── Map Generation ───────────────────────────────────────────────────

        [Header("Map Generation")]
        [SerializeField]
        [Tooltip("Logs WorldBuilder room count and door connection summary after world build.")]
        private bool logWorldBuilderSummary = true;

        [SerializeField]
        [Tooltip("Logs per-type room template breakdown from MapDiagnostics (10+ lines per run).")]
        private bool logMapGenerationDiagnostics = false;

        [SerializeField]
        [Tooltip("Logs room template duplicate IDs, selected map-build path, and edge-hole summaries around map generation.")]
        private bool logMapIntegrityDiagnostics = true;

        [SerializeField]
        [Tooltip("Optional debug override: build this room collection as a fixed/specific map, e.g. Base or Camp. Empty uses normal random generation.")]
        private string debugSpecificRoomCollection = "";

        // ── Map Bridge ───────────────────────────────────────────────────────

        [Header("Map Bridge")]
        [SerializeField]
        [Tooltip("Logs MapBridge initialization coroutine steps, room info, player spawn, and camera setup.")]
        private bool logMapBridgeLifecycle = false;

        // ── Map Rendering ────────────────────────────────────────────────────

        [Header("Map Rendering")]
        [SerializeField]
        [Tooltip("Logs room visual initialization lifecycle messages (RoomVisualController).")]
        private bool logRoomRenderingLifecycle = false;

        [SerializeField]
        [Tooltip("Logs tile generation summaries for each rendered room (TileVisualManager).")]
        private bool logTileVisualCreationSummary = false;

        [SerializeField]
        [Tooltip("Logs each generated tile collider (TileCollider).")]
        private bool logTileColliderCreation = false;

        // ── Area Triggers ────────────────────────────────────────────────────

        [Header("Debug Visual Overlays")]
        [SerializeField]
        [Tooltip("Draws purple debug visual overlays for area trigger zones in Scene and Game views (matches Flash AS3 World.w.showArea, Hotkey: F8).")]
        private bool showAreaTriggerDebug = false;

        [SerializeField]
        [Tooltip("Draws yellow/cyan debug visual overlays for door interaction triggers and boundary room transitions (Hotkey: F9).")]
        private bool showDoorColliderDebug = false;

        [SerializeField]
        [Tooltip("Draws teal debug visual overlays for interactive/physical objects, barricades, containers (Hotkey: F10).")]
        private bool showObjectColliderDebug = false;

        // ── Simulation Tick (P1) ─────────────────────────────────────────────

        [Header("Simulation Tick (P1)")]
        [SerializeField]
        [Tooltip("Enables dispatch of the fixed-step simulation (SimLoop -> ISimTickable). Accumulation and the overlay keep running when this is off. Behaviour toggle, NOT gated by runtimeLoggingEnabled.")]
        private bool simTickEnabled = true;

        [SerializeField]
        [Tooltip("Draws the SimLoop timing readout (rate, tick index, accumulator, alpha, dropped ticks). Display toggle, NOT gated by runtimeLoggingEnabled.")]
        private bool simTickOverlayEnabled = false;

        [SerializeField]
        [Tooltip("Stage B dual-run harness: runs the legacy and sim paths side by side and compares state. Behaviour toggle, NOT gated by runtimeLoggingEnabled. Must be off in a shipped build.")]
        private bool simTickDualRun = false;

        [SerializeField]
        [Tooltip("Drives TilePhysicsController from SimLoop at the configured tick rate instead of Unity's FixedUpdate. OFF = legacy path, byte-identical to the pre-P1 behaviour (which runs ~2x fast). Turn ON to feel the tick fix. Behaviour toggle, NOT gated by runtimeLoggingEnabled.")]
        private bool simTickMotor = false;

        [SerializeField]
        [Tooltip("Drives the room heartbeat (LandMap -> RoomInstance -> RoomObjectPhysicsLayer) from SimLoop at the configured tick rate instead of Unity's per-frame ITickable. OFF = per-frame driver with the historical hardcoded 1/60 step, so simulated time advances by (fps / 60) per real second: correct only at exactly 60 fps, 2.4x fast at 144 fps and 2x SLOW at 30 fps. ON = one step per sim tick at exactly SimClock.SimDt, i.e. AS3's 30 fps, independent of the display. The prop physics constants are identical in both modes; only the clock differs. Behaviour toggle, NOT gated by runtimeLoggingEnabled.")]
        private bool simTickRoom = false;

        [SerializeField]
        [Tooltip("Logs each field-level divergence found by the dual-run harness, with tick index. Very noisy — opt in only while diffing Stage B.")]
        private bool simTickLogDivergence = false;

        // ── Tile Collision Query (P2) ────────────────────────────────────────

        [Header("Tile Collision Query (P2)")]
        [SerializeField]
        [Tooltip("Enables the UnifiedTileQueryService seam for tile collision consumers. Retained as the escape hatch for the P2 migration; Unified is the only implementation left, so this is a display/behaviour toggle only.")]
        private bool tileQueryUnified = true;

        // ── Public accessors ─────────────────────────────────────────────────

        /// <summary>
        /// Not gated by <c>runtimeLoggingEnabled</c> — profiling is a measurement tool, not a log,
        /// and is separately gated to development builds inside PfeProfiler.
        /// </summary>
        public bool ProfilingEnabled                             => profilingEnabled;

        public bool LogGameDatabaseInitializationSummary         => runtimeLoggingEnabled && logGameDatabaseInitializationSummary;
        public bool LogGameDatabaseAssetRegistration             => runtimeLoggingEnabled && logGameDatabaseAssetRegistration;
        public bool LogGameDatabaseDuplicateRegistrationWarnings => runtimeLoggingEnabled && logGameDatabaseDuplicateRegistrationWarnings;
        public bool LogRoomTemplateLoadSummary                   => runtimeLoggingEnabled && logRoomTemplateLoadSummary;
        public bool LogRoomTemplateLoadDiagnostics               => runtimeLoggingEnabled && logRoomTemplateLoadDiagnostics;
        public bool LogLoadedRoomTemplateList                    => runtimeLoggingEnabled && logLoadedRoomTemplateList;
        public bool LogDependencyInjectionConstruct              => runtimeLoggingEnabled && logDependencyInjectionConstruct;
        public bool LogInputActionEvents                         => runtimeLoggingEnabled && logInputActionEvents;
        public bool LogWeaponLifecycle                           => runtimeLoggingEnabled && logWeaponLifecycle;
        public bool LogWeaponFiring                              => runtimeLoggingEnabled && logWeaponFiring;
        public bool LogWeaponControllerDiagnostics               => runtimeLoggingEnabled && logWeaponControllerDiagnostics;
        public bool LogProjectileSpawning                        => runtimeLoggingEnabled && logProjectileSpawning;
        public bool LogProjectileLifecycle                       => runtimeLoggingEnabled && logProjectileLifecycle;
        public bool LogGameManagerLifecycle                      => runtimeLoggingEnabled && logGameManagerLifecycle;
        public bool LogGameLoopEvents                            => runtimeLoggingEnabled && logGameLoopEvents;
        public bool LogWorldBuilderSummary                       => runtimeLoggingEnabled && logWorldBuilderSummary;
        public bool LogMapGenerationDiagnostics                  => runtimeLoggingEnabled && logMapGenerationDiagnostics;
        public bool LogMapIntegrityDiagnostics                   => runtimeLoggingEnabled && logMapIntegrityDiagnostics;
        public string DebugSpecificRoomCollection                => debugSpecificRoomCollection;
        public bool LogMapBridgeLifecycle                        => runtimeLoggingEnabled && logMapBridgeLifecycle;
        public bool LogRoomRenderingLifecycle                    => runtimeLoggingEnabled && logRoomRenderingLifecycle;
        public bool LogTileVisualCreationSummary                 => runtimeLoggingEnabled && logTileVisualCreationSummary;
        public bool LogTileColliderCreation                      => runtimeLoggingEnabled && logTileColliderCreation;

        // Visual debug toggles are NOT gated by runtimeLoggingEnabled
        public bool ShowAreaTriggerDebug
        {
            get => showAreaTriggerDebug;
            set => showAreaTriggerDebug = value;
        }

        public bool ShowDoorColliderDebug
        {
            get => showDoorColliderDebug;
            set => showDoorColliderDebug = value;
        }

        public bool ShowObjectColliderDebug
        {
            get => showObjectColliderDebug;
            set => showObjectColliderDebug = value;
        }

        // Simulation Tick flags are deliberately NOT gated by runtimeLoggingEnabled: the master
        // toggle silences logs, and must never be able to change gameplay or hide the overlay.
        public bool SimTickEnabled                               => simTickEnabled;
        public bool SimTickOverlayEnabled                        => simTickOverlayEnabled;
        public bool SimTickDualRun                               => simTickDualRun;
        public bool SimTickMotor                                 => simTickMotor;
        public bool SimTickRoom                                  => simTickRoom;

        /// <summary>Divergence logging is a log, so it does respect the master toggle.</summary>
        public bool SimTickLogDivergence                         => runtimeLoggingEnabled && simTickLogDivergence;

        // Tile Query flags (P2)
        public bool TileQueryUnified                             => tileQueryUnified;

        // RNG & Entity ID flags (P3)
        [Header("RNG & Entity IDs (P3)")]
        [SerializeField]
        [Tooltip("If non-zero, overrides the session seed with a fixed deterministic seed. 0 = dynamic seed.")]
        private int rngSeedOverride = 0;

        [SerializeField]
        [Tooltip("Logs RNG call sites and outcomes for debugging determinism.")]
        private bool rngLogCallSites = false;

        [SerializeField]
        [Tooltip("Logs deterministic entity ID assignment at spawn time.")]
        private bool logEntityIdAssignment = false;

        [SerializeField]
        [Tooltip("Renders debug overlay of runtime entity IDs above characters/objects.")]
        private bool showEntityIdOverlay = false;

        public int RngSeedOverride                               => rngSeedOverride;
        public bool RngLogCallSites                              => runtimeLoggingEnabled && rngLogCallSites;
        public bool LogEntityIdAssignment                        => runtimeLoggingEnabled && logEntityIdAssignment;
        public bool ShowEntityIdOverlay
        {
            get => showEntityIdOverlay;
            set => showEntityIdOverlay = value;
        }
    }
}
