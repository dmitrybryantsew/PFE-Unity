using UnityEngine;
using VContainer;
using R3;
using MessagePipe;
using PFE.Core.Input;
using PFE.Core.Messages;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Effects;
using PFE.Systems.Interaction;
using PFE.Systems.Inventory;
using PFE.Systems.Audio;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Magic;
using PFE.Systems.Physics;
using PFE.Systems.RPG;
using PFE.Systems.Weapons;
namespace PFE.Entities.Player
{
    /// <summary>
    /// Player controller for LittlePip (the protagonist).
    /// Replaces UnitPlayer.as from ActionScript (5,017 lines condensed to ~200 lines).
    ///
    /// Key features:
    /// - WASD movement with acceleration
    /// - Jump with adjustable force
    /// - Dash/double-tap detection (TODO)
    /// - Weapon aiming and firing
    /// - Uses VContainer for dependency injection
    /// - Uses InputReader for decoupled input handling
    /// </summary>
    public class PlayerController : UnitController
    {
        [Header("Player Specifics")]
        [SerializeField]
        [Tooltip("Should the player always face the mouse cursor?")]
        private bool _mouseAiming = true;

        [SerializeField]
        [Tooltip("Maximum time between directional taps for dash/drop-through detection.")]
        private float _doubleTapWindowSeconds = 0.25f;
        private TilePhysicsController _tilePhysics;
        private PlayerLocomotionController _locomotion;

        /// <summary>
        /// Runs the hold-to-act gesture for world objects. Optional — a player prefab without one keeps
        /// the immediate-press behaviour for every target, since every use below is null-guarded.
        /// </summary>
        private PlayerActionInteractor _actionInteractor;

        /// <summary>
        /// Frame of the last accepted interact press. The press arrives twice — once on the
        /// MessagePipe subscription and once from the direct poll in <see cref="Update"/> — and this is
        /// what stops one key press acting twice, which for a door would open and close it in a single
        /// frame.
        /// </summary>
        private int _lastInteractPressFrame = -1;

        /// <summary>
        /// AS3 <c>UnitPlayer.ggControl</c> (<c>UnitPlayer.as:65</c>) — the cutscene input lock, set false
        /// by <c>controlOff()</c> (<c>:2037-2050</c>) and true by <c>controlOn()</c> (<c>:2053-2066</c>),
        /// both reached from the script commands <c>control off</c> / <c>control on</c>
        /// (<c>Script.as:239-246</c>).
        ///
        /// <para><b>Defaults to true, and that default is the whole safety property.</b> The lock is
        /// released by a <i>separate</i> authored command, so a session that never runs a script must be
        /// fully playable; and if a script's release is missed the failure is "the player cannot move",
        /// which is far worse than "the player could move during a cutscene". Both are why this is a
        /// plain bool on the controller rather than a counter of outstanding locks.</para>
        ///
        /// <para><b>What it does NOT reproduce.</b> The Oracle's <c>controlOff</c> also clears the
        /// controller, drops the telekinesis target, clears the action target, makes the player
        /// invulnerable, zeroes the walk counter and hides the weapon
        /// (<c>UnitPlayer.as:2037-2050</c>). The port gates movement, jump, dash and interact, which is
        /// what the player can observe; the rest is reported by
        /// <c>ScriptPresentationHost.ReportUnconsumed</c>'s siblings rather than guessed at here.</para>
        /// </summary>
        public bool ScriptControlEnabled { get; set; } = true;

        /// <summary>
        /// Set while a modal HUD message is on screen — the port's <c>guiPause</c> (<c>GUI.as:120</c>),
        /// which <c>impMess</c> raises so the player cannot walk or act while reading a tutorial popup
        /// (<c>GUI.as:1931</c>, and the per-frame re-assertion at <c>:2069-2076</c>).
        ///
        /// <para><b>Why this is a second flag rather than another write to
        /// <see cref="ScriptControlEnabled"/>.</b> <c>PlayerScriptControlLock</c> is a reconciler: it
        /// re-applies its remembered value on every frame where the controller disagrees
        /// (<c>PlayerScriptControlLock.Update</c>). A modal that wrote <c>ScriptControlEnabled = false</c>
        /// directly would therefore be undone on whichever frames that reconciler ran afterwards, and
        /// would also have to guess what to restore on dismissal — restoring <c>true</c> would release a
        /// cutscene's <c>control off</c> that the popup never took. Two independent flags ORed at the
        /// point of use cannot fight and need no restoration.</para>
        /// </summary>
        public bool ModalInputBlocked { get; set; }

        /// <summary>
        /// Whether player input is currently honoured at all — the cutscene lock <b>and</b> the modal
        /// block. Every input gate reads this rather than <see cref="ScriptControlEnabled"/> directly,
        /// so a new blocker cannot be added to one gate and forgotten in another.
        /// </summary>
        public bool ControlActive => ScriptControlEnabled && !ModalInputBlocked;

        // Dependencies (injected via VContainer)
        private InputReader _input;
        private PFE.Core.PfeDebugSettings _debugSettings;

        /// <summary>
        /// The player's weapon, and the only weapon path there is.
        ///
        /// <para>The component exists from <c>Awake</c>, but the loadout does not create its
        /// <c>IWeaponController</c> until its own <c>Start()</c> — and Unity does not order
        /// <c>Start</c> between components. So every call below goes through the loadout's
        /// null-guarded forwarding methods rather than reaching for the controller directly.</para>
        ///
        /// <para>The legacy <c>WeaponLogic</c> / <c>WeaponView</c> / <c>IWeaponFactory</c> branch was
        /// retired in Phase 2.2. It had been unreachable since the Player prefab gained a
        /// <c>PlayerWeaponLoadout</c>, but it kept a second, half-wired weapon system alive.</para>
        /// </summary>
        private PlayerWeaponLoadout _loadout;
        private PlayerTelekinesisController _telekinesis;

        /// <summary>
        /// Runs AS3's mana block (<c>UnitPlayer.as:1302-1361</c>) once per sim tick. Created here
        /// because the player is motor-driven, so <see cref="PFE.Entities.Units.UnitController.SimTick"/>
        /// returns early for it and there is no other per-tick hook. See
        /// <see cref="PlayerManaTicker"/> for why it is a separate component.
        /// </summary>
        private PlayerManaTicker _manaTicker;

        /// <summary>
        /// Runs AS3's radiation block (<c>UnitPlayer.as:898-960</c>) once per sim tick and owns the
        /// player's exposure state. Created here for the same reason as <see cref="PlayerManaTicker"/>:
        /// the player is motor-driven, so <c>UnitController.SimTick</c> returns early for it and a
        /// separate <see cref="ISimTickable"/> is the only per-tick hook.
        /// </summary>
        private PlayerRadiationTicker _radiationTicker;

        /// <summary>
        /// The player's live radiation state — <c>gg.rad</c> and the accumulators around it. Read by the
        /// PipBuck pages and by RadAway; written by <see cref="PlayerRadiationTicker"/> and by any
        /// emitter that pushes exposure (the glowing ghoul's aura). Null only before <c>Awake</c>.
        /// </summary>
        public PlayerRadiationState Radiation => _radiationTicker != null ? _radiationTicker.State : null;

        /// <summary>
        /// The room the player is standing in — <c>gg.loc</c>. Follows the land map rather than caching
        /// a room, because a door within the same land never re-enters <c>MapBridge.SpawnPlayer</c>.
        /// </summary>
        public PFE.Systems.Map.RoomInstance CurrentRoom =>
            _pendingLandMap != null ? _pendingLandMap.currentRoom : null;

        /// <summary>
        /// Walk-over loot collection — AS3 <c>Loot.take()</c>'s automatic half, gated on the player's
        /// <c>isTake</c> window. Created here for the same reason as <see cref="PlayerManaTicker"/>: the
        /// player is motor-driven, so <c>UnitController.SimTick</c> returns early for it and a separate
        /// <see cref="ISimTickable"/> is the only per-tick hook. See <see cref="PlayerAutoPickup"/>.
        /// </summary>
        private PlayerAutoPickup _autoPickup;

