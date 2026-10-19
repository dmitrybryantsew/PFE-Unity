using System;
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
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Scripting;
using PFE.Systems.Physics;
using PFE.Systems.Particles;
using PFE.Systems.Particles.Adapters;
using PFE.Systems.Particles.Rendering;
using PFE.Data;
using PFE.Entities.Player;
using PFE.Systems.Weapons;
using PFE.Core.Rng;
using PFE.Core.Ids;
using PFE.Core.Scripting;
using PFE.Systems.Campaign;
using PFE.Systems.Trade;
using PFE.UI.Common;
using PFE.UI.PipBuck;
using UnityEngine;

public class GameLifetimeScope : LifetimeScope
{
    [SerializeField] private SoundService      _soundService;
    [SerializeField] private MusicService      _musicService;
    [SerializeField] private ImpactSoundTable  _impactSoundTable;
    [SerializeField] private PfeInputSettings _inputSettings;
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
        // R. Registered here because the publisher (InputReader) and the subscriber (PlayerController)
        // both already existed once the action was added — an unregistered broker publishes into
        // nothing, which is exactly how HealMessage was dead (see below).
        builder.RegisterMessageBroker<ReloadMessage>(pipe);
        // Spells. C (keyDef, supportive cast), T (keyMagic, magic weapon) and Z/X (keySpell1/2) — the
        // four keys AS3 binds to magic (Ctr.as:24-25, :62-65). All three brokers are registered in the
        // same pass that added the actions, because the HealMessage failure mode is invisible: an
        // unregistered broker accepts the publish and delivers to nobody, with no error anywhere.
        // SpellHotkeyMessage carries its own Slot, so one broker serves all four slots.
        builder.RegisterMessageBroker<SpellCastMessage>(pipe);
        builder.RegisterMessageBroker<MagicWeaponMessage>(pipe);
        builder.RegisterMessageBroker<GrenadeMessage>(pipe);
        builder.RegisterMessageBroker<SpellHotkeyMessage>(pipe);
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
        // The one request that actually rebuilds the world: CampaignManager publishes, MapBridge builds.
        builder.RegisterMessageBroker<LandBuildRequestMessage>(pipe);
        // The camp's wall map (allact='map') and a `travel` NPC both publish this.
        builder.RegisterMessageBroker<TravelMapOpenedMessage>(pipe);
        // A trader NPC publishes this; PipBuckBootstrapper's subscriber opens page 4 with the vendor
        // context. Registered here because a broker that does not exist accepts the publish and
        // delivers to nobody, silently — the HealMessage failure above.
        builder.RegisterMessageBroker<VendorOpenedMessage>(pipe);
        // A doctor (`doc`/`vdoc`, or `adoc` past its first repair) publishes this; the bootstrapper's
        // subscriber opens page 6. Page 6 is still a stub, and that is the point: the Oracle's answer
        // to a doctor is "open the clinic", so the port's answer is "open the clinic, which is a stub
        // that says so" rather than "consume the interaction and do nothing".
        builder.RegisterMessageBroker<ClinicOpenedMessage>(pipe);
        builder.RegisterMessageBroker<TutorialPromptMessage>(pipe);
        builder.RegisterMessageBroker<ObjectiveMarkerMessage>(pipe);
        // Campaign event surface (CampaignControl C3). The campaign used to mutate its own trigger and
        // quest collections and tell nobody, so nothing could bind a hook to "a quest advanced". These
        // four are the equivalent of the LandTransitionMessage seam, for progression rather than travel.
        builder.RegisterMessageBroker<TriggerChangedMessage>(pipe);
        builder.RegisterMessageBroker<QuestStateChangedMessage>(pipe);
        builder.RegisterMessageBroker<LandEnteredMessage>(pipe);
        builder.RegisterMessageBroker<CheckpointActivatedMessage>(pipe);
        // Script presentation surface. AS3 routes `dial`/`dialog`/`inform`/`landlevel` through one
        // function (GUI.dialText), so there is one message; `control off/on` is a second because the
        // lock is held and released by name; `gui off/on`, `black` and `dblack` are a third because
        // they drive one overlay. Registered here for the same reason as HealMessage above — a broker
        // that does not exist accepts the publish and delivers to nobody, silently.
        builder.RegisterMessageBroker<ScriptDialogueMessage>(pipe);
        builder.RegisterMessageBroker<ScriptControlLockMessage>(pipe);
        builder.RegisterMessageBroker<ScriptOverlayMessage>(pipe);
        // RPG messages
        builder.RegisterMessageBroker<LevelUpMessage>(pipe);
        builder.RegisterMessageBroker<SkillLevelChangedMessage>(pipe);
        builder.RegisterMessageBroker<StatsChangedMessage>(pipe);
        builder.RegisterMessageBroker<XpGainedMessage>(pipe);
        builder.RegisterMessageBroker<PerkAddedMessage>(pipe);
        builder.RegisterMessageBroker<TraumaChangedMessage>(pipe);

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

