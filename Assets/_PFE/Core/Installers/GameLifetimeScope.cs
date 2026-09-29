using VContainer;
using VContainer.Unity;
using MessagePipe;
using PFE.Core;
using PFE.Core.Input;
using PFE.Core.Messages;
using PFE.Core.Time;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Physics;
using PFE.Data;
using PFE.Entities.Player;
using PFE.Systems.Weapons;
using PFE.Core.Rng;
using PFE.Core.Ids;
using PFE.Core.Scripting;
using UnityEngine;

public class GameLifetimeScope : LifetimeScope
{
    [SerializeField] private SoundService      _soundService;
    [SerializeField] private MusicService      _musicService;
    [SerializeField] private ImpactSoundTable  _impactSoundTable;
    [SerializeField] private PfeInputSettings _inputSettings;
    [SerializeField] private ProjectilePrefabRegistry _projectilePrefabRegistry;
    [SerializeField] private TileAssetDatabase tileAssetDatabase;
    [SerializeField] private TileFormDatabase _tileFormDatabase;
    [SerializeField] private TileTextureLookup _tileTextureLookup;
    [SerializeField] private MaterialRenderDatabase _materialRenderDb;
    [SerializeField] private TileMaskLookup _tileMaskLookup;
    [SerializeField] private RoomBackgroundLookup _roomBackgroundLookup;
    [SerializeField] private PfeDebugSettings _debugSettings;
    [SerializeField] private GameSettings _gameSettings;
        // VContainer builds the container inside Awake, and Configure() below runs five
        // FindFirstObjectByType scene scans plus the Resources fallbacks. Run 17 showed the
        // BeforeSceneLoad -> AfterSceneLoad window at 1018.6 ms with no region covering any of it,
        // so we could not tell engine scene-load cost from our own container-build cost.
        // LifetimeScope.Awake is protected virtual (verified in VContainer.dll), so this is safe.
        protected override void Awake()
        {
            using (PFE.Core.Profiling.PfeProfiler.Region("boot.lifetimeScope",
                "boot: VContainer container build + Configure's 5 FindFirstObjectByType scene scans + Resources fallbacks"))
            {
                base.Awake();
            }
        }