        /// <summary>
        /// The player's spell caster — the port of AS3 <c>UnitPlayer</c>'s spell half. Created here for
        /// the same reason as <see cref="PlayerManaTicker"/>: the player is motor-driven, so
        /// <c>UnitController.SimTick</c> returns early for it and a separate <c>ISimTickable</c> is the
        /// only way to get a per-tick hook. See <see cref="PlayerSpellCaster"/>.
        /// </summary>
        private PlayerSpellCaster _spellCaster;

        /// <summary>
        /// The player's inventory. Created in <c>Awake</c> like the caster, and wired from both
        /// <c>Construct</c> and <c>Awake</c> — see <see cref="WireInventoryIfReady"/>. Per-player by
        /// construction, which is what a second co-op player needs.
        /// </summary>
        private PlayerInventory _inventoryComponent;

        /// <summary>
        /// The land's first-acquisition item scripts — AS3 <c>Land.itemScripts</c> +
        /// <c>Invent.takeScript</c> (<c>Land.as:141-149</c>, <c>Invent.as:1554-1560</c>). Created in
        /// <c>Awake</c> alongside the inventory, because it subscribes to the inventory's
        /// <c>OnItemAdded</c>/<c>OnWeaponAdded</c> and must therefore be built after it exists.
        ///
        /// <para>Without this, taking the Cogwheel opened no quest: <c>collectHS</c> is granted from
        /// exactly one place in the game, and that place is a <c>&lt;scr eve='take' item='col1'&gt;</c>
        /// block (<c>GameData.as:44-46</c>).</para>
        /// </summary>
        private PFE.Systems.Map.Scripting.LandItemScriptRunner _itemTakeScripts;

        // The three spell input subscribers, held between Construct and Awake because Unity does not
        // order the two. Same reason and same shape as _pendingEffectResolver above.
        private ISubscriber<SpellCastMessage> _pendingSpellCastSubscriber;
        private ISubscriber<SpellHotkeyMessage> _pendingSpellHotkeySubscriber;
        private PFE.Systems.Map.LandMap _pendingLandMap;
        private PFE.Systems.Audio.ISoundService _pendingSoundService;
        private PFE.Data.ContentRegistry _pendingRegistry;

        // MessagePipe subscriptions (disposable)
        private CompositeDisposable _disposables;

        private Camera _mainCamera;
        private bool _isRunning;
        private float _aimAngle;
        private float _lastLeftTapTime = float.NegativeInfinity;
        private float _lastRightTapTime = float.NegativeInfinity;
        private float _lastDownTapTime = float.NegativeInfinity;
        private bool _wasLeftHeld;
        private bool _wasRightHeld;
        private bool _wasDownHeld;

        public PlayerTelekinesisController Telekinesis => _telekinesis;
        private CharacterStats _characterStats;
        public CharacterStats CharacterStats => _characterStats;

        // VContainer Injection
        [Inject]
        public void Construct(
            InputReader input,
            ISubscriber<AttackMessage> attackSubscriber,
            ISubscriber<TeleportMessage> teleportSubscriber,
            ISubscriber<InteractMessage> interactSubscriber,
            ISubscriber<ReloadMessage> reloadSubscriber,
            ISubscriber<SpellCastMessage> spellCastSubscriber,
            ISubscriber<SpellHotkeyMessage> spellHotkeySubscriber,
            ISubscriber<GrenadeMessage> grenadeSubscriber,
            PFE.Core.PfeDebugSettings debugSettings,
            // Required, not optional: VContainer has no optional parameters (it resolves every [Inject]
            // argument or throws), so a default value here would document a fallback that never happens.
            // ISoundService is already a required dependency of PlayerWeaponLoadout, so it is registered.
            PFE.Systems.Map.LandMap landMap,
            PFE.Systems.Audio.ISoundService soundService,
            // The room's particle emitter. The player needs it for the same reason a spawned NPC
            // does — its effect visuals (the burning flame, the poison drip) are emitted through it —
            // but it CANNOT come down the spawner chain that reaches every other unit, because the
            // player is a scene object rather than something RoomUnitSpawner builds. Without this
            // parameter the player's EffectVisualSink stays null and a burning player shows nothing
            // while a burning enemy shows a flame: the asymmetry is the tell.
            PFE.Systems.Particles.Adapters.RoomParticleEmitter particleEmitter,
            PFE.Data.ContentRegistry registry = null)
        {
            _input = input;
            _debugSettings = debugSettings;
            _pendingLandMap = landMap;
            _pendingSoundService = soundService;
            _pendingRegistry = registry;

            // Held until Awake has built the caster's own collaborators. See WireSpellCasterIfReady.
            _pendingSpellCastSubscriber = spellCastSubscriber;
            _pendingSpellHotkeySubscriber = spellHotkeySubscriber;
            WireSpellCasterIfReady();

            // Same ordering problem, same answer, for the inventory: the registry arrives here, while
            // the loadout and the caster are built in Awake, so both sides call the wire. See
            // WireInventoryIfReady for why the loadout half is order-critical.
            EnsureInventoryComponent();
            WireInventoryIfReady();

            // The effect-definition resolver, built from the DI singleton the container already owns so
            // there is one resolution story rather than a second registration to keep in step. Handed
            // to the player's effect set here — but only if Awake has already built `_unitStats`, since
            // Unity does not order a scene component's Awake against the scope's injection. When it has
            // not, `_pendingEffectResolver` is applied at the end of Awake instead; see ApplyEffect
            // Resolver. A null registry (a plain-Awake test rig) leaves the set resolver-less, which
            // refuses every id rather than materialising a phantom effect.
            _pendingEffectResolver = registry != null
                ? new ContentRegistryEffectDefinitionResolver(registry)
                : null;
            ApplyEffectResolverIfReady();

            // Same ordering problem, same answer. See ApplyParticleEmitterIfReady.
            _pendingParticleEmitter = particleEmitter;
            ApplyParticleEmitterIfReady();

            // And again for radiation: the land map and the sound service arrive here, the stats are
            // built in Awake. Whichever runs second wins; Construct is idempotent.
            WireRadiationTickerIfReady();

            // ── Construct really is called twice, and this guard is why that is harmless ─────────────
            //
            // The player GameObject is injected by two independent paths: `RegisterComponent` in
            // GameLifetimeScope, and the scene scope's `autoInjectGameObjects`, which lists the very
            // same player prefab instance (SampleScene, fileID 1971043193 — a stripped GameObject whose
            // source is the Player prefab). VContainer runs the [Inject] method once per injection, so
            // Construct runs twice and every Subscribe below used to be registered twice: one
            // TeleportMessage arrived as two.
            //
            // Worse, `_disposables` was reassigned without disposing, so the first subscription set was
            // orphaned and could never be disposed. That is exactly how it presented: one physical Q
            // press delivered two press edges, the first grabbed a crate and the second dropped it again
            // in the same frame, so a hold never survived a single frame. Every attack and interact
            // message was silently doubled too.
            //
            // Disposing the previous set makes the duplicate harmless whatever its source; the warning
            // keeps the underlying scene misconfiguration visible instead of hiding it behind the guard.
            if (_disposables != null)
            {
                Debug.LogWarning(
                    "[PlayerController] Construct() called more than once — this component is injected " +
                    "twice (RegisterComponent AND autoInjectGameObjects both name the player GameObject). " +
                    "Re-subscribing; the previous MessagePipe subscriptions are disposed.");
                _disposables.Dispose();
            }

            if (_debugSettings.LogDependencyInjectionConstruct)
                Debug.Log("[PlayerController] Construct() called — dependencies injected.");

            // Subscribe to MessagePipe events
            _disposables = new CompositeDisposable();

            attackSubscriber.Subscribe(message =>
            {
                if (_debugSettings.LogWeaponLifecycle)
                    Debug.Log($"[PlayerController] AttackMessage received — IsStarted={message.IsStarted}.");
                if (message.IsStarted)
                    HandleAttackStart();
                else
                    HandleAttackEnd();
            }).AddTo(_disposables);

            // R — the reload key. AS3's `keyReload` (inter/Ctr.as:24) drives two things from one
            // press: the magazine reload, and — for a radio throwable — `currentWeapon.detonator()`
            // (UnitPlayer.as:2358). Both live behind `IWeaponController.StartReload()`, which the
            // controller decides between using its own definition (`WeaponDefinition.radio`), so
            // there is nothing to branch on here.
            //
            // The press edge only. See ReloadMessage for why a release edge has no consumer.
            reloadSubscriber.Subscribe(message =>
            {
                if (!message.IsStarted) return;
                if (_debugSettings.LogWeaponLifecycle)
                    Debug.Log("[PlayerController] ReloadMessage received — forwarding to the loadout.");
                _loadout?.StartReload();
            }).AddTo(_disposables);

            // G — AS3 `keyGrenad` (UnitPlayer.as:2188-2204). Fires the player's `throwWeapon`, a slot
            // independent of the weapon in hand, which is why this is not routed through Attack.
            //
            // Press edge only, even though the message carries both edges. AS3 reads the held boolean
            // every frame, but the handler itself clears it after a successful throw
            // (`if(this.throwWeapon.tip == 4) { this.ctr.keyGrenad = false; }`, :2196-2199), so one press
            // is one throw. `t_attack`/`t_auto` on the controller then pace anything that slips through.
            grenadeSubscriber.Subscribe(message =>
            {
                if (!message.IsHeld) return;

                bool threw = _loadout != null && _loadout.ThrowGrenade();

                if (_debugSettings.LogWeaponLifecycle)
                {
                    Debug.Log(threw
                        ? $"[PlayerController] GrenadeMessage — throwing '{_loadout.ThrowWeapon?.State.Def.weaponId}'."
                        : "[PlayerController] GrenadeMessage — no throwable in the grenade slot " +
                          "(AS3's `else { keyGrenad = false; }` arm).");
                }
            }).AddTo(_disposables);

            teleportSubscriber.Subscribe(message =>
            {
                // Q does two things at once in AS3 and they are NOT mutually exclusive. While the key
                // is held, actTele() runs ONCE on the press edge (latched by `teleReady`) and the
                // teleport charge `t_port` increments on EVERY frame; on release, actPort() fires if
                // the charge reached portTime (UnitPlayer.as:2141-2176). So a successful grab does not
                // cancel the charge — both run side by side, and AS3 will happily teleport while still
                // holding the prop.
                //
                // The port used to suppress the charge whenever telekinesis consumed the press
                // (`if (handledByTelekinesis) SetTeleportHeld(false)`), so the moment a grab started
                // working, Q stopped being able to teleport at all. The return value still reports
                // whether telekinesis handled the edge; it is just not a decision about the teleport.
                TelekinesisTrace.Log(
                    $"PlayerController got TeleportMessage IsStarted={message.IsStarted}  " +
                    $"telekinesis={(_telekinesis != null ? "present" : "NULL")}  " +
                    $"locomotion={(_locomotion != null ? "present" : "NULL")}");

                // Delivery count, so "the input published two edges" and "the broker delivered two"
                // are distinguishable from each other in the flight log.
                TelekinesisRecorder.Write($"[DELIVER] TeleportMessage IsStarted={message.IsStarted}");

                if (_telekinesis != null)
                {
                    _telekinesis.OnTeleportKeyPressed(message.IsStarted);
                }

                if (_locomotion != null)
                {
                    _locomotion.SetTeleportHeld(message.IsStarted);
                }
            }).AddTo(_disposables);

            interactSubscriber.Subscribe(message =>
            {
                if (message.IsPressed)
                {
                    HandleInteract();
                }
                else
                {
                    EndInteract();
                }
            }).AddTo(_disposables);
        }