        // === PipBuck device (Items I7 / I8 / I9) ===
        //
        // A second runtime-built canvas, for the same reasons as the HUD: SampleScene has no PipBuck
        // object, and the device needs data that only exists at runtime (the composition asset, the
        // player's inventory, the equipped weapon's condition).
        //
        // The view model is a plain class, not a MonoBehaviour, so it is constructor-injected rather
        // than attached to the canvas: the shell subscribes to it and every page writes into it, and
        // neither needs it to be a component.
        builder.Register<PipBuckViewModel>(Lifetime.Singleton);

        // World.allStat (World.as:1385). A singleton so that every interface which can freeze the world
        // shares ONE holder set — the whole reason the gate counts holders instead of holding a bool.
        // PipBuckManager acquires it; SimLoop reads it.
        builder.Register<InterfacePauseGate>(Lifetime.Singleton);

        // UiSoundService's constructor takes ISoundService, and SoundService is an OPTIONAL scene
        // reference (see the Audio System block above). VContainer resolves every constructor parameter
        // and never consults a `= null` default, so on a scene with no SoundService the container would
        // throw here — and because EntryPointDispatcher resolves the whole collection eagerly, that
        // throw escapes Build() and NO entry point starts at all (the same trap CampaignCatalog is
        // registered to avoid, see the Campaign Progression block). The factory makes the missing
        // service a null instead, which UiSoundService is explicitly written to tolerate: "silence is a
        // degraded interface; an exception out of a UI callback is a broken one."
        builder.Register<UiSoundService>(Lifetime.Singleton)
            .WithParameter<ISoundService>(resolver =>
                resolver.TryResolve<ISoundService>(out ISoundService sound) ? sound : null);

        // AsSelf() is REQUIRED, not decoration: PipBuckBootstrapper's [Inject] Construct takes this type
        // CONCRETELY, and any `As<>` call drops the implicit self-registration (see the note above
        // SimLoop for the mechanism). As<IAutoRegisteredSimTickable> is what puts it on SimLoop —
        // SimTickRegistrar reads that marker — so the device's page animation advances on the tick
        // clock without anyone having to remember a Register call.
        builder.Register<PipBuckManager>(Lifetime.Singleton)
            .AsSelf()
            .As<IAutoRegisteredSimTickable>();

        builder.RegisterComponentOnNewGameObject<PipBuckBootstrapper>(Lifetime.Singleton, "PipBuck");

        // Same laziness trap as the HUD above: nothing injects PipBuckBootstrapper, so without an
        // explicit resolve no GameObject is created and the device simply never exists.
        builder.RegisterBuildCallback(container => container.Resolve<PipBuckBootstrapper>());

        // The pause gate reaches SimLoop by PUSH rather than by injection. SimLoop is constructed
        // directly by six call sites across five fixtures, so a third constructor parameter would
        // either break them or make the container's constructor choice depend on VContainer's
        // resolution order — not something a behaviour switch should rest on. See SimLoop.WorldGate.
        // Pushed at build time, so the loop holds the container's instance before the first tick.
        builder.RegisterBuildCallback(container =>
            container.Resolve<SimLoop>().WorldGate = container.Resolve<InterfacePauseGate>());

