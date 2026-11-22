using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Transporting;
using PFE.Character;
using PFE.Character.Animation;
using PFE.Core;
using PFE.Core.Input;
using PFE.Data.Definitions.Campaign;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Sim.Replication;
using PFE.Sim.State;
using PFE.Systems.Campaign;
using PFE.Systems.Map;
using PFE.Systems.Map.Streaming;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Replication;
using UnityEngine;
using VContainer;
// Unity 6000 declares its own UnityEngine.EntityId; the Sim one is the identity on the wire.
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Net
{
    /// <summary>
    /// Runtime session bootstrap: the first executable FishNet wiring in the project.
    ///
    /// <para><b>Why runtime-created.</b> Scene, prefab and asset edits are owner-only. This class is
    /// created by <c>[RuntimeInitializeOnLoadMethod]</c> on a bare GameObject, following the existing
    /// <c>PfeScenarioTest</c> command-line convention. No scene has a NetworkManager, no prefab has a
    /// NetworkObject, and no asmdef was changed: the file sits in a folder with no asmdef, so Unity
    /// compiles it into <c>Assembly-CSharp</c>, which already references FishNet.Runtime, PFE.Sim,
    /// PFE.View, PFE.Data and VContainer.</para>
    ///
    /// <para><b>Why an empty SpawnablePrefabs.</b> FishNet's <c>ValidateSpawnablePrefabs</c> refuses to
    /// initialise in play mode when the field is null (its automatic asset lookup is editor-only and
    /// explicitly gated on <c>!ApplicationState.IsPlaying()</c>). Assigning a runtime
    /// <c>SinglePrefabObjects</c> — not <c>DefaultPrefabObjects</c> — satisfies the check without
    /// writing an asset, and avoids FishNet's editor branch that regenerates the prefab collection.
    /// This adapter never spawns a NetworkObject: every message is a broadcast of opaque bytes.</para>
    ///
    /// <para><b>Why <c>TickOrder == ViewAligned</c> exactly.</b> The projector also reports
    /// <c>ViewAligned</c> and is registered first, so this session runs last and sees the snapshot the
    /// projector just built. A value above 100 would be skipped whenever the world is paused by the
    /// SimLoop pause gate, silently freezing the network during a UI pause.</para>
    /// </summary>
    public sealed class PfeReplicationSession : MonoBehaviour, ISimTickable, ISimRoomTransitionSink
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            PfeNetConfig cli;
            string cliReason;
            bool hasCli = PfeNetLaunchResolver.TryResolveCommandLine(
                Environment.GetCommandLineArgs(), out cli, out cliReason);

            // Bootstrap runs ONCE, on the MainMenuScene, which has no GameLifetimeScope. The session
            // is DontDestroyOnLoad, so it survives the scene change and EnsureInitialized() finds
            // the container once SampleScene is up. A menu-driven launch therefore needs a live
            // watcher from process start: RuntimeInitializeOnLoadMethod will NOT fire again when the
            // menu loads the game scene, and a bridge value written after this method returned would
            // otherwise never be read by anything.
            var go = new GameObject("[PfeReplicationSession]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            PfeReplicationSession session = go.AddComponent<PfeReplicationSession>();

            if (hasCli)
            {
                // An explicit switch is an operator override. Discard any menu request so it cannot
                // open a second session later in this process.
                PfeNetLaunchData.ClearPending();
                session._launchSource = "command line";
                session.Configure(cli);
                return;
            }

            if (cliReason != PfeNetLaunchResolver.NoSwitchReason)
            {
                Debug.LogError("[PfeNet] " + cliReason + "; command-line network session disabled.");
            }

            session._awaitMenu = true;
        }

        private PfeNetConfig _config;
        private NetworkManager _manager;
        private PfeReplicationRouter _router;
        private PfeFishNetWire _wire;
        private PfeRemoteCommandSink _sink;
        private PfeReplicationWorldApplier _applier;
        private PfeBaselineViewAssembler _baselineView;
        private PfeInteractSink _interactSink;
        private PfeRemoteProjectileDisplay _projectiles;
        private SimReplicationHost _host;
        private SimReplicationClient _client;
        private SimLoop _loop;
        private MapBridge _mapBridge;
        private PFE.Core.Rng.IWorldSeed _worldSeed;

        private readonly List<PfeRemoteBody> _bodies = new List<PfeRemoteBody>(4);
        private readonly Dictionary<byte, PfeRemoteBody> _bodyBySlot = new Dictionary<byte, PfeRemoteBody>();

        // Each peer's own room declaration (SimReplicationMessageKind.PeerRoomTransition), kept as
        // the room ID string and re-resolved against the live map at every use: a land rebuild
        // replaces every RoomInstance, and a cached instance would pin streaming to a dead room.
        // The declared world-pixel position seeds a body that is built before any transition
        // arrived — otherwise a peer spawns at the HOST player's position in the HOST's room.
        private readonly Dictionary<byte, string> _peerRoomIds = new Dictionary<byte, string>();
        private readonly Dictionary<byte, Vector2> _peerRoomWorldPx = new Dictionary<byte, Vector2>();
        private string _lastTransitionError = "none";
        private int _roomTransitionsApplied;
        private int _roomRelocations;
        private int _roomTransitionUnknownRooms;
        private int _roomTransitionRefused;
        private int _roomTransitionsSent;
        private string _declaredTransitionRoomId;

        private byte _localSlot;
        private int _bodiesBuilt;
        private int _bodyFailures;
        private int _bodiesRemoved;
        private string _lastBodyError = "none";

        // Which room lookup actually supplied the current room, and the id it supplied. Two play-tests
        // were spent guessing which of the four candidates was live at boot; the overlay prints both
        // so a null room says WHERE the chain broke rather than just "no current room".
        private string _lastRoomSource = "none";
        private string _lastRoomId = "none";

        // F-12: the local player is a prefab instance. Nothing in the shipped rig stamps the two
        // values SimProjectileSnapshotProjector.CollectUnits() needs (a valid EntityId) and the
        // room attribution needs (a live tile query). Both are stamped HERE, on the network path
        // only, so single-player keeps its rig byte-for-byte unchanged.
        private SimProjectileSnapshotProjector _projector;
        private RoomStreamingManager _streaming;
        private PlayerController _boundPlayer;
        private string _boundRoomId;
        private ulong _boundPlayerHash;
        private int _identityStamps;
        private int _roomRebinds;
        private int _bootRoomActivations;
        private bool _bootRoomActivated;
        private int _skippedForeignRooms;
        private int _projectedLocalPlayers;

        // The container is scene-scoped while this session is DontDestroyOnLoad. A return-to-title
        // followed by a new game destroys the scope but leaves every cached reference alive as a
        // managed object, so the session reports "initialized" while nothing ticks it any more.
        private GameLifetimeScope _scope;
        private int _staleScopeFrames;

        // ── Host world/campaign contract (P2) ───────────────────────────────────────────
        // The client builds a world of its own before the first packet arrives. Until the host names
        // its room set, every RoomHash-addressable delta is being applied to an unverified map.
        private PfeWorldContract _localContract;
        private PfeWorldContract _hostContract;
        private PfeWorldVerdict _worldVerdict = PfeWorldVerdict.Unknown;
        private string _worldReason = "none";
        private int _worldContractsSent;
        private int _worldContractsReceived;
        private int _worldContractErrors;
        private int _worldAppliesSkipped;
        private readonly List<NetworkConnection> _pendingContractPeers = new List<NetworkConnection>(4);
        private readonly HashSet<int> _contractSentConnectionIds = new HashSet<int>();

        private bool _initialized;
        private bool _started;
        private bool _transportStarted;
        private bool _joinSent;
        // True only while no config owns the process yet and the menu may still supply one.
        private bool _awaitMenu;
        private string _launchSource = "waiting for menu";
        private ulong _declaredLocalHash;
        private int _tickCount;
        private int _decodeFailures;
        private int _receiveFailures;
        private int _receiveRefusals;
        private int _boundPeers;
        private string _lastError = "none";

        public int TickOrder => SimTickOrder.ViewAligned;

        private void Configure(PfeNetConfig config)
        {
            _config = config;
            PfeReplicationBroadcastSerializers.Register();
            // Both ends need this one too: a missing custom writer drops the world contract silently,
            // leaving the client at "host contract pending" with an otherwise healthy transport.
            PfeWorldContractBroadcastSerializers.Register();

            // AddComponent on an inactive GameObject defers Awake, so SpawnablePrefabs can be set
            // before FishNet validates it. Activating then runs the full manager initialisation.
            var netGo = new GameObject("[PfeNet NetworkManager]");
            netGo.SetActive(false);
            _manager = netGo.AddComponent<NetworkManager>();
            _manager.SpawnablePrefabs = ScriptableObject.CreateInstance<SinglePrefabObjects>();
            netGo.SetActive(true);

            if (_manager.ServerManager == null || _manager.ClientManager == null
                || _manager.TransportManager == null || _manager.TransportManager.Transport == null)
            {
                _lastError = "FishNet NetworkManager did not initialise (SpawnablePrefabs?)";
                Debug.LogError("[PfeNet] " + _lastError);
            }
        }

        private void Update()
        {
            // With no config this is a dormant watcher, not a session. In a built player with no
            // launch arguments the main-menu bridge is the only way to start one.
            if (_config == null)
            {
                PollMenuLaunch();
                return;
            }

            if (!_initialized)
            {
                EnsureInitialized();
                return;
            }

            // Display-only bodies ease toward the newest replicated sample at render-frame rate.
            // Runs before the dead-scope check on purpose: interpolation is a view concern, and
            // parking a body exactly on its last target is harmless even past a dead game scope.
            TickDisplayBodyVisuals();

            // _scope is a LifetimeScope (MonoBehaviour), so Unity's overloaded == null reports a
            // destroyed one. Count frames rather than latching a flag: a flag cleared at the end of
            // its producer's tick is unreadable on the next one, and a reset flag needs an explicit
            // release on every path. A monotonic counter has neither problem.
            if (_scope != null) return;
            _staleScopeFrames++;
            if (_staleScopeFrames == 1)
            {
                _lastError = "game scene replaced: cached SimLoop/container are dead, "
                    + "the session stopped ticking";
            }
        }

        /// <summary>
        /// Consume the menu's one-shot bridge value and turn it into a real session. On a refusal the
        /// watcher STAYS ARMED: the user can correct the field and start again, and <c>_lastError</c>
        /// names the seam on the overlay instead of the button silently doing nothing.
        /// </summary>
        private void PollMenuLaunch()
        {
            if (!_awaitMenu) return;

            PfeNetLaunchRequest request = PfeNetLaunchData.ConsumePending();
            if (request == null) return;

            PfeNetConfig config;
            string reason;
            if (!PfeNetLaunchResolver.TryResolveMenu(request, out config, out reason))
            {
                _lastError = "menu launch refused: " + reason;
                Debug.LogError("[PfeNet] " + _lastError);
                return;
            }

            _awaitMenu = false;
            _launchSource = "menu";
            Configure(config);
        }

        private void EnsureInitialized()
        {
            if (_config == null || _manager == null) return;
            if (_manager.ServerManager == null || _manager.ClientManager == null
                || _manager.TransportManager == null || _manager.TransportManager.Transport == null) return;

            GameLifetimeScope scope = UnityEngine.Object.FindFirstObjectByType<GameLifetimeScope>();
            if (scope == null || scope.Container == null) return;

            SimLoop loop;
            SimProjectileSnapshotProjector projector;
            PFE.Core.Rng.IWorldSeed worldSeed;
            // TryResolve is an EXTENSION method on IObjectResolver; letting the out-parameter infer T
            // is the idiom this project already uses (PfeScenarioTest.cs:921).
            if (!scope.Container.TryResolve(out loop) || !scope.Container.TryResolve(out projector)) return;
            if (loop == null || projector == null) return;

            // The world seed is what the contract compares and what both sides generate from. Resolved
            // here rather than injected because the session is created before the game scene exists
            // (see Bootstrap). A missing seed is not fatal — the contract then reports 0 and a peer
            // with a real seed reads as a mismatch, which is the safe direction.
            if (!scope.Container.TryResolve(out worldSeed)) worldSeed = null;
            _worldSeed = worldSeed;

            _loop = loop;
            _scope = scope;
            // Keep the projector: its last snapshot is the only offline-visible readback that says
            // whether the local PLAYER (as opposed to an NPC) reached the wire this tick.
            _projector = projector;
            _router = new PfeReplicationRouter();
            _wire = new PfeFishNetWire(_manager, _config.IsHost, _router);
            _sink = new PfeRemoteCommandSink();
            _applier = new PfeReplicationWorldApplier();
            _baselineView = new PfeBaselineViewAssembler();
            // Slice 3: the client-side display spawner for the transient set the projector already
            // emits (rounds, throws, mines). Pure reconcile state — the views themselves are built
            // per record inside OnClientBroadcast, never on the host path.
            _projectiles = new PfeRemoteProjectileDisplay();

            if (_config.IsHost)
            {
                ulong sessionId = unchecked((ulong)DateTime.UtcNow.Ticks) | 1UL;
                // `this` is the room-transition sink: a peer's room change relocates that peer's
                // simulated body, which is exactly the desync this session exists to prevent.
                //
                // The interact sink runs the host-side authoritative container open for a peer's
                // admitted InteractPressed edge (slice 1: loot sync). It resolves rooms by the same
                // RoomKey the projector hashes, and bodies from the live slot index.
                _interactSink = new PfeInteractSink(FindRoomByHash, LookupBodyBySlot);
                _host = new SimReplicationHost(sessionId, projector, _wire, _config.ChunkSize,
                    _config.CommandWindow, _sink, this, _interactSink);

                // requireAuthentication MUST be false: there is no authenticator in this bootstrap.
                // FishNet's default true drops every client-to-server broadcast and kicks the sender.
                _manager.ServerManager.RegisterBroadcast<PfeReplicationBroadcast>(
                    OnServerBroadcast, requireAuthentication: false);
                _manager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
                _manager.ServerManager.OnServerConnectionState += OnServerConnectionState;

                _started = _manager.ServerManager.StartConnection(_config.Port);
            }
            else
            {
                // A joined peer never originates authoritative Sim state; it mirrors host truth. This
                // is the push GameLifetimeScope's comment reserved for a future client — it makes the
                // ambient write context ClientPrediction for every non-tick path (the suppression
                // latch in ContainerPropPresenter reads PeerRole too), and HostWriteGuard's debug
                // tripwires become live on this process.
                SimAuthorityContext.SetPeerRole(SimPeerRole.Client);

                ulong nonce = _config.ClientNonce != 0UL
                    ? _config.ClientNonce
                    : (ulong)System.Diagnostics.Process.GetCurrentProcess().Id + 1UL;
                _client = new SimReplicationClient(nonce, _wire);

                _manager.ClientManager.RegisterBroadcast<PfeReplicationBroadcast>(OnClientBroadcast);
                _manager.ClientManager.RegisterBroadcast<PfeWorldContractBroadcast>(OnClientWorldContract);
                _manager.ClientManager.OnClientConnectionState += OnClientConnectionState;

                _started = _manager.ClientManager.StartConnection(_config.Address, _config.Port);
            }

            _declaredLocalHash = CurrentRoomHash();
            // The host owns slot 0; a client is assigned a peer slot, and the only body it builds is
            // the host's (slot 0), so its own slot only has to differ from 0.
            _localSlot = _config.IsHost ? PfeRemoteBodyRules.HostSlot : (byte)1;
            loop.Register(this);
            _initialized = true;
        }

        // ── Sim loop ───────────────────────────────────────────────────────────────────────

        public void SimTick(int tickIndex)
        {
            if (!_initialized || _config == null) return;
            _tickCount++;

            // The host-assigned peer id decides the local slot; commands arrive stamped with the
            // same number, so the local player's identity and its command slot cannot diverge.
            if (!_config.IsHost && _client != null && _client.IsJoined)
            {
                byte resolvedSlot;
                if (PfeRemoteBodyRules.TryLocalPlayerSlot(false, _client.PeerId, out resolvedSlot))
                    _localSlot = resolvedSlot;
            }

            EnsureBootRoomActive();
            EnsureLocalPlayerNetworkIdentity();
            _projectedLocalPlayers = CountProjectedLocalPlayers();

            if (_config.IsHost)
            {
                // A peer can be admitted before the host's world build has produced a room. The bind
                // callback tries once; retry here until every joined slot has a body.
                BuildPendingBodies();
                // Intent is applied every sim tick, not only on snapshot ticks: the locomotion holds
                // its levels between commands, and the command gate refuses a repeat, so a tick with
                // no new command changes nothing while a new one lands on the very next tick.
                DriveBodies();
                // Rooms follow PLAYERS, not the local transition driver alone: a peer in another
                // room keeps that room simulated and projected. The legacy ActivateRoom driver
                // only knows the local player, so presence is re-asserted every tick; the diff
                // inside ApplyRoomPresence makes the steady state free.
                ApplyHostRoomPresence();
                // The world build is asynchronous and may still be empty when a peer joins. Publishing
                // from the tick (not from the join callback) is what lets a zero-room world be retried
                // until it exists, instead of sending a contract the client can never match.
                PublishPendingWorldContracts();
                if (_host != null && _tickCount % _config.TicksPerSnapshot == 0) _host.Tick();
                return;
            }

            if (_client == null) return;

            // The contract can arrive before the local world build finishes. Re-capture periodically
            // while the last comparison said "no local world", so the verdict does not stay stale.
            if (_hostContract != null
                && PfeWorldContractRules.ShouldRetryLocalCapture(_worldVerdict, _tickCount))
            {
                RefreshWorldVerdict();
            }

            if (!_joinSent && _transportStarted)
            {
                _client.Join(BuildInterest(_declaredLocalHash));
                _joinSent = true;
            }
            if (!_client.IsJoined) return;

            ulong localHash = CurrentRoomHash();
            if (localHash != _declaredLocalHash)
            {
                _client.UpdateInterest(BuildInterest(localHash));
                _declaredLocalHash = localHash;
            }

            // The room I stand in is mine alone on this machine — the host simulates my body and
            // cannot see my local LandMap flip. Declare the room (once after join, then on every
            // change) or the host body stays in the first room and our positions diverge.
            PublishLocalRoomTransition();

            if (_tickCount % _config.TicksPerSnapshot != 0) return;
            var player = SinglePlayerSlotContext.Current.GetPlayer(0);
            if (player != null) _client.SendCommands(player.CommandBuffer, _config.CommandWindow);
        }

        // ── Remote bodies (F-12) ──────────────────────────────────────────────────────────

        /// <summary>
        /// Build a body for <paramref name="slot"/> once. Everything it needs is copied from the
        /// local player's own rig rather than looked up by id: the definition and the sprite are the
        /// only two values that must be right for the body to exist on screen, and inventing a
        /// resource id would fail silently at runtime with a body that ticks but never draws.
        ///
        /// <para><b>Why the visual is an assembler, not a sprite.</b> The pony is a multi-part
        /// <c>CharacterQuadAssembler</c> rig on the player root; the prefab's only static
        /// <c>SpriteRenderer</c> is the rifle under the Weapon child, and
        /// <c>GetComponentInChildren&lt;SpriteRenderer&gt;</c> finds depth-first — so the first build
        /// copied the GUN and drew it as the remote player's whole body (the "I see only a weapon"
        /// report). The assembler is handed over whole: definition, style, appearance and sorting
        /// layer, so the body draws the same pony this player's rig draws.</para>
        ///
        /// <para><b>Why the room does NOT come from <c>local.Room</c>.</b> That property is
        /// <c>_tileQuery?.Room</c> on <c>UnitController</c>, and the shipped player is a prefab
        /// instance: <c>PlayerRigBuilder</c> — the only production caller of <c>SetTileQuery</c> —
        /// never runs for it (see <c>MapBridge.cs:776</c>). Its <c>_tileQuery</c> stays null for the
        /// whole session, so <c>local.Room</c> is null too. Reading it here made every body fail on
        /// BOTH machines while transport, snapshots, commands and the world contract all stayed
        /// healthy. The same null also makes the snapshot projector skip the local player entirely
        /// (<c>SimProjectileSnapshotProjector.cs:304</c>) — see the F-12 note in the daily log.</para>
        ///
        /// <para><b>Why not <c>RoomStreamingManager.CurrentRoom</c> alone.</b> It is only written by
        /// <c>ActivateRoom</c>, which on the normal boot path first runs on a DOOR transition. Using
        /// it deferred every body until the player changed room: 1055 host ticks / 135 client
        /// snapshots of failures, then a body the instant the transition ran.
        /// <c>CurrentRoomInstance()</c> now resolves the live <c>LandMap.currentRoom</c> first.</para>
        /// </summary>
        private void BuildBody(byte slot, bool simulated)
        {
            if (_bodyBySlot.ContainsKey(slot)) return;
            if (!PfeRemoteBodyRules.ShouldCreateBody(slot, _localSlot)) return;

            PlayerController player = SinglePlayerSlotContext.Current.GetPlayer(0);
            // PlayerController derives from UnitController: the rig IS the unit. Searching the
            // children for a separate UnitController found nothing on the shipped player prefab.
            UnitController local = player;
            RoomInstance room = ResolveBodyRoom(slot, simulated);

            if (local == null || room == null)
            {
                // Two distinct causes, two distinct messages: a combined "unit/room" error hid which
                // half was missing for an entire play-test.
                string reason = local == null
                    ? "no local player unit to copy from"
                    : "no current room to build in";
                _bodyFailures++;
                _lastBodyError = "slot " + slot + ": " + reason;
                _lastError = "body slot " + slot + ": " + reason;
                return;
            }

            // The visual source is the rig itself, never a searched renderer: the prefab's only static
            // SpriteRenderer is the rifle under the Weapon child, and a GetComponentInChildren search
            // finds it first — the first build drew a floating gun as the remote player's whole body.
            var visualSource = player.GetComponent<CharacterQuadAssembler>();
            // The player's ability sheet (levitation/double-jump/teleport gates). The simulated body
            // wears a mana-free wrapper around it — see RemoteBodyAbilities.
            var abilitySource = player.GetComponent<ILocomotionAbilities>();
            // The animation driver's serialized frame mappings (stay-aim sub-range, jump velocity
            // mapping) are copied onto the simulated body so the host captures the same poses the
            // local rig draws; the rig's weapon renderer is the template for the remote body's
            // weapon child (hold point + sorting).
            var animationSource = player.GetComponent<CharacterAnimationDriver>();
            SpriteRenderer weaponVisualSource = FindWeaponRenderer(player.transform);
            SimUnitState localState = local.CaptureSimState();

            // A simulated peer spawns where the PEER declared itself, not at the local player's
            // position: CaptureSimState reads this machine's own rig, which is the host player —
            // the only other guess is wrong whenever the peer stands in another room.
            Vector2 spawnPixel = new Vector2(localState.Px, localState.Py);
            Vector2 declaredSpawn;
            if (simulated && _peerRoomWorldPx.TryGetValue(slot, out declaredSpawn)) spawnPixel = declaredSpawn;

            var context = new PfeRemoteBodyContext
            {
                Slot = slot,
                Simulated = simulated,
                Room = room,
                Definition = simulated ? local.Stats : null,
                Clock = _loop != null ? _loop.Clock : null,
                Loop = _loop,
                VisualSource = visualSource,
                OwnerDefinition = local.Stats,
                AbilitySource = abilitySource,
                AnimationSource = animationSource,
                WeaponVisualSource = weaponVisualSource,
                SpawnPixel = spawnPixel,
            };

            // The weapon mount's services, resolved from the same container the rest of the body
            // copies from. Each TryResolve failure leaves the field null; the mount degrades per
            // service (an unarmed body is a visible overlay fact, not a throw inside a spawn).
            if (_scope != null && _scope.Container != null)
            {
                PFE.Systems.Combat.IProjectileFactory factory;
                if (_scope.Container.TryResolve(out factory)) context.ProjectileFactory = factory;
                PFE.Systems.Audio.ISoundService sound;
                if (_scope.Container.TryResolve(out sound)) context.Sound = sound;
                PFE.Core.Rng.IRngService rng;
                if (_scope.Container.TryResolve(out rng)) context.Rng = rng;
                PfeDebugSettings debug;
                if (_scope.Container.TryResolve(out debug)) context.Debug = debug;
                context.Resolver = _scope.Container;
            }

            string error;
            PfeRemoteBody body = PfeRemoteBody.TryCreate(context, out error);
            if (body == null)
            {
                _bodyFailures++;
                _lastBodyError = "slot " + slot + ": " + error;
                _lastError = "body slot " + slot + ": " + error;
                return;
            }

            _bodies.Add(body);
            _bodyBySlot[slot] = body;
            _bodiesBuilt++;

            // The overlay reports the LAST failure, not the current state. Without this, a body that
            // finally builds after a room transition still shows the stale "no current room" error —
            // which is exactly what the second play-test showed with bodies built=1 live=1.
            _lastBodyError = "none";
            if (_lastError != null && _lastError.StartsWith("body slot ")) _lastError = "none";
        }

        /// <summary>The local rig's held-weapon renderer — the template for a remote body's weapon
        /// child. Searched by NAME ("Weapon" is the rig-builder convention): a component search would
        /// find the muzzle's or the body's own renderer first, and the remote gun would inherit the
        /// wrong position and sorting.</summary>
        private static SpriteRenderer FindWeaponRenderer(Transform playerRoot)
        {
            if (playerRoot == null) return null;
            Transform weapon = playerRoot.Find("Weapon");
            return weapon != null ? weapon.GetComponentInChildren<SpriteRenderer>() : null;
        }

        /// <summary>The room a new body is built in. A simulated peer body prefers the room the peer
        /// itself declared — the fallback (<see cref="CurrentRoomInstance"/>) is the HOST player's
        /// room, which is the wrong answer whenever the peer stands elsewhere. Display bodies and
        /// peers that have not declared yet keep the fallback.</summary>
        private RoomInstance ResolveBodyRoom(byte slot, bool simulated)
        {
            if (simulated)
            {
                string declaredRoomId;
                if (_peerRoomIds.TryGetValue(slot, out declaredRoomId)
                    && !string.IsNullOrEmpty(declaredRoomId))
                {
                    RoomInstance declared = FindRoomById(declaredRoomId);
                    if (declared != null) return declared;

                    _roomTransitionUnknownRooms++;
                    _lastTransitionError = "slot " + slot + ": no room '" + declaredRoomId + "' in the host map";
                }
            }
            return CurrentRoomInstance();
        }

        /// <summary>Host only: rebuild any peer body that the one-shot bind callback could not build.
        /// A join can arrive before <c>RoomStreamingManager.CurrentRoom</c> exists; without this the
        /// host reports <c>failed=1</c> forever and never simulates that peer. Once every joined slot
        /// has a body the cheap count check skips the whole loop.</summary>
        private void BuildPendingBodies()
        {
            if (_host == null || _bodyBySlot.Count >= _host.JoinedPeerCount) return;

            uint[] peers = _host.JoinedPeerIds();
            for (int i = 0; i < peers.Length; i++)
            {
                if (peers[i] > byte.MaxValue) continue;
                byte slot = (byte)peers[i];
                if (_bodyBySlot.ContainsKey(slot)) continue;
                BuildBody(slot, simulated: true);
            }
        }

        /// <summary>Host only: hand each peer body its newest admitted command. The body itself
        /// refuses a repeat, which is what keeps jump and dash from re-firing every tick. Weapon
        /// mounts advance EVERY tick (after the drive attempt), not only on a fresh command: a held
        /// trigger and a cooling barrel do not freeze between deliveries.</summary>
        private void DriveBodies()
        {
            if (_bodies.Count == 0 || _sink == null) return;

            for (int i = 0; i < _bodies.Count; i++)
            {
                PfeRemoteBody body = _bodies[i];
                if (body == null || !body.IsSimulated) continue;

                PlayerCommand command;
                if (_sink.TryGetLatest(body.Slot, out command)) body.Drive(command);
                body.TickWeapons();
            }
        }

        // ── Peer room transitions (the client → host "I changed rooms" declaration) ─────────

        /// <summary>ISimRoomTransitionSink, host side. Every declaration — not just the well-formed
        /// ones — passes <see cref="SimHostRoomTransitionRules.Admit"/>: the named room must exist
        /// and the arrival position must lie inside that room's world-pixel bounds (plus the doorway
        /// margin). A refused packet leaves the peer's recorded room and body untouched — before
        /// this gate, a bug or an out-of-order packet could teleport the peer across the entire map
        /// or park its body outside the room it claims to stand in.
        ///
        /// <para>One admitted declaration does two things: records the peer's room (the per-tick
        /// presence pass and later body builds read it) and, when the peer's body already exists,
        /// relocates it — motor room, authoritative position and tile query in one call, so the
        /// projector keeps attributing the body to the room it actually stands in.</para></summary>
        public void Apply(uint peerId, byte slot, string roomId, float worldPixelX, float worldPixelY)
        {
            if (!_config.IsHost) return;

            RoomInstance room = FindRoomById(roomId);
            Vector2 roomMinPx = Vector2.zero;
            Vector2 roomMaxPx = Vector2.zero;
            if (room != null)
            {
                // RoomOriginPixel is the room's bottom-left world-pixel corner — the land-Y flip
                // makes lower rows more negative, so adding height * TILE_SIZE reaches the top
                // edge. width/height are the room's own tile dims, not the 48x25 default a fixed
                // rect would silently bake in for every prob room.
                roomMinPx = WorldCoordinates.RoomOriginPixel(room);
                roomMaxPx = roomMinPx + new Vector2(
                    room.width * WorldConstants.TILE_SIZE,
                    room.height * WorldConstants.TILE_SIZE);
            }

            SimHostRoomTransitionVerdict verdict = SimHostRoomTransitionRules.Admit(
                room != null, worldPixelX, worldPixelY,
                roomMinPx.x, roomMinPx.y, roomMaxPx.x, roomMaxPx.y);
            if (verdict != SimHostRoomTransitionVerdict.Admit)
            {
                _roomTransitionRefused++;
                _lastTransitionError = "slot " + slot + ": " + SimHostRoomTransitionRules.Describe(verdict) +
                    " (room '" + roomId + "' @ " + worldPixelX + ", " + worldPixelY + ")";
                return;
            }

            _roomTransitionsApplied++;
            _peerRoomIds[slot] = roomId;
            _peerRoomWorldPx[slot] = new Vector2(worldPixelX, worldPixelY);
            RelocatePeerBody(slot, roomId, worldPixelX, worldPixelY);
        }

        private void RelocatePeerBody(byte slot, string roomId, float worldPixelX, float worldPixelY)
        {
            PfeRemoteBody body;
            if (!_bodyBySlot.TryGetValue(slot, out body) || body == null)
            {
                // BuildPendingBodies()/BuildBody() consume the recorded declaration instead; a
                // body that was never built is relocated at build time, not here.
                return;
            }

            RoomInstance room = FindRoomById(roomId);
            if (room == null)
            {
                _roomTransitionUnknownRooms++;
                _lastTransitionError = "slot " + slot + ": no room '" + roomId + "' in the host map";
                return;
            }

            // World pixels are the wire space; RepositionForRoom speaks Unity world space. The
            // same conversion seeds the body at build, so the two hand-offs cannot drift apart.
            Vector3 worldPos = WorldCoordinates.PixelToUnity(new Vector2(worldPixelX, worldPixelY));
            body.Relocate(room, worldPos);
            _roomRelocations++;
            if (_lastTransitionError.StartsWith("slot " + slot + ":")) _lastTransitionError = "none";
        }

        /// <summary>Resolve a room id against the LIVE world. Never cached: a land rebuild replaces
        /// every RoomInstance, and a stale instance would silently bind the body to the old world.
        /// Prob rooms are registered in the prob context rather than the land map, so the lookup
        /// covers both registries.</summary>
        private RoomInstance FindRoomById(string roomId)
        {
            LandMap map = CurrentMap();
            if (!string.IsNullOrEmpty(roomId) && map != null)
            {
                foreach (RoomInstance room in map.GetAllRooms())
                {
                    if (room != null && string.Equals(room.id, roomId, StringComparison.Ordinal)) return room;
                }
            }

            PFE.Systems.Map.Generation.ProbRoomSessions sessions = ProbSessions();
            if (sessions != null)
            {
                for (int i = 0; i < sessions.All.Count; i++)
                {
                    RoomInstance room = sessions.All[i].Room;
                    if (room != null && string.Equals(room.id, roomId, StringComparison.Ordinal)) return room;
                }
            }
            return null;
        }

        /// <summary>The hash half of <see cref="FindRoomById"/>: the interact sink's requests (and the
        /// client's command targets) name a room by <c>SimSnapshotProjectionRules.RoomKey</c>, the same
        /// key the projector stamps into every delta. Resolved per request — container opens are rare,
        /// and the live-world rule is the same one the id lookup follows.</summary>
        private RoomInstance FindRoomByHash(ulong roomHash)
        {
            if (roomHash == 0UL) return null;

            LandMap map = CurrentMap();
            if (map != null)
            {
                foreach (RoomInstance room in map.GetAllRooms())
                {
                    if (room != null && !string.IsNullOrEmpty(room.id)
                        && SimSnapshotProjectionRules.RoomKey(room.id) == roomHash)
                    {
                        return room;
                    }
                }
            }

            PFE.Systems.Map.Generation.ProbRoomSessions sessions = ProbSessions();
            if (sessions != null)
            {
                for (int i = 0; i < sessions.All.Count; i++)
                {
                    RoomInstance room = sessions.All[i].Room;
                    if (room != null && !string.IsNullOrEmpty(room.id)
                        && SimSnapshotProjectionRules.RoomKey(room.id) == roomHash)
                    {
                        return room;
                    }
                }
            }
            return null;
        }

        /// <summary>The interact sink's body lookup: the simulated host body for a slot, or null when
        /// none exists (the peer has not joined or its body was never built).</summary>
        private PfeRemoteBody LookupBodyBySlot(byte slot)
        {
            PfeRemoteBody body;
            return _bodyBySlot.TryGetValue(slot, out body) ? body : null;
        }

        private static PFE.Systems.Map.Generation.ProbRoomSessions ProbSessions()
        {
            RoomTransitionManager manager = RoomTransitionManager.Instance;
            return manager != null ? manager.ProbSessions : null;
        }

        /// <summary>Whether <paramref name="room"/> still belongs to the live world — the land map
        /// or the prob registry. A body can outlive the world that built it (land rebuild); keeping
        /// or relocating into a dead room would pin streaming open against nothing.</summary>
        private static bool IsRegisteredRoom(LandMap map, RoomInstance room)
        {
            if (room == null) return false;
            if (map != null && MapContainsRoom(map, room)) return true;

            PFE.Systems.Map.Generation.ProbRoomSessions sessions = ProbSessions();
            if (sessions != null)
            {
                for (int i = 0; i < sessions.All.Count; i++)
                {
                    if (ReferenceEquals(sessions.All[i].Room, room)) return true;
                }
            }
            return false;
        }

        private static bool MapContainsRoom(LandMap map, RoomInstance room)
        {
            foreach (RoomInstance candidate in map.GetAllRooms())
            {
                if (ReferenceEquals(candidate, room)) return true;
            }
            return false;
        }

        /// <summary>Host only: reconcile the streaming set with who is actually where — the local
        /// (host) player plus every simulated peer body. Driven every tick because the legacy
        /// single-player driver (<c>RoomTransitionManager</c> → <c>ActivateRoom</c>) runs on the
        /// same manager and only knows the local player; the diff inside
        /// <see cref="RoomStreamingManager.ApplyRoomPresence"/> makes the steady state free.</summary>
        private void ApplyHostRoomPresence()
        {
            if (_host == null || _host.JoinedPeerCount == 0) return;

            RoomStreamingManager streaming = ResolveStreaming();
            if (streaming == null) return;

            LandMap map = CurrentMap();
            var presence = new Dictionary<RoomInstance, int>(8);

            // The presentation room is anchored to the HOST's own player, not resolved fresh from
            // the LandMap-global currentRoom: that global names "the local player's room", and the
            // overlay and this pass both trust it. See HostPresentationRoom for the play-test #7
            // blue void this prevents.
            RoomInstance localRoom = HostPresentationRoom();
            if (localRoom != null) presence[localRoom] = 1;

            for (int i = 0; i < _bodies.Count; i++)
            {
                PfeRemoteBody body = _bodies[i];
                if (body == null || !body.IsSimulated) continue;

                RoomInstance room = body.Room;
                if (room == null) continue;
                // A body can outlive the world that built it (land rebuild): a room that is no
                // longer registered anywhere must not pin streaming open.
                if (!IsRegisteredRoom(map, room)) continue;

                int count;
                presence.TryGetValue(room, out count);
                presence[room] = count + 1;
            }

            streaming.ApplyRoomPresence(presence, localRoom);
        }

        /// <summary>
        /// The room that may own THIS machine's presentation layer — the room the local player
        /// stands in, resolved from the bound player rather than the LandMap-global
        /// <c>currentRoom</c>. The bind stamps the player's tile query and re-stamps it on every
        /// local transition (<see cref="EnsureLocalPlayerNetworkIdentity"/> runs earlier in the
        /// same tick, LandMap-led, so the query follows a door on the very next tick), and nothing
        /// in the remote relocation chain writes it: <c>PfeRemoteBody.Relocate</c> moves only the
        /// peer body's motor. Play-test #7's blue void was the host's own room being evicted the
        /// tick the presence pass named the client's room as local, so the value handed to
        /// <c>ApplyRoomPresence</c> is anchored to the one identity a remote packet cannot move.
        /// Falls back to <see cref="CurrentRoomInstance"/> until the player binds at boot.
        /// </summary>
        private RoomInstance HostPresentationRoom()
        {
            if (_boundPlayer != null && _boundPlayer.Room != null)
                return _boundPlayer.Room;
            return CurrentRoomInstance();
        }

        /// <summary>Client only: declare the local player's room to the host — once right after
        /// join (the host builds the peer's body from this declaration) and again on every room
        /// change. The check is id-change based, not transition-event based: every path that
        /// flips <c>LandMap.currentRoom</c> (door, edge, Z layer, prob entry, restore) is covered
        /// without subscribing to a scene singleton that dies on scene load.</summary>
        private void PublishLocalRoomTransition()
        {
            RoomInstance room = CurrentRoomInstance();
            string roomId = room != null && !string.IsNullOrEmpty(room.id) ? room.id : null;
            if (roomId == null
                || string.Equals(roomId, _declaredTransitionRoomId, StringComparison.Ordinal))
            {
                return;
            }

            PlayerController player = SinglePlayerSlotContext.Current.GetPlayer(0);
            if (player == null) return;

            Vector2 worldPixels = WorldCoordinates.UnityToPixel(player.transform.position);
            if (!_client.SendRoomTransition(roomId, worldPixels.x, worldPixels.y)) return;

            _declaredTransitionRoomId = roomId;
            _roomTransitionsSent++;
        }

        /// <summary>
        /// Client: ease every display-only body toward its newest replicated sample once per render
        /// frame. Snapshots arrive on the network's clock (ApplyState re-aims each body's buffer);
        /// these transform writes happen on the display's clock — that separation is the whole
        /// point of F-12's interpolation buffer. Simulated host bodies are skipped: they are
        /// authority and their motor owns the transform every sim tick.
        /// </summary>
        private void TickDisplayBodyVisuals()
        {
            for (int i = 0; i < _bodies.Count; i++)
            {
                PfeRemoteBody body = _bodies[i];
                if (body != null && !body.IsSimulated) body.TickVisual(Time.deltaTime);
            }
        }

        /// <summary>
        /// Client only: place each display body at the host's replicated position.
        ///
        /// <para><b>Why the delta's room hash is a gate, not decoration.</b> <c>SimUnitState.Px/Py</c>
        /// are ROOM-LOCAL pixels. Before this gate the receiver walked every room in the snapshot and
        /// offered every unit to every body, so a host avatar standing in another room was drawn at
        /// its own room-local coordinates inside the room the camera happened to follow. On the third
        /// play-test that read as "players in different rooms don't see each other" even though the
        /// command and snapshot paths were both healthy.</para>
        ///
        /// <para><b>Hide first, show on match.</b> A display body starts every snapshot hidden and is
        /// turned back on only by <c>PfeRemoteBody.ApplyState</c> finding its own hash in this room.
        /// A peer who leaves the room therefore leaves no statue behind.</para>
        /// </summary>
        private void ApplyBodyStates(in SimSnapshot snapshot)
        {
            if (_bodies.Count == 0 || snapshot.Rooms == null) return;

            for (int b = 0; b < _bodies.Count; b++)
            {
                if (_bodies[b] != null) _bodies[b].SetRemoteVisible(false);
            }

            ulong localRoomHash = CurrentRoomHash();

            for (int r = 0; r < snapshot.Rooms.Length; r++)
            {
                ulong roomHash = snapshot.Rooms[r].RoomHash;
                SimUnitState[] units = snapshot.Rooms[r].Units;
                if (units == null) continue;

                if (!PfeRemoteBodyRules.CanPlaceBodyInRoom(roomHash, localRoomHash))
                {
                    _skippedForeignRooms += units.Length;
                    continue;
                }

                for (int u = 0; u < units.Length; u++)
                {
                    for (int b = 0; b < _bodies.Count; b++)
                    {
                        PfeRemoteBody body = _bodies[b];
                        if (body != null && !body.IsSimulated) body.ApplyState(units[u]);
                    }
                }
            }
        }

        /// <summary>Destroy exactly the body owned by a peer that stopped, and drop it from BOTH
        /// indexes. A body left in <c>_bodies</c> keeps receiving <c>Drive</c>/<c>ApplyState</c>
        /// calls; a body left in <c>_bodyBySlot</c> blocks any later rejoin with that slot from
        /// ever rebuilding it. <c>PfeRemoteBody.Destroy</c> alone fixes neither.</summary>
        private void RemoveBody(byte slot)
        {
            PfeRemoteBody body;
            if (!_bodyBySlot.TryGetValue(slot, out body)) return;

            _bodyBySlot.Remove(slot);
            // The peer's room declaration dies with the peer: a rejoin must not inherit a room
            // from a previous session, and presence must drop the room on the next tick.
            _peerRoomIds.Remove(slot);
            _peerRoomWorldPx.Remove(slot);
            for (int i = _bodies.Count - 1; i >= 0; i--)
            {
                if (_bodies[i] == body) _bodies.RemoveAt(i);
            }
            if (body != null) body.Destroy();
            _bodiesRemoved++;
            _lastError = "removed body slot " + slot + " after disconnect";
        }

        /// <summary>Client side: a stopped connection leaves every display body a statue and every
        /// display round a frozen tracer. Destroy them all and clear both indexes so the next accepted
        /// snapshot can rebuild. Also used by <c>OnDestroy</c> so views never outlive their session.</summary>
        private void RemoveAllBodies()
        {
            for (int i = 0; i < _bodies.Count; i++)
            {
                PfeRemoteBody body = _bodies[i];
                if (body != null) body.Destroy();
            }
            _bodiesRemoved += _bodies.Count;
            _bodies.Clear();
            _bodyBySlot.Clear();
            _peerRoomIds.Clear();
            _peerRoomWorldPx.Clear();
            if (_projectiles != null) _projectiles.Clear();
        }

        // ── Host world/campaign contract (P2) ───────────────────────────────────────────

        /// <summary>
        /// Read this process's generated world and campaign identity. Null means "not built yet", which
        /// is a legitimate state: the world build is asynchronous and a client can receive its first
        /// contract before it has a single room.
        ///
        /// <para>Room keys come from the SAME helper the projector and the tile journal use to name a
        /// room in every delta. Inventing a second key would produce a contract that matches nothing
        /// even on two identical maps — and the symptom looks exactly like a lost packet.</para>
        /// </summary>
        private PfeWorldContract CaptureLocalContract()
        {
            LandMap map = CurrentMap();
            if (map == null) return null;

            var keys = new List<ulong>(64);
            foreach (RoomInstance room in map.GetAllRooms())
            {
                if (room == null || string.IsNullOrEmpty(room.id)) continue;
                keys.Add(SimSnapshotProjectionRules.RoomKey(room.id));
            }
            if (keys.Count == 0) return null;

            CampaignManager campaign = CampaignManager.Current;
            string landId = campaign != null
                ? campaign.CurrentLandId.CurrentValue ?? string.Empty : string.Empty;
            string missionId = campaign != null
                ? campaign.MissionId ?? string.Empty : string.Empty;
            bool hasCheckpoint = campaign != null && campaign.CurrentCheckpoint.HasValue;
            string checkpointCode = hasCheckpoint
                ? campaign.CurrentCheckpoint.Value.code ?? string.Empty : string.Empty;

            // Land layout inputs — exactly what the procedural build read (MapBridge →
            // WorldBuilder.BuildLand: state.landStage, state.visited, mbase_visited). Authored
            // layouts ignore all three, so they publish the (0,false,false) sentinel and can never
            // mismatch on a flag that did not shape their map.
            int landStage = 0;
            bool landVisited = false;
            bool landMbaseVisited = false;
            LandDefinition landDef = campaign != null && campaign.Catalog != null
                ? campaign.Catalog.GetLand(landId) : null;
            if (landDef != null && landDef.isProcedural)
            {
                LandRuntimeState landState = campaign.LandStates.Get(landId);
                landStage = landState.landStage;
                landVisited = landState.visited;
                landMbaseVisited = campaign.GetTrigger("mbase_visited").IsGreaterThanZero();
            }

            // Protocol v2 does not ship the set. Both sides derive it from (world seed, landId,
            // landStage), so the contract carries the seed — the CAUSE of any difference — and the
            // fold of the set — the EFFECT. The fold still goes through NormalizeRoomKeys, so a
            // re-ordered enumeration cannot move it and a duplicated key cannot cancel itself out.
            ulong[] normalized = PfeWorldContractRules.NormalizeRoomKeys(keys.ToArray());

            return new PfeWorldContract
            {
                Version = PfeWorldContractCodec.CurrentVersion,
                WorldSeed = _worldSeed != null ? _worldSeed.Value : 0UL,
                RoomCount = normalized.Length,
                RoomSetHash = PfeWorldContractRules.RoomSetSignature(normalized),
                LandId = landId,
                MissionId = missionId,
                HasCheckpoint = hasCheckpoint,
                CheckpointCode = checkpointCode,
                Difficulty = GlobalDifficulty.Value,
                LandStage = landStage,
                LandVisited = landVisited,
                LandMbaseVisited = landMbaseVisited,
            };
        }

        /// <summary>Send one contract per newly bound peer, on the reliable channel. The peer is queued
        /// at join and served on a later tick so a half-built world cannot be published.</summary>
        private void PublishPendingWorldContracts()
        {
            if (_pendingContractPeers.Count == 0) return;

            _localContract = CaptureLocalContract();
            if (!PfeWorldContractRules.IsWorldReady(_localContract)) return;

            byte[] bytes;
            string reason;
            if (!PfeWorldContractCodec.TrySerialize(_localContract, out bytes, out reason))
            {
                _worldContractErrors++;
                _lastError = "world contract refused: " + reason;
                return;
            }

            for (int i = _pendingContractPeers.Count - 1; i >= 0; i--)
            {
                NetworkConnection peer = _pendingContractPeers[i];
                if (peer == null)
                {
                    _pendingContractPeers.RemoveAt(i);
                    continue;
                }

                int connectionId = peer.ClientId;
                bool published = _contractSentConnectionIds.Contains(connectionId);
                if (!PfeWorldContractRules.ShouldPublish(_config.IsHost, published, _localContract))
                {
                    _pendingContractPeers.RemoveAt(i);
                    continue;
                }

                // requireAuthenticated (FishNet's spellings DIFFER: RegisterBroadcast takes
                // requireAuthentication) MUST be false: this bootstrap installs no authenticator, so
                // the default true would drop the packet and the client would wait forever.
                _manager.ServerManager.Broadcast(peer,
                    new PfeWorldContractBroadcast { Bytes = bytes },
                    requireAuthenticated: false, channel: Channel.Reliable);
                _contractSentConnectionIds.Add(connectionId);
                _worldContractsSent++;
                _pendingContractPeers.RemoveAt(i);
            }
        }

        private void OnClientWorldContract(PfeWorldContractBroadcast message, Channel channel)
        {
            if (_config == null || _config.IsHost) return;
            _worldContractsReceived++;

            PfeWorldContract contract;
            string reason;
            if (!PfeWorldContractCodec.TryDeserialize(message.Bytes, out contract, out reason))
            {
                // Network input never throws out of FishNet's receive loop.
                _worldContractErrors++;
                _lastError = "world contract refused: " + reason;
                return;
            }

            _hostContract = contract;
            RefreshWorldVerdict();
        }

        private void RefreshWorldVerdict()
        {
            _localContract = CaptureLocalContract();
            _worldVerdict = PfeWorldContractRules.Compare(_hostContract, _localContract, out _worldReason);
            if (_worldVerdict == PfeWorldVerdict.WorldMismatch)
            {
                _lastError = "host world mismatch: " + _worldReason + "; host state not applied";
            }
        }

        private static int RoomCount(PfeWorldContract contract)
        {
            return contract != null ? contract.RoomCount : 0;
        }

        // ── FishNet callbacks ─────────────────────────────────────────────────────────────

        private void OnServerBroadcast(NetworkConnection connection, PfeReplicationBroadcast message, Channel channel)
        {
            if (_host == null || _wire == null) return;

            SimReplicationEnvelope envelope;
            if (!_wire.TryReceive(message.Bytes, out envelope))
            {
                _decodeFailures++;
                return;
            }

            int connectionId = connection != null ? connection.ClientId : -1;
            uint[] before = _host.JoinedPeerIds();
            if (!_router.BeginReceive(connectionId))
            {
                _receiveRefusals++;
                return;
            }

            try
            {
                // A well-framed envelope can still carry a truncated payload. Network input must
                // never unwind FishNet's receive loop, so a payload failure becomes a counted refusal.
                _host.Receive(envelope);
            }
            catch (Exception ex)
            {
                _receiveFailures++;
                _lastError = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                _router.EndReceive();
            }

            // The join accept is emitted inside Receive, before any peer id is known to the wire.
            // Bind the newly admitted peer to the connection its request arrived on afterwards.
            uint[] after = _host.JoinedPeerIds();
            for (int i = 0; i < after.Length; i++)
            {
                if (Contains(before, after[i])) continue;
                if (_router.Bind(after[i], connectionId)) _boundPeers++;
                // Phase 5 binds slot = peerId for 1..255, so the body's identity is decided by the
                // same number the commands arrive stamped with.
                if (after[i] <= byte.MaxValue) BuildBody((byte)after[i], simulated: true);

                // The contract must be sent BEFORE the first snapshot can apply tile mutations to a
                // map nobody has compared. Queue the connection; the tick publishes once the host's
                // own world build has produced rooms.
                if (connection != null && !_pendingContractPeers.Contains(connection))
                    _pendingContractPeers.Add(connection);
            }
        }

        private void OnClientBroadcast(PfeReplicationBroadcast message, Channel channel)
        {
            if (_client == null || _wire == null) return;

            SimReplicationEnvelope envelope;
            if (!_wire.TryReceive(message.Bytes, out envelope))
            {
                _decodeFailures++;
                return;
            }

            int before = _client.Receiver.AcceptedSnapshotCount;
            try
            {
                _client.Receive(envelope);
                bool accepted = _client.Receiver.AcceptedSnapshotCount > before;

                // The view keeps its own copy of the baseline bytes: SimReplicationClient.Receive
                // returns void, so there is no snapshot to hand to the renderer otherwise.
                if (envelope.Kind == SimReplicationMessageKind.BaselineChunk)
                {
                    SimBaselineChunk chunk =
                        SimReplicationPayloadSerializer.ReadBaselineChunk(envelope.Payload);
                    _baselineView.Add(chunk);
                }

                if (!accepted)
                {
                    // The view's assembly completed but the receiver refused it: do not let stale
                    // bytes from a rejected lifetime be applied by the next accepted snapshot.
                    byte[] discarded;
                    _baselineView.TryTakeAssembly(out discarded);
                    return;
                }

                SimSnapshot snapshot;
                if (envelope.Kind == SimReplicationMessageKind.Snapshot)
                {
                    snapshot = SimSnapshotSerializer.Deserialize(envelope.Payload);
                }
                else
                {
                    byte[] assembled;
                    if (!_baselineView.TryTakeAssembly(out assembled)) return;
                    snapshot = SimSnapshotSerializer.Deserialize(assembled);
                }

                LandMap map = CurrentMap();
                // Host repeats unacknowledged journal events in the next snapshot, so a missing map
                // costs one frame of visibility, not the geometry itself.
                if (map == null) return;

                // A proven foreign map is the one case where applying host state is wrong: every
                // RoomHash addresses a room this client may not have. Every other verdict (no contract
                // yet, local world still building, campaign diverged) must still apply, or a healthy
                // session would freeze at startup.
                if (!PfeWorldContractRules.CanApplyHostState(_worldVerdict))
                {
                    _worldAppliesSkipped++;
                    return;
                }

                // Applying host state is a SnapshotApplication write, not client prediction: the guard
                // tripwires (HostWriteGuard.CheckRestore) and any authority-derived reads have to see
                // the mirrored context, or a client that mirrors host truth looks exactly like a client
                // that originates it.
                using (SimAuthorityContext.EnterSnapshotApplication())
                {
                    _applier.RefreshRooms(map);
                    _applier.Apply(snapshot);
                }

                // The host's own player is the remote avatar here. Built lazily on the first accepted
                // snapshot so the room and the definition are known to exist.
                BuildBody(PfeRemoteBodyRules.HostSlot, simulated: false);
                ApplyBodyStates(snapshot);

                // Rounds, thrown objects and mines ride the same room deltas. The room gate inside
                // is the same hash comparison the body pass uses: positions are host pixels, only
                // meaningful in the room the record arrived in.
                _projectiles.Apply(snapshot, CurrentRoomHash());
            }
            catch (Exception ex)
            {
                // A payload that the envelope frame accepted can still be truncated inside its own
                // reader. FishNet invokes handlers from its receive loop; an uncaught throw there
                // aborts the remaining packets on that connection.
                _receiveFailures++;
                _lastError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped) return;
            if (connection == null) return;

            if (_router == null) return;
            int connectionId = connection.ClientId;
            uint peerId;
            // Resolve BEFORE Unbind: it erases _peerByConnection, so a lookup afterwards always
            // fails and the peer's body survives its own disconnect forever.
            bool bound = _router.TryGetPeer(connectionId, out peerId);
            _router.Unbind(connectionId);

            // A rejoin must receive a fresh contract. Without this the sent-id set remembers the dead
            // connection and the new peer never learns which world it joined.
            _contractSentConnectionIds.Remove(connectionId);
            for (int i = _pendingContractPeers.Count - 1; i >= 0; i--)
            {
                NetworkConnection pending = _pendingContractPeers[i];
                if (pending == null || pending.ClientId == connectionId)
                    _pendingContractPeers.RemoveAt(i);
            }

            if (!bound) return;

            byte slot;
            if (!PfeRemoteBodyRules.TryRemovalSlot(peerId, _localSlot, out slot)) return;
            RemoveBody(slot);
        }

        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            _transportStarted = args.ConnectionState == LocalConnectionState.Started;
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            _transportStarted = args.ConnectionState == LocalConnectionState.Started;
            if (_transportStarted) return;

            _joinSent = false;
            // A stopped client has no host to replicate from: every display body would stand still
            // forever. Clearing both indexes lets the next accepted snapshot rebuild it lazily.
            RemoveAllBodies();

            // The next host we connect to must receive our room again: it never saw the declaration.
            _declaredTransitionRoomId = null;

            // The verdict belongs to the DEAD host. Keeping a WorldMismatch from it would block every
            // snapshot from the next host we connect to.
            _hostContract = null;
            _localContract = null;
            _worldVerdict = PfeWorldVerdict.Unknown;
            _worldReason = "no host yet";
        }

        // ── World lookups ─────────────────────────────────────────────────────────────────

        private LandMap CurrentMap()
        {
            if (_mapBridge == null) _mapBridge = UnityEngine.Object.FindFirstObjectByType<MapBridge>();
            return _mapBridge != null ? _mapBridge.CurrentLandMap : null;
        }

        /// <summary>The room the local player is standing in, resolved from the LIVE world state.
        ///
        /// <para><b>Why <c>LandMap.currentRoom</c> is first.</b> The second two-process play-test
        /// proved that <c>RoomStreamingManager.CurrentRoom</c> is null for the whole initial room:
        /// <c>MapBridge</c> creates the manager at boot (<c>MapBridge.cs:466-472</c>) but only
        /// <c>ActivateRoom</c> writes <c>_presentationRoom</c>, and on the normal boot path the first
        /// writer is a DOOR transition (<c>RoomTransitionManager.cs:828</c>). Both peers reported
        /// <c>no current room to build in</c> for 1055 / 135 attempts, then built their body the
        /// instant the player walked through a door. Sharing that dead lookup with the interest
        /// declaration was the mistake: it had already been silently returning room hash 0.</para>
        ///
        /// <para><c>LandMap.currentRoom</c> is AS3's <c>gg.loc</c>: <c>GameManager.GetCurrentRoom()</c>
        /// returns it, <c>MapBridge</c> spawns the player into it, and <c>CaptureLocalContract()</c>
        /// reads the same map that reported 4 rooms on both machines. It is live from boot and
        /// follows in-land door transitions.</para>
        ///
        /// <para>The later fallbacks are ordered cheapest-and-most-authoritative first and only run
        /// when the map has no room yet. Each records its source so the overlay can name it.</para>
        /// </summary>
        private RoomInstance CurrentRoomInstance()
        {
            LandMap map = CurrentMap();
            if (map != null && map.currentRoom != null)
                return RememberRoom("landMap", map.currentRoom);

            PlayerController player = SinglePlayerSlotContext.Current.GetPlayer(0);
            if (player != null)
            {
                if (player.CurrentRoom != null)
                    return RememberRoom("player.map", player.CurrentRoom);
                if (player.Room != null)
                    return RememberRoom("player.tileQuery", player.Room);
            }

            RoomStreamingManager streaming = UnityEngine.Object.FindFirstObjectByType<RoomStreamingManager>();
            if (streaming != null && streaming.CurrentRoom != null)
                return RememberRoom("streaming", streaming.CurrentRoom);

            _lastRoomSource = "none";
            _lastRoomId = "none";
            return null;
        }

        private RoomInstance RememberRoom(string source, RoomInstance room)
        {
            _lastRoomSource = source;
            _lastRoomId = string.IsNullOrEmpty(room.id) ? "<no id>" : room.id;
            return room;
        }

        private ulong CurrentRoomHash()
        {
            RoomInstance room = CurrentRoomInstance();
            return room != null && !string.IsNullOrEmpty(room.id)
                ? SimSnapshotProjectionRules.RoomKey(room.id)
                : 0UL;
        }

        /// <summary>
        /// Resolve the scene's streaming manager, retrying until it exists. <c>MapBridge</c> creates
        /// it at the END of its boot coroutine, after a multi-second visual build; a one-shot lookup
        /// from <c>EnsureInitialized()</c> would cache null for the whole session. Unity's overloaded
        /// <c>== null</c> also re-arms the lookup after a scene unload destroys the cached object.
        /// </summary>
        private RoomStreamingManager ResolveStreaming()
        {
            if (_streaming == null)
                _streaming = UnityEngine.Object.FindFirstObjectByType<RoomStreamingManager>();
            return _streaming;
        }

        private static bool ActiveRoomsContain(RoomStreamingManager streaming, RoomInstance room)
        {
            foreach (RoomInstance active in streaming.ActiveRooms)
            {
                if (ReferenceEquals(active, room)) return true;
            }
            return false;
        }

        /// <summary>
        /// Put the authoritative boot room into the projector's active-room set.
        ///
        /// <para><b>The third boot-time gap.</b> <c>SimProjectileSnapshotProjector.SimTick()</c>
        /// builds its <c>_rooms</c> list exclusively from <c>RoomStreamingManager.ActiveRooms</c>.
        /// <c>MapBridge.WaitForInitialization</c> CREATES the streaming manager
        /// (<c>MapBridge.cs:466-472</c>) but never calls <c>ActivateRoom</c>; the first writer on the
        /// normal boot path is a DOOR transition. Until then the projector emits a snapshot with
        /// <b>zero room deltas</b> — no units, no projectiles, no loot — while every wire counter
        /// stays perfectly healthy. That is why the second play-test needed a door transition before
        /// anything at all appeared, and why "commands are flowing" and "nobody is visible" were
        /// both true at once.</para>
        ///
        /// <para><b>Why <c>ActivateRoom</c> and not a second room source in the projector.</b> It is
        /// the manager's own designed entry point — the land-transition path already calls exactly
        /// this (<c>MapBridge.cs:1352-1353</c>). It sets <c>isActive</c>, raises OnRoomActivated so
        /// <c>PhysicsWorldService</c> builds the (idempotent) chain geometry, and gives the room a
        /// real lifecycle sequence for <c>SimRoomDelta.LifecycleSequence</c>. A projector that
        /// guessed its own fallback room would emit a delta with lifecycle 0 from a different source
        /// than the tile journal, which is a divergence no offline harness can see.</para>
        ///
        /// <para>Once-only: after boot, <c>RoomTransitionManager</c> owns activation. The guard also
        /// skips the call when something else already activated the room, so this never fights a
        /// transition that won the race.</para>
        /// </summary>
        private void EnsureBootRoomActive()
        {
            if (_bootRoomActivated) return;

            RoomInstance room = CurrentRoomInstance();
            RoomStreamingManager streaming = ResolveStreaming();
            if (room == null || streaming == null) return;

            if (ActiveRoomsContain(streaming, room))
            {
                _bootRoomActivated = true;
                return;
            }

            streaming.ActivateRoom(room);
            _bootRoomActivations++;
            _bootRoomActivated = true;
        }

        /// <summary>
        /// Stamp the local player with the two values the projector needs, and keep them current
        /// across door transitions.
        ///
        /// <para><b>Why the ID matters.</b> <c>CollectUnits()</c> skips every controller without
        /// <c>HasEntityId</c>, and <c>PfeRemoteBodyRules.IsBodyRecord</c> refuses a zero hash. The
        /// shipped player prefab never passes through <c>RoomUnitSpawner.Spawn</c>, so it has
        /// neither, and the host never projects its own avatar. The client's body for slot 0 then
        /// reports <c>state=0 foreign=28</c> forever: 28 real NPC records, none of them the host
        /// player. That was the exact third play-test symptom.</para>
        ///
        /// <para><b>Why the tile query matters.</b> The projector attributes a unit by
        /// <c>ReferenceEquals(controller.Room, room)</c>, and <c>Room</c> is
        /// <c>_tileQuery?.Room</c>. Without a query the player is filtered out of every room even
        /// after it has an id. Rebinding on room-id change keeps it in the delta after a door
        /// transition instead of leaving it attributed to the room that was just deactivated.</para>
        ///
        /// <para><b>Why identity is stamped from the peer id, not from slot 0.</b> A client stamping
        /// <c>player:0</c> would duplicate the host's hash. <see cref="PfeRemoteBodyRules.TryLocalPlayerSlot"/>
        /// returns false until the join accept supplies a real peer id, so an unjoined client stamps
        /// nothing at all rather than the wrong thing.</para>
        /// </summary>
        private void EnsureLocalPlayerNetworkIdentity()
        {
            PlayerController player = SinglePlayerSlotContext.Current.GetPlayer(0);
            RoomInstance room = CurrentRoomInstance();
            if (player == null || room == null) return;

            byte slot;
            uint peerId = _client != null ? _client.PeerId : 0u;
            if (!PfeRemoteBodyRules.TryLocalPlayerSlot(_config.IsHost, peerId, out slot)) return;

            ulong hash = PfeRemoteBodyRules.LocalPlayerHash(slot);
            if (!player.HasEntityId || _boundPlayerHash != hash)
            {
                player.AssignEntityId(EntityId.CreateForPlayer(slot),
                    new PFE.Core.Ids.EntityLifetime(PfeRemoteBodyRules.BodyGeneration, slot));
                _boundPlayerHash = hash;
                _identityStamps++;
            }

            string roomId = string.IsNullOrEmpty(room.id) ? null : room.id;
            // A scene reload replaces the player object. The new one has no tile query even when the
            // room id did not change, so the rebind must also key on the instance, not only the id.
            if (!ReferenceEquals(_boundPlayer, player)
                || PfeRemoteBodyRules.NeedsRoomRebind(_boundRoomId, roomId))
            {
                player.SetTileQuery(new UnifiedTileQueryService(room));
                _boundPlayer = player;
                _boundRoomId = roomId;
                _roomRebinds++;
            }
        }

        /// <summary>
        /// Readback for "the local PLAYER reached the wire", not just "units reached the wire".
        /// The projector's own <c>ProjectedUnitCount</c> counts NPCs too, so a healthy-looking number
        /// there says nothing about the avatar the peer is waiting for. This counts records carrying
        /// exactly the local player's hash in the projector's last snapshot.
        /// </summary>
        private int CountProjectedLocalPlayers()
        {
            if (_projector == null) return 0;

            SimSnapshot snapshot = _projector.Current;
            if (snapshot.Rooms == null) return 0;

            ulong hash = PfeRemoteBodyRules.LocalPlayerHash(_localSlot);
            int count = 0;
            for (int r = 0; r < snapshot.Rooms.Length; r++)
            {
                SimUnitState[] units = snapshot.Rooms[r].Units;
                if (units == null) continue;

                for (int u = 0; u < units.Length; u++)
                {
                    if (units[u].EntityHash == hash) count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Name the FIRST broken link in the projection chain. A panel full of zeroes is unreadable in
        /// a play-test; one word says whether to look at <c>SimTickRegistrar</c>, the streaming
        /// manager's boot timing, the player's entity id, or the room attribution.
        /// </summary>
        private PfeRemoteBodyRules.ProjectionFault CurrentProjectionFault()
        {
            if (_projector == null)
                return PfeRemoteBodyRules.ProjectionFault.NoStreamingManager;

            return PfeRemoteBodyRules.ClassifyProjection(
                _projector.HasStreaming,
                _projector.ActiveRoomCount,
                _projector.IdentifiedUnitCount,
                _projector.ProjectedUnitCount);
        }

        /// <summary>Coarse interest for this build: every room the map knows about is declared, plus
        /// the local room as the primary. Room-scoped interest management is a pure Sim feature that
        /// already works; narrowing the declared set is a view-layer tuning decision, not a protocol
        /// change. A zero local hash means "between rooms" and is legal.</summary>
        private SimPeerInterest BuildInterest(ulong localHash)
        {
            var adjacent = new List<ulong>();
            LandMap map = CurrentMap();
            if (map != null)
            {
                foreach (RoomInstance room in map.GetAllRooms())
                {
                    if (room == null || string.IsNullOrEmpty(room.id)) continue;
                    adjacent.Add(SimSnapshotProjectionRules.RoomKey(room.id));
                }
            }
            return new SimPeerInterest(localHash, adjacent);
        }

        private static bool Contains(uint[] values, uint value)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == value) return true;
            }
            return false;
        }

        private void OnDestroy()
        {
            if (_loop != null) _loop.Unregister(this);
            RemoveAllBodies();
            SimInteractCommandLatch.Reset();
            // A client session that dies (return to title, process exit) leaves the process in the
            // single-player default; the next game's GameLifetimeScope re-pushes Host at build, and
            // this covers the gap before it.
            if (_config != null && !_config.IsHost)
            {
                SimAuthorityContext.SetPeerRole(SimPeerRole.Host);
            }
            if (_manager == null) return;
            if (_manager.ServerManager != null)
            {
                _manager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
                _manager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
            }
            if (_manager.ClientManager != null)
            {
                _manager.ClientManager.UnregisterBroadcast<PfeWorldContractBroadcast>(OnClientWorldContract);
                _manager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
            }
        }

        // ── Owner play-test overlay ───────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (_config == null || _config.Role == PfeNetRole.Off) return;

            // Summary rows + three world-contract rows + three projection rows + two transition
            // rows + one projectile row + one row per live body. A fixed box would clip the last
            // body exactly when there are several peers.
            float height = 286f + 54f + 54f + 60f + 36f + 18f + 36f + 18f + _bodies.Count * 18f;
            GUI.Box(new Rect(8f, 8f, 460f, height), GUIContent.none);
            GUILayout.BeginArea(new Rect(18f, 14f, 440f, height - 14f));
            GUILayout.Label("<b>PFE replication</b>  ·  protocol v" +
                SimReplicationProtocolRules.CurrentProtocolVersion);
            GUILayout.Label(_config.IsHost
                ? "HOST  port " + _config.Port + "  ·  start=" + _started
                : "CLIENT " + _config.Address + ":" + _config.Port + "  ·  start=" + _started);
            GUILayout.Label("initialized=" + _initialized + "  transport=" + _transportStarted +
                "  ticks=" + _tickCount + "  deadScope=" + _staleScopeFrames);
            GUILayout.Label("source=" + _launchSource + "  menuPending=" +
                (PfeNetLaunchData.HasPending ? "yes" : "no"));
            // Which of the four room lookups is live on THIS machine. "room=none source=none" is
            // the single line that would have made both failed play-tests diagnosable in one glance.
            GUILayout.Label("room=" + _lastRoomId + "  source=" + _lastRoomSource);
            // The F-12 readbacks: a wire that is green while projUnits=0 or localProj=0 is exactly
            // the boot-room / identity failure that three play-tests could not localise from the
            // transport counters alone.
            GUILayout.Label("localPlayer=" + _localSlot + "  stamps=" + _identityStamps +
                "  rebinds=" + _roomRebinds + "  bootAct=" + _bootRoomActivations);
            GUILayout.Label("projUnits=" + (_projector != null ? _projector.ProjectedUnitCount : 0) +
                "  projPlayers=" + (_projector != null ? _projector.ProjectedPlayerUnitCount : 0) +
                "  localProj=" + _projectedLocalPlayers + "  roomSkip=" + _skippedForeignRooms);
            // Zero in every projection counter is not a diagnosis. These six readbacks separate
            // "producer never ticked", "producer cached a null streaming manager", "no active room",
            // "no unit carried an id" and "units could not be attributed to a room".
            GUILayout.Label("projTick=" + (_projector != null ? _projector.ProjectionCount : 0) +
                "  rooms=" + (_projector != null ? _projector.ActiveRoomCount : 0) +
                "  stream=" + (_projector != null && _projector.HasStreaming ? "yes" : "no") +
                "  live=" + UnitViewRegistry.Count +
                "  id=" + (_projector != null ? _projector.IdentifiedUnitCount : 0) +
                "  unattr=" + (_projector != null ? _projector.UnattributedUnitCount : 0) +
                "  fault=" + CurrentProjectionFault());
            // The projectile readbacks, one row: play-test #7's missing client rounds. captured names
            // what left the host; skippedNoId names rounds the registry walk dropped as anonymous (a
            // hash-0 record is malformed on arrival, so one is a round no client can ever draw);
            // skippedNoRoom names rounds no active room claimed. Without these, "host says shots=3,
            // client sees nothing" is undiagnosable from the panel.
            GUILayout.Label("projCaptured=" + (_projector != null ? _projector.ProjectedProjectileCount : 0) +
                "  projSkippedNoId=" + (_projector != null ? _projector.SkippedNoIdProjectileCount : 0) +
                "  projSkippedNoRoom=" + (_projector != null ? _projector.UnattributedProjectileCount : 0));

            if (_wire != null)
                GUILayout.Label("wire sent=" + _wire.SentCount + "  undelivered=" + _wire.UndeliveredCount +
                    "  last=" + (_wire.LastUndeliveredKind ?? "-"));

            if (_config.IsHost && _host != null)
            {
                GUILayout.Label("peers=" + _host.JoinedPeerCount + "  bound=" + _boundPeers +
                    "  snapshots=" + _host.SentSnapshotCount +
                    "  chunks=" + _host.SentBaselineChunkCount);
                GUILayout.Label("acks=" + _host.AppliedAckCount + "  journal=" + _host.JournalCount +
                    "  refused=" + _host.RejectedEnvelopeCount);
                GUILayout.Label("commands accepted=" + _host.AcceptedCommandBatchCount +
                    "  refused=" + _host.RejectedCommandBatchCount);
                GUILayout.Label("sink batches=" + _sink.AppliedBatches +
                    "  commands=" + _sink.AppliedCommands +
                    "  slots=" + _sink.KnownSlotCount);
                // The loot-sync readbacks: a peer press the host declined is visible HERE, with the
                // first broken link named, instead of reading as "the client pressed and nothing
                // happened".
                GUILayout.Label("interacts applied=" + (_interactSink != null ? _interactSink.AppliedOpens : 0) +
                    "  refused=" + (_interactSink != null ? _interactSink.RefusedOpens : 0));
                // The door-sync readbacks: a peer's door press the host declined is visible HERE, with
                // the first broken link named, instead of reading as "the client pressed E and the
                // door ignored her".
                GUILayout.Label("doors applied=" + (_interactSink != null ? _interactSink.AppliedDoorToggles : 0) +
                    "  refused=" + (_interactSink != null ? _interactSink.RefusedDoorToggles : 0));
                GUILayout.Label("interactRefusal: " +
                    (_interactSink != null ? _interactSink.LastRefusal : "no sink"));
            }
            else if (_client != null)
            {
                GUILayout.Label("joined=" + _client.IsJoined + "  peer=" + _client.PeerId +
                    "  session=" + _client.SessionId);
                GUILayout.Label("snapshots=" + _client.Receiver.AcceptedSnapshotCount +
                    "  acks=" + _client.AcksSent + "  chunks=" + _client.BaselineChunksReceived +
                    "  baselines=" + _client.AssembledBaselineCount);
                GUILayout.Label("command batches=" + _client.CommandBatchesSent +
                    "  commands=" + _client.CommandsSent +
                    "  refused=" + _client.RejectedCommandSendCount);
                GUILayout.Label("tiles=" + _applier.AppliedCells +
                    "  rebuilds=" + _applier.RebuildCount +
                    "  unknownRoom=" + _applier.SkippedUnknownRoom +
                    "  rooms=" + _applier.KnownRoomCount +
                    "  badHash=0x" + _applier.LastUnknownRoomHash.ToString("X16"));
                // The loot-sync readbacks: the client's half of "containers drop loot for both".
                // spawned stays 0 after a host-opened chest means the drop set never landed here;
                // unknownContent > 0 means the two builds carry different item tables.
                GUILayout.Label("drops spawned=" + _applier.SpawnedReplicatedDrops +
                    "  removed=" + _applier.RemovedReplicatedDrops +
                    "  updated=" + _applier.UpdatedReplicatedDrops +
                    "  unknownContent=" + _applier.SkippedUnknownDropContent +
                    "  badSet=" + _applier.RefusedMalformedDropSets);
                // The door-sync readbacks: a host-toggled door must move applied on every peer in the
                // room; noPresenter>0 means the state landed on the record but the visuals were not
                // built here (a presentation lag the next rebuild clears).
                GUILayout.Label("doorStates applied=" + _applier.AppliedDoorStates +
                    "  noPresenter=" + _applier.SkippedDoorNoPresenter +
                    "  unknownObject=" + _applier.SkippedUnknownObject);
                GUILayout.Label("rejects env=" + _client.RejectedEnvelopeCount +
                    "  snap=" + _client.RejectedSnapshotCount +
                    "  join=" + _client.LastJoinRejectReason);
            }

            // A body that was never built, never driven, or never placed must be visible here.
            // Wire/host/client counters alone say "nothing wrong" for all three.
            GUILayout.Label("bodies built=" + _bodiesBuilt + "  failed=" + _bodyFailures +
                "  removed=" + _bodiesRemoved + "  live=" + _bodies.Count +
                "  indexed=" + _bodyBySlot.Count);
            for (int i = 0; i < _bodies.Count; i++)
            {
                PfeRemoteBody body = _bodies[i];
                if (body == null) continue;
                GUILayout.Label("  slot " + body.Slot + (body.IsSimulated ? " sim" : " view") +
                    " cmd=" + body.AppliedCommands + " state=" + body.AppliedStates +
                    " anim=" + body.AnimStatesApplied +
                    (body.WeaponView != null
                        ? " wpn=" + body.WeaponView.CurrentHash.ToString("X4") +
                          " sprites=" + body.WeaponView.SpriteChanges
                        : "") +
                    (body.IsSimulated
                        ? " mount=" + (body.IsWeaponArmed ? "armed" : "none") +
                          " trig=" + (body.WeaponTriggerHeld ? "1" : "0") +
                          " shots=" + body.WeaponShotsFired
                        : "") +
                    " stale=" + body.SkippedStaleCommands +
                    " foreign=" + body.SkippedForeignStates);
            }
            GUILayout.Label("bodyError: " + _lastBodyError);

            // The slice-3 readbacks: a firefight in view must move spawned/updated on the client,
            // and a round that impacted on the host must move destroyed. stale>0 is expected under
            // lossy delivery (out-of-order snapshots); live>0 long after the fight ended is a ghost.
            if (_projectiles != null)
            {
                GUILayout.Label("projView spawned=" + _projectiles.Spawned +
                    "  updated=" + _projectiles.Updated +
                    "  destroyed=" + _projectiles.Destroyed +
                    "  live=" + _projectiles.LiveCount +
                    "  stale=" + _projectiles.RejectedStale +
                    "  skipRoom=" + _projectiles.SkippedForeignRoom +
                    "  skipRec=" + _projectiles.SkippedUndisplayable +
                    "  noDef=" + _projectiles.UnresolvedDefinitions);
            }

            // The room-transition readbacks: sent>0 with applied=0 on the host names the broken
            // link (receive, admission or room lookup) instead of "the peer moved and nothing
            // followed". refused>0 means the Wave 2B bounds gate turned a declaration away — the
            // named room was unknown or the arrival position sat outside that room's bounds, which
            // is a world mismatch or a corrupted packet, not a lost one. transitionError names the
            // exact reason and the offending coordinates.
            GUILayout.Label("roomTx sent=" + _roomTransitionsSent +
                "  applied=" + _roomTransitionsApplied +
                "  relocated=" + _roomRelocations +
                "  refused=" + _roomTransitionRefused +
                "  unknownRoom=" + _roomTransitionUnknownRooms);
            GUILayout.Label("transitionError: " + _lastTransitionError);

            // The contract is the only thing that can say "these two processes are in different
            // worlds". Wire and host counters stay healthy in exactly that case.
            GUILayout.Label("world=" + PfeWorldContractRules.Describe(_worldVerdict) +
                "  localRooms=" + RoomCount(_localContract) +
                "  hostRooms=" + RoomCount(_hostContract));
            GUILayout.Label("world contract rx=" + _worldContractsReceived +
                "  tx=" + _worldContractsSent + "  bad=" + _worldContractErrors +
                "  blocked=" + _worldAppliesSkipped);
            GUILayout.Label("worldReason: " + _worldReason);

            GUILayout.Label("decodeFail=" + _decodeFailures + "  recvFail=" + _receiveFailures +
                "  recvRefused=" + _receiveRefusals);
            GUILayout.Label("error: " + _lastError);
            GUILayout.Label("args: -pfe-net-host | -pfe-net-client [-pfe-net-address A] " +
                "[-pfe-net-port P] [-pfe-net-nonce N]");
            GUILayout.EndArea();
        }
    }
}