        /// <summary>
        /// The player's effect set uses the <b>player</b> param-replay path — AS3
        /// <c>Pers.setParameters</c> rather than <c>Unit.setEffParams</c>.
        ///
        /// <para>The two differ in two ways that both matter: the index is <c>eff.lvl</c> (not a
        /// hardcoded 1) and effects that are being removed are <b>skipped</b> (<c>Pers.as:2202</c>),
        /// where the NPC path replays them with index 0. So the mode has to be set from the player side
        /// or every player effect would resolve its per-level value from the wrong vector entry.</para>
        /// </summary>
        public override void SetEffectResolver(IEffectDefinitionResolver resolver)
        {
            base._unitStats?.EnsureEffects(resolver, PFE.Data.Definitions.PersMode.Player);
        }

        /// <summary>
        /// The resolver the container hands over in <see cref="Construct"/>, held until the unit's
        /// effect set exists.
        ///
        /// <para><b>Why it cannot just be applied in <c>Construct</c>.</b> The player object is
        /// injected by VContainer — which runs during the scope's build — but <c>_unitStats</c> is
        /// created in <c>Awake</c>, and Unity does not order the two. Applying in whichever of
        /// <c>Construct</c>/<c>Awake</c> runs second removes the dependency on that order rather than
        /// asserting one.</para>
        /// </summary>
        private IEffectDefinitionResolver _pendingEffectResolver;

        private void ApplyEffectResolverIfReady()
        {
            if (_pendingEffectResolver != null && base._unitStats != null)
            {
                SetEffectResolver(_pendingEffectResolver);
            }
        }

        /// <summary>
        /// The room's particle emitter, held until <c>Awake</c> has built the unit's effect set.
        ///
        /// <para><b>Why the player needs this at all.</b> Every other unit gets its emitter from
        /// <c>RoomUnitSpawner</c>, which builds them and hands it over as it goes. The player is a
        /// <i>scene object</i> — nothing spawns it — so it is in none of that chain and its
        /// <c>EffectVisualSink</c> stayed null. The symptom is precisely asymmetric: a burning enemy
        /// draws a flame, a burning player draws nothing, which reads as "the player is special"
        /// rather than "the player missed a handover".</para>
        ///
        /// <para><b>Why it cannot just be applied in <c>Construct</c>.</b> Identical to
        /// <see cref="ApplyEffectResolverIfReady"/>: the emitter arrives by VContainer injection, which
        /// runs during the scope's build, while <c>_unitStats</c> is created in <c>Awake</c> — and
        /// <c>UnitController.SetParticleEmitter</c> only installs the sink when <c>_unitStats</c>
        /// already exists. Applying in whichever of <c>Construct</c>/<c>Awake</c> runs second removes
        /// the dependency on that order instead of asserting one.</para>
        /// </summary>
        private PFE.Systems.Particles.Adapters.RoomParticleEmitter _pendingParticleEmitter;

        private void ApplyParticleEmitterIfReady()
        {
            if (_pendingParticleEmitter != null && base._unitStats != null)
            {
                SetParticleEmitter(_pendingParticleEmitter);
            }
        }

        /// <summary>
        /// Wires the spell caster once both halves exist — the component and its collaborators from
        /// <c>Awake</c>, the injected world/sound/registry/subscribers from <c>Construct</c>. Called from
        /// both, so it does not matter which runs first.
        ///
        /// <para>Without this the caster would be built with a null <c>LandMap</c> (no line-of-sight ray)
        /// or, worse, with no input subscriptions at all — the latter presents as "C does nothing" and
        /// the former as "spells cast through walls", neither of which points at the ordering.</para>
        /// </summary>
        private void WireSpellCasterIfReady()
        {
            if (_spellCaster == null || base._unitStats == null || _characterStats == null) return;
            if (_pendingSpellCastSubscriber == null) return;

            // The magic mount is optional: a rig without WeaponMounts falls back to the body position,
            // which is what the weapon path does too. Not a reason to leave the caster unwired.
            var mounts = GetComponent<WeaponMounts>() ?? GetComponentInChildren<WeaponMounts>();

            _spellCaster.Construct(
                _characterStats,
                base._unitStats,
                mounts,
                _pendingLandMap,
                _pendingSoundService,
                _pendingRegistry,
                // AS3 casts at the cursor (World.w.celX/celY). The loadout already receives the projected
                // mouse world position every frame, so the spell aims where the weapon aims.
                aimProvider: () => _loadout != null ? _loadout.AimTarget : Vector2.zero);

            _spellCaster.BindInput(_pendingSpellCastSubscriber, _pendingSpellHotkeySubscriber);
        }