        // Push the seeded combat RNG into CharacterStats. Its two damage rolls (ApplyOrganDamage's
        // head/torso/legs split and ApplyBloodDamage's bleed roll) were drawing from
        // UnityEngine.Random — unseeded and process-global, so the same hit resolved differently every
        // run. Set here rather than resolved because CharacterStats is created with AddComponent by
        // PlayerController and has no constructor to inject into; this is the one static in the RPG
        // path, and it is what makes RngSeedOverride actually govern damage.
        builder.RegisterBuildCallback(container => PFE.Systems.RPG.CharacterStats.CombatRng = container.Resolve<IRngService>());

        // === Combat Systems ===
        builder.Register<ICombatCalculator, CombatCalculator>(Lifetime.Singleton);
        builder.Register<IDamageCalculator, DamageCalculator>(Lifetime.Singleton);
        builder.Register<ICriticalHitSystem, CriticalHitSystem>(Lifetime.Singleton);
        builder.Register<IDurabilitySystem, DurabilitySystem>(Lifetime.Singleton);

        // Single point of damage resolution. RegisterEntryPoint (not Register) so IStartable actually
        // runs — DamageSystem.Start puts itself on SimLoop at SimTickOrder.Damage. AsSelf for the same
        // reason as SimLoop above: RegisterEntryPoint supplies IStartable/ITickable as the service
        // types, so the impl-type binding is never added (see the note above SimLoop), and without it
        // the four sources could not [Inject] the concrete type.
        builder.RegisterEntryPoint<DamageSystem>().AsSelf();

        // === Campaign Progression & World Transitions ===
        // CampaignManager's constructor takes a CampaignCatalog, and VContainer resolves EVERY
        // constructor parameter: ResolveOrParameter never consults ParameterInfo.HasDefaultValue, so the
        // `= null` default on that parameter is not optional to the container — the same trap the note
        // above DamageSystem records. Leaving it unregistered does not fail just this one service.
        // EntryPointDispatcher.Dispatch resolves the whole IInitializable collection eagerly, so the
        // throw is raised before the per-item try/catch and escapes Build()/Awake(): NO entry point
        // starts at all. SimLoop and GameManager are registered after this line, so the observed
        // symptom was no sim clock (tickables=0, tick=-1) and no map (props=<no room>).
        var campaignCatalog = Resources.Load<CampaignCatalog>("CampaignCatalog");
        if (campaignCatalog != null)
        {
            builder.RegisterInstance(campaignCatalog);
        }
        else
        {
            builder.RegisterInstance(ScriptableObject.CreateInstance<CampaignCatalog>());
            Debug.LogWarning("[GameLifetimeScope] CampaignCatalog asset is missing from Resources. Created an empty runtime catalog.");
        }
        // The bus is a plain singleton, not an entry point: it holds no per-frame work, only the
        // dispatch-depth guard, and constructing it eagerly keeps "the guard exists" independent of
        // whether a subscriber ever registers.
        //
        // The `Action<string> report` parameter is NOT optional just because it has a `= null` default:
        // VContainer resolves EVERY constructor parameter and never consults a default
        // (`ReflectionInjector.CreateInstance` -> `ResolveOrParameter` -> `Resolve`, which throws
        // `No such registration of type: System.Action`1[System.String]`). The four `IPublisher<>`
        // parameters above resolve because their brokers are registered; this one has no registration to
        // find, so without the line below the throw escapes `ContainerBuilder.Build()` inside
        // `EntryPointDispatcher.Dispatch()` and **no entry point starts at all** — which reads as
        // "CampaignManager was never created", not as "the bus is missing its reporter".
        builder.Register<CampaignEventBus>(Lifetime.Singleton)
            .WithParameter<Action<string>>(_ => null);

