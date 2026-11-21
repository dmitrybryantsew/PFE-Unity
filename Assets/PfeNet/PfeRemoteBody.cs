using System;
using PFE.Character;
using PFE.Character.Animation;
using PFE.Core;
using PFE.Core.Input;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Sim.State;
using PFE.Systems.Combat;
using PFE.Systems.Effects;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Particles.Adapters;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;
using UnityEngine;
// UnityEngine also declares an EntityId; the Sim one is the identity that crosses the wire.
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Net
{
    /// <summary>Everything a remote body needs handed to it. No service is resolved implicitly:
    /// <c>AddComponent</c> is never observed by VContainer, so <c>[Inject]</c> would never reach a
    /// runtime-built entity.</summary>
    public sealed class PfeRemoteBodyContext
    {
        public byte Slot;
        public bool Simulated;
        public RoomInstance Room;
        public UnitDefinition Definition;
        public SimClock Clock;
        public SimLoop Loop;
        public DamageSystem DamageSystem;
        public IEffectDefinitionResolver EffectResolver;
        public RoomParticleEmitter ParticleEmitter;
        public CharacterSpriteAssembler VisualSource;

        /// <summary>The local player's unit definition — the body box (<c>Width</c>/<c>Height</c>)
        /// the remote weapon view's hold-point rule reads. Set for both body shapes (the rig the
        /// visual is copied from is the local player); null degrades the view to the shipped
        /// player's 0.5×0.7 default.</summary>
        public UnitDefinition OwnerDefinition;
        public ILocomotionAbilities AbilitySource;
        public Vector2 SpawnPixel;

        /// <summary>The local player's <c>CharacterAnimationDriver</c>, read for its serialized frame
        /// mappings (stay-aim sub-range, jump velocity mapping). A simulated peer body has no prefab
        /// to configure them, and without the copy the host's view of the peer — and the state/frame
        /// it captures for the clients — would drop the aim tilt and fall pose the local rig draws.</summary>
        public CharacterAnimationDriver AnimationSource;

        /// <summary>The local rig's held-weapon renderer — the template for the remote body's weapon
        /// child: its sprite art vocabulary, sorting layer and order are copied. Its local position
        /// is NOT: the local presenter overwrites that transform every frame, so a copy is always
        /// the origin; the remote view derives the hold point per frame instead. Null means the rig
        /// has no weapon child and the remote body draws no weapon.</summary>
        public SpriteRenderer WeaponVisualSource;

        /// <summary>The container's projectile factory — arms the host-side weapon mount. Null leaves
        /// the body unarmed (the mount says so once), never throws inside a spawn loop.</summary>
        public PFE.Systems.Combat.IProjectileFactory ProjectileFactory;

        /// <summary>Optional services handed to the weapon mount: resolver (spawner init), sound (a
        /// peer's shot is heard by the host's AI), RNG (spread rolls), debug settings. Each may be
        /// null; the mount degrades rather than refuses.</summary>
        public VContainer.IObjectResolver Resolver;
        public PFE.Systems.Audio.ISoundService Sound;
        public PFE.Core.Rng.IRngService Rng;
        public PfeDebugSettings Debug;
    }

    /// <summary>
    /// A body for a remote peer, built from code (F-12). Two shapes, because the authority model
    /// makes them different things:
    ///
    /// <list type="bullet">
    /// <item><b>Host (<see cref="Simulated"/>):</b> a real <see cref="UnitController"/> with a
    /// <see cref="TilePhysicsController"/> motor and a <see cref="PlayerLocomotionController"/>,
    /// driven by the peer's admitted commands. It is registered by <c>UnitController.OnEnable</c>, so
    /// <c>SimProjectileSnapshotProjector</c> projects it with no extra hand-over — the body appears in
    /// <c>SimRoomDelta.Units</c> because it is a live unit, not because anything special-cases it.</item>
    /// <item><b>Client (<c>Simulated == false</c>):</b> a display-only ghost — a GameObject wearing a
    /// <c>CharacterSpriteAssembler</c> pony rig fed from the local player's own definition, style and
    /// appearance, no physics and no <see cref="UnitController"/>. This is deliberate. A client-side
    /// <c>UnitController</c> would run its own step and integrate its own velocity against
    /// the position the host just replicated, so the two writers would fight and the body would jitter
    /// by an amount that looks like packet loss. Authority is the host's; the client only draws.</item>
    /// </list>
    ///
    /// <para><b>Built disabled, then activated.</b> Every <c>Awake</c> in this repo resolves its
    /// siblings with one non-retrying <c>GetComponent</c>, so a component added after its dependency
    /// on a live object holds null forever. Building disabled reproduces a prefab's ordering (all
    /// components exist before any <c>Awake</c> runs) and makes the add order stop mattering.</para>
    ///
    /// <para><b>What is deliberately not handed over.</b> <c>SetObjectPhysicsLayer</c> is skipped: the
    /// body cannot stand on a crate, which is a limitation of a proxy avatar rather than a crash. The
    /// player-only rig components (weapon loadout, interactor, camera, input) are not built at all —
    /// a remote body must never read the local player's input.</para>
    /// </summary>
    public sealed class PfeRemoteBody
    {
        private readonly bool _simulated;
        private readonly Transform _root;
        private readonly TilePhysicsController _motor;
        private readonly CharacterSpriteAssembler _assembler;
        private readonly PfeRemoteWeaponView _weaponView;
        private readonly PeerAvatarViewSource _avatarSource;
        private readonly PFE.Systems.Weapons.EnemyWeaponMount _weaponMount;
        private readonly PFE.Systems.Combat.IProjectileFactory _projectileFactory;
        private readonly VContainer.IObjectResolver _resolver;
        private readonly PFE.Systems.Audio.ISoundService _sound;
        private readonly PFE.Core.Rng.IRngService _rng;
        private readonly PfeDebugSettings _debug;
        private uint _lastCommandSequence;
        private ulong _mountedWeaponHash;
        private bool _mountArmWarned;
        private EntityId _id;

        // Display-only interpolation buffer (F-12 Wave 2A): the newest usable snapshot is the
        // target, the position the viewer last saw is the origin, and TickVisual eases between
        // them every render frame. Simulated host bodies never touch these — their motor owns
        // the transform.
        private bool _hasInterpTarget;
        private float _prevPx, _prevPy;
        private float _targetPx, _targetPy;
        private float _interpElapsed;

        private PfeRemoteBody(byte slot, EntityId id, Transform root, bool simulated,
            UnitController unit, PlayerLocomotionController locomotion, TilePhysicsController motor,
            CharacterSpriteAssembler assembler, PfeRemoteWeaponView weaponView,
            PeerAvatarViewSource avatarSource, PFE.Systems.Weapons.EnemyWeaponMount weaponMount,
            PFE.Systems.Combat.IProjectileFactory projectileFactory,
            VContainer.IObjectResolver resolver,
            PFE.Systems.Audio.ISoundService sound,
            PFE.Core.Rng.IRngService rng,
            PfeDebugSettings debug)
        {
            Slot = slot;
            _id = id;
            _root = root;
            _simulated = simulated;
            Unit = unit;
            Locomotion = locomotion;
            _motor = motor;
            _assembler = assembler;
            _weaponView = weaponView;
            _avatarSource = avatarSource;
            _weaponMount = weaponMount;
            _projectileFactory = projectileFactory;
            _resolver = resolver;
            _sound = sound;
            _rng = rng;
            _debug = debug;
        }

        public byte Slot { get; }
        public UnitController Unit { get; }
        public PlayerLocomotionController Locomotion { get; }
        public ulong BodyHash => _id.Hash;
        public bool IsSimulated => _simulated;

        /// <summary>The room the simulated body currently resolves tiles against — the host-side
        /// answer to "which room is this peer in", read by the session's per-tick room-presence
        /// pass. Null for a display body: it has no motor and no room of its own, it only draws
        /// where the host's snapshot says.</summary>
        public RoomInstance Room => _simulated && _motor != null ? _motor.CurrentRoom : null;

        public int AppliedCommands { get; private set; }
        public int AppliedStates { get; private set; }
        public int SkippedStaleCommands { get; private set; }
        public int SkippedForeignStates { get; private set; }

        /// <summary>The slice-4 fire readbacks, straight off the host-side mount: armed at all, the
        /// trigger as last admitted, and how many shot batches the mount has produced. "The peer
        /// fired and nothing came out" splits at these three: unarmed (the carried hash never
        /// resolved), armed-but-never-firing (FireHeld never reached the mount), firing (the
        /// spawner or the snapshot path is the broken link).</summary>
        public bool IsWeaponArmed => _weaponMount != null && _weaponMount.IsArmed;
        public bool WeaponTriggerHeld => _weaponMount != null && _weaponMount.IsFiring;
        public int WeaponShotsFired => _weaponMount != null ? _weaponMount.ShotsFired : 0;

        /// <summary>How many snapshot states carried a usable animation state for this body. The
        /// client-side readback for "animation state reached the wire": a remote player running
        /// around must move this every snapshot.</summary>
        public int AnimStatesApplied { get; private set; }

        /// <summary>The held-weapon view built for this body, or null when the rig template was
        /// missing. Exposed for the session overlay's weapon readback (hash + sprite changes).</summary>
        public PfeRemoteWeaponView WeaponView => _weaponView;

        /// <summary>Build one body. Returns null and a reason rather than a half-built object: a body
        /// that exists but was never handed a seam is worse than no body, because it looks alive.</summary>
        public static PfeRemoteBody TryCreate(in PfeRemoteBodyContext ctx, out string error)
        {
            error = null;

            if (!PfeRemoteBodyRules.TryBodyId(ctx.Slot, out EntityId id))
            {
                error = "slot " + ctx.Slot + " is not a peer slot";
                return null;
            }

            if (ctx.Room == null)
            {
                error = "no room";
                return null;
            }

            GameObject go = null;
            try
            {
                go = new GameObject(ctx.Simulated ? "PfeRemotePlayer" : "PfeRemotePlayerView");
                go.SetActive(false);
                go.transform.position = WorldCoordinates.PixelToUnity(ctx.SpawnPixel);

                AttachVisual(go, ctx);
                CharacterSpriteAssembler assembler = go.GetComponent<CharacterSpriteAssembler>();

                UnitController unit = null;
                PlayerLocomotionController locomotion = null;
                TilePhysicsController motor = null;
                PeerAvatarViewSource avatarSource = null;
                EnemyWeaponMount weaponMount = null;

                // The held-weapon visual is built for BOTH body shapes: the client ghost needs it to
                // draw the peer's gun, and the simulated host body needs it so the HOST sees the same
                // gun on the peer it is simulating. Display-only — see PfeRemoteWeaponView.
                PfeRemoteWeaponView weaponView = BuildWeaponView(go, ctx);

                if (ctx.Simulated)
                {
                    if (ctx.Definition == null)
                    {
                        error = "no unit definition for a simulated body";
                        return null;
                    }

                    // Motor first is the documented order for an ACTIVE build; the disabled build
                    // makes it irrelevant, and keeping it costs nothing if that ever changes.
                    motor = go.AddComponent<TilePhysicsController>();
                    motor.SetRoom(ctx.Room);
                    if (ctx.Clock != null && ctx.Loop != null) motor.AttachSimulation(ctx.Clock, ctx.Loop);

                    unit = go.AddComponent<UnitController>();
                    unit.Initialize(ctx.Definition, new UnitStats());
                    unit.SetTileQuery(new UnifiedTileQueryService(ctx.Room));
                    unit.SetDamageSystem(ctx.DamageSystem);
                    unit.SetEffectResolver(ctx.EffectResolver);
                    unit.SetParticleEmitter(ctx.ParticleEmitter);
                    if (ctx.Clock != null && ctx.Loop != null) unit.AttachSimulation(ctx.Clock, ctx.Loop);
                    unit.AssignEntityId(id, PfeRemoteBodyRules.BodyLifetime(ctx.Slot));

                    // A remote player's body is a PLAYER, not the definition's faction. The shipped
                    // player definition carries fraction=2 (Raider) because the real player overrides
                    // Faction => FactionType.Player in code (AS3 UnitPlayer.as:385 sets F_PLAYER); a
                    // plain UnitController initialized from the definition resolves Raider instead, and
                    // every turret, zombie and raider in the room opens fire on the connected peer.
                    // SetRuntimeFaction writes the instance layer, never the shared ScriptableObject.
                    unit.SetRuntimeFaction(FactionType.Player);

                    // Abilities with the mana gates removed (see RemoteBodyAbilities). Without this
                    // the body can never levitate, double-jump or teleport — no ILocomotionAbilities
                    // resolves on it — and the host simulation diverges from the client the moment
                    // the peer leaves the ground.
                    var abilities = go.AddComponent<RemoteBodyAbilities>();
                    abilities.Source = ctx.AbilitySource;

                    locomotion = go.AddComponent<PlayerLocomotionController>();
                    // The swim impulse is a per-sim-tick pulse; register it on the sim clock exactly
                    // like PlayerController does for the local player, or swimming integrates at the
                    // FixedUpdate cadence and the body drifts from the client's own simulation.
                    if (ctx.Clock != null && ctx.Loop != null) locomotion.AttachSimulation(ctx.Clock, ctx.Loop);

                    // The animation FSM the local rig runs. It resolves the assembler and the
                    // locomotion by GetComponent in Awake (deferred to SetActive below), so it must be
                    // added after both. The frame mappings are copied from the local rig — without
                    // them the host captures "stay" frame 0 for a peer aiming up, and every client
                    // draws the remote player un-aimed.
                    var driver = go.AddComponent<CharacterAnimationDriver>();
                    driver.CopyFrameMappingsFrom(ctx.AnimationSource);

                    // The capture seam: stamps aim (from the locomotion the commands drive), weapon
                    // (from the last admitted command) and animation (from the assembler this driver
                    // is playing) into the unit record the projector emits for this body.
                    avatarSource = new PeerAvatarViewSource(locomotion, assembler);
                    unit.SetAvatarViewSource(avatarSource);

                    // The host-side weapon mount: the reusable NPC mount, re-armed from the weapon
                    // hash each admitted command carries. Its owner resolves to this body's
                    // UnitController (GetComponentInParent), so a peer's round carries the PLAYER
                    // faction and the peer's identity — the host's AI hears the shot and takes the
                    // hit exactly as if the body had fired locally. Ammo and mana stay untracked
                    // (the mount's npcOwner mode), the same documented gap as remote container
                    // perks: the peer's inventory is client-side truth.
                    weaponMount = go.AddComponent<EnemyWeaponMount>();
                }

                go.SetActive(true);
                return new PfeRemoteBody(ctx.Slot, id, go.transform, ctx.Simulated, unit, locomotion,
                    motor, assembler, weaponView, avatarSource, weaponMount,
                    ctx.ProjectileFactory, ctx.Resolver, ctx.Sound, ctx.Rng, ctx.Debug);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                if (go != null) UnityEngine.Object.Destroy(go);
                return null;
            }
        }

        /// <summary>Build the remote avatar's body from the local player's own character rig. The pony
        /// is a multi-part <see cref="CharacterSpriteAssembler"/> (body/mane/tail/eyes slots) living on
        /// the player root — the prefab's ONLY static SpriteRenderer is the rifle under the Weapon
        /// child, so the previous "copy the first SpriteRenderer" build drew a floating gun as the
        /// remote player's entire body. Setting a real assembler up from the same definition, style and
        /// appearance draws the same pony both shapes of body need, and because the assembler hangs its
        /// slots under the body root, the host body's own facing mirror (<c>UnitController</c> writes
        /// <c>localScale.x = facing</c> on the root) flips the rig with no extra code here.</summary>
        private static void AttachVisual(GameObject go, in PfeRemoteBodyContext ctx)
        {
            CharacterSpriteAssembler source = ctx.VisualSource;
            if (source == null || source.Definition == null) return;

            var assembler = go.AddComponent<CharacterSpriteAssembler>();
            assembler.SortingLayerName = source.SortingLayerName;
            assembler.Setup(source.Definition, source.StyleData,
                source.Appearance ?? CharacterAppearance.CreateDefault());
        }

        /// <summary>
        /// Build the held-weapon child from the local rig's weapon renderer as the template — the
        /// same hand-over idiom as <see cref="AttachVisual"/>: sorting and art come from the rig,
        /// while the POSITION is deliberately NOT copied — the local presenter's per-frame hold
        /// derivation owns the transform on the rig, so the child's static localPosition is just
        /// the origin, and copying it parks the remote gun at the pony's feet. The view re-derives
        /// the hold point per frame instead (see <see cref="PfeRemoteWeaponView"/>), from the body
        /// box of <paramref name="ownerStats"/> and the replicated aim angle.
        /// </summary>
        /// <returns>The view, or null (no weapon drawn) when the rig has no weapon child.</returns>
        private static PfeRemoteWeaponView BuildWeaponView(GameObject go, in PfeRemoteBodyContext ctx)
        {
            SpriteRenderer source = ctx.WeaponVisualSource;
            if (source == null) return null;

            var weaponGo = new GameObject("Weapon");
            weaponGo.transform.SetParent(go.transform, false);
            weaponGo.transform.localScale = source.transform.localScale;

            var renderer = weaponGo.AddComponent<SpriteRenderer>();
            renderer.sortingLayerName = source.sortingLayerName;
            renderer.sortingOrder = source.sortingOrder;
            renderer.enabled = false;

            // The renderer exists before the component that drives it, so Init is a hand-over, never
            // a search — the AddComponent ordering trap this class was built to avoid.
            var view = weaponGo.AddComponent<PfeRemoteWeaponView>();
            view.Init(renderer);

            // The body box the hold rule reads — the local player's own definition, for both body
            // shapes (see PfeRemoteBodyContext.OwnerDefinition).
            if (ctx.OwnerDefinition != null)
                view.SetBodySize(ctx.OwnerDefinition.Width, ctx.OwnerDefinition.Height);
            return view;
        }

        /// <summary>
        /// Host: drive the body with one admitted command. Each command is applied exactly once — the
        /// one-shot edges (<c>JumpPressed</c>, <c>DashPressed</c>) live on the command, so re-applying a
        /// held command every tick would re-fire jump and dash forever.
        /// </summary>
        public void Drive(in PlayerCommand command)
        {
            if (!_simulated || Locomotion == null) return;

            if (!PfeRemoteBodyRules.IsNewerCommand(command.Sequence, _lastCommandSequence))
            {
                SkippedStaleCommands++;
                return;
            }

            _lastCommandSequence = command.Sequence;
            Locomotion.SetIntent(PfeRemoteBodyRules.ToBodyIntent(command, Slot));

            // Teleport is hold-to-charge, release-to-execute at the cursor — a level and a point the
            // plain SetIntent(command) path does not carry. Forward both explicitly so the host body
            // charges toward the same cell the client aimed at instead of standing still while the
            // peer blinks across the room.
            Locomotion.SetTeleportHeld(command.TeleportHeld);
            Locomotion.SetCursorWorldPixels(new Vector2(command.CursorWorldX, command.CursorWorldY));

            // The held weapon rides the command; the aim the locomotion already tracks from it. Both
            // feed the capture seam (for the clients) and the host's own weapon view of this body.
            if (_avatarSource != null) _avatarSource.SetWeaponIdHash(command.WeaponIdHash);
            if (_weaponView != null)
            {
                _weaponView.SetWeapon(command.WeaponIdHash);
                _weaponView.SetAim(Locomotion.CurrentSnapshot.AimAngle);
            }

            DriveWeapon(in command);
            AppliedCommands++;
        }

        /// <summary>
        /// Host: keep the weapon mount in step with one admitted command — re-arm when the carried
        /// weapon hash changed, aim along the command's aim angle, hold or release the trigger.
        /// </summary>
        /// <para><b>Why the mount and not a bespoke spawner.</b> The mount is the reusable NPC weapon
        /// stack — controller, spawner, spread rolls, sound announcement, beam upkeep. Its owner
        /// resolves to this body's UnitController, so a peer's round carries the PLAYER faction and
        /// the peer's identity; the host's AI hears the shot and can be hit by it. What the host
        /// cannot do honestly is spend the peer's ammo (it lives on the client's machine): the
        /// mount's npcOwner mode leaves ammo untracked, the same documented gap as remote container
        /// perks.</para>
        private void DriveWeapon(in PlayerCommand command)
        {
            if (_weaponMount == null) return;

            if (command.WeaponIdHash != _mountedWeaponHash)
            {
                _mountedWeaponHash = command.WeaponIdHash;

                // Punch/kick and bare hooves stay unarmed: Initialize(null) leaves the mount
                // unarmed and says nothing (the unarmed case is not a failure).
                WeaponDefinition def = command.WeaponIdHash == 0UL
                    ? null
                    : PfeRemoteWeaponCatalog.Resolve(command.WeaponIdHash);
                bool armable = def != null && !def.IsUnarmed && _projectileFactory != null;
                _weaponMount.Initialize(
                    armable ? def : null,
                    FactionType.Player,
                    armable ? _projectileFactory : null,
                    _resolver, _sound, _debug, _rng);
                _weaponMount.SetHeldVisualVisible(false);

                // The play-test readback: "the peer fired and nothing came out" is invisible without
                // a record of whether the mount ever armed. Log the arm, and log a non-zero hash
                // that failed to arm ONCE, naming the exact broken link — the alternative is a
                // silent unarmed body that every counter reports as healthy.
                if (_weaponMount.IsArmed)
                {
                    Debug.Log($"[PfeRemoteBody] slot {Slot} armed '{def.weaponId}' " +
                              $"(hash 0x{command.WeaponIdHash:X16}); FireHeld will drive the mount");
                }
                else if (command.WeaponIdHash != 0UL && !_mountArmWarned)
                {
                    _mountArmWarned = true;
                    string reason = def == null
                        ? "catalog miss — the hash is not in PfeRemoteWeaponCatalog (id form mismatch?)"
                        : def.IsUnarmed
                            ? "the definition is unarmed"
                            : "no IProjectileFactory in the body context";
                    Debug.LogWarning($"[PfeRemoteBody] slot {Slot} carried weapon hash " +
                                     $"0x{command.WeaponIdHash:X16} but the mount stayed UNARMED: {reason}. " +
                                     "The peer's FireHeld will do nothing.");
                }
            }

            if (!_weaponMount.IsArmed) return;

            // The mount wants a target point, not an angle: push the body position out along the
            // aim ray. The distance is arbitrary (10 units) — only the direction is read.
            float aimRad = Locomotion != null
                ? Locomotion.CurrentSnapshot.AimAngle * Mathf.Deg2Rad
                : command.AimAngle * Mathf.Deg2Rad;
            Vector3 origin = _root != null ? _root.position : Vector3.zero;
            Vector3 target = origin +
                new Vector3(Mathf.Cos(aimRad), Mathf.Sin(aimRad), 0f) * 10f;
            _weaponMount.SetAimTarget(target);
            _weaponMount.SetFiring(command.FireHeld);
        }

        /// <summary>
        /// Host: advance the weapon mount one fixed sim tick — cooldowns, automatic pacing and beam
        /// upkeep. Called EVERY tick from the session, not only when a fresh command arrived:
        /// <see cref="Drive"/> refuses stale sequences, but a held trigger and a cooling barrel do
        /// not freeze between deliveries.
        /// </summary>
        public void TickWeapons()
        {
            if (!_simulated || _weaponMount == null || !_weaponMount.IsArmed) return;
            _weaponMount.Tick(1f / SimClock.FramesPerSecond);
        }

        /// <summary>
        /// Host: move the simulated body into another room, arriving at <paramref name="worldPos"/>.
        /// This is the receiving half of the peer's <c>PeerRoomTransition</c> declaration — the same
        /// hand-off <c>RoomTransitionManager</c> performs on a local player (<c>SetRoom</c> to
        /// refresh the collision context, then re-sync the authoritative pixel state and clear
        /// velocity), plus the tile-query rebind that keeps the projector attributing the body to
        /// the room it now stands in. A no-op on a display body: a client-side motor would integrate
        /// against the position the host just replicated, and the two writers would fight.
        /// </summary>
        public void Relocate(RoomInstance room, Vector3 worldPos)
        {
            if (!_simulated || room == null || _motor == null || Unit == null) return;

            _motor.RepositionForRoom(room, worldPos);
            Unit.SetTileQuery(new UnifiedTileQueryService(room));
        }

        /// <summary>
        /// Client: feed the ghost one host snapshot. The position does NOT go to the transform
        /// here — it re-aims the interpolation buffer (<see cref="PushInterpTarget"/>), and the
        /// per-render-frame <see cref="TickVisual"/> owns the single transform write. Samples
        /// arrive on the network's clock; transform writes happen on the display's clock.
        /// </summary>
        public void ApplyState(in SimUnitState state)
        {
            if (_simulated) return;

            if (!PfeRemoteBodyRules.IsBodyRecord(BodyHash, state))
            {
                SkippedForeignStates++;
                return;
            }

            if (!PfeRemoteBodyRules.IsUsablePosition(state.Px, state.Py)) return;

            PushInterpTarget(state.Px, state.Py);

            // Mirror for the host's facing — UnitController.FacingDirection on the wire. The simulated
            // host body does not need this (its own controller mirrors the root, assembler included);
            // a view body has no controller, so without the write the ghost pony faces one way forever.
            int facing = state.FacingDirection < 0 ? -1 : 1;
            if (_root.localScale.x != (float)facing)
            {
                _root.localScale = new Vector3(facing, 1f, 1f);
            }

            ApplyAvatarState(in state);
            SetRemoteVisible(true);
            AppliedStates++;
        }

        /// <summary>
        /// Client: re-aim the display interpolation at the newest host sample. A teleport-sized
        /// displacement (room transition, respawn) snaps — both anchors land on the sample, so the
        /// ghost is exactly where the host put it and never smears across the screen. Anything
        /// shorter opens a new window that starts from the position the viewer actually sees, not
        /// from the old target the viewer never reached, so a late snapshot eases instead of
        /// jumping.
        /// </summary>
        private void PushInterpTarget(float px, float py)
        {
            if (_hasInterpTarget &&
                !PfeRemoteInterpolationRules.ShouldSnap(true, _prevPx, _prevPy, px, py))
            {
                PfeRemoteInterpolationRules.Sample(
                    _prevPx, _prevPy, _targetPx, _targetPy,
                    _interpElapsed, PfeRemoteInterpolationRules.DefaultWindowSeconds,
                    out _prevPx, out _prevPy);
            }
            else
            {
                _prevPx = px;
                _prevPy = py;
            }

            _targetPx = px;
            _targetPy = py;
            _interpElapsed = 0f;
            _hasInterpTarget = true;
        }

        /// <summary>
        /// Client: advance the display-only body one render frame and write the transform. The
        /// session calls this from its own Update — this class is not a MonoBehaviour and has no
        /// frame callback of its own. Simulated host bodies never take this path: they are
        /// authority, and their motor writes the transform every sim tick.
        /// </summary>
        public void TickVisual(float dt)
        {
            if (_simulated || !_hasInterpTarget || _root == null) return;

            _interpElapsed += dt;
            PfeRemoteInterpolationRules.Sample(
                _prevPx, _prevPy, _targetPx, _targetPy,
                _interpElapsed, PfeRemoteInterpolationRules.DefaultWindowSeconds,
                out float px, out float py);
            _root.position = WorldCoordinates.PixelToUnity(new Vector2(px, py));
        }

        /// <summary>
        /// Client: draw the peer's avatar extras — the animation state/frame, the held weapon and the
        /// aim angle — from the unit record. Animation applies only when the host named a state this
        /// build knows (<see cref="RemoteAnimStateIds.Unknown"/> keeps the current state, it does not
        /// guess); <c>SetState</c> additionally no-ops on a clip the local definition does not carry,
        /// so a missing clip is a frozen pose, never a crash. The weapon view skips redundant hash
        /// writes, and its transform work runs on its own LateUpdate.
        /// </summary>
        private void ApplyAvatarState(in SimUnitState state)
        {
            if (_assembler != null && state.PoseId != RemoteAnimStateIds.Unknown)
            {
                string animName = RemoteAnimStateIds.ToName(state.PoseId);
                if (animName != null &&
                    (_assembler.CurrentState != animName || _assembler.CurrentFrame != state.PoseFrame))
                {
                    _assembler.SetState(animName, state.PoseFrame);
                }
                AnimStatesApplied++;
            }

            if (_weaponView != null)
            {
                _weaponView.SetWeapon(state.WeaponIdHash);
                _weaponView.SetAim(state.AimAngle);
            }
        }

        /// <summary>
        /// Show or hide a display-only body. The receiver hides every display body at the start of a
        /// snapshot and only a matching record in the receiver's own room turns one back on. Without
        /// this, a host avatar that walked into another room leaves a statue at its last position in
        /// the room the camera still follows — a ghost that looks like a desync but is a stale draw.
        ///
        /// <para>Simulated host bodies are never touched: they are authority, not a projection, and
        /// deactivating one would stop its locomotion mid-command.</para>
        /// </summary>
        public void SetRemoteVisible(bool visible)
        {
            if (_simulated || _root == null) return;

            GameObject root = _root.gameObject;
            if (root.activeSelf == visible) return;
            root.SetActive(visible);
        }

        /// <summary>Tear the body down. A destroyed body that stays in <c>UnitViewRegistry</c> would be
        /// projected as an anonymous unit, which is the hash-0 case the projector already skips.</summary>
        public void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }

        /// <summary>
        /// The <see cref="IUnitAvatarViewSource"/> a simulated body installs on its unit — the host
        /// capture seam for the peer's avatar extras. Aim is read live from the locomotion the
        /// admitted commands drive (the same <c>CurrentSnapshot.AimAngle</c> the local rig's
        /// animation consumes); the weapon is the last admitted command's hash, written by
        /// <see cref="Drive"/>; the animation is whatever the body's own
        /// <c>CharacterAnimationDriver</c> is currently playing.
        /// </summary>
        private sealed class PeerAvatarViewSource : IUnitAvatarViewSource
        {
            private readonly PlayerLocomotionController _locomotion;
            private readonly CharacterSpriteAssembler _assembler;
            private ulong _weaponIdHash;

            public PeerAvatarViewSource(PlayerLocomotionController locomotion,
                CharacterSpriteAssembler assembler)
            {
                _locomotion = locomotion;
                _assembler = assembler;
            }

            /// <summary>The held weapon per the newest admitted command. Written by Drive rather than
            /// read from a component because a simulated body has no loadout — the peer's inventory
            /// is the only authority and the command is the only channel to it.</summary>
            public void SetWeaponIdHash(ulong weaponIdHash) => _weaponIdHash = weaponIdHash;

            public float AimAngle =>
                _locomotion != null ? _locomotion.CurrentSnapshot.AimAngle : 0f;

            public ulong WeaponIdHash => _weaponIdHash;

            public byte AnimStateId =>
                _assembler != null
                    ? RemoteAnimStateIds.ToId(_assembler.CurrentState)
                    : RemoteAnimStateIds.Unknown;

            public ushort AnimFrame =>
                _assembler != null
                    ? (ushort)Mathf.Clamp(_assembler.CurrentFrame, 0, ushort.MaxValue)
                    : (ushort)0;
        }

        /// <summary>
        /// The ability sheet a simulated host body runs on: the local player's
        /// <see cref="ILocomotionAbilities"/> with every mana gate forced open. The host body is
        /// driven by the peer's admitted commands, and the peer's own simulation has ALREADY paid the
        /// mana for the levitation upkeep or the teleport — re-charging mana here would need the
        /// client's mana pool on the host, and a body that runs dry at a different moment than the
        /// client is exactly the "host thinks I'm on the ground while I'm on the ceiling" desync.
        /// <para>Implemented as a component (not just handing the source to the locomotion) because
        /// <c>PlayerLocomotionController.Awake</c> resolves its abilities with
        /// <c>GetComponent&lt;ILocomotionAbilities&gt;()</c> — the body must WEAR the sheet for the
        /// lookup to find it.</para>
        /// </summary>
        private sealed class RemoteBodyAbilities : MonoBehaviour, ILocomotionAbilities
        {
            public ILocomotionAbilities Source;

            public bool InfiniteMana => true;
            public bool CanDoubleJump => Source != null && Source.CanDoubleJump;
            public bool CanLevitate => Source != null && Source.CanLevitate;
            public bool CanAirDash => Source != null && Source.CanAirDash;
            public bool CanWallJump => Source != null && Source.CanWallJump;
            public int MaxJumpCount => Source != null ? Source.MaxJumpCount : 1;
            public float JumpForceMultiplier => Source != null ? Source.JumpForceMultiplier : 1f;
            public float AirJumpForceRatio => Source != null ? Source.AirJumpForceRatio : 0.8f;
            public float MoveSpeedMultiplier => Source != null ? Source.MoveSpeedMultiplier : 1f;
            public float LevitationMaxHeight => Source != null ? Source.LevitationMaxHeight : 0f;
            public float LevitationAcceleration => Source != null ? Source.LevitationAcceleration : 0f;
            public float LevitationManaCostPerTick => Source != null ? Source.LevitationManaCostPerTick : 0f;
            public float LevitationManaCostUpward => Source != null ? Source.LevitationManaCostUpward : 0f;
            public bool CanTeleport => Source != null && Source.CanTeleport;
            public float TeleportChargeTimeSeconds =>
                Source != null ? Source.TeleportChargeTimeSeconds : 0f;
            public float TeleportManaCost => Source != null ? Source.TeleportManaCost : 0f;
            public float TeleportCooldownSeconds =>
                Source != null ? Source.TeleportCooldownSeconds : 0f;
        }
    }
}