        /// <summary>
        /// Create the player's inventory component if it is not already present.
        ///
        /// <para><b>Added at runtime, not authored into the prefab.</b> The player is assembled from
        /// components in <c>Awake</c> (stats, mana ticker, spell caster), so a runtime-only component
        /// needs no scene edit — and a scene edit is the owner's call, not the agent's (rule #5).
        /// Idempotent because <c>Construct</c> and <c>Awake</c> both call it, in either order.</para>
        /// </summary>
        private void EnsureInventoryComponent()
        {
            if (_inventoryComponent != null) return;
            _inventoryComponent = GetComponent<PlayerInventory>() ?? gameObject.AddComponent<PlayerInventory>();
        }

        /// <summary>
        /// Binds the radiation ticker's collaborators from whichever of <c>Construct</c>/<c>Awake</c>
        /// ran second — the same two-sided wire as <see cref="WireInventoryIfReady"/>.
        ///
        /// <para><b>Why it cannot be done once.</b> The land map and the sound service are injected
        /// (<c>Construct</c>); <c>CharacterStats</c> and <c>UnitStats</c> are built in <c>Awake</c>.
        /// A ticker bound from only one of those moments would run with a null room — no ambient
        /// radiation and no object scan — or with null stats, i.e. no exposure at all.</para>
        /// </summary>
        private void WireRadiationTickerIfReady()
        {
            if (_radiationTicker == null || _characterStats == null || base._unitStats == null)
            {
                return;
            }

            _radiationTicker.Construct(this, _characterStats, base._unitStats, _pendingLandMap, _pendingSoundService);

            // The Rad Child perk scales organ damage by the player's remaining headroom
            // (Pers.as:1675-1678), so the RPG layer needs the exposure number too.
            _characterStats.RadiationProvider = () => _radiationTicker != null ? _radiationTicker.State.Rad : 0f;

            // RadAway reaches the same state through the inventory, which is built in a different
            // method and may not exist yet. Setting it here and in WireInventoryIfReady covers both
            // orders.
            if (_inventoryComponent != null)
            {
                _inventoryComponent.Radiation = _radiationTicker.State;
            }
        }

        /// <summary>
        /// Build and bind the inventory from whichever of <c>Construct</c>/<c>Awake</c> ran second.
        ///
        /// <para><b>Both sides are needed and neither can do it alone:</b> the content registry arrives
        /// in <c>Construct</c> (it is the container's), while the loadout and the spell caster are built
        /// in <c>Awake</c>. So each side calls this and it does whatever is possible at that moment.</para>
        ///
        /// <para><b>The loadout binding is order-critical.</b> <c>PlayerWeaponLoadout.Start</c> equips
        /// the starting weapon, and the <c>AmmoSource</c> setter only rebuilds the factory for
        /// <i>future</i> equips — so a weapon equipped before this runs keeps a null ammo source and
        /// reloads for free (infinite ammo) for the rest of its life. <c>Awake</c> precedes
        /// <c>Start</c>, so calling this from <c>Awake</c> is what makes that impossible.</para>
        /// </summary>
        private void WireInventoryIfReady()
        {
            if (_inventoryComponent == null) return;

            if (_loadout != null) _inventoryComponent.BindLoadout(_loadout);
            if (_spellCaster != null) _inventoryComponent.BindSpellCaster(_spellCaster);

            // Null before injection; PlayerInventory tolerates it and rebuilds its sink once the real
            // registry arrives, so calling this with null is a no-op rather than a permanent downgrade.
            _inventoryComponent.Construct(_pendingRegistry);

            // RadAway's destination. Null until the radiation ticker is built (it is created later in
            // Awake than this can run), so WireRadiationTickerIfReady sets it again from the other side.
            _inventoryComponent.Radiation = Radiation;
        }

        protected override void Awake()
        {
            base.Awake();
            _mainCamera = Camera.main;
            _tilePhysics = GetComponent<TilePhysicsController>();
            _locomotion = GetComponent<PlayerLocomotionController>();
            _loadout = GetComponent<PlayerWeaponLoadout>();
            _actionInteractor = GetComponent<PlayerActionInteractor>();
            _telekinesis = GetComponent<PlayerTelekinesisController>() ?? gameObject.AddComponent<PlayerTelekinesisController>();
            base._unitStats = new UnitStats();
            _characterStats = GetComponent<CharacterStats>() ?? gameObject.AddComponent<CharacterStats>();
            _characterStats.BindUnitStats(base._unitStats);

            // AS3 `new Pers()` applies `setGlobalDif(globalDif)` at construction (Pers.as:517-528), and
            // `Game.changeDif` re-applies it plus `setParameters()` on every change (Game.as:326-327).
            // Both halves land here: the setting is read once now, and the event covers a mid-run change
            // (the dev-console `dif` verb). Applied on the PLAYER only — the oracle's table is
            // `World.w.pers`, and an NPC has no Pers.
            _characterStats.ApplyGlobalDifficulty(PFE.Systems.Campaign.GlobalDifficulty.Value);
            PFE.Systems.Campaign.GlobalDifficulty.Changed += OnGlobalDifficultyChanged;

            // The player builds its own stats rather than receiving them from RoomUnitSpawner, so it
            // does not pass through UnitController.Initialize — and without this the player's effect
            // payload damage (a burn tick, an acid tick) would fall back to the raw HP subtraction:
            // no vulnerability, no armour, no damage number, and no death check.
            AttachEffectDamageSink();

            // AS3's mana block. Registered on the sim clock by AttachSimulation below; until that
            // runs it falls back to FixedUpdate and warns, so it is never silently absent.
            _manaTicker = GetComponent<PlayerManaTicker>() ?? gameObject.AddComponent<PlayerManaTicker>();
            _manaTicker.Construct(_characterStats, _locomotion, _telekinesis);

            // AS3's radiation block. Created here and wired from BOTH sides, because the land map and
            // the sound service arrive in Construct while `_characterStats`/`_unitStats` are built here
            // — Unity does not order the two. Construct is idempotent.
            _radiationTicker = GetComponent<PlayerRadiationTicker>() ?? gameObject.AddComponent<PlayerRadiationTicker>();
            WireRadiationTickerIfReady();

            // AS3's spell half. The component is created here so `GetComponentInParent<PlayerSpellCaster>`
            // finds it the moment the inventory asks to select a spell, but its dependencies are wired by
            // WireSpellCasterIfReady below — the container's half arrives in Construct and Unity does not
            // order Construct against Awake.
            _spellCaster = GetComponent<PlayerSpellCaster>() ?? gameObject.AddComponent<PlayerSpellCaster>();
            WireSpellCasterIfReady();

            // The player's inventory. Created here for the same reason as the caster — the player is
            // assembled in Awake, so a runtime-only component needs no scene edit — and wired from both
            // sides because Unity does not order Awake against the container's injection.
            EnsureInventoryComponent();
            WireInventoryIfReady();

            // AS3 Pers.setInvParameters(this.gg.invent) (Pers.as:2227) reads the inventory during every
            // full stat recalculation, and AS3 Invent.take ends in gg.pers.setParameters()
            // (Invent.as:1220) — so PICKING UP the screwdriver is what grants possLockPick, and
            // dropping it removes the grant on the next recalc. Both halves land here: the recalc
            // reads rows through the inventory (step 5.5), and every add/remove mutation schedules
            // one. ItemQuantityAdded covers every arrival including stack growth (the cogwheel
            // lesson, see PlayerInventory.Construct); OnItemRemoved covers a spent stack, whose row
            // lingers at Quantity 0 and must stop granting.
            _characterStats.ItemSkillSource = _inventoryComponent;
            if (_inventoryComponent.Inventory != null)
            {
                _inventoryComponent.Inventory.ItemQuantityAdded += OnInventoryStatsDirty;
                _inventoryComponent.Inventory.OnItemRemoved += OnItemRemovedRecalc;
            }

            // Walk-over loot collection. Created AFTER the inventory block above because it needs the
            // PlayerInventory that block has just ensured exists; putting it on the sim clock is
            // AttachSimulation's job below.
            _autoPickup = GetComponent<PlayerAutoPickup>() ?? gameObject.AddComponent<PlayerAutoPickup>();
            _autoPickup.Construct(_inventoryComponent, this);

            // The land's `eve='take'` item scripts. Built after the inventory for the same reason
            // `_autoPickup` is: it subscribes to that inventory's OnItemAdded/OnWeaponAdded. The
            // LandMap is passed as a factory because the injection that fills `_pendingLandMap` is not
            // ordered against this Awake.
            _itemTakeScripts = GetComponent<PFE.Systems.Map.Scripting.LandItemScriptRunner>()
                ?? gameObject.AddComponent<PFE.Systems.Map.Scripting.LandItemScriptRunner>();
            _itemTakeScripts.Construct(_inventoryComponent, () => _pendingLandMap);

            // AS3 Pers.die() fires from inside damage() / bloodDamage() / manaDamage() when an organ
            // reaches zero (Pers.as:1693-1697, :1793-1797, :1739-1743). CharacterStats raises the
            // event; what dying means is this class's business.
            _characterStats.onDeath += OnCharacterDeath;

            if (_locomotion != null)
            {
                _locomotion.SetUnitStats(base._unitStats);
                // AS3's UnitPlayer.control() rebuilds `precMult` from the player's locomotion state
                // every tick (UnitPlayer.as:1181-1200), and the weapon reads the composed value when
                // it fires. The locomotion controller owns that state, so it publishes it here rather
                // than the weapon layer reaching back into movement.
                _locomotion.SetCharacterStats(_characterStats);
            }

            // If Construct already ran (injection before Awake), this applies the resolver the
            // container handed over; if Construct has not run yet it is a no-op and Construct's own
            // call applies it. Either order ends with the player's effect set wired.
            ApplyEffectResolverIfReady();

            // …and the same for the effect VISUALS. Without this second call a burning player emits
            // no flame even though every enemy does, because the emitter only reaches this class by
            // injection and the sink only installs once _unitStats exists.
            ApplyParticleEmitterIfReady();
        }