        // The quest payout. `IQuestRewardHost` used to be a null factory here, which made every
        // completed quest log "owes N xp, but no IQuestRewardHost is wired, so the payout was dropped" —
        // 19 quests carry @xp, 3 carry @sp, 40 carry @rep and 77 `<reward>` rows exist in the shipped
        // data, so a whole progression axis was reading as "these quests pay nothing".
        //
        // Both lookups are `Func<>`s and not values, for the reason the ScriptPlayerHost block above
        // gives: CharacterStats/PlayerInventory are components on a scene object that a respawn, a room
        // rebuild or a scene reload replaces. Resolving once would leave quests paying a destroyed
        // object, which throws nothing and changes nothing. The lookup body mirrors
        // PlayerScriptControlLock.cs:96 and the rest of the codebase — a lazy FindFirstObjectByType.
        //
        // `IQuestScriptHost` stays a null factory, and that is now a measured answer rather than an
        // assumption: `endscr` occurs ZERO times across every `<quest>` in GameData.as, so the seam has
        // no quest-side caller to serve. CampaignManager already reports by name when an `endscr` does
        // not run, so a future authored one cannot go silent.
        builder.Register<QuestRewardHost>(Lifetime.Singleton)
            .As<IQuestRewardHost>()
            .WithParameter<Func<PFE.Systems.RPG.CharacterStats>>(_ => () =>
            {
                PlayerController player = UnityEngine.Object.FindFirstObjectByType<PlayerController>(
                    FindObjectsInactive.Include);
                return player != null ? player.CharacterStats : null;
            })
            .WithParameter<Func<PFE.Systems.Inventory.PlayerInventory>>(_ => () =>
            {
                PlayerController player = UnityEngine.Object.FindFirstObjectByType<PlayerController>(
                    FindObjectsInactive.Include);
                return player != null ? player.GetComponent<PFE.Systems.Inventory.PlayerInventory>() : null;
            })
            .WithParameter<Action<string>>(_ => null);

        // The vendor stocking seam — AS3's `LootGen.getRandom` + `Item` table lookup, both reached
        // through the port's existing loot roller and item catalog rather than reimplemented.
        //
        // Registered with an explicit factory, NOT `Register<IVendorItemSource, LootVendorItemSource>`:
        // that class has two constructors (`()` and `(Func<LootContext>)`), and VContainer picks by
        // "most resolvable parameters", so a stray `Func<LootContext>` registration elsewhere would
        // silently switch which one the container builds. Naming the constructor here removes the
        // question.
        //
        // This is the registration whose absence made every trader report "the campaign has no such
        // vendor": `CampaignManager.InitializeVendors` needs an item source and an RNG, had neither
        // injected, and was never called, so `CampaignManager.Vendors` stayed empty while the
        // catalogue's 13 vendor assets sat there correctly referenced.
        builder.Register<IVendorItemSource>(_ => new LootVendorItemSource(), Lifetime.Singleton);

        builder.RegisterEntryPoint<CampaignManager>()
            .As<ICampaignManager>()
            .AsSelf()
            .WithParameter<IQuestRewardHost>(resolver =>
                resolver.TryResolve<IQuestRewardHost>(out IQuestRewardHost rewards) ? rewards : null)
            .WithParameter<IQuestScriptHost>(_ => null);

