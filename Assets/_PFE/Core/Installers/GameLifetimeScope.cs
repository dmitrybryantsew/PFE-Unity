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
using PFE.Data;
using PFE.Entities.Player;
using PFE.Entities.Weapons;
using PFE.Systems.Weapons;
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
        builder.RegisterMessageBroker<WeaponFiredMessage>(pipe);
        builder.RegisterMessageBroker<WeaponReloadStartedMessage>(pipe);
        builder.RegisterMessageBroker<WeaponReloadCompletedMessage>(pipe);
        builder.RegisterMessageBroker<WeaponDurabilityChangedMessage>(pipe);

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

        // === Combat Systems ===
        builder.Register<ICombatCalculator, CombatCalculator>(Lifetime.Singleton);
        builder.Register<IDamageCalculator, DamageCalculator>(Lifetime.Singleton);
        builder.Register<ICriticalHitSystem, CriticalHitSystem>(Lifetime.Singleton);
        builder.Register<IDurabilitySystem, DurabilitySystem>(Lifetime.Singleton);

        // === Factory Pattern ===
        builder.Register<IWeaponFactory, WeaponFactory>(Lifetime.Singleton);
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

        var weaponView = FindFirstObjectByType<WeaponView>(FindObjectsInactive.Include);
        if (weaponView != null)
            builder.RegisterComponent(weaponView);

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

        if (_gameSettings != null)
        {
            builder.RegisterInstance(_gameSettings);
        }
        else
        {
            var runtimeGameSettings = Resources.Load<GameSettings>("GameSettings");
            builder.RegisterInstance(runtimeGameSettings != null
                ? runtimeGameSettings
                : ScriptableObject.CreateInstance<GameSettings>());
        }

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
        builder.RegisterEntryPoint<GameLoopManager>();

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
            Debug.LogWarning("[GameLifetimeScope] No MapBridge found in scene! Map rendering will not work.");
            Debug.LogWarning("[GameLifetimeScope] Please create a GameObject named 'MapRenderer' with MapBridge and RoomVisualController components.");
        }
    }
    
}