        /// <summary>
        /// Adds the mana block to the sim clock alongside the base registration.
        ///
        /// <para>The base registers this UnitController at <c>SimTickOrder.UnitsAndAi</c>, but its
        /// <c>SimTick</c> returns early for a motor-driven unit — which the player is — so that
        /// registration does no work for the player. The mana ticker therefore needs its own
        /// registration, and this is the only hook the engine calls to hand out the clock.</para>
        /// </summary>
        public override void AttachSimulation(PFE.Core.SimClock clock, PFE.Core.SimLoop loop)
        {
            base.AttachSimulation(clock, loop);

            if (_manaTicker != null)
            {
                _manaTicker.Attach(clock, loop);
            }

            // Registered after the mana block, at the same PlayerMotor order, so exposure is billed
            // after the motor has placed the player for this tick — walking into a reactor costs the
            // frame you walked in, not the one before it.
            if (_radiationTicker != null)
            {
                _radiationTicker.Attach(clock, loop);
            }

            // Registered after the mana ticker on purpose: both sit at SimTickOrder.PlayerMotor, and at
            // equal order SimLoop runs them in registration order — so the mana block resolves before a
            // cast can spend from the pool, which is the AS3 order (step()'s mana block precedes
            // control()).
            if (_spellCaster != null)
            {
                _spellCaster.Attach(clock, loop);
            }

            // Loot collection, also at PlayerMotor order and registered last. At equal order SimLoop runs
            // in registration order, and this one only reads the player's transform — it owns no state any
            // earlier block depends on — so last is both safe and the least surprising place for it.
            if (_autoPickup != null)
            {
                _autoPickup.Attach(clock, loop);
            }
        }

        protected override void OnDestroy()
        {
            PFE.Systems.Campaign.GlobalDifficulty.Changed -= OnGlobalDifficultyChanged;

            if (_characterStats != null)
            {
                _characterStats.onDeath -= OnCharacterDeath;
            }

            if (_inventoryComponent != null && _inventoryComponent.Inventory != null)
            {
                _inventoryComponent.Inventory.ItemQuantityAdded -= OnInventoryStatsDirty;
                _inventoryComponent.Inventory.OnItemRemoved -= OnItemRemovedRecalc;
            }

            // Clean up MessagePipe subscriptions
            _disposables?.Dispose();

            // The base drops this unit's SimLoop registration. This override used to be `private`,
            // which HIDES a base `OnDestroy` rather than calling it — Unity's message dispatch finds
            // the most-derived declaration and invokes only that. Harmless while the base had no
            // OnDestroy; a leaked registration the moment it gained one.
            base.OnDestroy();
        }

        /// <summary>
        /// AS3 <c>Game.changeDif</c>'s tail (<c>Game.as:326-327</c>): re-apply the parameter table and
        /// recompute the derived block. Subscribed in <c>Awake</c>, dropped in <c>OnDestroy</c>.
        /// </summary>
        private void OnGlobalDifficultyChanged(int globalDif)
        {
            if (_characterStats == null) return;

            _characterStats.ApplyGlobalDifficulty(globalDif);
            Debug.Log($"[PlayerController] globalDif changed to {globalDif}; the Pers parameter table was " +
                      "re-applied (organMult/dieDamage/teleMana/difCapsMult).");
        }

        /// <summary>
        /// The port of AS3 <c>Invent.take</c>'s tail (<c>Invent.as:1220</c>): a full recalculation after
        /// every inventory mutation, which is what makes carrying the screwdriver grant
        /// <c>possLockPick</c> the moment it lands in the bag. Also fired by <c>usePotion</c>'s three
        /// call sites in AS3 (Invent.as:397/458/477) — the port's consumable path raises the same two
        /// events, so one handler covers both.
        /// </summary>
        private void OnInventoryStatsDirty(GameItemInstance item, int quantity)
        {
            if (_characterStats != null) _characterStats.RecalculateStats();
        }

        private void OnItemRemovedRecalc(GameItemInstance item)
        {
            if (_characterStats != null) _characterStats.RecalculateStats();
        }

