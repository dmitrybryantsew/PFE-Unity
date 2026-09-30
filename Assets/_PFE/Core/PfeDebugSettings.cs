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
        [Tooltip("Logs AttackMessage delivery to PlayerController, and the weapon controller's BeginAttack/EndAttack.")]
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

        // ── Debug Visual Overlays ────────────────────────────────────────────
        //
        // ONE mask, not a dozen booleans. The requirement is "each one can be set on, or all at once,
        // or several of them", which is a set. The mask is also the single value behind three front
        // ends — the Inspector, the developer console (`col on doors,triggers`) and the F5/F6/F8/F9/F10
        // hotkeys — so the three cannot disagree.
        //
        // The per-feature accessors further down (ShowAreaTriggerDebug, ShowDoorColliderDebug,
        // ShowObjectColliderDebug, SimTickOverlayEnabled, ...) are now facades over this field. They
        // are kept deliberately: AreaTriggerPresenter, DoorPropPresenter, DoorTrigger,
        // ObjectColliderDebugPresenter, ColliderDebugOverlay and SimLoop all read them, and rewriting
        // six working call sites to reach into a mask would add risk for no gain.
        //
        // DEFAULT IS None — the game and nothing else. Three of these overlays used to draw
        // unconditionally in a development build, which is how a debugging aid becomes permanent
        // screen clutter nobody can switch off. Display only: never gated by runtimeLoggingEnabled,
        // because silencing logs must not be able to hide the thing you turned on to look at.

        [Header("Debug Visual Overlays (console: `col on <name>`, hotkeys F5/F6/F8/F9/F10)")]
        [SerializeField]
        [Tooltip("Which debug visualisations are drawn. Default None = the game, and nothing else. " +
                 "Every entry is independent: turning one on never turns another off. " +
                 "Console: `col on doors,triggers`, `col off`, `col` to list.")]
        private DebugOverlayChannel enabledOverlays = DebugOverlayChannel.None;

        [SerializeField]
        [Tooltip("Which tile physics types the tile-collider overlay draws (only consulted while the Tiles " +
                 "overlay is on). A None filter turns the overlay off.")]
        private ColliderDebugTileFilter tileColliderFilter = ColliderDebugTileFilter.All;

        [SerializeField]
        [Tooltip("Which units the unit-collider overlay draws (only consulted while the Units overlay is on). " +
                 "A None filter turns the overlay off.")]
        private ColliderDebugUnitFilter unitColliderFilter = ColliderDebugUnitFilter.All;

        // ── Simulation Tick (P1) ─────────────────────────────────────────────

        [Header("Simulation Tick (P1)")]
        [SerializeField]
        [Tooltip("Enables dispatch of the fixed-step simulation (SimLoop -> ISimTickable). Accumulation and the overlay keep running when this is off. Behaviour toggle, NOT gated by runtimeLoggingEnabled.")]
        private bool simTickEnabled = true;

        // The SIM CLOCK readout's on/off now lives in `enabledOverlays` (channel Clock) so that it is
        // reachable from the same console list as every other overlay. SimTickOverlayEnabled below is
        // a facade over it, which is what SimLoop and SimDebugOverlay still read.

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
        [Tooltip("Drives damage resolution from SimLoop at SimTickOrder.Damage instead of resolving each hit inline at the moment of contact. " +
                 "OFF = a hit is resolved by the source that reported it, immediately (the historical path). " +
                 "ON = the source only records the hit, and DamageSystem resolves it once per tick after every source has moved — which is what makes a hit reproducible and replicable across peers, because the result stops depending on the order the physics engine reported contacts. " +
                 "The formula is armour-aware in BOTH modes; this flag chooses only WHEN it runs. With no unit carrying armour the two modes give identical numbers, so it is safe to flip while play-testing. " +
                 "Ignored (falls back to immediate) while SimTickEnabled is off, because nothing would drain the queue. Behaviour toggle, NOT gated by runtimeLoggingEnabled.")]
        private bool simTickDamage = false;

        [SerializeField]
        [Tooltip("Stage C flip: moves a projectile's TILE collision onto the LowLevelPhysics2D chain mirror, and its integration onto SimLoop at exactly SimClock.SimDt (AS3's 30 Hz). " +
                 "OFF = unchanged: tile hits come from Unity's per-tile BoxCollider2D grid via OnTriggerEnter2D, and the projectile integrates in FixedUpdate at Time.fixedDeltaTime. " +
                 "ON = the hit decision comes from a swept query against the chain mirror (single-sourced from ITileQueryService, no ghost collisions at tile seams), and the projectile integrates once per sim tick. " +
                 "Entity hits (IDamageable) are unaffected in both modes — they still come from Unity colliders. Behaviour toggle, NOT gated by runtimeLoggingEnabled. This is the Stage C rollback switch; it stays until Stage D removes the old path.")]
        private bool projectilesUseLowLevelPhysics = false;

        [SerializeField]
        [Tooltip("Stage C flip: moves a thrown object's (grenade, bottle, sticky bomb) TILE collision onto the tile-query seam and its integration onto SimLoop at exactly SimClock.SimDt (AS3's 30 Hz). " +
                 "OFF = unchanged: tile hits come from Unity's per-tile BoxCollider2D grid via OnTriggerEnter2D, and the object integrates in FixedUpdate at Time.fixedDeltaTime (50 Hz, i.e. 1.67x fast). " +
                 "ON = the hit decision comes from the tile seam, reproduced from AS3 PhisBullet.run() exactly — a point swept in sub-steps of at most World.maxdelta (9 px), X resolved before Y, bounce retention skok=0.4, floor damping tormoz=0.6, and settle below 2 px/frame of fall speed. " +
                 "Entity hits (IDamageable) and the AoE detonation are unaffected in both modes. The fuse is 72 canonical frames either way, so it burns for 2.4 s with the flag on and 1.44 s with it off — the same 1.67x clock error the rest of the legacy path has. Behaviour toggle, NOT gated by runtimeLoggingEnabled. This is the Stage C rollback switch; it stays until Stage D removes the old path.")]
        private bool thrownObjectsUseTileSeam = false;

        [SerializeField]
        [Tooltip("Logs each field-level divergence found by the dual-run harness, with tick index. Very noisy — opt in only while diffing Stage B.")]
        private bool simTickLogDivergence = false;

        // ── Damage Formula ───────────────────────────────────────────────────

        [SerializeField]
        [Tooltip("Applies the target's vulnerability table to incoming damage (AS3 Unit.vulner, applied at Unit.damage():3527-3530). " +
                 "OFF = the term does not exist, every hit is multiplied by 1, and the numbers are byte-identical to the pre-A7 behaviour. " +
                 "ON = each hit is multiplied by the target's multiplier for its own damage type, BEFORE the armour pool is worn — which is the oracle's order (:3529 precedes the pool block at :3578). " +
                 "Gameplay-affecting, in one specific way: AS3's baseline is 1 everywhere EXCEPT emp = 0 (Unit.as:583-590), so every unit becomes EMP-immune until a <vulner> element grants it back — 25 of the 94 elements do. " +
                 "The table comes from UnitDefinition.vulnerabilities, which is imported but has zero other combat callers, so this flag is what makes that data live. " +
                 "Behaviour toggle, NOT gated by runtimeLoggingEnabled.")]
        private bool applyVulnerabilities = false;

        [SerializeField]
        [Tooltip("AS3 World.w.testDam (World.as:202, flipped by the console at Consol.as:475) — reports every hit's listed damage instead of the rolled value, removing the 0.7-1.3 spread (Unit.as:4085). " +
                 "OFF = normal play: a hit does (random() * 0.6 + 0.7) times its damage. " +
                 "ON = the spread is discarded, so a weapon's numbers are readable and reproducible while tuning — this is the toggle to reach for when testing on the training dummies. " +
                 "The random draw STILL HAPPENS while this is on, because AS3 discards the result on the line after the roll (:4086-4089); skipping the draw would shift every later roll on the shared combat stream, so this flag changes the number shown, never the stream. " +
                 "Debug toggle, NOT gated by runtimeLoggingEnabled.")]
        private bool testDamage = false;

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

        // ── Debug overlays: the mask, and the per-feature facades over it ────
        //
        // Visual debug toggles are NOT gated by runtimeLoggingEnabled.
        //
        // Note the asymmetry with the rest of this file: these are written at runtime by the developer
        // console, so their value lives only for the play session unless the Inspector is used.
        // Nothing calls EditorUtility.SetDirty, and a ScriptableObject edit made in play mode is
        // discarded when play mode ends — which is the right default for a debug overlay.

        /// <summary>The set of debug visualisations currently drawn. The single source of truth.</summary>
        public DebugOverlayChannel EnabledOverlays
        {
            get => enabledOverlays;
            set => enabledOverlays = value;
        }

        /// <summary>Is one overlay on? The mask's own membership test, exposed for consumers.</summary>
        public bool IsOverlayEnabled(DebugOverlayChannel channel)
        {
            return (enabledOverlays & channel) != 0;
        }

        /// <summary>
        /// Turn one overlay on or off, leaving every other one exactly as it was. Independence is the
        /// contract here: tiles and units in particular must be able to be on at the same time.
        /// </summary>
        public void SetOverlay(DebugOverlayChannel channel, bool enabled)
        {
            enabledOverlays = enabled
                ? enabledOverlays | channel
                : enabledOverlays & ~channel;
        }

        // The six accessors below are facades over the mask, kept so the existing consumers
        // (AreaTriggerPresenter, DoorPropPresenter, DoorTrigger, ObjectColliderDebugPresenter,
        // ColliderDebugOverlay, SimLoop, SimDebugOverlay) need no change at all. Each is exactly the
        // membership test for one channel — there is no second copy of the state to fall out of step.

        public bool ShowAreaTriggerDebug
        {
            get => IsOverlayEnabled(DebugOverlayChannel.Triggers);
            set => SetOverlay(DebugOverlayChannel.Triggers, value);
        }

        public bool ShowDoorColliderDebug
        {
            get => IsOverlayEnabled(DebugOverlayChannel.Doors);
            set => SetOverlay(DebugOverlayChannel.Doors, value);
        }

        public bool ShowObjectColliderDebug
        {
            get => IsOverlayEnabled(DebugOverlayChannel.Objects);
            set => SetOverlay(DebugOverlayChannel.Objects, value);
        }

        public bool ShowTileColliderDebug
        {
            get => IsOverlayEnabled(DebugOverlayChannel.Tiles);
            set => SetOverlay(DebugOverlayChannel.Tiles, value);
        }

        public ColliderDebugTileFilter TileColliderFilter
        {
            get => tileColliderFilter;
            set => tileColliderFilter = value;
        }

        public bool ShowUnitColliderDebug
        {
            get => IsOverlayEnabled(DebugOverlayChannel.Units);
            set => SetOverlay(DebugOverlayChannel.Units, value);
        }

        public ColliderDebugUnitFilter UnitColliderFilter
        {
            get => unitColliderFilter;
            set => unitColliderFilter = value;
        }

        // Simulation Tick flags are deliberately NOT gated by runtimeLoggingEnabled: the master
        // toggle silences logs, and must never be able to change gameplay or hide the overlay.
        public bool SimTickEnabled                               => simTickEnabled;

        /// <summary>
        /// The SIM CLOCK readout. Now a facade over <see cref="DebugOverlayChannel.Clock"/> so that
        /// <c>col on all</c> reaches it like every other overlay; SimLoop and SimDebugOverlay are the
        /// two consumers and neither needed changing.
        /// </summary>
        public bool SimTickOverlayEnabled                        => IsOverlayEnabled(DebugOverlayChannel.Clock);

        public bool SimTickDualRun                               => simTickDualRun;
        public bool SimTickMotor                                 => simTickMotor;
        public bool SimTickRoom                                  => simTickRoom;

        /// <summary>
        /// Whether damage resolves on the tick (<c>SimTickOrder.Damage</c>) rather than inline at
        /// report time. See <c>DamageSystem</c>; it also requires <see cref="SimTickEnabled"/>, because
        /// a queued hit with nothing to drain it would never land.
        /// </summary>
        public bool SimTickDamage                                => simTickDamage;

        /// <summary>
        /// Whether a target's vulnerability table is applied to incoming damage. See
        /// <c>DamageSystem</c>; the data it reads is <c>UnitDefinition.vulnerabilities</c>, which this
        /// flag is the only combat caller of.
        /// </summary>
        public bool ApplyVulnerabilities                         => applyVulnerabilities;

        /// <summary>
        /// AS3 <c>World.w.testDam</c> — discard the damage spread and report each hit's listed damage.
        /// Read by <c>DamageSystem</c>, which passes it to <c>DamageVariance.Roll</c>. Note it is
        /// <b>not</b> a rollback switch for the spread: it changes the number, not whether the roll is
        /// taken.
        /// </summary>
        public bool TestDamage                                   => testDamage;

        public bool ProjectilesUseLowLevelPhysics                => projectilesUseLowLevelPhysics;
        public bool ThrownObjectsUseTileSeam                     => thrownObjectsUseTileSeam;

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
