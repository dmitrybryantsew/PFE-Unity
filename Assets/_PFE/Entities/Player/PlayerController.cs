using UnityEngine;
using VContainer;
using MessagePipe;
using PFE.Character;
using PFE.Core;
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
    public class PlayerController : UnitController, IDamageContextSink
    {
        /// <summary>
        /// The player's step-up allowance — AS3 <c>UnitPlayer.as:2530-2569</c>, which is <b>not</b> the
        /// generic unit's 10/4.
        ///
        /// <para>The oracle sets <c>porog = 0</c> at rest and <c>porog = 20</c> inside the two walk
        /// branches (<c>:2550</c>, <c>:2569</c>), and <c>porog_jump = 10</c> while running or holding
        /// Up (<c>:2533</c>). The asymmetry is the point: a <i>stationary</i> player is stopped by any
        /// ledge, and a <i>walking</i> one climbs half a tile — 20 px, i.e. two <c>zForm</c> levels.</para>
        ///
        /// <para><b>Walking is read from <see cref="_velocity"/>, not from a key.</b> AS3's branch is
        /// "a walk key is held"; the only observable difference is a player who holds a key against a
        /// wall, whose velocity is zero — and in that state there is nothing to step onto anyway,
        /// because the step is evaluated while moving into the obstacle.</para>
        /// </summary>
        protected override float StepUpAllowancePixels =>
            _isGrounded
                ? (Mathf.Abs(_velocity.x) > 0.01f
                    ? TileQueryConstants.PlayerPorogWalk
                    : TileQueryConstants.PlayerPorogGrounded)
                : TileQueryConstants.PlayerPorogJump;

        [Header("Player Specifics")]
        [SerializeField]
        [Tooltip("Should the player always face the mouse cursor?")]
        [LocalOnly(Note = "Config toggle; never replicated")]
        private bool _mouseAiming = true;

        [SerializeField]
        [Tooltip("Maximum time between directional taps for dash/drop-through detection.")]
        [LocalOnly(Note = "Config tuning; never replicated")]
        private float _doubleTapWindowSeconds = 0.25f;
        [LocalOnly(Note = "Wiring reference; legacy fallback motor, never replicated")]
        private TilePhysicsController _tilePhysics;
        [LocalOnly(Note = "Wiring reference; primary motor, never replicated")]
        private PlayerLocomotionController _locomotion;

        /// <summary>
        /// Runs the hold-to-act gesture for world objects. Optional — a player prefab without one keeps
        /// the immediate-press behaviour for every target, since every use below is null-guarded.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; hold-to-act gesture runner, never replicated")]
        private PlayerActionInteractor _actionInteractor;

        // The interact press frame-dedup moved to PlayerCommandExecutor (thread F): it is acting-side
        // state and lives beside the code that acts on the press.

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
        [Authoritative(Note = "AS3 ggControl cutscene lock; host-owned Sim truth (F-02)")]
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
        [Authoritative(Note = "Modal-HUD input block (guiPause); host-owned Sim truth (F-02)")]
        public bool ModalInputBlocked { get; set; }

        /// <summary>
        /// Whether player input is currently honoured at all — the cutscene lock <b>and</b> the modal
        /// block. Every input gate reads this rather than <see cref="ScriptControlEnabled"/> directly,
        /// so a new blocker cannot be added to one gate and forgotten in another.
        /// </summary>
        public bool ControlActive => ScriptControlEnabled && !ModalInputBlocked;

        // Dependencies (injected via VContainer)
        [LocalOnly(Note = "Wiring reference; injected input service, never replicated")]
        private InputReader _input;

        /// <summary>
        /// The input service, for the systems that need to read the mode's own keys.
        ///
        /// <para><b>Not named <c>Input</c>.</b> A member called <c>Input</c> shadows
        /// <c>UnityEngine.Input</c> inside this class, and every <c>Input.GetKey</c> below silently
        /// becomes a member lookup on <see cref="InputReader"/>. That is a compile error here, but it
        /// would be a silent rebind in a file that only used <c>Input.mousePosition</c>.</para>
        /// </summary>
        public InputReader InputService => _input;

        /// <summary>
        /// The interface pause gate — the port of <c>World.allStat</c>. Held by S.A.T.S. and the
        /// PipBuck while either is open.
        ///
        /// <para><b>Why the player reads it.</b> The gate freezes the room by skipping non-view
        /// tickables, but the player's own input handling runs in <c>Update</c>, which no bucket
        /// governs. Without this check the player walks and aims with the PipBuck open — the defect
        /// <c>10_SATS_TACTICAL_MODE.md</c> §5 lists as a prerequisite, and with S.A.T.S. open the
        /// queue would fire during the frozen frame it exists to plan.</para>
        /// </summary>
        [LocalOnly(Note = "Wiring reference; the shared pause gate, never replicated")]
        private PFE.Core.InterfacePauseGate _pauseGate;

        /// <summary>S.A.T.S. — the queue's aim override, and the plan's invalidation triggers.</summary>
        [LocalOnly(Note = "Wiring reference; the interface mode, never replicated")]
        private PFE.Systems.Combat.Sats.SatsController _sats;

        /// <summary>True while an interface (PipBuck or S.A.T.S.) has the world frozen.</summary>
        private bool IsWorldPaused => _pauseGate != null && _pauseGate.IsWorldPaused;

        /// <summary>
        /// One of the §3.11 cancel triggers — every call site names which of the seventeen it is, so
        /// <c>grep SatsInvalidationReason</c> enumerates the ledger instead of a reader having to
        /// trust a comment. Null-safe: a scene without the S.A.T.S. bootstrapper is legitimate.
        /// </summary>
        private void InvalidateSats(PFE.Systems.Combat.Sats.SatsInvalidationReason reason)
            => _sats?.Invalidate(reason);

        /// <summary>The room the S.A.T.S. ledger last saw — <c>null</c> until the first observation.</summary>
        [LocalOnly(Note = "S.A.T.S. room-transition observation cache; local interface state, derived on each peer and never replicated")]
        private PFE.Systems.Map.RoomInstance _satsObservedRoom;

        /// <summary>
        /// §3.11 row 11 — the plan dies when the player leaves the location
        /// (<c>UnitPlayer.as:602-605</c>, inside <c>outLoc</c>).
        ///
        /// <para><b>Why a poll rather than a call in <c>outLoc</c>.</b> The port has no <c>outLoc</c>:
        /// the Oracle's one method is split across <c>DoorTrigger</c>,
        /// <c>RoomTransitionManager</c>, <c>TerminalActions</c> and <c>ExitAction</c>, and none of those
        /// components participates in the container, so none of them can be handed the controller.
        /// Watching <c>landMap.currentRoom</c> from the player — which <i>is</i> injected — covers every
        /// one of those paths plus any a script invents, and cannot be forgotten by a future exit
        /// handler.</para>
        ///
        /// <para>The first observation is not a transition: it establishes the baseline, or a plan
        /// queued before the land map finished loading would be cancelled by startup.</para>
        /// </summary>
        private void PollSatsLocationExit()
        {
            PFE.Systems.Map.RoomInstance room = CurrentRoom;
            if (ReferenceEquals(room, _satsObservedRoom)) return;

            bool hadBaseline = _satsObservedRoom != null;
            _satsObservedRoom = room;

            if (hadBaseline)
            {
                InvalidateSats(PFE.Systems.Combat.Sats.SatsInvalidationReason.LocationExit);
            }
        }

        /// <summary>
        /// The interface gate and S.A.T.S., as a <b>second</b> injection method.
        ///
        /// <para><b>Why not two more parameters on <see cref="Construct"/>.</b> <c>PlayerRigBuilder</c>
        /// calls <c>PlayerWeaponLoadout.Construct</c> positionally, and <c>Construct</c> is the seam a
        /// hand-built rig uses; inserting parameters in the middle of it silently re-binds every
        /// argument after the insertion point. A separate <c>[Inject]</c> method leaves that seam
        /// exactly as it was and still gets the container's instances.</para>
        /// </summary>
        [Inject]
        public void InjectInterfaces(PFE.Core.InterfacePauseGate pauseGate,
                                     PFE.Systems.Combat.Sats.SatsController sats)
        {
            _pauseGate = pauseGate;
            _sats = sats;
        }
        [LocalOnly(Note = "Wiring reference; injected debug settings asset, never replicated")]
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
        [LocalOnly(Note = "Wiring reference; weapon loadout, never replicated")]
        private PlayerWeaponLoadout _loadout;
        [LocalOnly(Note = "Wiring reference; telekinesis component, never replicated")]
        private PlayerTelekinesisController _telekinesis;

        /// <summary>
        /// Runs AS3's mana block (<c>UnitPlayer.as:1302-1361</c>) once per sim tick. Created here
        /// because the player is motor-driven, so <see cref="PFE.Entities.Units.UnitController.SimTick"/>
        /// returns early for it and there is no other per-tick hook. See
        /// <see cref="PlayerManaTicker"/> for why it is a separate component.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; sim-tick component, never replicated")]
        private PlayerManaTicker _manaTicker;

        /// <summary>
        /// Runs AS3's radiation block (<c>UnitPlayer.as:898-960</c>) once per sim tick and owns the
        /// player's exposure state. Created here for the same reason as <see cref="PlayerManaTicker"/>:
        /// the player is motor-driven, so <c>UnitController.SimTick</c> returns early for it and a
        /// separate <see cref="ISimTickable"/> is the only per-tick hook.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; sim-tick component, never replicated")]
        private PlayerRadiationTicker _radiationTicker;

        /// <summary>
        /// Runs AS3's regen block (<c>UnitPlayer.as:963-967</c>) once per sim tick — the regeneration
        /// implant's passive healing. Created and registered right after the radiation block, because
        /// AS3 has them adjacent inside one <c>actions()</c> and the radiation ceiling must be current
        /// before regen reads it.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; sim-tick component, never replicated")]
        private PlayerHealthRegenTicker _regenTicker;

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
        [LocalOnly(Note = "Wiring reference; sim-tick component, never replicated")]
        private PlayerAutoPickup _autoPickup;

        /// <summary>
        /// The player's spell caster — the port of AS3 <c>UnitPlayer</c>'s spell half. Created here for
        /// the same reason as <see cref="PlayerManaTicker"/>: the player is motor-driven, so
        /// <c>UnitController.SimTick</c> returns early for it and a separate <c>ISimTickable</c> is the
        /// only way to get a per-tick hook. See <see cref="PlayerSpellCaster"/>.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; spell caster, never replicated")]
        private PlayerSpellCaster _spellCaster;

        /// <summary>
        /// The companion manager — AS3 <c>gg.callPet</c>/<c>gg.currentPet</c>/<c>gg.noPet</c>
        /// (<c>UnitPlayer.as:3899-3970</c>). Created in <c>Awake</c> and registered on the sim loop by
        /// <see cref="AttachSimulation"/>, for the same reason as <see cref="PlayerManaTicker"/>: the
        /// player is motor-driven, so a separate <see cref="ISimTickable"/> is the only per-tick hook.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; companion manager, never replicated")]
        private PFE.Entities.Pets.PetCompanion _petCompanion;

        /// <summary>
        /// The player's inventory. Created in <c>Awake</c> like the caster, and wired from both
        /// <c>Construct</c> and <c>Awake</c> — see <see cref="WireInventoryIfReady"/>. Per-player by
        /// construction, which is what a second co-op player needs.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; inventory component, never replicated")]
        private PlayerInventory _inventoryComponent;

        /// <summary>
        /// The session's registries, held until attach — thread E. Injection is not ordered against
        /// Awake (the file's standing warning), so both sides call <see cref="AttachPendingRegistriesIfReady"/>.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; view registries, never replicated")]
        private UnitViewRegistry _pendingViewRegistry;

        [LocalOnly(Note = "Wiring reference; view registries, never replicated")]
        private EliteEnemyRegistry _pendingEliteRegistry;

        /// <summary>
        /// Joins the session's view registries from whichever of <c>Construct</c>/<c>Awake</c> ran
        /// second — the same two-sided wire as <see cref="WireInventoryIfReady"/>. Until this runs the
        /// player's OnEnable could not register anything (no instance yet), and the attach itself is
        /// retroactive, so either order ends with the player in the projector's unit list.
        /// </summary>
        private void AttachPendingRegistriesIfReady()
        {
            if (_pendingViewRegistry == null && _pendingEliteRegistry == null) return;
            AttachViewRegistries(_pendingViewRegistry, _pendingEliteRegistry);
        }

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
        [LocalOnly(Note = "Wiring reference; take-event scripts, never replicated")]
        private PFE.Systems.Map.Scripting.LandItemScriptRunner _itemTakeScripts;

        // The three spell input subscribers, held between Construct and Awake because Unity does not
        // order the two. Same reason and same shape as _pendingEffectResolver above.
        [LocalOnly(Note = "Wiring reference; DI-held pre-Awake, never replicated")]
        private ISubscriber<SpellCastMessage> _pendingSpellCastSubscriber;
        [LocalOnly(Note = "Wiring reference; DI-held pre-Awake, never replicated")]
        private ISubscriber<SpellHotkeyMessage> _pendingSpellHotkeySubscriber;
        [LocalOnly(Note = "Wiring reference; DI-held pre-Awake, never replicated")]
        private PFE.Systems.Map.LandMap _pendingLandMap;
        [LocalOnly(Note = "Wiring reference; DI-held pre-Awake, never replicated")]
        private PFE.Systems.Audio.ISoundService _pendingSoundService;
        [LocalOnly(Note = "Wiring reference; DI-held pre-Awake, never replicated")]
        private PFE.Data.ContentRegistry _pendingRegistry;

        /// <summary>
        /// The campaign singleton held between Construct and Awake — thread E. Feeds the inventory's
        /// first-take script lookup and the take-script runner; both tolerate null and fall back to
        /// <c>CampaignManager.Current</c>, the tracked static that serves containerless rigs.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; DI-held pre-Awake, never replicated")]
        private PFE.Systems.Campaign.CampaignManager _pendingCampaign;

        /// <summary>
        /// The script system the take-script runner registers with — thread E. Same held-between-
        /// Construct-and-Awake wiring as the campaign; the runner falls back to
        /// <c>ScriptSystem.Current</c> when this is null.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; DI-held pre-Awake, never replicated")]
        private PFE.Systems.Map.Scripting.ScriptSystem _pendingScriptSystem;

        // The MessagePipe subscription bag moved into LocalPlayerInputSampler with the subscriptions
        // it disposes (thread F).

        [LocalOnly(Note = "View reference; cached camera, never replicated")]
        private Camera _mainCamera;
        [Authoritative(Note = "Locomotion run state mirror; host-owned Sim truth (F-02)")]
        private bool _isRunning;

        /// <summary>AS3 <c>isrnd(0.1)</c> (<c>UnitPlayer.as:1509</c>) — the per-tick <c>autoAttack</c> flip chance.</summary>
        private const float AutoAttackFlipChance = 0.1f;

        /// <summary>AS3 <c>isrnd(0.05)</c> (<c>UnitPlayer.as:1170</c>) — the forced-aim scatter chance per tick.</summary>
        private const float ForcedAimScatterChance = 0.05f;

        /// <summary>
        /// The aim point the <c>vote</c> effect is holding, in world units. It survives between ticks
        /// on purpose: AS3's forced-aim arm only reassigns <c>celX</c>/<c>celY</c> on the 5 % roll, so
        /// the aim freezes wherever it last was.
        /// </summary>
        [Authoritative(Note = "vote-effect held aim point (AS3 celX/celY); host-owned Sim truth (F-02)")]
        private Vector3 _forcedAimTarget;

        /// <summary>Whether <see cref="_forcedAimTarget"/> has been seeded since the effect started.</summary>
        [Authoritative(Note = "Forced-aim seed flag; host-owned Sim truth (F-02)")]
        private bool _forcedAimValid;

        /// <summary>
        /// Whether the <c>vote</c> effect is currently holding the trigger for the player. Edge
        /// tracking only: the loadout is told on the transition, not every tick, because
        /// <c>BeginAttack</c>/<c>EndAttack</c> latch a held flag rather than firing once.
        /// </summary>
        [Authoritative(Note = "vote-effect trigger-hold latch; host-owned Sim truth (F-02)")]
        private bool _forcedAttackActive;
        [ClientPredicted(Note = "Sampled aim angle fed into PlayerCommand; predicted (F-03)")]
        private float _aimAngle;

        // F-03 / Phase 2.1: the local player's intent as a tick-stamped command stream now lives in
        // LocalPlayerInputSampler (thread F) — the tape, the sequence counter, the double-tap state
        // and every input subscription moved there. The tape is a Producer, not a read-back cache:
        // Update() samples input per render frame while SimLoop ticks at 30 Hz, and PlayerCommandTape
        // folds the frames of one tick into ONE entry; the local consumer executes the fresh frame
        // (see Update below), never a tape lookup.
        [LocalOnly(Note = "Wiring reference; input→intent half, never replicated")]
        private LocalPlayerInputSampler _inputSampler;

        [LocalOnly(Note = "Wiring reference; intent→subsystem half, never replicated")]
        private PlayerCommandExecutor _commandExecutor;

        /// <summary>
        /// Create the sampler/executor pair on first use. Both <c>Construct</c> and <c>Awake</c> call
        /// this because Unity does not order the container's injection against the component's own
        /// awake — and the executor's context needs <c>this</c>, which field initializers cannot
        /// reference (CS0027). Idempotent: the pair is created once and shared by both entry points.
        /// </summary>
        private void EnsureInputStack()
        {
            if (_commandExecutor == null)
            {
                _commandExecutor = new PlayerCommandExecutor(new PlayerCommandContext(this));
            }
            if (_inputSampler == null)
            {
                _inputSampler = new LocalPlayerInputSampler();
            }
        }

        /// <summary>The local player's recent command window (redundant last-N). Read-only to callers.</summary>
        public PFE.Core.Input.PlayerCommandBuffer CommandBuffer => _inputSampler.CommandBuffer;

        public PlayerTelekinesisController Telekinesis => _telekinesis;
        [LocalOnly(Note = "Wiring reference; RPG stats component, never replicated")]
        private CharacterStats _characterStats;
        public CharacterStats CharacterStats => _characterStats;

        /// <summary>
        /// The room resolver this controller installed on <c>WorldItemPickup.RoomProvider</c>, kept so
        /// <see cref="OnDestroy"/> can tell "still mine" from "already replaced" before clearing it.
        /// </summary>
        [LocalOnly(Note = "Wiring reference; installed room resolver delegate, never replicated")]
        private System.Func<PFE.Systems.Map.RoomInstance> _lootRoomProvider;

        /// <summary>
        /// The player's companions — AS3 <c>gg.callPet</c> and the three pet fields it keeps. Never
        /// null after <c>Awake</c>.
        /// </summary>
        public PFE.Entities.Pets.PetCompanion Pets => _petCompanion;

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
            ISubscriber<PunchMessage> punchSubscriber,
            ISubscriber<MagicWeaponMessage> magicWeaponSubscriber,
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
            PFE.Data.ContentRegistry registry = null,
            // Thread E: the view/elite registries the player joins like any spawned unit. Optional for
            // the same reason the registry is: a plain-Awake test rig has no container. Registered
            // unconditionally in GameLifetimeScope, so production always fills them.
            UnitViewRegistry viewRegistry = null,
            EliteEnemyRegistry eliteRegistry = null,
            // Thread E: the campaign singleton, handed to the inventory (first-take quest scripts)
            // and the take-script runner. Same optionality as the registries — a bare-Awake rig has
            // no container, and both consumers fall back to CampaignManager.Current.
            PFE.Systems.Campaign.CampaignManager campaign = null,
            // Thread E: the script system the take-script runner registers runners with. Optional
            // for the same reason the campaign is; the runner falls back to ScriptSystem.Current.
            PFE.Systems.Map.Scripting.ScriptSystem scriptSystem = null)
        {
            _input = input;
            _debugSettings = debugSettings;
            _pendingLandMap = landMap;
            _pendingSoundService = soundService;
            _pendingRegistry = registry;
            _pendingViewRegistry = viewRegistry;
            _pendingEliteRegistry = eliteRegistry;
            _pendingCampaign = campaign;
            _pendingScriptSystem = scriptSystem;
            AttachPendingRegistriesIfReady();

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

            if (_debugSettings.LogDependencyInjectionConstruct)
                Debug.Log("[PlayerController] Construct() called — dependencies injected.");

            // ── Input → intent: the sampler owns every input subscription ────────────────────────────
            //
            // The controller no longer subscribes to gameplay messages itself (thread F): the sampler
            // is the sole subscriber, folds the per-frame stream into tick-stamped PlayerCommands, and
            // forwards the discrete action edges to the executor straight away. The double-injection
            // re-subscription guard (the player GameObject is injected by both RegisterComponent and
            // the scene scope's autoInjectGameObjects) moved into LocalPlayerInputSampler.Construct
            // with the subscriptions it protects.
            EnsureInputStack();
            _inputSampler.Construct(
                input,
                attackSubscriber,
                teleportSubscriber,
                interactSubscriber,
                reloadSubscriber,
                spellCastSubscriber,
                spellHotkeySubscriber,
                grenadeSubscriber,
                punchSubscriber,
                magicWeaponSubscriber,
                debugSettings,
                _commandExecutor,
                _doubleTapWindowSeconds);
            _commandExecutor.BindDebugSettings(debugSettings);
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
        [LocalOnly(Note = "Wiring reference; DI-held pre-Awake, never replicated")]
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
        [LocalOnly(Note = "Wiring reference; DI-held pre-Awake, never replicated")]
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
        /// The player's own corpse-dropped loot plays through the injected service — see
        /// <see cref="UnitController.LootDropSoundService"/>. Thread E: replaces the retired
        /// <c>SoundBus.Current</c> reach.
        /// </summary>
        protected override ISoundService LootDropSoundService => _pendingSoundService;

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
            // The sound service rides the same two-sided wire so a dropped item's landing sample plays
            // through the injected service instead of the retired SoundBus static.
            _inventoryComponent.Construct(_pendingRegistry, _pendingSoundService, _pendingCampaign);

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
            // The replication seam for the avatar extras (aim/weapon/animation). Test rigs without
            // an assembler simply report "state unknown" forever; that is the safe default, not a
            // crash, because capture stamps only what the provider returns.
            SetAvatarViewSource(new AvatarViewSource(this, GetComponent<CharacterQuadAssembler>()));
            _actionInteractor = GetComponent<PlayerActionInteractor>();
            _telekinesis = GetComponent<PlayerTelekinesisController>() ?? gameObject.AddComponent<PlayerTelekinesisController>();
            // The executor's component half is bound here — every component it drives exists from this
            // point of Awake on. Its context half (this) was supplied at construction.
            EnsureInputStack();
            _commandExecutor.BindComponents(
                _locomotion,
                _telekinesis,
                _loadout,
                _tilePhysics,
                _actionInteractor);
            base._unitStats = new UnitStats();
            _characterStats = GetComponent<CharacterStats>() ?? gameObject.AddComponent<CharacterStats>();
            _characterStats.BindUnitStats(base._unitStats);

            // Where a code-spawned loot pickup finds the room it must fall through. A pickup is built with
            // `new GameObject` and has no injection point, so this is the same settable-static idiom
            // `LootRuntime` uses for the loot tables. The player is the one who opens containers and drops
            // items, and `CurrentRoom` follows the land map rather than caching, so this stays correct
            // across a room transition without being re-wired.
            _lootRoomProvider = () => CurrentRoom;
            PFE.Systems.Inventory.WorldItemPickup.RoomProvider = _lootRoomProvider;

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

            // AS3's regen block. Created here for the same reason and wired from both sides: it reads
            // `regenFew`/`regenMax` off CharacterStats, which is built above, and the hp off UnitStats.
            _regenTicker = GetComponent<PlayerHealthRegenTicker>() ?? gameObject.AddComponent<PlayerHealthRegenTicker>();
            _regenTicker.Construct(_characterStats);

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

            // The companions. Created here so `PlayerInventory`'s `@pet` branch and the PipBuck's pet row
            // both find it the moment they ask; put on the sim clock by AttachSimulation below.
            _petCompanion = GetComponent<PFE.Entities.Pets.PetCompanion>()
                            ?? gameObject.AddComponent<PFE.Entities.Pets.PetCompanion>();
            _petCompanion.Construct(this);

            // The land's `eve='take'` item scripts. Built after the inventory for the same reason
            // `_autoPickup` is: it subscribes to that inventory's OnItemAdded/OnWeaponAdded. The
            // LandMap is passed as a factory because the injection that fills `_pendingLandMap` is not
            // ordered against this Awake.
            _itemTakeScripts = GetComponent<PFE.Systems.Map.Scripting.LandItemScriptRunner>()
                ?? gameObject.AddComponent<PFE.Systems.Map.Scripting.LandItemScriptRunner>();
            _itemTakeScripts.Construct(_inventoryComponent, () => _pendingLandMap, campaign: _pendingCampaign,
                scriptSystem: _pendingScriptSystem);

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

            // …and the same for the view registries (thread E): the player is a unit like any other
            // for the snapshot projector's unit list, and Construct is not ordered against this Awake.
            AttachPendingRegistriesIfReady();
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

            // The swim impulse, first of the per-frame blocks because it is the only one that is a
            // pulse rather than a level: it is registered at SimTickOrder.Input so it lands before the
            // motor's step consumes it. Registered even when the motor itself stays on FixedUpdate —
            // the two drivers are independent flags, and the impulse is a frame count either way.
            if (_locomotion != null)
            {
                _locomotion.AttachSimulation(clock, loop);
            }

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

            // Registered immediately after the radiation block, at the same order: AS3 has the
            // exposure block, then `healhp`, then regen inside one `actions()`, and at equal order
            // SimLoop runs in registration order. Regen first would top the player up and let the
            // same tick's exposure claw it back — a jittery bar instead of a smooth one.
            if (_regenTicker != null)
            {
                _regenTicker.Attach(clock, loop);
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

            // Companions, at the same order and registered last: the pet's own brain ticks at
            // `SimTickOrder.UnitMotor - 1`, so the player has already been placed for this tick when the
            // pet reads the player's position — the same ordering the oracle gets from
            // `UnitPlayer.step` running before `UnitPet.step` in the room's unit list.
            if (_petCompanion != null)
            {
                _petCompanion.Attach(clock, loop);
            }
        }

        protected override void OnDestroy()
        {
            // Only clear the loot room resolver if it is still the one THIS controller installed — a
            // respawn that built a new player before this one was torn down must not have its wiring
            // dropped by the old object's teardown.
            if (ReferenceEquals(PFE.Systems.Inventory.WorldItemPickup.RoomProvider, _lootRoomProvider))
            {
                PFE.Systems.Inventory.WorldItemPickup.RoomProvider = null;
            }

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

            // Clean up MessagePipe subscriptions (owned by the sampler since thread F)
            _inputSampler.Dispose();

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
            UpdateForcedAttack();

            // The per-frame intent: the sampler reads the input devices, folds what they produced into
            // one command for this frame's tick, and pushes it onto the replay tape. The controller
            // never reads the tape back — the same fresh frame goes straight to the executor, which is
            // the wiring fact PlayerCommandConsumptionLintTests pins (the 2026-11-06 lost-jump defect
            // was a read-back between these two lines).
            LocalPlayerInputSampler.SampledFrame frame = _inputSampler.SampleFrame(
                controlActive: ControlActive,
                isWorldPaused: IsWorldPaused,
                zaput: _characterStats != null && _characterStats.zaput != 0,
                aimAngle: _aimAngle,
                weaponKey: CurrentWeaponKey(),
                motorDriven: _locomotion != null,
                commandTick: SimLoop != null ? SimLoop.TickIndex + 1 : 0,
                // The player's own slot from the F-09 context. Single-player has one local slot
                // (index 0); a multiplayer context would resolve this client's slot differently.
                // The seam is that the context is consulted, not that the answer changes today.
                commandSlot: 0,
                // The teleport level is read back off the locomotion so the command carries the
                // POST-telekinesis-steal key state, and the last cursor the camera forwarded.
                teleportHeld: _locomotion != null && _locomotion.IsTeleportHeld,
                teleportCursorPixels: _locomotion != null ? _locomotion.CursorWorldPixels : Vector2.zero);

            _commandExecutor.Execute(in frame, frame.HasCommand ? ResolveCursorWorldPixels() : null);

            if (frame.ArmTakeWindow)
            {
                _autoPickup?.ArmTakeWindow();
            }

            PollSatsLocationExit();

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

            // Direct interact check (E key or Right-Click), the poll that backs the Interact message
            // stream. Both edges are read, because acting is a hold: AS3 keeps keyAction as a held
            // boolean (set at Ctr.as:699, cleared at :797) and UnitPlayer.as:2115-2135 acts on the
            // false edge by abandoning the action. The press is gated on the cutscene lock — `control
            // off` must also stop the player opening a door mid-script — while the release is
            // deliberately NOT gated, so a lock that lands mid-hold still delivers the abandon and
            // the interactor cannot stay stuck in its "acting" state. The executor dedups the two
            // streams by frame, so the redundancy cannot double-fire.
            _inputSampler.PollLegacyInteractEdges(ControlActive);
        }

        /// <summary>
        /// The cursor in world pixels for this frame, or null when no camera has been resolved yet.
        /// ONLY a real cursor is ever handed over: `SetCursorWorldPixels` disables the telekinesis
        /// controller's own camera fallback, so forwarding a zero placeholder would pin the cursor to
        /// the room origin and make every grab miss — while looking exactly like a range problem.
        /// </summary>
        private Vector2? ResolveCursorWorldPixels()
        {
            if (_mainCamera == null)
            {
                // `_mainCamera` is cached from Camera.main at Awake, so a camera created later leaves
                // it null forever. Retry here; `Camera.main` is cheap and cached by Unity, unlike a
                // scene scan.
                _mainCamera = Camera.main;
            }
            if (_mainCamera == null)
            {
                return null;
            }

            // `Input.mousePosition.z` is always 0, and `ScreenToWorldPoint` reads that z as the
            // DISTANCE IN FRONT OF THE CAMERA. On a perspective camera a z of 0 is the near plane, so
            // the "world point" comes back sitting on the camera — and the camera follows the player,
            // so the cursor silently tracks the player instead of the mouse. Pass the distance to the
            // plane the rooms live on: z = 0. For an orthographic camera this assignment is a no-op.
            Vector3 mouseScreen = Input.mousePosition;
            if (!_mainCamera.orthographic)
            {
                mouseScreen.z = -_mainCamera.transform.position.z;
            }

            Vector3 mouseWorld = _mainCamera.ScreenToWorldPoint(mouseScreen);
            // Convert Unity world units to pixel space
            return new Vector2(
                mouseWorld.x * TileQueryConstants.UnitToPixel,
                mouseWorld.y * TileQueryConstants.UnitToPixel);
        }

        /// <summary>
        /// No-motor fallback: the original Rigidbody2D movement for a hand-built rig that has neither
        /// a <see cref="PlayerLocomotionController"/> nor a <see cref="TilePhysicsController"/>. Kept
        /// on the controller because it writes <c>UnitController</c>-internal state directly.
        /// </summary>
        private void ApplyFallbackMotor(Vector2 input)
        {
            if (input == Vector2.zero)
            {
                _isRunning = false;
                return;
            }

            float targetSpeed = _isRunning ? base._stats.RunSpeed : base._stats.WalkSpeed;
            float acceleration = base._stats.Acceleration * Time.deltaTime * 50f;
            float targetVelocityX = input.x * targetSpeed;
            SetVelocityX(Mathf.MoveTowards(_velocity.x, targetVelocityX, acceleration));
        }

        // HandleMovementInput, DetectHorizontalDoubleTap and DetectDownDoubleTap moved to
        // LocalPlayerInputSampler (thread F): the input read, the gate/zaput shaping, the edge
        // detection, the command build and the tape push are the sampler's "input → intent" half;
        // the motor dispatch half lives in PlayerCommandExecutor.Execute, called from Update.

        /// <summary>
        /// Handle weapon aiming towards mouse cursor.
        /// Original PFE had weapons rotate around the player character.
        /// </summary>
        private void HandleAiming()
        {
            if (!_mouseAiming || _mainCamera == null) return;

            // AS3 `:1164-1176` — the aim block is a THREE-way, and S.A.T.S. is the first arm:
            //   if (sats.que.length > 0)  { celX = que[0].X; celY = que[0].Y }
            //   else if (attackForever)   { if(isrnd(0.05)) celX/celY = a random point in the room }
            //   else                      { celX = World.w.celX; celY = World.w.celY }
            //
            // The queued cell wins over everything, including the `vote` effect: a plan the player
            // has already paid for is not scattered by a panic. It also wins over the pause gate,
            // because a live queue outlives the mode — that is what "accept" means — and the weapon
            // keeps its aim on the queued target until the plan is spent.
            if (_sats != null && _sats.QueueAimWorldUnits.HasValue)
            {
                ApplyAimTarget(_sats.QueueAimWorldUnits.Value);
                return;
            }

            // The world is frozen while an interface is open; a frozen player does not re-aim.
            if (IsWorldPaused) return;

            // AS3 `:1164-1176` — the third read site of `attackForever`, and the one that makes `vote`
            // read as panic rather than as a stuck trigger. The oracle's aim block is a three-way:
            //   if (sats.que.length > 0)  … VATS queue
            //   else if (attackForever)   { if(isrnd(0.05)) celX/celY = a random point in the room }
            //   else                      { celX = World.w.celX; celY = World.w.celY }
            // So while the effect runs the aim point STOPS tracking the cursor: it holds its last value
            // and teleports somewhere random on 5 % of ticks. The `else` arm is not reached at all —
            // this is a freeze plus scatter, not a wobble around the cursor.
            if (_characterStats != null && _characterStats.attackForever > 0)
            {
                if (!_forcedAimValid || UnityEngine.Random.value < ForcedAimScatterChance)
                {
                    _forcedAimTarget = RandomAimPointInCurrentRoom();
                    _forcedAimValid = true;
                }

                ApplyAimTarget(_forcedAimTarget);
                return;
            }

            _forcedAimValid = false;

            Vector3 mouseScreen = Input.mousePosition;
            // Same near-plane trap as the cursor forward in Update above: `Input.mousePosition.z` is 0, which on
            // a perspective camera means "on the camera", not "on the z = 0 plane the rooms live on".
            // Aim then collapses toward the player and the facing is wrong. Pass the plane distance.
            if (!_mainCamera.orthographic)
            {
                mouseScreen.z = -_mainCamera.transform.position.z;
            }

            Vector3 mouseWorld  = _mainCamera.ScreenToWorldPoint(mouseScreen);
            mouseWorld.z = 0f;

            ApplyAimTarget(mouseWorld);
        }

        /// <summary>
        /// Turn a world-space aim point into the facing angle and hand it to the loadout. Split out of
        /// <see cref="HandleAiming"/> so the forced-aim path and the cursor path cannot drift apart.
        /// </summary>
        private void ApplyAimTarget(Vector3 targetWorld)
        {
            Vector2 aimDirection = (Vector2)(targetWorld - transform.position);
            _aimAngle = Mathf.Atan2(aimDirection.y, aimDirection.x) * Mathf.Rad2Deg;

            // Push the aim target into the loadout; the controller and the presenter read it from there.
            _loadout?.SetAimTarget(targetWorld);
        }

        /// <summary>
        /// The equipped weapon as the wire's hash vocabulary, or 0 when unarmed. The loadout is the
        /// authority on what is in hand; nothing equipped (or a loadout-less test rig) hashes to 0,
        /// which both the host stamp and the client's weapon view read as "no weapon".
        /// </summary>
        private ulong CurrentWeaponIdHash()
        {
            string weaponId = _loadout != null ? _loadout.Current?.State.Def?.weaponId : null;
            return string.IsNullOrEmpty(weaponId)
                ? 0UL
                : PFE.Sim.State.SimSnapshotProjectionRules.LootItemKey(weaponId, isWeapon: true);
        }

        /// <summary>
        /// The equipped weapon as the interned key the command uplink carries (protocol v10), or
        /// <c>Uninterned</c> when unarmed or not in this build's weapon table. The host converts the
        /// key back to the content hash at the body boundary; the local capture path keeps sending
        /// the hash itself (<see cref="CurrentWeaponIdHash"/>), so a key the table cannot resolve
        /// degrades to "unarmed" only on the uplink, never on the local avatar.
        /// </summary>
        private ushort CurrentWeaponKey()
        {
            string weaponId = _loadout != null ? _loadout.Current?.State.Def?.weaponId : null;
            return string.IsNullOrEmpty(weaponId)
                ? PFE.Sim.Content.ContentInternTable.Uninterned
                : PFE.Sim.Content.SimWeaponKeyRules.ResolveWeaponKey(
                    ContentInternContext.Weapons, weaponId);
        }

        /// <summary>
        /// The <see cref="IUnitAvatarViewSource"/> the local rig installs on itself, so the shared
        /// capture path replicates aim, weapon and animation without <c>UnitController</c> knowing
        /// where any of them live. Reads its four values live each capture: aim from the controller,
        /// weapon from the loadout, animation state/frame straight off the assembler the
        /// <c>CharacterAnimationDriver</c> is already driving — there is no second write path to
        /// drift from the local view.
        /// </summary>
        private sealed class AvatarViewSource : IUnitAvatarViewSource
        {
            [LocalOnly(Note = "Wiring back-reference; the view source reads the owner's live state, it carries none of its own")]
            private readonly PlayerController _owner;
            [LocalOnly(Note = "View-layer assembler reference, fixed at construction; the animation driver owns the state it reads")]
            private readonly CharacterQuadAssembler _assembler;

            public AvatarViewSource(PlayerController owner, CharacterQuadAssembler assembler)
            {
                _owner = owner;
                _assembler = assembler;
            }

            public float AimAngle => _owner._aimAngle;

            public ulong WeaponIdHash => _owner.CurrentWeaponIdHash();

            public byte AnimStateId =>
                _assembler != null
                    ? PFE.Character.Animation.RemoteAnimStateIds.ToId(_assembler.CurrentState)
                    : PFE.Character.Animation.RemoteAnimStateIds.Unknown;

            public ushort AnimFrame =>
                _assembler != null ? (ushort)Mathf.Clamp(_assembler.CurrentFrame, 0, ushort.MaxValue) : (ushort)0;
        }

        /// <summary>
        /// The <see cref="PlayerCommandExecutor.IPlayerContext"/> the local rig installs on its
        /// executor — the rig-specific services the executor needs that are not components: the
        /// authority gates (cutscene lock is consulted at the call sites in <c>Update</c>, but
        /// <c>attackForever</c> and the S.A.T.S. state gate the discrete actions), S.A.T.S.
        /// invalidation, interact targeting, and the motor-state mirrors that land on
        /// <c>UnitController</c> fields. A host-side body for a remote peer implements the same
        /// interface against Sim state; that is the seam that keeps one execution code path for
        /// local and remote players (thread F).
        /// </summary>
        private sealed class PlayerCommandContext : PlayerCommandExecutor.IPlayerContext
        {
            [LocalOnly(Note = "Wiring back-reference; reads the owner's live state, carries none of its own")]
            private readonly PlayerController _owner;

            public PlayerCommandContext(PlayerController owner)
            {
                _owner = owner;
            }

            public GameObject Self => _owner.gameObject;

            public float AttackForever =>
                _owner._characterStats != null ? _owner._characterStats.attackForever : 0f;

            public bool SatsHasQueuedShots => _owner._sats != null && _owner._sats.HasQueuedShots;

            public bool SatsIsActive => _owner._sats != null && _owner._sats.IsActive;

            public void InvalidateSats(PFE.Systems.Combat.Sats.SatsInvalidationReason reason)
                => _owner.InvalidateSats(reason);

            public PFE.Systems.Interaction.IInteractable FindCursorTarget() => _owner.FindCursorTarget();

            public bool BeginOrPerformInteract(PFE.Systems.Interaction.IInteractable target)
                => _owner.BeginOrPerformInteract(target);

            public void MirrorLocomotionState(bool isGrounded, bool isRunning)
            {
                _owner._isGrounded = isGrounded;
                _owner._isRunning = isRunning;
            }

            public void MirrorLegacyGrounded(bool isGrounded) => _owner._isGrounded = isGrounded;

            public void ApplyFallbackMotor(Vector2 processedMoveInput)
                => _owner.ApplyFallbackMotor(processedMoveInput);
        }

        /// <summary>
        /// AS3 <c>celX = Math.random() * loc.limX</c> / <c>celY = Math.random() * loc.limY</c>
        /// (<c>:1172-1173</c>) — a uniformly random point across the room's pixel extent.
        /// </summary>
        /// <remarks>
        /// <b>Built from the documented origin helpers, not from <c>tile * TILE_SIZE</c>.</b>
        /// <c>WorldCoordinates.RoomOriginPixelY</c> returns a room's origin as its <i>bottom edge in
        /// world pixels</i>, and the room extends upward from there, so the rect needs no Y mirror —
        /// unlike the AS3→room-local conversions, which do. The inset is <c>borderOffset</c> tiles on
        /// both axes, the same subtraction <c>UnitNPCController.As3PixelsToRoomLocal</c> makes.
        /// </remarks>
        private Vector3 RandomAimPointInCurrentRoom()
        {
            PFE.Systems.Map.RoomInstance room = CurrentRoom;
            if (room == null)
            {
                // No room resolved: hold the player's own position rather than inventing a target.
                return transform.position;
            }

            float tile = WorldConstants.TILE_SIZE;
            int borderOffset = Mathf.Max(0, room.borderOffset);
            float widthPixels = Mathf.Max(1, room.width - borderOffset) * tile;
            float heightPixels = Mathf.Max(1, room.height - borderOffset) * tile;

            float originPixelX = WorldCoordinates.RoomOriginPixelX(room.landPosition.x, borderOffset);
            float originPixelY = WorldCoordinates.RoomOriginPixelY(room.landPosition.y, borderOffset);

            float pixelX = originPixelX + UnityEngine.Random.Range(0f, widthPixels);
            float pixelY = originPixelY + UnityEngine.Random.Range(0f, heightPixels);

            return new Vector3(
                pixelX / TileQueryConstants.UnitToPixel,
                pixelY / TileQueryConstants.UnitToPixel,
                0f);
        }

        /// <summary>
        /// AS3 <c>UnitPlayer.damage():3253-3268</c> — a psionic hit, applied before any HP logic.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this seam and not <c>OnHitEffectProducers</c>.</b> Every other <c>dopEffect</c>
        /// arm is a generic producer gated on the target's susceptibility, because AS3's
        /// <c>Unit.damage()</c> is. <c>psy</c> is not one of those: it lives in the <i>player's</i>
        /// override, above the <c>loc.train || loc.base</c> early return, with no <c>vulner</c> test and
        /// no <c>dopCh</c> re-roll. Routing it through the generic path would add a resistance gate and
        /// an extra RNG draw the oracle does not have — and would apply it to NPCs, which the oracle
        /// never does.</para>
        ///
        /// <para><b>It never refuses the hit.</b> AS3 runs this block and then continues into the normal
        /// damage body, so <c>false</c> is "not refused". Contrast the alicorn's own <c>damage()</c>
        /// override, whose <c>tr2</c> arm really does <c>return 0</c>.</para>
        /// </remarks>
        bool IDamageContextSink.OnDamageContext(in DamageContext context)
        {
            if (PsyEffectRules.IsPsyHit(context.DopEffect))
            {
                // The draw is passed, not pre-computed: AS3's second `isrnd()` is conditional, and
                // drawing both up front would shift the RNG stream for every later hit in the tick.
                string effectId = PsyEffectRules.RollEffect(() => UnityEngine.Random.value);
                _unitStats?.Effects?.AddEffect(effectId, 0f);
            }

            return false;
        }

        /// <summary>
        /// AS3 <c>UnitPlayer.as:1507-1516</c> — the <c>vote</c> effect's forced attack.
        ///
        /// <para><b>What the oracle does.</b> <c>attackForever</c> does not hold the fire button; it
        /// drives <c>autoAttack</c>, and <c>autoAttack</c> is what the fire gate reads:
        /// <c>if((ctr.keyAttack || autoAttack) &amp;&amp; …)</c> (<c>:2318</c>). While
        /// <c>attackForever &gt; 0</c> the oracle flips <c>autoAttack</c> at 10 % per tick — so the
        /// player stutters between firing and not firing rather than holding the trigger — and the
        /// instant <c>attackForever</c> clears it forces <c>autoAttack = 0</c>.</para>
        ///
        /// <para><b>Why the loadout is driven directly instead of through
        /// <see cref="PlayerCommandExecutor.AttackEdge"/>.</b> That path first tries to interact with whatever is
        /// under the cursor. AS3's <c>autoAttack</c> substitutes for <c>keyAttack</c> at the <i>weapon</i>
        /// gate, which sits below the interact decision — a panicking player who happens to be pointing
        /// at a door should keep firing, not open it.</para>
        /// </summary>
        private void UpdateForcedAttack()
        {
            if (_characterStats == null)
            {
                return;
            }

            // A cutscene or a modal panel owns the input; do not let a status effect fire through it.
            // The effect keeps ticking, it simply cannot act until control returns.
            if (!ControlActive)
            {
                return;
            }

            if (_characterStats.attackForever > 0)
            {
                // `if(isrnd(0.1)) this.autoAttack = 1 - this.autoAttack;` (`:1509-1512`).
                if (UnityEngine.Random.value < AutoAttackFlipChance)
                {
                    _characterStats.autoAttack = 1 - _characterStats.autoAttack;
                }
            }
            else
            {
                // `else { this.autoAttack = 0; }` (`:1514-1517`).
                _characterStats.autoAttack = 0;
            }

            bool forced = _characterStats.autoAttack != 0;
            if (forced == _forcedAttackActive)
            {
                return;
            }

            _forcedAttackActive = forced;

            if (forced)
            {
                _loadout?.BeginAttack();
            }
            else if (_input == null || !_input.IsAttackHeld)
            {
                // Release only when the player is not holding the button themselves: AS3's gate is
                // `keyAttack || autoAttack`, so the forced term clearing must not cancel a real hold.
                _loadout?.EndAttack();
            }
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
            HostWriteGuard.Check(SimAuthority.Authoritative);
            // Call base implementation (handles UnitStats damage and death check)
            base.TakeDamage(damage);

            if (_characterStats != null)
            {
                _characterStats.ApplyOrganDamage(damage);
            }

            TryBarkPlayerDamaged(damage);

            // Player-specific damage feedback
            // TODO: Trigger damage feedback (screen shake, flash, etc.)
        }

        public override bool ApplyDamage(in DamageOutcome outcome)
        {
            HostWriteGuard.Check(SimAuthority.Authoritative);
            bool broke = base.ApplyDamage(outcome);
            if (_characterStats != null && outcome.HpDamage > 0f)
            {
                _characterStats.ApplyOrganDamage(outcome.HpDamage);
            }

            TryBarkPlayerDamaged(outcome.HpDamage);
            return broke;
        }

        // ── Speech ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The player's voice set — <c>UnitPlayer.as:350</c> <c>id_replic = "pip"</c>, set in the
        /// constructor because the player has no <c>&lt;unit&gt;</c> row in <c>AllData</c> at all.
        /// </summary>
        public override string ReplicaId => "pip";

        /// <summary>The last line the player said — <c>UnitPlayer.prev_replic</c> (<c>:307</c>).</summary>
        [Authoritative(Note = "AS3 prev_replic no-repeat speech state; host-owned Sim truth (F-02)")]
        private string _prevReplic;

        /// <summary>
        /// <c>UnitPlayer.replic</c> (<c>UnitPlayer.as:4967-4995</c>) — the player's own speech path,
        /// which differs from the generic unit's in four ways and every one of them is visible.
        /// </summary>
        /// <remarks>
        /// <para><b>1. No <c>dam</c>/<c>die</c> window rolls.</b> The generic <c>replic</c> zeroes the
        /// cooldown on a 5 %/50 % roll; the player's version has no such arm. Its damage gate lives at
        /// the <i>call site</i> instead and is proportional to the hit —
        /// <c>if(_loc6_ / maxhp &gt; 0.1 &amp;&amp; isrnd(_loc6_ / maxhp))</c>
        /// (<c>UnitPlayer.as:3327-3330</c>) — so a scratch never makes her flinch and a rocket always
        /// does. See <see cref="TryBarkPlayerDamaged"/>.</para>
        ///
        /// <para><b>2. A shorter cooldown.</b> <c>75 + random*30</c> instead of the generic
        /// 110 + random*150. The player comments on what happens to her far more often than a raider
        /// does, and this is the number that decides it.</para>
        ///
        /// <para><b>3. No repeats.</b> If the drawn line equals the previous one the oracle draws
        /// <i>again</i>, and only gives up if the second draw also matches. That is why the player never
        /// says "Ouch!" twice in a row even though the table has nine <c>dam</c> lines.</para>
        ///
        /// <para><b>4. Female forms, always.</b> The call passes <c>msex = false</c> literally, rather
        /// than reading the unit's <c>sex</c> attribute — Littlepip is the protagonist, and the
        /// <c>#male|female#</c> splice must take the female half whatever the (nonexistent) row says.</para>
        ///
        /// <para><b>And the bubble is <c>replic2</c>.</b> Same text, 90 px above the feet instead of
        /// 110, with a ±10 px jitter instead of ±25 (<c>:4990-4993</c>).</para>
        /// </remarks>
        public override bool TryReplic(PFE.Sim.Units.ReplicKind kind)
        {
            HostWriteGuard.Check(SimAuthority.Authoritative);
            if (!IsAlive || !isActiveAndEnabled) return false;
            if (kind == PFE.Sim.Units.ReplicKind.None) return false;
            if (_replicCooldown > 0) return false;

            _replicCooldown = 75 + (int)(UnityEngine.Random.value * 30f);

            bool matureFilter = PFE.UI.PipBuck.PipTextTable.MatureContentFilter;
            string playerName = PFE.Systems.Units.ReplicCatalog.PlayerName;

            if (!PFE.Systems.Units.ReplicCatalog.Shared.TryResolve(
                    "pip", kind, UnityEngine.Random.value, male: false, matureFilter, playerName, out string text))
            {
                return false;
            }

            // One retry on a repeat, then give up — `UnitPlayer.as:4982-4989`.
            if (text == _prevReplic)
            {
                if (!PFE.Systems.Units.ReplicCatalog.Shared.TryResolve(
                        "pip", kind, UnityEngine.Random.value, male: false, matureFilter, playerName,
                        out string retry))
                {
                    return false;
                }

                if (retry == _prevReplic) return false;
                text = retry;
            }

            _prevReplic = text;
            PFE.Systems.Units.UnitBarkFeed.Emit(transform.position, text, player: true, UnityEngine.Random.value);
            return true;
        }

        /// <summary>
        /// The player's damage bark — <c>if(_loc6_ / maxhp &gt; 0.1 &amp;&amp; isrnd(_loc6_ / maxhp))
        /// this.replic("dam");</c> (<c>UnitPlayer.as:3327-3330</c>).
        /// </summary>
        /// <param name="dealt">The HP damage the hit actually took, after armour and resistance.</param>
        private void TryBarkPlayerDamaged(float dealt)
        {
            float maxHp = MaxHealth;
            if (maxHp <= 0f || dealt <= 0f) return;

            float fraction = dealt / maxHp;
            if (fraction <= 0.1f) return;
            if (!PFE.Sim.Units.ReplicRules.IsRandom(UnityEngine.Random.value, fraction)) return;

            TryReplic(PFE.Sim.Units.ReplicKind.Dam);
        }

        // Organ trauma can fire more than once (each of head/torso/legs/blood is checked), and a
        // dead player must not run the death path repeatedly.
        [Authoritative(Note = "Once-only organ-death latch; host-owned Sim truth (F-02)")]
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
            HostWriteGuard.Check(SimAuthority.Authoritative);
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
            HostWriteGuard.Check(SimAuthority.Authoritative);
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
            HostWriteGuard.Check(SimAuthority.Authoritative);
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
        [Authoritative(Note = "AS3 celObj per-frame target; host-owned Sim truth (F-02)")]
        public IInteractable CurrentInteractTarget { get; private set; }
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
        /// Acts on <paramref name="target"/>: starts a hold if it authors a duration, otherwise
        /// interacts at once — AS3's zero-time branch (<c>UnitPlayer.as:1985-1988</c>).
        ///
        /// <para>Shared by the interact key and by <see cref="PlayerCommandExecutor"/>'s
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

        // Public getters

        public new UnitStats Stats => base._unitStats;
        public bool IsRunning => _locomotion != null ? _locomotion.CurrentSnapshot.IsRunning : _isRunning;

        /// <summary>
        /// Whether the unit is crouched/crawling — AS3 <c>isSit</c>. Read by the fog light: AS3
        /// halves the unit's box height while sitting (<c>Unit.sit():2520</c>,
        /// <c>scY = sitY = stayY / 2</c>), and the light hangs off the box's top, so a crawling
        /// unit's light drops into the crawl space with it. See
        /// <see cref="FogLightCurrentHeightPixels"/>.
        /// </summary>
        public bool IsCrouching => _locomotion != null && _locomotion.IsCrouching;

        /// <summary>
        /// The unit's <i>current</i> box height for the fog pass, in pixels — AS3 <c>scY</c>:
        /// the standing sprite height normally, <c>sitY</c> while crouched
        /// (<c>Unit.as:2519-2525</c>). <c>Location.lighting():3140</c> puts the light at the box's
        /// top plus a standing-height term (<c>Y - scY + stayY * 0.247</c>), so feeding it the
        /// standing height while crawling parks the light ~35 px too high — inside the solid tile
        /// above a one-tile passage, where it reveals the tiles overhead that the oracle leaves
        /// dark (the "crawling under one tile reveals 2 tiles above" report).
        /// </summary>
        public float FogLightCurrentHeightPixels
        {
            get
            {
                float standing = SpriteSizePixels.y;
                if (!IsCrouching)
                {
                    return standing;
                }

                // AS3 `sitY` defaults to `stayY / 2` (Unit.as:1037-1044); the definition's
                // `sitHeight` is that value in Unity units (0.35 for Littlepip = 35 px). Fall back
                // to the half when no definition is wired, so the light still drops.
                float sitPixels = _stats != null && _stats.sitHeight > 0f
                    ? _stats.sitHeight * 100f
                    : standing * 0.5f;
                return Mathf.Min(sitPixels, standing);
            }
        }
    }
}