        private void Update()
        {
            HandleAiming();
            HandleMovementInput();

            // Refreshed every frame, before anything reads it: this is the port of AS3's
            // `World.w.loc.celObj`, which `Location.step` recomputes on every frame
            // (Location.as:3371-3394) and `GUI.setCelObj` then draws the floating prompt from
            // (GUI.as:1244-1467). Gated on the same cutscene lock the interact key uses — a script
            // that has taken control must not leave a prompt hanging over an object the player
            // cannot work.
            //
            // The cost is one `Physics2D.OverlapCircleAll` at the cursor per frame, which is what
            // the Oracle's own per-frame object scan costs; it is not a scene walk.
            CurrentInteractTarget = ControlActive ? FindCursorTarget() : null;

            // Direct interact check (E key or Right-Click). Both edges are read, because acting is a
            // hold: AS3 keeps keyAction as a held boolean (set at Ctr.as:699, cleared at :797) and
            // UnitPlayer.as:2115-2135 acts on the false edge by abandoning the action. The MessagePipe
            // subscription above carries the same two edges; both paths are guarded per frame below,
            // so the redundancy cannot double-fire.
            bool ePressed = Input.GetKeyDown(KeyCode.E) ||
                (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.eKey.wasPressedThisFrame);
            bool rightClick = Input.GetMouseButtonDown(1) ||
                (UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.rightButton.wasPressedThisFrame);

            // Gated on the cutscene lock: `control off` must also stop the player opening a door or
            // grabbing a terminal while a script is talking. `EndInteract` below is deliberately NOT
            // gated — if the lock lands while the key is held, the release still has to be delivered or
            // the interactor stays stuck in its "acting" state for the rest of the session.
            if (ControlActive && (ePressed || rightClick))
            {
                HandleInteract();
            }

            bool eReleased = Input.GetKeyUp(KeyCode.E) ||
                (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.eKey.wasReleasedThisFrame);
            bool rightReleased = Input.GetMouseButtonUp(1) ||
                (UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.rightButton.wasReleasedThisFrame);

            if (eReleased || rightReleased)
            {
                EndInteract();
            }
        }

        /// <summary>
        /// Handle WASD movement input.
        /// Replaces: control() function from UnitPlayer.as
        /// </summary>
        private void HandleMovementInput()
        {
            if (_input == null)
            {
                return;
            }

            // AS3 `ggControl` gates the whole control block at `World.as:1379`. Substituting a zero
            // vector rather than early-returning keeps the rest of this method running, which matters:
            // the locomotion controller has to be told "no input" so it decelerates, whereas an early
            // return would leave it holding the last direction for the whole cutscene.
            Vector2 input = ControlActive ? _input.CurrentMoveInput : Vector2.zero;

            // Arm the loot-take window — AS3's `this.isTake = 40`, set while a horizontal direction is
            // HELD (UnitPlayer.as:2551, :2570) or on a jump press (:2685). It is assigned, not
            // accumulated, so holding a direction pins it at 40 rather than growing it, which is what
            // makes "walk over loot and it gets picked up" a property of walking rather than of pressing
            // something. Armed here, on the frame boundary, because a tick must not read Input: the tick
            // only ages the value this sets.
            if (_autoPickup != null &&
                (Mathf.Abs(input.x) > 0.1f || (_input.Jump != null && _input.Jump.WasPressedThisFrame())))
            {
                _autoPickup.ArmTakeWindow();
            }

            if (_locomotion != null)
            {
                // Every edge and level below is gated on the same lock as the move vector: a cutscene
                // that let the player jump or dash would be worse than one that let them walk, because
                // it also breaks the animation the script is playing.
                bool jumpHeld = ControlActive && _input.IsJumpHeld;
                bool runHeld = ControlActive && _input.IsDashing.Value;
                bool jumpPressed = ControlActive && _input.Jump != null && _input.Jump.WasPressedThisFrame();
                bool dashPressed = runHeld && DetectHorizontalDoubleTap(input.x);
                bool dropThroughPressed = DetectDownDoubleTap(input.y);
                // Provide mouse cursor world position for teleport targeting (AS3: World.w.celX/celY)
                //
                // Two things matter here and both were wrong.
                //
                // (1) `_mainCamera` is cached from Camera.main at Awake, so a camera that is created
                //     later leaves it null forever. Retry it here; `Camera.main` is cheap and cached by
                //     Unity, unlike a scene scan.
                // (2) ONLY hand the cursor over when we actually have one. `SetCursorWorldPixels` sets
                //     `_cursorSetExternally = true`, which *disables* the telekinesis controller's own
                //     fallback (`UpdateCursorPositionIfNeeded`, which additionally tries
                //     `FindFirstObjectByType<Camera>()`). Forwarding a `Vector2.zero` placeholder
                //     therefore pins the cursor to the room origin and makes every grab miss -- while
                //     looking exactly like a physics or mass problem.
                //
                // Skipping the forward is free for locomotion: it has no set-externally flag and its
                // field already defaults to `Vector2.zero`.
                if (_mainCamera == null)
                {
                    _mainCamera = Camera.main;
                }

                if (_mainCamera != null)
                {
                    // (3) `Input.mousePosition.z` is always 0, and `ScreenToWorldPoint` reads that z as
                    //     the DISTANCE IN FRONT OF THE CAMERA. On a perspective camera a z of 0 is the
                    //     near plane, so the "world point" comes back sitting on the camera -- and the
                    //     camera follows the player, so the cursor silently tracks the player instead of
                    //     the mouse. Every grab then misses, because the cursor never leaves the player's
                    //     feet, and it looks exactly like a range or mass problem. The gameplay camera
                    //     here IS perspective (see ColliderDebugOverlay.GetViewRect, UnitHealthOverlay
                    //     .SelectVisible, both of which had to stop branching on `orthographic`), so the
                    //     fix is to pass the distance to the plane the rooms live on: z = 0.
                    //
                    //     For an orthographic camera this assignment is a no-op -- the projection ignores
                    //     z for x/y -- so the same two lines are correct for both. This mirrors the
                    //     already-correct site in HandleCursorInteraction below (line ~479) and in
                    //     PlayerTelekinesisController.UpdateCursorPositionIfNeeded.
                    Vector3 mouseScreen = Input.mousePosition;
                    if (!_mainCamera.orthographic)
                    {
                        mouseScreen.z = -_mainCamera.transform.position.z;
                    }

                    Vector3 mouseWorld = _mainCamera.ScreenToWorldPoint(mouseScreen);
                    // Convert Unity world units to pixel space
                    Vector2 cursorWorldPixels = new Vector2(
                        mouseWorld.x * TileQueryConstants.UnitToPixel,
                        mouseWorld.y * TileQueryConstants.UnitToPixel);
                    _locomotion.SetCursorWorldPixels(cursorWorldPixels);
                    _telekinesis?.SetCursorWorldPixels(cursorWorldPixels);
                }

                _locomotion.SetIntent(input, jumpHeld, runHeld, jumpPressed, dashPressed, dropThroughPressed, _aimAngle);
                _isGrounded = _locomotion.CurrentSnapshot.IsGrounded;
                _isRunning = _locomotion.CurrentSnapshot.IsRunning;
                return;
            }

            if (_tilePhysics != null)
            {
                // Route input through tile physics controller
                bool jump = Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.W);
                bool down = Input.GetKey(KeyCode.S);
                _tilePhysics.SetInput(input.x, jump, down);

                // Sync grounded state from tile physics
                _isGrounded = _tilePhysics.IsGrounded;
                return;
            }

            // Fallback: original Rigidbody2D movement (if no TilePhysicsController)
            if (input == Vector2.zero)
            {
                _isRunning = false;
                return;
            }

            float targetSpeed = _isRunning ? base._stats.RunSpeed : base._stats.WalkSpeed;
            float acceleration = base._stats.Acceleration * Time.deltaTime * 50f;
            float targetVelocityX = input.x * targetSpeed;
            _velocity.x = Mathf.MoveTowards(_velocity.x, targetVelocityX, acceleration);
        }

        private bool DetectHorizontalDoubleTap(float horizontalInput)
        {
            const float threshold = 0.5f;
            float currentTime = Time.unscaledTime;
            bool dashTriggered = false;

            bool leftHeld = horizontalInput <= -threshold;
            bool rightHeld = horizontalInput >= threshold;

            if (leftHeld && !_wasLeftHeld)
            {
                dashTriggered = currentTime - _lastLeftTapTime <= _doubleTapWindowSeconds;
                _lastLeftTapTime = currentTime;
            }

            if (rightHeld && !_wasRightHeld)
            {
                dashTriggered = currentTime - _lastRightTapTime <= _doubleTapWindowSeconds;
                _lastRightTapTime = currentTime;
            }

            _wasLeftHeld = leftHeld;
            _wasRightHeld = rightHeld;
            return dashTriggered;
        }