        // The vendor restock inputs — AS3 `Pers.limitBuys` (`Pers.as:317`, default 1) and
        // `Pers.barterLvl` (`Pers.as:315`, default 0), read by `Game.refillVendors` (`Game.as:508-525`)
        // through `Vendor.refill` (`Vendor.as:300-312`).
        //
        // Both were declared as `Func<>` properties on CampaignManager and assigned **nowhere**, so
        // every refill used the field defaults and a perk that raised `barterLvl` changed nothing about
        // what a shop restocked. That is the same shape as the missing `InitializeVendors` caller two
        // blocks above: the consumer was written, the producer was not — and unlike a missing
        // registration, a missing `Func` assignment throws nothing and logs nothing.
        //
        // Lazy lookups, not values, for the reason the `IQuestRewardHost` block above gives: the player
        // is assembled in `PlayerController.Awake`, which Unity does not order against this build
        // callback. Resolving once would leave the vendors restocking against a destroyed object.
        //
        // Resolving an entry point inside a build callback is already the pattern here — the SimLoop
        // gate above does exactly this, and `RegisterEntryPoint` supplies `AsSelf` for the concrete
        // type only when asked, which the registration above does.
        builder.RegisterBuildCallback(container =>
        {
            CampaignManager campaign = container.Resolve<CampaignManager>();

            campaign.VendorLimitBuysProvider = () =>
            {
                PlayerController player = UnityEngine.Object.FindFirstObjectByType<PlayerController>(
                    FindObjectsInactive.Include);
                return player != null && player.CharacterStats != null
                    ? player.CharacterStats.limitBuys
                    : 1f;
            };

            campaign.VendorBarterLevelProvider = () =>
            {
                PlayerController player = UnityEngine.Object.FindFirstObjectByType<PlayerController>(
                    FindObjectsInactive.Include);
                return player != null && player.CharacterStats != null
                    ? player.CharacterStats.barterLvl
                    : 0;
            };
        });

        // === Map-script interpreter (CampaignControl C2) ===
        //
        // Four registrations, and every one of them needs an explicit parameter for the same reason:
        // VContainer resolves EVERY constructor parameter and never consults a `= null` default. So a
        // class written to tolerate a missing collaborator — which these are, deliberately, because the
        // offline fixtures construct them with a bare `new` — still throws on the container path unless
        // the parameter is supplied. The CampaignCatalog block above documents the same trap.
        //
        // The probe is a singleton because its two latches ARE the seam: the per-frame half writes them
        // (ScriptSystem.Tick) and the per-tick half reads them (ScriptRunner.Step). Two instances would
        // be a cutscene that never advances, with nothing red anywhere.
        builder.Register<ScriptInputProbe>(Lifetime.Singleton)
            .WithParameter<InputReader>(resolver =>
                resolver.TryResolve<InputReader>(out InputReader reader) ? reader : null)
            // `readMouse` is a `bool` with a `= true` default, and VContainer resolves value-type
            // parameters exactly like reference ones — it does not fall back to the default. Left out,
            // this registration throws on `bool` after the InputReader line above has already succeeded,
            // which is a confusing place to fail. `true` is the shipping value: AS3 reads the right
            // mouse button for `keyPressed2` (Ctr.as:582-588), and only the offline fixtures pass false.
            .WithParameter<bool>(true);

        // The presentation host is the only writer of `mess`/`dial`/`dialog`/`inform`/`control`/`gui`/
        // `black`/`dblack`/`landlevel`. The text table is loaded lazily from Resources, so a scene with
        // no localisation still constructs — it resolves no text and reports each missing id once.
        builder.Register<ScriptPresentationHost>(Lifetime.Singleton)
            .As<IScriptPresentationHost>()
            .WithParameter<PipTextTable>(_ => PFE.UI.Common.PfeTextTableProvider.Shared)
            .WithParameter<Action<string>>(_ => null)
            // `landlevel`'s two numbers — AS3 `game.lands[val].dif` and `pers.level`
            // (Script.as:288-292). A factory for the same reason `stats` below is one: the land comes
            // from the campaign and the level from a scene MonoBehaviour, and neither may exist when
            // the container is built. Returning `null` for an unknown land is the oracle's own
            // `Boolean(lands[val])` guard, not a fallback — the panel then shows the name only.
            .WithParameter<Func<string, LandLevelFacts?>>(_ => landId =>
            {
                PFE.Systems.Campaign.CampaignManager campaign =
                    PFE.Systems.Campaign.CampaignManager.Current;
                PFE.Data.Definitions.Campaign.LandDefinition land =
                    campaign != null && campaign.Catalog != null
                        ? campaign.Catalog.GetLand(landId)
                        : null;

                if (land == null) return (LandLevelFacts?)null;

                PlayerController player = UnityEngine.Object.FindFirstObjectByType<PlayerController>(
                    FindObjectsInactive.Include);
                int playerLevel = player != null && player.CharacterStats != null
                    ? player.CharacterStats.Level
                    : 1;

                return new LandLevelFacts(land.baseDifficulty, playerLevel);
            });