        protected override void Configure(IContainerBuilder builder)
        {
        // === MessagePipe Event System ===
        var pipe = builder.RegisterMessagePipe();
        // Input messages
        builder.RegisterMessageBroker<JumpMessage>(pipe);
        builder.RegisterMessageBroker<AttackMessage>(pipe);
        builder.RegisterMessageBroker<InteractMessage>(pipe);
        builder.RegisterMessageBroker<DashMessage>(pipe);
        builder.RegisterMessageBroker<TeleportMessage>(pipe);
        // Combat messages
        builder.RegisterMessageBroker<DamageDealtMessage>(pipe);
        builder.RegisterMessageBroker<DamageTakenMessage>(pipe);
        // HealMessage was declared in GameMessages.cs and consumed by
        // PFE.Systems.Combat.FloatingTextManager, but never registered — so every heal published
        // into a broker that did not exist and no floating "+N" ever appeared. Registering the
        // broker is the whole fix; both publisher and subscriber already existed.
        builder.RegisterMessageBroker<HealMessage>(pipe);
        builder.RegisterMessageBroker<WeaponFiredMessage>(pipe);
        builder.RegisterMessageBroker<WeaponReloadStartedMessage>(pipe);
        builder.RegisterMessageBroker<WeaponReloadCompletedMessage>(pipe);
        builder.RegisterMessageBroker<WeaponDurabilityChangedMessage>(pipe);
        // Map messages
        builder.RegisterMessageBroker<LandTransitionMessage>(pipe);
        builder.RegisterMessageBroker<TutorialPromptMessage>(pipe);
        builder.RegisterMessageBroker<ObjectiveMarkerMessage>(pipe);

        // === Audio System ===
        // SoundService is a MonoBehaviour — assign it in the scene and reference here.
        if (_soundService != null)
            builder.RegisterComponent(_soundService).AsImplementedInterfaces();
        else
            Debug.LogWarning("[GameLifetimeScope] SoundService not assigned — audio will be silent. Add a SoundService component to the scene and assign it.");

        if (_impactSoundTable != null)
            builder.RegisterInstance(_impactSoundTable);
        else
            Debug.LogWarning("[GameLifetimeScope] ImpactSoundTable not assigned — surface impact sounds will be silent. Run PFE/Import/Import Sounds to create the asset.");

        if (_musicService != null)
            builder.RegisterComponent(_musicService).AsImplementedInterfaces();
        else
            Debug.LogWarning("[GameLifetimeScope] MusicService not assigned — music will be silent. Add a MusicService component to the AudioManager and assign it.");

        // === Time System ===
        builder.Register<ITimeProvider, UnityTimeProvider>(Lifetime.Singleton);

        // === Deterministic PRNG System (P3) ===
        ulong masterSeed = 0x853c49e6748fea9bUL;
        if (_debugSettings != null && _debugSettings.RngSeedOverride != 0)
        {
            masterSeed = (ulong)_debugSettings.RngSeedOverride;
        }
        builder.RegisterInstance<IRngService>(new PcgRngService(masterSeed));

        // === Entity Registry (P3) ===
        builder.Register<IEntityRegistry, EntityRegistry>(Lifetime.Singleton);

        // === Lua Scripting Engine & Developer Console ===
        builder.Register<ILuaEngine, MoonSharpScriptEngine>(Lifetime.Singleton);
        builder.Register<DeveloperConsoleService>(Lifetime.Singleton).AsSelf();
        builder.RegisterComponentOnNewGameObject<DeveloperConsoleController>(Lifetime.Singleton, "DeveloperConsole");

        // === Instruments (Phase 1) ===
        // Both are built at runtime rather than authored into the scene. Neither exists in
        // SampleScene today, and both need data that only exists at runtime (the player's UnitStats,
        // the loadout's equipped controller), so there is nothing to author.
        builder.RegisterComponentOnNewGameObject<SaveHotkeys>(Lifetime.Singleton, "SaveHotkeys");
        builder.RegisterComponentOnNewGameObject<HudBootstrapper>(Lifetime.Singleton, "Hud");

        // RegisterComponentOnNewGameObject is LAZY, and that is not obvious from its name.
        // RegisterComponent<T>(instance) and RegisterComponentInHierarchy both call
        // RegisterBuildCallback internally ("Force inject execution", ContainerBuilderUnityExtensions
        // lines 130/150); RegisterComponentOnNewGameObject does not. A registration nothing depends
        // on is therefore never constructed — no GameObject, no Awake, no Start.
        //
        // Nothing injects any of these three, so none of them existed: the HUD never built
        // ("[HudBootstrapper] HUD built" is absent from Editor.log) and F5/F9 did nothing. The
        // console was the tell — it renders only because DeveloperConsoleController carries its own
        // [RuntimeInitializeOnLoadMethod] self-install hook, and that instance is created with
        // AddComponent, so [Inject] never runs and its _resolver stays null. That is exactly why
        // `room 2 1` answered "LandMap not available" while `damage 100` worked: the damage path has
        // a FindFirstObjectByType fallback, the LandMap path does not, and the console had no
        // container wiring at all — no LandMap, no SimClock, no Lua engine.
        //
        // Resolving them here gives each one its injection, and makes the console's own hook a
        // no-op (it checks Instance != null first). These callbacks are registered before the
        // player/loadout registrations below, and that is fine: ContainerBuilder.Build() completes
        // the whole registry before EmitCallbacks runs it, so registration order cannot affect what
        // is resolvable from inside a callback.
        builder.RegisterBuildCallback(container => container.Resolve<DeveloperConsoleController>());
        builder.RegisterBuildCallback(container => container.Resolve<SaveHotkeys>());
        builder.RegisterBuildCallback(container => container.Resolve<HudBootstrapper>());

        // === Combat Systems ===
        builder.Register<ICombatCalculator, CombatCalculator>(Lifetime.Singleton);
        builder.Register<IDamageCalculator, DamageCalculator>(Lifetime.Singleton);
        builder.Register<ICriticalHitSystem, CriticalHitSystem>(Lifetime.Singleton);
        builder.Register<IDurabilitySystem, DurabilitySystem>(Lifetime.Singleton);

        // === LowLevelPhysics2D World (Stage B) ===
        builder.Register<IPhysicsWorldService, PhysicsWorldService>(Lifetime.Singleton).AsSelf();

        // === Factory Pattern ===
        builder.Register<IProjectileFactory, ProjectileFactory>(Lifetime.Singleton);

        // === Projectile Prefab Registry ===
        if (_projectilePrefabRegistry != null)
            builder.RegisterInstance(_projectilePrefabRegistry);
        else
            Debug.LogWarning("[GameLifetimeScope] ProjectilePrefabRegistry not assigned — " +
                             "weapons will log errors when firing. " +
                             "Create the asset (Assets > Create > PFE > Projectile Prefab Registry) " +
                             "and assign it here.");

        // === Scene MonoBehaviours that need injection ===
        var playerController = FindFirstObjectByType<PlayerController>(FindObjectsInactive.Include);
        if (playerController != null)
            builder.RegisterComponent(playerController);
        else
            Debug.LogWarning("[GameLifetimeScope] No PlayerController found in scene.");

        var weaponLoadout = FindFirstObjectByType<PlayerWeaponLoadout>(FindObjectsInactive.Include);
        if (weaponLoadout != null)
            builder.RegisterComponent(weaponLoadout);
        else
            Debug.LogWarning("[GameLifetimeScope] No PlayerWeaponLoadout found in scene — IProjectileFactory will not be injected into it.");

        // === Input System ===
        var inputSettings = _inputSettings
            ?? Resources.Load<PfeInputSettings>("PfeInputSettings")
            ?? ScriptableObject.CreateInstance<PfeInputSettings>();
        builder.RegisterInstance(inputSettings);
        builder.Register<InputReader>(Lifetime.Singleton);

        if (_debugSettings != null)
        {
            builder.RegisterInstance(_debugSettings);
        }
        else
        {
            var runtimeDebugSettings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
            builder.RegisterInstance(runtimeDebugSettings != null
                ? runtimeDebugSettings
                : ScriptableObject.CreateInstance<PfeDebugSettings>());
        }

        // Resolve once into a local: the simulation clock below needs the configured tick rate, and
        // re-loading here would risk handing SimClock a different instance than the one registered.
        var resolvedGameSettings = _gameSettings;
        if (resolvedGameSettings == null)
        {
            resolvedGameSettings = Resources.Load<GameSettings>("GameSettings");
            if (resolvedGameSettings == null)
            {
                resolvedGameSettings = ScriptableObject.CreateInstance<GameSettings>();
            }
        }

        builder.RegisterInstance(resolvedGameSettings);

        // === Simulation Clock (P1) ===
        // The canonical constants (30 Hz, px-per-frame units) are compile-time values on SimClock.
        // The tick rate is per-match state rather than process state, so it is a registered
        // instance and not a static: a multiplayer session agrees one rate, instead of every peer
        // mutating a shared global. 30 Hz is replica-exact; 60/90/120 mirror the community build's
        // experimental multi-FPS mode.
        var simClock = new SimClock(resolvedGameSettings.SimulationTicksPerSecond);
        builder.RegisterInstance(simClock);

        // Registered but with no ISimTickable consumers attached yet, so this driver only advances
        // its own counters. Consumers move across in Stage C.
        //
        // AsSelf() is REQUIRED, not cosmetic: RegisterEntryPoint<T>() is
        // `Register<T>(lifetime).AsImplementedInterfaces()` — and As* *replaces* the service types
        // rather than adding to them, so without AsSelf the only resolvable services are IStartable
        // and ITickable. MapBridge injects the concrete SimLoop and would fail with
        // "No such registration of type: PFE.Core.SimLoop". Same reason GameManager needs AsSelf
        // below. Singleton also matters here: the instance handed to MapBridge must be the very same
        // one being ticked, or the motor would register on a SimLoop that never runs.
        builder.RegisterEntryPoint<SimLoop>().AsSelf();

        // Syncs GameSettings audio sliders → ISoundService / IMusicService each tick
        builder.RegisterEntryPoint<AudioVolumeSync>();

        // === Data System ===
        builder.Register<ContentRegistry>(Lifetime.Singleton);
        builder.Register<ModLoader>(Lifetime.Singleton);
        builder.Register<GameDatabase>(Lifetime.Singleton);

        // === Visual Systems ===
        builder.Register<FloatingTextManager>(Lifetime.Singleton);
        
        // Register TileAssetDatabase (either from inspector or create runtime)
        if (tileAssetDatabase != null)
        {
            builder.RegisterInstance(tileAssetDatabase);
        }
        else
        {
            // Create runtime database if not assigned
            var runtimeDb = ScriptableObject.CreateInstance<TileAssetDatabase>();
            builder.RegisterInstance(runtimeDb);
            Debug.Log("[GameLifetimeScope] Created runtime TileAssetDatabase (no asset assigned)");
        }

        // === Map System ===
        builder.Register<LandMap>(Lifetime.Singleton);
        builder.Register<RoomGenerator>(Lifetime.Singleton);
        builder.Register<WorldBuilder>(Lifetime.Singleton);
        builder.RegisterInstance(_tileFormDatabase);
        builder.RegisterInstance(_tileTextureLookup);
        builder.RegisterInstance(_materialRenderDb);
        if (_tileMaskLookup != null)
        {
            builder.RegisterInstance(_tileMaskLookup);
        }
        else
        {
            var runtimeMaskLookup = ScriptableObject.CreateInstance<TileMaskLookup>();
            builder.RegisterInstance(runtimeMaskLookup);
            Debug.LogWarning("[GameLifetimeScope] TileMaskLookup is not assigned. Created empty runtime lookup.");
        }
        if (_roomBackgroundLookup != null)
        {
            builder.RegisterInstance(_roomBackgroundLookup);
        }
        else
        {
            var runtimeBackgroundLookup = ScriptableObject.CreateInstance<RoomBackgroundLookup>();
            builder.RegisterInstance(runtimeBackgroundLookup);
            Debug.LogWarning("[GameLifetimeScope] RoomBackgroundLookup is not assigned. Created empty runtime lookup.");
        }
        // === Game Managers ===
        //builder.Register<GameManager>(Lifetime.Singleton);
        //builder.RegisterEntryPoint<GameManager>();
        builder.RegisterEntryPoint<GameManager>(Lifetime.Singleton).AsSelf();

        // === Game Loop ===
        // AsSelf for the same reason as SimLoop above. Nothing injects GameLoopManager today, so
        // this is currently a no-op — but C3 of the P1 plan (and any pause UI) will inject it to
        // call Pause()/Resume(), and it would fail with the same "No such registration" otherwise.
        builder.RegisterEntryPoint<GameLoopManager>().AsSelf();

        // === Map Rendering ===
        // Check if MapBridge exists in scene, if not we'll create it at runtime
        var existingMapBridge = FindFirstObjectByType<MapBridge>();
        if (existingMapBridge != null)
        {
            builder.RegisterComponent(existingMapBridge);
            Debug.Log("[GameLifetimeScope] Registered existing MapBridge");
        }
        else
        {
            var existingMapRendererBootstrapper = FindFirstObjectByType<MapRendererBootstrapper>(FindObjectsInactive.Include);
            if (existingMapRendererBootstrapper != null)
            {
                builder.RegisterComponent(existingMapRendererBootstrapper);
                Debug.Log("[GameLifetimeScope] Registered existing MapRendererBootstrapper");
            }
            else
            {
                Debug.LogWarning("[GameLifetimeScope] No MapBridge found in scene! Map rendering will not work.");
                Debug.LogWarning("[GameLifetimeScope] Please create a GameObject named 'MapRenderer' with MapBridge and RoomVisualController components.");
            }
        }
    }
    
}