        private bool DetectDownDoubleTap(float verticalInput)
        {
            const float threshold = 0.5f;
            float currentTime = Time.unscaledTime;
            bool downHeld = verticalInput <= -threshold;
            bool dropTriggered = false;

            if (downHeld && !_wasDownHeld)
            {
                dropTriggered = currentTime - _lastDownTapTime <= _doubleTapWindowSeconds;
                _lastDownTapTime = currentTime;
            }

            _wasDownHeld = downHeld;
            return dropTriggered;
        }

        /// <summary>
        /// Handle weapon aiming towards mouse cursor.
        /// Original PFE had weapons rotate around the player character.
        /// </summary>
        private void HandleAiming()
        {
            if (!_mouseAiming || _mainCamera == null) return;

            Vector3 mouseScreen = Input.mousePosition;
            // Same near-plane trap as HandleMovementInput above: `Input.mousePosition.z` is 0, which on
            // a perspective camera means "on the camera", not "on the z = 0 plane the rooms live on".
            // Aim then collapses toward the player and the facing is wrong. Pass the plane distance.
            if (!_mainCamera.orthographic)
            {
                mouseScreen.z = -_mainCamera.transform.position.z;
            }

            Vector3 mouseWorld  = _mainCamera.ScreenToWorldPoint(mouseScreen);
            mouseWorld.z = 0f;

            Vector2 aimDirection = mouseWorld - transform.position;
            _aimAngle = Mathf.Atan2(aimDirection.y, aimDirection.x) * Mathf.Rad2Deg;

            // Push the aim target into the loadout; the controller and the presenter read it from there.
            _loadout?.SetAimTarget(mouseWorld);
        }

        /// <summary>Start attacking when attack button is pressed.</summary>
        private void HandleAttackStart()
        {
            // If the mouse is aimed directly at an interactable within reach, interact instead of
            // attacking. Cursor only — this must not fall back to the nearest target, or a click meant
            // as a shot would be swallowed by whatever happens to be standing next to the player.
            //
            // Routed through the same hold/immediate decision as the interact key, so a Z door needs
            // its hold here too rather than opening on the click. There is deliberately no matching
            // release: a hold begun by a click simply runs to completion, or is abandoned by walking
            // away (the range guard). Letting go of the attack button cancelling an E-initiated hold
            // would be a surprising coupling.
            if (BeginOrPerformInteract(FindCursorTarget()))
            {
                return;
            }

            _loadout?.BeginAttack();
        }

        /// <summary>Stop attacking when attack button is released.</summary>
        private void HandleAttackEnd()
        {
            _loadout?.EndAttack();
        }

        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>
        /// The player is player-controlled. AS3's <c>Unit.player</c>.
        ///
        /// <para><b>This was a latent lie until now:</b> <c>UnitController.IsPlayer</c> is
        /// <c>virtual … => false</c> and nothing overrode it, so every unit — the player included —
        /// reported <c>false</c>. Nothing in production read it, which is why it never showed, but
        /// AS3 branches on <c>_loc1_.player</c> in the explosion path
        /// (<c>weapon/Bullet.as:782</c>, <c>:817</c>) and that branch is exactly where the player's
        /// own-explosion multiplier lives.</para>
        /// </summary>
        public override bool IsPlayer => true;

        /// <summary>
        /// The player's faction. AS3 sets this in code, not in data — <c>UnitPlayer.as:385</c>
        /// <c>fraction = F_PLAYER</c> — because <c>littlepip</c> carries no <c>fraction</c> attribute
        /// in <c>AllData.as</c> at all. Overriding here rather than writing <c>Stats.fraction</c>
        /// keeps the shared <c>UnitDefinition</c> asset untouched.
        /// </summary>
        public override PFE.Data.Definitions.FactionType Faction => PFE.Data.Definitions.FactionType.Player;

        /// <summary>
        /// AS3 <c>UnitPlayer.levit &gt; 0</c> (<c>unit/UnitPlayer.as:1113-1116</c>) — levitation is one of
        /// the two per-tick <c>demask</c> writers, so it reaches perception the same way the fast-move
        /// raise does. Read from the locomotion controller, which owns the state; null is legal before
        /// <c>Awake</c> wires it and means "not levitating".
        /// </summary>
        protected override bool StealthLevitatingSource => _locomotion != null && _locomotion.IsLevitating;

        /// <summary>
        /// AS3 <c>UnitPlayer.teleObj</c> (<c>unit/UnitPlayer.as:1239-1242</c>) — the other per-tick
        /// <c>demask</c> writer. Held telekinetically, the player is revealed as plainly as a levitating
        /// one; the state ends when the object is dropped or thrown.
        /// </summary>
        protected override bool StealthTelekinesisSource => _telekinesis != null && _telekinesis.IsHoldingObject;

        /// <summary>
        /// Apply damage to the player.
        /// Overrides base implementation to add player-specific death handling.
        /// </summary>
        public override void TakeDamage(float damage)
        {
            // Call base implementation (handles UnitStats damage and death check)
            base.TakeDamage(damage);

            if (_characterStats != null)
            {
                _characterStats.ApplyOrganDamage(damage);
            }

            // Player-specific damage feedback
            // TODO: Trigger damage feedback (screen shake, flash, etc.)
        }

        public override bool ApplyDamage(in DamageOutcome outcome)
        {
            bool broke = base.ApplyDamage(outcome);
            if (_characterStats != null && outcome.HpDamage > 0f)
            {
                _characterStats.ApplyOrganDamage(outcome.HpDamage);
            }
            return broke;
        }

        // Organ trauma can fire more than once (each of head/torso/legs/blood is checked), and a
        // dead player must not run the death path repeatedly.
        private bool _characterDeathHandled;

        /// <summary>
        /// Organ-level death — AS3's <c>Pers.die()</c>. Distinct from the <c>UnitStats.CurrentHp</c>
        /// path that already reaches <see cref="OnDeath"/>: in AS3 an organ hitting zero kills you
        /// even while the outer HP bar still has points in it.
        /// </summary>
        private void OnCharacterDeath()
        {
            if (_characterDeathHandled) return;

            // Funnel through RaiseDeath so this path consults the same multiplayer authority seam as
            // the two UnitController death paths (UnitController.IsAuthoritativeForDeath). The latch
            // is only set when the death was actually acted on, so a suppressed death does not latch
            // and block a later authoritative one.
            if (RaiseDeath()) _characterDeathHandled = true;
        }

        /// <summary>
        /// The player is never a loot source. See <c>UnitController.DropsLootOnDeath</c> — without this
        /// the player's own death would run the drop path and log an "unmatched key" warning for
        /// <c>littlepip</c>, which names no table.
        /// </summary>
        protected override bool DropsLootOnDeath => false;

        /// <summary>
        /// Override OnDeath to handle player-specific death logic.
        /// </summary>
        protected override void OnDeath()
        {
            base.OnDeath();

            // Player-specific death handling
            Debug.Log("[PlayerController] LittlePip has died!");

            // TODO: Show game over screen
            // TODO: Reload last save
        }

        /// <summary>
        /// Handle player death.
        /// </summary>
        private void Die()
        {
            Debug.Log("[Player] LittlePip has died!");

            // TODO: Show game over screen
            // TODO: Reload last save
        }

        /// <summary>
        /// The radiation block's death arm — <c>UnitPlayer.as:929-931</c>, <c>if(hp &lt;= 0) this.die()</c>.
        ///
        /// <para><b>Why this is a method and not a direct <c>CurrentHp</c> write in the ticker.</b> The
        /// hp has already been clamped by the ticker; what is left is the <i>reaction</i>, and every
        /// death in the port funnels through <see cref="PFE.Entities.Units.UnitController.RaiseDeath"/>
        /// so the multiplayer authority gate and the once-only latch both apply. Writing hp to zero from
        /// a sibling component would leave a player at 0 hp and still standing.</para>
        /// </summary>
        public void KillFromRadiation()
        {
            if (base._unitStats == null || !IsAlive)
            {
                return;
            }

            base._unitStats.CurrentHp.Value = 0f;

            if (!IsAlive)
            {
                RaiseDeath();
            }
        }