        // The player-facing half (`perk`, `xp`, `item`, `armor`, `effect`, `weapon`). `stats` is a
        // factory and not a value: CharacterStats is a scene MonoBehaviour that may not exist yet when
        // the container is built, and the vocabulary that touches it is rare enough that resolving it
        // per call is cheaper than making the container depend on scene load order.
        //
        // **It was a null factory, and that silently disabled every perk grant in the game.** The
        // comment above describes the intent; the line below passed `_ => null`, so
        // `ScriptPlayerHost._stats` was null and `AddPerk` took its "no CharacterStats is reachable"
        // branch — reported once as a LogWarning and then dropped. The visible consequence was the
        // begin land's double jump: `beginLevit` runs `<s act="perk" val="levitation"/>`
        // (GameData.as:1225), the only grant of `isDJ` in the whole game, and the player got the
        // dialogue line, the `perkLevit` toast, and no perk. `AddPerk` itself was correct — it was
        // simply never reached. Same shape as the `IQuestRewardHost` null factory fixed above, and the
        // reason the lookup below is written out rather than left as `_ => null`.
        builder.Register<ScriptPlayerHost>(Lifetime.Singleton)
            .As<IScriptPlayerHost>()
            .WithParameter<Func<PFE.Systems.RPG.CharacterStats>>(_ => () =>
            {
                PlayerController player = UnityEngine.Object.FindFirstObjectByType<PlayerController>(
                    FindObjectsInactive.Include);
                return player != null ? player.CharacterStats : null;
            })
            .WithParameter<Action<string>>(_ => null);

        // The clock and the registry. AsSelf() is REQUIRED, not decoration: SimTickRegistrar reads the
        // IAutoRegisteredSimTickable marker to put it on SimLoop, and AreaTriggerSystem reaches the
        // system through the static ScriptSystem.Current (it is constructed by RoomVisualController,
        // which the container never observes). As<IAutoRegisteredSimTickable> is what makes "the
        // interpreter advances at 30 Hz" a declaration instead of a line someone must remember.
        builder.RegisterEntryPoint<ScriptSystem>()
            .AsSelf()
            .As<IAutoRegisteredSimTickable>()
            .WithParameter<Action<string>>(_ => null);

        // The consumer for `control off` / `control on`. A separate component rather than a twelfth
        // parameter on PlayerController.Construct, which is called directly by the offline fixtures —
        // and it resolves the player lazily, so a respawn that replaces the controller is picked up and
        // the held lock value is re-applied to the new one.
        builder.RegisterComponentOnNewGameObject<PlayerScriptControlLock>(Lifetime.Singleton, "ScriptControl");

        // Same laziness trap as HudBootstrapper and PipBuckBootstrapper above: nothing injects this, so
        // without an explicit resolve no GameObject is created and the lock has no subscriber — which
        // reads as "the player can walk around during the opening cutscene".
        builder.RegisterBuildCallback(container => container.Resolve<PlayerScriptControlLock>());

        // === LowLevelPhysics2D World (Stage B) ===
        builder.Register<IPhysicsWorldService, PhysicsWorldService>(Lifetime.Singleton).AsSelf();

