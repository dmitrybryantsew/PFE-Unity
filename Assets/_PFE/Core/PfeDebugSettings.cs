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

        // ── Public accessors ─────────────────────────────────────────────────

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
        public bool LogMapBridgeLifecycle                        => runtimeLoggingEnabled && logMapBridgeLifecycle;
        public bool LogRoomRenderingLifecycle                    => runtimeLoggingEnabled && logRoomRenderingLifecycle;
        public bool LogTileVisualCreationSummary                 => runtimeLoggingEnabled && logTileVisualCreationSummary;
        public bool LogTileColliderCreation                      => runtimeLoggingEnabled && logTileColliderCreation;
    }
}