        /// <summary>
        /// A radioactive source's elemental damage, paid out every 30 frames by
        /// <see cref="PlayerRadiationTicker"/> — <c>UnitPlayer.as:1536-1551</c>.
        ///
        /// <para>Routed through <c>ReportEffectDamage</c> rather than <c>UnitStats.Damage</c> so armour,
        /// vulnerabilities, the floating number and the death check all apply — the same reason an
        /// effect tick does. The oracle's own call is <c>damage(val, type, null, true)</c>, which is
        /// exactly that path's shape.</para>
        /// </summary>
        public void ApplyRadiationElementalDamage(float damage, PFE.Data.Definitions.DamageType type)
        {
            ReportEffectDamage(damage, type);
        }

        // There is deliberately no serialized interaction radius any more. The reach is AS3's
        // World.w.actionDist, read from WorldConstants.ACTION_REACH by both this class and every
        // IInteractable, so a scene cannot widen it by accident and the two cannot drift apart.

        /// <summary>
        /// The interactable under the cursor, within reach, as of this frame — the port of AS3's
        /// <c>World.w.loc.celObj</c>. <c>Location.step</c> refreshes that every frame
        /// (<c>Location.as:3371-3394</c>) and <c>GUI.setCelObj</c> reads it to draw the world-anchored
        /// interaction prompt (<c>GUI.as:1244-1467</c>); <c>Update</c> refreshes this one the same way.
        ///
        /// <para>Null when nothing is under the cursor, when the target is out of reach, or while a
        /// script holds control. Read-only: the prompt view consumes it, nothing writes it but
        /// <see cref="Update"/>.</para>
        /// </summary>
        public IInteractable CurrentInteractTarget { get; private set; }

        /// <summary>
        /// Resolves what the player is trying to act on, <b>without acting</b>: the interactable
        /// under the cursor, if one is within reach.
        ///
        /// <para><b>There is no nearest-target fallback, and adding one back is a bug.</b> AS3
        /// requires the cursor to be on the object — <c>UnitPlayer.actAction</c> tests
        /// <c>loc.celObj &amp;&amp; loc.celObj.onCursor &amp;&amp; loc.celDist &lt;= World.w.actionDist</c>
        /// (<c>UnitPlayer.as:1931</c>) — and when nothing is under the cursor it does nothing at all.
        /// The old <c>?? FindNearestTarget()</c> here made <c>E</c> work from anywhere near a door
        /// regardless of where the player was pointing, which is not a behaviour AS3 has anywhere in
        /// the interaction path.</para>
        ///
        /// <para>Split out of the old <c>HandleInteract</c> because the press has to <i>decide</i>
        /// before it acts: a target that authors a hold must not be interacted with on the press —
        /// AS3 arms <c>t_action</c> and returns (<c>UnitPlayer.as:1944-1988</c>), and the effect lands
        /// only from the timer's completion branch (<c>:1081-1086</c>).</para>
        /// </summary>
        private IInteractable ResolveInteractTarget()
        {
            return FindCursorTarget();
        }

        /// <summary>
        /// The interactable under the mouse cursor, if one is within reach. AS3:
        /// <c>loc.celObj &amp;&amp; loc.celObj.onCursor</c> (<c>UnitPlayer.as:1931</c>).
        /// </summary>
        private IInteractable FindCursorTarget()
        {
            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
                if (_mainCamera == null) _mainCamera = FindFirstObjectByType<Camera>();
            }
            if (_mainCamera == null) return null;

            Vector3 mouseScreen = Input.mousePosition;
            if (!_mainCamera.orthographic)
            {
                mouseScreen.z = -_mainCamera.transform.position.z;
            }
            Vector3 mouseWorld = _mainCamera.ScreenToWorldPoint(mouseScreen);
            mouseWorld.z = 0f;
            Vector2 playerPos = transform.position;

            Collider2D[] cursorHits = Physics2D.OverlapCircleAll(mouseWorld, CursorHitRadius);
            for (int i = 0; i < cursorHits.Length; i++)
            {
                Collider2D hit = cursorHits[i];
                if (hit == null || hit.gameObject == gameObject) continue;

                IInteractable cursorInteractable = hit.GetComponent<IInteractable>() ?? hit.GetComponentInParent<IInteractable>();
                if (cursorInteractable != null && cursorInteractable.CanInteract(gameObject))
                {
                    Vector2 closestPt = hit.ClosestPoint(playerPos);
                    float distSq = (closestPt - playerPos).sqrMagnitude;
                    if (distSq <= WorldConstants.ACTION_REACH * WorldConstants.ACTION_REACH)
                    {
                        return cursorInteractable;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Radius of the cursor probe, in world units. AS3 asks whether the <i>cell</i> under the
        /// cursor is the one holding the object (<c>loc.celObj.onCursor</c>), and a cell is
        /// <c>World.tileX</c> = 40 source pixels = 0.4 world units. A 0.45 probe covers that cell
        /// with a hair of slack for small props whose collider sits inside the tile.
        /// </summary>
        private const float CursorHitRadius = 0.45f;

        /// <summary>
        /// The action key went down.
        /// </summary>
        private void HandleInteract()
        {
            // Guarded per frame. Both the MessagePipe subscription and the direct poll in Update
            // deliver the same press, and an instant action must not run twice for one key press —
            // which for a door means open-then-close in a single frame.
            if (Time.frameCount == _lastInteractPressFrame) return;
            _lastInteractPressFrame = Time.frameCount;

            // AS3 UnitPlayer.as:2125-2129: if holding an object, Action key throws it.
            if (_telekinesis != null && _telekinesis.IsHoldingObject)
            {
                if (_telekinesis.TryThrow())
                {
                    return;
                }
            }

            // A hold in flight owns the action: AS3 will not start a second while actionObj is set —
            // actAction() only re-checks the incumbent (:1924-1930).
            if (_actionInteractor != null && _actionInteractor.IsHolding) return;

            BeginOrPerformInteract(ResolveInteractTarget());
        }

        /// <summary>
        /// Acts on <paramref name="target"/>: starts a hold if it authors a duration, otherwise
        /// interacts at once — AS3's zero-time branch (<c>UnitPlayer.as:1985-1988</c>).
        ///
        /// <para>Shared by the interact key and by <see cref="HandleAttackStart"/>'s
        /// "clicking an interactable interacts instead of attacking" path, so both obey the same rule.
        /// Splitting them is how a held Z door would open instantly on a left-click while needing a
        /// third of a second on E.</para>
        /// </summary>
        /// <returns>True when the target was consumed, so the caller must not also act.</returns>
        private bool BeginOrPerformInteract(IInteractable target)
        {
            if (target == null)
            {
                return false;
            }

            if (target is IHoldInteractable hold && hold.HoldFrames > 0)
            {
                if (_actionInteractor != null &&
                    _actionInteractor.TryBeginHold(hold, gameObject, hold.WorldPosition))
                {
                    return true;
                }
            }

            target.Interact(gameObject);
            return true;
        }

        /// <summary>
        /// The action key came up: abandon any hold in flight. Nothing fires.
        ///
        /// <para>This is AS3's release path — a false <c>keyAction</c> nulls <c>actionObj</c> at
        /// <c>UnitPlayer.as:2131-2135</c> — and it is why letting go of E half-way through a Z door
        /// does nothing at all rather than finishing.</para>
        /// </summary>
        private void EndInteract()
        {
            _actionInteractor?.ReleaseHold();
        }

        // Public getters

        public new UnitStats Stats => base._unitStats;
        public bool IsRunning => _locomotion != null ? _locomotion.CurrentSnapshot.IsRunning : _isRunning;
    }
}