        // === Factory Pattern ===
        // ProjectileFactory builds its own template in code (ProjectileTemplateBuilder), so there is no
        // prefab or registry to assign here. It used to warn when a ProjectilePrefabRegistry asset was
        // unassigned; that asset and this field are both gone.
        builder.Register<IProjectileFactory, ProjectileFactory>(Lifetime.Singleton);

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
            builder.RegisterInstance(_debugSettings).As<PFE.Data.IGameDatabaseDebugSettings>().AsSelf();
        }
        else
        {
            var runtimeDebugSettings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
            builder.RegisterInstance(runtimeDebugSettings != null
                ? runtimeDebugSettings
                : ScriptableObject.CreateInstance<PfeDebugSettings>())
                .As<PFE.Data.IGameDatabaseDebugSettings>().AsSelf();
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
        // AsSelf() is LOAD-BEARING, not readability. An earlier version of this comment claimed the
        // opposite, on the reasoning that the `As*` family *appends* rather than replaces — which is
        // true and is only half the mechanism. The half it missed:
        //
        //   RegistrationBuilder.AddInterfaceType (:138-147) appends, so the `As<>` calls stack.
        //   BUT Registry.Build (Registry.cs:27-44) registers the implementation type only in its
        //   *else* branch — i.e. only while `InterfaceTypes` is still null. As soon as any `As<>`
        //   has run, it instead inserts a deliberately **null** entry under the implementation-type
        //   key ("Mark the ImplementationType with a guard because we need to check if it exists
        //   later"), and Registry.TryGet (:112-115) returns false for a null registration.
        //
        // So: `Register<T>()` with no `As<>` is resolvable as T; `Register<T>().As<I>()` is NOT.
        // That is what makes `.AsSelf()` required above, and it is also why every RegisterEntryPoint
        // in this file carries one — RegisterEntryPoint supplies IStartable/ITickable as service
        // types, so the self binding is already gone by the time it returns. MapBridge's
        // [Inject] Construct takes several of these types CONCRETELY, so a missing AsSelf is not a
        // subtle degradation: Awake throws and the scene never bootstraps. Found 2026-10-04, when
        // MapBridge failed to resolve ParticleWorld.
        //
        // Singleton still matters, and for a real reason: the instance handed to MapBridge must be the
        // very same one being ticked, or the motor would register on a SimLoop that never runs.
        builder.RegisterEntryPoint<SimLoop>().AsSelf();

        // === Particle / effect visuals (the 118 <part> rows) ===
        //
        // Both data assets are null-tolerant on purpose: a fresh clone that has not run the two
        // importers yet must still boot and simply draw nothing, rather than fail to start. Each miss
        // is named in the log together with the menu item that fixes it, because "particles do not
        // appear" is otherwise indistinguishable from "the data was never imported".
        ParticleDefinitionAsset particleDefinitions =
            Resources.Load<ParticleDefinitionAsset>("ParticleDefinitions");
        if (particleDefinitions == null)
        {
            Debug.LogWarning(
                "[GameLifetimeScope] ParticleDefinitions.asset not found in Resources — run " +
                "PFE/Data/Import Particles from AllData.as. No particle will resolve; every emit " +
                "will be reported as an unknown id.");
        }

        builder.RegisterInstance<ParticleDefinitionTable>(
            particleDefinitions != null ? particleDefinitions.Table : ParticleDefinitionTable.Empty);

        ParticleSpriteCatalogAsset particleSprites =
            Resources.Load<ParticleSpriteCatalogAsset>("ParticleSprites");
        if (particleSprites == null)
        {
            Debug.LogWarning(
                "[GameLifetimeScope] ParticleSprites.asset not found in Resources — run " +
                "PFE/Art/Import Particle Sprites. Particles will spawn with zero frames and " +
                "therefore draw nothing.");
        }

        // ONE catalogue, registered under two service types. `ParticleWorld` only needs the
        // `IParticleSpriteFrames` seam (an int-returning frame count, so the world stays Unity-free),
        // while `ParticleRenderer` needs `FrameAt` to get an actual Sprite. Registering the same instance
        // twice is what keeps those from becoming two independent dictionaries that can disagree.
        ParticleSpriteCatalog particleCatalog = new ParticleSpriteCatalog(particleSprites);
        builder.RegisterInstance(particleCatalog);
        builder.RegisterInstance<IParticleSpriteFrames>(particleCatalog);

        // Room-scoped by nature, and registered as the concrete type as well so the room can push the
        // current ITileQueryService into *this* instance (the adapter's own docs explain why it is
        // pushed rather than injected). Until a room pushes one the answer is dry, which is honest:
        // only 6 of the 118 rows read water at all.
        // AsSelf() is REQUIRED, not decoration: MapBridge's [Inject] Construct takes this type
        // CONCRETELY (it pushes the current room's tile query into *this* instance), and any `As<>`
        // call drops the implicit self-registration — see the note above SimLoop for the mechanism.
        builder.Register<TileQueryParticleWater>(Lifetime.Singleton)
            .AsSelf()
            .As<IParticleTileWater>();

        // The world takes only seams, so it stays constructible from a bare `new` in the offline
        // fixtures. As<IAutoRegisteredSimTickable> is what puts it on SimLoop — SimTickRegistrar reads
        // that marker, so nothing has to remember to register it.
        // AsSelf() is REQUIRED: MapBridge hands the CONCRETE ParticleWorld to RoomVisualController
        // (SetParticleWorld), because the view needs more than the IParticleWorld seam. Without it,
        // Resolve(typeof(ParticleWorld)) throws at Awake and the whole scene fails to bootstrap.
        builder.Register<ParticleWorld>(Lifetime.Singleton)
            .AsSelf()
            .As<IParticleWorld>()
            .As<IAutoRegisteredSimTickable>();

        // The Unity-side emit seam: gameplay hands it a Unity world position and it does the
        // room-origin subtraction and the AS3 Y mirror once, for every call site. Registered as the
        // concrete type — there is one implementation, and the part worth pinning (the conversion) is
        // pinned by WorldCoordinates' fixtures rather than by a mock. Like the water adapter it takes
        // the current room's tile query by push, from the same place, so a room change cannot leave it
        // converting against the previous room's origin.
        builder.Register<RoomParticleEmitter>(Lifetime.Singleton);

        // The single place every always-on tickable is attached to SimLoop. This replaces the
        // per-system `IStartable` driver that ParticleWorld used to need.
        builder.RegisterEntryPoint<SimTickRegistrar>();

        // Syncs GameSettings audio sliders → ISoundService / IMusicService each tick
        builder.RegisterEntryPoint<AudioVolumeSync>();

        // === Data System ===
        //
        // ONE registry, not two — and the container's copy is GameDatabase's own.
        //
        // This used to be `builder.Register<ContentRegistry>(Lifetime.Singleton)` next to
        // `builder.Register<GameDatabase>(Lifetime.Singleton)`, which built TWO independent
        // registries: GameDatabase's ctor does `Registry = new ContentRegistry()`, and the
        // container's singleton was a second, empty table. GameManager calls
        // `gameDatabase.Initialize()` (GameManager.cs:64), which populates **GameDatabase.Registry**
        // — so the populated table was invisible to every consumer that takes `ContentRegistry` from
        // the container, and the container's copy stayed empty for the whole session.
        //
        // The consumers are the two id→row resolvers, and both fail silently by design:
        //   PlayerWeaponLoadout.Construct → ContentRegistryAmmoResolver → Resolve(ammoId) = null
        //   MapBridge/PlayerController.Construct → ContentRegistryEffectDefinitionResolver = null
        // A null ammo row is AS3's "неправильный патрон" branch — every ammo term stays at its
        // identity — so the ammo ballistics fold (damage ×, pierce +, armour ×, knock ×, prec ×,
        // probiv, tipdam, det) was inert on the live fire path while the F2 ammo dropdown, which
        // loads AmmoDefinitions from Resources itself, showed the round's real numbers. Selecting
        // armour-piercing p5_1 therefore changed nothing: `piercing` stayed 0 and a 20-armour target
        // kept taking 0 from the 7-damage minigun. Registering the database first and handing out
        // ITS registry makes `Registry.Initialize(sources)` (GameDatabase.cs:320) the single table
        // every consumer sees, which is what the resolver comments already claimed was happening.
        builder.Register<ModLoader>(Lifetime.Singleton);
        builder.Register<GameDatabase>(Lifetime.Singleton);
        builder.Register<ContentRegistry>(
            container => container.Resolve<GameDatabase>().Registry, Lifetime.Singleton);

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
